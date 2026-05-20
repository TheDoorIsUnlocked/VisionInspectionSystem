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

    public bool IsValid => Confidence >= 0.5f;

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
    /// 模型路径
    /// </summary>
    public string ModelPath { get; set; } = "";

    /// <summary>
    /// 置信度阈值
    /// </summary>
    public float ConfidenceThreshold { get; set; } = 0.5f;

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
