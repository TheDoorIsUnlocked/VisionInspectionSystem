namespace VisionInspection.Core.Services
{
    /// <summary>
    /// ROI形状类型
    /// </summary>
    public enum ShapeType
    {
        Rectangle,
        Circle,
        Polygon
    }

    /// <summary>
    /// 2D点
    /// </summary>
    public struct Point2D
    {
        public float X { get; set; }
        public float Y { get; set; }

        public Point2D(float x, float y)
        {
            X = x;
            Y = y;
        }
    }

    /// <summary>
    /// ROI信息
    /// </summary>
    public class ROIInfo
    {
        /// <summary>
        /// ROI ID
        /// </summary>
        public string Id { get; set; } = "";

        /// <summary>
        /// ROI名称
        /// </summary>
        public string Name { get; set; } = "";

        /// <summary>
        /// 形状类型
        /// </summary>
        public ShapeType ShapeType { get; set; } = ShapeType.Rectangle;

        /// <summary>
        /// X坐标（左上角）
        /// </summary>
        public float X { get; set; }

        /// <summary>
        /// Y坐标（左上角）
        /// </summary>
        public float Y { get; set; }

        /// <summary>
        /// 宽度
        /// </summary>
        public float Width { get; set; }

        /// <summary>
        /// 高度
        /// </summary>
        public float Height { get; set; }

        /// <summary>
        /// 多边形点（仅多边形类型使用）
        /// </summary>
        public List<Point2D>? Points { get; set; }

        /// <summary>
        /// 是否启用
        /// </summary>
        public bool IsEnabled { get; set; } = true;

        /// <summary>
        /// 颜色（十六进制）
        /// </summary>
        public string Color { get; set; } = "#2196F3";

        /// <summary>
        /// 关联的检测类别（空表示检测所有类别）
        /// </summary>
        public List<string> TargetClasses { get; set; } = new();
    }
}
