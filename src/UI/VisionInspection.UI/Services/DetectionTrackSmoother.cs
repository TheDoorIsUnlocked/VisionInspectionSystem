using SkiaSharp;
using VisionInspection.Core.Services;
using YoloDotNet.Models;
using KeyPoint = VisionInspection.Core.Services.KeyPoint;

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
        public int HitCount;     // 连续命中次数（达到 confirmHits 才首次显示）
        public int MissedCount;  // 连续丢失次数（超过 maxMissed 才删除）
        public bool EverConfirmed; // 是否曾经确认显示过（一旦为 true，后续仅按 MissedCount 决定是否续显）
        public object? Raw;                   // 原始检测对象（DetectedObject / ObjectDetection），用于取附加数据（关键点/掩膜）
        public List<KeyPoint>? SmoothedKeyPoints;  // 平滑后的关键点（仅姿态模型有）
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
    /// 用新一帧检测结果更新轨迹（SOP 路径，输入 YoloDotNet 的 ObjectDetection）。<b>仅在推理产生新结果时调用</b>。
    /// </summary>
    /// <param name="newDetections">本帧 YOLO 原始检测结果</param>
    /// <param name="minConfidence">低于此置信度的检测直接丢弃</param>
    public void Update(List<ObjectDetection> newDetections, double minConfidence = 0.3)
    {
        var news = newDetections
            .Where(d => d.Confidence >= minConfidence && !d.BoundingBox.IsEmpty)
            .Select(d => (label: d.Label?.Name ?? "unknown",
                          box: new SKRect(d.BoundingBox.Left, d.BoundingBox.Top, d.BoundingBox.Right, d.BoundingBox.Bottom),
                          conf: (double)d.Confidence, raw: (object)d))
            .ToList();
        UpdateInternal(news);
    }

    /// <summary>
    /// 用新一帧检测结果更新轨迹（实时检测路径，输入自定义 DetectedObject；保留关键点/掩膜并平滑关键点）。<b>仅在推理产生新结果时调用</b>。
    /// </summary>
    /// <param name="newDetections">本帧 YOLO 原始检测结果</param>
    /// <param name="minConfidence">低于此置信度的检测直接丢弃</param>
    public void Update(List<DetectedObject> newDetections, double minConfidence = 0.3)
    {
        var news = newDetections
            .Where(d => d.Confidence >= minConfidence && d.PixelBoundingBox.Width > 0 && d.PixelBoundingBox.Height > 0)
            .Select(d => (label: d.ClassName ?? "unknown",
                          box: new SKRect(d.PixelBoundingBox.Left, d.PixelBoundingBox.Top, d.PixelBoundingBox.Right, d.PixelBoundingBox.Bottom),
                          conf: (double)d.Confidence,
                          raw: (object)d))
            .ToList();
        UpdateInternal(news);
    }

    private void UpdateInternal(List<(string label, SKRect box, double conf, object raw)> news)
    {
        var matchedTrackIdx = new HashSet<int>();
        var matchedNews = new HashSet<int>();

        // 贪心匹配：每个新检测找最优匹配的现有轨迹（同 label）。
        // 匹配分综合 IoU + 中心点距离 + 尺寸相似度，抖动场景下也能稳定匹配（纯 IoU 在抖动时易失配导致闪烁）。
        for (int i = 0; i < news.Count; i++)
        {
            int bestIdx = -1;
            float bestScore = -1f;
            for (int j = 0; j < _tracks.Count; j++)
            {
                if (matchedTrackIdx.Contains(j)) continue;
                if (!string.Equals(_tracks[j].Label, news[i].label, StringComparison.OrdinalIgnoreCase)) continue;
                float score = MatchScore(_tracks[j].Box, news[i].box);
                if (score > bestScore) { bestScore = score; bestIdx = j; }
            }
            // 匹配得分需超过用户设定的跟踪 IoU 阈值才视为同一目标
            if (bestIdx >= 0 && bestScore >= _iouThreshold)
            {
                // EMA 平滑位置与置信度
                var t = _tracks[bestIdx];
                t.Box = Lerp(t.Box, news[i].box, _ema);
                t.Confidence = t.Confidence * (1 - _ema) + news[i].conf * _ema;
                t.HitCount++;
                if (t.HitCount >= _confirmHits) t.EverConfirmed = true;
                t.MissedCount = 0;
                t.Raw = news[i].raw;

                // 关键点平滑（仅姿态模型会带 KeyPoints；按 index 做 EMA）
                if (news[i].raw is DetectedObject d && d.KeyPoints != null && d.KeyPoints.Count > 0)
                {
                    if (t.SmoothedKeyPoints == null || t.SmoothedKeyPoints.Count != d.KeyPoints.Count)
                    {
                        t.SmoothedKeyPoints = d.KeyPoints.Select(k =>
                            new KeyPoint { Index = k.Index, Name = k.Name, X = k.X, Y = k.Y, Confidence = k.Confidence }).ToList();
                    }
                    else
                    {
                        for (int k = 0; k < d.KeyPoints.Count; k++)
                        {
                            t.SmoothedKeyPoints[k].X = t.SmoothedKeyPoints[k].X * (1 - _ema) + d.KeyPoints[k].X * _ema;
                            t.SmoothedKeyPoints[k].Y = t.SmoothedKeyPoints[k].Y * (1 - _ema) + d.KeyPoints[k].Y * _ema;
                            t.SmoothedKeyPoints[k].Confidence = t.SmoothedKeyPoints[k].Confidence * (1 - _ema) + d.KeyPoints[k].Confidence * _ema;
                        }
                    }
                }

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
                MissedCount = 0,
                Raw = news[i].raw,
                SmoothedKeyPoints = (news[i].raw is DetectedObject d && d.KeyPoints != null)
                    ? d.KeyPoints.Select(k => new KeyPoint { Index = k.Index, Name = k.Name, X = k.X, Y = k.Y, Confidence = k.Confidence }).ToList()
                    : null
            });
        }

        // 未匹配的旧轨迹 → missed++，位置保持、置信度衰减，超过上限删除。
        // 注意：不再重置 HitCount。一旦 EverConfirmed=true，即使偶发失配（抖动导致）也继续显示，
        // 直到 MissedCount 超过 maxMissed 才删除，避免框在抖动时瞬间消失造成闪烁。
        for (int j = _tracks.Count - 1; j >= 0; j--)
        {
            if (matchedTrackIdx.Contains(j)) continue;
            _tracks[j].MissedCount++;
            _tracks[j].Confidence *= _missDecay;
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
            // 已确认过（连续命中 confirmHits 帧）且未真正丢失的轨道才显示
            if (t.EverConfirmed)
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

    /// <summary>
    /// 获取当前可渲染的平滑检测对象（已通过确认机制的）。<b>仅实时检测路径使用</b>：
    /// 在平滑框/置信度的基础上，保留原始 DetectedObject 的类别、关键点、分割掩膜等附加信息。
    /// </summary>
    public List<DetectedObject> GetActiveObjects()
    {
        var result = new List<DetectedObject>(_tracks.Count);
        foreach (var t in _tracks)
        {
            // 已确认过（连续命中 confirmHits 帧）且未真正丢失的轨道才显示
            if (!t.EverConfirmed) continue;
            if (t.Raw is not DetectedObject src) continue;
            result.Add(new DetectedObject
            {
                ClassId = src.ClassId,
                ClassName = t.Label,
                Confidence = (float)t.Confidence,
                BoundingBox = new float[] { t.Box.Left, t.Box.Top, t.Box.Width, t.Box.Height },
                PixelBoundingBox = new SKRectI(
                    (int)Math.Round(t.Box.Left), (int)Math.Round(t.Box.Top),
                    (int)Math.Round(t.Box.Right), (int)Math.Round(t.Box.Bottom)),
                Mask = src.Mask,
                KeyPoints = t.SmoothedKeyPoints ?? src.KeyPoints,
                IsInRoi = src.IsInRoi,
                RoiId = src.RoiId
            });
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

    /// <summary>
    /// 综合匹配得分（0~1），用于相邻帧同一目标的关联。
    /// <para>纯 IoU 在框抖动时易低于阈值导致失配（框闪烁）。这里在 IoU 之外，
    /// 额外用「中心点归一化距离 + 尺寸相似度」兜底：抖动场景下中心点基本不动、尺寸不变，
    /// 仍能取得较高得分而稳定匹配。</para>
    /// </summary>
    private static float MatchScore(SKRect a, SKRect b)
    {
        float iou = IoU(a, b);

        // 中心点归一化距离（0=重合，越大越远；以平均尺寸为基准）
        float acx = (a.Left + a.Right) / 2f, acy = (a.Top + a.Bottom) / 2f;
        float bcx = (b.Left + b.Right) / 2f, bcy = (b.Top + b.Bottom) / 2f;
        float dx = acx - bcx, dy = acy - bcy;
        float dist = MathF.Sqrt(dx * dx + dy * dy);
        float refSize = 0.5f * (a.Width + a.Height + b.Width + b.Height);
        float normDist = refSize > 0 ? dist / refSize : 1f;
        float centerScore = MathF.Max(0f, 1f - normDist * 2f); // normDist<0.5 才有分

        // 尺寸相似度（0~1，1=完全相同）
        float wSim = MathF.Min(a.Width / MathF.Max(b.Width, 1e-3f), b.Width / MathF.Max(a.Width, 1e-3f));
        float hSim = MathF.Min(a.Height / MathF.Max(b.Height, 1e-3f), b.Height / MathF.Max(a.Height, 1e-3f));
        float sizeSim = (float)Math.Clamp(wSim * hSim, 0.0, 1.0);

        // 综合：IoU 优先；否则依赖「中心点近且尺寸相似」
        return MathF.Max(iou, centerScore * (0.5f + 0.5f * sizeSim));
    }

    private static SKRect Lerp(SKRect a, SKRect b, float t)
        => new(
            a.Left + (b.Left - a.Left) * t,
            a.Top + (b.Top - a.Top) * t,
            a.Right + (b.Right - a.Right) * t,
            a.Bottom + (b.Bottom - a.Bottom) * t);
}
