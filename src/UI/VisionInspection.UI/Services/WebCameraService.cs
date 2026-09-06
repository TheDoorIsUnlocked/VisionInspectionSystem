using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OpenCvSharp;
using VisionInspection.Core.Services;

namespace VisionInspection.UI.Services
{
    /// <summary>
    /// 笔记本摄像头 / UVC USB 相机服务实现 - 使用OpenCV DirectShow后端
    /// 支持 DJI Pocket3 等消费级 UVC 相机。
    /// 注意：UVC 相机必须走 DirectShow / MediaFoundation，不能用海康 MVS SDK 驱动。
    /// </summary>
    public class WebCameraService : ICameraService
    {
        #region 字段

        private VideoCapture? _capture;
        private bool _isConnected = false;
        private bool _isGrabbing = false;
        private CancellationTokenSource? _cancellationTokenSource;
        private Task? _grabTask;

        // 保护 _capture 访问：采集线程 Read 与主线程 Release 不能并发，
        // 否则采集线程读取已释放的原生对象会触发 System.AccessViolationException
        private readonly object _captureLock = new object();

        #endregion

        #region 属性

        public bool IsConnected => _isConnected;
        public bool IsGrabbing => _isGrabbing;
        public CameraInfo CurrentCamera { get; private set; } = new CameraInfo();

        #endregion

        #region 事件

        public event EventHandler<byte[]>? ImageGrabbed;
        public event EventHandler<CameraImageData>? ImageDataGrabbed;
        public event EventHandler<bool>? ConnectionStatusChanged;
        public event EventHandler<string>? ErrorOccurred;

        #endregion

        #region 相机枚举

