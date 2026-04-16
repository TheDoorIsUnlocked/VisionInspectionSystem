using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using MvFGCtrlC.NET;
using VisionInspection.Core.Services;

namespace VisionInspection.UI.Services
{
    /// <summary>
    /// 海康工业相机服务实现
    /// </summary>
    public class HikvisionCameraService : ICameraService
    {
        #region 字段

        private CSystem _system = new CSystem();
        private CInterface _interface = null;
        private CDevice _device = null;
        private CStream _stream = null;

        private bool _isInterfaceOpen = false;
        private bool _isDeviceOpen = false;
        private bool _isGrabbing = false;

        private Thread _grabThread = null;
        private bool _threadRunning = false;

        private readonly object _lockObject = new object();

        #endregion

        #region 属性

        public bool IsConnected => _isDeviceOpen;
        public bool IsGrabbing => _isGrabbing;
        public CameraInfo CurrentCamera { get; private set; }

        #endregion

        #region 事件

        public event EventHandler<byte[]> ImageGrabbed;
        public event EventHandler<bool> ConnectionStatusChanged;
        public event EventHandler<string> ErrorOccurred;

        #endregion

        #region 相机枚举

        /// <summary>
        /// 枚举可用相机
        /// </summary>
        public Task<List<CameraInfo>> EnumCamerasAsync()
        {
            return Task.Run(() =>
            {
                var cameras = new List<CameraInfo>();

                try
                {
                    // 更新接口列表
                    bool changed = false;
                    int nRet = _system.UpdateInterfaceList(
                        CParamDefine.MV_FG_GEV_INTERFACE | CParamDefine.MV_FG_CAMERALINK_INTERFACE |
                        CParamDefine.MV_FG_CXP_INTERFACE | CParamDefine.MV_FG_XoF_INTERFACE,
                        ref changed);

                    if (nRet != CErrorCode.MV_FG_SUCCESS)
                    {
                        ErrorOccurred?.Invoke(this, $"枚举接口失败，错误码：0x{nRet:X}");
                        return cameras;
                    }

                    // 获取接口数量
                    uint interfaceNum = 0;
                    nRet = _system.GetNumInterfaces(ref interfaceNum);
                    if (nRet != CErrorCode.MV_FG_SUCCESS)
                    {
                        ErrorOccurred?.Invoke(this, $"获取接口数量失败，错误码：0x{nRet:X}");
                        return cameras;
                    }

                    // 遍历所有接口
                    for (uint i = 0; i < interfaceNum; i++)
                    {
                        // 打开接口
                        nRet = _system.OpenInterface(i, out _interface);
                        if (nRet != CErrorCode.MV_FG_SUCCESS)
                        {
                            continue;
                        }

                        _isInterfaceOpen = true;

                        // 枚举设备
                        bool deviceChanged = false;
                        nRet = _interface.UpdateDeviceList(ref deviceChanged);
                        if (nRet != CErrorCode.MV_FG_SUCCESS)
                        {
                            _interface.CloseInterface();
                            _isInterfaceOpen = false;
                            continue;
                        }

                        // 获取设备数量
                        uint deviceNum = 0;
                        nRet = _interface.GetNumDevices(ref deviceNum);
                        if (nRet != CErrorCode.MV_FG_SUCCESS)
                        {
                            _interface.CloseInterface();
                            _isInterfaceOpen = false;
                            continue;
                        }

                        // 获取设备信息
                        for (uint j = 0; j < deviceNum; j++)
                        {
                            MV_FG_DEVICE_INFO info = new MV_FG_DEVICE_INFO();
                            nRet = _interface.GetDeviceInfo(j, ref info);
                            if (nRet == CErrorCode.MV_FG_SUCCESS)
                            {
                                var cameraInfo = ParseDeviceInfo(info, j);
                                cameras.Add(cameraInfo);
                            }
                        }

                        // 关闭接口
                        _interface.CloseInterface();
                        _isInterfaceOpen = false;
                    }
                }
                catch (Exception ex)
                {
                    ErrorOccurred?.Invoke(this, $"枚举相机异常：{ex.Message}");
                }

                return cameras;
            });
        }

        #endregion

        #region 连接管理

