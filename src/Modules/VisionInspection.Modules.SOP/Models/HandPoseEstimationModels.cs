using SkiaSharp;

namespace VisionInspection.Modules.SOP.Models;

/// <summary>
/// 手部关键点定义（MediaPipe格式 - 21个关键点）
/// </summary>
public enum HandKeypointType
{
    // 手腕
    Wrist = 0,

    // 拇指 (1-4)
    ThumbCMC = 1,    // 拇指掌指关节
    ThumbMCP = 2,    // 拇指近端指间关节
    ThumbIP = 3,     // 拇指远端指间关节
    ThumbTip = 4,    // 拇指指尖

    // 食指 (5-8)
    IndexFingerMCP = 5,   // 食指掌指关节
    IndexFingerPIP = 6,   // 食指近端指间关节
    IndexFingerDIP = 7,   // 食指远端指间关节
    IndexFingerTip = 8,   // 食指指尖

    // 中指 (9-12)
    MiddleFingerMCP = 9,  // 中指掌指关节
    MiddleFingerPIP = 10, // 中指近端指间关节
    MiddleFingerDIP = 11, // 中指远端指间关节
    MiddleFingerTip = 12, // 中指指尖

    // 无名指 (13-16)
    RingFingerMCP = 13,   // 无名指掌指关节
    RingFingerPIP = 14,   // 无名指近端指间关节
    RingFingerDIP = 15,   // 无名指远端指间关节
    RingFingerTip = 16,   // 无名指指尖

    // 小指 (17-20)
    PinkyMCP = 17,        // 小指掌指关节
    PinkyPIP = 18,        // 小指近端指间关节
    PinkyDIP = 19,        // 小指远端指间关节
    PinkyTip = 20         // 小指指尖
}

/// <summary>
/// 单个手部关键点
/// </summary>
public class HandKeypoint
{
    public HandKeypointType Type { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }  // MediaPipe提供3D坐标
    public float Confidence { get; set; }

    public bool IsValid => Confidence >= 0.05f;  // 降低阈值以适应DWPose的低置信度输出

    public HandKeypoint(HandKeypointType type, float x, float y, float z, float confidence)
    {
        Type = type;
        X = x;
        Y = y;
        Z = z;
        Confidence = confidence;
    }

    public SKPoint ToSKPoint() => new SKPoint(X, Y);
}

/// <summary>
/// 手部姿态（21个关键点）
/// </summary>
public class HandPose
{
    public int TrackId { get; set; }
    public HandType HandType { get; set; }  // 左手或右手
    public SKRect BoundingBox { get; set; }
    public List<HandKeypoint> Keypoints { get; set; } = new();
    public DateTime Timestamp { get; set; }

    /// <summary>
    /// 获取指定类型的关键点
    /// </summary>
    public HandKeypoint? GetKeypoint(HandKeypointType type)
    {
        return Keypoints.FirstOrDefault(k => k.Type == type);
    }

    /// <summary>
    /// 获取手腕位置
    /// </summary>
    public HandKeypoint? Wrist => GetKeypoint(HandKeypointType.Wrist);

    /// <summary>
    /// 获取指尖关键点
    /// </summary>
    public IEnumerable<HandKeypoint> FingerTips => new[]
    {
        GetKeypoint(HandKeypointType.ThumbTip),
        GetKeypoint(HandKeypointType.IndexFingerTip),
        GetKeypoint(HandKeypointType.MiddleFingerTip),
        GetKeypoint(HandKeypointType.RingFingerTip),
        GetKeypoint(HandKeypointType.PinkyTip)
    }.Where(k => k != null).Cast<HandKeypoint>();

    /// <summary>
    /// 计算手部中心点
    /// </summary>
    public SKPoint GetCenter()
    {
        if (Keypoints.Count == 0) return new SKPoint(0, 0);

        float avgX = Keypoints.Average(k => k.X);
        float avgY = Keypoints.Average(k => k.Y);
        return new SKPoint(avgX, avgY);
    }

    /// <summary>
    /// 判断是否为有效手势（至少有多少个关键点有效）
    /// </summary>
    public bool IsValidGesture(int minValidPoints = 15)
    {
        return Keypoints.Count(k => k.IsValid) >= minValidPoints;
    }
}

/// <summary>
/// 手部类型
/// </summary>
public enum HandType
{
    Unknown = 0,
    Left = 1,
    Right = 2
}

