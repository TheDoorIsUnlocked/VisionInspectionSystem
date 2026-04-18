using SkiaSharp;

namespace VisionInspection.Modules.SOP.Models;

/// <summary>
/// 人体姿态关键点定义（COCO格式 - 17个关键点）
/// </summary>
public enum KeypointType
{
    Nose = 0,
    LeftEye = 1,
    RightEye = 2,
    LeftEar = 3,
    RightEar = 4,
    LeftShoulder = 5,
    RightShoulder = 6,
    LeftElbow = 7,
    RightElbow = 8,
    LeftWrist = 9,
    RightWrist = 10,
    LeftHip = 11,
    RightHip = 12,
    LeftKnee = 13,
    RightKnee = 14,
    LeftAnkle = 15,
    RightAnkle = 16
}

/// <summary>
/// 单个关键点
/// </summary>
public class Keypoint
{
    public KeypointType Type { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Confidence { get; set; }

    public bool IsValid => Confidence >= 0.5f;

    public Keypoint(KeypointType type, float x, float y, float confidence)
    {
        Type = type;
        X = x;
        Y = y;
        Confidence = confidence;
    }
}

/// <summary>
/// 人体姿态（17个关键点）
/// </summary>
public class HumanPose
{
    public int TrackId { get; set; }
    public SKRect BoundingBox { get; set; }
    public List<Keypoint> Keypoints { get; set; } = new();
    public DateTime Timestamp { get; set; }

    /// <summary>
    /// 获取指定类型的关键点
    /// </summary>
    public Keypoint? GetKeypoint(KeypointType type)
    {
        return Keypoints.FirstOrDefault(k => k.Type == type);
    }

    /// <summary>
    /// 获取左手腕
    /// </summary>
    public Keypoint? LeftWrist => GetKeypoint(KeypointType.LeftWrist);

    /// <summary>
    /// 获取右手腕
    /// </summary>
    public Keypoint? RightWrist => GetKeypoint(KeypointType.RightWrist);

    /// <summary>
    /// 获取鼻子
    /// </summary>
    public Keypoint? Nose => GetKeypoint(KeypointType.Nose);

    /// <summary>
    /// 获取左耳
    /// </summary>
    public Keypoint? LeftEar => GetKeypoint(KeypointType.LeftEar);

    /// <summary>
    /// 获取右耳
    /// </summary>
    public Keypoint? RightEar => GetKeypoint(KeypointType.RightEar);

    /// <summary>
    /// 获取左肩
    /// </summary>
    public Keypoint? LeftShoulder => GetKeypoint(KeypointType.LeftShoulder);

    /// <summary>
    /// 获取右肩
    /// </summary>
    public Keypoint? RightShoulder => GetKeypoint(KeypointType.RightShoulder);

    /// <summary>
    /// 获取左肘
    /// </summary>
    public Keypoint? LeftElbow => GetKeypoint(KeypointType.LeftElbow);

    /// <summary>
    /// 获取右肘
    /// </summary>
    public Keypoint? RightElbow => GetKeypoint(KeypointType.RightElbow);

    /// <summary>
    /// 获取左髋
    /// </summary>
    public Keypoint? LeftHip => GetKeypoint(KeypointType.LeftHip);

    /// <summary>
    /// 获取右髋
    /// </summary>
    public Keypoint? RightHip => GetKeypoint(KeypointType.RightHip);

    /// <summary>
    /// 判断关键点是否在指定区域内
    /// </summary>
    public bool IsKeypointInRegion(KeypointType type, SKRect region)
    {
        var kp = GetKeypoint(type);
        if (kp == null || !kp.IsValid) return false;

        return kp.X >= region.Left && kp.X <= region.Right &&
               kp.Y >= region.Top && kp.Y <= region.Bottom;
    }

    /// <summary>
    /// 判断手腕是否在区域内（指定左右手）
    /// </summary>
    public bool IsHandInRegion(string handSide, SKRect region)
    {
        var handType = handSide.ToLower() switch
        {
            "left" or "左手" => KeypointType.LeftWrist,
            "right" or "右手" => KeypointType.RightWrist,
            _ => KeypointType.RightWrist
        };
        return IsKeypointInRegion(handType, region);
    }

