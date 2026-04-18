using SkiaSharp;
using VisionInspection.Modules.SOP.Models;
using YoloDotNet.Models;

namespace VisionInspection.Modules.SOP.Services;

/// <summary>
/// 步骤条件评估器
/// </summary>
public class StepConditionEvaluator
{
    private readonly SOPStateMachine _stateMachine;

    public StepConditionEvaluator(SOPStateMachine stateMachine)
    {
        _stateMachine = stateMachine;
    }

    /// <summary>
    /// 评估步骤的所有条件
    /// </summary>
    public ConditionEvaluationResult EvaluateConditions(SOPStep step, List<ObjectDetection> detections)
    {
        if (step.PassConditions.Count == 0)
        {
            return new ConditionEvaluationResult { IsPass = true };
        }

        var results = new List<ConditionCheckResult>();

        foreach (var condition in step.PassConditions)
        {
            var result = CheckCondition(condition, detections);
            results.Add(result);

            if (!result.IsMet)
            {
                return new ConditionEvaluationResult
                {
                    IsPass = false,
                    FailedCondition = condition,
                    Message = result.Message,
                    CheckResults = results
                };
            }
        }

        return new ConditionEvaluationResult
        {
            IsPass = true,
            Message = $"步骤 '{step.StepName}' 所有条件满足",
            CheckResults = results
        };
    }

    private ConditionCheckResult CheckCondition(StepCondition condition, List<ObjectDetection> detections)
    {
        return condition.Type switch
        {
            ConditionType.ObjectPresent => CheckObjectPresent(condition, detections),
            ConditionType.ObjectInZone => CheckObjectInZone(condition, detections),
            ConditionType.ObjectStable => CheckObjectStable(condition),
            ConditionType.ObjectAbsent => CheckObjectAbsent(condition, detections),
            ConditionType.SequenceComplete => CheckSequenceComplete(condition),
            ConditionType.TimeElapsed => CheckTimeElapsed(condition),
            _ => new ConditionCheckResult { IsMet = false, Message = $"未知条件类型: {condition.Type}" }
        };
    }

    /// <summary>
    /// 检查目标是否存在
    /// </summary>
    private ConditionCheckResult CheckObjectPresent(StepCondition condition, List<ObjectDetection> detections)
    {
        var matchingObjects = detections
            .Where(d => (d.Label?.Name ?? "").Equals(condition.TargetObject, StringComparison.OrdinalIgnoreCase))
            .Where(d => d.Confidence >= condition.MinConfidence)
            .ToList();

        if (matchingObjects.Count == 0)
        {
            return new ConditionCheckResult
            {
                IsMet = false,
                Message = $"未检测到目标 '{condition.TargetObject}'"
            };
        }

        var bestMatch = matchingObjects.OrderByDescending(d => d.Confidence).First();
        return new ConditionCheckResult
        {
            IsMet = true,
            Message = $"检测到 '{condition.TargetObject}'，置信度: {bestMatch.Confidence:P2}",
            MatchedObjects = matchingObjects
        };
    }

    /// <summary>
    /// 检查目标是否在指定区域内
    /// </summary>
    private ConditionCheckResult CheckObjectInZone(StepCondition condition, List<ObjectDetection> detections)
    {
        if (string.IsNullOrEmpty(condition.ZoneId))
        {
            return new ConditionCheckResult
            {
                IsMet = false,
                Message = "未指定区域ID"
            };
        }

        // 获取区域定义（这里简化处理，实际应从配置中读取）
        var zone = GetZoneDefinition(condition.ZoneId);
        if (zone == null)
        {
            return new ConditionCheckResult
            {
                IsMet = false,
                Message = $"未找到区域定义: {condition.ZoneId}"
            };
        }

        var matchingObjects = detections
            .Where(d => (d.Label?.Name ?? "").Equals(condition.TargetObject, StringComparison.OrdinalIgnoreCase))
            .Where(d => d.Confidence >= condition.MinConfidence)
            .Where(d => IsInZone(d.BoundingBox, zone))
            .ToList();

        if (matchingObjects.Count == 0)
        {
            return new ConditionCheckResult
            {
                IsMet = false,
                Message = $"目标 '{condition.TargetObject}' 不在区域 '{condition.ZoneId}' 内"
            };
        }

        return new ConditionCheckResult
        {
            IsMet = true,
            Message = $"目标 '{condition.TargetObject}' 在区域 '{condition.ZoneId}' 内",
            MatchedObjects = matchingObjects
        };
    }

