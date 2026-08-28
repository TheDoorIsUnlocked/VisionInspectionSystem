# -*- coding: utf-8 -*-
import io, re, sys

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

# ---------------------------------------------------------------------------
# 1) MediaPipeHandDetector.cs
# ---------------------------------------------------------------------------
mphd = base + r"\src\Modules\VisionInspection.Modules.SOP\Services\MediaPipeHandDetector.cs"

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
                // 与已有 track 做中心点最近邻匹配
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

            // 清理本帧未匹配到的旧 track（避免无限增长）
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

        // 维度: [N, 8]，第一维 N 才是检测数量（不是 8！）
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

            // 模型输入为 192×192 正方形，w 在两个方向相同；乘 1.25 还原手掌实际尺寸
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

        // NMS 去重（IoU>0.5 合并重叠框，进一步抑制面部/重复误检）
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

            // 竖长形 → 整张脸（宽高比 0.55~0.9，在画面"上方"）
            bool tallFace = aspectRatio >= 0.55f && aspectRatio <= 0.9f
                && centerY < imageHeight * upper;

            // 横宽形 → 面部局部（宽高比 > 1.5，在很上方，用更小的比例避免误伤手）
            bool wideFacePart = aspectRatio > 1.5f
                && centerY < imageHeight * Math.Min(upper, 0.28f);

            // 近似正方且很靠上（squareHigh）：宽高比 0.9~1.3 且中心在很靠上
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

apply_rx(mphd, [(R1, R1_new), (R2a, R2_new), (R2b, ""), (R3, R3_new), (R4, R4_new), (R5, R5_new), (R6, R6_new)])

# ---------------------------------------------------------------------------
# 2) MediaPipeHandService.cs
# ---------------------------------------------------------------------------
mps = base + r"\src\Modules\VisionInspection.Modules.SOP\Services\MediaPipeHandService.cs"

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

apply_rx(mps, [(R7, R7_new)])

# ---------------------------------------------------------------------------
# 3) HandPoseEstimationModels.cs
# ---------------------------------------------------------------------------
hpm = base + r"\src\Modules\VisionInspection.Modules.SOP\Models\HandPoseEstimationModels.cs"

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

apply_rx(hpm, [(R8, R8_new), (R9, R9_new)])

# ---------------------------------------------------------------------------
# 4) SOPModule.cs
# ---------------------------------------------------------------------------
sm = base + r"\src\Modules\VisionInspection.Modules.SOP\SOPModule.cs"

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
                    // 兜底方案: YOLO-pose(手腕) + MediaPipe Landmark 精修手指
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

apply_rx(sm, [(R10, R10_new), (R11, R11_new), (R12, R12_new), (R13, R13_new), (R14, R14_new)])

# ---------------------------------------------------------------------------
# Sanity checks
# ---------------------------------------------------------------------------
checks = [
    (mphd, ["DWPose", "YoloHandDetectionService", "DWPoseHandEstimationService", "HandDetectionBackend.Yolo", "HandDetectionBackend.DWPose", "_handHistory", "_lastHandPose", "HistorySize", "SmoothRectWithHistory", "CalculateHistoryAverage"]),
    (mps, ["new MediaPipeHandDetector(palmPath", "Math.Max(config.ConfidenceThreshold", "DWPose", "YoloHandDetectionService"]),
    (hpm, ["DWPoseDetModelPath", "DWPosePoseModelPath", "DWPoseModelDir", "HandDetectionBackend.DWPose", "HandDetectionBackend.Yolo"]),
    (sm, ["DWPoseHandEstimationService", "YoloHandDetectionService", "HandDetectionBackend.DWPose", "HandDetectionBackend.Yolo", "DWPoseModelDir"]),
]
for p, bad in checks:
    s = load(p)
    hits = [b for b in bad if b in s]
    if hits:
        raise SystemExit(f"[FAIL] {p} still contains: {hits}")
    print(f"[OK] {p}: removed-symbol check passed")

print("ALL DONE")
