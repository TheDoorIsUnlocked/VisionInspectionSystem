using SkiaSharp;
using VisionInspection.Modules.SOP.Models;
using YoloDotNet.Models;

namespace VisionInspection.Modules.SOP.Services;

/// <summary>
/// 基于姿态的步骤条件评估器
/// </summary>
public class PoseConditionEvaluator
{
    private readonly HumanTracker _tracker;
    private readonly Dictionary<string, SKRect> _regions;

    public PoseConditionEvaluator(Dictionary<string, SKRect>? regions = null)
    {
        _tracker = new HumanTracker();
        _regions = regions ?? new Dictionary<string, SKRect>();
    }

    /// <summary>
    /// 评估步骤条件
    /// </summary>
    public ConditionCheckResult EvaluateCondition(
        StepCondition condition,
        List<HumanPose> poses,
        SOPStep step)
    {
        // 更新跟踪器
        var trackedHumans = _tracker.Update(poses);

        if (trackedHumans.Count == 0)
        {
            return new ConditionCheckResult
            {
                IsMet = false,
                Message = "未检测到操作人员"
            };
        }

        // 使用第一个被跟踪的人进行评估
        var targetHuman = trackedHumans.First();

        return condition.Type switch
        {
            ConditionType.ObjectPresent => CheckHandInRegion(condition, targetHuman),
            ConditionType.ObjectInZone => CheckHandInSpecificRegion(condition, targetHuman),
            ConditionType.ObjectStable => CheckPoseStable(condition, targetHuman),
            ConditionType.ObjectAbsent => CheckHandNotInRegion(condition, targetHuman),
            ConditionType.TimeElapsed => CheckTimeElapsed(condition, step),
            ConditionType.SequenceComplete => CheckSequenceComplete(condition, step),
            _ => new ConditionCheckResult { IsMet = false, Message = $"未知的条件类型: {condition.Type}" }
        };
    }

    /// <summary>
    /// 检查手是否在区域内（对应"取料"等动作）
    /// </summary>
    private ConditionCheckResult CheckHandInRegion(StepCondition condition, TrackedHuman human)
    {
        var regionId = condition.Parameters.GetValueOrDefault("RegionId", "")?.ToString();
        var handSide = condition.Parameters.GetValueOrDefault("HandSide", "right")?.ToString() ?? "right";
        var minConfidence = condition.MinConfidence;

        if (string.IsNullOrEmpty(regionId) || !_regions.TryGetValue(regionId, out var region))
        {
            return new ConditionCheckResult
            {
                IsMet = false,
                Message = $"未找到区域定义: {regionId}"
            };
        }

        var pose = human.CurrentPose;
        var handType = handSide.ToLower() switch
        {
            "left" or "左手" => KeypointType.LeftWrist,
            _ => KeypointType.RightWrist
        };

        var hand = pose.GetKeypoint(handType);

        if (hand == null || hand.Confidence < minConfidence)
        {
            return new ConditionCheckResult
            {
                IsMet = false,
                Message = $"未检测到{handSide}手腕"
            };
        }

        var isInRegion = hand.X >= region.Left && hand.X <= region.Right &&
                        hand.Y >= region.Top && hand.Y <= region.Bottom;

        if (isInRegion)
        {
            return new ConditionCheckResult
            {
                IsMet = true,
                Message = $"{handSide}手已进入区域 '{regionId}'",
                MatchedObjects = new List<ObjectDetection>(),
                MatchedPoses = new List<HumanPose>()
            };
        }
        else
        {
            return new ConditionCheckResult
            {
                IsMet = false,
                Message = $"{handSide}手未进入区域 '{regionId}'"
            };
        }
    }

    /// <summary>
    /// 检查手是否在特定区域（用于精确定位）
    /// </summary>
    private ConditionCheckResult CheckHandInSpecificRegion(StepCondition condition, TrackedHuman human)
    {
        // 复用CheckHandInRegion逻辑
        return CheckHandInRegion(condition, human);
    }

