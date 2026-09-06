using SkiaSharp;
using VisionInspection.Modules.SOP.Models;
using YoloDotNet.Models;

namespace VisionInspection.Modules.SOP.Services;

/// <summary>
/// 跨相机协同规则评估器：多相机分别检测后，按规则的条件组合（全部满足）判定是否命中。
/// 纯评估逻辑，无 UI 依赖；冷却按规则 Id 维护，防止每帧连续触发。
/// </summary>
public sealed class CrossCameraEvaluator
{
    private readonly Dictionary<string, DateTime> _lastFireTimes = new();
    private readonly Dictionary<string, bool> _activeStates = new();

    /// <summary>
    /// 评估所有跨相机规则，返回首个命中的规则（含冷却过滤）。
    /// </summary>
    public CrossCameraRuleHit? Evaluate(
        IReadOnlyDictionary<string, List<ObjectDetection>> perCameraDetections,
        IReadOnlyList<ZoneDefinition> regions,
        SOPCrossCameraConfig cfg,
        DateTime now)
    {
        if (cfg == null || !cfg.Enabled || cfg.Rules.Count == 0) return null;
        if (regions == null || regions.Count == 0) return null;

        foreach (var rule in cfg.Rules)
        {
            if (string.IsNullOrEmpty(rule.Id) || rule.Conditions.Count == 0) continue;

            // 冷却：同一规则在冷却期内不重复触发
            if (_activeStates.GetValueOrDefault(rule.Id) &&
                (now - _lastFireTimes.GetValueOrDefault(rule.Id)).TotalSeconds < cfg.CooldownSeconds)
                continue;

            var details = new List<string>();
            bool allHit = true;

            foreach (var cond in rule.Conditions)
            {
                var zone = ResolveZone(regions, cond.CameraId, cond.RegionId);
                if (zone == null)
                {
                    allHit = false;
                    break;
                }

                if (!perCameraDetections.TryGetValue(cond.CameraId, out var dets) || dets == null)
                {
                    // 该相机本帧无检测结果 → 不触发（防误报）
                    allHit = false;
                    break;
                }

                bool present = EvaluateCondition(dets, cond, zone);
                if (present != cond.Present)
                {
                    allHit = false;
                    break;
                }

                details.Add($"{cond.CameraId}/{zone.Name ?? cond.RegionId}:{(cond.Present ? "出现" : "未出现")}{cond.ObjectClass}");
            }

            if (allHit)
            {
                _lastFireTimes[rule.Id] = now;
                _activeStates[rule.Id] = true;
                return new CrossCameraRuleHit(rule, string.Join(" 且 ", details), now);
            }

            _activeStates[rule.Id] = false;
        }

        return null;
    }

    /// <summary>
    /// 命中判定：区域内存在 ≥ MinConfidence 的指定类别（检测框中心落入区域矩形）
    /// </summary>
    private static bool EvaluateCondition(List<ObjectDetection> dets, SOPCrossCameraCondition cond, ZoneDefinition zone)
    {
        var rect = new SKRect(zone.X, zone.Y, zone.X + zone.Width, zone.Y + zone.Height);
        return dets.Any(d =>
            d.Confidence >= cond.MinConfidence &&
            string.Equals(d.Label?.Name, cond.ObjectClass, StringComparison.OrdinalIgnoreCase) &&
            rect.Contains(d.BoundingBox.MidX, d.BoundingBox.MidY));
    }

    /// <summary>
    /// 区域解析：优先精确匹配 (CameraId, ZoneId)；无 camera 归属时按 ZoneId 唯一匹配
    /// </summary>
    private static ZoneDefinition? ResolveZone(IReadOnlyList<ZoneDefinition> regions, string cameraId, string zoneId)
    {
        return regions.FirstOrDefault(z =>
                   string.Equals(z.ZoneId, zoneId, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(z.CameraId, cameraId, StringComparison.OrdinalIgnoreCase))
               ?? regions.FirstOrDefault(z =>
                   string.Equals(z.ZoneId, zoneId, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// 跨相机规则命中结果
/// </summary>
public class CrossCameraRuleHit
{
    public SOPCrossCameraRule Rule { get; }
    public string Detail { get; }
    public DateTime Timestamp { get; }

    public CrossCameraRuleHit(SOPCrossCameraRule rule, string detail, DateTime timestamp)
    {
        Rule = rule;
        Detail = detail;
        Timestamp = timestamp;
    }
}
