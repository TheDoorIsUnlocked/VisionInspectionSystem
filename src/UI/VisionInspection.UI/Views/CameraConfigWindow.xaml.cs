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
using VisionInspection.Core.Models;
using VisionInspection.Core.Services;
using VisionInspection.UI.Services;

namespace VisionInspection.UI.Views
{
    /// <summary>
    /// 相机配置窗口
    /// 参考VM程序架构：使用CameraManager单例管理相机，图像通过CameraManager共享
    /// </summary>
    public partial class CameraConfigWindow : Window, INotifyPropertyChanged
    {
        private readonly CameraManager _cameraManager;
        private readonly CameraConfigManager _configManager;
        private List<CameraInfo> _cameras = new();
        private CameraInfo _selectedCamera = null;
        private CameraConfiguration _cameraConfig;

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

            // 使用 CameraManager 单例
            _cameraManager = CameraManager.Instance;
            _configManager = CameraConfigManager.Instance;
            
            // 加载保存的配置
            _cameraConfig = _configManager.LoadConfig();
            
            // 设置相机服务（如果还没有设置）
            if (_cameraManager.CurrentCameraService == null)
            {
                _cameraManager.SetCameraService(new HikvisionCameraService());
            }
            
            // 订阅 CameraManager 的事件用于本地预览
            _cameraManager.ImageGrabbed += CameraManager_ImageGrabbed;
            _cameraManager.ConnectionStatusChanged += CameraManager_ConnectionStatusChanged;
            _cameraManager.ErrorOccurred += CameraManager_ErrorOccurred;
            
            // 同步连接状态
            IsConnected = _cameraManager.IsConnected;

            // 加载保存的参数范围到Slider
            LoadSavedParameterRanges();

            UpdateUIState();
            
            // 窗口加载完成后，尝试自动连接上次使用的相机
            Loaded += CameraConfigWindow_Loaded;
        }
        
