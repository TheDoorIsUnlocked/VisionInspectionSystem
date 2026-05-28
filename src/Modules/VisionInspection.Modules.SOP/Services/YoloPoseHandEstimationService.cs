using SkiaSharp;
using VisionInspection.Modules.SOP.Models;
using YoloDotNet;
using YoloDotNet.Enums;
using YoloDotNet.ExecutionProvider.Cpu;
using YoloDotNet.ExecutionProvider.Cuda;
using YoloDotNet.Models;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace VisionInspection.Modules.SOP.Services;

/// <summary>
/// 基于YOLOv8-pose + MediaPipe Landmark的手部姿态估计服务
/// 使用YOLOv8-pose检测手腕位置，裁剪手部区域后使用MediaPipe Landmark检测手指
/// </summary>
public class YoloPoseHandEstimationService : IHandPoseEstimationService, IDisposable
{
    private Yolo? _yolo;
    private InferenceSession? _landmarkSession;
    private HandPoseEstimationConfig _config = new();
    private readonly object _lockObject = new();
    
    // 历史记录用于平滑
    private HandPose? _lastLeftHand;
    private HandPose? _lastRightHand;
    private readonly float _smoothingFactor = 0.85f;
    private readonly Queue<HandPose> _handHistory = new(5);
    private const int HistorySize = 5;

    public bool IsInitialized => _yolo != null && _landmarkSession != null;

    public async Task InitializeAsync(HandPoseEstimationConfig config)
    {
        _config = config;
        
        // 1. 加载YOLOv8-pose模型
        var poseModelPath = await FindPoseModelAsync(config);
        
        // 2. 加载MediaPipe手部关键点模型
        var landmarkModelPath = await FindLandmarkModelAsync(config);

        await Task.Run(() =>
        {
            lock (_lockObject)
            {
                // 初始化YOLOv8-pose
                var options = new YoloOptions
                {
                    ExecutionProvider = config.UseGpu 
                        ? new CudaExecutionProvider(poseModelPath, 0)
                        : new CpuExecutionProvider(poseModelPath),
                    ImageResize = ImageResize.Proportional,
                    SamplingOptions = new(SKFilterMode.Nearest, SKMipmapMode.None)
                };

                _yolo = new Yolo(options);
                Console.WriteLine($"[YoloPoseHand] YOLOv8-pose模型加载成功: {poseModelPath}");

                // 初始化MediaPipe Landmark模型
                var sessionOptions = new SessionOptions();
                if (config.UseGpu)
                {
                    sessionOptions.AppendExecutionProvider_CUDA(0);
                }
                _landmarkSession = new InferenceSession(landmarkModelPath, sessionOptions);
                Console.WriteLine($"[YoloPoseHand] MediaPipe Landmark模型加载成功: {landmarkModelPath}");
            }
        });
    }

    private async Task<string> FindPoseModelAsync(HandPoseEstimationConfig config)
    {
        var modelPath = config.PalmModelPath;
        
        if (string.IsNullOrEmpty(modelPath) || 
            !File.Exists(modelPath) || 
            modelPath.Contains("palm_detection") || 
            modelPath.Contains("hand_landmark"))
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var possiblePaths = new[]
            {
                Path.Combine(baseDir, "yolo_models", "yolov8s-pose.onnx"),
                Path.Combine(baseDir, "yolo_models", "yolov11s-pose.onnx"),
                Path.Combine(baseDir, "Models", "yolov8s-pose.onnx"),
                Path.Combine(baseDir, "Models", "yolov11s-pose.onnx"),
                Path.Combine(baseDir, "..", "yolo_models", "yolov8s-pose.onnx"),
                Path.Combine(baseDir, "..", "yolo_models", "yolov11s-pose.onnx"),
                Path.Combine(baseDir, "..", "..", "yolo_models", "yolov8s-pose.onnx"),
                Path.Combine(baseDir, "..", "..", "yolo_models", "yolov11s-pose.onnx"),
            };
            
            modelPath = possiblePaths.FirstOrDefault(File.Exists) ?? "";
            
            if (string.IsNullOrEmpty(modelPath))
            {
                throw new FileNotFoundException("未找到YOLOv8-pose模型文件");
            }
        }
        