        /// <summary>
        /// 连接相机
        /// </summary>
        public Task<bool> ConnectAsync(CameraInfo camera)
        {
            return Task.Run(() =>
            {
                try
                {
                    // 断开已有连接
                    Disconnect();

                    // 更新接口列表
                    bool changed = false;
                    int nRet = _system.UpdateInterfaceList(
                        CParamDefine.MV_FG_GEV_INTERFACE | CParamDefine.MV_FG_CAMERALINK_INTERFACE |
                        CParamDefine.MV_FG_CXP_INTERFACE | CParamDefine.MV_FG_XoF_INTERFACE,
                        ref changed);

                    if (nRet != CErrorCode.MV_FG_SUCCESS)
                    {
                        ErrorOccurred?.Invoke(this, $"更新接口列表失败，错误码：0x{nRet:X}");
                        return false;
                    }

                    // 打开第一个接口（简化处理，实际应该根据相机信息选择接口）
                    nRet = _system.OpenInterface(0, out _interface);
                    if (nRet != CErrorCode.MV_FG_SUCCESS)
                    {
                        ErrorOccurred?.Invoke(this, $"打开接口失败，错误码：0x{nRet:X}");
                        return false;
                    }
                    _isInterfaceOpen = true;

                    // 打开设备
                    nRet = _interface.OpenDevice(camera.Index, out _device);
                    if (nRet != CErrorCode.MV_FG_SUCCESS)
                    {
                        ErrorOccurred?.Invoke(this, $"打开设备失败，错误码：0x{nRet:X}");
                        _interface.CloseInterface();
                        _isInterfaceOpen = false;
                        return false;
                    }

                    _isDeviceOpen = true;
                    CurrentCamera = camera;

                    // 设置默认参数
                    SetDefaultParameters();

                    ConnectionStatusChanged?.Invoke(this, true);
                    return true;
                }
                catch (Exception ex)
                {
                    ErrorOccurred?.Invoke(this, $"连接相机异常：{ex.Message}");
                    return false;
                }
            });
        }

        /// <summary>
        /// 断开连接
        /// </summary>
        public void Disconnect()
        {
            try
            {
                StopGrabbing();

                if (_stream != null)
                {
                    _stream.CloseStream();
                    _stream = null;
                }

                if (_device != null)
                {
                    _device.CloseDevice();
                    _device = null;
                }

                if (_interface != null)
                {
                    _interface.CloseInterface();
                    _interface = null;
                }

                _isDeviceOpen = false;
                _isInterfaceOpen = false;
                CurrentCamera = null;

                ConnectionStatusChanged?.Invoke(this, false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"断开连接异常：{ex.Message}");
            }
        }

        #endregion

        #region 图像采集

        /// <summary>
        /// 开始采集
        /// </summary>
        public Task<bool> StartGrabbingAsync()
        {
            return Task.Run(() =>
            {
                if (!_isDeviceOpen)
                {
                    ErrorOccurred?.Invoke(this, "设备未连接");
                    return false;
                }

                try
                {
                    // 打开流通道
                    int nRet = _device.OpenStream(0, out _stream);
                    if (nRet != CErrorCode.MV_FG_SUCCESS)
                    {
                        ErrorOccurred?.Invoke(this, $"打开流通道失败，错误码：0x{nRet:X}");
                        return false;
                    }

                    // 开始采集
                    nRet = _stream.StartAcquisition();
                    if (nRet != CErrorCode.MV_FG_SUCCESS)
                    {
                        ErrorOccurred?.Invoke(this, $"开始采集失败，错误码：0x{nRet:X}");
                        return false;
                    }

                    _isGrabbing = true;

                    // 启动采集线程
                    _threadRunning = true;
                    _grabThread = new Thread(GrabThreadProc);
                    _grabThread.Start();

                    return true;
                }
                catch (Exception ex)
                {
                    ErrorOccurred?.Invoke(this, $"开始采集异常：{ex.Message}");
                    return false;
                }
            });
        }

        /// <summary>
        /// 停止采集
        /// </summary>
        public void StopGrabbing()
        {
            try
            {
                _threadRunning = false;

                if (_grabThread != null)
                {
                    _grabThread.Join(1000);
                    _grabThread = null;
                }

                if (_isGrabbing && _stream != null)
                {
                    _stream.StopAcquisition();
                    _isGrabbing = false;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"停止采集异常：{ex.Message}");
            }
        }

