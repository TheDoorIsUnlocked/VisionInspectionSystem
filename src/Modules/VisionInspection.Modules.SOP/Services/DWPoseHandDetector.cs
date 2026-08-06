using SkiaSharp;
using VisionInspection.Modules.SOP.Models;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace VisionInspection.Modules.SOP.Services;

/// <summary>
/// 基于DWPose的手部检测器
/// 使用DWPose的ONNX模型进行全身姿态检测，提取手部关键点
/// 支持双手同时检测，检测更稳定
/// </summary>
public class DWPoseHandDetector : IDisposable
{
    private InferenceSession? _detSession;
    private InferenceSession? _poseSession;
    private readonly string _detModelPath;
    private readonly string _poseModelPath;
    private readonly float _confidenceThreshold;
    private readonly int _maxNumHands;
    private readonly object _lockObject = new();

    // 模型输入尺寸
    private const int DetInputSize = 640;
    private int _poseInputWidth = 384;  // 动态从模型读取
    private int _poseInputHeight = 384; // 动态从模型读取

    // 修复：使用卡尔曼滤波跟踪器替代简单平滑
    private readonly Dictionary<int, HandKalmanTracker> _handTrackers = new();
    private readonly int _maxLostFrames = 10;  // 减少丢失帧容忍度，避免残留

    // 跳帧检测参数 - 改为每帧都检测，提高响应速度
    private int _frameCount = 0;
    private readonly int _detectInterval = 1;  // 每帧都检测
    private List<HandPose> _lastDetectionResults = new();
    
    // 异步推理队列
    private readonly object _inferenceLock = new();

    // 文件日志记录器
    private static readonly object _dwposeLogLock = new();
    private const string _dwposeDebugLogPath = "sop_dwpose_debug.log";

    private void DebugLog(string message)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
        var logLine = $"[{timestamp}] [DWPose] {message}";

        Console.WriteLine(logLine);
        System.Diagnostics.Debug.WriteLine(logLine);