        return modelPath;
    }

    private async Task<string> FindLandmarkModelAsync(HandPoseEstimationConfig config)
    {
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var possiblePaths = new[]
        {
            // 相对于执行目录的路径
            Path.Combine(baseDir, "models", "hand_landmark_sparse_Nx3x224x224.onnx"),
            Path.Combine(baseDir, "Models", "hand_landmark_sparse_Nx3x224x224.onnx"),
            Path.Combine(baseDir, "..", "models", "hand_landmark_sparse_Nx3x224x224.onnx"),
            Path.Combine(baseDir, "..", "..", "models", "hand_landmark_sparse_Nx3x224x224.onnx"),
            Path.Combine(baseDir, "..", "..", "..", "models", "hand_landmark_sparse_Nx3x224x224.onnx"),
            // 相对于项目源代码的路径（开发环境）
            Path.Combine(baseDir, "..", "..", "..", "VisionInspectionSystem", "src", "UI", "VisionInspection.UI", "models", "hand_landmark_sparse_Nx3x224x224.onnx"),
            Path.Combine(baseDir, "..", "..", "VisionInspectionSystem", "src", "UI", "VisionInspection.UI", "models", "hand_landmark_sparse_Nx3x224x224.onnx"),
            // 绝对路径（从项目根目录）
            Path.Combine(baseDir, "VisionInspectionSystem", "src", "UI", "VisionInspection.UI", "models", "hand_landmark_sparse_Nx3x224x224.onnx"),
        };
        
        var modelPath = possiblePaths.FirstOrDefault(File.Exists);
        
        if (string.IsNullOrEmpty(modelPath))
        {
            // 记录查找过的路径以便调试
            Console.WriteLine("[YoloPoseHand] 查找模型文件失败，已尝试以下路径:");
            foreach (var path in possiblePaths)
            {
                Console.WriteLine($"  - {path} (存在: {File.Exists(path)})");
            }
            throw new FileNotFoundException("未找到MediaPipe Landmark模型文件 (hand_landmark_sparse_Nx3x224x224.onnx)");
        }

        Console.WriteLine($"[YoloPoseHand] 找到Landmark模型: {modelPath}");
        return modelPath;
    }

    private static int _callCount = 0;
    
    public Task<HandPoseEstimationResult> DetectHandsAsync(SKBitmap image)
    {
        _callCount++;
        Console.WriteLine($"[YoloPoseHand] DetectHandsAsync 被调用 #{_callCount}");
        
        if (_yolo == null || _landmarkSession == null)
        {
            Console.WriteLine($"[YoloPoseHand] 服务未初始化: _yolo={_yolo != null}, _landmarkSession={_landmarkSession != null}");
            return Task.FromResult(new HandPoseEstimationResult { Hands = new List<HandPose>() });
        }

        try
        {
            // 使用YOLOv8-pose检测人体姿态
            Console.WriteLine($"[YoloPoseHand] 开始运行 YOLOv8-pose...");
            var poseResults = _yolo.RunPoseEstimation(image, confidence: _config.ConfidenceThreshold);
            
            Console.WriteLine($"[YoloPoseHand] YOLOv8-pose检测到 {poseResults?.Count ?? 0} 个人体姿态");
            
            if (poseResults == null || poseResults.Count == 0)
            {
                return Task.FromResult(new HandPoseEstimationResult { Hands = new List<HandPose>() });
            }
            
            // 打印第一个人体的关键点信息用于调试
            if (poseResults.Count > 0)
            {
                var firstPose = poseResults[0];
                Console.WriteLine($"[YoloPoseHand] 第1个人体关键点数: {firstPose.KeyPoints.Length}");
                if (firstPose.KeyPoints.Length > 10)
                {
                    var leftWrist = firstPose.KeyPoints[9];
                    var rightWrist = firstPose.KeyPoints[10];
                    Console.WriteLine($"[YoloPoseHand] 左手腕: 置信度={leftWrist.Confidence:F3}, 位置=({leftWrist.X:F1},{leftWrist.Y:F1})");
                    Console.WriteLine($"[YoloPoseHand] 右手腕: 置信度={rightWrist.Confidence:F3}, 位置=({rightWrist.X:F1},{rightWrist.Y:F1})");
                }
            }

            var hands = new List<HandPose>();
            
            foreach (var pose in poseResults)
            {
                // 从人体姿态中提取手部位置并检测关键点
                var leftHand = DetectHandFromWrist(pose, image, HandType.Left);
                var rightHand = DetectHandFromWrist(pose, image, HandType.Right);
                
                if (leftHand != null)
                {
                    leftHand = ApplySmoothing(leftHand, ref _lastLeftHand);
                    // 检查是否与已检测到的手重叠（避免重复检测）
                    if (!IsOverlappingWithExistingHands(leftHand, hands))
                    {
                        hands.Add(leftHand);
                    }
                }
                
                if (rightHand != null)
                {
                    rightHand = ApplySmoothing(rightHand, ref _lastRightHand);
                    // 检查是否与已检测到的手重叠（避免重复检测）
                    if (!IsOverlappingWithExistingHands(rightHand, hands))
                    {
                        hands.Add(rightHand);
                    }
                }
            }

            Console.WriteLine($"[YoloPoseHand] 检测到 {hands.Count} 只手（来自 {poseResults.Count} 个人体姿态）");
            return Task.FromResult(new HandPoseEstimationResult { Hands = hands });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[YoloPoseHand] 手部检测失败: {ex.Message}");
            Console.WriteLine($"[YoloPoseHand] 异常堆栈: {ex.StackTrace}");
            return Task.FromResult(new HandPoseEstimationResult { Hands = new List<HandPose>() });
        }
    }

    private HandPose? DetectHandFromWrist(PoseEstimation pose, SKBitmap image, HandType handType)
    {
        try
        {
            Console.WriteLine($"[YoloPoseHand] DetectHandFromWrist 开始: {handType}, pose.KeyPoints.Length={pose.KeyPoints.Length}");
            
            // 获取手腕关键点索引 (COCO格式: 9=左腕, 10=右腕)
            int wristIndex = handType == HandType.Left ? 9 : 10;
            
            if (pose.KeyPoints.Length <= wristIndex)
            {
                Console.WriteLine($"[YoloPoseHand] {handType}手腕关键点索引超出范围: {wristIndex} >= {pose.KeyPoints.Length}");
                return null;
            }

            var wrist = pose.KeyPoints[wristIndex];
            
            // 检查手腕置信度 - 暂时降低阈值以便调试
            float actualThreshold = Math.Min(_config.ConfidenceThreshold, 0.3f);
            if (wrist.Confidence < actualThreshold)
            {
                Console.WriteLine($"[YoloPoseHand] {handType}手腕置信度太低: {wrist.Confidence:F3} < {actualThreshold}");
                return null;
            }
            
            Console.WriteLine($"[YoloPoseHand] {handType}手腕检测成功: 置信度={wrist.Confidence:F3}, 位置=({wrist.X:F1},{wrist.Y:F1})");

            // 计算手部区域（以手腕为中心）
            float handSize = 200; // 手部区域大小
            float centerX = wrist.X;
            float centerY = wrist.Y;
            float halfSize = handSize / 2;

            // 裁剪手部区域
            var cropRect = new SKRectI(
                Math.Max(0, (int)(centerX - halfSize)),
                Math.Max(0, (int)(centerY - halfSize)),
                Math.Min(image.Width, (int)(centerX + halfSize)),
                Math.Min(image.Height, (int)(centerY + halfSize))
            );

            if (cropRect.Width < 50 || cropRect.Height < 50)
            {
                return null;
            }

            // 裁剪并调整大小为模型输入尺寸 (224x224)
            using var handBitmap = new SKBitmap(224, 224);
            using var canvas = new SKCanvas(handBitmap);
            
            var srcRect = new SKRect(cropRect.Left, cropRect.Top, cropRect.Right, cropRect.Bottom);
            var dstRect = new SKRect(0, 0, 224, 224);
            
            canvas.DrawBitmap(image, srcRect, dstRect);

            // 运行MediaPipe Landmark模型
            Console.WriteLine($"[YoloPoseHand] 开始运行Landmark推理，裁剪区域: {cropRect}");
            var landmarks = RunLandmarkInference(handBitmap);
            
            if (landmarks == null)
            {
                Console.WriteLine("[YoloPoseHand] Landmark推理返回null");
                return null;
            }
            
            if (landmarks.Count == 0)
            {
                Console.WriteLine("[YoloPoseHand] Landmark推理返回空列表");
                return null;
            }
            
            Console.WriteLine($"[YoloPoseHand] Landmark推理成功，检测到 {landmarks.Count} 个关键点");

            // 将关键点坐标转换回原始图像坐标系
            var keypoints = new List<HandKeypoint>();
            
            for (int i = 0; i < landmarks.Count && i < 21; i++)
            {
                var (x, y, z) = landmarks[i];
                
                // MediaPipe输出的是相对于224x224输入图像的像素坐标 (0-224)
                // 先归一化为 0-1，再转换为裁剪区域内的像素坐标
                float normalizedX = x / 224.0f;
                float normalizedY = y / 224.0f;
                
                float localX = normalizedX * cropRect.Width;
                float localY = normalizedY * cropRect.Height;
                
                // 加上裁剪区域的偏移量，得到原始图像坐标
                float originalX = localX + cropRect.Left;
                float originalY = localY + cropRect.Top;
                
                keypoints.Add(new HandKeypoint(
                    (HandKeypointType)i,
                    originalX,
                    originalY,
                    z,
                    0.9f
                ));
            }

            return new HandPose
            {
                TrackId = handType == HandType.Left ? 0 : 1,
                HandType = handType,
                BoundingBox = new SKRect(cropRect.Left, cropRect.Top, cropRect.Right, cropRect.Bottom),
                Keypoints = keypoints,
                Timestamp = DateTime.Now
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[YoloPoseHand] 手部关键点检测失败: {ex.Message}");
            return null;
        }
    }

    private List<(float x, float y, float z)>? RunLandmarkInference(SKBitmap handBitmap)
    {
        try
        {
            // 准备输入张量 [1, 3, 224, 224]
            var inputTensor = new DenseTensor<float>(new[] { 1, 3, 224, 224 });
            
            // 将图像数据归一化到 [-1, 1] 或 [0, 1]
            for (int y = 0; y < 224; y++)
            {
                for (int x = 0; x < 224; x++)
                {
                    var pixel = handBitmap.GetPixel(x, y);
                    inputTensor[0, 0, y, x] = pixel.Red / 255.0f;
                    inputTensor[0, 1, y, x] = pixel.Green / 255.0f;
                    inputTensor[0, 2, y, x] = pixel.Blue / 255.0f;
                }
            }

            // 运行推理 - MediaPipe hand_landmark 模型输入名称为 "input"
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("input", inputTensor)
            };

            using var results = _landmarkSession!.Run(inputs);
            
            // 解析输出 [1, 63] 或 [1, 21, 3]
            var output = results.First().AsTensor<float>();
            var landmarks = new List<(float x, float y, float z)>();
            
            if (output.Dimensions.Length == 2 && output.Dimensions[1] == 63)
            {
                // 扁平化格式 [1, 63]
                for (int i = 0; i < 21; i++)
                {
                    float x = output[0, i * 3];
                    float y = output[0, i * 3 + 1];
                    float z = output[0, i * 3 + 2];
                    landmarks.Add((x, y, z));
                }
            }
            else if (output.Dimensions.Length == 3 && output.Dimensions[1] == 21 && output.Dimensions[2] == 3)
            {
                // 三维格式 [1, 21, 3]
                for (int i = 0; i < 21; i++)
                {
                    float x = output[0, i, 0];
                    float y = output[0, i, 1];
                    float z = output[0, i, 2];
                    landmarks.Add((x, y, z));
                }
            }
            
            return landmarks;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[YoloPoseHand] Landmark推理失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 检查新手是否与已检测到的手重叠
    /// </summary>
    private bool IsOverlappingWithExistingHands(HandPose newHand, List<HandPose> existingHands)
    {
        const float overlapThreshold = 0.5f; // IoU阈值
        
        foreach (var existingHand in existingHands)
        {
            float iou = CalculateIoU(newHand.BoundingBox, existingHand.BoundingBox);
            if (iou > overlapThreshold)
            {
                Console.WriteLine($"[YoloPoseHand] 检测到重叠的手 (IoU={iou:F2})，跳过");
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 计算两个矩形的IoU（交并比）
    /// </summary>
    private float CalculateIoU(SKRect rect1, SKRect rect2)
    {
        float x1 = Math.Max(rect1.Left, rect2.Left);
        float y1 = Math.Max(rect1.Top, rect2.Top);
        float x2 = Math.Min(rect1.Right, rect2.Right);
        float y2 = Math.Min(rect1.Bottom, rect2.Bottom);

        if (x2 <= x1 || y2 <= y1)
            return 0f;

        float intersection = (x2 - x1) * (y2 - y1);
        float area1 = rect1.Width * rect1.Height;
        float area2 = rect2.Width * rect2.Height;
        float union = area1 + area2 - intersection;

        return union > 0 ? intersection / union : 0f;
    }

    private HandPose? ApplySmoothing(HandPose currentHand, ref HandPose? lastHand)
    {
        // 添加到历史记录
        _handHistory.Enqueue(currentHand);
        if (_handHistory.Count > HistorySize)
        {
            _handHistory.Dequeue();
        }

        if (lastHand == null || lastHand.Keypoints.Count != currentHand.Keypoints.Count)
        {
            lastHand = currentHand;
            return currentHand;
        }

        // 平滑边界框
        var smoothedBoundingBox = new SKRect(
            SmoothValue(lastHand.BoundingBox.Left, currentHand.BoundingBox.Left),
            SmoothValue(lastHand.BoundingBox.Top, currentHand.BoundingBox.Top),
            SmoothValue(lastHand.BoundingBox.Right, currentHand.BoundingBox.Right),
            SmoothValue(lastHand.BoundingBox.Bottom, currentHand.BoundingBox.Bottom)
        );

        // 平滑关键点
        var smoothedKeypoints = new List<HandKeypoint>();
        for (int i = 0; i < currentHand.Keypoints.Count; i++)
        {
            var lastKp = lastHand.Keypoints[i];
            var currKp = currentHand.Keypoints[i];
            
            var smoothedKp = new HandKeypoint(
                currKp.Type,
                SmoothValue(lastKp.X, currKp.X),
                SmoothValue(lastKp.Y, currKp.Y),
                SmoothValue(lastKp.Z, currKp.Z),
                currKp.Confidence
            );
            smoothedKeypoints.Add(smoothedKp);
        }

        var smoothedHand = new HandPose
        {
            TrackId = currentHand.TrackId,
            HandType = currentHand.HandType,
            BoundingBox = smoothedBoundingBox,
            Keypoints = smoothedKeypoints,
            Timestamp = currentHand.Timestamp
        };

        lastHand = smoothedHand;
        return smoothedHand;
    }

    private float SmoothValue(float last, float current)
    {
        return _smoothingFactor * last + (1 - _smoothingFactor) * current;
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
            _landmarkSession?.Dispose();
            _landmarkSession = null;
        }
    }
}
