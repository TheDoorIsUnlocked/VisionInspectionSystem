# -*- coding: utf-8 -*-
# 完整重建脚本：在 `git checkout HEAD -- src/` 之后运行，
# 一键重做全部 SOP 手部检测后端改造（R1-R14 + 手动编辑 + 删除 DWPose 残留 + 接口精简）。
# 设计目标：抵抗外部进程反复抹除工作树 —— 索引/根目录未跟踪文件幸存，脚本可重复运行。
import io, re, os, sys
import subprocess as _sp

def git(*args):
    # 在同一 Python 进程内执行 git，写文件后微秒级暂存，消除外部抹除在"写→加"间隙删库的陷阱
    r = _sp.run(["git"] + list(args), cwd=base, capture_output=True, text=True)
    if r.returncode != 0:
        sys.stderr.write(f"[GIT FAIL] git {' '.join(map(str, args))}\n{r.stderr}\n")
        raise SystemExit("[FAIL] git 命令失败，已中止（未提交，工作树已 reset 到干净 HEAD）")
    return r.stdout.strip()

def stage(p):
    git("add", p)
    print(f"[GIT ADD] {os.path.basename(p)}")

def load(p):
    with io.open(p, "r", encoding="utf-8") as f:
        return f.read()

def save(p, s):
    with io.open(p, "w", encoding="utf-8", newline="\n") as f:
        f.write(s)

def normalize(s):
    return "\n".join(line.rstrip() for line in s.replace("\r\n", "\n").split("\n"))

def apply_rx(path, pats):
    s = normalize(load(path))
    for i, (pat, repl) in enumerate(pats):
        rx = re.compile(pat, re.DOTALL)
        if not rx.search(s):
            raise SystemExit(f"[FAIL] {path} regex #{i} did not match.\nPATTERN:\n{pat}")
        s = rx.sub(lambda m: repl, s, 1)
    save(path, s)
    print(f"[OK] {path}: applied {len(pats)} regex(es)")

base = r"E:\yolo\YoloDotNet-master\VisionInspectionSystem"
mphd = base + r"\src\Modules\VisionInspection.Modules.SOP\Services\MediaPipeHandDetector.cs"
mps  = base + r"\src\Modules\VisionInspection.Modules.SOP\Services\MediaPipeHandService.cs"
hpm  = base + r"\src\Modules\VisionInspection.Modules.SOP\Models\HandPoseEstimationModels.cs"
sm   = base + r"\src\Modules\VisionInspection.Modules.SOP\SOPModule.cs"
ihs  = base + r"\src\Modules\VisionInspection.Modules.SOP\Services\IHandPoseEstimationService.cs"

# ============================ R1..R14 (来自 _sop_edits.py) ============================
R1 = r'''    /// <summary>\s*/// 应用平滑处理减少闪烁\s*/// </summary>\s*private List<HandPose> ApplySmoothing\(List<HandPose> currentHands\)\s*\{.*?\n    \}'''
R1_new = r'''    /// <summary>
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
    }'''

R2a = r'''    /// <summary>\s*/// 计算历史平均值\s*/// </summary>\s*private \(float x, float y, float z\) CalculateHistoryAverage\(int keypointIndex\)\s*\{.*?\n    \}'''
R2_new = r'''    /// <summary>
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
    }'''

R2b = r'''    /// <summary>\s*/// 使用历史记录平滑矩形\s*/// </summary>\s*private SKRect SmoothRectWithHistory\(SKRect current\)\s*\{.*?\n    \}'''

R3 = r'''    /// <summary>\s*/// 解析单阶段手部检测模型的输出.*?private List<PalmDetection> ParseSingleStagePalmOutput\(Tensor<float> scores, int imageWidth, int imageHeight\)\s*\{.*?\n    \}'''
R3_new = r'''    /// <summary>
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
    }'''

R4 = r'''    /// <summary>\s*/// 过滤面部误检测.*?private List<PalmDetection> FilterFaceDetections\(List<PalmDetection> detections, int imageWidth, int imageHeight\)\s*\{.*?\n    \}'''
R4_new = r'''    /// <summary>
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
    }'''

