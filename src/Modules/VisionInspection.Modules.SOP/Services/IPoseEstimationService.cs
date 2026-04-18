using SkiaSharp;
using VisionInspection.Modules.SOP.Models;
using YoloDotNet;
using YoloDotNet.Enums;
using YoloDotNet.ExecutionProvider.Cuda;
using YoloDotNet.ExecutionProvider.Cpu;
using YoloDotNet.Models;

// 使用YoloOptions的完整命名空间
using YoloOptions = YoloDotNet.Models.YoloOptions;

namespace VisionInspection.Modules.SOP.Services;

/// <summary>
/// 姿态估计服务接口
/// </summary>
public interface IPoseEstimationService
{
    /// <summary>
    /// 检测图像中的人体并估计姿态
    /// </summary>
    Task<List<HumanPose>> DetectPosesAsync(SKBitmap image);

    /// <summary>
    /// 对检测到的人体进行姿态估计
    /// </summary>
    Task<HumanPose?> EstimatePoseAsync(SKBitmap image, SKRect boundingBox);

    /// <summary>
    /// 是否已初始化
    /// </summary>
    bool IsInitialized { get; }

    /// <summary>
    /// 初始化服务
    /// </summary>
    Task InitializeAsync(PoseEstimationConfig config);
}

/// <summary>
/// 基于YOLO的姿态估计服务（使用YOLO-Pose模型）
/// </summary>
public class YoloPoseEstimationService : IPoseEstimationService
{
    private YoloDotNet.Yolo? _yolo;
    private PoseEstimationConfig _config = new();

    public bool IsInitialized => _yolo != null;

    public async Task InitializeAsync(PoseEstimationConfig config)
    {
        _config = config;

        if (!File.Exists(config.ModelPath))
        {
            throw new FileNotFoundException($"姿态估计模型文件不存在: {config.ModelPath}");
        }

        await Task.Run(() =>
        {
            var options = new YoloOptions
            {
                ExecutionProvider = new YoloDotNet.ExecutionProvider.Cuda.CudaExecutionProvider(config.ModelPath, 0),
                ImageResize = YoloDotNet.Enums.ImageResize.Proportional,
                SamplingOptions = new(SkiaSharp.SKFilterMode.Nearest, SkiaSharp.SKMipmapMode.None)
            };

            _yolo = new YoloDotNet.Yolo(options);
        });
    }

    public async Task<List<HumanPose>> DetectPosesAsync(SKBitmap image)
    {
        if (_yolo == null)
            throw new InvalidOperationException("姿态估计服务未初始化");

        return await Task.Run(() =>
        {
            var results = new List<HumanPose>();

            // 使用YOLO-Pose模型检测人体和关键点
            // 注意：这里假设模型输出包含姿态关键点
            // 实际实现需要根据具体的YOLO-Pose模型格式解析
            var detections = _yolo.RunObjectDetection(
                image,
                confidence: _config.ConfidenceThreshold);

            foreach (var detection in detections)
            {
                var pose = new HumanPose
                {
                    BoundingBox = detection.BoundingBox,
                    Timestamp = DateTime.Now
                };

                // 这里需要解析模型的关键点输出
                // 暂时使用模拟数据，实际应根据模型输出解析
                pose.Keypoints = ParseKeypointsFromDetection(detection);

                results.Add(pose);
            }

            return results;
        });
    }

    public async Task<HumanPose?> EstimatePoseAsync(SKBitmap image, SKRect boundingBox)
    {
        var poses = await DetectPosesAsync(image);
        return poses.FirstOrDefault(p =>
            Math.Abs(p.BoundingBox.MidX - boundingBox.MidX) < 50 &&
            Math.Abs(p.BoundingBox.MidY - boundingBox.MidY) < 50);
    }

    /// <summary>
    /// 从检测结果解析关键点（需要根据实际模型格式调整）
    /// </summary>
    private List<Keypoint> ParseKeypointsFromDetection(YoloDotNet.Models.ObjectDetection detection)
    {
        // 这里应该解析模型的关键点输出
        // 暂时返回空列表，实际实现需要根据YOLO-Pose模型格式
        return new List<Keypoint>();
    }
}

/// <summary>
/// 模拟姿态估计服务（用于测试）
/// </summary>
public class MockPoseEstimationService : IPoseEstimationService
{
    private readonly Random _random = new();

    public bool IsInitialized => true;

    public Task InitializeAsync(PoseEstimationConfig config)
    {
        return Task.CompletedTask;
    }

    public Task<List<HumanPose>> DetectPosesAsync(SKBitmap image)
    {
        // 模拟检测1-2个人体
        var count = _random.Next(1, 3);
        var poses = new List<HumanPose>();

        for (int i = 0; i < count; i++)
        {
            var pose = GenerateMockPose(i);
            poses.Add(pose);
        }

        return Task.FromResult(poses);
    }

    public Task<HumanPose?> EstimatePoseAsync(SKBitmap image, SKRect boundingBox)
    {
        var pose = GenerateMockPose(0);
        pose.BoundingBox = boundingBox;
        return Task.FromResult<HumanPose?>(pose);
    }

