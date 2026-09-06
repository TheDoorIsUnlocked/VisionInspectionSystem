using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace VisionInspection.Core.Services
{
    /// <summary>
    /// 一路相机的运行槽位
    /// </summary>
    public class CameraSlot
    {
        /// <summary>相机 ID（主相机固定 "main_camera"，其余 "cam_2".."cam_4"）</summary>
        public string CameraId { get; init; } = "";
        public string DisplayName { get; set; } = "";
        public bool IsPrimary { get; init; }
        public ICameraService? Service { get; internal set; }
        public CameraInfo? CameraInfo { get; internal set; }
        public bool IsConnected { get; internal set; }
        public bool IsGrabbing { get; internal set; }
        public CameraImageData? LatestImageData { get; internal set; }
        public DateTime LastFrameTime { get; internal set; } = DateTime.MinValue;
    }

    /// <summary>
    /// 统一帧路由事件参数
    /// </summary>
    public class CameraFrameEventArgs : EventArgs
    {
        public string CameraId { get; }
        public CameraImageData ImageData { get; }
        public DateTime Timestamp { get; }

        public CameraFrameEventArgs(string cameraId, CameraImageData imageData, DateTime timestamp)
        {
            CameraId = cameraId;
            ImageData = imageData;
            Timestamp = timestamp;
        }
    }

    /// <summary>
    /// 槽位状态变化事件参数
    /// </summary>
    public class CameraSlotEventArgs : EventArgs
    {
        public string CameraId { get; }
        public bool IsConnected { get; }
        public bool IsGrabbing { get; }

        public CameraSlotEventArgs(string cameraId, bool isConnected, bool isGrabbing)
        {
            CameraId = cameraId;
            IsConnected = isConnected;
            IsGrabbing = isGrabbing;
        }
    }

    /// <summary>
    /// 相机管理器 - 单例模式，统一管理多路相机槽位
    /// 参考VM程序架构：相机图像存储在管理器中，通过事件通知更新
    /// 兼容策略：旧的单相机 API（SetCameraService/ConnectAsync/IsConnected 等）全部委托到主槽位（main_camera），
    /// 现有调用点（CameraConfigWindow、MainViewModel 等）零修改。
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

        /// <summary>最多支持的相机路数</summary>
        public const int MaxCameraCount = 4;

        /// <summary>主相机 ID（旧单相机 facade 指向的槽位）</summary>
        public const string PrimaryCameraId = "main_camera";

        private readonly Dictionary<string, CameraSlot> _slots = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _slotsLock = new();
        private CameraImageData? _latestImageData;
        private readonly object _imageLock = new object();

        /// <summary>
        /// 相机槽位快照（按注册顺序）
        /// </summary>
        public IReadOnlyList<CameraSlot> Slots
        {
            get
            {
                lock (_slotsLock)
                {
                    return _slots.Values.OrderBy(s => s.CameraId, StringComparer.OrdinalIgnoreCase).ToList();
                }
            }
        }

        /// <summary>
        /// 当前相机服务（旧 API：主槽位）
        /// </summary>
        public ICameraService? CurrentCameraService
        {
            get
            {
                var slot = GetSlot(PrimaryCameraId);
                return slot?.Service;
            }
        }

        /// <summary>
        /// 当前相机信息（旧 API：主槽位）
        /// </summary>
        public CameraInfo? CurrentCamera
        {
            get
            {
                var slot = GetSlot(PrimaryCameraId);
                return slot?.CameraInfo;
            }
        }

        /// <summary>
        /// 是否已连接相机（旧 API：主槽位；任一槽位连接也算连接）
        /// </summary>
        public bool IsConnected
        {
            get
            {
                lock (_slotsLock)
                {
                    return _slots.Values.Any(s => s.IsConnected);
                }
            }
        }

        /// <summary>
        /// 是否正在采集（旧 API：主槽位；任一槽位在采集也算采集）
        /// </summary>
        public bool IsGrabbing
        {
            get
            {
                lock (_slotsLock)
                {
                    return _slots.Values.Any(s => s.IsGrabbing);
                }
            }
        }

        /// <summary>
        /// 最新图像数据 - 线程安全访问（旧 API：主槽位）
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
        /// 图像采集事件 - 通知图像已更新（旧 API：仅主槽位触发，兼容现有订阅者）
        /// </summary>
        public event EventHandler<CameraImageData>? ImageGrabbed;

        /// <summary>
        /// 连接状态改变事件（旧 API：主槽位）
        /// </summary>
        public event EventHandler<bool>? ConnectionStatusChanged;

        /// <summary>
        /// 错误事件（旧 API：主槽位）
        /// </summary>
        public event EventHandler<string>? ErrorOccurred;

        /// <summary>
        /// 统一帧路由事件：每路相机帧到达都会触发，按 CameraId 区分
        /// </summary>
        public event EventHandler<CameraFrameEventArgs>? FrameGrabbed;

        /// <summary>
        /// 槽位连接/采集状态变化事件
        /// </summary>
        public event EventHandler<CameraSlotEventArgs>? SlotStatusChanged;

        // ==================== 槽位管理 ====================

        /// <summary>
        /// 注册一个相机槽位（幂等：已存在则返回现有槽位）
        /// </summary>
        public CameraSlot RegisterSlot(string cameraId, string displayName, bool isPrimary = false)
        {
            lock (_slotsLock)
            {
                if (_slots.TryGetValue(cameraId, out var existing))
                {
                    existing.DisplayName = displayName;
                    return existing;
                }

                var slot = new CameraSlot
                {
                    CameraId = cameraId,
                    DisplayName = displayName,
                    IsPrimary = isPrimary
                };
                _slots[cameraId] = slot;
                return slot;
            }
        }

        /// <summary>
        /// 注销槽位（断开 + 释放服务 + 移除）
        /// </summary>
        public void UnregisterSlot(string cameraId)
        {
            CameraSlot? slot;
            lock (_slotsLock)
            {
                if (!_slots.TryGetValue(cameraId, out slot)) return;
                _slots.Remove(cameraId);
            }

            ReleaseSlot(slot);
        }

        /// <summary>
        /// 获取槽位（不存在返回 null）
        /// </summary>
        public CameraSlot? GetSlot(string cameraId)
        {
            lock (_slotsLock)
            {
                return _slots.TryGetValue(cameraId, out var slot) ? slot : null;
            }
        }

        // ==================== 槽位连接/采集 ====================

        /// <summary>
        /// 绑定服务并连接槽位
        /// </summary>
        public async Task<bool> ConnectSlotAsync(string cameraId, ICameraService service, CameraInfo camera)
        {
            var slot = RegisterSlot(cameraId, camera.DisplayName, cameraId == PrimaryCameraId);

            lock (_slotsLock)
            {
                // 若该槽位已有服务，先释放旧服务
                if (slot.Service != null && !ReferenceEquals(slot.Service, service))
                {
                    var old = slot.Service;
                    old.ImageDataGrabbed -= OnImageDataGrabbed;
                    old.ConnectionStatusChanged -= OnConnectionStatusChanged;
                    old.ErrorOccurred -= OnErrorOccurred;
                    if (old.IsGrabbing) old.StopGrabbing();
                    if (old.IsConnected) old.Disconnect();
                    old.Dispose();
                    slot.Service = null;
                }

                slot.Service = service;
                slot.CameraInfo = camera;

                service.ImageDataGrabbed += OnImageDataGrabbed;
                service.ConnectionStatusChanged += OnConnectionStatusChanged;
                service.ErrorOccurred += OnErrorOccurred;
            }

            var ok = await service.ConnectAsync(camera);
            if (ok)
            {
                slot.IsConnected = true;
                NotifySlotStatus(slot);
            }
            return ok;
        }

        /// <summary>
        /// 断开槽位
        /// </summary>
        public void DisconnectSlot(string cameraId)
        {
            var slot = GetSlot(cameraId);
            if (slot == null) return;

            try
            {
                slot.Service?.Disconnect();
            }
            finally
            {
                slot.IsConnected = false;
                slot.IsGrabbing = false;
                NotifySlotStatus(slot);
            }
        }

        /// <summary>
        /// 开始槽位采集
        /// </summary>
        public async Task<bool> StartGrabbingSlotAsync(string cameraId)
        {
            var slot = GetSlot(cameraId);
            if (slot?.Service == null) return false;

            var ok = await slot.Service.StartGrabbingAsync();
            if (ok)
            {
                slot.IsGrabbing = true;
                NotifySlotStatus(slot);
            }
            return ok;
        }

        /// <summary>
        /// 停止槽位采集
        /// </summary>
        public void StopGrabbingSlot(string cameraId)
        {
            var slot = GetSlot(cameraId);
            if (slot == null) return;

            slot.Service?.StopGrabbing();
            slot.IsGrabbing = false;
            NotifySlotStatus(slot);
        }

        /// <summary>
        /// 开始所有已连接槽位采集
        /// </summary>
        public async Task StartAllGrabbingAsync()
        {
            foreach (var slot in Slots.Where(s => s.IsConnected && !s.IsGrabbing))
            {
                await StartGrabbingSlotAsync(slot.CameraId);
            }
        }

        /// <summary>
        /// 停止所有槽位采集
        /// </summary>
        public void StopAllGrabbing()
        {
            foreach (var slot in Slots)
            {
                StopGrabbingSlot(slot.CameraId);
            }
        }

        // ==================== 槽位参数 ====================

        /// <summary>
        /// 设置槽位曝光时间
        /// </summary>
        public async Task<bool> SetSlotExposureTimeAsync(string cameraId, float exposureTime)
        {
            var slot = GetSlot(cameraId);
            if (slot?.Service == null) return false;
            return await slot.Service.SetExposureTimeAsync(exposureTime);
        }

        /// <summary>
        /// 设置槽位增益
        /// </summary>
        public async Task<bool> SetSlotGainAsync(string cameraId, float gain)
        {
            var slot = GetSlot(cameraId);
            if (slot?.Service == null) return false;
            return await slot.Service.SetGainAsync(gain);
        }

        // ==================== 旧单相机 API（委托主槽位） ====================

        /// <summary>
        /// 设置当前相机服务（旧 API：主槽位）
        /// </summary>
        public void SetCameraService(ICameraService cameraService)
        {
            var slot = RegisterSlot(PrimaryCameraId, "主相机", isPrimary: true);
            lock (_slotsLock)
            {
                if (slot.Service != null && !ReferenceEquals(slot.Service, cameraService))
                {
                    var old = slot.Service;
                    old.ImageDataGrabbed -= OnImageDataGrabbed;
                    old.ConnectionStatusChanged -= OnConnectionStatusChanged;
                    old.ErrorOccurred -= OnErrorOccurred;
                    if (old.IsGrabbing) old.StopGrabbing();
                    if (old.IsConnected) old.Disconnect();
                    old.Dispose();
                }

                slot.Service = cameraService;
                if (cameraService != null)
                {
                    cameraService.ImageDataGrabbed += OnImageDataGrabbed;
                    cameraService.ConnectionStatusChanged += OnConnectionStatusChanged;
                    cameraService.ErrorOccurred += OnErrorOccurred;
                }
            }
        }

        /// <summary>
        /// 连接相机（旧 API：主槽位）
        /// </summary>
        public async Task<bool> ConnectAsync(CameraInfo camera)
        {
            var slot = GetSlot(PrimaryCameraId);
            if (slot?.Service == null) return false;

            slot.CameraInfo = camera;
            var ok = await slot.Service.ConnectAsync(camera);
            if (ok)
            {
                slot.IsConnected = true;
                NotifySlotStatus(slot);
            }
            return ok;
        }

        /// <summary>
        /// 断开相机连接（旧 API：主槽位）
        /// </summary>
        public void Disconnect()
        {
            DisconnectSlot(PrimaryCameraId);
        }

        /// <summary>
        /// 开始采集（旧 API：主槽位）
        /// </summary>
        public async Task<bool> StartGrabbingAsync()
        {
            return await StartGrabbingSlotAsync(PrimaryCameraId);
        }

        /// <summary>
        /// 停止采集（旧 API：主槽位）
        /// </summary>
        public void StopGrabbing()
        {
            StopGrabbingSlot(PrimaryCameraId);
        }

        /// <summary>
        /// 设置曝光时间（旧 API：主槽位）
        /// </summary>
        public async Task<bool> SetExposureTimeAsync(float exposureTime)
        {
            return await SetSlotExposureTimeAsync(PrimaryCameraId, exposureTime);
        }

        /// <summary>
        /// 设置增益（旧 API：主槽位）
        /// </summary>
        public async Task<bool> SetGainAsync(float gain)
        {
            return await SetSlotGainAsync(PrimaryCameraId, gain);
        }

        /// <summary>
        /// 获取曝光时间（旧 API：主槽位）
        /// </summary>
        public async Task<float> GetExposureTimeAsync()
        {
            var slot = GetSlot(PrimaryCameraId);
            if (slot?.Service == null) return 0;
            return await slot.Service.GetExposureTimeAsync();
        }

        /// <summary>
        /// 获取增益（旧 API：主槽位）
        /// </summary>
        public async Task<float> GetGainAsync()
        {
            var slot = GetSlot(PrimaryCameraId);
            if (slot?.Service == null) return 0;
            return await slot.Service.GetGainAsync();
        }

        /// <summary>
        /// 获取曝光时间范围（旧 API：主槽位）
        /// </summary>
        public async Task<(float Min, float Max)> GetExposureTimeRangeAsync()
        {
            var slot = GetSlot(PrimaryCameraId);
            if (slot?.Service == null) return (0, 0);
            return await slot.Service.GetExposureTimeRangeAsync();
        }

        /// <summary>
        /// 获取增益范围（旧 API：主槽位）
        /// </summary>
        public async Task<(float Min, float Max)> GetGainRangeAsync()
        {
            var slot = GetSlot(PrimaryCameraId);
            if (slot?.Service == null) return (0, 0);
            return await slot.Service.GetGainRangeAsync();
        }

        /// <summary>
        /// 枚举可用相机（旧 API：主槽位；无主槽位服务时返回空）
        /// </summary>
        public async Task<List<CameraInfo>> EnumCamerasAsync()
        {
            var slot = GetSlot(PrimaryCameraId);
            if (slot?.Service == null) return new List<CameraInfo>();
            return await slot.Service.EnumCamerasAsync();
        }

        // ==================== 生命周期 ====================

        /// <summary>
        /// 安全关闭所有相机服务 - 用于程序退出时
        /// </summary>
        public void Shutdown()
        {
            try
            {
                System.Diagnostics.Debug.WriteLine("CameraManager开始关闭...");

                lock (_slotsLock)
                {
                    foreach (var slot in _slots.Values.ToList())
                    {
                        ReleaseSlot(slot);
                    }
                    _slots.Clear();
                }

                _latestImageData = null;
                System.Diagnostics.Debug.WriteLine("CameraManager已完全关闭");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"CameraManager关闭时发生异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 释放单个槽位：停止采集、断开、取消订阅、释放服务
        /// </summary>
        private void ReleaseSlot(CameraSlot slot)
        {
            try
            {
                if (slot.Service != null)
                {
                    slot.Service.ImageDataGrabbed -= OnImageDataGrabbed;
                    slot.Service.ConnectionStatusChanged -= OnConnectionStatusChanged;
                    slot.Service.ErrorOccurred -= OnErrorOccurred;
                    if (slot.Service.IsGrabbing) slot.Service.StopGrabbing();
                    if (slot.Service.IsConnected) slot.Service.Disconnect();
                    slot.Service.Dispose();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"释放相机槽位 {slot.CameraId} 异常: {ex.Message}");
            }
            finally
            {
                slot.IsConnected = false;
                slot.IsGrabbing = false;
                slot.Service = null;
                slot.CameraInfo = null;
            }
        }

        private void NotifySlotStatus(CameraSlot slot)
        {
            SlotStatusChanged?.Invoke(this, new CameraSlotEventArgs(slot.CameraId, slot.IsConnected, slot.IsGrabbing));
            if (slot.CameraId == PrimaryCameraId)
            {
                ConnectionStatusChanged?.Invoke(this, slot.IsConnected);
            }
        }

        // ==================== 事件路由 ====================

        private CameraSlot? FindSlotByService(ICameraService service)
        {
            lock (_slotsLock)
            {
                return _slots.Values.FirstOrDefault(s => ReferenceEquals(s.Service, service));
            }
        }

        private void OnImageDataGrabbed(object? sender, CameraImageData e)
        {
            var slot = FindSlotByService(sender as ICameraService);
            if (slot == null) return;

            e.CameraId = slot.CameraId;
            e.CameraName = slot.DisplayName;

            lock (_imageLock)
            {
                slot.LatestImageData = e;
                slot.LastFrameTime = DateTime.Now;
                if (slot.CameraId == PrimaryCameraId)
                {
                    _latestImageData = e;
                }
            }

            FrameGrabbed?.Invoke(this, new CameraFrameEventArgs(slot.CameraId, e, DateTime.Now));
            if (slot.CameraId == PrimaryCameraId)
            {
                ImageGrabbed?.Invoke(this, e);
            }
        }

        private void OnConnectionStatusChanged(object? sender, bool e)
        {
            var slot = FindSlotByService(sender as ICameraService);
            if (slot == null)
            {
                ConnectionStatusChanged?.Invoke(this, e);
                return;
            }

            slot.IsConnected = e;
            if (!e) slot.IsGrabbing = false;
            NotifySlotStatus(slot);
        }

        private void OnErrorOccurred(object? sender, string e)
        {
            var slot = FindSlotByService(sender as ICameraService);
            if (slot != null && slot.CameraId != PrimaryCameraId)
            {
                ErrorOccurred?.Invoke(this, $"[{slot.DisplayName}] {e}");
                return;
            }
            ErrorOccurred?.Invoke(this, e);
        }
    }
}