    /// <summary>
    /// 检查姿态是否稳定（用于"放置"等动作确认）
    /// </summary>
    private ConditionCheckResult CheckPoseStable(StepCondition condition, TrackedHuman human)
    {
        var toleranceValue = condition.Parameters.GetValueOrDefault("Tolerance", 20f);
        float tolerance = toleranceValue is float f ? f : Convert.ToSingle(toleranceValue);

        var requiredFrames = condition.StableFrames;

        if (human.FrameCount < requiredFrames)
        {
            return new ConditionCheckResult
            {
                IsMet = false,
                Message = $"姿态尚未稳定（已跟踪 {human.FrameCount} 帧，需要 {requiredFrames} 帧）"
            };
        }

        // 检查最近几帧的姿态变化
        var recentPoses = human.History.TakeLast(requiredFrames).ToList();
        if (recentPoses.Count < 2)
        {
            return new ConditionCheckResult
            {
                IsMet = false,
                Message = "历史数据不足，无法判断稳定性"
            };
        }

        // 计算关键点位置变化
        var maxMovement = CalculateMaxMovement(recentPoses);

        if (maxMovement <= tolerance)
        {
            return new ConditionCheckResult
            {
                IsMet = true,
                Message = $"姿态已稳定（最大移动: {maxMovement:F1}px，容差: {tolerance}px）",
                MatchedObjects = new List<ObjectDetection>(),
                MatchedPoses = new List<HumanPose>()
            };
        }
        else
        {
            return new ConditionCheckResult
            {
                IsMet = false,
                Message = $"姿态未稳定（最大移动: {maxMovement:F1}px，容差: {tolerance}px）"
            };
        }
    }

    /// <summary>
    /// 计算多帧之间的最大关键点移动距离
    /// </summary>
    private float CalculateMaxMovement(List<HumanPose> poses)
    {
        float maxMovement = 0;

        for (int i = 1; i < poses.Count; i++)
        {
            var prevPose = poses[i - 1];
            var currPose = poses[i];

            foreach (var keypointType in Enum.GetValues<KeypointType>())
            {
                var prevKp = prevPose.GetKeypoint(keypointType);
                var currKp = currPose.GetKeypoint(keypointType);

                if (prevKp?.IsValid != true || currKp?.IsValid != true)
                    continue;

                var dx = prevKp.X - currKp.X;
                var dy = prevKp.Y - currKp.Y;
                var distance = (float)Math.Sqrt(dx * dx + dy * dy);

                maxMovement = Math.Max(maxMovement, distance);
            }
        }

        return maxMovement;
    }

    /// <summary>
    /// 检查手是否不在区域内（用于"离开"动作）
    /// </summary>
    private ConditionCheckResult CheckHandNotInRegion(StepCondition condition, TrackedHuman human)
    {
        var regionId = condition.Parameters.GetValueOrDefault("RegionId", "")?.ToString();
        var handSide = condition.Parameters.GetValueOrDefault("HandSide", "right")?.ToString() ?? "right";

        if (string.IsNullOrEmpty(regionId) || !_regions.TryGetValue(regionId, out var region))
        {
            return new ConditionCheckResult
            {
                IsMet = true, // 区域未定义，认为条件满足
                Message = $"区域 '{regionId}' 未定义，跳过检查"
            };
        }

        var pose = human.CurrentPose;
        var handType = handSide.ToLower() switch
        {
            "left" or "左手" => KeypointType.LeftWrist,
            _ => KeypointType.RightWrist
        };

        var hand = pose.GetKeypoint(handType);

        if (hand == null || !hand.IsValid)
        {
            return new ConditionCheckResult
            {
                IsMet = true, // 检测不到手，认为已离开
                Message = $"未检测到{handSide}手，认为已离开区域"
            };
        }

        var isInRegion = hand.X >= region.Left && hand.X <= region.Right &&
                        hand.Y >= region.Top && hand.Y <= region.Bottom;

        if (!isInRegion)
        {
            return new ConditionCheckResult
            {
                IsMet = true,
                Message = $"{handSide}手已离开区域 '{regionId}'",
                MatchedObjects = new List<ObjectDetection>(),
                MatchedPoses = new List<HumanPose>()
            };
        }
        else
        {
            return new ConditionCheckResult
            {
                IsMet = false,
                Message = $"{handSide}手仍在区域 '{regionId}' 内"
            };
        }
    }

    /// <summary>
    /// 检查时间是否满足
    /// </summary>
    private ConditionCheckResult CheckTimeElapsed(StepCondition condition, SOPStep step)
    {
        // 这里需要访问步骤开始时间，暂时返回成功
        // 实际实现中应该通过状态机获取
        return new ConditionCheckResult
        {
            IsMet = true,
            Message = "时间条件检查（需要状态机支持）"
        };
    }

