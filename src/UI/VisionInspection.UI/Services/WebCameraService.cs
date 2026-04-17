using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OpenCvSharp;
using VisionInspection.Core.Services;

namespace VisionInspection.UI.Services
{
    /// <summary>
    /// 笔记本摄像头服务实现 - 使用OpenCV
    /// </summary>
    public class WebCameraService : ICameraService
    {
        #region 字段

        private VideoCapture? _capture;
        private bool _isConnected = false;
        private bool _isGrabbing = false;
        private CancellationTokenSource? _cancellationTokenSource;
        private Task? _grabTask;

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
        /// 枚举可用相机（笔记本摄像头通常是索引0和1）
        /// </summary>
        public Task<List<CameraInfo>> EnumCamerasAsync()
        {
            return Task.Run(() =>
            {
                var cameras = new List<CameraInfo>();

                // 尝试打开前3个摄像头索引
                for (int i = 0; i < 3; i++)
                {
                    try
                    {
                        using var capture = new VideoCapture(i);
                        if (capture.IsOpened())
                        {
                            // 尝试读取一帧以确认相机可用
                            using var frame = new Mat();
                            if (capture.Read(frame) && !frame.Empty())
                            {
                                cameras.Add(new CameraInfo
                                {
                                    Id = $"webcam_{i}",
                                    Name = $"摄像头 {i}",
                                    Model = "Web Camera",
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

                    int cameraIndex = (int)camera.ExtInfo;
                    _capture = new VideoCapture(cameraIndex);

                    if (!_capture.IsOpened())
                    {
                        ErrorOccurred?.Invoke(this, $"无法打开摄像头 {cameraIndex}");
                        return false;
                    }

                    // 设置分辨率（可选）
                    _capture.Set(VideoCaptureProperties.FrameWidth, 1280);
                    _capture.Set(VideoCaptureProperties.FrameHeight, 720);

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
                // 先停止采集
                StopGrabbing();

                if (_isConnected && _capture != null)
                {
                    _capture.Release();
                    _capture.Dispose();
                    _capture = null;
                    
                    _isConnected = false;
                    System.Diagnostics.Debug.WriteLine("摄像头已断开");
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

                    // 启动采集任务
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
        /// 采集循环
        /// </summary>
        private void GrabLoop(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested && _isGrabbing)
            {
                try
                {
                    if (_capture != null)
                    {
                        using var frame = new Mat();
                        if (_capture.Read(frame) && !frame.Empty())
                        {
                            // 转换为RGB格式
                            using var rgbFrame = new Mat();
                            Cv2.CvtColor(frame, rgbFrame, ColorConversionCodes.BGR2RGB);

                            // 确保数据是连续的
                            using var continuousFrame = rgbFrame.IsContinuous() ? rgbFrame.Clone() : rgbFrame.Clone();
                            
                            // 获取图像数据 - 使用正确的字节数组拷贝方式
                            int width = continuousFrame.Width;
                            int height = continuousFrame.Height;
                            int channels = continuousFrame.Channels();
                            int totalBytes = width * height * channels;
                            
                            byte[] data = new byte[totalBytes];
                            System.Runtime.InteropServices.Marshal.Copy(continuousFrame.Data, data, 0, totalBytes);

                            // 触发事件
                            ImageGrabbed?.Invoke(this, data);
                            ImageDataGrabbed?.Invoke(this, new CameraImageData
                            {
                                Data = data,
                                Width = width,
                                Height = height,
                                IsColor = true,
                                Channels = channels
                            });
                        }
                    }

                    // 控制帧率约30fps
                    Thread.Sleep(33);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"采集异常：{ex.Message}");
                }
            }
        }

        /// <summary>
        /// 停止采集
        /// </summary>
        public void StopGrabbing()
        {
            try
            {
                _isGrabbing = false;
                _cancellationTokenSource?.Cancel();

                if (_grabTask != null)
                {
                    _grabTask.Wait(1000);
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

        #region 参数设置（笔记本摄像头支持有限）

        public Task<bool> SetExposureTimeAsync(float exposureTime)
        {
            // 笔记本摄像头通常不支持曝光时间设置
            return Task.FromResult(false);
        }

        public Task<bool> SetGainAsync(float gain)
        {
            // 笔记本摄像头通常不支持增益设置
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
            return Task.FromResult((0f, 0f));
        }

        public Task<(float Min, float Max)> GetGainRangeAsync()
        {
            return Task.FromResult((0f, 0f));
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
