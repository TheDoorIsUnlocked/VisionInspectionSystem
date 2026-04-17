using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace VisionInspection.Core.Services
{
    /// <summary>
    /// 相机管理器 - 单例模式，统一管理所有相机
    /// 参考VM程序架构：相机图像存储在管理器中，通过事件通知更新
    /// </summary>
    public class CameraManager
    {
        private static CameraManager? _instance;
        private static readonly object _lock = new object();

        /// <summary>
        /// 获取相机管理器实例
        /// </summary>
        public static CameraManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        _instance ??= new CameraManager();
                    }
                }
                return _instance;
            }
        }

        private ICameraService? _currentCameraService;
        private CameraInfo? _currentCamera;
        private CameraImageData? _latestImageData;
        private readonly object _imageLock = new object();

        /// <summary>
        /// 当前相机服务
        /// </summary>
        public ICameraService? CurrentCameraService => _currentCameraService;

        /// <summary>
        /// 当前相机信息
        /// </summary>
        public CameraInfo? CurrentCamera => _currentCamera;

        /// <summary>
        /// 是否已连接相机
        /// </summary>
        public bool IsConnected => _currentCameraService?.IsConnected ?? false;

        /// <summary>
        /// 是否正在采集
        /// </summary>
        public bool IsGrabbing => _currentCameraService?.IsGrabbing ?? false;

        /// <summary>
        /// 最新图像数据 - 线程安全访问
        /// </summary>
        public CameraImageData? LatestImageData
        {
            get
            {
                lock (_imageLock)
                {
                    return _latestImageData;
                }
            }
        }

        /// <summary>
        /// 图像采集事件 - 通知图像已更新
        /// </summary>
        public event EventHandler<CameraImageData>? ImageGrabbed;

        /// <summary>
        /// 连接状态改变事件
        /// </summary>
        public event EventHandler<bool>? ConnectionStatusChanged;

        /// <summary>
        /// 错误事件
        /// </summary>
        public event EventHandler<string>? ErrorOccurred;

        /// <summary>
        /// 设置当前相机服务
        /// </summary>
        public void SetCameraService(ICameraService cameraService)
        {
            // 如果已有相机服务，先断开连接并取消订阅事件
            if (_currentCameraService != null)
            {
                _currentCameraService.ImageDataGrabbed -= OnImageDataGrabbed;
                _currentCameraService.ConnectionStatusChanged -= OnConnectionStatusChanged;
                _currentCameraService.ErrorOccurred -= OnErrorOccurred;
                
                if (_currentCameraService.IsGrabbing)
                    _currentCameraService.StopGrabbing();
                if (_currentCameraService.IsConnected)
                    _currentCameraService.Disconnect();
                
                _currentCameraService.Dispose();
            }

            _currentCameraService = cameraService;
            
            // 订阅新相机服务的事件
            if (_currentCameraService != null)
            {
                _currentCameraService.ImageDataGrabbed += OnImageDataGrabbed;
                _currentCameraService.ConnectionStatusChanged += OnConnectionStatusChanged;
                _currentCameraService.ErrorOccurred += OnErrorOccurred;
            }
        }

        /// <summary>
        /// 连接相机
        /// </summary>
        public async Task<bool> ConnectAsync(CameraInfo camera)
        {
            if (_currentCameraService == null)
                return false;

            _currentCamera = camera;
            return await _currentCameraService.ConnectAsync(camera);
        }

        /// <summary>
        /// 断开相机连接
        /// </summary>
        public void Disconnect()
        {
            _currentCameraService?.Disconnect();
            _currentCamera = null;
        }

        /// <summary>
        /// 开始采集
        /// </summary>
        public async Task<bool> StartGrabbingAsync()
        {
            return await (_currentCameraService?.StartGrabbingAsync() ?? Task.FromResult(false));
        }

        /// <summary>
        /// 停止采集
        /// </summary>
        public void StopGrabbing()
        {
            _currentCameraService?.StopGrabbing();
        }

        /// <summary>
        /// 设置曝光时间
        /// </summary>
        public async Task<bool> SetExposureTimeAsync(float exposureTime)
        {
            if (_currentCameraService == null)
                return false;
            return await _currentCameraService.SetExposureTimeAsync(exposureTime);
        }

        /// <summary>
        /// 设置增益
        /// </summary>
        public async Task<bool> SetGainAsync(float gain)
        {
            if (_currentCameraService == null)
                return false;
            return await _currentCameraService.SetGainAsync(gain);
        }

        /// <summary>
        /// 获取曝光时间
        /// </summary>
        public async Task<float> GetExposureTimeAsync()
        {
            if (_currentCameraService == null)
                return 0;
            return await _currentCameraService.GetExposureTimeAsync();
        }

        /// <summary>
        /// 获取增益
        /// </summary>
        public async Task<float> GetGainAsync()
        {
            if (_currentCameraService == null)
                return 0;
            return await _currentCameraService.GetGainAsync();
        }

        /// <summary>
        /// 获取曝光时间范围
        /// </summary>
        public async Task<(float Min, float Max)> GetExposureTimeRangeAsync()
        {
            if (_currentCameraService == null)
                return (0, 0);
            return await _currentCameraService.GetExposureTimeRangeAsync();
        }

        /// <summary>
        /// 获取增益范围
        /// </summary>
        public async Task<(float Min, float Max)> GetGainRangeAsync()
        {
            if (_currentCameraService == null)
                return (0, 0);
            return await _currentCameraService.GetGainRangeAsync();
        }

        /// <summary>        /// <summary>
        /// 枚举可用相机
        /// </summary>
        public async Task<List<CameraInfo>> EnumCamerasAsync()
        {
            if (_currentCameraService == null)
                return new List<CameraInfo>();
            return await _currentCameraService.EnumCamerasAsync();
        }

        /// <summary>
        /// 安全关闭相机服务 - 用于程序退出时
        /// </summary>
        public void Shutdown()
        {
            try
            {
                System.Diagnostics.Debug.WriteLine("CameraManager开始关闭...");
                
                // 停止采集
                if (IsGrabbing)
                {
                    System.Diagnostics.Debug.WriteLine("正在停止相机采集...");
                    StopGrabbing();
                }
                
                // 断开连接
                if (IsConnected)
                {
                    System.Diagnostics.Debug.WriteLine("正在断开相机连接...");
                    Disconnect();
                }
                
                // 释放当前相机服务
                if (_currentCameraService != null)
                {
                    System.Diagnostics.Debug.WriteLine("正在释放相机服务...");
                    _currentCameraService.ImageDataGrabbed -= OnImageDataGrabbed;
                    _currentCameraService.ConnectionStatusChanged -= OnConnectionStatusChanged;
                    _currentCameraService.ErrorOccurred -= OnErrorOccurred;
                    _currentCameraService.Dispose();
                    _currentCameraService = null;
                }
                
                _currentCamera = null;
                _latestImageData = null;
                
                System.Diagnostics.Debug.WriteLine("CameraManager已完全关闭");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"CameraManager关闭时发生异常: {ex.Message}");
            }
        }

        private void OnImageDataGrabbed(object? sender, CameraImageData e)
        {
            // 存储最新图像数据
            lock (_imageLock)
            {
                _latestImageData = e;
            }
            // 通知订阅者图像已更新
            ImageGrabbed?.Invoke(this, e);
        }

        private void OnConnectionStatusChanged(object? sender, bool e)
        {
            ConnectionStatusChanged?.Invoke(this, e);
        }

        private void OnErrorOccurred(object? sender, string e)
        {
            ErrorOccurred?.Invoke(this, e);
        }
    }
}