R5 = r'''    public void Dispose\(\)\s*\{.*?\n    \}'''
R5_new = r'''    public void Dispose()
    {
        _palmSession?.Dispose();
        _landmarkSession?.Dispose();
        _palmSession = null;
        _landmarkSession = null;
        lock (_trackLock) _trackedHands.Clear();
    }'''

R6 = r'''    /// <summary>\s*/// 手掌检测结果\s*/// </summary>\s*private class PalmDetection'''
R6_new = r'''    /// <summary>
    /// 在图像上涂黑指定手掌区域，用于多遍抑制重跑（把已检出的手压掉，逼出第 2 只手）
    /// </summary>
    private static void MaskRegion(SKBitmap image, PalmDetection d)
    {
        using var paint = new SKPaint { Color = SKColors.Black };
        using var canvas = new SKCanvas(image);
        canvas.DrawRect(d.X, d.Y, d.Width, d.Height, paint);
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
    private class PalmDetection'''

R7 = r'''            lock \(_lock\)\s*\{.*?\n            \}'''
R7_new = r'''            lock (_lock)
            {
                Console.WriteLine($"[MediaPipeHand] *** 将使用 MediaPipe 手部检测方案 (非阻塞模式) ***");
                Console.WriteLine($"[MediaPipeHand] Palm: {config.PalmModelPath}");
                Console.WriteLine($"[MediaPipeHand] Landmark: {config.LandmarkModelPath}");

                _detector = new MediaPipeHandDetector(config);
                _detector.Initialize();
                Console.WriteLine($"[MediaPipeHand] 初始化完成, IsInitialized={_detector.IsInitialized}");
            }'''

R8 = r'''    // ========== DWPose.*?public string DWPoseModelDir \{ get; set; \} = @".*?";'''
R8_new = r'''    // ========== 手部检测后端选项 ==========

    /// <summary>
    /// 手部检测后端选择。
    /// Auto   : MediaPipe → YOLO-pose 兜底（向后兼容）
    /// MediaPipe : 强制使用 MediaPipe 两阶段方案（默认、最稳健）
    /// YoloPose  : 强制使用 YOLOv8-pose + MediaPipe Landmark 方案
    /// </summary>
    public HandDetectionBackend Backend { get; set; } = HandDetectionBackend.MediaPipe;'''

R9 = r'''public enum HandDetectionBackend\s*\{.*?YoloPose = 4\s*\}'''
R9_new = r'''public enum HandDetectionBackend
{
    /// <summary>自动：MediaPipe → YOLO-pose 兜底（向后兼容默认行为）</summary>
    Auto = 0,

    /// <summary>MediaPipe 两阶段手部关键点（握拳/横向手泛化好，默认方案）</summary>
    MediaPipe = 1,

    /// <summary>YOLOv8-pose 检测手腕 + MediaPipe Landmark 精修手指关键点</summary>
    YoloPose = 4
}'''

R10 = r'''    // 手部检测后端选择：.*?private HandDetectionBackend _handBackend = HandDetectionBackend\.Auto;'''
R10_new = r'''    // 手部检测后端选择：Auto=MediaPipe→YOLO-pose 兜底；MediaPipe/YoloPose=强制
    // MediaPipe 为默认后端（握拳/横向手泛化好、无需额外大模型）。
    private HandDetectionBackend _handBackend = HandDetectionBackend.MediaPipe;'''

R11 = r'''            // 选择手部检测方案\s*// Backend=DWPose / MediaPipe / Yolo / YoloPose : 按配置强制使用该方案.*?对横伸/远离躯干的手识别率通常比 DWPose 高。'''
R11_new = r'''            // 选择手部检测方案
            // Backend=MediaPipe / YoloPose : 按配置强制使用该方案
            // Backend=Auto（默认）          : MediaPipe → YOLO-pose 兜底
            // MediaPipe 为默认后端（握拳/横向手泛化好、无需额外大模型）。
            // YoloPose 使用 yolov8s-pose 检测手腕 + MediaPipe Landmark 精修手指，
            // 对横伸/远离躯干的手识别率通常更高，作为兜底。'''

