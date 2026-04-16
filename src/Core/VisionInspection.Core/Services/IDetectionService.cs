using SkiaSharp;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace VisionInspection.Core.Services
{
    /// <summary>
    /// 检测结果
    /// </summary>
    public class DetectionResult
    {
        /// <summary>
        /// 检测到的对象列表
        /// </summary>
        public List<DetectedObject> Objects { get; set; } = new();

        /// <summary>
        /// 处理时间（毫秒）
        /// </summary>
        public double ProcessingTimeMs { get; set; }

        /// <summary>
        /// 原始图像宽度
        /// </summary>
        public int ImageWidth { get; set; }

        /// <summary>
        /// 原始图像高度
        /// </summary>
        public int ImageHeight { get; set; }

        /// <summary>
        /// 检测时间戳
        /// </summary>
        public DateTime Timestamp { get; set; } = DateTime.Now;
    }

    /// <summary>
    /// 检测到的对象
    /// </summary>
    public class DetectedObject
    {
        /// <summary>
        /// 类别ID
        /// </summary>
        public int ClassId { get; set; }

        /// <summary>
        /// 类别名称
        /// </summary>
        public string ClassName { get; set; } = "";

        /// <summary>
        /// 置信度
        /// </summary>
        public float Confidence { get; set; }

        /// <summary>
        /// 边界框（归一化坐标 x, y, width, height）
        /// </summary>
        public float[] BoundingBox { get; set; } = new float[4];

        /// <summary>
        /// 边界框（像素坐标）
        /// </summary>
        public SKRectI PixelBoundingBox { get; set; }

        /// <summary>
        /// 分割掩码（仅分割模型）
        /// </summary>
        public byte[]? Mask { get; set; }

        /// <summary>
        /// 关键点（仅姿态估计模型）
        /// </summary>
        public List<KeyPoint>? KeyPoints { get; set; }

        /// <summary>
        /// 是否在ROI区域内
        /// </summary>
        public bool IsInRoi { get; set; } = true;

        /// <summary>
        /// 所在的ROI区域ID
        /// </summary>
        public string? RoiId { get; set; }
    }

    /// <summary>
    /// 关键点
    /// </summary>
    public class KeyPoint
    {
        /// <summary>
        /// 点索引
        /// </summary>
        public int Index { get; set; }

        /// <summary>
        /// 点名称
        /// </summary>
        public string Name { get; set; } = "";

        /// <summary>
        /// X坐标
        /// </summary>
        public float X { get; set; }

        /// <summary>
        /// Y坐标
        /// </summary>
        public float Y { get; set; }

        /// <summary>
        /// 置信度
        /// </summary>
        public float Confidence { get; set; }
    }

    /// <summary>
    /// 检测服务接口
    /// </summary>
    public interface IDetectionService
    {
        /// <summary>
        /// 是否已初始化
        /// </summary>
        bool IsInitialized { get; }

        /// <summary>
        /// 初始化检测服务
        /// </summary>
        Task<bool> InitializeAsync(ModelInfo modelInfo);

        /// <summary>
        /// 释放资源
        /// </summary>
        void Dispose();

        /// <summary>
        /// 对图像进行检测
        /// </summary>
        Task<DetectionResult> DetectAsync(SKBitmap image);

        /// <summary>
        /// 对图像进行检测（带ROI过滤）
        /// </summary>
        Task<DetectionResult> DetectAsync(SKBitmap image, List<ROIInfo> rois);

        /// <summary>
        /// 设置置信度阈值
        /// </summary>
        void SetConfidenceThreshold(float threshold);

        /// <summary>
        /// 设置IoU阈值
        /// </summary>
        void SetIouThreshold(float threshold);

        /// <summary>
        /// 获取支持的类别列表
        /// </summary>
        List<string> GetClasses();

        /// <summary>
        /// 检测结果事件
        /// </summary>
        event EventHandler<DetectionResult>? DetectionCompleted;

        /// <summary>
        /// 检测错误事件
        /// </summary>
        event EventHandler<string>? DetectionError;
    }
}
