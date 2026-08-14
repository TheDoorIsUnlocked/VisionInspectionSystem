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

    // 平滑处理参数（EMA 权重，last 占 _smoothingFactor）
    private readonly float _smoothingFactor = 0.7f;

    // 多手时间平滑状态：按稳定 TrackId 维护最近帧手部姿态
    private readonly object _trackLock = new();
    private readonly Dictionary<int, HandPose> _trackedHands = new();
    private int _nextTrackId = 0;

    // 阈值与过滤参数（运行时从配置读取，支持界面调节并持久化）
    private readonly float _keypointConfidenceThreshold;  // 关键点平均置信度阈值
    private readonly float _minBoxAreaRatio;              // 最小检测框面积比例
    private readonly bool _enableFaceFilter;              // 是否启用面部过滤
    private readonly float _faceFilterUpperRatio;         // 面部过滤画面上边界比例

    // 日志（仅控制台，不写文件避免 I/O 卡顿）
    private static void DebugLog(string message)
    {
        System.Diagnostics.Debug.WriteLine($"[MediaPipe] {message}");
    }

    public bool IsInitialized => _palmSession != null && _landmarkSession != null;

    public MediaPipeHandDetector(HandPoseEstimationConfig config)
    {
        _palmModelPath = config.PalmModelPath;
        _landmarkModelPath = config.LandmarkModelPath;
        _confidenceThreshold = config.DetectionConfidenceThreshold;   // 手掌检测置信度阈值
        _keypointConfidenceThreshold = config.ConfidenceThreshold;     // 关键点平均置信度阈值
        _maxNumHands = config.MaxNumHands;
        _minBoxAreaRatio = config.MinBoxAreaRatio;
        _enableFaceFilter = config.EnableFaceFilter;
        _faceFilterUpperRatio = config.FaceFilterUpperRatio;
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
                    InterOpNumThreads = 2,
                    IntraOpNumThreads = 2,
                    GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
                };

                _palmSession = new InferenceSession(_palmModelPath, options);
                _landmarkSession = new InferenceSession(_landmarkModelPath, options);

                // 打印模型输入输出信息
                Console.WriteLine("[MediaPipeHand] Palm model inputs:");
                foreach (var m in _palmSession.InputMetadata)
                    Console.WriteLine($"  {m.Key}: {string.Join("×", m.Value.Dimensions)} ({m.Value.ElementType})");
                Console.WriteLine("[MediaPipeHand] Palm model outputs:");
                foreach (var m in _palmSession.OutputMetadata)
                    Console.WriteLine($"  {m.Key}: {string.Join("×", m.Value.Dimensions)} ({m.Value.ElementType})");

                Console.WriteLine("[MediaPipeHand] Landmark model inputs:");
                foreach (var m in _landmarkSession.InputMetadata)
                    Console.WriteLine($"  {m.Key}: {string.Join("×", m.Value.Dimensions)} ({m.Value.ElementType})");
                Console.WriteLine("[MediaPipeHand] Landmark model outputs:");
                foreach (var m in _landmarkSession.OutputMetadata)
                    Console.WriteLine($"  {m.Key}: {string.Join("×", m.Value.Dimensions)} ({m.Value.ElementType})");

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
        DebugLog("DetectHands 开始");

        if (_palmSession == null || _landmarkSession == null)
        {
            DebugLog("模型未加载，返回空");
            return new List<HandPose>();
        }

        // 在副本上做多遍抑制：palm 模型一遍通常只出 1 个框，
        // 故每遍检完一只手就把该区域涂黑，再跑一遍逼出第 2 只手（最多 _maxNumHands 遍）。
        var working = image.Copy();
        if (working == null)
        {
            DebugLog("图像拷贝失败，直接在原图检测单遍");
            working = image; // 兜底：不释放原图（调用方拥有），直接在原图单遍检测
        }

        try
        {
            var hands = new List<HandPose>();
            var detectedPalms = new List<PalmDetection>();

            for (int pass = 0; pass < _maxNumHands; pass++)
            {
                // 第一阶段：手掌检测（在已涂黑的副本上）
                var palmDetections = DetectPalms(working);
                DebugLog($"第{pass + 1}遍手掌检测完成，检测到 {palmDetections.Count} 个候选手掌");

                if (palmDetections.Count == 0)
                    break;

                // 选置信度最高、且不与已检出手重叠（IoU>0.3）的候选框，避免重复选中同一只手
                PalmDetection? best = null;
                foreach (var p in palmDetections.OrderByDescending(p => p.Confidence))
                {
                    bool overlaps = detectedPalms.Any(d => PalmIoU(d, p) > 0.3f);
                    if (!overlaps)
                    {
                        best = p;
                        break;
                    }
                }
                if (best == null)
                    break;

                // 第二阶段：对该手掌做关键点检测（从副本裁剪，已检出的手已被涂黑，不会污染）
                var handPose = DetectHandLandmarks(working, best);

                bool accepted = false;
                if (handPose != null)
                {
                    float avgConfidence = handPose.Keypoints.Count > 0
                        ? handPose.Keypoints.Average(kp => kp.Confidence)
                        : 0;

                    bool confOk = avgConfidence >= _keypointConfidenceThreshold;
                    bool valid = ValidateHandPose(handPose);

                    if (confOk && valid)
                    {
                        hands.Add(handPose);
                        detectedPalms.Add(best);
                        accepted = true;
                        DebugLog($"第{pass + 1}遍: 检出第{hands.Count}只手 (score={best.Confidence:F3}, conf={avgConfidence:F3})");
                    }
                    else
                    {
                        DebugLog($"第{pass + 1}遍: 候选框验证失败 confOk={confOk} valid={valid}，涂黑后继续");
                    }
                }
                else
                {
                    DebugLog($"第{pass + 1}遍: 关键点检测失败，涂黑后继续");
                }

                // 无论本遍是否成功，都把该候选区域涂黑：避免下一遍重复选中同一只手陷入死循环
                MaskRegion(working, best);

                if (accepted && hands.Count >= _maxNumHands)
                    break;
            }

            if (hands.Count == 0)
            {
                // 无检测，清空多手跟踪状态
                lock (_trackLock) _trackedHands.Clear();
                return new List<HandPose>();
            }

            // 应用多手时间平滑
            var smoothedHands = ApplySmoothing(hands);
            DebugLog($"DetectHands 结束，返回 {smoothedHands.Count} 只手");
            return smoothedHands;
        }
        catch (Exception ex)
        {
            DebugLog($"检测异常: {ex.Message}");
            return new List<HandPose>();
        }
        finally
        {
            // 仅当 working 是独立副本时才释放，避免误释放调用方拥有的原图
            if (working != image)
                working.Dispose();
        }
    }

    /// <summary>
    /// 应用多手时间平滑：按稳定 TrackId 维护最近一帧的手部姿态。
    /// 用包围盒中心点最近邻匹配跨帧 ID，EMA 平滑关键点与包围盒，支持双手输出且跨帧 ID 稳定。
    /// </summary>
    private List<HandPose> ApplySmoothing(List<HandPose> currentHands)
    {
        if (currentHands.Count == 0)
            return new List<HandPose>();

        lock (_trackLock)
        {
            var matchedIds = new HashSet<int>();
            var result = new List<HandPose>();

            foreach (var cur in currentHands)
            {
                int bestId = -1;
                float bestDist = float.MaxValue;
                var curCenter = BoxCenter(cur.BoundingBox);
                foreach (var kv in _trackedHands)
                {
                    var prevCenter = BoxCenter(kv.Value.BoundingBox);
                    float dx = prevCenter.X - curCenter.X;
                    float dy = prevCenter.Y - curCenter.Y;
                    float d = dx * dx + dy * dy;
                    float maxDist = Math.Max(cur.BoundingBox.Width, kv.Value.BoundingBox.Width) * 1.6f + 30f;
                    if (d < bestDist && MathF.Sqrt(d) < maxDist)
                    {
                        bestDist = d;
                        bestId = kv.Key;
                    }
                }

                HandPose smoothed;
                if (bestId >= 0)
                {
                    var prev = _trackedHands[bestId];
                    smoothed = new HandPose
                    {
                        TrackId = bestId,
                        HandType = cur.HandType,
                        BoundingBox = EmaRect(prev.BoundingBox, cur.BoundingBox),
                        Keypoints = EmaKeypoints(prev.Keypoints, cur.Keypoints),
                        Timestamp = cur.Timestamp
                    };
                    _trackedHands[bestId] = smoothed;
                }
                else
                {
                    int newId = _nextTrackId++;
                    cur.TrackId = newId;
                    smoothed = cur;
                    _trackedHands[newId] = cur;
                }

                matchedIds.Add(smoothed.TrackId);
                result.Add(smoothed);
            }

            foreach (var staleId in _trackedHands.Keys.Where(k => !matchedIds.Contains(k)).ToList())
                _trackedHands.Remove(staleId);

            return result;
        }
    }

    /// <summary>
    /// 验证手部姿态是否合理，用于过滤面部误检测。
    /// 三重检查：关键点散布面积、手腕到指尖距离、包围盒宽高比。
    /// </summary>
    private bool ValidateHandPose(HandPose hand)
    {
        if (hand.Keypoints.Count < 21) return false;

        // 获取手腕和指尖
        var wrist = hand.Keypoints.FirstOrDefault(kp => kp.Type == HandKeypointType.Wrist);
        var tipTypes = new[] {
            HandKeypointType.ThumbTip, HandKeypointType.IndexFingerTip,
            HandKeypointType.MiddleFingerTip, HandKeypointType.RingFingerTip,
            HandKeypointType.PinkyTip
        };
        var tips = hand.Keypoints.Where(k => tipTypes.Contains(k.Type) && k.Confidence > 0.3f).ToList();

        if (wrist == null || tips.Count < 2) return false;

        // === 检查1：关键点散布面积（面部误检所有点挤在一起） ===
        float minX = float.MaxValue, minY = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue;
        foreach (var kp in hand.Keypoints)
        {
            if (kp.X < minX) minX = kp.X;
            if (kp.Y < minY) minY = kp.Y;
            if (kp.X > maxX) maxX = kp.X;
            if (kp.Y > maxY) maxY = kp.Y;
        }

        float kpWidth = maxX - minX;
        float kpHeight = maxY - minY;
        float kpArea = kpWidth * kpHeight;

        // 散布面积至少 30x30 = 900 平方像素（面部局部误检通常远小于此）
        if (kpArea < 900f)
        {
            DebugLog($"ValidateHandPose: 关键点散布面积太小({kpArea:F0}px²)，可能是面部局部误检测");
            return false;
        }

        // === 检查2：手腕到最远指尖距离（与 YOLO HasValidHandStructure 相同逻辑） ===
        float maxWristToTip = 0;
        foreach (var tip in tips)
        {
            float dx = wrist.X - tip.X;
            float dy = wrist.Y - tip.Y;
            float dist = MathF.Sqrt(dx * dx + dy * dy);
            if (dist > maxWristToTip) maxWristToTip = dist;
        }

        float boxDiagonal = MathF.Sqrt(kpWidth * kpWidth + kpHeight * kpHeight);
        // 手腕到指尖距离应 > 包围盒对角线的 18%
        if (maxWristToTip < boxDiagonal * 0.18f)
        {
            DebugLog($"ValidateHandPose: 手腕到指尖距离太短({maxWristToTip:F0} < {boxDiagonal * 0.18f:F0})，可能是面部误检测");
            return false;
        }

        // === 检查3：包围盒宽高比（面部接近正方形，手部呈矩形） ===
        float aspectRatio = kpWidth / Math.Max(kpHeight, 1);

        // 手腕是否在包围盒边缘（手部：手腕在边缘；面部：在内部）
        float distFromEdge = Math.Min(
            Math.Min(wrist.X - minX, maxX - wrist.X) / Math.Max(kpWidth, 1),
            Math.Min(wrist.Y - minY, maxY - wrist.Y) / Math.Max(kpHeight, 1)
        );

        // 接近正方形且手腕在内部 → 面部
        bool squareShape = aspectRatio > 0.6f && aspectRatio < 1.7f;
        if (squareShape && distFromEdge > 0.25f)
        {
            DebugLog($"ValidateHandPose: 正方形包围盒+手腕在内部 aspect={aspectRatio:F2} edgeDist={distFromEdge:F2} → 面部");
            return false;
        }

        DebugLog($"ValidateHandPose: 通过 area={kpArea:F0} wristToTip={maxWristToTip:F0}/{boxDiagonal*0.18f:F0} aspect={aspectRatio:F2}");
        return true;
    }

    /// <summary>
    /// 计算包围盒中心点
    /// </summary>
    private static SKPoint BoxCenter(SKRect r)
        => new SKPoint((r.Left + r.Right) / 2f, (r.Top + r.Bottom) / 2f);

    /// <summary>
    /// 对两个包围盒做 EMA 平滑（last 权重 _smoothingFactor，current 权重 1-_smoothingFactor）
    /// </summary>
    private SKRect EmaRect(SKRect prev, SKRect cur)
    {
        return new SKRect(
            SmoothValue(prev.Left, cur.Left),
            SmoothValue(prev.Top, cur.Top),
            SmoothValue(prev.Right, cur.Right),
            SmoothValue(prev.Bottom, cur.Bottom));
    }

    /// <summary>
    /// 对两组（同序）关键点做 EMA 平滑（按索引对齐；数量不一致时多余关键点直接采用当前帧）
    /// </summary>
    private List<HandKeypoint> EmaKeypoints(List<HandKeypoint> prev, List<HandKeypoint> cur)
    {
        var outList = new List<HandKeypoint>();
        int n = Math.Min(prev.Count, cur.Count);
        for (int i = 0; i < n; i++)
        {
            var p = prev[i];
            var c = cur[i];
            outList.Add(new HandKeypoint(
                c.Type,
                SmoothValue(p.X, c.X),
                SmoothValue(p.Y, c.Y),
                SmoothValue(p.Z, c.Z),
                c.Confidence));
        }
        for (int i = n; i < cur.Count; i++)
            outList.Add(cur[i]);
        return outList;
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

            // 记录输出名称以便调试模型格式
            DebugLog($"boxesResult.Name={boxesResult.Name}, scoresResult.Name={scoresResult.Name}");

            // 检查是否是特殊的单阶段模型输出 (batch_nums + score_cx_cy_w...)
            bool isSingleStageModel = boxes == null ||
                                      boxesResult.Name == "batch_nums" ||
                                      scoresResult.Name.Contains("score_cx_cy_w") ||
                                      scoresResult.Name.Contains("output");

            if (isSingleStageModel && scores != null)
            {
                DebugLog("检测到单阶段手部检测模型，使用适配的解析逻辑");
                var result = ParseSingleStagePalmOutput(scores, image.Width, image.Height);
                // 过滤面部误检测
                result = FilterFaceDetections(result, image.Width, image.Height);
                return result;
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
    /// 解析单阶段手部检测模型输出。
    /// 模型输出张量维度为 [N, 8]，N 是动态检测数量（模型原生支持多手），
    /// 每行 [score, cx, cy, w, wrist_x, wrist_y, middle_x, middle_y]（归一化到 192×192）。
    /// 遍历全部 N 行，按手掌置信度阈值过滤，做最小框面积过滤 + NMS(IoU>0.5) 后再返回。
    /// 早期版本误把特征维 8 当作检测数、且固定读第 0 行，导致永远只返回 1 只手。
    /// </summary>
    private List<PalmDetection> ParseSingleStagePalmOutput(Tensor<float> scores, int imageWidth, int imageHeight)
    {
        var raw = new List<PalmDetection>();
        if (scores == null) return raw;

        int n = scores.Dimensions[0];
        int feat = scores.Dimensions.Length > 1 ? scores.Dimensions[1] : 0;

        DebugLog($"解析单阶段模型输出，N={n} feat={feat}");

        if (feat < 8)
        {
            DebugLog($"输出特征维度 {feat} 不符合预期 (需要 >= 8)");
            return raw;
        }

        float minArea = _minBoxAreaRatio * imageWidth * imageHeight;

        for (int i = 0; i < n; i++)
        {
            float score = scores[i, 0];
            float cx = scores[i, 1];
            float cy = scores[i, 2];
            float w = scores[i, 3];

            if (score <= _confidenceThreshold)
                continue;

            float width = w * imageWidth * 1.25f;
            float height = w * imageHeight * 1.25f;
            float centerX = cx * imageWidth;
            float centerY = cy * imageHeight;

            float x = Math.Max(0, centerX - width / 2);
            float y = Math.Max(0, centerY - height / 2);
            if (x + width > imageWidth) width = imageWidth - x;
            if (y + height > imageHeight) height = imageHeight - y;

            float area = width * height;
            if (area < minArea)
            {
                DebugLog($"第{i}个候选框面积太小({area:F0} < {minArea:F0})，丢弃");
                continue;
            }

            raw.Add(new PalmDetection
            {
                X = x,
                Y = y,
                Width = width,
                Height = height,
                Confidence = score
            });

            DebugLog($"palm[{i}] box: X={x:F0} Y={y:F0} W={width:F0} H={height:F0} score={score:F3}");
        }

        var result = NmsPalmDetections(raw, 0.5f);
        DebugLog($"单阶段解析得到 {result.Count} 个手掌（NMS 后，N={n}）");
        return result;
    }

    /// <summary>
    /// 过滤面部误检测。仅当启用面部过滤（EnableFaceFilter）时生效。
    /// 启发式：竖长脸(宽高比 0.55~0.9 且偏上)、横宽脸局部(>1.5 且很靠上)、
    /// 近似正方且很靠上(squareHigh) 视为面部丢弃。
    /// 用 FaceFilterUpperRatio 控制"画面上方"的判定边界（值越大过滤越激进）。
    /// </summary>
    private List<PalmDetection> FilterFaceDetections(List<PalmDetection> detections, int imageWidth, int imageHeight)
    {
        if (!_enableFaceFilter)
            return detections;

        float upper = _faceFilterUpperRatio;
        var filtered = new List<PalmDetection>();
        foreach (var d in detections)
        {
            float boxW = d.Width;
            float boxH = d.Height;
            if (boxH < 1) boxH = 1;
            float aspectRatio = boxW / boxH;
            float centerY = d.Y + boxH / 2f;

            bool tallFace = aspectRatio >= 0.55f && aspectRatio <= 0.9f
                && centerY < imageHeight * upper;

            bool wideFacePart = aspectRatio > 1.5f
                && centerY < imageHeight * Math.Min(upper, 0.28f);

            bool squareHigh = aspectRatio > 0.9f && aspectRatio <= 1.3f
                && centerY < imageHeight * 0.18f;

            if (tallFace || wideFacePart || squareHigh)
            {
                DebugLog($"FilterFace: 丢弃面部误检 aspect={aspectRatio:F2} tall={tallFace} wide={wideFacePart} sqHigh={squareHigh}");
                continue;
            }
            filtered.Add(d);
        }
        return filtered;
    }

    /// <summary>
    /// 计算裁剪区域（与 CropHandRegion 保持一致）
    /// </summary>
    private (float cropX, float cropY, float cropW, float cropH) GetCropRect(SKBitmap image, PalmDetection palm)
    {
        int margin = 20;
        float x = Math.Max(0, palm.X - margin);
        float y = Math.Max(0, palm.Y - margin);
        float w = Math.Min(palm.Width + margin * 2, image.Width - x);
        float h = Math.Min(palm.Height + margin * 2, image.Height - y);
        return (x, y, w, h);
    }

    /// <summary>
    /// 手部关键点检测
    /// </summary>
    private HandPose? DetectHandLandmarks(SKBitmap image, PalmDetection palm)
    {
        // 获取裁剪区域参数（用于后续坐标映射）
        var (cropX, cropY, cropW, cropH) = GetCropRect(image, palm);

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
                        // 模型输入是 crop 区域 resize 到 224x224 的结果
                        // 所以输出坐标需要映射回 crop 区域，再加回 crop 偏移
                        float x, y;
                        if (nx > 1.0f || ny > 1.0f)
                        {
                            // 输出是像素坐标（相对于 224x224）
                            x = (nx / inputSize) * cropW + cropX;
                            y = (ny / inputSize) * cropH + cropY;
                        }
                        else
                        {
                            // 输出是 0-1 归一化坐标（相对于 224x224 输入）
                            x = nx * cropW + cropX;
                            y = ny * cropH + cropY;
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

                        // 转换回原始图像坐标（使用 crop 区域参数）
                        float x = nx * cropW + cropX;
                        float y = ny * cropH + cropY;

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

        // 归一化: RGB → [0, 1]（该模型训练时使用此范围）
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

        // 归一化: RGB → [0, 1]
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
        lock (_trackLock) _trackedHands.Clear();
    }

    /// <summary>
    /// 在图像上涂黑指定手掌区域，用于多遍抑制重跑（把已检出的手压掉，逼出第 2 只手）。
    /// 涂黑框在原检测框基础上向外膨胀约 20%，确保手掌边缘不会被下一遍重新检出。
    /// </summary>
    private static void MaskRegion(SKBitmap image, PalmDetection d)
    {
        float inflate = Math.Max(d.Width, d.Height) * 0.2f;
        float x = Math.Max(0, d.X - inflate);
        float y = Math.Max(0, d.Y - inflate);
        float w = Math.Min(image.Width - x, d.Width + inflate * 2);
        float h = Math.Min(image.Height - y, d.Height + inflate * 2);

        using var paint = new SKPaint { Color = SKColors.Black };
        using var canvas = new SKCanvas(image);
        canvas.DrawRect(x, y, w, h, paint);
    }

    /// <summary>
    /// 计算两个手掌检测框的交并比（IoU）
    /// </summary>
    private static float PalmIoU(PalmDetection a, PalmDetection b)
    {
        float ax2 = a.X + a.Width, ay2 = a.Y + a.Height;
        float bx2 = b.X + b.Width, by2 = b.Y + b.Height;
        float ix1 = Math.Max(a.X, b.X), iy1 = Math.Max(a.Y, b.Y);
        float ix2 = Math.Min(ax2, bx2), iy2 = Math.Min(ay2, by2);
        float iw = Math.Max(0, ix2 - ix1), ih = Math.Max(0, iy2 - iy1);
        float inter = iw * ih;
        float areaA = a.Width * a.Height;
        float areaB = b.Width * b.Height;
        float union = areaA + areaB - inter;
        return union > 0 ? inter / union : 0f;
    }

    /// <summary>
    /// 对棕榈检测框做非极大值抑制（按置信度降序，IoU 超过阈值的低分框被丢弃）
    /// </summary>
    private static List<PalmDetection> NmsPalmDetections(List<PalmDetection> detections, float iouThreshold)
    {
        var ordered = detections.OrderByDescending(d => d.Confidence).ToList();
        var keep = new List<PalmDetection>();
        while (ordered.Count > 0)
        {
            var best = ordered[0];
            keep.Add(best);
            ordered.RemoveAt(0);
            ordered.RemoveAll(d => PalmIoU(best, d) > iouThreshold);
        }
        return keep;
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