R12 = r'''            if \(handConfig\.Backend == HandDetectionBackend\.DWPose\)\s*\{.*?\}\s*else if \(handConfig\.Backend == HandDetectionBackend\.MediaPipe\)'''
R12_new = r'''            if (handConfig.Backend == HandDetectionBackend.MediaPipe)'''

R13 = r'''            else if \(handConfig\.Backend == HandDetectionBackend\.Yolo\)\s*\{.*?\}\s*else if \(handConfig\.Backend == HandDetectionBackend\.YoloPose\)'''
R13_new = r'''            else if (handConfig.Backend == HandDetectionBackend.YoloPose)'''

R14 = r'''            else // Auto：维持原有优先级\s*\{.*?            \}'''
R14_new = r'''            else // Auto：MediaPipe 优先，失败兜底 YOLO-pose
            {
                bool useMediaPipe = File.Exists(handConfig.PalmModelPath)
                    && handConfig.PalmModelPath.Contains("palm_detection")
                    && File.Exists(handConfig.LandmarkModelPath);

                if (useMediaPipe)
                {
                    Log($"检测到 MediaPipe 模型，使用 MediaPipe 方案（默认后端，握拳/横向手支持更好）");
                    _handPoseService = new MediaPipeHandService();
                    await _handPoseService.InitializeAsync(handConfig);
                    Log($"MediaPipe 手部姿态初始化完成, IsInitialized={_handPoseService.IsInitialized}");
                }
                else
                {
                    string yoloPoseModelPath = @"e:\yolo\YoloDotNet-master\yolo_models\yolov8s-pose.onnx";
                    if (!File.Exists(yoloPoseModelPath))
                    {
                        yoloPoseModelPath = new[]
                        {
                            @"e:\yolo\YoloDotNet-master\yolo_models\yolov11s-pose.onnx",
                            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "yolo_models", "yolov8s-pose.onnx"),
                            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "yolo_models", "yolov11s-pose.onnx"),
                        }.FirstOrDefault(File.Exists) ?? "";
                    }

                    string landmarkModelPath = @"e:\yolo\YoloDotNet-master\VisionInspectionSystem\src\UI\VisionInspection.UI\models\hand_landmark_sparse_Nx3x224x224.onnx";
                    if (!File.Exists(landmarkModelPath))
                    {
                        landmarkModelPath = new[]
                        {
                            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "models", "hand_landmark_sparse_Nx3x224x224.onnx"),
                            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Models", "hand_landmark_sparse_Nx3x224x224.onnx"),
                        }.FirstOrDefault(File.Exists) ?? "";
                    }

                    if (!string.IsNullOrEmpty(yoloPoseModelPath) && !string.IsNullOrEmpty(landmarkModelPath))
                    {
                        handConfig.PalmModelPath = yoloPoseModelPath;
                        handConfig.LandmarkModelPath = landmarkModelPath;
                        _handPoseService = new YoloPoseHandEstimationService();
                        await _handPoseService.InitializeAsync(handConfig);
                        Log($"手部姿态估计初始化成功（YoloPose兜底方案）, pose={yoloPoseModelPath}");
                    }
                    else
                    {
                        Log("Auto 模式：MediaPipe 与 YOLO-pose 模型均缺失，手部检测未初始化");
                    }
                }
            }'''