    /// <summary>
    /// 检查目标是否稳定
    /// </summary>
    private ConditionCheckResult CheckObjectStable(StepCondition condition)
    {
        var trackedObjects = _stateMachine.TrackedObjects
            .Where(kv => kv.Value.ClassName.Equals(condition.TargetObject, StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv.Value)
            .ToList();

        if (trackedObjects.Count == 0)
        {
            return new ConditionCheckResult
            {
                IsMet = false,
                Message = $"未跟踪到目标 '{condition.TargetObject}'"
            };
        }

        var toleranceValue = condition.Parameters.GetValueOrDefault("Tolerance", 20f);
        float tolerance = toleranceValue is float f ? f : Convert.ToSingle(toleranceValue);
        var stableObjects = trackedObjects.Where(o => o.IsStable(tolerance)).ToList();

        if (stableObjects.Count == 0)
        {
            var maxFrames = trackedObjects.Max(o => o.FrameCount);
            return new ConditionCheckResult
            {
                IsMet = false,
                Message = $"目标 '{condition.TargetObject}' 尚未稳定（已跟踪 {maxFrames} 帧，需要 {condition.StableFrames} 帧）"
            };
        }

        var mostStable = stableObjects.OrderByDescending(o => o.FrameCount).First();
        return new ConditionCheckResult
        {
            IsMet = true,
            Message = $"目标 '{condition.TargetObject}' 已稳定（跟踪 {mostStable.FrameCount} 帧）",
            MatchedObjects = new List<ObjectDetection>() // 这里需要转换
        };
    }

    /// <summary>
    /// 检查目标是否不存在
    /// </summary>
    private ConditionCheckResult CheckObjectAbsent(StepCondition condition, List<ObjectDetection> detections)
    {
        var matchingObjects = detections
            .Where(d => (d.Label?.Name ?? "").Equals(condition.TargetObject, StringComparison.OrdinalIgnoreCase))
            .Where(d => d.Confidence >= condition.MinConfidence)
            .ToList();

        if (matchingObjects.Count > 0)
        {
            var bestMatch = matchingObjects.OrderByDescending(d => d.Confidence).First();
            return new ConditionCheckResult
            {
                IsMet = false,
                Message = $"不应存在目标 '{condition.TargetObject}'，但检测到置信度 {bestMatch.Confidence:P2}"
            };
        }

        return new ConditionCheckResult
        {
            IsMet = true,
            Message = $"目标 '{condition.TargetObject}' 不存在，符合条件"
        };
    }

    /// <summary>
    /// 检查前置序列是否完成
    /// </summary>
    private ConditionCheckResult CheckSequenceComplete(StepCondition condition)
    {
        var completedSteps = _stateMachine.StepHistory.Select(r => r.StepId).ToHashSet();
        var requiredSteps = condition.Parameters.GetValueOrDefault("RequiredSteps", new List<int>()) as List<int> ?? new List<int>();

        var missingSteps = requiredSteps.Where(s => !completedSteps.Contains(s)).ToList();

        if (missingSteps.Count > 0)
        {
            return new ConditionCheckResult
            {
                IsMet = false,
                Message = $"前置步骤未完成: {string.Join(", ", missingSteps)}"
            };
        }

        return new ConditionCheckResult
        {
            IsMet = true,
            Message = "所有前置步骤已完成"
        };
    }

    /// <summary>
    /// 检查时间是否满足
    /// </summary>
    private ConditionCheckResult CheckTimeElapsed(StepCondition condition)
    {
        var elapsedSeconds = (DateTime.Now - _stateMachine.StepStartTime).TotalSeconds;
        var requiredSecondsValue = condition.Parameters.GetValueOrDefault("RequiredSeconds", 0.0);
        double requiredSeconds = requiredSecondsValue is double d ? d : Convert.ToDouble(requiredSecondsValue);

        if (elapsedSeconds < requiredSeconds)
        {
            return new ConditionCheckResult
            {
                IsMet = false,
                Message = $"时间未满足，已等待 {elapsedSeconds:F1}s，需要 {requiredSeconds:F1}s"
            };
        }

        return new ConditionCheckResult
        {
            IsMet = true,
            Message = $"时间满足，已等待 {elapsedSeconds:F1}s"
        };
    }

    private ZoneDefinition? GetZoneDefinition(string zoneId)
    {
        // TODO: 从配置中读取区域定义
        // 这里返回模拟数据
        return new ZoneDefinition
        {
            ZoneId = zoneId,
            Name = zoneId,
            X = 0.2f,
            Y = 0.2f,
            Width = 0.3f,
            Height = 0.3f
        };
    }

    private bool IsInZone(SKRect objectBox, ZoneDefinition zone)
    {
        // 简化判断：检查对象中心点是否在区域内
        var objectCenterX = objectBox.MidX;
        var objectCenterY = objectBox.MidY;

        // 假设区域坐标是归一化的（0-1），需要转换为实际像素
        // 这里简化处理
        return objectCenterX >= zone.X && objectCenterX <= zone.X + zone.Width &&
               objectCenterY >= zone.Y && objectCenterY <= zone.Y + zone.Height;
    }
}

/// <summary>
/// 条件评估结果
/// </summary>
public class ConditionEvaluationResult
{
    public bool IsPass { get; set; }
    public string Message { get; set; } = "";
    public StepCondition? FailedCondition { get; set; }
    public List<ConditionCheckResult> CheckResults { get; set; } = new();
}

/// <summary>
/// 条件检查结果
/// </summary>
public class ConditionCheckResult
{
    public bool IsMet { get; set; }
    public string Message { get; set; } = "";
    public List<ObjectDetection> MatchedObjects { get; set; } = new();
    public List<HumanPose> MatchedPoses { get; set; } = new();
}

/// <summary>
/// 区域定义
/// </summary>
public class ZoneDefinition
{
    public string ZoneId { get; set; } = "";
    public string Name { get; set; } = "";
    public float X { get; set; }
    public float Y { get; set; }
    public float Width { get; set; }
    public float Height { get; set; }
}