        /// <summary>
        /// 采集线程
        /// </summary>
        private void GrabThreadProc()
        {
            MV_FG_BUFFER_INFO stFrameInfo = new MV_FG_BUFFER_INFO();
            const uint nTimeout = 1000;
            IntPtr pDataBuf = IntPtr.Zero;
            uint nDataBufSize = 0;

            while (_threadRunning)
            {
                try
                {
                    if (!_isGrabbing || _stream == null)
                    {
                        Thread.Sleep(10);
                        continue;
                    }

                    // 获取一帧图像缓存信息
                    int nRet = _stream.GetFrameBuffer(ref stFrameInfo, nTimeout);
                    if (nRet != CErrorCode.MV_FG_SUCCESS)
                    {
                        continue;
                    }

                    // 分配或重新分配缓冲区
                    if (pDataBuf == IntPtr.Zero || nDataBufSize < stFrameInfo.nFilledSize)
                    {
                        if (pDataBuf != IntPtr.Zero)
                        {
                            Marshal.FreeHGlobal(pDataBuf);
                        }

                        pDataBuf = Marshal.AllocHGlobal(new IntPtr(stFrameInfo.nFilledSize));
                        if (pDataBuf == IntPtr.Zero)
                        {
                            _stream.ReleaseFrameBuffer(stFrameInfo);
                            continue;
                        }
                        nDataBufSize = stFrameInfo.nFilledSize;
                    }

                    // 复制图像数据
                    CopyMemory(pDataBuf, stFrameInfo.pBuffer, stFrameInfo.nFilledSize);

                    // 处理图像
                    ProcessImage(pDataBuf, stFrameInfo.nWidth, stFrameInfo.nHeight, stFrameInfo.nFilledSize);

                    // 释放帧缓冲区
                    _stream.ReleaseFrameBuffer(stFrameInfo);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"采集线程异常：{ex.Message}");
                }
            }

            // 清理缓冲区
            if (pDataBuf != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(pDataBuf);
            }
        }

        [DllImport("kernel32.dll", EntryPoint = "CopyMemory", SetLastError = false)]
        private static extern void CopyMemory(IntPtr dest, IntPtr src, uint count);

