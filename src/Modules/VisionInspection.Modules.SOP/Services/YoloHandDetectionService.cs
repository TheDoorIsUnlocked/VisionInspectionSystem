using SkiaSharp;
using VisionInspection.Modules.SOP.Models;
using YoloDotNet;
using YoloDotNet.Enums;
using YoloDotNet.ExecutionProvider.Cpu;
using YoloDotNet.ExecutionProvider.Cuda;
using YoloDotNet.Models;

namespace VisionInspection.Modules.SOP.Services;

/// <summary>
/// 基于YOLOv8-hand的专用手部检测服务
/// 直接检测手部，无需先检测人体
/// </summary>
public class YoloHandDetectionService : IHandPoseEstimationService, IDisposable
{
    private Yolo? _yolo;
    private HandPoseEstimationConfig _config = new();
    private readonly object _lockObject = new();
    
    // 跟踪历史用于平滑
    private readonly Dictionary<int, SmoothHandTracker> _handTrackers = new();
    private const int MaxLostFrames = 5;
    
    // 可配置参数（带默认值）
    public int SmoothWindowSize { get; set; } = 5; // 平滑窗口大小（帧数）- 减小以更快响应
    public float SmoothAlpha { get; set; } = 0.65f; // 指数平滑系数（0.65=65%当前+35%历史，响应快且不闪烁）
    public int InferenceInterval { get; set; } = 1; // 每N帧检测一次（1=每帧推理，保证跟踪实时性）
    public float DetectionConfidenceThreshold { get; set; } = 0.15f; // 检测置信度阈值
    public int DetectionHoldFrames { get; set; } = 8; // 检测保持帧数
    
    // 跳帧检测配置
    private int _frameCounter = 0; // 帧计数器
    private HandPoseEstimationResult? _lastResult = null; // 上次检测结果
    private int _holdFrameCounter = 0; // 保持帧计数器
    
    /// <summary>
    /// 带时序平滑的手部跟踪器
    /// </summary>
    private class SmoothHandTracker
    {
        public int TrackId { get; }
        public int LostFrames { get; set; }
        public DateTime LastUpdateTime { get; set; }
        
        // 历史姿态队列用于移动平均
        private readonly Queue<HandPose> _poseHistory = new();
        private readonly int _maxHistorySize;
        
        // 指数平滑后的姿态
        private HandPose? _smoothedPose;
        private readonly float _alpha;
        
        public SmoothHandTracker(int trackId, HandPose pose, int maxHistorySize = 5, float alpha = 0.7f)
        {
            TrackId = trackId;
            _maxHistorySize = maxHistorySize;
            _alpha = alpha;
            LastUpdateTime = DateTime.Now;
            LostFrames = 0;
            
            // 初始化历史记录
            _poseHistory.Enqueue(pose);
            _smoothedPose = pose;
        }
        
        public void Update(HandPose pose)
        {
            // 添加到历史记录
            _poseHistory.Enqueue(pose);
            while (_poseHistory.Count > _maxHistorySize)
            {
                _poseHistory.Dequeue();
            }
            
            // 计算平滑后的姿态
            _smoothedPose = CalculateSmoothedPose();
            
            LastUpdateTime = DateTime.Now;
            LostFrames = 0;
        }
        
        public void MarkLost()
        {
            LostFrames++;
        }
        
        public HandPose GetSmoothedPose()
        {
            return _smoothedPose ?? _poseHistory.LastOrDefault() ?? new HandPose 
            { 
                TrackId = TrackId, 
                Keypoints = new List<HandKeypoint>() 
            };
        }
        
        public HandPose PredictFromHistory()
        {
            // 丢失帧时返回最后平滑的结果
            return GetSmoothedPose();
        }
        