        /// <summary>
        /// 窗口加载完成事件 - 尝试恢复上次连接的相机
        /// </summary>
        private async void CameraConfigWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // 如果配置中有上次使用的相机，尝试自动搜索并选择
            if (!string.IsNullOrEmpty(_cameraConfig.LastCameraId) || 
                !string.IsNullOrEmpty(_cameraConfig.LastCameraSerialNumber))
            {
                try
                {
                    // 先搜索相机
                    await SearchCamerasAsync();
                    
                    // 查找匹配的相机
                    var matchedCamera = _cameras.FirstOrDefault(c => 
                        c.Id == _cameraConfig.LastCameraId || 
                        c.SerialNumber == _cameraConfig.LastCameraSerialNumber);
                    
                    if (matchedCamera != null)
                    {
                        _selectedCamera = matchedCamera;
                        
                        // 在UI中选中该相机
                        foreach (var child in DeviceListPanel.Children)
                        {
                            if (child is RadioButton rb && rb.Tag is CameraInfo ci)
                            {
                                if (ci.Id == matchedCamera.Id || ci.SerialNumber == matchedCamera.SerialNumber)
                                {
                                    rb.IsChecked = true;
                                    break;
                                }
                            }
                        }
                        
                        UpdateUIState();
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"自动恢复相机配置失败: {ex.Message}");
                }
            }
        }
        
        /// <summary>
        /// 加载保存的参数范围
        /// </summary>
        private void LoadSavedParameterRanges()
        {
            try
            {
                // 设置Slider的范围为保存的值
                ExposureSlider.Minimum = _cameraConfig.ExposureTimeMin;
                ExposureSlider.Maximum = _cameraConfig.ExposureTimeMax;
                GainSlider.Minimum = _cameraConfig.GainMin;
                GainSlider.Maximum = _cameraConfig.GainMax;
                
                // 设置当前值为保存的值（仅作为初始显示）
                ExposureSlider.Value = _cameraConfig.ExposureTime;
                GainSlider.Value = _cameraConfig.Gain;
                
                // 更新显示文本
                if (ExposureValueText != null)
                    ExposureValueText.Text = $"{_cameraConfig.ExposureTime:F0} μs";
                if (GainValueText != null)
                    GainValueText.Text = $"{_cameraConfig.Gain:F1} dB";
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"加载保存的参数范围失败: {ex.Message}");
            }
        }

        #region CameraManager 事件处理

        /// <summary>
        /// 相机图像采集事件 - 用于本地预览
        /// 参考VM程序：相机配置窗口直接显示图像
        /// </summary>
        private void CameraManager_ImageGrabbed(object sender, CameraImageData imageData)
        {
            Dispatcher.Invoke(() =>
            {
                var bitmap = ConvertCameraImageToBitmap(imageData);
                if (bitmap != null)
                {
                    PreviewImage.Source = bitmap;
                    NoImageText.Visibility = Visibility.Collapsed;
                }
            });
        }

        /// <summary>
        /// 相机连接状态改变事件
        /// </summary>
        private void CameraManager_ConnectionStatusChanged(object sender, bool isConnected)
        {
            Dispatcher.Invoke(() =>
            {
                IsConnected = isConnected;
            });
        }

        /// <summary>
        /// 相机错误事件
        /// </summary>
        private void CameraManager_ErrorOccurred(object sender, string error)
        {
            Dispatcher.Invoke(() =>
            {
                MessageBox.Show(error, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            });
        }

        /// <summary>
        /// 将相机图像数据转换为BitmapSource
        /// </summary>
        private BitmapSource ConvertCameraImageToBitmap(CameraImageData imageData)
        {
            try
            {
                if (imageData?.Data == null || imageData.Data.Length == 0)
                    return null;

                PixelFormat pixelFormat;
                int stride;

                if (imageData.IsColor && imageData.Channels == 3)
                {
                    // RGB24格式
                    pixelFormat = PixelFormats.Rgb24;
                    stride = imageData.Width * 3;
                }
                else
                {
                    // Mono8格式 - 需要转换为灰度
                    pixelFormat = PixelFormats.Gray8;
                    stride = imageData.Width;
                }

                var bitmap = BitmapSource.Create(
                    imageData.Width,
                    imageData.Height,
                    96, 96,
                    pixelFormat,
                    null,
                    imageData.Data,
                    stride);

                bitmap.Freeze();
                return bitmap;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"转换图像异常：{ex.Message}");
                return null;
            }
        }

        #endregion

        #region 按钮事件

        /// <summary>
        /// 搜索相机接口按钮点击
        /// </summary>
        private async void EnumInterfaceButton_Click(object sender, RoutedEventArgs e)
        {
            await SearchCamerasAsync();
        }

        /// <summary>
        /// 刷新设备列表按钮点击
        /// </summary>
        private async void EnumDeviceButton_Click(object sender, RoutedEventArgs e)
        {
            await SearchCamerasAsync();
        }

        /// <summary>
        /// 搜索相机
        /// </summary>
        private async Task SearchCamerasAsync()
        {
            try
            {
                EnumInterfaceButton.IsEnabled = false;
                EnumDeviceButton.IsEnabled = false;
                
                _cameras = await _cameraManager.EnumCamerasAsync();
                DeviceListPanel.Children.Clear();
                
                if (_cameras.Count == 0)
                {
                    EnumInterfaceButton.IsEnabled = true;
                    EnumDeviceButton.IsEnabled = true;
                    MessageBox.Show("未找到相机设备", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                
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
                
                EnumDeviceButton.IsEnabled = true;
                
                if (_cameras.Count > 0)
                {
                    MessageBox.Show($"找到 {_cameras.Count} 个相机", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"搜索相机失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                EnumInterfaceButton.IsEnabled = true;
                UpdateUIState();
            }
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
        /// 连接相机按钮点击
        /// </summary>
        private async void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedCamera == null)
            {
                MessageBox.Show("请先选择相机", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                ConnectButton.IsEnabled = false;

                if (await _cameraManager.ConnectAsync(_selectedCamera))
                {
                    // 保存当前相机信息到配置
                    _configManager.UpdateLastCamera(
                        _selectedCamera.Id, 
                        _selectedCamera.Name, 
                        _selectedCamera.SerialNumber);

                    // 读取参数范围并设置Slider
                    await LoadCameraParameterRangesAsync();

                    // 更新当前参数值
                    var currentExposure = await _cameraManager.GetExposureTimeAsync();
                    var currentGain = await _cameraManager.GetGainAsync();
                    
                    ExposureSlider.Value = currentExposure;
                    GainSlider.Value = currentGain;
                    
                    // 保存当前参数值
                    _configManager.UpdateCameraParameters(currentExposure, currentGain);

                    MessageBox.Show("相机连接成功", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show("相机连接失败", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"连接异常: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                UpdateUIState();
            }
        }

        /// <summary>
        /// 加载相机参数范围
        /// </summary>
        private async Task LoadCameraParameterRangesAsync()
        {
            try
            {
                // 获取曝光时间范围
                var exposureRange = await _cameraManager.GetExposureTimeRangeAsync();
                
                // 验证范围是否有效
                if (exposureRange.Min > 0 && exposureRange.Max > exposureRange.Min)
                {
                    ExposureSlider.Minimum = exposureRange.Min;
                    ExposureSlider.Maximum = exposureRange.Max;
                    Debug.WriteLine($"曝光范围: {exposureRange.Min} - {exposureRange.Max}");
                }
                else
                {
                    // 使用默认值
                    ExposureSlider.Minimum = 20;
                    ExposureSlider.Maximum = 10000000;
                    Debug.WriteLine($"曝光范围无效，使用默认值: {ExposureSlider.Minimum} - {ExposureSlider.Maximum}");
                }

                // 获取增益范围
                var gainRange = await _cameraManager.GetGainRangeAsync();
                
                // 验证范围是否有效
                if (gainRange.Min >= 0 && gainRange.Max > gainRange.Min)
                {
                    GainSlider.Minimum = gainRange.Min;
                    GainSlider.Maximum = gainRange.Max;
                    Debug.WriteLine($"增益范围: {gainRange.Min} - {gainRange.Max}");
                }
                else
                {
                    // 使用默认值
                    GainSlider.Minimum = 0;
                    GainSlider.Maximum = 20;
                    Debug.WriteLine($"增益范围无效，使用默认值: {GainSlider.Minimum} - {GainSlider.Maximum}");
                }
                
                // 保存参数范围到配置
                _configManager.UpdateParameterRanges(
                    (float)ExposureSlider.Minimum, 
                    (float)ExposureSlider.Maximum,
                    (float)GainSlider.Minimum, 
                    (float)GainSlider.Maximum);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"加载参数范围异常：{ex.Message}");
                // 使用默认范围
                ExposureSlider.Minimum = 20;
                ExposureSlider.Maximum = 10000000;
                GainSlider.Minimum = 0;
                GainSlider.Maximum = 20;
            }
        }

        /// <summary>
        /// 断开连接按钮点击
        /// </summary>
        private void DisconnectButton_Click(object sender, RoutedEventArgs e)
        {
            _cameraManager.Disconnect();
            PreviewImage.Source = null;
            NoImageText.Visibility = Visibility.Visible;
            UpdateUIState();
        }

        /// <summary>
        /// 开始采集按钮点击
        /// </summary>
        private async void StartGrabButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (await _cameraManager.StartGrabbingAsync())
                {
                    UpdateUIState();
                }
                else
                {
                    MessageBox.Show("开始采集失败", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"开始采集异常: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 停止采集按钮点击
        /// </summary>
        private void StopGrabButton_Click(object sender, RoutedEventArgs e)
        {
            _cameraManager.StopGrabbing();
            UpdateUIState();
        }

        /// <summary>
        /// 曝光时间改变 - 实时调整
        /// </summary>
        private async void ExposureSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_cameraManager?.IsConnected == true && ExposureValueText != null)
            {
                float value = (float)e.NewValue;
                ExposureValueText.Text = $"{value:F0} μs";
                await _cameraManager.SetExposureTimeAsync(value);
                
                // 保存当前曝光值到配置
                _configManager.UpdateCameraParameters(value, (float)GainSlider.Value);
            }
        }

        /// <summary>
        /// 增益改变 - 实时调整
        /// </summary>
        private async void GainSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_cameraManager?.IsConnected == true && GainValueText != null)
            {
                float value = (float)e.NewValue;
                GainValueText.Text = $"{value:F1} dB";
                await _cameraManager.SetGainAsync(value);
                
                // 保存当前增益值到配置
                _configManager.UpdateCameraParameters((float)ExposureSlider.Value, value);
            }
        }

        /// <summary>
        /// 保存配置按钮点击
        /// </summary>
        private void SaveConfigButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 保存当前所有配置
                var config = _configManager.LoadConfig();
                config.ExposureTime = (float)ExposureSlider.Value;
                config.Gain = (float)GainSlider.Value;
                config.ExposureTimeMin = (float)ExposureSlider.Minimum;
                config.ExposureTimeMax = (float)ExposureSlider.Maximum;
                config.GainMin = (float)GainSlider.Minimum;
                config.GainMax = (float)GainSlider.Maximum;
                
                _configManager.SaveConfig(config);
                
                MessageBox.Show("配置已保存", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"保存配置失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 关闭窗口按钮点击
        /// </summary>
        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            // 不真正关闭窗口，只是隐藏
            Hide();
        }

        /// <summary>
        /// 窗口关闭事件
        /// </summary>
        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            // 取消关闭操作，改为隐藏窗口
            e.Cancel = true;
            Hide();
        }

        #endregion

        #region UI更新

        /// <summary>
        /// 更新UI状态
        /// </summary>
        private void UpdateUIState()
        {
            // 搜索按钮始终可用
            EnumInterfaceButton.IsEnabled = true;
            EnumDeviceButton.IsEnabled = _cameras.Count > 0;

            // 连接按钮
            ConnectButton.IsEnabled = _selectedCamera != null && !IsConnected;
            DisconnectButton.IsEnabled = IsConnected;

            // 参数控制 - 连接后可用，采集时也可调整
            bool canControlParams = IsConnected;
            ExposureSlider.IsEnabled = canControlParams;
            GainSlider.IsEnabled = canControlParams;

            // 采集按钮
            StartGrabButton.IsEnabled = IsConnected && !_cameraManager.IsGrabbing;
            StopGrabButton.IsEnabled = IsConnected && _cameraManager.IsGrabbing;

            // 保存配置按钮
            SaveConfigButton.IsEnabled = IsConnected;
        }

        #endregion

        #region INotifyPropertyChanged

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        #endregion
    }
}
