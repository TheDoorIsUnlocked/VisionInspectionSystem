namespace VisionInspection.Core.Models
{
    /// <summary>
    /// 相机配置 - 用于保存和恢复相机设置
    /// </summary>
    public class CameraConfiguration
    {
        /// <summary>
        /// 最后使用的相机ID
        /// </summary>
        public string LastCameraId { get; set; } = "";

        /// <summary>
        /// 最后使用的相机名称
        /// </summary>
        public string LastCameraName { get; set; } = "";

        /// <summary>
        /// 最后使用的相机序列号
        /// </summary>
        public string LastCameraSerialNumber { get; set; } = "";

        /// <summary>
        /// 曝光时间
        /// </summary>
        public float ExposureTime { get; set; } = 5000;

        /// <summary>
        /// 增益
        /// </summary>
        public float Gain { get; set; } = 0;

        /// <summary>
        /// 曝光时间范围 - 最小值
        /// </summary>
        public float ExposureTimeMin { get; set; } = 20;

        /// <summary>
        /// 曝光时间范围 - 最大值
        /// </summary>
        public float ExposureTimeMax { get; set; } = 10000000;

        /// <summary>
        /// 增益范围 - 最小值
        /// </summary>
        public float GainMin { get; set; } = 0;

        /// <summary>
        /// 增益范围 - 最大值
        /// </summary>
        public float GainMax { get; set; } = 20;

        /// <summary>
        /// 是否自动曝光
        /// </summary>
        public bool AutoExposure { get; set; } = false;

        /// <summary>
        /// 是否自动增益
        /// </summary>
        public bool AutoGain { get; set; } = false;

        /// <summary>
        /// 帧率
        /// </summary>
        public float FrameRate { get; set; } = 30;

        /// <summary>
        /// 图像宽度
        /// </summary>
        public int ImageWidth { get; set; } = 1920;

        /// <summary>
        /// 图像高度
        /// </summary>
        public int ImageHeight { get; set; } = 1080;

        /// <summary>
        /// 最后更新时间
        /// </summary>
        public DateTime LastUpdated { get; set; } = DateTime.Now;
    }
}