        /// <summary>
        /// 枚举可用 UVC 相机（DirectShow 后端，遍历 0..10 索引）
        /// </summary>
        public Task<List<CameraInfo>> EnumCamerasAsync()
        {
            return Task.Run(() =>
            {
                var cameras = new List<CameraInfo>();

                // 尝试打开前 10 个摄像头索引（UVC 相机可能占用多个索引）
                for (int i = 0; i < 10; i++)
                {
                    try
                    {
                        using var capture = new VideoCapture(i, VideoCaptureAPIs.DSHOW);
                        if (!capture.IsOpened())
                            continue;

                        // 尝试读取一帧以确认相机可用
                        using var frame = new Mat();
                        if (capture.Read(frame) && !frame.Empty())
                        {
                            cameras.Add(new CameraInfo
                            {
                                Id = $"webcam_{i}",
                                Name = $"摄像头 {i}",
                                Model = "UVC Camera",
                                SerialNumber = $"{i}",
                                InterfaceType = "USB",
                                Index = (uint)i,
                                InterfaceIndex = 0,
                                Type = 0,
                                DisplayName = $"🎥 摄像头 {i} ({frame.Width}x{frame.Height})",
                                ExtInfo = i // 存储索引
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"检查摄像头 {i} 失败: {ex.Message}");
                    }
                }

                return cameras;
            });
        }

        #endregion

        #region 连接控制

        /// <summary>
        /// 连接相机（使用 DirectShow 后端，对消费级 UVC 相机兼容性最好）
        /// </summary>
        public Task<bool> ConnectAsync(CameraInfo camera)
        {
            return Task.Run(() =>
            {
                try
                {
                    // 断开已有连接（内部会先停止采集并等待采集线程完全退出）
                    Disconnect();

                    if (camera?.ExtInfo == null)
                    {
                        ErrorOccurred?.Invoke(this, "相机信息无效");
                        return false;
                    }

                    int cameraIndex = (int)camera.ExtInfo;
                    lock (_captureLock)
                    {
                        _capture = new VideoCapture(cameraIndex, VideoCaptureAPIs.DSHOW);

                        if (!_capture.IsOpened())
                        {
                            _capture.Dispose();
                            _capture = null;
                            ErrorOccurred?.Invoke(this, $"无法打开摄像头 {cameraIndex}");
                            return false;
                        }

                        // 请求 1280x720（DSHOW 下多数 UVC 相机可协商；失败则保持相机默认分辨率，不影响采集）
                        _capture.Set(VideoCaptureProperties.FrameWidth, 1280);
                        _capture.Set(VideoCaptureProperties.FrameHeight, 720);
                    }

                    _isConnected = true;
                    CurrentCamera = camera;

                    ConnectionStatusChanged?.Invoke(this, true);
                    return true;
                }
                catch (Exception ex)
                {
                    ErrorOccurred?.Invoke(this, $"连接摄像头异常：{ex.Message}");
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
                // 先停止采集（确保采集线程完全退出后再释放 capture，避免原生访问冲突）
                StopGrabbing();

                lock (_captureLock)
                {
                    if (_isConnected && _capture != null)
                    {
                        _capture.Release();
                        _capture.Dispose();
                        _capture = null;

                        _isConnected = false;
                        System.Diagnostics.Debug.WriteLine("摄像头已断开");
                    }
                }

                CurrentCamera = new CameraInfo();
                ConnectionStatusChanged?.Invoke(this, false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"断开连接异常：{ex.Message}");
            }
        }

        #endregion

        #region 采集控制

        /// <summary>
        /// 开始采集
        /// </summary>
        public Task<bool> StartGrabbingAsync()
        {
            return Task.Run(() =>
            {
                if (!_isConnected || _capture == null)
                {
                    ErrorOccurred?.Invoke(this, "设备未连接");
                    return false;
                }

                try
                {
                    _cancellationTokenSource = new CancellationTokenSource();
                    _isGrabbing = true;

                    _grabTask = Task.Run(() => GrabLoop(_cancellationTokenSource.Token));

                    return true;
                }
                catch (Exception ex)
                {
                    ErrorOccurred?.Invoke(this, $"开始采集异常：{ex.Message}");
                    _isGrabbing = false;
                    return false;
                }
            });
        }

        /// <summary>
        /// 采集循环：在 _captureLock 保护下读取帧，避免与释放操作并发导致原生崩溃
        /// </summary>
        private void GrabLoop(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested && _isGrabbing)
            {
                Mat? frame = null;
                lock (_captureLock)
                {
                    if (_capture == null || !_capture.IsOpened()) break;
                    frame = new Mat();
                    if (!_capture.Read(frame) || frame.Empty())
                    {
                        frame.Dispose();
                        frame = null;
                    }
                }

                if (frame == null)
                {
                    Thread.Sleep(33);
                    continue;
                }

                try
                {
                    using (frame)
                    using (var rgbFrame = new Mat())
                    {
                        Cv2.CvtColor(frame, rgbFrame, ColorConversionCodes.BGR2RGB);

                        // 确保数据连续：不连续时克隆为连续 Mat，避免拷贝越界
                        using var continuousFrame = rgbFrame.IsContinuous() ? rgbFrame : rgbFrame.Clone();

                        // 用 Mat 实际字节数计算（比 width*height*channels 更安全，兼容非 4 字节对齐的 stride）
                        long totalBytes = continuousFrame.Total() * continuousFrame.ElemSize();

                        byte[] data = new byte[totalBytes];
                        System.Runtime.InteropServices.Marshal.Copy(continuousFrame.Data, data, 0, (int)totalBytes);

                        ImageGrabbed?.Invoke(this, data);
                        ImageDataGrabbed?.Invoke(this, new CameraImageData
                        {
                            Data = data,
                            Width = continuousFrame.Width,
                            Height = continuousFrame.Height,
                            IsColor = true,
                            Channels = continuousFrame.Channels()
                        });
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"采集异常：{ex.Message}");
                }

                Thread.Sleep(33);
            }
        }

        /// <summary>
        /// 停止采集：等待采集线程真正退出，
        /// 避免采集线程仍在 Read 时释放 VideoCapture 造成 System.AccessViolationException
        /// </summary>
        public void StopGrabbing()
        {
            try
            {
                if (!_isGrabbing)
                {
                    // 未在采集：直接返回，不打印日志（避免连接/断开时刷屏"摄像头采集已停止"）
                    return;
                }

                _isGrabbing = false;
                _cancellationTokenSource?.Cancel();

                if (_grabTask != null)
                {
                    // 等待采集线程退出（Read 可能阻塞，给足时间；超时也不强制释放，避免原生崩溃）
                    _grabTask.Wait(3000);
                    _grabTask = null;
                }

                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = null;

                System.Diagnostics.Debug.WriteLine("摄像头采集已停止");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"停止采集异常：{ex.Message}");
            }
        }

        #endregion

        #region 参数设置（UVC 相机仅支持有限调节；返回默认范围避免 UI 提示"范围无效"）

        public Task<bool> SetExposureTimeAsync(float exposureTime)
        {
            // UVC 相机通常不支持工业式曝光时间设置
            return Task.FromResult(false);
        }

        public Task<bool> SetGainAsync(float gain)
        {
            return Task.FromResult(false);
        }

        public Task<float> GetExposureTimeAsync()
        {
            return Task.FromResult(0f);
        }

        public Task<float> GetGainAsync()
        {
            return Task.FromResult(0f);
        }

        public Task<(float Min, float Max)> GetExposureTimeRangeAsync()
        {
            // 返回与默认值一致的范围，避免 CameraConfigWindow 打印"曝光范围无效"且 Slider 被重置
            return Task.FromResult((20f, 10000000f));
        }

        public Task<(float Min, float Max)> GetGainRangeAsync()
        {
            return Task.FromResult((0f, 20f));
        }

        #endregion

        #region 资源释放

        public void Dispose()
        {
            Disconnect();
        }

        #endregion
    }
}
