using SkiaSharp;
using VisionInspection.Modules.SOP.Models;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace VisionInspection.Modules.SOP.Services;

/// <summary>
/// 基于ONNX的MediaPipe手部检测器
/// 使用MediaPipe Hands的ONNX模型进行两阶段检测：
/// 1. 手掌检测 (palm_detection)
/// 2. 关键点检测 (hand_landmark)
/// </summary>
public class MediaPipeHandDetector : IDisposable
{
    private InferenceSession? _palmSession;
    private InferenceSession? _landmarkSession;
    private readonly string _palmModelPath;
    private readonly string _landmarkModelPath;
    private readonly float _confidenceThreshold;
    private readonly int _maxNumHands;
    private readonly object _lockObject = new();

    // 模型输入尺寸
    private const int PalmInputSize = 192;
    private const int LandmarkInputSize = 224;

    // 平滑处理参数
    private HandPose? _lastHandPose;
    private readonly float _smoothingFactor = 0.7f; // 降低平滑因子，减少延迟，0.7更响应迅速
    private readonly int _maxLostFrames = 5; // 减少到5帧后骨架消失（约0.15秒@30fps），减少残留
    private int _lostFrameCount = 0;
    
    // 历史记录用于多帧平滑
    private readonly Queue<HandPose> _handHistory = new Queue<HandPose>(3);
    private const int HistorySize = 3; // 减少历史记录，更响应当前帧
    
    // 跳帧检测参数 - 每帧都检测以提高响应速度，减少闪烁
    private int _frameCount = 0;
    private readonly int _detectInterval = 1; // 每帧都检测，减少跳帧导致的闪烁
    private HandPose? _lastDetectionResult; // 缓存上次检测结果

    // 文件日志记录器
    private static readonly object _mediaPipeLogLock = new();
    private const string _mediaPipeDebugLogPath = "sop_mediapipe_debug.log";

    private void DebugLog(string message)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
        var logLine = $"[{timestamp}] [MediaPipe] {message}";

        Console.WriteLine(logLine);
        System.Diagnostics.Debug.WriteLine(logLine);