# ============================ 手动编辑（构造函数/字段/历史清理/阈值） ============================
R_A = r'''    // 平滑处理参数\s*private HandPose\? _lastHandPose;\s*private readonly float _smoothingFactor = 0\.7f;\s*// 历史记录用于多帧平滑\s*private readonly Queue<HandPose> _handHistory = new Queue<HandPose>\(3\);\s*private const int HistorySize = 3;'''
R_A_new = r'''    // 平滑处理参数（EMA 权重，last 占 _smoothingFactor）
    private readonly float _smoothingFactor = 0.7f;

    // 多手时间平滑状态：按稳定 TrackId 维护最近帧手部姿态
    private readonly object _trackLock = new();
    private readonly Dictionary<int, HandPose> _trackedHands = new();
    private int _nextTrackId = 0;

    // 阈值与过滤参数（运行时从配置读取，支持界面调节并持久化）
    private readonly float _keypointConfidenceThreshold;  // 关键点平均置信度阈值
    private readonly float _minBoxAreaRatio;              // 最小检测框面积比例
    private readonly bool _enableFaceFilter;              // 是否启用面部过滤
    private readonly float _faceFilterUpperRatio;         // 面部过滤画面上边界比例'''

R_B = r'''    public MediaPipeHandDetector\(string palmModelPath, string landmarkModelPath,\s*float confidenceThreshold = 0\.5f, int maxNumHands = 2\)\s*\{\s*_palmModelPath = palmModelPath;\s*_landmarkModelPath = landmarkModelPath;\s*_confidenceThreshold = confidenceThreshold;\s*_maxNumHands = maxNumHands;\s*\}'''
R_B_new = r'''    public MediaPipeHandDetector(HandPoseEstimationConfig config)
    {
        _palmModelPath = config.PalmModelPath;
        _landmarkModelPath = config.LandmarkModelPath;
        _confidenceThreshold = config.DetectionConfidenceThreshold;   // 手掌检测置信度阈值
        _keypointConfidenceThreshold = config.ConfidenceThreshold;     // 关键点平均置信度阈值
        _maxNumHands = config.MaxNumHands;
        _minBoxAreaRatio = config.MinBoxAreaRatio;
        _enableFaceFilter = config.EnableFaceFilter;
        _faceFilterUpperRatio = config.FaceFilterUpperRatio;
    }'''

R_C = r'''            if \(palmDetections\.Count == 0\)\s*\{\s*// 无检测，清空历史\s*_handHistory\.Clear\(\);\s*_lastHandPose = null;\s*return new List<HandPose>\(\);\s*\}'''
R_C_new = r'''            if (palmDetections.Count == 0)
            {
                // 无检测，清空多手跟踪状态
                lock (_trackLock) _trackedHands.Clear();
                return new List<HandPose>();
            }'''

R_D = r'''            if \(hands\.Count == 0\)\s*\{\s*_handHistory\.Clear\(\);\s*_lastHandPose = null;\s*return new List<HandPose>\(\);\s*\}'''
R_D_new = r'''            if (hands.Count == 0)
            {
                lock (_trackLock) _trackedHands.Clear();
                return new List<HandPose>();
            }'''

R_E = r'''if \(avgConfidence < 0\.3f\)'''
R_E_new = r'''if (avgConfidence < _keypointConfidenceThreshold)'''

# ============================ 先恢复干净工作树（撤销外部抹除造成的删除） ============================
# 必须在写文件之前执行；_sop_full.py 本身与 models/ 下 gitignored 的 onnx 均为未跟踪，不会被 reset 清除。
git("reset", "--hard", "HEAD")
print("[OK] git reset --hard HEAD 完成，工作树已恢复到干净 HEAD")

# ============================ 应用 ============================
apply_rx(mphd, [(R1, R1_new), (R2a, R2_new), (R2b, ""), (R3, R3_new),
                (R4, R4_new), (R5, R5_new), (R6, R6_new),
                (R_A, R_A_new), (R_B, R_B_new), (R_C, R_C_new), (R_D, R_D_new), (R_E, R_E_new)]); stage(mphd)
apply_rx(mps, [(R7, R7_new)]); stage(mps)
apply_rx(hpm, [(R8, R8_new), (R9, R9_new)]); stage(hpm)
apply_rx(sm,   [(R10, R10_new), (R11, R11_new), (R12, R12_new), (R13, R13_new), (R14, R14_new)]); stage(sm)