        /// <summary>
        /// 处理图像
        /// </summary>
        private void ProcessImage(IntPtr imageData, uint width, uint height, uint size)
        {
            try
            {
                if (imageData == IntPtr.Zero || width == 0 || height == 0)
                {
                    return;
                }

                // 将图像数据转换为字节数组
                byte[] buffer = new byte[size];
                Marshal.Copy(imageData, buffer, 0, (int)size);

                // 触发图像采集事件
                ImageGrabbed?.Invoke(this, buffer);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"处理图像异常：{ex.Message}");
            }
        }

        #endregion

        #region 参数设置

        /// <summary>
        /// 设置默认参数
        /// </summary>
        private void SetDefaultParameters()
        {
            try
            {
                CParam param = new CParam(_device);

                // 连续采集模式
                param.SetEnumValue("AcquisitionMode", 2);

                // 关闭触发模式
                param.SetEnumValue("TriggerMode", 0);

                // 设置默认曝光时间
                param.SetFloatValue("ExposureTime", 10000);

                // 设置默认增益
                param.SetFloatValue("Gain", 0);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"设置默认参数异常：{ex.Message}");
            }
        }

        /// <summary>
        /// 设置曝光时间
        /// </summary>
        public Task<bool> SetExposureTimeAsync(float exposureTime)
        {
            return Task.Run(() =>
            {
                if (!_isDeviceOpen) return false;

                try
                {
                    CParam param = new CParam(_device);
                    int nRet = param.SetFloatValue("ExposureTime", exposureTime);
                    return nRet == CErrorCode.MV_FG_SUCCESS;
                }
                catch (Exception ex)
                {
                    ErrorOccurred?.Invoke(this, $"设置曝光时间异常：{ex.Message}");
                    return false;
                }
            });
        }

        /// <summary>
        /// 设置增益
        /// </summary>
        public Task<bool> SetGainAsync(float gain)
        {
            return Task.Run(() =>
            {
                if (!_isDeviceOpen) return false;

                try
                {
                    CParam param = new CParam(_device);
                    int nRet = param.SetFloatValue("Gain", gain);
                    return nRet == CErrorCode.MV_FG_SUCCESS;
                }
                catch (Exception ex)
                {
                    ErrorOccurred?.Invoke(this, $"设置增益异常：{ex.Message}");
                    return false;
                }
            });
        }

        /// <summary>
        /// 获取曝光时间
        /// </summary>
        public Task<float> GetExposureTimeAsync()
        {
            return Task.Run(() =>
            {
                if (!_isDeviceOpen) return 0;

                try
                {
                    CParam param = new CParam(_device);
                    MV_FG_FLOATVALUE value = new MV_FG_FLOATVALUE();
                    param.GetFloatValue("ExposureTime", ref value);
                    return value.fCurValue;
                }
                catch
                {
                    return 0;
                }
            });
        }

        /// <summary>
        /// 获取增益
        /// </summary>
        public Task<float> GetGainAsync()
        {
            return Task.Run(() =>
            {
                if (!_isDeviceOpen) return 0;

                try
                {
                    CParam param = new CParam(_device);
                    MV_FG_FLOATVALUE value = new MV_FG_FLOATVALUE();
                    param.GetFloatValue("Gain", ref value);
                    return value.fCurValue;
                }
                catch
                {
                    return 0;
                }
            });
        }

        #endregion

        #region 辅助方法

        /// <summary>
        /// 解析设备信息
        /// </summary>
        private CameraInfo ParseDeviceInfo(MV_FG_DEVICE_INFO info, uint index)
        {
            var cameraInfo = new CameraInfo
            {
                Index = index,
                Type = info.nDevType
            };

            switch (info.nDevType)
            {
                case CParamDefine.MV_FG_GEV_DEVICE:
                    var gevInfo = (MV_GEV_DEVICE_INFO)CAdditional.ByteToStruct(
                        info.DevInfo.stGEVDevInfo, typeof(MV_GEV_DEVICE_INFO));
                    cameraInfo.Name = gevInfo.chUserDefinedName;
                    cameraInfo.Model = gevInfo.chModelName;
                    cameraInfo.SerialNumber = gevInfo.chSerialNumber;
                    cameraInfo.InterfaceType = "GigE";
                    cameraInfo.DisplayName = $"GigE[{index}] {gevInfo.chUserDefinedName} | {gevInfo.chModelName}";
                    break;

                case CParamDefine.MV_FG_CXP_DEVICE:
                    var cxpInfo = (MV_CXP_DEVICE_INFO)CAdditional.ByteToStruct(
                        info.DevInfo.stCXPDevInfo, typeof(MV_CXP_DEVICE_INFO));
                    cameraInfo.Name = cxpInfo.chUserDefinedName;
                    cameraInfo.Model = cxpInfo.chModelName;
                    cameraInfo.SerialNumber = cxpInfo.chSerialNumber;
                    cameraInfo.InterfaceType = "CXP";
                    cameraInfo.DisplayName = $"CXP[{index}] {cxpInfo.chUserDefinedName} | {cxpInfo.chModelName}";
                    break;

                case CParamDefine.MV_FG_CAMERALINK_DEVICE:
                    var cmlInfo = (MV_CML_DEVICE_INFO)CAdditional.ByteToStruct(
                        info.DevInfo.stCMLDevInfo, typeof(MV_CML_DEVICE_INFO));
                    cameraInfo.Name = cmlInfo.chUserDefinedName;
                    cameraInfo.Model = cmlInfo.chModelName;
                    cameraInfo.SerialNumber = cmlInfo.chSerialNumber;
                    cameraInfo.InterfaceType = "CameraLink";
                    cameraInfo.DisplayName = $"CML[{index}] {cmlInfo.chUserDefinedName} | {cmlInfo.chModelName}";
                    break;

                case CParamDefine.MV_FG_XoF_DEVICE:
                    var xofInfo = (MV_XoF_DEVICE_INFO)CAdditional.ByteToStruct(
                        info.DevInfo.stXoFDevInfo, typeof(MV_XoF_DEVICE_INFO));
                    cameraInfo.Name = xofInfo.chUserDefinedName;
                    cameraInfo.Model = xofInfo.chModelName;
                    cameraInfo.SerialNumber = xofInfo.chSerialNumber;
                    cameraInfo.InterfaceType = "XoF";
                    cameraInfo.DisplayName = $"XoF[{index}] {xofInfo.chUserDefinedName} | {xofInfo.chModelName}";
                    break;

                default:
                    cameraInfo.Name = "Unknown";
                    cameraInfo.Model = "Unknown";
                    cameraInfo.InterfaceType = "Unknown";
                    cameraInfo.DisplayName = $"Unknown[{index}]";
                    break;
            }

            return cameraInfo;
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            Disconnect();
        }

        #endregion
    }
}
