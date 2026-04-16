using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VisionInspection.Core.Services;
using VisionInspection.UI.Services;

namespace VisionInspection.UI.Views
{
    /// <summary>
    /// 相机配置窗口
    /// </summary>
    public partial class CameraConfigWindow : Window, INotifyPropertyChanged
    {
        private readonly ICameraService _cameraService;
        private List<CameraInfo> _cameras = new();
        private CameraInfo _selectedCamera = null;

        #region 属性

        private bool _isConnected = false;
        public bool IsConnected
        {
            get => _isConnected;
            set
            {
                _isConnected = value;
                OnPropertyChanged();
                UpdateUIState();
            }
        }

        #endregion

        public CameraConfigWindow()
        {
            InitializeComponent();
            DataContext = this;

            // 使用海康真实相机服务
            _cameraService = new HikvisionCameraService();
            _cameraService.ImageGrabbed += CameraService_ImageGrabbed;
            _cameraService.ConnectionStatusChanged += CameraService_ConnectionStatusChanged;
            _cameraService.ErrorOccurred += CameraService_ErrorOccurred;

            UpdateUIState();
        }

        #region 事件处理

        /// <summary>
        /// 图像采集事件
        /// </summary>
        private void CameraService_ImageGrabbed(object sender, byte[] imageData)
        {
            Dispatcher.Invoke(() =>
            {
                var bitmap = ConvertByteArrayToBitmapImage(imageData, 640, 480);
                if (bitmap != null)
                {
                    PreviewImage.Source = bitmap;
                    NoImageText.Visibility = Visibility.Collapsed;
                }
            });
        }