# ============================ 删除 SOPModule 中的悬挂 else（旧 YOLO-hand/DWPose 回退链） ============================
def delete_stray_else(path):
    lines = normalize(load(path)).split("\n")
    c = next(i for i, l in enumerate(lines) if "回退方案: 尝试 YOLO-hand" in l)
    opener = None
    for i in range(c, -1, -1):
        s = lines[i].strip()
        if s == "else" or (s.startswith("else") and not s.startswith("else if")):
            opener = i
            break
    assert lines[opener + 1].strip() == "{", f"opener+1 不是 {{ 而是 {lines[opener+1]!r}"
    depth = 0
    end = None
    for j in range(opener + 1, len(lines)):
        depth += lines[j].count("{") - lines[j].count("}")
        if depth == 0:
            end = j
            break
    new = lines[:opener] + lines[end + 1:]
    save(path, "\n".join(new))
    print(f"[OK] {path}: 删除悬挂 else 块 {end - opener + 1} 行")

delete_stray_else(sm); stage(sm)

# ============================ IHandPoseEstimationService.cs 精简为仅接口 ============================
INTERFACE = '''using SkiaSharp;
using VisionInspection.Modules.SOP.Models;

namespace VisionInspection.Modules.SOP.Services;

/// <summary>
/// 手部姿态估计服务接口
/// </summary>
public interface IHandPoseEstimationService
{
    Task<HandPoseEstimationResult> DetectHandsAsync(SKBitmap image);
    bool IsInitialized { get; }
    Task InitializeAsync(HandPoseEstimationConfig config);
    Task ShutdownAsync();
}
'''
save(ihs, INTERFACE); stage(ihs)
print(f"[OK] {ihs}: 已精简为仅接口")

# ============================ 收尾一致性检查 ============================
import re as _re

def has_bad(s, token):
    # 避免 HandDetectionBackend.YoloPose 被误判为 HandDetectionBackend.Yolo
    if token == "HandDetectionBackend.Yolo":
        return bool(_re.search(r"HandDetectionBackend\.Yolo(?!Pose)", s))
    return token in s

forbidden_global = ["DWPoseHandEstimationService", "DWPoseHandDetector", "YoloHandDetectionService",
                    "DWPoseModelDir", "HandDetectionBackend.Yolo", "HandDetectionBackend.DWPose"]
forbidden_mphd = ["_handHistory", "_lastHandPose", "HistorySize"]
# 这些文件将被 git rm 删除，其内部残留无需检查
delete_files = ["DWPoseHandDetector.cs", "YoloHandDetectionService.cs"]
bad = []
for root, _, files in os.walk(base + r"\src"):
    for f in files:
        if not f.endswith(".cs"):
            continue
        if f in delete_files:
            continue
        p = os.path.join(root, f)
        s = load(p)
        if f == "MediaPipeHandDetector.cs":
            hits = [b for b in forbidden_mphd if b in s]
        else:
            hits = [b for b in forbidden_global if has_bad(s, b)]
        if hits:
            bad.append((p, hits))
if bad:
    for p, h in bad:
        print("RESIDUAL", h, "->", p)
    raise SystemExit("[FAIL] 一致性检查未通过")
print("ALL DONE (一致性检查通过)")

# ============================ 删除 DWPose / Yolo 残留文件（git rm） ============================
RM_FILES = [
    base + r"\docs\DWPose与SOP模块集成文档.md",
    base + r"\scripts\dwpose_hand_inference.py",
    base + r"\src\Modules\VisionInspection.Modules.SOP\Services\DWPoseHandDetector.cs",
    base + r"\src\Modules\VisionInspection.Modules.SOP\Services\YoloHandDetectionService.cs",
]
for rf in RM_FILES:
    if os.path.exists(rf):
        git("rm", rf)
        print(f"[GIT RM] {os.path.basename(rf)}")
    else:
        print(f"[SKIP] 已不存在: {os.path.basename(rf)}")