    private HumanPose GenerateMockPose(int trackId)
    {
        var pose = new HumanPose
        {
            TrackId = trackId,
            BoundingBox = new SKRect(100 + trackId * 200, 100, 300 + trackId * 200, 500),
            Timestamp = DateTime.Now
        };

        // 生成17个关键点
        var baseX = 200f + trackId * 200;
        var baseY = 200f;

        pose.Keypoints = new List<Keypoint>
        {
            new(KeypointType.Nose, baseX, baseY - 80, 0.95f),
            new(KeypointType.LeftEye, baseX - 15, baseY - 85, 0.92f),
            new(KeypointType.RightEye, baseX + 15, baseY - 85, 0.93f),
            new(KeypointType.LeftEar, baseX - 30, baseY - 80, 0.88f),
            new(KeypointType.RightEar, baseX + 30, baseY - 80, 0.89f),
            new(KeypointType.LeftShoulder, baseX - 40, baseY - 30, 0.95f),
            new(KeypointType.RightShoulder, baseX + 40, baseY - 30, 0.96f),
            new(KeypointType.LeftElbow, baseX - 60, baseY + 30, 0.90f),
            new(KeypointType.RightElbow, baseX + 60, baseY + 30, 0.91f),
            new(KeypointType.LeftWrist, baseX - 80, baseY + 80, 0.85f),
            new(KeypointType.RightWrist, baseX + 80, baseY + 80, 0.87f),
            new(KeypointType.LeftHip, baseX - 30, baseY + 80, 0.94f),
            new(KeypointType.RightHip, baseX + 30, baseY + 80, 0.95f),
            new(KeypointType.LeftKnee, baseX - 35, baseY + 150, 0.88f),
            new(KeypointType.RightKnee, baseX + 35, baseY + 150, 0.89f),
            new(KeypointType.LeftAnkle, baseX - 40, baseY + 220, 0.82f),
            new(KeypointType.RightAnkle, baseX + 40, baseY + 220, 0.83f)
        };

        return pose;
    }
}

/// <summary>
/// 人体跟踪器（基于IoU匹配）
/// </summary>
public class HumanTracker
{
    private readonly Dictionary<int, TrackedHuman> _tracks = new();
    private int _nextId = 1;
    private readonly float _iouThreshold;

    public HumanTracker(float iouThreshold = 0.3f)
    {
        _iouThreshold = iouThreshold;
    }

    /// <summary>
    /// 更新跟踪器
    /// </summary>
    public List<TrackedHuman> Update(List<HumanPose> detections)
    {
        var matchedTracks = new Dictionary<int, TrackedHuman>();
        var unmatchedDetections = new List<HumanPose>(detections);

        // 计算IoU并匹配
        foreach (var (trackId, track) in _tracks)
        {
            float bestIou = 0;
            HumanPose? bestDetection = null;

            foreach (var detection in unmatchedDetections)
            {
                var iou = CalculateIoU(track.LastBoundingBox, detection.BoundingBox);
                if (iou > bestIou)
                {
                    bestIou = iou;
                    bestDetection = detection;
                }
            }

            if (bestIou >= _iouThreshold && bestDetection != null)
            {
                track.Update(bestDetection);
                matchedTracks[trackId] = track;
                unmatchedDetections.Remove(bestDetection);
            }
        }

        // 为未匹配的检测创建新跟踪
        foreach (var detection in unmatchedDetections)
        {
            var newTrack = new TrackedHuman(_nextId++, detection);
            matchedTracks[newTrack.Id] = newTrack;
        }

        _tracks.Clear();
        foreach (var (id, track) in matchedTracks)
        {
            _tracks[id] = track;
        }

        return _tracks.Values.ToList();
    }

    /// <summary>
    /// 计算两个边界框的IoU
    /// </summary>
    private float CalculateIoU(SKRect box1, SKRect box2)
    {
        var x1 = Math.Max(box1.Left, box2.Left);
        var y1 = Math.Max(box1.Top, box2.Top);
        var x2 = Math.Min(box1.Right, box2.Right);
        var y2 = Math.Min(box1.Bottom, box2.Bottom);

        if (x2 <= x1 || y2 <= y1) return 0;

        var intersection = (x2 - x1) * (y2 - y1);
        var area1 = box1.Width * box1.Height;
        var area2 = box2.Width * box2.Height;
        var union = area1 + area2 - intersection;

        return union > 0 ? intersection / union : 0;
    }

    /// <summary>
    /// 获取指定ID的跟踪人体
    /// </summary>
    public TrackedHuman? GetTrack(int trackId)
    {
        return _tracks.TryGetValue(trackId, out var track) ? track : null;
    }

    /// <summary>
    /// 清除所有跟踪
    /// </summary>
    public void Clear()
    {
        _tracks.Clear();
        _nextId = 1;
    }
}

/// <summary>
/// 被跟踪的人体
/// </summary>
public class TrackedHuman
{
    public int Id { get; }
    public HumanPose CurrentPose { get; private set; }
    public SKRect LastBoundingBox => CurrentPose.BoundingBox;
    public DateTime LastUpdateTime { get; private set; }
    public int FrameCount { get; private set; }
    public List<HumanPose> History { get; } = new();

    public TrackedHuman(int id, HumanPose initialPose)
    {
        Id = id;
        CurrentPose = initialPose;
        LastUpdateTime = DateTime.Now;
        FrameCount = 1;
        History.Add(initialPose);
    }

    public void Update(HumanPose newPose)
    {
        CurrentPose = newPose;
        CurrentPose.TrackId = Id;
        LastUpdateTime = DateTime.Now;
        FrameCount++;
        History.Add(newPose);

        // 限制历史记录长度
        if (History.Count > 30)
        {
            History.RemoveAt(0);
        }
    }
}