    /// <summary>
    /// 判断头部是否朝向某个方向
    /// </summary>
    public bool IsHeadFacing(string direction)
    {
        var nose = Nose;
        var leftEar = LeftEar;
        var rightEar = RightEar;

        if (nose == null || !nose.IsValid) return false;
        if (leftEar == null || rightEar == null) return false;
        if (!leftEar.IsValid || !rightEar.IsValid) return false;

        return direction.ToLower() switch
        {
            "front" or "正面" => Math.Abs(leftEar.Y - rightEar.Y) < 10,
            "left" or "左" => leftEar.X < nose.X,
            "right" or "右" => rightEar.X > nose.X,
            _ => false
        };
    }

    /// <summary>
    /// 判断双手是否都在工作区域内
    /// </summary>
    public bool AreBothHandsInRegion(SKRect region)
    {
        return IsKeypointInRegion(KeypointType.LeftWrist, region) &&
               IsKeypointInRegion(KeypointType.RightWrist, region);
    }

    /// <summary>
    /// 判断是否有手在区域内
    /// </summary>
    public bool IsAnyHandInRegion(SKRect region)
    {
        return IsKeypointInRegion(KeypointType.LeftWrist, region) ||
               IsKeypointInRegion(KeypointType.RightWrist, region);
    }

    /// <summary>
    /// 计算两个关键点之间的距离
    /// </summary>
    public float GetDistance(KeypointType type1, KeypointType type2)
    {
        var kp1 = GetKeypoint(type1);
        var kp2 = GetKeypoint(type2);

        if (kp1 == null || kp2 == null) return float.MaxValue;
        if (!kp1.IsValid || !kp2.IsValid) return float.MaxValue;

        var dx = kp1.X - kp2.X;
        var dy = kp1.Y - kp2.Y;
        return (float)Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>
    /// 判断手臂是否伸展（用于检测拿取动作）
    /// </summary>
    public bool IsArmExtended(string handSide)
    {
        var (shoulder, elbow, wrist) = handSide.ToLower() switch
        {
            "left" or "左手" => (LeftShoulder, LeftElbow, LeftWrist),
            "right" or "右手" => (RightShoulder, RightElbow, RightWrist),
            _ => (RightShoulder, RightElbow, RightWrist)
        };

        if (shoulder == null || elbow == null || wrist == null) return false;
        if (!shoulder.IsValid || !elbow.IsValid || !wrist.IsValid) return false;

        // 计算肩-肘-腕的角度，接近180度表示手臂伸展
        var angle = CalculateAngle(shoulder, elbow, wrist);
        return angle > 150; // 大于150度认为手臂伸展
    }

    /// <summary>
    /// 计算三点形成的角度（中间点为顶点）
    /// </summary>
    private float CalculateAngle(Keypoint p1, Keypoint p2, Keypoint p3)
    {
        var dx1 = p1.X - p2.X;
        var dy1 = p1.Y - p2.Y;
        var dx2 = p3.X - p2.X;
        var dy2 = p3.Y - p2.Y;

        var dot = dx1 * dx2 + dy1 * dy2;
        var det = dx1 * dy2 - dy1 * dx2;

        var angle = (float)(Math.Atan2(det, dot) * 180 / Math.PI);
        return Math.Abs(angle);
    }
}

/// <summary>
/// 人体检测结果
/// </summary>
public class HumanDetection
{
    public int TrackId { get; set; }
    public SKRect BoundingBox { get; set; }
    public float Confidence { get; set; }
    public HumanPose? Pose { get; set; }
    public DateTime Timestamp { get; set; }
}

/// <summary>
/// 姿态检测配置
/// </summary>
public class PoseEstimationConfig
{
    public bool Enabled { get; set; } = false;
    public string ModelPath { get; set; } = "models/pose_estimation.onnx";
    public float ConfidenceThreshold { get; set; } = 0.5f;
    public int InputWidth { get; set; } = 640;
    public int InputHeight { get; set; } = 640;
}

/// <summary>
/// SOP检测模式
/// </summary>
public enum SOPDetectionMode
{
    /// <summary>
    /// 基于物体检测（当前实现）
    /// </summary>
    ObjectBased,

    /// <summary>
    /// 基于姿态估计（实战指南方案）
    /// </summary>
    PoseBased,

    /// <summary>
    /// 混合模式（物体+姿态）
    /// </summary>
    Hybrid
}