    /// <summary>
    /// 检查前置序列是否完成
    /// </summary>
    private ConditionCheckResult CheckSequenceComplete(StepCondition condition, SOPStep step)
    {
        // 这里需要访问步骤历史，暂时返回成功
        // 实际实现中应该通过状态机获取
        return new ConditionCheckResult
        {
            IsMet = true,
            Message = "序列条件检查（需要状态机支持）"
        };
    }

    /// <summary>
    /// 添加区域定义
    /// </summary>
    public void AddRegion(string regionId, SKRect region)
    {
        _regions[regionId] = region;
    }

    /// <summary>
    /// 获取所有跟踪的人体
    /// </summary>
    public List<TrackedHuman> GetTrackedHumans()
    {
        return _tracker.Update(new List<HumanPose>());
    }

    /// <summary>
    /// 清除所有跟踪数据
    /// </summary>
    public void Clear()
    {
        _tracker.Clear();
    }
}

/// <summary>
/// 姿态违规检测器
/// </summary>
public class PoseViolationDetector
{
    private readonly HumanTracker _tracker;
    private readonly Dictionary<string, SKRect> _forbiddenZones;

    public PoseViolationDetector(Dictionary<string, SKRect>? forbiddenZones = null)
    {
        _tracker = new HumanTracker();
        _forbiddenZones = forbiddenZones ?? new Dictionary<string, SKRect>();
    }

    /// <summary>
    /// 检测违规行为
    /// </summary>
    public List<ViolationRecord> DetectViolations(
        List<HumanPose> poses,
        SOPStep currentStep,
        DateTime timestamp)
    {
        var violations = new List<ViolationRecord>();

        // 更新跟踪器
        var trackedHumans = _tracker.Update(poses);

        foreach (var human in trackedHumans)
        {
            // 检查禁区入侵
            var intrusionViolation = CheckZoneIntrusion(human, currentStep, timestamp);
            if (intrusionViolation != null)
            {
                violations.Add(intrusionViolation);
            }

            // 检查危险动作
            var dangerViolation = CheckDangerousPose(human, currentStep, timestamp);
            if (dangerViolation != null)
            {
                violations.Add(dangerViolation);
            }
        }

        return violations;
    }

    /// <summary>
    /// 检查禁区入侵
    /// </summary>
    private ViolationRecord? CheckZoneIntrusion(TrackedHuman human, SOPStep step, DateTime timestamp)
    {
        var pose = human.CurrentPose;

        foreach (var (zoneId, zone) in _forbiddenZones)
        {
            // 检查双手是否在禁区内
            if (pose.IsAnyHandInRegion(zone))
            {
                return new ViolationRecord
                {
                    Timestamp = timestamp,
                    Type = ViolationType.ZoneIntrusion,
                    Description = $"操作人员手部进入禁区 '{zoneId}'",
                    StepId = step.StepId,
                    Evidence = $"PoseIntrusion_{zoneId}_{timestamp:HHmmss}"
                };
            }
        }

        return null;
    }

    /// <summary>
    /// 检查危险姿态
    /// </summary>
    private ViolationRecord? CheckDangerousPose(TrackedHuman human, SOPStep step, DateTime timestamp)
    {
        var pose = human.CurrentPose;

        // 检查是否弯腰过度（可能受伤）
        var leftShoulder = pose.LeftShoulder;
        var rightShoulder = pose.RightShoulder;
        var leftHip = pose.LeftHip;
        var rightHip = pose.RightHip;

        if (leftShoulder?.IsValid == true && leftHip?.IsValid == true)
        {
            // 简单判断：肩膀比臀部低很多说明弯腰过度
            if (leftShoulder.Y > leftHip.Y + 100)
            {
                return new ViolationRecord
                {
                    Timestamp = timestamp,
                    Type = ViolationType.WrongOrder, // 使用WrongOrder表示不规范动作
                    Description = "检测到不规范姿势：过度弯腰",
                    StepId = step.StepId,
                    Evidence = $"DangerousPose_Bend_{timestamp:HHmmss}"
                };
            }
        }

        return null;
    }

    /// <summary>
    /// 添加禁区
    /// </summary>
    public void AddForbiddenZone(string zoneId, SKRect zone)
    {
        _forbiddenZones[zoneId] = zone;
    }
}
