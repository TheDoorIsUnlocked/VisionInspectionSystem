using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace VisionInspection.Core.Services
{
    /// <summary>
    /// 模拟相机服务 - 用于测试
    /// </summary>
    public class MockCameraService : ICameraService
    {
        private bool _isConnected = false;
        private bool _isGrabbing = false;
        private CameraInfo _currentCamera = null;
        private Timer _grabTimer = null;
        private float _exposureTime = 10000;
        private float _gain = 0;

        public bool IsConnected => _isConnected;
        public bool IsGrabbing => _isGrabbing;
        public CameraInfo CurrentCamera => _currentCamera;

        public event EventHandler<bool> ConnectionStatusChanged;
        public event EventHandler<byte[]> ImageGrabbed;
        public event EventHandler<CameraImageData> ImageDataGrabbed;
        public event EventHandler<string> ErrorOccurred;

        public Task<List<CameraInfo>> EnumCamerasAsync()
        {
            // 返回模拟相机列表
            var cameras = new List<CameraInfo>
            {
                new CameraInfo
                {
                    Id = "CAM001",
                    Name = "模拟相机1",
                    Model = "MV-CA013-20GM",
                    SerialNumber = "SN123456",
                    InterfaceType = "GigE"
                },
                new CameraInfo
                {
                    Id = "CAM002",
                    Name = "模拟相机2",
                    Model = "MV-CA013-20GM",
                    SerialNumber = "SN789012",
                    InterfaceType = "GigE"
                }
            };

            return Task.FromResult(cameras);
        }

        public Task<bool> ConnectAsync(CameraInfo camera)
        {
            _currentCamera = camera;
            _isConnected = true;
            ConnectionStatusChanged?.Invoke(this, true);
            return Task.FromResult(true);
        }

        public void Disconnect()
        {
            StopGrabbing();
            _isConnected = false;
            _currentCamera = null;
            ConnectionStatusChanged?.Invoke(this, false);
        }

        public Task<bool> StartGrabbingAsync()
        {
            if (!_isConnected)
            {
                ErrorOccurred?.Invoke(this, "相机未连接");
                return Task.FromResult(false);
            }

            _isGrabbing = true;

            // 启动模拟采集定时器
            _grabTimer = new Timer(GrabTimerCallback, null, 0, 100);

            return Task.FromResult(true);
        }

        private void GrabTimerCallback(object state)
        {
            if (!_isGrabbing) return;

            // 生成模拟图像数据（简单的测试图案）
            var imageData = GenerateTestPattern();
            ImageGrabbed?.Invoke(this, imageData);

            // 同时触发新的图像数据事件
            var cameraImageData = new CameraImageData
            {
                Data = imageData,
                Width = 640,
                Height = 480,
                IsColor = true,
                Channels = 3
            };
            ImageDataGrabbed?.Invoke(this, cameraImageData);
        }

        private byte[] GenerateTestPattern()
        {
            // 生成一个简单的测试图像（640x480 RGB24）
            int width = 640;
            int height = 480;
            int size = width * height * 3;
            var data = new byte[size];

            // 创建渐变图案
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int index = (y * width + x) * 3;
                    data[index] = (byte)(x * 255 / width);      // R
                    data[index + 1] = (byte)(y * 255 / height); // G
                    data[index + 2] = 128;                       // B
                }
            }

            return data;
        }

        public void StopGrabbing()
        {
            _isGrabbing = false;
            _grabTimer?.Dispose();
            _grabTimer = null;
        }

        public Task<bool> SetExposureTimeAsync(float exposureTime)
        {
            _exposureTime = exposureTime;
            return Task.FromResult(true);
        }

        public Task<bool> SetGainAsync(float gain)
        {
            _gain = gain;
            return Task.FromResult(true);
        }

        public Task<float> GetExposureTimeAsync()
        {
            return Task.FromResult(_exposureTime);
        }

        public Task<float> GetGainAsync()
        {
            return Task.FromResult(_gain);
        }

        public Task<(float Min, float Max)> GetExposureTimeRangeAsync()
        {
            // 模拟曝光时间范围：10μs - 1000000μs
            return Task.FromResult((10f, 1000000f));
        }

        public Task<(float Min, float Max)> GetGainRangeAsync()
        {
            // 模拟增益范围：0dB - 20dB
            return Task.FromResult((0f, 20f));
        }

        public void Dispose()
        {
            StopGrabbing();
            _grabTimer?.Dispose();
        }
    }
}
