using System.Diagnostics;
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
    private readonly Dictionary<string, ZoneDefinition> _zones;

    // 调试日志：与 SOPModule.DebugLog 写同一文件 sop_module_debug.log，方便一次性查看
    private const string DebugLogFile = "sop_module_debug.log";
    private static readonly object _dbgLock = new();
    private static void Dbg(string msg)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] [StepConditionEvaluator] {msg}";
        Console.WriteLine(line);
        Debug.WriteLine(line);
        lock (_dbgLock)
        {
            try { File.AppendAllText(DebugLogFile, line + Environment.NewLine); } catch { }
        }
    }

    public StepConditionEvaluator(SOPStateMachine stateMachine, IReadOnlyList<ZoneDefinition>? zones = null)
    {
        _stateMachine = stateMachine;
        _zones = (zones ?? new List<ZoneDefinition>()).ToDictionary(z => z.ZoneId, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 动态更新区域定义（用于热切换配置）
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
    /// 评估步骤的所有条件（物体 + 手部姿态）
    /// </summary>
    public ConditionEvaluationResult EvaluateConditions(SOPStep step, List<ObjectDetection> detections, HandPoseEstimationResult? handResult = null)
    {
        // 先更新手部跨帧跟踪状态（用于稳定/移动判断）
        UpdateHandTracks(handResult);

        if (step.PassConditions.Count == 0)
        {
            return new ConditionEvaluationResult { IsPass = true };
        }

        var results = new List<ConditionCheckResult>();

        // [DEBUG] 评估入口摘要：每帧打印当前步骤的关键输入，便于排查"卡在某一步"。
        // 一帧一行，格式固定，方便从 stdout grep。
        if (step.PassConditions.Any(c =>
            c.Type == ConditionType.HandMoveFromTo ||
            c.Type == ConditionType.HandInRegion ||
            c.Type == ConditionType.HandNotInRegion ||
            c.Type == ConditionType.HandStable))
        {
            int handCount = handResult?.Hands?.Count ?? 0;
            string handSummary = handCount == 0
                ? "none"
                : string.Join("|", handResult!.Hands.Select(h =>
                    $"{h.HandType}(kx={h.Keypoints.Count},bbox={h.BoundingBox.Left:F0},{h.BoundingBox.Top:F0}-{h.BoundingBox.Right:F0},{h.BoundingBox.Bottom:F0})"));
            string detSummary = detections.Count == 0
                ? "none"
                : string.Join("|", detections.Select(d =>
                    $"{d.Label?.Name ?? "?"}({d.Confidence:F2},[{d.BoundingBox.Left:F0},{d.BoundingBox.Top:F0}-{d.BoundingBox.Right:F0},{d.BoundingBox.Bottom:F0}])"));
            Dbg($"step='{step.StepName}' hands={handSummary} dets={detSummary}");
        }

        foreach (var condition in step.PassConditions)
        {
            var result = CheckCondition(condition, detections, handResult);
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

    private ConditionCheckResult CheckCondition(StepCondition condition, List<ObjectDetection> detections, HandPoseEstimationResult? handResult)
    {
        // 手部动作条件分支
        if (condition.Type == ConditionType.HandInRegion ||
            condition.Type == ConditionType.HandNotInRegion ||
            condition.Type == ConditionType.HandStable ||
            condition.Type == ConditionType.HandMoveFromTo)
        {
            return CheckHandCondition(condition, detections, handResult);
        }

        if (condition.Type == ConditionType.HandNearObject)
        {
            return CheckHandNearObject(condition, detections, handResult);
        }

        // 物体/时间条件分支
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

    #region 手部动作条件评估

    /// <summary>
    /// 每只手最近若干帧的中心轨迹与经过区域记录（用于稳定/移动判断）
    /// </summary>
    private class HandTrackState
    {
        public List<SKPoint> CenterHistory { get; } = new();
        public HashSet<string> RecentRegions { get; } = new();   // 最近访问过的区域
        public const int MaxHistory = 30;
    }

    private readonly Dictionary<int, HandTrackState> _handTracks = new();

    private void UpdateHandTracks(HandPoseEstimationResult? handResult)
    {
        if (handResult == null) return;

        var alive = new HashSet<int>();
        foreach (var hand in handResult.Hands)
        {
            if (hand.TrackId < 0) continue;
            alive.Add(hand.TrackId);

            if (!_handTracks.TryGetValue(hand.TrackId, out var track))
            {
                track = new HandTrackState();
                _handTracks[hand.TrackId] = track;
            }

            var center = hand.GetCenter();
            track.CenterHistory.Add(center);
            if (track.CenterHistory.Count > HandTrackState.MaxHistory)
                track.CenterHistory.RemoveAt(0);

            // 记录当前所在的区域
            foreach (var zone in _zones.Values)
            {
                var zoneRect = new SKRect(zone.X, zone.Y, zone.X + zone.Width, zone.Y + zone.Height);
                if (zoneRect.Contains(center))
                {
                    track.RecentRegions.Add(zone.ZoneId);
                }
            }
            if (track.RecentRegions.Count > 20)
                track.RecentRegions.Remove(track.RecentRegions.First());
        }

        // 清理离开画面的手
        foreach (var id in _handTracks.Keys.ToList())
        {
            if (!alive.Contains(id)) _handTracks.Remove(id);
        }
    }

    /// <summary>
    /// 根据条件参数选择左手/右手（默认右手）
    /// </summary>
    private HandPose? SelectHand(StepCondition condition, HandPoseEstimationResult? handResult)
    {
        if (handResult == null || handResult.Hands.Count == 0) return null;

        var handSide = condition.Parameters.GetValueOrDefault("HandSide", "right")?.ToString()?.ToLower() ?? "right";
        var preferredType = handSide switch
        {
            "left" or "左手" => HandType.Left,
            _ => HandType.Right
        };

        var chosen = handResult.Hands.FirstOrDefault(h => h.HandType == preferredType && h.IsValidGesture(8))
                     ?? handResult.Hands.FirstOrDefault(h => h.IsValidGesture(8));
        return chosen;
    }

    private ConditionCheckResult CheckHandCondition(StepCondition condition, List<ObjectDetection> detections, HandPoseEstimationResult? handResult)
    {
        var hand = SelectHand(condition, handResult);
        if (hand == null)
        {
            var side = condition.Parameters.GetValueOrDefault("HandSide", "right");
            return new ConditionCheckResult { IsMet = false, Message = $"未检测到{side}手" };
        }

        var center = hand.GetCenter();

        switch (condition.Type)
        {
            case ConditionType.HandInRegion:
            {
                var zone = GetZoneDefinition(condition.ZoneId ?? "");
                if (zone == null) return new ConditionCheckResult { IsMet = false, Message = $"未找到区域: {condition.ZoneId}" };
                var zoneRect = new SKRect(zone.X, zone.Y, zone.X + zone.Width, zone.Y + zone.Height);
                return zoneRect.Contains(center)
                    ? new ConditionCheckResult { IsMet = true, Message = $"{condition.ZoneId} 内检测到手部" }
                    : new ConditionCheckResult { IsMet = false, Message = $"手部未在区域 {condition.ZoneId} 内" };
            }
            case ConditionType.HandNotInRegion:
            {
                var zone = GetZoneDefinition(condition.ZoneId ?? "");
                if (zone == null) return new ConditionCheckResult { IsMet = false, Message = $"未找到区域: {condition.ZoneId}" };
                var zoneRect = new SKRect(zone.X, zone.Y, zone.X + zone.Width, zone.Y + zone.Height);
                return !zoneRect.Contains(center)
                    ? new ConditionCheckResult { IsMet = true, Message = $"手部不在区域 {condition.ZoneId} 内" }
                    : new ConditionCheckResult { IsMet = false, Message = $"手部仍在区域 {condition.ZoneId} 内" };
            }
            case ConditionType.HandStable:
            {
                if (!_handTracks.TryGetValue(hand.TrackId, out var track) || track.CenterHistory.Count < condition.StableFrames)
                    return new ConditionCheckResult { IsMet = false, Message = $"手部稳定帧不足（已跟踪 {track?.CenterHistory.Count ?? 0}/{condition.StableFrames}）" };

                var tolerance = condition.Parameters.GetValueOrDefault("Tolerance", 20f);
                float tol = tolerance is float f ? f : Convert.ToSingle(tolerance);

                var recent = track.CenterHistory.TakeLast(condition.StableFrames).ToList();
                double maxMove = 0;
                for (int i = 1; i < recent.Count; i++)
                {
                    var d = SKPoint.Distance(recent[i - 1], recent[i]);
                    if (d > maxMove) maxMove = d;
                }
                return maxMove <= tol
                    ? new ConditionCheckResult { IsMet = true, Message = $"手部已稳定（最大位移 {maxMove:F1}px ≤ {tol}px）" }
                    : new ConditionCheckResult { IsMet = false, Message = $"手部未稳定（最大位移 {maxMove:F1}px > {tol}px）" };
            }
            case ConditionType.HandMoveFromTo:
            {
                var fromRegion = condition.Parameters.GetValueOrDefault("FromRegion", "")?.ToString() ?? "";
                var toRegion = condition.Parameters.GetValueOrDefault("ToRegion", "")?.ToString() ?? "";
                bool hasFrom = !string.IsNullOrEmpty(fromRegion);
                bool hasTo = !string.IsNullOrEmpty(toRegion);

                var zoneFrom = hasFrom ? GetZoneDefinition(fromRegion) : null;
                var zoneTo = hasTo ? GetZoneDefinition(toRegion) : null;
                if (hasFrom && zoneFrom == null) return new ConditionCheckResult { IsMet = false, Message = $"未找到起始区域: {fromRegion}" };
                if (hasTo && zoneTo == null) return new ConditionCheckResult { IsMet = false, Message = $"未找到目标区域: {toRegion}" };

                var fromRect = zoneFrom != null ? new SKRect(zoneFrom.X, zoneFrom.Y, zoneFrom.X + zoneFrom.Width, zoneFrom.Y + zoneFrom.Height) : SKRect.Empty;
                var toRect = zoneTo != null ? new SKRect(zoneTo.X, zoneTo.Y, zoneTo.X + zoneTo.Width, zoneTo.Y + zoneTo.Height) : SKRect.Empty;

                bool inFrom = zoneFrom != null && fromRect.Contains(center);
                bool inTo = zoneTo != null && toRect.Contains(center);
                bool visitedFrom = hasFrom
                    && _handTracks.TryGetValue(hand.TrackId, out var t2)
                    && t2.RecentRegions.Contains(fromRegion);

                // [DEBUG] HandMoveFromTo 入口摘要：所有输入一目了然
                Dbg(
                    $"HandMoveFromTo target='{condition.TargetObject}' minConf={condition.MinConfidence:F2} stableFrames={condition.StableFrames} " +
                    $"from='{fromRegion}'({(zoneFrom != null ? $"[{zoneFrom.X},{zoneFrom.Y}-{zoneFrom.X + zoneFrom.Width},{zoneFrom.Y + zoneFrom.Height}]" : "null")}) " +
                    $"to='{toRegion}'({(zoneTo != null ? $"[{zoneTo.X},{zoneTo.Y}-{zoneTo.X + zoneTo.Width},{zoneTo.Y + zoneTo.Height}]" : "null")}) " +
                    $"hand[{hand.HandType}](center=({center.X:F0},{center.Y:F0}) bbox=[{hand.BoundingBox.Left:F0},{hand.BoundingBox.Top:F0}-{hand.BoundingBox.Right:F0},{hand.BoundingBox.Bottom:F0}] keypoints={hand.Keypoints.Count}) " +
                    $"inFrom={inFrom} inTo={inTo} visitedFrom={visitedFrom} trackEntries={_handTracks.Count}");

                // ⭐ 目标物体辅助判定：手正拿着 target_object（适用于"拿起/放下"最直接的语义）
                bool handHoldingTarget = false;
                ObjectDetection? targetObjDebug = null;
                if (!string.IsNullOrEmpty(condition.TargetObject))
                {
                    var allCandidates = detections
                        .Where(d => (d.Label?.Name ?? "").Equals(condition.TargetObject, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    var targetObj = allCandidates
                        .Where(d => d.Confidence >= condition.MinConfidence)
                        .OrderByDescending(d => d.Confidence)
                        .FirstOrDefault();
                    targetObjDebug = targetObj;

                    if (allCandidates.Count > 0 && targetObj == null)
                    {
                        Dbg(
                            $"  找到 {allCandidates.Count} 个候选 '{condition.TargetObject}' 但全部低于阈值 " +
                            $"({condition.MinConfidence:F2})：{string.Join(",", allCandidates.Select(c => $"conf={c.Confidence:F2}"))}");
                    }

                    if (targetObj != null)
                    {
                        handHoldingTarget = IsHandHoldingObject(hand, targetObj, out var intersectDetail);
                        Dbg(
                            $"  target_obj '{condition.TargetObject}' conf={targetObj.Confidence:F2} " +
                            $"bbox=[{targetObj.BoundingBox.Left:F0},{targetObj.BoundingBox.Top:F0}-{targetObj.BoundingBox.Right:F0},{targetObj.BoundingBox.Bottom:F0}] " +
                            $"handHolding={handHoldingTarget} {intersectDetail}");
                    }
                    else if (allCandidates.Count == 0)
                    {
                        Dbg($"  本帧未检测到 '{condition.TargetObject}'（detections 里有 {detections.Count} 个其它物体）");
                    }
                }

                if (hasTo)
                {
                    // 主判据：目标物体（杯子）已到达目标区域（放下的最直接证据）
                    if (!string.IsNullOrEmpty(condition.TargetObject))
                    {
                        var targetObj = detections
                            .Where(d => (d.Label?.Name ?? "").Equals(condition.TargetObject, StringComparison.OrdinalIgnoreCase))
                            .Where(d => d.Confidence >= condition.MinConfidence)
                            .OrderByDescending(d => d.Confidence)
                            .FirstOrDefault();

                        if (targetObj != null)
                        {
                            bool objInTo = toRect != SKRect.Empty && IsInZone(targetObj.BoundingBox, zoneTo!);
                            // 若指定了起始区域，要求手曾访问过起始区域，才能算“从该区域放到目标区域”。
                            // 否则会出现：杯子本来就在目标区域（如拿起前已在桌面），被误判为“已经放下”。
                            if (objInTo && (!hasFrom || visitedFrom))
                            {
                                return new ConditionCheckResult
                                {
                                    IsMet = true,
                                    Message = $"目标物体 '{condition.TargetObject}' 已到达区域 {toRegion}"
                                };
                            }
                        }
                    }

                    // 辅助判据：手到达目标区域 且 正握着目标物体（手把杯子放到该区域，
                    // 而非空手碰一下就判定放回）
                    if (inTo && (!hasFrom || visitedFrom) && handHoldingTarget)
                        return new ConditionCheckResult
                        {
                            IsMet = true,
                            Message = $"手部携带 '{condition.TargetObject}' 已到达区域 {toRegion}"
                        };

                    return new ConditionCheckResult
                    {
                        IsMet = false,
                        Message = hasFrom
                            ? $"手部/物体尚未从 {fromRegion} 到达 {toRegion}（需携带 '{condition.TargetObject}'）"
                            : $"'{condition.TargetObject}' 尚未进入目标区域 {toRegion}（需携带杯子）"
                    };
                }

                // 无目标区域：理解为"从起始区域拿起目标物体"（pickup 语义）
                // ⚠️ 修复：必须以"目标物体（杯子）确实被拿起"为判据，
                //   不能再允许"手空着进入/离开起始区域"就判定拿起（杯子根本没出现）。
                if (hasFrom)
                {
                    // 必须先访问过起始区域，否则不能算"从该区域拿起"
                    if (!visitedFrom)
                    {
                        return new ConditionCheckResult
                        {
                            IsMet = false,
                            Message = $"手部尚未访问区域 {fromRegion}，不能判定拿起"
                        };
                    }

                    // 判定 1：手正握着目标物体（物体被握起）——最直接的"拿起"证据
                    if (handHoldingTarget)
                    {
                        return new ConditionCheckResult
                        {
                            IsMet = true,
                            Message = $"手部已在 {fromRegion} 内拿起 '{condition.TargetObject}'"
                        };
                    }

                    // 判定 2：目标物体已离开起始区域（被拿走）——要求能检测到目标物体
                    if (!string.IsNullOrEmpty(condition.TargetObject))
                    {
                        var targetObj = detections
                            .Where(d => (d.Label?.Name ?? "").Equals(condition.TargetObject, StringComparison.OrdinalIgnoreCase))
                            .Where(d => d.Confidence >= condition.MinConfidence)
                            .OrderByDescending(d => d.Confidence)
                            .FirstOrDefault();

                        if (targetObj != null && fromRect != SKRect.Empty && !IsInZone(targetObj.BoundingBox, zoneFrom!))
                        {
                            return new ConditionCheckResult
                            {
                                IsMet = true,
                                Message = $"目标物体 '{condition.TargetObject}' 已离开区域 {fromRegion}"
                            };
                        }
                    }

                    return new ConditionCheckResult
                    {
                        IsMet = false,
                        Message = $"手部在区域 {fromRegion} 内，但未检测到 '{condition.TargetObject}' 被拿起（杯子需被拿起）"
                    };
                }

                // 既无 from 也无 to：只判"手拿着目标物体"
                if (handHoldingTarget)
                    return new ConditionCheckResult { IsMet = true, Message = $"手部已拿起 '{condition.TargetObject}'" };

                return new ConditionCheckResult { IsMet = false, Message = "未配置起始/目标区域" };
            }
            default:
                return new ConditionCheckResult { IsMet = false, Message = $"未支持的手部条件: {condition.Type}" };
        }
    }

    /// <summary>
    /// 手部靠近目标物体（手-物交互判断）：手的包围盒与目标检测框（外扩 margin）相交
    /// </summary>
    private ConditionCheckResult CheckHandNearObject(StepCondition condition, List<ObjectDetection> detections, HandPoseEstimationResult? handResult)
    {
        var hand = SelectHand(condition, handResult);
        if (hand == null)
            return new ConditionCheckResult { IsMet = false, Message = "未检测到目标手" };

        var marginValue = condition.Parameters.GetValueOrDefault("Margin", 30f);
        float margin = marginValue is float f ? f : Convert.ToSingle(marginValue);

        foreach (var det in detections)
        {
            if (!string.Equals(det.Label?.Name, condition.TargetObject, StringComparison.OrdinalIgnoreCase)) continue;
            if (det.Confidence < condition.MinConfidence) continue;

            var box = det.BoundingBox;
            var expanded = new SKRect(box.Left - margin, box.Top - margin, box.Right + margin, box.Bottom + margin);
            if (expanded.IntersectsWith(hand.BoundingBox))
                return new ConditionCheckResult { IsMet = true, Message = $"手部靠近 '{condition.TargetObject}'" };
        }

        return new ConditionCheckResult { IsMet = false, Message = $"手部未靠近 '{condition.TargetObject}'" };
    }

    #endregion

    /// <summary>
    /// 判断手是否正拿着目标物体：手框与物体框相交，或手的中心落在物体框内。
    /// </summary>
    private static bool IsHandHoldingObject(HandPose hand, ObjectDetection targetObj)
    {
        return IsHandHoldingObject(hand, targetObj, out _);
    }

    /// <summary>
    /// 重载：附加返回每条子判断的明细，供调试日志使用。
    /// </summary>
    private static bool IsHandHoldingObject(HandPose hand, ObjectDetection targetObj, out string detail)
    {
        var objBox = targetObj.BoundingBox;
        if (objBox.IsEmpty)
        {
            detail = "(物体 BBox 为空)";
            return false;
        }

        // 1. 手部包围盒与物体包围盒相交
        bool intersectBox = hand.BoundingBox.IntersectsWith(objBox);
        if (intersectBox)
        {
            detail = $"(BBox相交: hand[{hand.BoundingBox.Left:F0},{hand.BoundingBox.Top:F0}-{hand.BoundingBox.Right:F0},{hand.BoundingBox.Bottom:F0}] ∩ obj[{objBox.Left:F0},{objBox.Top:F0}-{objBox.Right:F0},{objBox.Bottom:F0}])";
            return true;
        }

        // 2. 手部中心落在物体框内
        var center = hand.GetCenter();
        bool centerInObj = objBox.Contains((int)center.X, (int)center.Y);
        if (centerInObj)
        {
            detail = $"(手中心({center.X:F0},{center.Y:F0})在物体内)";
            return true;
        }

        // 3. 手部中心在物体框附近（留 30px 容差）
        const float margin = 30f;
        var expanded = new SKRect(objBox.Left - margin, objBox.Top - margin, objBox.Right + margin, objBox.Bottom + margin);
        bool centerNearObj = expanded.Contains((int)center.X, (int)center.Y);
        if (centerNearObj)
        {
            detail = $"(手中心({center.X:F0},{center.Y:F0})在物体框外扩[{margin}px]范围内)";
            return true;
        }

        detail = $"(手中心({center.X:F0},{center.Y:F0})距离物体中心最近" +
                 $"|dx={center.X - (objBox.Left + objBox.Right) / 2:F0}|dy={center.Y - (objBox.Top + objBox.Bottom) / 2:F0}|)";
        return false;
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
        if (_zones.TryGetValue(zoneId, out var zone))
        {
            return zone;
        }

        Console.WriteLine($"[SOP] 警告: 未找到区域定义 '{zoneId}'，请检查 YAML 配置中的 regions 字段");
        return null;
    }

    private static bool IsInZone(SKRect objectBox, ZoneDefinition zone)
    {
        // 检查边界框是否与区域有重叠（IOU > 0 即可）
        var zoneBox = new SKRect(zone.X, zone.Y, zone.X + zone.Width, zone.Y + zone.Height);
        return objectBox.IntersectsWith(zoneBox);
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


