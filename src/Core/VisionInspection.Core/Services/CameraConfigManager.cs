using System.Text.Json;
using VisionInspection.Core.Models;

namespace VisionInspection.Core.Services
{
    /// <summary>
    /// 相机配置管理器 - 负责保存和加载相机配置
    /// </summary>
    public class CameraConfigManager
    {
        private static CameraConfigManager? _instance;
        private static readonly object _lock = new object();
        private readonly string _configFilePath;
        private CameraConfiguration? _cachedConfig;

        /// <summary>
        /// 获取相机配置管理器实例
        /// </summary>
        public static CameraConfigManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        _instance ??= new CameraConfigManager();
                    }
                }
                return _instance;
            }
        }

        private CameraConfigManager()
        {
            // 配置文件路径：程序运行目录下的 configs/camera_config.json
            var appDirectory = AppDomain.CurrentDomain.BaseDirectory;
            var configDirectory = Path.Combine(appDirectory, "configs");
            
            // 确保配置目录存在
            if (!Directory.Exists(configDirectory))
            {
                Directory.CreateDirectory(configDirectory);
            }
            
            _configFilePath = Path.Combine(configDirectory, "camera_config.json");
        }

        /// <summary>
        /// 加载相机配置
        /// </summary>
        public CameraConfiguration LoadConfig()
        {
            if (_cachedConfig != null)
            {
                return _cachedConfig;
            }

            try
            {
                if (File.Exists(_configFilePath))
                {
                    var json = File.ReadAllText(_configFilePath);
                    var config = JsonSerializer.Deserialize<CameraConfiguration>(json);
                    if (config != null)
                    {
                        _cachedConfig = config;
                        return config;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"加载相机配置失败: {ex.Message}");
            }

            // 返回默认配置
            _cachedConfig = new CameraConfiguration();
            return _cachedConfig;
        }

        /// <summary>
        /// 保存相机配置
        /// </summary>
        public void SaveConfig(CameraConfiguration config)
        {
            try
            {
                config.LastUpdated = DateTime.Now;
                _cachedConfig = config;
                
                var json = JsonSerializer.Serialize(config, new JsonSerializerOptions
                {
                    WriteIndented = true
                });
                
                File.WriteAllText(_configFilePath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"保存相机配置失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 更新最后使用的相机信息
        /// </summary>
        public void UpdateLastCamera(string cameraId, string cameraName, string serialNumber)
        {
            var config = LoadConfig();
            config.LastCameraId = cameraId;
            config.LastCameraName = cameraName;
            config.LastCameraSerialNumber = serialNumber;
            SaveConfig(config);
        }

        /// <summary>
        /// 更新相机参数
        /// </summary>
        public void UpdateCameraParameters(float exposureTime, float gain)
        {
            var config = LoadConfig();
            config.ExposureTime = exposureTime;
            config.Gain = gain;
            SaveConfig(config);
        }

        /// <summary>
        /// 更新参数范围
        /// </summary>
        public void UpdateParameterRanges(float exposureMin, float exposureMax, float gainMin, float gainMax)
        {
            var config = LoadConfig();
            config.ExposureTimeMin = exposureMin;
            config.ExposureTimeMax = exposureMax;
            config.GainMin = gainMin;
            config.GainMax = gainMax;
            SaveConfig(config);
        }

        /// <summary>
        /// 保存多相机槽位配置列表
        /// </summary>
        public void SaveCameras(List<CameraSlotConfig> cameras)
        {
            var config = LoadConfig();
            config.Cameras = cameras ?? new List<CameraSlotConfig>();
            SaveConfig(config);
        }

        /// <summary>
        /// 加载多相机槽位配置列表（无配置时返回空列表）
        /// </summary>
        public List<CameraSlotConfig> LoadCameras()
        {
            var config = LoadConfig();
            return config.Cameras ?? new List<CameraSlotConfig>();
        }

        /// <summary>
        /// 清除配置缓存，强制下次从文件重新加载
        /// </summary>
        public void ClearCache()
        {
            _cachedConfig = null;
        }
    }
}