/// <summary>
/// 手部姿态估计结果
/// </summary>
public class HandPoseEstimationResult
{
    public List<HandPose> Hands { get; set; } = new();
    public DateTime Timestamp { get; set; }
    public long ProcessingTimeMs { get; set; }

    /// <summary>
    /// 获取左手
    /// </summary>
    public HandPose? LeftHand => Hands.FirstOrDefault(h => h.HandType == HandType.Left);

    /// <summary>
    /// 获取右手
    /// </summary>
    public HandPose? RightHand => Hands.FirstOrDefault(h => h.HandType == HandType.Right);
}

/// <summary>
/// 手部姿态估计配置
/// </summary>
public class HandPoseEstimationConfig
{
    /// <summary>
    /// 手掌检测模型路径（第一阶段）
    /// </summary>
    public string PalmModelPath { get; set; } = "";

    /// <summary>
    /// 关键点检测模型路径（第二阶段）
    /// </summary>
    public string LandmarkModelPath { get; set; } = "";

    /// <summary>
    /// 原始模型路径（目录或文件）
    /// </summary>
    public string RawModelPath { get; set; } = "";

    /// <summary>
    /// 模型路径（兼容旧版本，实际使用时会被拆解为Palm和Landmark路径）
    /// 支持两种格式：
    /// 1. 目录路径 - 自动在目录下查找 palm_detection 和 hand_landmark 模型
    /// 2. 文件路径 - 作为 PalmModelPath，并在同目录查找 Landmark 模型
    /// </summary>
    public string ModelPath
    {
        get => RawModelPath;  // 返回原始路径，而不是 PalmModelPath
        set
        {
            RawModelPath = value;

            if (string.IsNullOrEmpty(value)) return;

            // 情况1：如果是目录
            if (Directory.Exists(value))
            {
                PalmModelPath = Path.Combine(value, "palm_detection_full_Nx3x192x192_post.onnx");
                LandmarkModelPath = Path.Combine(value, "hand_landmark_sparse_Nx3x224x224.onnx");
            }
            // 情况2：如果是文件
            else if (File.Exists(value))
            {
                PalmModelPath = value;
                var dir = Path.GetDirectoryName(value);
                // 在同一目录查找 landmark 模型
                var landmarkPath = Path.Combine(dir ?? "", "hand_landmark_sparse_Nx3x224x224.onnx");
                LandmarkModelPath = File.Exists(landmarkPath) ? landmarkPath : "";
            }
            // 情况3：直接赋值（可能是不存在的路径）
            else
            {
                PalmModelPath = value;
            }
        }
    }

    /// <summary>
    /// 置信度阈值
    /// </summary>
    public float ConfidenceThreshold { get; set; } = 0.3f; // 降低阈值以减少闪烁

    /// <summary>
    /// 模型推理时的最低置信度。握拳/握物时模型输出置信度低，建议 0.08~0.15
    /// 提高至 0.20 以减少面部误检（面部误检通常置信度<0.3）
    /// </summary>
    public float DetectionConfidenceThreshold { get; set; } = 0.20f;

    /// <summary>
    /// 最小检测框面积比例（相对画面面积）。握拳时检测框更小，需降低此值
    /// 默认 0.0005（画面的 0.05%），例如 640x480 下最小约 153 像素（~12x12）
    /// </summary>
    public float MinBoxAreaRatio { get; set; } = 0.0005f;

    /// <summary>
    /// 是否启用旋转增强：原始方向检测不到手时，自动旋转画面90°再次检测
    /// 解决手指朝左/朝右时检测率极低的问题
    /// </summary>
    public bool RotationAugmentation { get; set; } = true;

    /// <summary>
    /// 最大检测手数
    /// </summary>
    public int MaxNumHands { get; set; } = 2;

    /// <summary>
    /// 是否检测左右手
    /// </summary>
    public bool DetectLeftHand { get; set; } = true;

    /// <summary>
    /// 是否检测右手
    /// </summary>
    public bool DetectRightHand { get; set; } = true;

    /// <summary>
    /// 是否使用GPU
    /// </summary>
    public bool UseGpu { get; set; } = true;

    // ========== 手部检测后端选项 ==========

    /// <summary>
    /// 手部检测后端选择。
    /// Auto   : MediaPipe → YOLO-pose 兜底（向后兼容）
    /// MediaPipe : 强制使用 MediaPipe 两阶段方案（默认、最稳健）
    /// YoloPose  : 强制使用 YOLOv8-pose + MediaPipe Landmark 方案
    /// </summary>
    public HandDetectionBackend Backend { get; set; } = HandDetectionBackend.MediaPipe;

