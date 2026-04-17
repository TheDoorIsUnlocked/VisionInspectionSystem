using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using MvCamCtrl.NET;
using VisionInspection.Core.Services;

namespace VisionInspection.UI.Services
{
    /// <summary>
    /// 海康工业相机服务实现 - 使用MvCameraControl.Net SDK
    /// </summary>
    public class HikvisionCameraService : ICameraService
    {
        #region 字段

        private MyCamera _camera = new MyCamera();
        private MyCamera.MV_CC_DEVICE_INFO _deviceInfo;
        private bool _isConnected = false;
        private bool _isGrabbing = false;

        private MyCamera.cbOutputExdelegate _imageCallback;
        private readonly object _lockObject = new object();

        // 图像缓冲区
        private UInt32 _bufferSize = 5120 * 5120 * 3 + 2048;
        private byte[] _buffer;

        #endregion

        #region 属性

        public bool IsConnected => _isConnected;
        public bool IsGrabbing => _isGrabbing;
        public CameraInfo CurrentCamera { get; private set; }

        #endregion

        #region 事件

        public event EventHandler<byte[]> ImageGrabbed;
        public event EventHandler<bool> ConnectionStatusChanged;
        public event EventHandler<string> ErrorOccurred;

        #endregion

        public HikvisionCameraService()
        {
            _buffer = new byte[_bufferSize];
        }

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
                    MyCamera.MV_CC_DEVICE_INFO_LIST deviceList = new MyCamera.MV_CC_DEVICE_INFO_LIST();

                    // 枚举GigE和USB设备
                    int nRet = MyCamera.MV_CC_EnumDevices_NET(
                        MyCamera.MV_GIGE_DEVICE | MyCamera.MV_USB_DEVICE,
                        ref deviceList);

                    if (nRet != 0)
                    {
                        ErrorOccurred?.Invoke(this, $"枚举设备失败，错误码：{nRet}");
                        return cameras;
                    }

                    // 遍历所有设备
                    for (int i = 0; i < deviceList.nDeviceNum; i++)
                    {
                        MyCamera.MV_CC_DEVICE_INFO device =
                            (MyCamera.MV_CC_DEVICE_INFO)Marshal.PtrToStructure(
                                deviceList.pDeviceInfo[i],
                                typeof(MyCamera.MV_CC_DEVICE_INFO));

                        var cameraInfo = ParseDeviceInfo(device, i);
                        cameras.Add(cameraInfo);
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

                    if (camera?.ExtInfo == null)
                    {
                        ErrorOccurred?.Invoke(this, "相机信息无效");
                        return false;
                    }

                    _deviceInfo = (MyCamera.MV_CC_DEVICE_INFO)camera.ExtInfo;

                    // 创建设备
                    int nRet = _camera.MV_CC_CreateDevice_NET(ref _deviceInfo);
                    if (nRet != MyCamera.MV_OK)
                    {
                        ErrorOccurred?.Invoke(this, $"创建设备失败，错误码：0x{nRet:X}");
                        return false;
                    }

                    // 打开设备
                    nRet = _camera.MV_CC_OpenDevice_NET();
                    if (nRet != MyCamera.MV_OK)
                    {
                        _camera.MV_CC_DestroyDevice_NET();
                        ErrorOccurred?.Invoke(this, $"打开设备失败，错误码：0x{nRet:X}");
                        return false;
                    }

                    _isConnected = true;
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

                if (_isConnected)
                {
                    _camera.MV_CC_CloseDevice_NET();
                    _camera.MV_CC_DestroyDevice_NET();
                    _isConnected = false;
                }

                CurrentCamera = null;
                ConnectionStatusChanged?.Invoke(this, false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"断开连接异常：{ex.Message}");
            }
        }