        lock (_mediaPipeLogLock)
        {
            try
            {
                File.AppendAllText(_mediaPipeDebugLogPath, logLine + Environment.NewLine);
            }
            catch { }
        }
    }

    public bool IsInitialized => _palmSession != null && _landmarkSession != null;

    public MediaPipeHandDetector(string palmModelPath, string landmarkModelPath, 
        float confidenceThreshold = 0.5f, int maxNumHands = 2)
    {
        _palmModelPath = palmModelPath;
        _landmarkModelPath = landmarkModelPath;
        _confidenceThreshold = confidenceThreshold;
        _maxNumHands = maxNumHands;
    }

    /// <summary>
    /// 初始化检测器
    /// </summary>
    public void Initialize()
    {
        lock (_lockObject)
        {
            if (_palmSession != null && _landmarkSession != null) return;

            // 检查模型文件是否存在
            bool palmExists = File.Exists(_palmModelPath);
            bool landmarkExists = File.Exists(_landmarkModelPath);

            if (!palmExists || !landmarkExists)
            {
                Console.WriteLine("[MediaPipeHand] 未找到完整的ONNX模型，使用模拟模式");
                if (!palmExists) Console.WriteLine($"  - 缺少手掌检测模型: {_palmModelPath}");
                if (!landmarkExists) Console.WriteLine($"  - 缺少关键点检测模型: {_landmarkModelPath}");
                return;
            }

            try
            {
                var options = new SessionOptions
                {
                    InterOpNumThreads = 4,
                    IntraOpNumThreads = 4,
                    GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
                };

                _palmSession = new InferenceSession(_palmModelPath, options);
                _landmarkSession = new InferenceSession(_landmarkModelPath, options);
                
                Console.WriteLine($"[MediaPipeHand] 手掌检测模型加载成功");
                Console.WriteLine($"[MediaPipeHand] 关键点检测模型加载成功");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MediaPipeHand] 模型加载失败: {ex.Message}");
                throw;
            }
        }
    }

    /// <summary>
    /// 检测手部
    /// </summary>
    public List<HandPose> DetectHands(SKBitmap image)
    {
        var hands = new List<HandPose>();
        
        _frameCount++;

        DebugLog("DetectHands 开始");
        DebugLog($"_palmSession 是否为null: {_palmSession == null}");
        DebugLog($"_landmarkSession 是否为null: {_landmarkSession == null}");

        if (_palmSession == null || _landmarkSession == null)
        {
            // 模拟模式：返回模拟数据用于测试
            DebugLog("使用模拟模式");
            return SimulateHandDetection(image);
        }

        // 每次调用都增加丢失计数（如果检测失败会重置）
        // 这样可以确保即使跳帧，长时间无手时骨架也会消失
        // 注意：只要有任何缓存（_lastDetectionResult 或 _lastHandPose），就应该增加计数
        if (_lastDetectionResult != null || _lastHandPose != null)
        {
            _lostFrameCount++;
            DebugLog($"跳帧前检查: lostFrameCount={_lostFrameCount}/{_maxLostFrames}");
            
            // 如果超过最大丢失帧数，清空缓存
            if (_lostFrameCount > _maxLostFrames)
            {
                DebugLog($"超过最大丢失帧数，清空缓存，骨架将消失");
                _lastHandPose = null;
                _handHistory.Clear();
                _lastDetectionResult = null;
                _lostFrameCount = 0;
            }
        }
        
        // 跳帧检测策略：不是每帧都检测，降低检测频率以提高稳定性
        // 但只有在有缓存结果且未超过最大丢失帧数时才跳帧
        // 同时检查缓存结果的置信度
        float cacheConfidence = _lastDetectionResult?.Keypoints?.Count > 0 
            ? _lastDetectionResult.Keypoints.Average(kp => kp.Confidence) 
            : 0;
        bool hasValidCache = _lastDetectionResult != null 
            && _lostFrameCount < _maxLostFrames 
            && cacheConfidence >= 0.5f; // 缓存置信度也要足够
        bool shouldSkipDetection = (_frameCount % _detectInterval != 0) && hasValidCache;
        
        if (shouldSkipDetection)
        {
            DebugLog($"跳帧检测: frameCount={_frameCount}, 使用缓存结果，lostFrameCount={_lostFrameCount}, 缓存置信度={cacheConfidence:F3}");
            // 直接返回缓存的结果（已经平滑过）
            return new List<HandPose> { _lastDetectionResult };
        }
        else if (_frameCount % _detectInterval != 0 && _lastDetectionResult != null)
        {
            DebugLog($"跳帧检测跳过: 缓存无效(lostFrameCount={_lostFrameCount}, 置信度={cacheConfidence:F3})，执行检测");
        }

        try
        {
            // 第一阶段：手掌检测
            DebugLog("开始手掌检测...");
            var palmDetections = DetectPalms(image);
            DebugLog($"手掌检测完成，检测到 {palmDetections.Count} 个手掌");

            // 第二阶段：对每个检测到的手掌进行关键点检测
            foreach (var palm in palmDetections.Take(_maxNumHands))
            {
                DebugLog($"处理第 {hands.Count + 1} 个手掌的关键点检测...");
                var handPose = DetectHandLandmarks(image, palm);
                if (handPose != null)
                {
                    hands.Add(handPose);
                    DebugLog($"关键点检测成功，当前共 {hands.Count} 只手");
                }
                else
                {
                    DebugLog("关键点检测返回null");
                }
            }

            // 如果模型支持多手检测（输出多个结果），继续检测第二只手
            // 注意：当前模型只支持单手检测，如需双手检测，请使用 YOLOv8-pose 或下载多手检测模型
            if (hands.Count == 1 && _maxNumHands > 1)
            {
                DebugLog("尝试检测第二只手（当前模型可能不支持）...");
                // 这里可以添加第二只手的检测逻辑
                // 例如：屏蔽第一只手区域后再次检测
            }
            
            // 注意：如果检测失败，不在这里使用缓存
            // 缓存只在跳帧检测时使用，真正的检测失败应该由 ApplySmoothing 处理
            // 这样可以确保长时间无手时骨架会消失
            
            // 记录检测失败
            if (hands.Count == 0)
            {
                DebugLog($"当前帧检测失败，hands.Count=0，将由ApplySmoothing处理");
            }
        }
        catch (Exception ex)
        {
            DebugLog($"检测异常: {ex.Message}");
            DebugLog($"异常堆栈: {ex.StackTrace}");
        }

        // 应用平滑处理
        DebugLog($"调用ApplySmoothing，hands.Count={hands.Count}");
        var smoothedHands = ApplySmoothing(hands);
        
        // 关键：只有当本次检测成功（hands.Count > 0）时才更新缓存
        // 如果检测失败（hands.Count == 0），即使ApplySmoothing返回了缓存结果，也不应该更新缓存
        if (smoothedHands.Count > 0 && hands.Count > 0)
        {
            // 本次检测成功，进行验证和缓存更新
            var hand = smoothedHands[0];
            // 计算平均置信度
            float avgConfidence = hand.Keypoints.Count > 0 
                ? hand.Keypoints.Average(kp => kp.Confidence) 
                : 0;
            
            // 验证手部姿态（过滤面部误检测）
            bool isValidPose = ValidateHandPose(hand);
            
            // 只有当平均置信度 >= 0.7 且姿态合理时才认为是有效检测
            if (avgConfidence >= 0.7f && isValidPose)
            {
                _lastDetectionResult = hand;
                _lostFrameCount = 0; // 重置丢失计数
                DebugLog($"更新缓存结果，关键点数: {hand.Keypoints.Count}, 平均置信度: {avgConfidence:F3}, 姿态验证通过");
                DebugLog($"DetectHands 结束，返回 1 只手（验证通过）");
                return smoothedHands;
            }
            else if (!isValidPose)
            {
                DebugLog($"检测结果姿态不合理，可能是面部误检测，丢弃结果");
                // 姿态不合理，返回空列表（不绘制）
                DebugLog($"DetectHands 结束，返回 0 只手（姿态验证失败）");
                return new List<HandPose>();
            }
            else
            {
                DebugLog($"检测结果置信度太低({avgConfidence:F3})，丢弃结果");
                // 置信度太低，返回空列表（不绘制）
                DebugLog($"DetectHands 结束，返回 0 只手（置信度太低）");
                return new List<HandPose>();
            }
        }
        else if (smoothedHands.Count > 0 && hands.Count == 0)
        {
            // 检测失败，但ApplySmoothing返回了缓存结果
            // 此时不更新缓存，直接返回缓存结果（如果lostFrameCount允许）
            DebugLog($"检测失败，使用缓存结果，不更新缓存，lostFrameCount={_lostFrameCount}");
            DebugLog($"DetectHands 结束，返回 1 只手（缓存）");
            return smoothedHands;
        }
        
        DebugLog($"DetectHands 结束，返回 {smoothedHands.Count} 只手");
        return smoothedHands;
    }

    /// <summary>
    /// 应用平滑处理减少闪烁
    /// </summary>
    private List<HandPose> ApplySmoothing(List<HandPose> currentHands)
    {
        if (currentHands.Count == 0)
        {
            // 注意：_lostFrameCount 在 DetectHands 方法开始时已经增加
            // 这里只处理返回逻辑
            if (_lastDetectionResult != null && _lostFrameCount <= _maxLostFrames)
            {
                // 检测丢失但缓存还在，返回缓存结果（保持上次位置）
                DebugLog($"ApplySmoothing: 检测丢失，使用缓存结果，lostFrameCount={_lostFrameCount}");
                return new List<HandPose> { _lastDetectionResult };
            }
            else if (_lastHandPose != null && _lostFrameCount <= _maxLostFrames)
            {
                // 备用：使用平滑后的历史结果
                DebugLog($"ApplySmoothing: 使用历史平滑结果");
                return new List<HandPose> { _lastHandPose };
            }
            DebugLog($"ApplySmoothing: 无有效缓存，返回空列表");
            return new List<HandPose>();
        }

        // 检测成功，准备平滑处理
        // 注意：_lostFrameCount 在 DetectHands 中检测成功后重置，这里不再重置
        var currentHand = currentHands[0];

        // 添加到历史记录
        _handHistory.Enqueue(currentHand);
        if (_handHistory.Count > HistorySize)
        {
            _handHistory.Dequeue();
        }

        // 注意：_lostFrameCount 在 DetectHands 中检测成功后重置，这里不再重置
        
        // 如果没有历史记录，初始化并返回当前结果
        if (_lastHandPose == null)
        {
            _lastHandPose = currentHand;
            _handHistory.Enqueue(currentHand);
            DebugLog($"ApplySmoothing: 初始化历史记录");
            return currentHands;
        }
        
        // 如果关键点数量不匹配，重新初始化
        if (_lastHandPose.Keypoints.Count != currentHand.Keypoints.Count)
        {
            _lastHandPose = currentHand;
            _handHistory.Clear();
            _handHistory.Enqueue(currentHand);
            DebugLog($"ApplySmoothing: 关键点数量不匹配，重新初始化");
            return currentHands;
        }

        // 使用历史平均值进行平滑
        var smoothedHand = new HandPose
        {
            TrackId = currentHand.TrackId,
            HandType = currentHand.HandType,
            BoundingBox = SmoothRectWithHistory(currentHand.BoundingBox),
            Keypoints = new List<HandKeypoint>(),
            Timestamp = currentHand.Timestamp
        };

        for (int i = 0; i < currentHand.Keypoints.Count; i++)
        {
            var currKp = currentHand.Keypoints[i];
            
            // 计算历史平均值
            var (avgX, avgY, avgZ) = CalculateHistoryAverage(i);
            
            // 结合历史平均和当前值
            var smoothedKp = new HandKeypoint(
                currKp.Type,
                SmoothValue(avgX, currKp.X),
                SmoothValue(avgY, currKp.Y),
                SmoothValue(avgZ, currKp.Z),
                currKp.Confidence
            );
            
            smoothedHand.Keypoints.Add(smoothedKp);
        }

        _lastHandPose = smoothedHand;
        return new List<HandPose> { smoothedHand };
    }

    /// <summary>
    /// 验证手部姿态是否合理，用于过滤面部误检测
    /// 检查关键点的几何分布是否符合手部特征
    /// </summary>
    private bool ValidateHandPose(HandPose hand)
    {
        if (hand.Keypoints.Count < 21) return false;
        
        // 获取关键点
        var wrist = hand.Keypoints.FirstOrDefault(kp => kp.Type == HandKeypointType.Wrist);
        var thumbTip = hand.Keypoints.FirstOrDefault(kp => kp.Type == HandKeypointType.ThumbTip);
        var indexTip = hand.Keypoints.FirstOrDefault(kp => kp.Type == HandKeypointType.IndexFingerTip);
        var middleTip = hand.Keypoints.FirstOrDefault(kp => kp.Type == HandKeypointType.MiddleFingerTip);
        var ringTip = hand.Keypoints.FirstOrDefault(kp => kp.Type == HandKeypointType.RingFingerTip);
        var pinkyTip = hand.Keypoints.FirstOrDefault(kp => kp.Type == HandKeypointType.PinkyTip);
        
        if (wrist == null || thumbTip == null || indexTip == null || middleTip == null || ringTip == null || pinkyTip == null)
            return false;
        
        // 计算手腕到各指尖的距离
        float Dist(HandKeypoint a, HandKeypoint b) => 
            (float)Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
        
        var dThumb = Dist(wrist, thumbTip);
        var dIndex = Dist(wrist, indexTip);
        var dMiddle = Dist(wrist, middleTip);
        var dRing = Dist(wrist, ringTip);
        var dPinky = Dist(wrist, pinkyTip);
        
        // 检查距离比例：中指应该是最长的，小指最短
        // 正常手的比例：中指 > 食指 > 无名指 > 小指 > 拇指
        // 放宽阈值，允许更多变化
        if (dMiddle < dIndex * 0.6f || dMiddle < dRing * 0.6f)
        {
            DebugLog($"ValidateHandPose: 中指长度异常({dMiddle:F1} vs {dIndex:F1}/{dRing:F1})，可能是面部误检测");
            return false;
        }
        
        if (dPinky > dRing * 1.1f || dThumb > dIndex * 1.2f)
        {
            DebugLog($"ValidateHandPose: 手指长度比例异常(小指{dPinky:F1} vs 无名指{dRing:F1}, 拇指{dThumb:F1} vs 食指{dIndex:F1})，可能是面部误检测");
            return false;
        }
        
        // 检查指尖的分布：手部的指尖应该呈扇形分布
        // 计算指尖之间的角度
        float Angle(HandKeypoint a, HandKeypoint b, HandKeypoint c)
        {
            float abX = a.X - b.X, abY = a.Y - b.Y;
            float cbX = c.X - b.X, cbY = c.Y - b.Y;
            float dot = abX * cbX + abY * cbY;
            float magAB = (float)Math.Sqrt(abX * abX + abY * abY);
            float magCB = (float)Math.Sqrt(cbX * cbX + cbY * cbY);
            if (magAB * magCB < 0.001f) return 0;
            return (float)Math.Acos(Math.Max(-1, Math.Min(1, dot / (magAB * magCB)))) * 180 / (float)Math.PI;
        }
        
        var angleThumbIndex = Angle(thumbTip, wrist, indexTip);
        var angleIndexMiddle = Angle(indexTip, wrist, middleTip);
        var angleMiddleRing = Angle(middleTip, wrist, ringTip);
        var angleRingPinky = Angle(ringTip, wrist, pinkyTip);
        
        // 正常手部：拇指和食指之间的角度应该在 15-150 度之间（放宽阈值）
        if (angleThumbIndex < 15 || angleThumbIndex > 150)
        {
            DebugLog($"ValidateHandPose: 拇指-食指角度异常({angleThumbIndex:F1}°)，可能是面部误检测");
            return false;
        }
        
        // 总角度应该在 40-220 度之间（扇形分布，放宽阈值）
        var totalAngle = angleThumbIndex + angleIndexMiddle + angleMiddleRing + angleRingPinky;
        if (totalAngle < 40 || totalAngle > 220)
        {
            DebugLog($"ValidateHandPose: 指尖总角度异常({totalAngle:F1}°)，可能是面部误检测");
            return false;
        }
        
        DebugLog($"ValidateHandPose: 手部姿态验证通过，总角度={totalAngle:F1}°");
        return true;
    }

    /// <summary>
    /// 计算历史平均值
    /// </summary>
    private (float x, float y, float z) CalculateHistoryAverage(int keypointIndex)
    {
        if (_handHistory.Count == 0) return (0, 0, 0);
        
        float sumX = 0, sumY = 0, sumZ = 0;
        int count = 0;
        
        foreach (var hand in _handHistory)
        {
            if (keypointIndex < hand.Keypoints.Count)
            {
                var kp = hand.Keypoints[keypointIndex];
                sumX += kp.X;
                sumY += kp.Y;
                sumZ += kp.Z;
                count++;
            }
        }
        
        return count > 0 ? (sumX / count, sumY / count, sumZ / count) : (0, 0, 0);
    }

    /// <summary>
    /// 使用历史记录平滑矩形
    /// </summary>
    private SKRect SmoothRectWithHistory(SKRect current)
    {
        if (_handHistory.Count < 2) return current;
        
        float sumLeft = 0, sumTop = 0, sumRight = 0, sumBottom = 0;
        int count = 0;
        
        foreach (var hand in _handHistory)
        {
            sumLeft += hand.BoundingBox.Left;
            sumTop += hand.BoundingBox.Top;
            sumRight += hand.BoundingBox.Right;
            sumBottom += hand.BoundingBox.Bottom;
            count++;
        }
        
        var avgRect = new SKRect(
            sumLeft / count,
            sumTop / count,
            sumRight / count,
            sumBottom / count
        );
        
        return new SKRect(
            SmoothValue(avgRect.Left, current.Left),
            SmoothValue(avgRect.Top, current.Top),
            SmoothValue(avgRect.Right, current.Right),
            SmoothValue(avgRect.Bottom, current.Bottom)
        );
    }

    /// <summary>
    /// 平滑单个值
    /// </summary>
    private float SmoothValue(float last, float current)
    {
        return last * _smoothingFactor + current * (1 - _smoothingFactor);
    }

    /// <summary>
    /// 手掌检测
    /// </summary>
    private List<PalmDetection> DetectPalms(SKBitmap image)
    {
        var detections = new List<PalmDetection>();

        // 检查模型是否已加载
        if (_palmSession == null)
        {
            DebugLog("手掌检测模型未初始化，跳过");
            return detections;
        }

        // 预处理图像
        var inputTensor = PreprocessForPalmDetection(image);

        // 运行推理
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input", inputTensor)
        };

        IDisposableReadOnlyCollection<DisposableNamedOnnxValue>? results = null;
        try
        {
            results = _palmSession.Run(inputs);
        }
        catch (Exception ex)
        {
            DebugLog($"手掌检测推理失败: {ex.Message}");
            return detections;
        }

        try
        {
            // 验证结果
            if (results == null || results.Count < 2)
            {
                DebugLog($"手掌检测结果无效: count={results?.Count}");
                return detections;
            }

            // 解析结果（添加安全检查）
            var boxesResult = results.ElementAtOrDefault(0);
            var scoresResult = results.ElementAtOrDefault(1);

            if (boxesResult == null || scoresResult == null)
            {
                DebugLog("输出张量为null");
                return detections;
            }

            // 尝试转换为张量并记录详细信息
            var boxes = boxesResult.AsTensor<float>();
            var scores = scoresResult.AsTensor<float>();

            DebugLog($"boxes 是否为null: {boxes == null}, scores 是否为null: {scores == null}");

            if (boxes != null)
            {
                DebugLog($"boxes 维度: [{string.Join(",", boxes.Dimensions.ToArray())}]");
            }
            if (scores != null)
            {
                DebugLog($"scores 维度: [{string.Join(",", scores.Dimensions.ToArray())}]");
            }

            // 检查是否是特殊的单阶段模型输出 (batch_nums + score_cx_cy_w...)
            bool isSingleStageModel = boxesResult.Name == "batch_nums" || 
                                      scoresResult.Name.Contains("score_cx_cy_w");
            
            if (isSingleStageModel && scores != null)
            {
                DebugLog("检测到单阶段手部检测模型，使用适配的解析逻辑");
                return ParseSingleStagePalmOutput(scores, image.Width, image.Height);
            }

            if (boxes == null || scores == null)
            {
                DebugLog("张量转换失败 - 尝试获取更多信息...");
                try
                {
                    DebugLog($"boxesResult 类型: {boxesResult.GetType().FullName}");
                    DebugLog($"scoresResult 类型: {scoresResult.GetType().FullName}");
                    DebugLog($"boxesResult 名称: {boxesResult.Name}");
                    DebugLog($"scoresResult 名称: {scoresResult.Name}");
                }
                catch (Exception infoEx)
                {
                    DebugLog($"获取结果信息失败: {infoEx.Message}");
                }
                return detections;
            }

            // 处理检测结果 (标准两阶段模型)
            for (int i = 0; i < scores.Dimensions[1]; i++)
            {
                float score = scores[0, i];
                if (score > _confidenceThreshold)
                {
                    float x1 = boxes[0, i, 0] * image.Width;
                    float y1 = boxes[0, i, 1] * image.Height;
                    float x2 = boxes[0, i, 2] * image.Width;
                    float y2 = boxes[0, i, 3] * image.Height;

                    detections.Add(new PalmDetection
                    {
                        X = x1,
                        Y = y1,
                        Width = x2 - x1,
                        Height = y2 - y1,
                        Confidence = score
                    });
                }
            }
        }
        catch (Exception ex)
        {
            DebugLog($"解析手掌检测结果失败: {ex.Message}");
        }

        return detections.OrderByDescending(d => d.Confidence).ToList();
    }

    /// <summary>
    /// 解析单阶段手部检测模型的输出
    /// 输出格式: [score, cx, cy, w, wrist_x, wrist_y, middle_x, middle_y]
    /// </summary>
    private List<PalmDetection> ParseSingleStagePalmOutput(Tensor<float> scores, int imageWidth, int imageHeight)
    {
        var detections = new List<PalmDetection>();
        
        DebugLog($"解析单阶段模型输出，维度: [{string.Join(",", scores.Dimensions.ToArray())}]");
        
        // 假设输出是 [1, 8] 或 [8]，包含单个手的检测信息
        int numDetections = scores.Dimensions.Length > 1 ? scores.Dimensions[1] : scores.Dimensions[0];
        
        if (numDetections >= 8)
        {
            // 读取第一个检测 (假设 batch=1)
            int idx = scores.Dimensions.Length > 1 ? 0 : 0;
            
            float score = scores[idx, 0];
            float cx = scores[idx, 1];  // 中心点 x (归一化 0-1)
            float cy = scores[idx, 2];  // 中心点 y (归一化 0-1)
            float w = scores[idx, 3];   // 宽度 (归一化 0-1)
            
            DebugLog($"检测到: score={score:F3}, cx={cx:F3}, cy={cy:F3}, w={w:F3}");
            
            if (score > _confidenceThreshold)
            {
                // 转换为像素坐标
                float centerX = cx * imageWidth;
                float centerY = cy * imageHeight;
                float width = w * imageWidth * 1.3f; // 增大检测框，确保覆盖整个手掌
                float height = width * 1.3f; // 假设手的高宽比约为 1.3，并稍微增大
                
                // 计算左上角坐标，确保不超出图像边界
                float x = centerX - width / 2;
                float y = centerY - height / 2;
                
                detections.Add(new PalmDetection
                {
                    X = x,
                    Y = y,
                    Width = width,
                    Height = height,
                    Confidence = score
                });
                
                DebugLog($"添加手掌检测: X={detections[0].X:F1}, Y={detections[0].Y:F1}, W={detections[0].Width:F1}, H={detections[0].Height:F1}");
            }
            else
            {
                DebugLog($"置信度 {score:F3} 低于阈值 {_confidenceThreshold}，跳过");
            }
        }
        else
        {
            DebugLog($"输出维度 {numDetections} 不符合预期 (需要 >= 8)");
        }
        
        return detections;
    }

    /// <summary>
    /// 手部关键点检测
    /// </summary>
    private HandPose? DetectHandLandmarks(SKBitmap image, PalmDetection palm)
    {
        // 裁剪手掌区域
        var handImage = CropHandRegion(image, palm);
        if (handImage == null) return null;

        // 检查模型是否已加载
        if (_landmarkSession == null)
        {
            DebugLog("关键点检测模型未初始化，跳过");
            return null;
        }

        // 预处理
        var inputTensor = PreprocessForLandmarkDetection(handImage);

        // 运行推理
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input", inputTensor)
        };

        IDisposableReadOnlyCollection<DisposableNamedOnnxValue>? results = null;
        try
        {
            results = _landmarkSession.Run(inputs);
        }
        catch (Exception ex)
        {
            DebugLog($"关键点检测推理失败: {ex.Message}");
            return null;
        }

        try
        {
            // 验证结果
            if (results == null || results.Count < 1)
            {
                DebugLog($"关键点检测结果无效: count={results?.Count}");
                return null;
            }

            // 解析21个关键点（添加安全检查）
            var landmarksResult = results.ElementAtOrDefault(0);
            if (landmarksResult == null)
            {
                DebugLog("关键点输出张量为null");
                return null;
            }

            var landmarks = landmarksResult.AsTensor<float>();
            if (landmarks == null)
            {
                DebugLog("关键点张量转换失败");
                return null;
            }

            // 记录关键点张量的维度信息
            DebugLog($"关键点张量维度: [{string.Join(",", landmarks.Dimensions.ToArray())}]");

            var handPose = new HandPose
            {
                TrackId = 0,
                HandType = HandType.Unknown,
                BoundingBox = new SKRect(palm.X, palm.Y, palm.X + palm.Width, palm.Y + palm.Height),
                Keypoints = new List<HandKeypoint>(),
                Timestamp = DateTime.Now
            };

            // MediaPipe 21点手部关键点
            string[] jointNames = {
                "Wrist",
                "ThumbCMC", "ThumbMCP", "ThumbIP", "ThumbTip",
                "IndexFingerMCP", "IndexFingerPIP", "IndexFingerDIP", "IndexFingerTip",
                "MiddleFingerMCP", "MiddleFingerPIP", "MiddleFingerDIP", "MiddleFingerTip",
                "RingFingerMCP", "RingFingerPIP", "RingFingerDIP", "RingFingerTip",
                "PinkyMCP", "PinkyPIP", "PinkyDIP", "PinkyTip"
            };

            // 安全检查：确保张量维度足够
            int dim1 = landmarks.Dimensions.Length > 1 ? landmarks.Dimensions[1] : 0;
            DebugLog($"关键点维度1: {dim1}");

            if (dim1 == 0)
            {
                DebugLog("关键点维度为0，无法解析");
                return null;
            }

            // 检测输出格式并适配
            // 格式1: [1, 21, 3] - 标准3D
            // 格式2: [21, 3] - 无batch维度
            // 格式3: [1, 63] - 扁平化 (21*3)
            // 格式4: [63] - 完全扁平化
            
            bool isFlattened = landmarks.Dimensions.Length == 2 && dim1 == 63;
            bool isFullyFlattened = landmarks.Dimensions.Length == 1 && dim1 == 63;
            
            DebugLog($"格式判断: isFlattened={isFlattened}, isFullyFlattened={isFullyFlattened}");

            try
            {
                // 关键点模型输入尺寸是 224x224，输出坐标是相对于这个输入尺寸的（0-1范围）
                // 需要将坐标转换回原始图像坐标
                float inputSize = LandmarkInputSize; // 224
                
                if (isFlattened || isFullyFlattened)
                {
                    // 扁平化格式 [1, 63] 或 [63]
                    DebugLog("使用扁平化格式解析 [1,63]");
                    
                    // 记录原始值用于调试
                    float rawX0 = isFlattened ? landmarks[0, 0] : landmarks[0];
                    float rawY0 = isFlattened ? landmarks[0, 1] : landmarks[1];
                    DebugLog($"原始关键点0: rawX={rawX0:F3}, rawY={rawY0:F3}, palm=({palm.X:F1},{palm.Y:F1},{palm.Width:F1},{palm.Height:F1})");
                    
                    for (int i = 0; i < 21; i++)
                    {
                        float nx, ny, z;
                        
                        if (isFlattened)
                        {
                            nx = landmarks[0, i * 3 + 0];
                            ny = landmarks[0, i * 3 + 1];
                            z = landmarks[0, i * 3 + 2];
                        }
                        else
                        {
                            nx = landmarks[i * 3 + 0];
                            ny = landmarks[i * 3 + 1];
                            z = landmarks[i * 3 + 2];
                        }

                        // 转换回原始图像坐标
                        // 关键点模型输出是相对于 224x224 输入的坐标
                        // 需要判断输出是 0-1 范围还是像素范围
                        float x, y;
                        if (nx > 1.0f || ny > 1.0f)
                        {
                            // 输出是像素坐标（相对于 224x224）
                            x = nx * (palm.Width / inputSize) + palm.X;
                            y = ny * (palm.Height / inputSize) + palm.Y;
                        }
                        else
                        {
                            // 输出是 0-1 归一化坐标
                            x = nx * palm.Width + palm.X;
                            y = ny * palm.Height + palm.Y;
                        }

                        handPose.Keypoints.Add(new HandKeypoint(
                            (HandKeypointType)i,
                            x,
                            y,
                            z,
                            palm.Confidence
                        ));
                    }
                }
                else if (landmarks.Dimensions.Length >= 3)
                {
                    // 格式 [1, 21, 3] 或 [21, 3]
                    int batchIdx = landmarks.Dimensions.Length > 2 ? 0 : -1;
                    DebugLog($"使用3D格式解析，batchIdx={batchIdx}");
                    
                    for (int i = 0; i < Math.Min(21, dim1); i++)
                    {
                        float nx, ny, z;

                        if (batchIdx >= 0)
                        {
                            // 格式 [1, 21, 3]
                            nx = landmarks[batchIdx, i, 0];
                            ny = landmarks[batchIdx, i, 1];
                            z = landmarks[batchIdx, i, 2];
                        }
                        else
                        {
                            // 格式 [21, 3]
                            nx = landmarks[i, 0];
                            ny = landmarks[i, 1];
                            z = landmarks[i, 2];
                        }

                        // 转换回原始图像坐标
                        float x = nx * palm.Width + palm.X;
                        float y = ny * palm.Height + palm.Y;

                        handPose.Keypoints.Add(new HandKeypoint(
                            (HandKeypointType)i,
                            x,
                            y,
                            z,
                            palm.Confidence
                        ));
                    }
                }
                else
                {
                    DebugLog($"不支持的维度格式: [{string.Join(",", landmarks.Dimensions.ToArray())}]");
                    return null;
                }
            }
            catch (Exception idxEx)
            {
                DebugLog($"解析关键点失败: {idxEx.Message}");
                // 如果已经解析了一些关键点，仍然返回
                if (handPose.Keypoints.Count == 0)
                    return null;
            }

        return handPose;
        }
        catch (Exception ex)
        {
            DebugLog($"解析关键点检测结果失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 为手掌检测预处理图像
    /// </summary>
    private DenseTensor<float> PreprocessForPalmDetection(SKBitmap image)
    {
        // 调整图像大小
        var resized = image.Resize(new SKSizeI(PalmInputSize, PalmInputSize), SKFilterQuality.High);
        
        // 创建输入张量 [1, 3, 192, 192]
        var tensor = new DenseTensor<float>(new[] { 1, 3, PalmInputSize, PalmInputSize });

        // 归一化并填充张量
        for (int y = 0; y < PalmInputSize; y++)
        {
            for (int x = 0; x < PalmInputSize; x++)
            {
                var pixel = resized.GetPixel(x, y);
                tensor[0, 0, y, x] = pixel.Red / 255.0f;
                tensor[0, 1, y, x] = pixel.Green / 255.0f;
                tensor[0, 2, y, x] = pixel.Blue / 255.0f;
            }
        }

        return tensor;
    }

    /// <summary>
    /// 为关键点检测预处理图像
    /// </summary>
    private DenseTensor<float> PreprocessForLandmarkDetection(SKBitmap handImage)
    {
        // 调整图像大小
        var resized = handImage.Resize(new SKSizeI(LandmarkInputSize, LandmarkInputSize), SKFilterQuality.High);
        
        // 创建输入张量 [1, 3, 224, 224]
        var tensor = new DenseTensor<float>(new[] { 1, 3, LandmarkInputSize, LandmarkInputSize });

        // 归一化并填充张量
        for (int y = 0; y < LandmarkInputSize; y++)
        {
            for (int x = 0; x < LandmarkInputSize; x++)
            {
                var pixel = resized.GetPixel(x, y);
                tensor[0, 0, y, x] = pixel.Red / 255.0f;
                tensor[0, 1, y, x] = pixel.Green / 255.0f;
                tensor[0, 2, y, x] = pixel.Blue / 255.0f;
            }
        }

        return tensor;
    }

    /// <summary>
    /// 裁剪手掌区域
    /// </summary>
    private SKBitmap? CropHandRegion(SKBitmap image, PalmDetection palm)
    {
        // 添加一些边距
        int margin = 20;
        int x = Math.Max(0, (int)palm.X - margin);
        int y = Math.Max(0, (int)palm.Y - margin);
        int width = Math.Min((int)palm.Width + margin * 2, image.Width - x);
        int height = Math.Min((int)palm.Height + margin * 2, image.Height - y);

        if (width <= 0 || height <= 0) return null;

        // 创建裁剪后的图像
        var cropped = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(cropped))
        {
            canvas.DrawBitmap(image, 
                new SKRect(x, y, x + width, y + height),
                new SKRect(0, 0, width, height));
        }

        return cropped;
    }

    /// <summary>
    /// 模拟手部检测（用于测试）
    /// </summary>
    private List<HandPose> SimulateHandDetection(SKBitmap image)
    {
        var hands = new List<HandPose>();
        
        // 在图像中心生成模拟手部
        float centerX = image.Width / 2f;
        float centerY = image.Height / 2f;
        float scale = Math.Min(image.Width, image.Height) / 4f;

        var handPose = new HandPose
        {
            TrackId = 1,
            HandType = HandType.Right,
            Keypoints = GenerateSimulatedKeypoints(centerX, centerY, scale),
            BoundingBox = new SKRect(centerX - scale, centerY - scale, centerX + scale, centerY + scale),
            Timestamp = DateTime.Now
        };

        hands.Add(handPose);
        return hands;
    }

    /// <summary>
    /// 生成模拟关键点（用于测试）
    /// </summary>
    private List<HandKeypoint> GenerateSimulatedKeypoints(float centerX, float centerY, float scale)
    {
        var keypoints = new List<HandKeypoint>();
        
        // 模拟21个关键点（张开的手掌姿势）
        // 手腕
        keypoints.Add(new HandKeypoint(HandKeypointType.Wrist, centerX, centerY + scale * 0.8f, 0, 0.95f));
        
        // 拇指
        keypoints.Add(new HandKeypoint(HandKeypointType.ThumbCMC, centerX - scale * 0.3f, centerY + scale * 0.6f, 0, 0.95f));
        keypoints.Add(new HandKeypoint(HandKeypointType.ThumbMCP, centerX - scale * 0.5f, centerY + scale * 0.4f, 0, 0.95f));
        keypoints.Add(new HandKeypoint(HandKeypointType.ThumbIP, centerX - scale * 0.7f, centerY + scale * 0.2f, 0, 0.95f));
        keypoints.Add(new HandKeypoint(HandKeypointType.ThumbTip, centerX - scale * 0.9f, centerY, 0, 0.95f));
        
        // 食指
        keypoints.Add(new HandKeypoint(HandKeypointType.IndexFingerMCP, centerX - scale * 0.2f, centerY + scale * 0.3f, 0, 0.95f));
        keypoints.Add(new HandKeypoint(HandKeypointType.IndexFingerPIP, centerX - scale * 0.2f, centerY, 0, 0.95f));
        keypoints.Add(new HandKeypoint(HandKeypointType.IndexFingerDIP, centerX - scale * 0.2f, centerY - scale * 0.3f, 0, 0.95f));
        keypoints.Add(new HandKeypoint(HandKeypointType.IndexFingerTip, centerX - scale * 0.2f, centerY - scale * 0.6f, 0, 0.95f));
        
        // 中指
        keypoints.Add(new HandKeypoint(HandKeypointType.MiddleFingerMCP, centerX, centerY + scale * 0.3f, 0, 0.95f));
        keypoints.Add(new HandKeypoint(HandKeypointType.MiddleFingerPIP, centerX, centerY, 0, 0.95f));
        keypoints.Add(new HandKeypoint(HandKeypointType.MiddleFingerDIP, centerX, centerY - scale * 0.35f, 0, 0.95f));
        keypoints.Add(new HandKeypoint(HandKeypointType.MiddleFingerTip, centerX, centerY - scale * 0.7f, 0, 0.95f));
        
        // 无名指
        keypoints.Add(new HandKeypoint(HandKeypointType.RingFingerMCP, centerX + scale * 0.2f, centerY + scale * 0.3f, 0, 0.95f));
        keypoints.Add(new HandKeypoint(HandKeypointType.RingFingerPIP, centerX + scale * 0.2f, centerY, 0, 0.95f));
        keypoints.Add(new HandKeypoint(HandKeypointType.RingFingerDIP, centerX + scale * 0.2f, centerY - scale * 0.3f, 0, 0.95f));
        keypoints.Add(new HandKeypoint(HandKeypointType.RingFingerTip, centerX + scale * 0.2f, centerY - scale * 0.6f, 0, 0.95f));
        
        // 小指
        keypoints.Add(new HandKeypoint(HandKeypointType.PinkyMCP, centerX + scale * 0.4f, centerY + scale * 0.3f, 0, 0.95f));
        keypoints.Add(new HandKeypoint(HandKeypointType.PinkyPIP, centerX + scale * 0.5f, centerY + scale * 0.05f, 0, 0.95f));
        keypoints.Add(new HandKeypoint(HandKeypointType.PinkyDIP, centerX + scale * 0.6f, centerY - scale * 0.2f, 0, 0.95f));
        keypoints.Add(new HandKeypoint(HandKeypointType.PinkyTip, centerX + scale * 0.7f, centerY - scale * 0.45f, 0, 0.95f));

        return keypoints;
    }

    public void Dispose()
    {
        _palmSession?.Dispose();
        _landmarkSession?.Dispose();
        _palmSession = null;
        _landmarkSession = null;
    }

    /// <summary>
    /// 手掌检测结果
    /// </summary>
    private class PalmDetection
    {
        public float X { get; set; }
        public float Y { get; set; }
        public float Width { get; set; }
        public float Height { get; set; }
        public float Confidence { get; set; }
    }
}
