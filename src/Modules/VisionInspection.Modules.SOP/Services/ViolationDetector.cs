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
    private readonly StepConditionEvaluator _conditionEvaluator;
    private readonly Dictionary<string, DateTime> _violationCooldown = new();
    private readonly Dictionary<string, ZoneDefinition> _zones;

    public ViolationDetector(SOPStateMachine stateMachine, StepConditionEvaluator conditionEvaluator, IReadOnlyList<ZoneDefinition>? zones = null)
    {
        _stateMachine = stateMachine;
        _conditionEvaluator = conditionEvaluator;
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
    public List<ViolationRecord> DetectViolations(SOPStep currentStep, List<ObjectDetection> detections, HandPoseEstimationResult? handResult, DateTime timestamp)
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
                ViolationType.WrongOrder => DetectWrongOrder(currentStep, timestamp),
                ViolationType.MissingRequiredObject => DetectMissingRequiredObject(currentStep, detections, timestamp),
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

        // 全局跳步检测：基于"后续步骤的完整条件是否已满足"判断，比单纯检测物体出现更可靠，
        // 避免把正常的拿/放过程误判为跳步。仅在 EnableSkipDetection 开启、且非第一步时启用。
        if (_stateMachine.Workflow?.Settings.EnableSkipDetection == true)
        {
            var minStepId = _stateMachine.Workflow.Steps.Count > 0
                ? _stateMachine.Workflow.Steps.Min(s => s.StepId)
                : 0;
            if (currentStep.StepId > minStepId)
            {
                var skip = DetectSkipStep(currentStep, detections, handResult, timestamp);
                if (skip != null && !IsOnCooldown(skip))
                {
                    violations.Add(skip);
                    SetCooldown(skip);
                }
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

        // TimeoutSec <= 0 表示该步骤不限制超时（"启用本步超时"未勾选）
        if (step.TimeoutSec > 0 && elapsedSeconds > step.TimeoutSec)
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
    /// 检测漏放 / 缺料：当前步骤声明的必放物料（required_objects）中，若有任意一项在画面
    /// （或在指定区域内）未以足够置信度出现，即判"漏放"违规。
    /// 这是通用的数据驱动规则——每个配方在 YAML 中声明自己的必放物料，切换产品即复用同一引擎。
    /// 防误报：若画面中完全没有任何必放物料出现，视为"成品尚未入框"，暂不判定漏放。
    /// </summary>
    private ViolationRecord? DetectMissingRequiredObject(SOPStep step, List<ObjectDetection> detections, DateTime timestamp)
    {
        var reqRules = step.ViolationRules
            .Where(r => r.Type == ViolationType.MissingRequiredObject)
            .ToList();
        if (reqRules.Count == 0) return null;

        // 汇集本步骤所有必放物料（类别 + 可选区域 + 最小置信度）
        var required = new List<(string Class, string Zone, float MinConf)>();
        foreach (var rule in reqRules)
        {
            if (!rule.Parameters.TryGetValue("RequiredObjects", out var obj)
                || obj is not System.Collections.IEnumerable enumerable)
            {
                continue;
            }

            foreach (var item in enumerable)
            {
                if (item is Dictionary<string, object> d)
                {
                    var cls = (d.GetValueOrDefault("class", "") as string) ?? "";
                    if (string.IsNullOrWhiteSpace(cls)) continue;
                    var zone = (d.GetValueOrDefault("zone", "") as string) ?? "";
                    var mcVal = d.GetValueOrDefault("min_confidence", 0.5f);
                    float minc = mcVal is float f ? f : Convert.ToSingle(mcVal);
                    required.Add((cls, zone, minc));
                }
                else if (item is string s && !string.IsNullOrWhiteSpace(s))
                {
                    required.Add((s, "", 0.5f));
                }
            }
        }

        if (required.Count == 0) return null;

        // 防误报：画面中完全未出现任何必放物料时，视为成品尚未入框，不判漏放
        var anyPresentAnywhere = detections.Any(d =>
            required.Any(r => (d.Label?.Name ?? "").Equals(r.Class, StringComparison.OrdinalIgnoreCase)
                              && d.Confidence >= r.MinConf));
        if (!anyPresentAnywhere) return null;

        // 逐项检查是否漏放
        var missing = new List<string>();
        foreach (var (cls, zone, minc) in required)
        {
            var matches = detections
                .Where(d => (d.Label?.Name ?? "").Equals(cls, StringComparison.OrdinalIgnoreCase))
                .Where(d => d.Confidence >= minc);

            if (!string.IsNullOrEmpty(zone))
            {
                var z = GetZoneDefinition(zone);
                if (z != null) matches = matches.Where(d => IsInZone(d.BoundingBox, z));
            }

            if (!matches.Any())
            {
                missing.Add(string.IsNullOrEmpty(zone) ? cls : $"{cls}(区域:{zone})");
            }
        }

        if (missing.Count == 0) return null;

        return new ViolationRecord
        {
            Timestamp = timestamp,
            Type = ViolationType.MissingRequiredObject,
            Description = $"漏放：缺少必须放置的物料 {string.Join(", ", missing)}",
            StepId = step.StepId,
            Evidence = $"Missing_{string.Join("_", missing)}_{timestamp:HHmmss}"
        };
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
    /// 检测跳步：若"下一个"步骤（按 StepId 顺序）的条件已经满足，而当前步骤尚未通过，
    /// 说明当前步骤被跳过。只检查下一步，避免把更后面的 time_elapsed 等条件误报为跳步。
    /// </summary>
    private ViolationRecord? DetectSkipStep(SOPStep step, List<ObjectDetection> detections, HandPoseEstimationResult? handResult, DateTime timestamp)
    {
        if (_stateMachine.Workflow?.Settings.EnableSkipDetection != true)
            return null;

        // 只检查"下一个"步骤：跳步=当前步骤未完成却做了下一步。
        var nextStep = _stateMachine.Workflow.Steps
            .Where(s => s.StepId > step.StepId)
            .OrderBy(s => s.StepId)
            .FirstOrDefault();

        if (nextStep == null) return null;

        // 排除纯时间条件步骤（如 complete 的 time_elapsed），否则只要等 1 秒就误报跳步
        if (nextStep.PassConditions.Count == 1 && nextStep.PassConditions[0].Type == ConditionType.TimeElapsed)
            return null;

        // 跳步检测：忽略 nextStep 的 from_region 时序约束。用户跳过中间步骤时，
        // 物体没经过起始区域（如没到嘴边），若要求 visitedFrom 会漏报跳步。
        // 只需核心动作意图（物体到达目标区域）即判定"正在做下一步"，从而报跳步。
        var eval = _conditionEvaluator.EvaluateConditions(nextStep, detections, handResult, ignoreFromRegion: true);
        if (eval.IsPass)
        {
            return new ViolationRecord
            {
                Timestamp = timestamp,
                Type = ViolationType.SkipStep,
                Description = $"可能跳过了步骤 '{step.StepName}'，直接满足下一步 '{nextStep.StepName}'",
                StepId = step.StepId,
                Evidence = $"Skip_{step.StepId}_{nextStep.StepId}_{timestamp:HHmmss}"
            };
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

    private static bool IsInZone(SKRect objectBox, ZoneDefinition zone)
    {
        var zoneBox = new SKRect(zone.X, zone.Y, zone.X + zone.Width, zone.Y + zone.Height);
        return objectBox.IntersectsWith(zoneBox);
    }
}