        /// <summary>
        /// 释放资源
        /// </summary>
        public void Dispose()
        {
            Disconnect();
            _camera = null;
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
                if (!_isConnected)
                {
                    ErrorOccurred?.Invoke(this, "设备未连接");
                    return false;
                }

                try
                {
                    // 注册图像回调
                    _imageCallback = new MyCamera.cbOutputExdelegate(ImageCallbackFunc);
                    int nRet = _camera.MV_CC_RegisterImageCallBackEx_NET(_imageCallback, IntPtr.Zero);
                    if (nRet != MyCamera.MV_OK)
                    {
                        ErrorOccurred?.Invoke(this, $"注册回调失败，错误码：0x{nRet:X}");
                        return false;
                    }

                    // 开始抓图
                    nRet = _camera.MV_CC_StartGrabbing_NET();
                    if (nRet != MyCamera.MV_OK)
                    {
                        ErrorOccurred?.Invoke(this, $"开始采集失败，错误码：0x{nRet:X}");
                        return false;
                    }

                    _isGrabbing = true;
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
                if (_isGrabbing)
                {
                    _camera.MV_CC_StopGrabbing_NET();
                    _isGrabbing = false;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"停止采集异常：{ex.Message}");
            }
        }

        /// <summary>
        /// 图像回调函数
        /// </summary>
        private void ImageCallbackFunc(IntPtr pData, ref MyCamera.MV_FRAME_OUT_INFO_EX pFrameInfo, IntPtr pUser)
        {
            try
            {
                if (pData == IntPtr.Zero || pFrameInfo.nWidth == 0 || pFrameInfo.nHeight == 0)
                {
                    return;
                }

                // 确定目标像素格式
                MyCamera.MvGvspPixelType enDstPixelType;
                if (IsMonoData(pFrameInfo.enPixelType))
                {
                    enDstPixelType = MyCamera.MvGvspPixelType.PixelType_Gvsp_Mono8;
                }
                else if (IsColorData(pFrameInfo.enPixelType))
                {
                    enDstPixelType = MyCamera.MvGvspPixelType.PixelType_Gvsp_RGB8_Packed;
                }
                else
                {
                    return;
                }

                // 像素格式转换
                IntPtr pImage = Marshal.UnsafeAddrOfPinnedArrayElement(_buffer, 0);

                MyCamera.MV_PIXEL_CONVERT_PARAM stConvertParam = new MyCamera.MV_PIXEL_CONVERT_PARAM();
                stConvertParam.nWidth = pFrameInfo.nWidth;
                stConvertParam.nHeight = pFrameInfo.nHeight;
                stConvertParam.pSrcData = pData;
                stConvertParam.nSrcDataLen = pFrameInfo.nFrameLen;
                stConvertParam.enSrcPixelType = pFrameInfo.enPixelType;
                stConvertParam.enDstPixelType = enDstPixelType;
                stConvertParam.pDstBuffer = pImage;
                stConvertParam.nDstBufferSize = _bufferSize;

                int nRet = _camera.MV_CC_ConvertPixelType_NET(ref stConvertParam);
                if (nRet != MyCamera.MV_OK)
                {
                    return;
                }

                // 计算输出数据大小
                int dataSize = (int)(pFrameInfo.nWidth * pFrameInfo.nHeight *
                    (enDstPixelType == MyCamera.MvGvspPixelType.PixelType_Gvsp_Mono8 ? 1 : 3));

                // 复制数据
                byte[] imageData = new byte[dataSize];
                Marshal.Copy(pImage, imageData, 0, dataSize);

                // 触发事件
                ImageGrabbed?.Invoke(this, imageData);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"图像回调异常：{ex.Message}");
            }
        }

        /// <summary>
        /// 判断是否为黑白图像
        /// </summary>
        private bool IsMonoData(MyCamera.MvGvspPixelType enGvspPixelType)
        {
            switch (enGvspPixelType)
            {
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_Mono8:
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_Mono10:
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_Mono10_Packed:
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_Mono12:
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_Mono12_Packed:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// 判断是否为彩色图像
        /// </summary>
        private bool IsColorData(MyCamera.MvGvspPixelType enGvspPixelType)
        {
            switch (enGvspPixelType)
            {
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_BayerGR8:
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_BayerRG8:
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_BayerGB8:
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_BayerBG8:
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_BayerGR10:
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_BayerRG10:
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_BayerGB10:
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_BayerBG10:
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_BayerGR12:
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_BayerRG12:
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_BayerGB12:
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_BayerBG12:
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_RGB8_Packed:
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_BGR8_Packed:
                    return true;
                default:
                    return false;
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
                // 设置连续采集模式
                _camera.MV_CC_SetEnumValue_NET("AcquisitionMode", 2);

                // 关闭触发模式
                _camera.MV_CC_SetEnumValue_NET("TriggerMode", 0);

                // 设置默认曝光时间
                _camera.MV_CC_SetFloatValue_NET("ExposureTime", 10000);

                // 设置默认增益
                _camera.MV_CC_SetFloatValue_NET("Gain", 0);
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
                if (!_isConnected) return false;

                try
                {
                    int nRet = _camera.MV_CC_SetFloatValue_NET("ExposureTime", exposureTime);
                    return nRet == MyCamera.MV_OK;
                }
                catch (Exception ex)
                {
                    ErrorOccurred?.Invoke(this, $"设置曝光时间异常：{ex.Message}");
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
                if (!_isConnected) return 0;

                try
                {
                    MyCamera.MVCC_FLOATVALUE value = new MyCamera.MVCC_FLOATVALUE();
                    _camera.MV_CC_GetFloatValue_NET("ExposureTime", ref value);
                    return value.fCurValue;
                }
                catch
                {
                    return 0;
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
                if (!_isConnected) return false;

                try
                {
                    int nRet = _camera.MV_CC_SetFloatValue_NET("Gain", gain);
                    return nRet == MyCamera.MV_OK;
                }
                catch (Exception ex)
                {
                    ErrorOccurred?.Invoke(this, $"设置增益异常：{ex.Message}");
                    return false;
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
                if (!_isConnected) return 0;

                try
                {
                    MyCamera.MVCC_FLOATVALUE value = new MyCamera.MVCC_FLOATVALUE();
                    _camera.MV_CC_GetFloatValue_NET("Gain", ref value);
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
        private CameraInfo ParseDeviceInfo(MyCamera.MV_CC_DEVICE_INFO info, int index)
        {
            var cameraInfo = new CameraInfo
            {
                Index = (uint)index,
                Type = info.nTLayerType,
                ExtInfo = info
            };

            if (info.nTLayerType == MyCamera.MV_GIGE_DEVICE)
            {
                // GigE设备
                IntPtr buffer = Marshal.UnsafeAddrOfPinnedArrayElement(info.SpecialInfo.stGigEInfo, 0);
                MyCamera.MV_GIGE_DEVICE_INFO gigeInfo =
                    (MyCamera.MV_GIGE_DEVICE_INFO)Marshal.PtrToStructure(buffer, typeof(MyCamera.MV_GIGE_DEVICE_INFO));

                cameraInfo.Name = gigeInfo.chUserDefinedName;
                cameraInfo.Model = gigeInfo.chModelName;
                cameraInfo.SerialNumber = gigeInfo.chSerialNumber;
                cameraInfo.InterfaceType = "GigE";
                cameraInfo.DisplayName = string.IsNullOrEmpty(gigeInfo.chUserDefinedName)
                    ? $"GigE[{index}] {gigeInfo.chManufacturerName} {gigeInfo.chModelName} ({gigeInfo.chSerialNumber})"
                    : $"GigE[{index}] {gigeInfo.chUserDefinedName} ({gigeInfo.chSerialNumber})";
            }
            else if (info.nTLayerType == MyCamera.MV_USB_DEVICE)
            {
                // USB设备
                IntPtr buffer = Marshal.UnsafeAddrOfPinnedArrayElement(info.SpecialInfo.stUsb3VInfo, 0);
                MyCamera.MV_USB3_DEVICE_INFO usbInfo =
                    (MyCamera.MV_USB3_DEVICE_INFO)Marshal.PtrToStructure(buffer, typeof(MyCamera.MV_USB3_DEVICE_INFO));

                cameraInfo.Name = usbInfo.chUserDefinedName;
                cameraInfo.Model = usbInfo.chModelName;
                cameraInfo.SerialNumber = usbInfo.chSerialNumber;
                cameraInfo.InterfaceType = "USB3";
                cameraInfo.DisplayName = string.IsNullOrEmpty(usbInfo.chUserDefinedName)
                    ? $"USB[{index}] {usbInfo.chManufacturerName} {usbInfo.chModelName} ({usbInfo.chSerialNumber})"
                    : $"USB[{index}] {usbInfo.chUserDefinedName} ({usbInfo.chSerialNumber})";
            }

            return cameraInfo;
        }

        #endregion
    }
}