        /// <summary>
        /// 使用移动平均和指数平滑计算平滑后的姿态
        /// 采用双重平滑策略：1) 多帧移动平均 2) 指数平滑
        /// </summary>
        private HandPose CalculateSmoothedPose()
        {
            if (_poseHistory.Count == 0)
            {
                return _smoothedPose ?? new HandPose { TrackId = TrackId, Keypoints = new List<HandKeypoint>() };
            }
            
            if (_poseHistory.Count == 1)
            {
                return _poseHistory.First();
            }
            
            // 获取最新的原始姿态
            var latestPose = _poseHistory.Last();
            var previousSmoothed = _smoothedPose ?? latestPose;
            
            // 创建平滑后的姿态
            var smoothedPose = new HandPose
            {
                TrackId = TrackId,
                HandType = latestPose.HandType,
                Timestamp = DateTime.Now,
                Keypoints = new List<HandKeypoint>(),
                BoundingBox = SmoothBoundingBox(previousSmoothed.BoundingBox, latestPose.BoundingBox)
            };
            
            // 对关键点进行平滑
            var keypointDict = new Dictionary<HandKeypointType, List<HandKeypoint>>();
            
            // 收集所有历史帧中的关键点
            foreach (var pose in _poseHistory)
            {
                foreach (var kp in pose.Keypoints)
                {
                    if (!keypointDict.ContainsKey(kp.Type))
                    {
                        keypointDict[kp.Type] = new List<HandKeypoint>();
                    }
                    keypointDict[kp.Type].Add(kp);
                }
            }
            
            // 计算每个关键点的平滑位置
            foreach (var kvp in keypointDict)
            {
                var keypoints = kvp.Value;
                if (keypoints.Count == 0) continue;
                
                // 获取最新帧的关键点
                var latestKp = keypoints.Last();
                
                // 查找前一帧平滑后的对应关键点
                var prevKp = previousSmoothed.Keypoints.FirstOrDefault(k => k.Type == kvp.Key);
                
                if (prevKp != null && keypoints.Count >= 3)
                {
                    // 双重平滑策略：
                    // 第1步：计算多帧移动平均（降低噪声）
                    float avgX = keypoints.Average(k => k.X);
                    float avgY = keypoints.Average(k => k.Y);
                    
                    // 第2步：指数平滑（保持响应性）
                    // smoothed = alpha * current + (1 - alpha) * previous
                    float smoothedX = _alpha * avgX + (1 - _alpha) * prevKp.X;
                    float smoothedY = _alpha * avgY + (1 - _alpha) * prevKp.Y;
                    
                    // 计算平均置信度（更稳定）
                    float avgConf = keypoints.Average(k => k.Confidence);
                    
                    smoothedPose.Keypoints.Add(new HandKeypoint(
                        kvp.Key,
                        smoothedX,
                        smoothedY,
                        latestKp.Z,
                        avgConf
                    ));
                }
                else if (prevKp != null)
                {
                    // 历史数据不足，使用简单指数平滑
                    float smoothedX = _alpha * latestKp.X + (1 - _alpha) * prevKp.X;
                    float smoothedY = _alpha * latestKp.Y + (1 - _alpha) * prevKp.Y;
                    
                    smoothedPose.Keypoints.Add(new HandKeypoint(
                        kvp.Key,
                        smoothedX,
                        smoothedY,
                        latestKp.Z,
                        latestKp.Confidence
                    ));
                }
                else
                {
                    // 没有历史数据，使用最新值
                    smoothedPose.Keypoints.Add(latestKp);
                }
            }
            
            return smoothedPose;
        }
        
        /// <summary>
        /// 平滑边界框
        /// </summary>
        private SKRect SmoothBoundingBox(SKRect prev, SKRect current)
        {
            float left = _alpha * current.Left + (1 - _alpha) * prev.Left;
            float top = _alpha * current.Top + (1 - _alpha) * prev.Top;
            float right = _alpha * current.Right + (1 - _alpha) * prev.Right;
            float bottom = _alpha * current.Bottom + (1 - _alpha) * prev.Bottom;
            
            return new SKRect(left, top, right, bottom);
        }
        
        public void Dispose() 
        { 
            _poseHistory.Clear();
        }
    }