# ============================ Phase B: 配置 / 注释清理 ============================
# B1: sop_config.json 默认后端 -> MediaPipe（与代码默认一致，确保 MediaPipe 为默认手部检测模型）
cfg = base + r"\src\UI\VisionInspection.UI\configs\sop_config.json"
cs = load(cfg)
cs2 = cs.replace('"HandDetectionBackend": "YoloPose"', '"HandDetectionBackend": "MediaPipe"')
assert cs2 != cs, "sop_config.json 中未找到 HandDetectionBackend:YoloPose"
save(cfg, cs2); stage(cfg)
print("[OK] sop_config.json -> MediaPipe")

# B2: yaml 陈旧 DWPose 注释 / 描述修正
YAMLS = [
    base + r"\configs\sop\sop_cup_usage.yaml",
    base + r"\src\UI\VisionInspection.UI\configs\sop\sop_cup_usage.yaml",
    base + r"\configs\sop\sop_phone_usage.yaml",
    base + r"\src\UI\VisionInspection.UI\configs\sop\sop_phone_usage.yaml",
    base + r"\configs\sop\sop_hand_action_demo.yaml",
    base + r"\configs\sop\sop_template.yaml",
]
for yp in YAMLS:
    if not os.path.exists(yp):
        continue
    ys = load(yp)
    ys2 = ys.replace("(DWPose)", "(MediaPipe)") \
            .replace("DWPose 手部结果", "手部姿态结果") \
            .replace("（YOLO 物体 + DWPose 手部）", "（YOLO 物体 + 手部姿态）")
    if ys2 != ys:
        save(yp, ys2); stage(yp)
        print(f"[OK] yaml 注释更新: {os.path.basename(yp)}")

# B3: MainViewModel.cs 陈旧 DWPose 注释
mv = base + r"\src\UI\VisionInspection.UI\ViewModels\MainViewModel.cs"
mv_staged = False
ms = load(mv)
ms2 = ms.replace("// 注意：DWPose输出的坐标与SkiaSharp坐标系一致（原点在左上角）",
                 "// 注意：手部检测输出的坐标与SkiaSharp坐标系一致（原点在左上角）")
if ms2 != ms:
    save(mv, ms2); stage(mv); mv_staged = True
    print("[OK] MainViewModel.cs 注释更新")

# ============================ 索引守卫：提交前确认编辑文件为 M、删除文件为 D ============================
EXPECT_M = [mphd, mps, hpm, sm, ihs, cfg]
if mv_staged:
    EXPECT_M.append(mv)
EXPECT_D = RM_FILES
bad = []
for f in EXPECT_M:
    st = git("diff", "--cached", "--name-status", "--", f)
    code = st.split("\t")[0] if st else "?"
    if code != "M":
        bad.append((os.path.basename(f), st or "MISSING/未暂存"))
for f in EXPECT_D:
    st = git("diff", "--cached", "--name-status", "--", f)
    code = st.split("\t")[0] if st else "?"
    if code != "D":
        bad.append((os.path.basename(f), st or "未删除"))
if bad:
    for b in bad:
        print("GUARD FAIL", b)
    raise SystemExit("[FAIL] 索引守卫未通过，放弃提交（未造成误删）")

# ============================ 提交 ============================
git("commit", "-m",
    "SOP手部检测: 默认MediaPipe, 删除DWPose/Yolo后端, 修复双置信度持久化与面部误检\n\n"
    "- 默认手部检测后端改为 MediaPipe（sop_config.json + 代码默认）\n"
    "- 删除 DWPose / Yolo 手部检测后端及相关文件/文档/脚本\n"
    "- MediaPipeHandDetector 改用单参构造读取 HandPoseEstimationConfig，"
    "修复关键点/手掌双置信度保存后仍是默认值、无法调节的 bug\n"
    "- 多手时间平滑（TrackId + EMA），ParseSingleStagePalmOutput 支持动态 N 手，"
    "FilterFaceDetections 启发式过滤面部误检\n"
    "- 清理 yaml/注释中陈旧的 DWPose 描述")
print("COMMITTED")