        /// <summary>
        /// 将字节数组转换为BitmapImage
        /// </summary>
        private BitmapImage ConvertByteArrayToBitmapImage(byte[] data, int width, int height)
        {
            try
            {
                // 创建BMP文件头
                int stride = width * 3;
                int imageSize = stride * height;
                int fileSize = 54 + imageSize;

                using (var stream = new System.IO.MemoryStream())
                {
                    // BMP文件头
                    stream.WriteByte(0x42); // 'B'
                    stream.WriteByte(0x4D); // 'M'
                    stream.Write(BitConverter.GetBytes(fileSize), 0, 4);
                    stream.Write(BitConverter.GetBytes(0), 0, 4);
                    stream.Write(BitConverter.GetBytes(54), 0, 4);

                    // DIB头
                    stream.Write(BitConverter.GetBytes(40), 0, 4);
                    stream.Write(BitConverter.GetBytes(width), 0, 4);
                    stream.Write(BitConverter.GetBytes(height), 0, 4);
                    stream.Write(BitConverter.GetBytes((short)1), 0, 2);
                    stream.Write(BitConverter.GetBytes((short)24), 0, 2);
                    stream.Write(BitConverter.GetBytes(0), 0, 4);
                    stream.Write(BitConverter.GetBytes(imageSize), 0, 4);
                    stream.Write(BitConverter.GetBytes(0), 0, 4);
                    stream.Write(BitConverter.GetBytes(0), 0, 4);
                    stream.Write(BitConverter.GetBytes(0), 0, 4);
                    stream.Write(BitConverter.GetBytes(0), 0, 4);

                    // 图像数据
                    stream.Write(data, 0, data.Length);
                    stream.Position = 0;

                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.StreamSource = stream;
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.EndInit();
                    bitmap.Freeze();

                    return bitmap;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"转换图像异常：{ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 连接状态改变事件
        /// </summary>
        private void CameraService_ConnectionStatusChanged(object sender, bool isConnected)
        {
            Dispatcher.Invoke(() =>
            {
                IsConnected = isConnected;
            });
        }

        /// <summary>
        /// 错误事件
        /// </summary>
        private void CameraService_ErrorOccurred(object sender, string error)
        {
            Dispatcher.Invoke(() =>
            {
                MessageBox.Show(error, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            });
        }

        #endregion

        #region 按钮事件

        /// <summary>
        /// 枚举相机
        /// </summary>
        private async void EnumInterfaceButton_Click(object sender, RoutedEventArgs e)
        {
            _cameras = await _cameraService.EnumCamerasAsync();

            DeviceListPanel.Children.Clear();
            foreach (var camera in _cameras)
            {
                var radioButton = new RadioButton
                {
                    Content = camera.DisplayName,
                    Tag = camera,
                    Margin = new Thickness(5),
                    GroupName = "CameraGroup"
                };
                radioButton.Checked += CameraRadioButton_Checked;
                DeviceListPanel.Children.Add(radioButton);
            }

            if (_cameras.Count > 0)
            {
                MessageBox.Show($"找到 {_cameras.Count} 个相机", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show("未找到相机设备", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            }

            UpdateUIState();
        }

        /// <summary>
        /// 接口选择改变（保留此方法但留空）
        /// </summary>
        private void InterfaceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // 模拟相机不需要接口选择
        }

        /// <summary>
        /// 刷新设备列表
        /// </summary>
        private async void EnumDeviceButton_Click(object sender, RoutedEventArgs e)
        {
            _cameras = await _cameraService.EnumCamerasAsync();

            DeviceListPanel.Children.Clear();
            foreach (var camera in _cameras)
            {
                var radioButton = new RadioButton
                {
                    Content = camera.DisplayName,
                    Tag = camera,
                    Margin = new Thickness(5),
                    GroupName = "CameraGroup"
                };
                radioButton.Checked += CameraRadioButton_Checked;
                DeviceListPanel.Children.Add(radioButton);
            }

            if (_cameras.Count == 0)
            {
                MessageBox.Show("未找到相机设备", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            }

            UpdateUIState();
        }

        /// <summary>
        /// 相机选择改变
        /// </summary>
        private void CameraRadioButton_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton radioButton && radioButton.Tag is CameraInfo camera)
            {
                _selectedCamera = camera;
                UpdateUIState();
            }
        }

        /// <summary>
        /// 连接相机
        /// </summary>
        private async void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedCamera == null)
            {
                MessageBox.Show("请先选择相机", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (await _cameraService.ConnectAsync(_selectedCamera))
            {
                // 更新参数显示
                ExposureSlider.Value = await _cameraService.GetExposureTimeAsync();
                GainSlider.Value = await _cameraService.GetGainAsync();

                MessageBox.Show("相机连接成功", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show("相机连接失败", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            UpdateUIState();
        }

        /// <summary>
        /// 断开连接
        /// </summary>
        private void DisconnectButton_Click(object sender, RoutedEventArgs e)
        {
            _cameraService.Disconnect();
            PreviewImage.Source = null;
            NoImageText.Visibility = Visibility.Visible;
            UpdateUIState();
        }

        /// <summary>
        /// 开始采集
        /// </summary>
        private async void StartGrabButton_Click(object sender, RoutedEventArgs e)
        {
            if (await _cameraService.StartGrabbingAsync())
            {
                UpdateUIState();
            }
            else
            {
                MessageBox.Show("开始采集失败", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 停止采集
        /// </summary>
        private void StopGrabButton_Click(object sender, RoutedEventArgs e)
        {
            _cameraService.StopGrabbing();
            UpdateUIState();
        }

        /// <summary>
        /// 曝光时间改变
        /// </summary>
        private async void ExposureSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (ExposureValueText != null)
            {
                ExposureValueText.Text = $"{e.NewValue:F0} μs";
                if (_cameraService?.IsConnected == true)
                {
                    await _cameraService.SetExposureTimeAsync((float)e.NewValue);
                }
            }
        }

        /// <summary>
        /// 增益改变
        /// </summary>
        private async void GainSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (GainValueText != null)
            {
                GainValueText.Text = $"{e.NewValue:F1} dB";
                if (_cameraService?.IsConnected == true)
                {
                    await _cameraService.SetGainAsync((float)e.NewValue);
                }
            }
        }

        /// <summary>
        /// 保存配置
        /// </summary>
        private void SaveConfigButton_Click(object sender, RoutedEventArgs e)
        {
            // TODO: 保存相机配置到文件
            MessageBox.Show("配置保存成功", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        /// <summary>
        /// 关闭窗口
        /// </summary>
        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        /// <summary>
        /// 窗口关闭
        /// </summary>
        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            _cameraService?.Dispose();
        }

        #endregion

        #region UI更新

        /// <summary>
        /// 更新UI状态
        /// </summary>
        private void UpdateUIState()
        {
            bool hasCamera = _cameras.Count > 0;
            bool isConnected = _cameraService.IsConnected;
            bool isGrabbing = _cameraService.IsGrabbing;

            EnumDeviceButton.IsEnabled = !isConnected;
            ConnectButton.IsEnabled = hasCamera && _selectedCamera != null && !isConnected;
            DisconnectButton.IsEnabled = isConnected;

            ExposureSlider.IsEnabled = isConnected && !isGrabbing;
            GainSlider.IsEnabled = isConnected && !isGrabbing;

            StartGrabButton.IsEnabled = isConnected && !isGrabbing;
            StopGrabButton.IsEnabled = isConnected && isGrabbing;

            SaveConfigButton.IsEnabled = isConnected;
        }

        #endregion

        #region INotifyPropertyChanged

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        #endregion
    }
}