    public bool IsInitialized => _yolo != null;

    public async Task InitializeAsync(HandPoseEstimationConfig config)
    {
        _config = config;
        
        // 查找YOLOv8-hand模型
        var modelPath = await FindHandModelAsync(config);

        await Task.Run(() =>
        {
            lock (_lockObject)
            {
                var options = new YoloOptions
                {
                    ExecutionProvider = config.UseGpu 
                        ? new CudaExecutionProvider(modelPath, 0)
                        : new CpuExecutionProvider(modelPath),
                    ImageResize = ImageResize.Proportional,
                    SamplingOptions = new(SKFilterMode.Nearest, SKMipmapMode.None)
                };

                _yolo = new Yolo(options);
                Console.WriteLine($"[YoloHand] YOLOv8-hand模型加载成功: {modelPath}");
            }
        });
    }

    private async Task<string> FindHandModelAsync(HandPoseEstimationConfig config)
    {
        var modelPath = config.PalmModelPath;
        
        if (string.IsNullOrEmpty(modelPath) || !File.Exists(modelPath))
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var possiblePaths = new[]
            {
                // YOLO11n-pose 手部关键点检测模型（推荐）
                Path.Combine(baseDir, "yolo_models", "yolo11n-pose-hands.onnx"),
                Path.Combine(baseDir, "Models", "yolo11n-pose-hands.onnx"),
                // YOLOv8-hand专用模型
                Path.Combine(baseDir, "yolo_models", "yolov8n-hand.onnx"),
                Path.Combine(baseDir, "yolo_models", "yolov8s-hand.onnx"),
                Path.Combine(baseDir, "yolo_models", "yolov8m-hand.onnx"),
                Path.Combine(baseDir, "Models", "yolov8n-hand.onnx"),
                Path.Combine(baseDir, "Models", "yolov8s-hand.onnx"),
                Path.Combine(baseDir, "Models", "yolov8m-hand.onnx"),
                // 备用路径
                Path.Combine(baseDir, "..", "yolo_models", "yolo11n-pose-hands.onnx"),
                Path.Combine(baseDir, "..", "..", "yolo_models", "yolo11n-pose-hands.onnx"),
                Path.Combine(baseDir, "..", "yolo_models", "yolov8n-hand.onnx"),
                Path.Combine(baseDir, "..", "..", "yolo_models", "yolov8n-hand.onnx"),
            };
            
            modelPath = possiblePaths.FirstOrDefault(File.Exists) ?? "";
            
            if (string.IsNullOrEmpty(modelPath))
            {
                throw new FileNotFoundException("未找到YOLO手部检测模型文件。请下载yolo11n-pose-hands.onnx或yolov8n-hand.onnx模型");
            }
        }
        
