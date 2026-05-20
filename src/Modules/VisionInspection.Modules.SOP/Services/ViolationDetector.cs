using SkiaSharp;
using VisionInspection.Modules.SOP.Models;
using YoloDotNet.Models;

namespace VisionInspection.Modules.SOP.Services;

/// <summary>
/// 违规检测器
/// </summary>
public class ViolationDetector
{
    private readonly SOPStateMachine _stateMachine;
    private readonly Dictionary<string, DateTime> _violationCooldown = new();
    private readonly Dictionary<string, ZoneDefinition> _zones;

    public ViolationDetector(SOPStateMachine stateMachine, IReadOnlyList<ZoneDefinition>? zones = null)
    {
        _stateMachine = stateMachine;
        _zones = (zones ?? new List<ZoneDefinition>()).ToDictionary(z => z.ZoneId, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 动态更新区域定义
    /// </summary>
    public void UpdateZones(IReadOnlyList<ZoneDefinition> zones)
    {
        _zones.Clear();
        foreach (var zone in zones)
        {
            _zones[zone.ZoneId] = zone;
        }
    }

    /// <summary>
    /// 检测违规
    /// </summary>
    public List<ViolationRecord> DetectViolations(SOPStep currentStep, List<ObjectDetection> detections, DateTime timestamp)
    {
        var violations = new List<ViolationRecord>();

        foreach (var rule in currentStep.ViolationRules)
        {
            var violation = rule.Type switch
            {
                ViolationType.Timeout => DetectTimeout(currentStep, timestamp),
                ViolationType.ForbiddenObject => DetectForbiddenObject(currentStep, detections, timestamp),
                ViolationType.ObjectRemoved => DetectObjectRemoved(currentStep, detections, timestamp),
                ViolationType.ZoneIntrusion => DetectZoneIntrusion(currentStep, detections, timestamp),
                ViolationType.SkipStep => DetectSkipStep(currentStep, detections, timestamp),
                ViolationType.WrongOrder => DetectWrongOrder(currentStep, timestamp),
                _ => null
            };

            if (violation != null && !IsOnCooldown(violation))
            {
                violations.Add(violation);
                SetCooldown(violation);
            }
        }

        // 检查全局超时
        if (_stateMachine.Workflow?.Settings.EnableTimeoutDetection == true)
        {
            var timeoutViolation = DetectTimeout(currentStep, timestamp);
            if (timeoutViolation != null && !IsOnCooldown(timeoutViolation))
            {
                violations.Add(timeoutViolation);
                SetCooldown(timeoutViolation);
            }
        }

        return violations;
    }

    /// <summary>
    /// 检测超时
    /// </summary>
    private ViolationRecord? DetectTimeout(SOPStep step, DateTime timestamp)
    {
        var elapsedSeconds = (timestamp - _stateMachine.StepStartTime).TotalSeconds;

        if (elapsedSeconds > step.TimeoutSec)
        {
            return new ViolationRecord
            {
                Timestamp = timestamp,
                Type = ViolationType.Timeout,
                Description = $"步骤 '{step.StepName}' 超时，已用时 {elapsedSeconds:F1}s，限制 {step.TimeoutSec}s",
                StepId = step.StepId,
                Evidence = $"Timeout_{step.StepId}_{timestamp:HHmmss}"
            };
        }

        return null;
    }

    /// <summary>
    /// 检测禁止对象
    /// </summary>
    private ViolationRecord? DetectForbiddenObject(SOPStep step, List<ObjectDetection> detections, DateTime timestamp)
    {
        var forbiddenClasses = step.ViolationRules
            .Where(r => r.Type == ViolationType.ForbiddenObject)
            .SelectMany(r =>
            {
                var obj = r.Parameters.GetValueOrDefault("ForbiddenClasses", null);
                return obj is System.Collections.IEnumerable enumerable && !(obj is string)
                    ? enumerable.Cast<object>().Select(o => o?.ToString() ?? "").Where(s => !string.IsNullOrEmpty(s))
                    : Enumerable.Empty<string>();
            })
            .Distinct()
            .ToList();

        foreach (var forbiddenClass in forbiddenClasses)
        {
            var detected = detections
                .Where(d => (d.Label?.Name ?? "").Equals(forbiddenClass, StringComparison.OrdinalIgnoreCase))
                .Where(d => d.Confidence >= 0.5f)
                .ToList();

            if (detected.Count > 0)
            {
                var bestMatch = detected.OrderByDescending(d => d.Confidence).First();
                return new ViolationRecord
                {
                    Timestamp = timestamp,
                    Type = ViolationType.ForbiddenObject,
                    Description = $"检测到禁止对象 '{forbiddenClass}'，置信度 {bestMatch.Confidence:P2}",
                    StepId = step.StepId,
                    Evidence = $"Forbidden_{forbiddenClass}_{timestamp:HHmmss}"
                };
            }
        }

        return null;
    }

    /// <summary>
    /// 检测对象被移除
    /// </summary>
    private ViolationRecord? DetectObjectRemoved(SOPStep step, List<ObjectDetection> detections, DateTime timestamp)
    {
        var mustKeepClasses = step.ViolationRules
            .Where(r => r.Type == ViolationType.ObjectRemoved)
            .SelectMany(r =>
            {
                var obj = r.Parameters.GetValueOrDefault("MustKeepClasses", null);
                return obj is System.Collections.IEnumerable enumerable && !(obj is string)
                    ? enumerable.Cast<object>().Select(o => o?.ToString() ?? "").Where(s => !string.IsNullOrEmpty(s))
                    : Enumerable.Empty<string>();
            })
            .Distinct()
            .ToList();

        foreach (var mustKeepClass in mustKeepClasses)
        {
            // 检查历史记录中是否有该对象
            var wasPresent = _stateMachine.StepHistory.Any(h =>
                h.StepId < step.StepId &&
                h.IsPass);

            if (wasPresent)
            {
                var currentlyPresent = detections.Any(d =>
                    (d.Label?.Name ?? "").Equals(mustKeepClass, StringComparison.OrdinalIgnoreCase) &&
                    d.Confidence >= 0.5f);

                if (!currentlyPresent)
                {
                    return new ViolationRecord
                    {
                        Timestamp = timestamp,
                        Type = ViolationType.ObjectRemoved,
                        Description = $"必须保持的对象 '{mustKeepClass}' 被移除",
                        StepId = step.StepId,
                        Evidence = $"Removed_{mustKeepClass}_{timestamp:HHmmss}"
                    };
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 检测区域入侵
    /// </summary>
    private ViolationRecord? DetectZoneIntrusion(SOPStep step, List<ObjectDetection> detections, DateTime timestamp)
    {
        var zoneRules = step.ViolationRules.Where(r => r.Type == ViolationType.ZoneIntrusion).ToList();

        foreach (var rule in zoneRules)
        {
            var zoneId = rule.Parameters.GetValueOrDefault("ZoneId", "")?.ToString() ?? "";
            var forbiddenClassesObj = rule.Parameters.GetValueOrDefault("ForbiddenClasses", null);
            var forbiddenClasses = forbiddenClassesObj is System.Collections.IEnumerable enumerable && !(forbiddenClassesObj is string)
                ? enumerable.Cast<object>().Select(o => o?.ToString() ?? "").Where(s => !string.IsNullOrEmpty(s)).ToList()
                : new List<string>();

            if (string.IsNullOrEmpty(zoneId))
                continue;

            var zone = GetZoneDefinition(zoneId);
            if (zone == null)
                continue;

            foreach (var forbiddenClass in forbiddenClasses)
            {
                var intrusions = detections
                    .Where(d => (d.Label?.Name ?? "").Equals(forbiddenClass, StringComparison.OrdinalIgnoreCase))
                    .Where(d => d.Confidence >= 0.5f)
                    .Where(d => IsInZone(d.BoundingBox, zone))
                    .ToList();

                if (intrusions.Count > 0)
                {
                    return new ViolationRecord
                    {
                        Timestamp = timestamp,
                        Type = ViolationType.ZoneIntrusion,
                        Description = $"对象 '{forbiddenClass}' 进入禁区 '{zoneId}'",
                        StepId = step.StepId,
                        Evidence = $"Intrusion_{forbiddenClass}_{zoneId}_{timestamp:HHmmss}"
                    };
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 检测跳步
    /// </summary>
    private ViolationRecord? DetectSkipStep(SOPStep step, List<ObjectDetection> detections, DateTime timestamp)
    {
        if (_stateMachine.Workflow?.Settings.EnableSkipDetection != true)
            return null;

        // 检查是否满足了后续步骤的条件（说明可能跳过了当前步骤）
        var futureSteps = _stateMachine.Workflow.Steps
            .Where(s => s.StepId > step.StepId)
            .OrderBy(s => s.StepId)
            .ToList();

        foreach (var futureStep in futureSteps)
        {
            // 简化判断：如果检测到后续步骤的关键对象，可能说明跳步了
            var keyObjects = futureStep.PassConditions.Select(c => c.TargetObject).Distinct().ToList();

            foreach (var keyObject in keyObjects)
            {
                if (string.IsNullOrEmpty(keyObject))
                    continue;

                var detected = detections.Any(d =>
                    (d.Label?.Name ?? "").Equals(keyObject, StringComparison.OrdinalIgnoreCase) &&
                    d.Confidence >= 0.6f);

                if (detected)
                {
                    return new ViolationRecord
                    {
                        Timestamp = timestamp,
                        Type = ViolationType.SkipStep,
                        Description = $"可能跳过了步骤 '{step.StepName}'，直接进行 '{futureStep.StepName}'",
                        StepId = step.StepId,
                        Evidence = $"Skip_{step.StepId}_{futureStep.StepId}_{timestamp:HHmmss}"
                    };
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 检测顺序错误
    /// </summary>
    private ViolationRecord? DetectWrongOrder(SOPStep step, DateTime timestamp)
    {
        if (step.RequiredPreviousSteps.Count == 0)
            return null;

        var completedSteps = _stateMachine.StepHistory.Select(h => h.StepId).ToHashSet();
        var missingSteps = step.RequiredPreviousSteps.Where(s => !completedSteps.Contains(s)).ToList();

        if (missingSteps.Count > 0)
        {
            return new ViolationRecord
            {
                Timestamp = timestamp,
                Type = ViolationType.WrongOrder,
                Description = $"步骤顺序错误，缺少前置步骤: {string.Join(", ", missingSteps)}",
                StepId = step.StepId,
                Evidence = $"WrongOrder_{step.StepId}_{string.Join("_", missingSteps)}_{timestamp:HHmmss}"
            };
        }

        return null;
    }

    private bool IsOnCooldown(ViolationRecord violation)
    {
        var key = $"{violation.Type}_{violation.StepId}";
        if (_violationCooldown.TryGetValue(key, out var lastTime))
        {
            return (DateTime.Now - lastTime).TotalSeconds < 3; // 3秒冷却期
        }
        return false;
    }

    private void SetCooldown(ViolationRecord violation)
    {
        var key = $"{violation.Type}_{violation.StepId}";
        _violationCooldown[key] = DateTime.Now;
    }

    private ZoneDefinition? GetZoneDefinition(string zoneId)
    {
        if (_zones.TryGetValue(zoneId, out var zone))
        {
            return zone;
        }

        Console.WriteLine($"[SOP] 警告: 违规检测器未找到区域定义 '{zoneId}'");
        return null;
    }

    private bool IsInZone(SKRect objectBox, ZoneDefinition zone)
    {
        var objectCenterX = objectBox.MidX;
        var objectCenterY = objectBox.MidY;

        return objectCenterX >= zone.X && objectCenterX <= zone.X + zone.Width &&
               objectCenterY >= zone.Y && objectCenterY <= zone.Y + zone.Height;
    }
}
