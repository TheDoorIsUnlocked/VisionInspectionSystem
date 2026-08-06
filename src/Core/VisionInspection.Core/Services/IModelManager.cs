using System.Collections.Generic;
using System.Threading.Tasks;

namespace VisionInspection.Core.Services
{
    /// <summary>
    /// YOLO模型信息
    /// </summary>
    public class ModelInfo
    {
        /// <summary>
        /// 模型ID
        /// </summary>
        public string Id { get; set; } = "";

        /// <summary>
        /// 模型名称
        /// </summary>
        public string Name { get; set; } = "";

        /// <summary>
        /// 模型描述
        /// </summary>
        public string Description { get; set; } = "";

        /// <summary>
        /// 模型文件路径
        /// </summary>
        public string ModelPath { get; set; } = "";

        /// <summary>
        /// 模型类型
        /// </summary>
        public ModelType Type { get; set; } = ModelType.ObjectDetection;

        /// <summary>
        /// 类别列表
        /// </summary>
        public List<string> Classes { get; set; } = new();

        /// <summary>
        /// 输入尺寸
        /// </summary>
        public int InputSize { get; set; } = 640;

        /// <summary>
        /// 置信度阈值
        /// </summary>
        public float ConfidenceThreshold { get; set; } = 0.5f;

        /// <summary>
        /// NMS IoU阈值
        /// </summary>
        public float IouThreshold { get; set; } = 0.45f;

        /// <summary>
        /// 实时检测平滑系数（EMA）：越小轨迹越稳，越大越跟手。范围 0.05~0.5
        /// </summary>
        public float SmoothEma { get; set; } = 0.2f;

        /// <summary>
        /// 实时检测稳定帧数：新目标连续命中多少帧后才显示。越大越能过滤单帧噪点。
        /// </summary>
        public int SmoothConfirmHits { get; set; } = 2;

        /// <summary>
        /// 实时检测丢失保持帧数：目标丢失后仍保持显示的帧数。越大越不容易忽隐忽现。
        /// </summary>
        public int SmoothMaxMissed { get; set; } = 5;

        /// <summary>
        /// 实时检测跟踪 IoU 阈值：相邻帧两个框 IoU 大于此值才认为是同一目标。
        /// </summary>
        public float SmoothIouThreshold { get; set; } = 0.3f;

        /// <summary>
        /// 是否使用GPU
        /// </summary>
        public bool UseGpu { get; set; } = true;

        /// <summary>
        /// GPU设备ID
        /// </summary>
        public int GpuId { get; set; } = 0;

        /// <summary>
        /// 创建时间
        /// </summary>
        public DateTime CreatedAt { get; set; } = DateTime.Now;

        /// <summary>
        /// 最后修改时间
        /// </summary>
        public DateTime UpdatedAt { get; set; } = DateTime.Now;
    }

    /// <summary>
    /// 模型类型
    /// </summary>
    public enum ModelType
    {
        ObjectDetection,
        Segmentation,
        Classification,
        PoseEstimation,
        OBBDetection
    }

    /// <summary>
    /// 模型管理器接口
    /// </summary>
    public interface IModelManager
    {
        /// <summary>
        /// 获取所有模型
        /// </summary>
        Task<List<ModelInfo>> GetAllModelsAsync();

        /// <summary>
        /// 根据ID获取模型
        /// </summary>
        Task<ModelInfo?> GetModelAsync(string id);

        /// <summary>
        /// 添加模型
        /// </summary>
        Task<bool> AddModelAsync(ModelInfo model);

        /// <summary>
        /// 更新模型
        /// </summary>
        Task<bool> UpdateModelAsync(ModelInfo model);

        /// <summary>
        /// 删除模型
        /// </summary>
        Task<bool> DeleteModelAsync(string id);

        /// <summary>
        /// 加载模型
        /// </summary>
        Task<bool> LoadModelAsync(string id);

        /// <summary>
        /// 卸载当前模型
        /// </summary>
        Task<bool> UnloadModelAsync();

        /// <summary>
        /// 获取当前加载的模型
        /// </summary>
        ModelInfo? CurrentModel { get; }

        /// <summary>
        /// 当前模型是否已加载
        /// </summary>
        bool IsModelLoaded { get; }

        /// <summary>
        /// 模型加载状态改变事件
        /// </summary>
        event EventHandler<bool>? ModelLoadedStateChanged;

        /// <summary>
        /// 模型列表改变事件
        /// </summary>
        event EventHandler? ModelListChanged;
    }
}
