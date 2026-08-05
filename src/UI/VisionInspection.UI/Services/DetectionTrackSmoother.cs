using SkiaSharp;
using YoloDotNet.Models;

namespace VisionInspection.UI.Services;

/// <summary>
/// 检测框时序平滑器。
/// <para>
/// YOLO 逐帧独立检测，会导致：① 某帧检测到、下一帧漏检 → 框闪烁；② bbox 每帧抖动几像素 → 框跳动。
/// 本类通过 IOU 匹配跟踪 + EMA 位置平滑 + 确认/保持机制消除这些视觉抖动：
/// <list type="bullet">
///   <item>新检测需连续命中 <c>confirmHits</c> 帧才显示（消除单帧噪点闪现）</item>
///   <item>轨迹丢失后保持 <c>maxMissed</c> 帧、位置沿用上一帧（消除短暂漏检的闪烁）</item>
///   <item>匹配上的框做 EMA 平滑，位置渐变而非跳变</item>
/// </list>
/// </para>
/// <para>
/// <b>使用方式</b>：仅在推理产生新结果时调用 <see cref="Update"/>（不要每帧渲染时调用），
/// 渲染时调用 <see cref="GetActiveBoxes"/> 获取平滑后的框列表。
/// </para>
/// </summary>
public class DetectionTrackSmoother
{
    /// <summary>平滑后可渲染的检测框</summary>
    public class TrackedBox
    {
        public string Label = "";
        public SKRect Box;
        public double Confidence;
    }

    private class Track
    {
        public string Label = "";
        public SKRect Box;
        public double Confidence;
        public int HitCount;     // 连续命中次数（达到 confirmHits 才显示）
        public int MissedCount;  // 连续丢失次数（超过 maxMissed 才删除）
    }

    private readonly List<Track> _tracks = new();
    private readonly float _iouThreshold;
    private readonly float _ema;           // 新框权重，0~1，越小越平滑
    private readonly int _confirmHits;     // 连续命中几帧才显示
    private readonly int _maxMissed;       // 连续丢失几帧才删除
    private readonly float _missDecay;     // 丢失时置信度衰减系数

    /// <param name="iouThreshold">IOU 匹配阈值，低于此值视为不匹配</param>
    /// <param name="ema">新框权重（0~1），越小越平滑、越大越跟手</param>
    /// <param name="confirmHits">连续命中几帧后才显示，消除单帧噪点</param>
    /// <param name="maxMissed">连续丢失几帧后删除，消除短暂漏检闪烁</param>
    /// <param name="missDecay">丢失帧的置信度衰减（0~1，每帧乘以此值）</param>
    public DetectionTrackSmoother(
        float iouThreshold = 0.3f,
        float ema = 0.35f,
        int confirmHits = 2,
        int maxMissed = 4,
        float missDecay = 0.85f)
    {
        _iouThreshold = iouThreshold;
        _ema = ema;
        _confirmHits = confirmHits;
        _maxMissed = maxMissed;
        _missDecay = missDecay;
    }

    /// <summary>
    /// 用新一帧检测结果更新轨迹。<b>仅在推理产生新结果时调用</b>。
    /// </summary>
    /// <param name="newDetections">本帧 YOLO 原始检测结果</param>
    /// <param name="minConfidence">低于此置信度的检测直接丢弃</param>
    public void Update(List<ObjectDetection> newDetections, double minConfidence = 0.3)
    {
        // 预过滤低置信度噪点
        var news = newDetections
            .Where(d => d.Confidence >= minConfidence && !d.BoundingBox.IsEmpty)
            .Select(d => (label: d.Label?.Name ?? "unknown", box: d.BoundingBox, conf: d.Confidence))
            .ToList();

        var matchedTrackIdx = new HashSet<int>();
        var matchedNews = new HashSet<int>();

        // 贪心 IOU 匹配：每个新检测找最优匹配的现有轨迹（同 label）
        for (int i = 0; i < news.Count; i++)
        {
            int bestIdx = -1;
            float bestIou = _iouThreshold;
            for (int j = 0; j < _tracks.Count; j++)
            {
                if (matchedTrackIdx.Contains(j)) continue;
                if (!string.Equals(_tracks[j].Label, news[i].label, StringComparison.OrdinalIgnoreCase)) continue;
                float iou = IoU(_tracks[j].Box, news[i].box);
                if (iou > bestIou) { bestIou = iou; bestIdx = j; }
            }
            if (bestIdx >= 0)
            {
                // EMA 平滑位置与置信度
                var t = _tracks[bestIdx];
                t.Box = Lerp(t.Box, news[i].box, _ema);
                t.Confidence = t.Confidence * (1 - _ema) + news[i].conf * _ema;
                t.HitCount++;
                t.MissedCount = 0;
                matchedTrackIdx.Add(bestIdx);
                matchedNews.Add(i);
            }
        }

        // 未匹配的新检测 → 新建轨迹（需达到 confirmHits 才显示）
        for (int i = 0; i < news.Count; i++)
        {
            if (matchedNews.Contains(i)) continue;
            _tracks.Add(new Track
            {
                Label = news[i].label,
                Box = news[i].box,
                Confidence = news[i].conf,
                HitCount = 1,
                MissedCount = 0
            });
        }

        // 未匹配的旧轨迹 → missed++，位置保持、置信度衰减，超过上限删除
        for (int j = _tracks.Count - 1; j >= 0; j--)
        {
            if (matchedTrackIdx.Contains(j)) continue;
            _tracks[j].MissedCount++;
            _tracks[j].Confidence *= _missDecay;
            _tracks[j].HitCount = 0; // 丢失后重新确认，避免恢复时直接显示抖动框
            if (_tracks[j].MissedCount > _maxMissed)
                _tracks.RemoveAt(j);
        }
    }

    /// <summary>
    /// 获取当前可渲染的平滑轨迹（已通过确认机制的）。
    /// </summary>
    public List<TrackedBox> GetActiveBoxes()
    {
        var result = new List<TrackedBox>(_tracks.Count);
        foreach (var t in _tracks)
        {
            if (t.HitCount >= _confirmHits)
            {
                result.Add(new TrackedBox
                {
                    Label = t.Label,
                    Box = t.Box,
                    Confidence = t.Confidence
                });
            }
        }
        return result;
    }

    /// <summary>清空所有轨迹（停止检测时调用）</summary>
    public void Clear() => _tracks.Clear();

    private static float IoU(SKRect a, SKRect b)
    {
        float ix = Math.Max(a.Left, b.Left);
        float iy = Math.Max(a.Top, b.Top);
        float ix2 = Math.Min(a.Right, b.Right);
        float iy2 = Math.Min(a.Bottom, b.Bottom);
        float iw = Math.Max(0, ix2 - ix);
        float ih = Math.Max(0, iy2 - iy);
        float inter = iw * ih;
        float ua = a.Width * a.Height + b.Width * b.Height - inter;
        return ua <= 0 ? 0 : inter / ua;
    }

    private static SKRect Lerp(SKRect a, SKRect b, float t)
        => new(
            a.Left + (b.Left - a.Left) * t,
            a.Top + (b.Top - a.Top) * t,
            a.Right + (b.Right - a.Right) * t,
            a.Bottom + (b.Bottom - a.Bottom) * t);
}