        return modelPath;
    }

    public async Task<HandPoseEstimationResult> DetectHandsAsync(SKBitmap image)
    {
        if (_yolo == null)
        {
            return new HandPoseEstimationResult { Hands = new List<HandPose>() };
        }

        // 跳帧检测：不是每帧都进行推理
        _frameCounter++;
        bool shouldRunInference = (_frameCounter % InferenceInterval) == 0;
        
        // 如果没有上次结果，强制进行检测
        if (_lastResult == null)
        {
            shouldRunInference = true;
        }

        if (!shouldRunInference && _lastResult != null)
        {
            // 使用上次结果，但更新跟踪器的预测
            return await Task.Run(() =>
            {
                lock (_lockObject)
                {
                    var predictedHands = new List<HandPose>();
                    
                    // 如果有跟踪器，使用跟踪器的预测
                    if (_handTrackers.Count > 0)
                    {
                        foreach (var tracker in _handTrackers.Values)
                        {
                            predictedHands.Add(tracker.PredictFromHistory());
                        }
                    }
                    else
                    {
                        // 如果没有跟踪器，直接使用上次结果（保持检测不闪烁）
                        predictedHands = _lastResult.Hands.ToList();
                    }
                    
                    return new HandPoseEstimationResult
                    {
                        Hands = predictedHands,
                        Timestamp = DateTime.Now
                    };
                }
            });
        }

        return await Task.Run(() =>
        {
            lock (_lockObject)
            {
                try
                {
                    var hands = new List<HandPose>();
                    int trackId = 0;

                    // 检查模型类型（通过模型文件名判断）
                    bool isPoseModel = _config.PalmModelPath?.Contains("pose") ?? false;
                    
                    // 使用降低后的置信度阈值进行检测（减少闪烁）
                    float actualThreshold = Math.Min(_config.ConfidenceThreshold, DetectionConfidenceThreshold);

                    if (isPoseModel)
                    {
                        // 使用YOLO-pose模型进行姿态估计
                        var poseResults = _yolo.RunPoseEstimation(image, actualThreshold);
                        
                        foreach (var pose in poseResults)
                        {
                            // 将姿态估计结果转换为手部姿态
                            var handPose = ConvertPoseToHandPose(pose, trackId++, image.Width, image.Height);
                            if (handPose != null)
                            {
                                hands.Add(handPose);
                            }
                        }
                    }
                    else
                    {
                        // 使用普通YOLO检测模型
                        var detections = _yolo.RunObjectDetection(image, actualThreshold);
                        
                        foreach (var detection in detections)
                        {
                            var labelName = detection.Label.Name.ToLower();
                            
                            // 处理手部类别（专用手部模型）
                            bool isHand = labelName.Contains("hand") || labelName.Contains("手");
                            
                            if (isHand)
                            {
                                var handPose = ConvertDetectionToHandPose(detection, trackId++, image.Width, image.Height);
                                if (handPose != null)
                                {
                                    hands.Add(handPose);
                                }
                            }
                        }
                    }

                    // 更新跟踪
                    hands = UpdateTracking(hands);
                    
                    // 检测保持机制：如果没有检测到但之前有结果，继续使用上次结果
                    if (hands.Count == 0 && _lastResult != null && _lastResult.Hands.Count > 0 && _holdFrameCounter < DetectionHoldFrames)
                    {
                        _holdFrameCounter++;
                        // 返回上次的结果（预测版本）
                        var heldHands = new List<HandPose>();
                        foreach (var tracker in _handTrackers.Values)
                        {
                            heldHands.Add(tracker.PredictFromHistory());
                        }
                        if (heldHands.Count > 0)
                        {
                            hands = heldHands;
                        }
                    }
                    else if (hands.Count > 0)
                    {
                        // 检测成功，重置保持计数器
                        _holdFrameCounter = 0;
                    }
                    
                    // 保存结果
                    _lastResult = new HandPoseEstimationResult
                    {
                        Hands = hands,
                        Timestamp = DateTime.Now
                    };

                    return _lastResult;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[YoloHand] 检测失败: {ex.Message}");
                    return new HandPoseEstimationResult { Hands = new List<HandPose>() };
                }
            }
        });
    }

    /// <summary>
    /// 将YOLO姿态估计结果转换为手部姿态
    /// </summary>
    private HandPose? ConvertPoseToHandPose(YoloDotNet.Models.PoseEstimation pose, int trackId, int imgWidth, int imgHeight)
    {
        var box = pose.BoundingBox;
        
        // 过滤太小或太大的检测框
        float boxWidth = (float)(box.Right - box.Left);
        float boxHeight = (float)(box.Bottom - box.Top);
        float boxArea = boxWidth * boxHeight;
        float imgArea = imgWidth * imgHeight;
        
        if (boxArea < imgArea * 0.001f || boxArea > imgArea * 0.5f)
        {
            return null;
        }

        var handPose = new HandPose
        {
            TrackId = trackId,
            HandType = HandType.Unknown,
            BoundingBox = new SKRect((float)box.Left, (float)box.Top, (float)box.Right, (float)box.Bottom),
            Timestamp = DateTime.Now,
            Keypoints = new List<HandKeypoint>()
        };

        // YOLO-pose 手部模型通常输出 21 个关键点
        // 关键点顺序：手腕 + 5个手指 x 4个关节
        if (pose.KeyPoints != null && pose.KeyPoints.Length > 0)
        {
            // 手部关键点映射（YOLO-pose 手部模型）
            var keypointTypes = new[]
            {
                HandKeypointType.Wrist,
                HandKeypointType.ThumbCMC, HandKeypointType.ThumbMCP, HandKeypointType.ThumbIP, HandKeypointType.ThumbTip,
                HandKeypointType.IndexFingerMCP, HandKeypointType.IndexFingerPIP, HandKeypointType.IndexFingerDIP, HandKeypointType.IndexFingerTip,
                HandKeypointType.MiddleFingerMCP, HandKeypointType.MiddleFingerPIP, HandKeypointType.MiddleFingerDIP, HandKeypointType.MiddleFingerTip,
                HandKeypointType.RingFingerMCP, HandKeypointType.RingFingerPIP, HandKeypointType.RingFingerDIP, HandKeypointType.RingFingerTip,
                HandKeypointType.PinkyMCP, HandKeypointType.PinkyPIP, HandKeypointType.PinkyDIP, HandKeypointType.PinkyTip,
            };

            int keypointCount = Math.Min(pose.KeyPoints.Length, keypointTypes.Length);
            for (int i = 0; i < keypointCount; i++)
            {
                var kp = pose.KeyPoints[i];
                handPose.Keypoints.Add(new HandKeypoint(
                    keypointTypes[i],
                    (float)kp.X,
                    (float)kp.Y,
                    0f, // Z坐标（2D检测为0）
                    (float)kp.Confidence
                ));
            }
        }
        else
        {
            // 如果没有关键点，添加边界框中心作为手腕点
            float centerX = (float)(box.Left + box.Right) / 2;
            float centerY = (float)(box.Top + box.Bottom) / 2;
            
            handPose.Keypoints.Add(new HandKeypoint(
                HandKeypointType.Wrist,
                centerX,
                centerY,
                0,
                (float)pose.Confidence
            ));
        }

        return handPose;
    }

    /// <summary>
    /// 将YOLO检测结果转换为手部姿态
    /// </summary>
    private HandPose? ConvertDetectionToHandPose(YoloDotNet.Models.ObjectDetection detection, int trackId, int imgWidth, int imgHeight)
    {
        var box = detection.BoundingBox;
        
        // 过滤太小或太大的检测框
        float boxWidth = box.Width;
        float boxHeight = box.Height;
        float boxArea = boxWidth * boxHeight;
        float imgArea = imgWidth * imgHeight;
        
        if (boxArea < imgArea * 0.001f || boxArea > imgArea * 0.5f)
        {
            return null;
        }

        var handPose = new HandPose
        {
            TrackId = trackId,
            HandType = HandType.Unknown, // YOLO-hand通常不区分左右手
            BoundingBox = new SKRect((float)box.Left, (float)box.Top, (float)box.Right, (float)box.Bottom),
            Timestamp = DateTime.Now,
            Keypoints = new List<HandKeypoint>()
        };

        // 添加边界框中心作为手腕点（简化处理）
        float centerX = (float)(box.Left + box.Right) / 2;
        float centerY = (float)(box.Top + box.Bottom) / 2;
        
        handPose.Keypoints.Add(new HandKeypoint(
            HandKeypointType.Wrist,
            centerX,
            centerY,
            0,
            (float)detection.Confidence
        ));

        return handPose;
    }

    /// <summary>
    /// 更新手部跟踪 - 使用位置匹配而非trackId匹配
    /// </summary>
    private List<HandPose> UpdateTracking(List<HandPose> currentHands)
    {
        var smoothedHands = new List<HandPose>();
        var matchedTrackers = new HashSet<int>();
        var matchedHands = new HashSet<int>();

        // 计算所有当前手与跟踪器的匹配代价（中心点距离）
        var matches = new List<(int handIndex, int trackId, float distance)>();
        
        for (int i = 0; i < currentHands.Count; i++)
        {
            var hand = currentHands[i];
            var handCenter = GetHandCenter(hand);
            
            foreach (var kvp in _handTrackers)
            {
                var tracker = kvp.Value;
                var trackerCenter = GetHandCenter(tracker.GetSmoothedPose());
                
                float distance = CalculateDistance(handCenter, trackerCenter);
                matches.Add((i, kvp.Key, distance));
            }
        }

        // 按距离排序，优先匹配距离近的
        matches.Sort((a, b) => a.distance.CompareTo(b.distance));
        
        // 执行匹配（匈牙利算法的简化版本 - 贪心匹配）
        foreach (var match in matches)
        {
            // 如果距离太远（超过100像素），认为是不同的手
            if (match.distance > 100) continue;
            
            // 如果手或跟踪器已经被匹配，跳过
            if (matchedHands.Contains(match.handIndex) || matchedTrackers.Contains(match.trackId))
                continue;
            
            // 执行匹配
            var hand = currentHands[match.handIndex];
            var tracker = _handTrackers[match.trackId];
            
            tracker.Update(hand);
            matchedHands.Add(match.handIndex);
            matchedTrackers.Add(match.trackId);
            
            // 更新手的trackId为跟踪器的trackId，保持一致性
            hand.TrackId = match.trackId;
            smoothedHands.Add(tracker.GetSmoothedPose());
        }

        // 处理未匹配的手（新出现的手）
        for (int i = 0; i < currentHands.Count; i++)
        {
            if (!matchedHands.Contains(i))
            {
                var hand = currentHands[i];
                // 分配新的trackId
                int newTrackId = GetNextTrackId();
                hand.TrackId = newTrackId;
                
                // 创建新跟踪器
                _handTrackers[newTrackId] = new SmoothHandTracker(newTrackId, hand, SmoothWindowSize, SmoothAlpha);
                smoothedHands.Add(hand);
            }
        }

        // 标记未匹配的跟踪器为丢失
        var lostTrackers = new List<int>();
        foreach (var kvp in _handTrackers)
        {
            if (!matchedTrackers.Contains(kvp.Key))
            {
                kvp.Value.MarkLost();
                if (kvp.Value.LostFrames <= MaxLostFrames)
                {
                    smoothedHands.Add(kvp.Value.PredictFromHistory());
                }
                else
                {
                    lostTrackers.Add(kvp.Key);
                }
            }
        }

        // 移除丢失太久的跟踪器
        foreach (var id in lostTrackers)
        {
            _handTrackers.Remove(id);
        }

        return smoothedHands;
    }

    /// <summary>
    /// 获取手部中心点（使用手腕位置或边界框中心）
    /// </summary>
    private SKPoint GetHandCenter(HandPose hand)
    {
        // 优先使用手腕位置
        var wrist = hand.Keypoints.FirstOrDefault(k => k.Type == HandKeypointType.Wrist);
        if (wrist != null)
        {
            return new SKPoint(wrist.X, wrist.Y);
        }
        
        // 如果没有手腕点，使用边界框中心
        var box = hand.BoundingBox;
        return new SKPoint((box.Left + box.Right) / 2, (box.Top + box.Bottom) / 2);
    }

    /// <summary>
    /// 计算两点之间的距离
    /// </summary>
    private float CalculateDistance(SKPoint p1, SKPoint p2)
    {
        float dx = p1.X - p2.X;
        float dy = p1.Y - p2.Y;
        return (float)Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>
    /// 获取下一个可用的trackId
    /// </summary>
    private int GetNextTrackId()
    {
        int maxId = 0;
        foreach (var key in _handTrackers.Keys)
        {
            if (key > maxId) maxId = key;
        }
        return maxId + 1;
    }

    public Task ShutdownAsync()
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        lock (_lockObject)
        {
            _yolo?.Dispose();
            _yolo = null;
            _handTrackers.Clear();
        }
    }
}