        lock (_dwposeLogLock)
        {
            try
            {
                File.AppendAllText(_dwposeDebugLogPath, logLine + Environment.NewLine);
            }
            catch { }
        }
    }

    public bool IsInitialized => _detSession != null && _poseSession != null;

    public DWPoseHandDetector(string detModelPath, string poseModelPath,
        float confidenceThreshold = 0.3f, int maxNumHands = 2)
    {
        _detModelPath = detModelPath;
        _poseModelPath = poseModelPath;
        _confidenceThreshold = confidenceThreshold;
        _maxNumHands = maxNumHands;
    }

    private static bool _isInitializing = false;
    private static readonly object _initLock = new object();

    /// <summary>
    /// 初始化检测器
    /// </summary>
    public void Initialize()
    {
        lock (_initLock)
        {
            if (_isInitializing)
            {
                DebugLog("另一个线程正在初始化，等待完成...");
                // 等待初始化完成
                while (_isInitializing)
                {
                    System.Threading.Thread.Sleep(100);
                }
            }

            if (_detSession != null && _poseSession != null)
            {
                DebugLog("模型已加载，跳过初始化");
                return;
            }

            _isInitializing = true;

            bool detExists = File.Exists(_detModelPath);
            bool poseExists = File.Exists(_poseModelPath);

            if (!detExists || !poseExists)
            {
                string errorMsg = "[DWPoseHand] 未找到完整的ONNX模型";
                if (!detExists) errorMsg += $"\n  - 缺少检测模型: {_detModelPath}";
                if (!poseExists) errorMsg += $"\n  - 缺少姿态模型: {_poseModelPath}";
                DebugLog(errorMsg);
                Console.WriteLine(errorMsg);
                throw new FileNotFoundException($"DWPose模型文件不存在: {(detExists ? _poseModelPath : _detModelPath)}");
            }

            try
            {
                DebugLog($"开始加载DWPose模型...");
                DebugLog($"  - 检测模型路径: {_detModelPath}");
                DebugLog($"  - 姿态模型路径: {_poseModelPath}");
                DebugLog($"  - 检测模型存在: {File.Exists(_detModelPath)}");
                DebugLog($"  - 姿态模型存在: {File.Exists(_poseModelPath)}");

                // 尝试使用CUDA加速
                var cudaAvailable = TryGetCudaDevice(out int cudaDeviceId);
                SessionOptions detOptions;
                SessionOptions poseOptions;

                if (cudaAvailable)
                {
                    DebugLog($"CUDA设备可用，使用GPU加速 (设备ID: {cudaDeviceId})");

                    // 检测模型使用CUDA
                    detOptions = new SessionOptions
                    {
                        GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
                    };
                    detOptions.AppendExecutionProvider_CUDA(cudaDeviceId);

                    // 姿态模型使用CUDA
                    poseOptions = new SessionOptions
                    {
                        GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
                    };
                    poseOptions.AppendExecutionProvider_CUDA(cudaDeviceId);
                }
                else
                {
                    DebugLog($"CUDA不可用，使用CPU推理");
                    detOptions = new SessionOptions
                    {
                        InterOpNumThreads = 2,
                        IntraOpNumThreads = 2,
                        GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                        ExecutionMode = ExecutionMode.ORT_SEQUENTIAL
                    };
                    poseOptions = detOptions;
                }

                DebugLog($"正在加载检测模型...");
                _detSession = new InferenceSession(_detModelPath, detOptions);
                DebugLog($"检测模型加载成功");

                DebugLog($"正在加载姿态模型...");
                _poseSession = new InferenceSession(_poseModelPath, poseOptions);
                DebugLog($"姿态模型加载成功");

                // 读取姿态模型的输入尺寸
                var inputMeta = _poseSession.InputMetadata;
                foreach (var input in inputMeta)
                {
                    var dims = input.Value.Dimensions;
                    DebugLog($"姿态模型输入 '{input.Key}': 维度=[{string.Join(", ", dims)}]");
                    if (dims.Length >= 4 && dims[2] > 0 && dims[3] > 0)
                    {
                        _poseInputHeight = dims[2];
                        _poseInputWidth = dims[3];
                        DebugLog($"检测到姿态模型输入尺寸: {_poseInputHeight}x{_poseInputWidth}");
                    }
                }

                DebugLog($"DWPose模型加载完成");
            }
            catch (Exception ex)
            {
                DebugLog($"模型加载失败: {ex.Message}");
                DebugLog($"堆栈跟踪: {ex.StackTrace}");
                Console.WriteLine($"[DWPoseHand] 模型加载失败: {ex.Message}");
                _detSession?.Dispose();
                _poseSession?.Dispose();
                _detSession = null;
                _poseSession = null;
                throw; // 重新抛出异常，让上层知道初始化失败
            }
            finally
            {
                _isInitializing = false;
            }
        }
    }

    /// <summary>
    /// 尝试获取可用的CUDA设备
    /// </summary>
    private bool TryGetCudaDevice(out int deviceId)
    {
        deviceId = 0;
        try
        {
            // 尝试创建CUDA会话选项，如果失败则说明CUDA不可用
            var testOptions = new SessionOptions();
            testOptions.AppendExecutionProvider_CUDA(0);
            testOptions.Dispose();
            DebugLog("CUDA设备检测成功");
            return true;
        }
        catch (Exception ex)
        {
            DebugLog($"CUDA设备检测失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 检测图像中的手部
    /// </summary>
    public List<HandPose> DetectHands(SKBitmap image)
    {
        if (_detSession == null || _poseSession == null)
        {
            Initialize();
            if (_detSession == null || _poseSession == null)
            {
                return new List<HandPose>();
            }
        }

        _frameCount++;

        // 修复：清理丢失过久的跟踪器
        var lostTrackers = _handTrackers.Where(t => t.Value.IsLost).Select(t => t.Key).ToList();
        foreach (var trackId in lostTrackers)
        {
            _handTrackers[trackId].Dispose();
            _handTrackers.Remove(trackId);
            DebugLog($"手部 {trackId} 超过最大丢失帧数，移除");
        }

        // 跳帧检测：如果不是检测帧，返回平滑后的历史结果
        if (_frameCount % _detectInterval != 0)
        {
            // 使用历史数据进行预测，保持检测连续性
            var predictedHands = PredictFromHistory();
            if (predictedHands.Count > 0)
            {
                DebugLog($"跳帧预测: frameCount={_frameCount}, 预测 {predictedHands.Count} 只手");
                return predictedHands;
            }
            // 如果没有历史数据，返回上次结果
            if (_lastDetectionResults.Count > 0)
            {
                DebugLog($"跳帧检测: frameCount={_frameCount}, 返回缓存结果 ({_lastDetectionResults.Count}只手)");
                return _lastDetectionResults;
            }
        }

        try
        {
            // 第一阶段：人体检测
            DebugLog("开始人体检测...");
            var personDetections = DetectPersons(image);
            DebugLog($"检测到 {personDetections.Count} 个人体");

            if (personDetections.Count == 0)
            {
                _lastDetectionResults.Clear();
                return new List<HandPose>();
            }

            // 第二阶段：姿态估计，提取手部关键点
            var allHandPoses = new List<HandPose>();
            int handId = 0;

            foreach (var personBox in personDetections.Take(_maxNumHands))
            {
                DebugLog($"处理人体检测框: ({personBox.Left:F0}, {personBox.Top:F0}, {personBox.Right:F0}, {personBox.Bottom:F0})");

                var handPoses = ExtractHandPoses(image, personBox, handId);
                if (handPoses.Count > 0)
                {
                    allHandPoses.AddRange(handPoses);
                    handId += handPoses.Count;
                }
            }

            // 更新跟踪ID
            allHandPoses = UpdateTracking(allHandPoses);

            // 平滑处理
            allHandPoses = SmoothHandPoses(allHandPoses);

            _lastDetectionResults = allHandPoses;
            DebugLog($"最终返回 {allHandPoses.Count} 只手的姿态");

            return allHandPoses;
        }
        catch (Exception ex)
        {
            DebugLog($"检测过程发生错误: {ex.Message}");
            DebugLog($"堆栈: {ex.StackTrace}");
            return _lastDetectionResults;
        }
    }

    /// <summary>
    /// 检测图像中的人体
    /// </summary>
    private List<SKRect> DetectPersons(SKBitmap image)
    {
        var detections = new List<SKRect>();

        // 预处理图像
        var (inputTensor, ratio) = PreprocessForDetection(image);

        // 运行检测
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("images", inputTensor)
        };

        using var results = _detSession!.Run(inputs);
        var output = results.First().AsTensor<float>();

        // 解析YOLOX输出
        detections = ParseYOLOXOutput(output, image.Width, image.Height, ratio);

        return detections;
    }

    /// <summary>
    /// 为检测模型预处理图像（YOLOX格式）
    /// </summary>
    private (DenseTensor<float> tensor, float ratio) PreprocessForDetection(SKBitmap image)
    {
        var tensor = new DenseTensor<float>(new[] { 1, 3, DetInputSize, DetInputSize });

        // 计算缩放比例
        float ratio = Math.Min((float)DetInputSize / image.Width, (float)DetInputSize / image.Height);
        int newWidth = (int)(image.Width * ratio);
        int newHeight = (int)(image.Height * ratio);

        // 缩放图像
        using var scaled = image.Resize(new SKImageInfo(newWidth, newHeight), SKFilterQuality.High);

        // 填充到目标尺寸（使用114填充，YOLOX标准）
        const float padValue = 114.0f;

        // 先填充整个张量
        for (int c = 0; c < 3; c++)
        {
            for (int y = 0; y < DetInputSize; y++)
            {
                for (int x = 0; x < DetInputSize; x++)
                {
                    tensor[0, c, y, x] = padValue;
                }
            }
        }

        // 复制缩放后的图像（HWC格式）
        for (int y = 0; y < newHeight; y++)
        {
            for (int x = 0; x < newWidth; x++)
            {
                var pixel = scaled.GetPixel(x, y);
                tensor[0, 0, y, x] = pixel.Red;
                tensor[0, 1, y, x] = pixel.Green;
                tensor[0, 2, y, x] = pixel.Blue;
            }
        }

        return (tensor, ratio);
    }

    /// <summary>
    /// 解析YOLOX检测输出（正确的后处理）
    /// </summary>
    private List<SKRect> ParseYOLOXOutput(Tensor<float> output, int imgWidth, int imgHeight, float ratio)
    {
        var detections = new List<SKRect>();
        var candidates = new List<(float score, SKRect box)>();

        // YOLOX输出格式: [batch, num_anchors, 85] 其中85 = 4(cx,cy,w,h) + 1(obj_conf) + 80(classes)
        // 注意：输出是原始输出，需要后处理解码
        int numDetections = output.Dimensions[1];

        // 第一步：后处理解码（YOLOX decode）
        var decodedOutputs = DecodeYOLOXOutput(output, DetInputSize);

        // 收集前10个最高置信度的框用于调试
        var topScores = decodedOutputs.OrderByDescending(x => x.objConf * x.maxClsScore).Take(10).ToList();
        DebugLog($"前10个最高分数的检测:");
        foreach (var item in topScores)
        {
            DebugLog($"  索引{item.idx}: cx={item.cx:F1}, cy={item.cy:F1}, w={item.w:F1}, h={item.h:F1}, objConf={item.objConf:F3}, clsScore={item.maxClsScore:F3}, clsId={item.maxClsId}, total={item.objConf * item.maxClsScore:F3}");
        }

        // 第二步：过滤和转换
        foreach (var item in decodedOutputs)
        {
            if (item.objConf < 0.01f) continue;

            // 只保留人体检测（COCO类别0）或动物（用于测试）
            // COCO类别: 0=person, 14=bird, 15=cat, 16=dog, 17=horse, 18=sheep, 19=cow, 20=elephant, 21=bear, 22=zebra, 23=giraffe
            if (item.maxClsId != 0 && (item.maxClsId < 14 || item.maxClsId > 23)) continue;

            float finalScore = item.objConf * item.maxClsScore;
            if (finalScore < 0.2f) continue;

            // 从 cx,cy,w,h 转换为 x1,y1,x2,y2
            float x1 = item.cx - item.w / 2;
            float y1 = item.cy - item.h / 2;
            float x2 = item.cx + item.w / 2;
            float y2 = item.cy + item.h / 2;

            // 缩放到原始图像尺寸（除以ratio）
            x1 /= ratio;
            y1 /= ratio;
            x2 /= ratio;
            y2 /= ratio;

            // 裁剪到图像边界
            x1 = Math.Max(0, Math.Min(imgWidth, x1));
            y1 = Math.Max(0, Math.Min(imgHeight, y1));
            x2 = Math.Max(0, Math.Min(imgWidth, x2));
            y2 = Math.Max(0, Math.Min(imgHeight, y2));

            // 过滤太小的框
            if (x2 - x1 < 50 || y2 - y1 < 50) continue;

            var box = new SKRect(x1, y1, x2, y2);
            candidates.Add((finalScore, box));
        }

        DebugLog($"YOLOX原始输出: {numDetections}个候选框，过滤后: {candidates.Count}个");

        // NMS
        candidates = candidates.OrderByDescending(c => c.score).ToList();
        var used = new bool[candidates.Count];

        for (int i = 0; i < candidates.Count && detections.Count < _maxNumHands; i++)
        {
            if (used[i]) continue;

            detections.Add(candidates[i].box);
            used[i] = true;

            for (int j = i + 1; j < candidates.Count; j++)
            {
                if (used[j]) continue;
                if (CalculateIoU(candidates[i].box, candidates[j].box) > 0.3f)  // 降低IoU阈值，更好地去除重叠框
                {
                    used[j] = true;
                }
            }
        }

        DebugLog($"NMS后保留: {detections.Count}个人体框");
        return detections;
    }

    /// <summary>
    /// YOLOX输出解码
    /// </summary>
    private List<(int idx, float cx, float cy, float w, float h, float objConf, float maxClsScore, int maxClsId)> DecodeYOLOXOutput(Tensor<float> output, int imgSize)
    {
        var results = new List<(int, float, float, float, float, float, float, int)>();
        int numDetections = output.Dimensions[1];

        // YOLOX strides
        int[] strides = { 8, 16, 32 };
        int[] hsizes = { imgSize / 8, imgSize / 16, imgSize / 32 };
        int[] wsizes = { imgSize / 8, imgSize / 16, imgSize / 32 };

        // 生成grids
        var grids = new List<(float x, float y)>();
        var expandedStrides = new List<float>();

        for (int i = 0; i < strides.Length; i++)
        {
            for (int h = 0; h < hsizes[i]; h++)
            {
                for (int w = 0; w < wsizes[i]; w++)
                {
                    grids.Add((w, h));
                    expandedStrides.Add(strides[i]);
                }
            }
        }

        // 解码每个检测
        for (int i = 0; i < numDetections && i < grids.Count; i++)
        {
            float cxRaw = output[0, i, 0];
            float cyRaw = output[0, i, 1];
            float wRaw = output[0, i, 2];
            float hRaw = output[0, i, 3];
            float objConf = output[0, i, 4];

            // 解码: (raw + grid) * stride
            float cx = (cxRaw + grids[i].x) * expandedStrides[i];
            float cy = (cyRaw + grids[i].y) * expandedStrides[i];
            float w = (float)Math.Exp(wRaw) * expandedStrides[i];
            float h = (float)Math.Exp(hRaw) * expandedStrides[i];

            // 获取最高分类分数
            float maxClsScore = 0;
            int maxClsId = -1;
            for (int c = 0; c < 80; c++)
            {
                float score = output[0, i, 5 + c];
                if (score > maxClsScore)
                {
                    maxClsScore = score;
                    maxClsId = c;
                }
            }

            results.Add((i, cx, cy, w, h, objConf, maxClsScore, maxClsId));
        }

        return results;
    }

    /// <summary>
    /// 从人体姿态中提取手部关键点
    /// </summary>
    private List<HandPose> ExtractHandPoses(SKBitmap image, SKRect personBox, int startId)
    {
        var handPoses = new List<HandPose>();

        // 预处理人体区域
        var (inputTensor, center, scale) = PreprocessForPose(image, personBox);

        // 运行姿态估计
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input", inputTensor)
        };

        using var results = _poseSession!.Run(inputs);

        // 解析输出
        var keypoints = ParsePoseOutput(results, center, scale);

        // 调试：输出关键点信息
        DebugLog($"姿态估计完成，关键点数量: {keypoints.GetLength(0)}");
        
        // 调试：输出所有关键点中置信度最高的10个
        var allScores = new List<(int idx, float score)>();
        for (int i = 0; i < 133; i++)
        {
            allScores.Add((i, keypoints[i, 2]));
        }
        var topScores = allScores.OrderByDescending(x => x.score).Take(10);
        DebugLog("前10个最高置信度关键点:");
        foreach (var item in topScores)
        {
            DebugLog($"  索引{item.idx}: score={item.score:F3}, x={keypoints[item.idx, 0]:F1}, y={keypoints[item.idx, 1]:F1}");
        }
        
        DebugLog($"左手腕(91): x={keypoints[91, 0]:F1}, y={keypoints[91, 1]:F1}, score={keypoints[91, 2]:F3}");
        DebugLog($"右手腕(113): x={keypoints[113, 0]:F1}, y={keypoints[113, 1]:F1}, score={keypoints[113, 2]:F3}");

        // 计算脸部区域，用于过滤"脸部被误判为手"的情况
        var faceBox = ComputeFaceBox(keypoints);
        if (faceBox != SKRect.Empty)
        {
            DebugLog($"脸部区域: [{faceBox.Left:F0},{faceBox.Top:F0}-{faceBox.Right:F0},{faceBox.Bottom:F0}]");
        }

        // 提取左手
        var leftHand = ExtractSingleHand(keypoints, true, startId, personBox);
        bool leftHandValid = leftHand != null && ValidateHandPose(leftHand, faceBox);
        if (leftHandValid)
        {
            DebugLog($"左手检测成功，关键点数: {leftHand.Keypoints.Count}");
        }
        else
        {
            DebugLog($"左手检测失败: leftHand={(leftHand != null)}, Validate={leftHandValid}");
        }

        // 提取右手
        var rightHand = ExtractSingleHand(keypoints, false, startId + 1, personBox);
        bool rightHandValid = rightHand != null && ValidateHandPose(rightHand, faceBox);
        if (rightHandValid)
        {
            DebugLog($"右手检测成功，关键点数: {rightHand.Keypoints.Count}");
        }
        else
        {
            DebugLog($"右手检测失败: rightHand={(rightHand != null)}, Validate={rightHandValid}");
        }

        // 检查左右手是否重叠（同一只手被检测为两只手的情况）
        if (leftHandValid && rightHandValid)
        {
            float iou = CalculateHandIoU(leftHand, rightHand);
            float wristDist = CalculateWristDistance(leftHand, rightHand);
            DebugLog($"左右手IoU: {iou:F3}, 手腕距离: {wristDist:F1}");
            
            // 修复：降低IoU阈值，并添加手腕距离检查
            if (iou > 0.3f || wristDist < 50f)  // IoU过高或手腕距离过近，认为是同一只手
            {
                // 选择置信度更高的那只手
                float leftScore = leftHand.Keypoints.Average(k => k.Confidence);
                float rightScore = rightHand.Keypoints.Average(k => k.Confidence);
                DebugLog($"左右手重叠，左手平均置信度: {leftScore:F3}, 右手: {rightScore:F3}");
                if (leftScore >= rightScore)
                {
                    handPoses.Add(leftHand);
                    DebugLog("保留左手，丢弃右手");
                }
                else
                {
                    handPoses.Add(rightHand);
                    DebugLog("保留右手，丢弃左手");
                }
            }
            else
            {
                // 真正有两只不同的手
                handPoses.Add(leftHand);
                handPoses.Add(rightHand);
                DebugLog("检测到两只不同的手");
            }
        }
        else
        {
            // 只有一只手有效
            if (leftHandValid) handPoses.Add(leftHand);
            if (rightHandValid) handPoses.Add(rightHand);
        }

        return handPoses;
    }

    /// <summary>
    /// 为姿态模型预处理图像
    /// 修复：正确的图像预处理和调试保存
    /// </summary>
    private (DenseTensor<float>, float[], float[]) PreprocessForPose(SKBitmap image, SKRect box)
    {
        // 扩展检测框以包含更多上下文
        float padding = 1.25f;
        float boxWidth = box.Width * padding;
        float boxHeight = box.Height * padding;
        float centerX = box.MidX;
        float centerY = box.MidY;

        // 保持宽高比
        float aspectRatio = (float)_poseInputWidth / _poseInputHeight;
        if (boxWidth > boxHeight * aspectRatio)
        {
            boxHeight = boxWidth / aspectRatio;
        }
        else
        {
            boxWidth = boxHeight * aspectRatio;
        }

        // 裁剪并缩放
        float scaleX = _poseInputWidth / boxWidth;
        float scaleY = _poseInputHeight / boxHeight;

        var tensor = new DenseTensor<float>(new[] { 1, 3, _poseInputHeight, _poseInputWidth });
        
        // DWPose使用ImageNet标准化参数
        float[] mean = { 123.675f, 116.28f, 103.53f };
        float[] std = { 58.395f, 57.12f, 57.375f };

        // 创建调试用的预处理图像
        bool saveDebugImage = _frameCount % 30 == 0; // 每30帧保存一次
        SKBitmap? debugBitmap = saveDebugImage ? new SKBitmap(_poseInputWidth, _poseInputHeight) : null;
        using var debugCanvas = debugBitmap != null ? new SKCanvas(debugBitmap) : null;
        debugCanvas?.Clear(SKColors.Black);

        for (int y = 0; y < _poseInputHeight; y++)
        {
            for (int x = 0; x < _poseInputWidth; x++)
            {
                // 映射回原始图像坐标
                float srcX = centerX + (x - _poseInputWidth / 2.0f) / scaleX;
                float srcY = centerY + (y - _poseInputHeight / 2.0f) / scaleY;

                int px = (int)Math.Clamp(srcX, 0, image.Width - 1);
                int py = (int)Math.Clamp(srcY, 0, image.Height - 1);

                var pixel = image.GetPixel(px, py);

                // 正确的通道顺序：DWPose期望RGB输入
                // tensor[0, 0, y, x] = R, [0, 1, y, x] = G, [0, 2, y, x] = B
                tensor[0, 0, y, x] = (pixel.Red - mean[0]) / std[0];
                tensor[0, 1, y, x] = (pixel.Green - mean[1]) / std[1];
                tensor[0, 2, y, x] = (pixel.Blue - mean[2]) / std[2];

                // 保存调试图像
                if (debugBitmap != null)
                {
                    debugBitmap.SetPixel(x, y, pixel);
                }
            }
        }

        // 保存调试图像
        if (debugBitmap != null)
        {
            try
            {
                string debugPath = $"debug_pose_input_{_frameCount}.png";
                using var data = debugBitmap.Encode(SKEncodedImageFormat.Png, 100);
                using var stream = File.OpenWrite(debugPath);
                data.SaveTo(stream);
                DebugLog($"保存调试图像: {debugPath}");
            }
            catch { }
            debugBitmap.Dispose();
        }

        float[] center = { centerX, centerY };
        float[] scale = { boxWidth, boxHeight };

        return (tensor, center, scale);
    }

    /// <summary>
    /// 解析姿态估计输出
    /// 修复：正确的坐标解码和映射
    /// </summary>
    private float[,] ParsePoseOutput(IReadOnlyCollection<NamedOnnxValue> results, float[] center, float[] scale)
    {
        // DWPose输出133个关键点 (COCO-WholeBody格式)
        // 手部关键点: 左手指91-112, 右手指113-134
        var keypoints = new float[133, 3]; // x, y, score

        // 获取simcc输出
        var simccX = results.ElementAt(0).AsTensor<float>();
        var simccY = results.ElementAt(1).AsTensor<float>();

        int numKeypoints = simccX.Dimensions[1];
        int sizeX = simccX.Dimensions[2];
        int sizeY = simccY.Dimensions[2];

        // simcc_split_ratio = 2.0 (DWPose默认)
        const float simccSplitRatio = 2.0f;
        
        for (int i = 0; i < numKeypoints && i < 133; i++)
        {
            // 找到最大值位置
            int maxX = 0, maxY = 0;
            float maxValX = float.MinValue, maxValY = float.MinValue;

            for (int x = 0; x < sizeX; x++)
            {
                float val = simccX[0, i, x];
                if (val > maxValX)
                {
                    maxValX = val;
                    maxX = x;
                }
            }

            for (int y = 0; y < sizeY; y++)
            {
                float val = simccY[0, i, y];
                if (val > maxValY)
                {
                    maxValY = val;
                    maxY = y;
                }
            }

            // 解码simcc坐标
            // simcc输出的是归一化后的位置，需要除以split_ratio
            float coordX = maxX / simccSplitRatio;
            float coordY = maxY / simccSplitRatio;

            // 修复：使用与DWPose参考实现相同的坐标映射公式
            // 参考实现: keypoints = keypoints / model_input_size * scale + center - scale / 2
            keypoints[i, 0] = coordX / _poseInputWidth * scale[0] + center[0] - scale[0] / 2;
            keypoints[i, 1] = coordY / _poseInputHeight * scale[1] + center[1] - scale[1] / 2;
            keypoints[i, 2] = Math.Max(maxValX, maxValY);
        }

        return keypoints;
    }

    /// <summary>
    /// 从COCO-WholeBody关键点提取单只手
    /// </summary>
    private HandPose? ExtractSingleHand(float[,] keypoints, bool isLeftHand, int trackId, SKRect personBox)
    {
        // COCO-WholeBody手部关键点索引
        // 左手: 91-112 (wrist + 21关键点)
        // 右手: 113-134 (wrist + 21关键点)
        int startIdx = isLeftHand ? 91 : 113;

        var handPose = new HandPose
        {
            TrackId = trackId,
            HandType = isLeftHand ? HandType.Left : HandType.Right,
            BoundingBox = personBox,
            Timestamp = DateTime.Now
        };

        // 检查手腕点是否存在且置信度足够
        float wristScore = keypoints[startIdx, 2];
        float wristThreshold = 0.25f;  // 提高手腕阈值，减少误检
        DebugLog($"  {(isLeftHand ? "左" : "右")}手腕检查: startIdx={startIdx}, score={wristScore:F3}, threshold={wristThreshold:F3}, pass={wristScore >= wristThreshold}");
        if (wristScore < wristThreshold)
        {
            return null;
        }

        // 转换COCO-WholeBody格式到MediaPipe格式
        // COCO: wrist, thumb1-4, index1-4, middle1-4, ring1-4, pinky1-4
        // MediaPipe: wrist, thumb1-4, index1-4, middle1-4, ring1-4, pinky1-4

        int[] cocoToMediaPipe = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20 };

        for (int i = 0; i < 21; i++)
        {
            int cocoIdx = startIdx + i;
            if (cocoIdx >= keypoints.GetLength(0)) continue;

            float x = keypoints[cocoIdx, 0];
            float y = keypoints[cocoIdx, 1];
            float score = keypoints[cocoIdx, 2];

            // 调试：输出前5个关键点的坐标
            if (i < 5)
            {
                DebugLog($"    {(isLeftHand ? "左" : "右")}手关键点{i}: x={x:F1}, y={y:F1}, score={score:F3}");
            }

            var keypoint = new HandKeypoint(
                (HandKeypointType)cocoToMediaPipe[i],
                x, y, 0, score
            );

            handPose.Keypoints.Add(keypoint);
        }

        // 更新边界框
        if (handPose.Keypoints.Count > 0)
        {
            float minX = handPose.Keypoints.Min(k => k.X);
            float maxX = handPose.Keypoints.Max(k => k.X);
            float minY = handPose.Keypoints.Min(k => k.Y);
            float maxY = handPose.Keypoints.Max(k => k.Y);

            handPose.BoundingBox = new SKRect(minX, minY, maxX, maxY);
        }

        return handPose.Keypoints.Count >= 15 ? handPose : null;
    }

    /// <summary>
    /// 验证手部姿态是否合理 - 严格过滤防止误检（包括脸部误识别为手）
    /// </summary>
    private bool ValidateHandPose(HandPose handPose, SKRect faceBox)
    {
        // 基本要求：至少检测到手腕和几根手指
        if (handPose.Keypoints.Count < 15) return false;

        var wrist = handPose.GetKeypoint(HandKeypointType.Wrist);
        // 手腕必须被检测到且置信度足够高
        if (wrist == null || wrist.Confidence < 0.3f) return false;

        // 检查是否有至少3个指尖被检测到（证明是真实的手）
        var tips = new[]
        {
            handPose.GetKeypoint(HandKeypointType.ThumbTip),
            handPose.GetKeypoint(HandKeypointType.IndexFingerTip),
            handPose.GetKeypoint(HandKeypointType.MiddleFingerTip),
            handPose.GetKeypoint(HandKeypointType.RingFingerTip),
            handPose.GetKeypoint(HandKeypointType.PinkyTip)
        };
        
        int validTips = tips.Count(t => t != null && t.Confidence > 0.2f);
        if (validTips < 3) return false;

        // 检查手部大小合理性（防止面部误检）
        var middleTip = handPose.GetKeypoint(HandKeypointType.MiddleFingerTip);
        if (middleTip != null && middleTip.Confidence > 0.2f)
        {
            float handSize = Math.Abs(middleTip.Y - wrist.Y);
            // 手部大小必须在合理范围内
            if (handSize < 30 || handSize > 400) return false;
        }

        // 新增：检查手部平均置信度
        float avgConfidence = handPose.Keypoints.Average(k => k.Confidence);
        if (avgConfidence < 0.25f) return false;

        // 新增：排除落在脸部区域的手（DWPose 常见误检：把面部关键点当手）
        if (faceBox != SKRect.Empty)
        {
            var handCenter = new SKPoint(handPose.BoundingBox.MidX, handPose.BoundingBox.MidY);
            var wristPoint = new SKPoint(wrist.X, wrist.Y);

            // 若手部中心或手腕在脸框内，或手与脸 IoU 过高，视为假手
            bool centerInFace = faceBox.Contains(handCenter);
            bool wristInFace = faceBox.Contains(wristPoint);
            float faceIoU = CalculateIoU(handPose.BoundingBox, faceBox);

            if (centerInFace || wristInFace || faceIoU > 0.25f)
            {
                DebugLog($"  手部被判定为脸部误检，已过滤 (centerInFace={centerInFace}, wristInFace={wristInFace}, faceIoU={faceIoU:F2})");
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 从 COCO-WholeBody 关键点计算脸部区域（用于过滤手部误检）
    /// </summary>
    private static SKRect ComputeFaceBox(float[,] keypoints)
    {
        // 优先使用脸部网格关键点（23-90，共68点）
        var faceMeshPoints = new List<SKPoint>();
        for (int i = 23; i <= 90 && i < keypoints.GetLength(0); i++)
        {
            if (keypoints[i, 2] > 0.2f)
            {
                faceMeshPoints.Add(new SKPoint(keypoints[i, 0], keypoints[i, 1]));
            }
        }

        // 备用：使用五官关键点（0=鼻子, 1/2=眼睛, 3/4=耳朵）
        var faceFeaturePoints = new List<SKPoint>();
        foreach (int i in new[] { 0, 1, 2, 3, 4 })
        {
            if (i < keypoints.GetLength(0) && keypoints[i, 2] > 0.3f)
            {
                faceFeaturePoints.Add(new SKPoint(keypoints[i, 0], keypoints[i, 1]));
            }
        }

        var points = faceMeshPoints.Count >= 5 ? faceMeshPoints : faceFeaturePoints;
        if (points.Count < 3) return SKRect.Empty;

        float minX = points.Min(p => p.X);
        float maxX = points.Max(p => p.X);
        float minY = points.Min(p => p.Y);
        float maxY = points.Max(p => p.Y);

        // 添加适当边距：下巴、额头、耳朵两侧
        float padX = Math.Max(20f, (maxX - minX) * 0.15f);
        float padY = Math.Max(30f, (maxY - minY) * 0.25f);

        return new SKRect(minX - padX, minY - padY, maxX + padX, maxY + padY);
    }

    /// <summary>
    /// 更新手部跟踪ID - 修复：使用卡尔曼滤波跟踪器
    /// </summary>
    private List<HandPose> UpdateTracking(List<HandPose> currentHands)
    {
        // 简单的最近邻跟踪
        var trackedHands = new List<HandPose>();
        var usedCurrent = new bool[currentHands.Count];
        var matchedTrackers = new HashSet<int>();

        // 计算距离矩阵
        foreach (var trackerPair in _handTrackers.ToList())
        {
            int trackId = trackerPair.Key;
            var tracker = trackerPair.Value;
            if (tracker.LastPose == null) continue;
            
            float minDist = float.MaxValue;
            int bestMatch = -1;

            for (int j = 0; j < currentHands.Count; j++)
            {
                if (usedCurrent[j]) continue;
                if (tracker.LastPose.HandType != currentHands[j].HandType) continue;

                float dist = CalculateHandDistance(tracker.LastPose, currentHands[j]);
                if (dist < minDist && dist < 100) // 100像素阈值
                {
                    minDist = dist;
                    bestMatch = j;
                }
            }

            if (bestMatch >= 0)
            {
                currentHands[bestMatch].TrackId = trackId;
                trackedHands.Add(currentHands[bestMatch]);
                usedCurrent[bestMatch] = true;
                matchedTrackers.Add(trackId);

                // 更新跟踪器
                tracker.Update(currentHands[bestMatch]);
            }
        }

        // 标记未匹配的跟踪器为丢失
        foreach (var tracker in _handTrackers.Values)
        {
            if (!matchedTrackers.Contains(tracker.TrackId))
            {
                tracker.MarkLost();
            }
        }

        // 分配新ID
        int nextId = _handTrackers.Count > 0 ? _handTrackers.Max(k => k.Key) + 1 : 0;
        for (int i = 0; i < currentHands.Count; i++)
        {
            if (!usedCurrent[i])
            {
                currentHands[i].TrackId = nextId++;
                trackedHands.Add(currentHands[i]);
                
                // 创建新跟踪器
                _handTrackers[currentHands[i].TrackId] = new HandKalmanTracker(currentHands[i].TrackId, currentHands[i]);
            }
        }

        return trackedHands;
    }

    /// <summary>
    /// 从历史数据预测手部位置（用于跳帧时保持检测连续性）
    /// </summary>
    private List<HandPose> PredictFromHistory()
    {
        var predictedHands = new List<HandPose>();
        
        // 修复：使用卡尔曼滤波跟踪器进行预测
        foreach (var tracker in _handTrackers.Values.ToList())
        {
            if (!tracker.IsLost)
            {
                var predictedHand = tracker.GetSmoothedPose();
                predictedHands.Add(predictedHand);
                DebugLog($"跟踪器 {tracker.TrackId} 预测位置: ({predictedHand.GetCenter().X:F1}, {predictedHand.GetCenter().Y:F1})");
            }
        }
        
        return predictedHands;
    }

    /// <summary>
    /// 计算两只手的距离
    /// </summary>
    private float CalculateHandDistance(HandPose hand1, HandPose hand2)
    {
        var wrist1 = hand1.GetKeypoint(HandKeypointType.Wrist);
        var wrist2 = hand2.GetKeypoint(HandKeypointType.Wrist);

        if (wrist1 == null || wrist2 == null) return float.MaxValue;

        float dx = wrist1.X - wrist2.X;
        float dy = wrist1.Y - wrist2.Y;
        return (float)Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>
    /// 平滑手部姿态 - 修复：使用卡尔曼滤波替代简单平均
    /// </summary>
    private List<HandPose> SmoothHandPoses(List<HandPose> currentHands)
    {
        var smoothedHands = new List<HandPose>();
        var matchedTrackers = new HashSet<int>();

        // 第一步：匹配检测到的手与现有跟踪器
        foreach (var hand in currentHands)
        {
            int trackId = hand.TrackId;
            
            if (_handTrackers.TryGetValue(trackId, out var tracker))
            {
                // 更新现有跟踪器
                tracker.Update(hand);
                matchedTrackers.Add(trackId);
            }
            else
            {
                // 创建新跟踪器
                _handTrackers[trackId] = new HandKalmanTracker(trackId, hand);
                matchedTrackers.Add(trackId);
                DebugLog($"创建新跟踪器: {trackId}");
            }
        }

        // 第二步：标记未匹配的跟踪器为丢失
        foreach (var tracker in _handTrackers.Values)
        {
            if (!matchedTrackers.Contains(tracker.TrackId))
            {
                tracker.MarkLost();
            }
        }

        // 第三步：清理丢失过久的跟踪器
        var lostTrackers = _handTrackers.Where(t => t.Value.IsLost).Select(t => t.Key).ToList();
        foreach (var trackId in lostTrackers)
        {
            _handTrackers[trackId].Dispose();
            _handTrackers.Remove(trackId);
            DebugLog($"移除丢失跟踪器: {trackId}");
        }

        // 第四步：生成平滑后的结果
        foreach (var hand in currentHands)
        {
            if (_handTrackers.TryGetValue(hand.TrackId, out var tracker))
            {
                // 使用卡尔曼滤波平滑后的结果
                var smoothedHand = tracker.GetSmoothedPose();
                smoothedHands.Add(smoothedHand);
            }
            else
            {
                // 新检测到的手，直接使用
                smoothedHands.Add(hand);
            }
        }

        return smoothedHands;
    }

    /// <summary>
    /// 计算两个框的IoU
    /// </summary>
    private float CalculateIoU(SKRect box1, SKRect box2)
    {
        float x1 = Math.Max(box1.Left, box2.Left);
        float y1 = Math.Max(box1.Top, box2.Top);
        float x2 = Math.Min(box1.Right, box2.Right);
        float y2 = Math.Min(box1.Bottom, box2.Bottom);

        float intersection = Math.Max(0, x2 - x1) * Math.Max(0, y2 - y1);
        float area1 = box1.Width * box1.Height;
        float area2 = box2.Width * box2.Height;
        float union = area1 + area2 - intersection;

        return union > 0 ? intersection / union : 0;
    }

    /// <summary>
    /// 计算两只手的边界框IoU
    /// </summary>
    private float CalculateHandIoU(HandPose hand1, HandPose hand2)
    {
        return CalculateIoU(hand1.BoundingBox, hand2.BoundingBox);
    }

    /// <summary>
    /// 计算两只手的手腕距离
    /// </summary>
    private float CalculateWristDistance(HandPose hand1, HandPose hand2)
    {
        var wrist1 = hand1.GetKeypoint(HandKeypointType.Wrist);
        var wrist2 = hand2.GetKeypoint(HandKeypointType.Wrist);

        if (wrist1 == null || wrist2 == null) return float.MaxValue;

        float dx = wrist1.X - wrist2.X;
        float dy = wrist1.Y - wrist2.Y;
        return (float)Math.Sqrt(dx * dx + dy * dy);
    }

    public void Dispose()
    {
        lock (_lockObject)
        {
            _detSession?.Dispose();
            _poseSession?.Dispose();
            _detSession = null;
            _poseSession = null;
            
            foreach (var tracker in _handTrackers.Values)
            {
                tracker.Dispose();
            }
            _handTrackers.Clear();
        }
    }
}

/// <summary>
/// 手部卡尔曼滤波跟踪器
/// 修复：使用卡尔曼滤波实现真正的运动跟踪
/// </summary>
public class HandKalmanTracker : IDisposable
{
    private readonly int _trackId;
    private readonly KalmanFilter _kalman;
    private int _lostFrames;
    private HandPose? _lastPose;
    private DateTime _lastUpdateTime;
    
    // 卡尔曼滤波状态维度：位置x,y + 速度vx,vy
    private const int StateDim = 4;
    private const int MeasureDim = 2;
    
    public int TrackId => _trackId;
    public int LostFrames => _lostFrames;
    public bool IsLost => _lostFrames > 10;
    public HandPose? LastPose => _lastPose;
    
    public HandKalmanTracker(int trackId, HandPose initialPose)
    {
        _trackId = trackId;
        _lastPose = initialPose;
        _lastUpdateTime = DateTime.Now;
        
        // 初始化卡尔曼滤波器
        _kalman = new KalmanFilter(StateDim, MeasureDim);
        
        // 状态转移矩阵：x = x + vx, y = y + vy, vx = vx, vy = vy
        _kalman.TransitionMatrix = new Matrix(new[,]
        {
            { 1f, 0f, 1f, 0f },
            { 0f, 1f, 0f, 1f },
            { 0f, 0f, 1f, 0f },
            { 0f, 0f, 0f, 1f }
        });
        
        // 测量矩阵：只测量位置x,y
        _kalman.MeasurementMatrix = new Matrix(new[,]
        {
            { 1f, 0f, 0f, 0f },
            { 0f, 1f, 0f, 0f }
        });
        
        // 过程噪声协方差
        _kalman.ProcessNoiseCov = Matrix.Identity(StateDim) * 1e-4f;
        
        // 测量噪声协方差
        _kalman.MeasurementNoiseCov = Matrix.Identity(MeasureDim) * 1e-2f;
        
        // 初始状态
        var center = initialPose.GetCenter();
        _kalman.StatePost = new Matrix(new[,]
        {
            { center.X },
            { center.Y },
            { 0f },
            { 0f }
        });
    }
    
    /// <summary>
    /// 预测下一帧位置
    /// </summary>
    public SKPoint Predict()
    {
        var prediction = _kalman.Predict();
        return new SKPoint(prediction[0, 0], prediction[1, 0]);
    }
    
    /// <summary>
    /// 更新跟踪器状态
    /// </summary>
    public HandPose Update(HandPose detection)
    {
        var center = detection.GetCenter();
        var measurement = new Matrix(new[,]
        {
            { center.X },
            { center.Y }
        });
        
        _kalman.Correct(measurement);
        _lostFrames = 0;
        _lastPose = detection;
        _lastUpdateTime = DateTime.Now;
        
        return detection;
    }
    
    /// <summary>
    /// 标记为丢失
    /// </summary>
    public void MarkLost()
    {
        _lostFrames++;
    }
    
    /// <summary>
    /// 获取平滑后的手部姿态（使用卡尔曼滤波预测）
    /// </summary>
    public HandPose GetSmoothedPose()
    {
        if (_lastPose == null) throw new InvalidOperationException("跟踪器未初始化");
        
        // 修复：如果刚更新过（丢失帧数为0），直接返回原始检测值，不做预测
        if (_lostFrames == 0)
        {
            return _lastPose;
        }
        
        // 只有在丢失检测时才使用预测
        var predictedCenter = Predict();
        var smoothedPose = new HandPose
        {
            TrackId = _trackId,
            HandType = _lastPose.HandType,
            BoundingBox = _lastPose.BoundingBox,
            Keypoints = new List<HandKeypoint>(),
            Timestamp = DateTime.Now
        };
        
        // 计算偏移量，限制最大偏移防止预测漂移
        var lastCenter = _lastPose.GetCenter();
        float offsetX = predictedCenter.X - lastCenter.X;
        float offsetY = predictedCenter.Y - lastCenter.Y;
        
        // 限制偏移量，防止预测漂移过大
        const float maxOffset = 30f;  // 进一步限制偏移量
        offsetX = Math.Clamp(offsetX, -maxOffset, maxOffset);
        offsetY = Math.Clamp(offsetY, -maxOffset, maxOffset);
        
        // 应用偏移到所有关键点
        foreach (var kp in _lastPose.Keypoints)
        {
            smoothedPose.Keypoints.Add(new HandKeypoint(
                kp.Type,
                kp.X + offsetX,
                kp.Y + offsetY,
                kp.Z,
                kp.Confidence * 0.9f  // 降低置信度表示预测值
            ));
        }
        
        return smoothedPose;
    }
    
    public void Dispose()
    {
        _kalman?.Dispose();
    }
}

/// <summary>
/// 简化的卡尔曼滤波器实现 - 修复：使用非泛型Matrix
/// </summary>
public class KalmanFilter : IDisposable
{
    private Matrix _statePre;      // 先验状态
    private Matrix _statePost;     // 后验状态
    private Matrix _transitionMatrix;  // 状态转移矩阵
    private Matrix _measurementMatrix; // 测量矩阵
    private Matrix _processNoiseCov;   // 过程噪声协方差
    private Matrix _measurementNoiseCov; // 测量噪声协方差
    private Matrix _errorCovPre;   // 先验误差协方差
    private Matrix _errorCovPost;  // 后验误差协方差
    private Matrix _gain;          // 卡尔曼增益
    
    public Matrix StatePost
    {
        get => _statePost;
        set => _statePost = value.Clone();
    }
    
    public Matrix TransitionMatrix
    {
        get => _transitionMatrix;
        set => _transitionMatrix = value.Clone();
    }
    
    public Matrix MeasurementMatrix
    {
        get => _measurementMatrix;
        set => _measurementMatrix = value.Clone();
    }
    
    public Matrix ProcessNoiseCov
    {
        get => _processNoiseCov;
        set => _processNoiseCov = value.Clone();
    }
    
    public Matrix MeasurementNoiseCov
    {
        get => _measurementNoiseCov;
        set => _measurementNoiseCov = value.Clone();
    }
    
    public KalmanFilter(int dynamParams, int measureParams)
    {
        _statePre = new Matrix(dynamParams, 1);
        _statePost = new Matrix(dynamParams, 1);
        _transitionMatrix = Matrix.Identity(dynamParams);
        _measurementMatrix = new Matrix(measureParams, dynamParams);
        _processNoiseCov = Matrix.Identity(dynamParams);
        _measurementNoiseCov = Matrix.Identity(measureParams);
        _errorCovPre = new Matrix(dynamParams, dynamParams);
        _errorCovPost = Matrix.Identity(dynamParams);
        _gain = new Matrix(dynamParams, measureParams);
    }
    
    public Matrix Predict()
    {
        // x'(k) = A * x(k-1)
        _statePre = _transitionMatrix * _statePost;
        
        // P'(k) = A * P(k-1) * A^T + Q
        _errorCovPre = _transitionMatrix * _errorCovPost * _transitionMatrix.Transpose() + _processNoiseCov;
        
        return _statePre;
    }
    
    public void Correct(Matrix measurement)
    {
        // K(k) = P'(k) * H^T * (H * P'(k) * H^T + R)^-1
        var temp = _measurementMatrix * _errorCovPre * _measurementMatrix.Transpose() + _measurementNoiseCov;
        _gain = _errorCovPre * _measurementMatrix.Transpose() * temp.Inverse();
        
        // x(k) = x'(k) + K(k) * (z(k) - H * x'(k))
        var innovation = measurement - _measurementMatrix * _statePre;
        _statePost = _statePre + _gain * innovation;
        
        // P(k) = (I - K(k) * H) * P'(k)
        var identity = Matrix.Identity(_statePost.Rows);
        _errorCovPost = (identity - _gain * _measurementMatrix) * _errorCovPre;
    }
    
    public void Dispose()
    {
        // 清理资源
    }
}

/// <summary>
/// 简单的矩阵实现（用于卡尔曼滤波）- 修复：改为非泛型float专用版本
/// </summary>
public class Matrix
{
    private readonly float[,] _data;
    
    public int Rows => _data.GetLength(0);
    public int Cols => _data.GetLength(1);
    
    public float this[int row, int col]
    {
        get => _data[row, col];
        set => _data[row, col] = value;
    }
    
    public Matrix(int rows, int cols)
    {
        _data = new float[rows, cols];
    }
    
    public Matrix(float[,] data)
    {
        _data = (float[,])data.Clone();
    }
    
    public Matrix Clone()
    {
        return new Matrix(_data);
    }
    
    public static Matrix Identity(int size)
    {
        var matrix = new Matrix(size, size);
        for (int i = 0; i < size; i++)
        {
            matrix[i, i] = 1f;
        }
        return matrix;
    }
    
    public static Matrix operator *(Matrix a, Matrix b)
    {
        if (a.Cols != b.Rows)
            throw new ArgumentException("矩阵维度不匹配");
        
        var result = new Matrix(a.Rows, b.Cols);
        for (int i = 0; i < a.Rows; i++)
        {
            for (int j = 0; j < b.Cols; j++)
            {
                float sum = 0;
                for (int k = 0; k < a.Cols; k++)
                {
                    sum += a[i, k] * b[k, j];
                }
                result[i, j] = sum;
            }
        }
        return result;
    }
    
    public static Matrix operator *(Matrix a, float scalar)
    {
        var result = new Matrix(a.Rows, a.Cols);
        for (int i = 0; i < a.Rows; i++)
        {
            for (int j = 0; j < a.Cols; j++)
            {
                result[i, j] = a[i, j] * scalar;
            }
        }
        return result;
    }
    
    public static Matrix operator +(Matrix a, Matrix b)
    {
        if (a.Rows != b.Rows || a.Cols != b.Cols)
            throw new ArgumentException("矩阵维度不匹配");
        
        var result = new Matrix(a.Rows, a.Cols);
        for (int i = 0; i < a.Rows; i++)
        {
            for (int j = 0; j < a.Cols; j++)
            {
                result[i, j] = a[i, j] + b[i, j];
            }
        }
        return result;
    }
    
    public static Matrix operator -(Matrix a, Matrix b)
    {
        if (a.Rows != b.Rows || a.Cols != b.Cols)
            throw new ArgumentException("矩阵维度不匹配");
        
        var result = new Matrix(a.Rows, a.Cols);
        for (int i = 0; i < a.Rows; i++)
        {
            for (int j = 0; j < a.Cols; j++)
            {
                result[i, j] = a[i, j] - b[i, j];
            }
        }
        return result;
    }
    
    public Matrix Transpose()
    {
        var result = new Matrix(Cols, Rows);
        for (int i = 0; i < Rows; i++)
        {
            for (int j = 0; j < Cols; j++)
            {
                result[j, i] = this[i, j];
            }
        }
        return result;
    }
    
    public Matrix Inverse()
    {
        if (Rows != Cols)
            throw new ArgumentException("只有方阵才能求逆");
        
        int n = Rows;
        var result = Identity(n);
        var temp = Clone();
        
        // 高斯-约旦消元法
        for (int i = 0; i < n; i++)
        {
            // 找主元
            float pivot = temp[i, i];
            if (Math.Abs(pivot) < 1e-10)
                throw new InvalidOperationException("矩阵不可逆");
            
            // 归一化当前行
            for (int j = 0; j < n; j++)
            {
                temp[i, j] /= pivot;
                result[i, j] /= pivot;
            }
            
            // 消去其他行
            for (int k = 0; k < n; k++)
            {
                if (k != i)
                {
                    float factor = temp[k, i];
                    for (int j = 0; j < n; j++)
                    {
                        temp[k, j] -= factor * temp[i, j];
                        result[k, j] -= factor * result[i, j];
                    }
                }
            }
        }
        
        return result;
    }
}
