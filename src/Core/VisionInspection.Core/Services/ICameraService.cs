using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace VisionInspection.Core.Services
{
    /// <summary>
    /// 相机服务接口
    /// </summary>
    public interface ICameraService : IDisposable
    {
        /// <summary>
        /// 是否已连接
        /// </summary>
        bool IsConnected { get; }

        /// <summary>
        /// 是否正在采集
        /// </summary>
        bool IsGrabbing { get; }

        /// <summary>
        /// 当前相机信息
        /// </summary>
        CameraInfo CurrentCamera { get; }

        /// <summary>
        /// 连接状态改变事件
        /// </summary>
        event EventHandler<bool> ConnectionStatusChanged;

        /// <summary>
        /// 图像采集事件
        /// </summary>
        event EventHandler<byte[]> ImageGrabbed;

        /// <summary>
        /// 错误事件
        /// </summary>
        event EventHandler<string> ErrorOccurred;

        /// <summary>
        /// 枚举可用相机
        /// </summary>
        Task<List<CameraInfo>> EnumCamerasAsync();

        /// <summary>
        /// 连接相机
        /// </summary>
        Task<bool> ConnectAsync(CameraInfo camera);

        /// <summary>
        /// 断开连接
        /// </summary>
        void Disconnect();

        /// <summary>
        /// 开始采集
        /// </summary>
        Task<bool> StartGrabbingAsync();

        /// <summary>
        /// 停止采集
        /// </summary>
        void StopGrabbing();

        /// <summary>
        /// 设置曝光时间
        /// </summary>
        Task<bool> SetExposureTimeAsync(float exposureTime);

        /// <summary>
        /// 设置增益
        /// </summary>
        Task<bool> SetGainAsync(float gain);

        /// <summary>
        /// 获取曝光时间
        /// </summary>
        Task<float> GetExposureTimeAsync();

        /// <summary>
        /// 获取增益
        /// </summary>
        Task<float> GetGainAsync();
    }

    /// <summary>
    /// 相机信息
    /// </summary>
    public class CameraInfo
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Model { get; set; } = "";
        public string SerialNumber { get; set; } = "";
        public string InterfaceType { get; set; } = "";
        public uint Index { get; set; }
        public uint InterfaceIndex { get; set; }
        public uint Type { get; set; }
        public string DisplayName { get; set; } = "";
        public object ExtInfo { get; set; }
    }
}