    // ========== 面部过滤参数 ==========

    /// <summary>
    /// 是否启用面部过滤（将画面上的脸部/下巴/嘴巴排除，减少误检）
    /// </summary>
    public bool EnableFaceFilter { get; set; } = true;

    /// <summary>
    /// 面部过滤的画面高度比例：检测框中心Y在此比例以上视为"画面上方"
    /// 默认 0.38，值越大过滤范围越大（更激进），越小越保守
    /// </summary>
    public float FaceFilterUpperRatio { get; set; } = 0.38f;

    // ========== 手部结构验证参数 ==========

    /// <summary>
    /// 是否启用手部结构验证（检查手腕→指尖距离以区分真手和面部误检）
    /// 手指合拢/握拳场景如果被误过滤，可关闭此选项
    /// </summary>
    public bool EnableHandStructureCheck { get; set; } = true;

    /// <summary>
    /// 手部结构验证的阈值：手腕到最远指尖距离 / 检测框对角线
    /// 默认 0.18，值越小越宽松（握拳容易通过），值越大越严格（过滤更激进）
    /// 范围建议：0.10（极宽松）~ 0.30（极严格）
    /// </summary>
    public float HandStructureWristTipRatio { get; set; } = 0.18f;
}

/// <summary>
/// 手部骨架连接线定义（用于绘制骨架线）
/// </summary>
public static class HandSkeletonConnections
{
    /// <summary>
    /// 骨架连接线列表（每对表示一条线连接的两个关键点）
    /// </summary>
    public static readonly (HandKeypointType Start, HandKeypointType End)[] Connections = new[]
    {
        // 手腕到各手指根部
        (HandKeypointType.Wrist, HandKeypointType.ThumbCMC),
        (HandKeypointType.Wrist, HandKeypointType.IndexFingerMCP),
        (HandKeypointType.Wrist, HandKeypointType.MiddleFingerMCP),
        (HandKeypointType.Wrist, HandKeypointType.RingFingerMCP),
        (HandKeypointType.Wrist, HandKeypointType.PinkyMCP),

        // 拇指
        (HandKeypointType.ThumbCMC, HandKeypointType.ThumbMCP),
        (HandKeypointType.ThumbMCP, HandKeypointType.ThumbIP),
        (HandKeypointType.ThumbIP, HandKeypointType.ThumbTip),

        // 食指
        (HandKeypointType.IndexFingerMCP, HandKeypointType.IndexFingerPIP),
        (HandKeypointType.IndexFingerPIP, HandKeypointType.IndexFingerDIP),
        (HandKeypointType.IndexFingerDIP, HandKeypointType.IndexFingerTip),

        // 中指
        (HandKeypointType.MiddleFingerMCP, HandKeypointType.MiddleFingerPIP),
        (HandKeypointType.MiddleFingerPIP, HandKeypointType.MiddleFingerDIP),
        (HandKeypointType.MiddleFingerDIP, HandKeypointType.MiddleFingerTip),

        // 无名指
        (HandKeypointType.RingFingerMCP, HandKeypointType.RingFingerPIP),
        (HandKeypointType.RingFingerPIP, HandKeypointType.RingFingerDIP),
        (HandKeypointType.RingFingerDIP, HandKeypointType.RingFingerTip),

        // 小指
        (HandKeypointType.PinkyMCP, HandKeypointType.PinkyPIP),
        (HandKeypointType.PinkyPIP, HandKeypointType.PinkyDIP),
        (HandKeypointType.PinkyDIP, HandKeypointType.PinkyTip)
    };
}

/// <summary>
/// 手部检测后端枚举。
/// Auto=维持原有优先级(MediaPipe→YOLO-hand→YOLO-pose→DWPose)；其余为强制指定方案。
/// </summary>
public enum HandDetectionBackend
{
    /// <summary>自动：MediaPipe → YOLO-pose 兜底（向后兼容默认行为）</summary>
    Auto = 0,

    /// <summary>MediaPipe 两阶段手部关键点（握拳/横向手泛化好，默认方案）</summary>
    MediaPipe = 1,

    /// <summary>YOLOv8-pose 检测手腕 + MediaPipe Landmark 精修手指关键点</summary>
    YoloPose = 4
}
