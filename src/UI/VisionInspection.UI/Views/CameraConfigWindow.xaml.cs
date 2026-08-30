using SkiaSharp;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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

        // 使用DispatcherTimer延迟更新预览，避免频繁刷新
        private DispatcherTimer? _previewUpdateTimer;
        private BitmapSource? _pendingFrame;
        private readonly object _frameLock = new object();

        public CameraConfigWindow()
        {
            InitializeComponent();
            DataContext = this;

            // 使用 CameraManager 单例
            _cameraManager = CameraManager.Instance;
            _configManager = CameraConfigManager.Instance;
            
            // 加载保存的配置
            _cameraConfig = _configManager.LoadConfig();
            
            // 设置相机服务（默认使用笔记本摄像头）
            if (_cameraManager.CurrentCameraService == null)
            {
                _cameraManager.SetCameraService(new WebCameraService());
            }
            
            // 注意：事件订阅移到 IsVisibleChanged 中处理，避免隐藏时仍接收事件
            // 同步连接状态
            IsConnected = _cameraManager.IsConnected;

            // 加载保存的参数范围到Slider
            LoadSavedParameterRanges();

            UpdateUIState();
            
            // 初始化预览更新定时器（30fps）
            _previewUpdateTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, OnPreviewUpdateTimerTick, Dispatcher);
            _previewUpdateTimer.Stop(); // 初始状态停止
            
            // 窗口加载完成后，尝试自动连接上次使用的相机
            Loaded += CameraConfigWindow_Loaded;
            
            // 订阅可见性改变事件，在隐藏时取消事件订阅
            IsVisibleChanged += CameraConfigWindow_IsVisibleChanged;
        }
        
        /// <summary>
        /// 窗口可见性改变事件 - 在隐藏时取消订阅，显示时重新订阅
        /// </summary>
        private void CameraConfigWindow_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.NewValue is bool isVisible)
            {
                if (isVisible)
                {
                    // 窗口显示时，重新订阅事件
                    _cameraManager.ImageGrabbed -= CameraManager_ImageGrabbed; // 先取消避免重复订阅
                    _cameraManager.ImageGrabbed += CameraManager_ImageGrabbed;
                    _cameraManager.ConnectionStatusChanged -= CameraManager_ConnectionStatusChanged;
                    _cameraManager.ConnectionStatusChanged += CameraManager_ConnectionStatusChanged;
                    _cameraManager.ErrorOccurred -= CameraManager_ErrorOccurred;
                    _cameraManager.ErrorOccurred += CameraManager_ErrorOccurred;
                    Debug.WriteLine("相机配置窗口：已订阅相机事件");
                }
                else
                {
                    // 窗口隐藏时，取消订阅事件，避免不必要的处理
                    _cameraManager.ImageGrabbed -= CameraManager_ImageGrabbed;
                    _cameraManager.ConnectionStatusChanged -= CameraManager_ConnectionStatusChanged;
                    _cameraManager.ErrorOccurred -= CameraManager_ErrorOccurred;
                    
                    // 停止定时器
                    _previewUpdateTimer?.Stop();
                    Debug.WriteLine("相机配置窗口：已取消相机事件订阅");
                }
            }
        }
        
        /// <summary>
        /// 预览更新定时器回调 - 批量处理帧更新
        /// </summary>
        private void OnPreviewUpdateTimerTick(object? sender, EventArgs e)
        {
            lock (_frameLock)
            {
                if (_pendingFrame != null)
                {
                    PreviewImage.Source = _pendingFrame;
                    NoImageText.Visibility = Visibility.Collapsed;
                    _pendingFrame = null;
                }
            }
        }
        
        /// <summary>
        /// 窗口加载完成事件 - 尝试恢复上次连接的相机
        /// </summary>
        private async void CameraConfigWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // 如果相机已经在采集（主窗口正在使用），不要重新枚举，避免中断采集
            if (_cameraManager.IsGrabbing)
            {
                Debug.WriteLine("相机配置窗口：检测到相机正在采集，跳过自动搜索，避免中断主窗口");
                
                // 如果已连接，显示当前相机信息
                if (_cameraManager.IsConnected && _cameraManager.CurrentCamera != null)
                {
                    _selectedCamera = _cameraManager.CurrentCamera;
                    // 创建一个简单的UI项显示当前相机
                    DeviceListPanel.Children.Clear();
                    var radioButton = new RadioButton
                    {
                        Content = _cameraManager.CurrentCamera.DisplayName,
                        Tag = _cameraManager.CurrentCamera,
                        Margin = new Thickness(5),
                        GroupName = "CameraGroup",
                        IsChecked = true,
                        IsEnabled = false // 禁用选择，因为正在使用中
                    };
                    DeviceListPanel.Children.Add(radioButton);
                    _cameras.Add(_cameraManager.CurrentCamera);
                }
                
                UpdateUIState();
                return;
            }
            
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

        // 用于限制预览更新频率
        private DateTime _lastPreviewUpdate = DateTime.MinValue;
        private readonly TimeSpan _previewUpdateInterval = TimeSpan.FromMilliseconds(100); // 10fps，降低频率减少卡顿

        /// <summary>
        /// 相机图像采集事件 - 用于本地预览
        /// 参考VM程序：相机配置窗口直接显示图像
        /// </summary>
        private void CameraManager_ImageGrabbed(object sender, CameraImageData imageData)
        {
            // 限制更新频率，避免与主窗口竞争资源
            var now = DateTime.Now;
            if (now - _lastPreviewUpdate < _previewUpdateInterval)
            {
                return; // 跳过此帧
            }
            _lastPreviewUpdate = now;

            // 立即复制数据，避免与其他订阅者竞争
            // 因为imageData.Data是共享的byte[]，可能被其他线程修改
            byte[]? dataCopy = null;
            if (imageData.Data != null && imageData.Data.Length > 0)
            {
                dataCopy = new byte[imageData.Data.Length];
                Buffer.BlockCopy(imageData.Data, 0, dataCopy, 0, imageData.Data.Length);
            }
            
            // 在后台线程转换图像，避免阻塞UI
            Task.Run(() =>
            {
                if (dataCopy == null) return;
                
                // 创建新的CameraImageData，使用复制的数据
                var imageDataCopy = new CameraImageData
                {
                    Data = dataCopy,
                    Width = imageData.Width,
                    Height = imageData.Height,
                    IsColor = imageData.IsColor,
                    Channels = imageData.Channels
                };
                
                var bitmap = ConvertCameraImageToBitmapSource(imageDataCopy);
                if (bitmap != null)
                {
                    lock (_frameLock)
                    {
                        _pendingFrame = bitmap;
                    }
                    
                    // 确保定时器在运行
                    if (_previewUpdateTimer?.IsEnabled == false)
                    {
                        Dispatcher.BeginInvoke(() => _previewUpdateTimer?.Start(), DispatcherPriority.Background);
                    }
                }
            });
        }
        
        /// <summary>
        /// 将相机图像数据转换为BitmapSource（直接转换，不使用SKBitmap中间格式）
        /// </summary>
        private BitmapSource? ConvertCameraImageToBitmapSource(CameraImageData imageData)
        {
            try
            {
                if (imageData.Data == null || imageData.Data.Length == 0)
                    return null;

                // 根据图像格式选择WPF像素格式
                // WebCameraService 已经将 BGR 转换为 RGB，所以这里要用 RGB 格式
                PixelFormat format;
                int bytesPerPixel;
                int stride;

                if (imageData.IsColor)
                {
                    if (imageData.Channels == 4)
                    {
                        format = PixelFormats.Rgba64; // RGBA 格式
                        bytesPerPixel = 4;
                    }
                    else if (imageData.Channels == 3)
                    {
                        format = PixelFormats.Rgb24; // RGB 格式（不是BGR！）
                        bytesPerPixel = 3;
                    }
                    else
                    {
                        format = PixelFormats.Rgb24;
                        bytesPerPixel = 3;
                    }
                }
                else
                {
                    format = PixelFormats.Gray8;
                    bytesPerPixel = 1;
                }

                stride = imageData.Width * bytesPerPixel;

                // 确保数据长度正确
                int expectedLength = stride * imageData.Height;
                if (imageData.Data.Length < expectedLength)
                {
                    Debug.WriteLine($"图像数据长度不足: {imageData.Data.Length} < {expectedLength}");
                    return null;
                }

                var bitmapSource = BitmapSource.Create(
                    imageData.Width,
                    imageData.Height,
                    96, 96,
                    format,
                    null,
                    imageData.Data,
                    stride);

                bitmapSource.Freeze(); // 冻结以提高性能
                return bitmapSource;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"转换BitmapSource失败: {ex.Message}");
                return null;
            }
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
                ShowStatus(error, true);
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
                    ShowStatus("未找到相机设备", true);
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
                ShowStatus($"找到 {_cameras.Count} 个相机");
            }
            catch (Exception ex)
            {
                ShowStatus($"搜索相机失败: {ex.Message}", true);
            }
            finally
            {
                EnumInterfaceButton.IsEnabled = true;
                UpdateUIState();
            }
        }

        /// <summary>
        /// RTSP 地址输入变化时刷新连接按钮状态
        /// </summary>
        private void RtspUrlTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
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

                // 网络相机：把发现的 RTSP 地址回填到输入框，便于手动修改
                if (CameraTypeComboBox.SelectedIndex == 2 && camera.ExtInfo is OnvifCameraExt ext)
                {
                    if (RtspUrlTextBox != null)
                        RtspUrlTextBox.Text = ext.RtspUrl;
                }

                UpdateUIState();
            }
        }

        /// <summary>
        /// 连接相机按钮点击
        /// </summary>
        private async void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            // 网络相机：允许手动输入 RTSP 地址直接连接（无需先发现设备）
            if (CameraTypeComboBox.SelectedIndex == 2)
            {
                string rtsp = RtspUrlTextBox?.Text.Trim() ?? "";
                if (string.IsNullOrEmpty(rtsp))
                {
                    MessageBox.Show("请输入 RTSP 地址（可先点「搜索相机」自动发现，再修改地址中的账号密码）",
                        "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var ext = (_selectedCamera?.ExtInfo as OnvifCameraExt) ?? new OnvifCameraExt();
                ext.RtspUrl = rtsp;
                _selectedCamera ??= new CameraInfo
                {
                    Id = $"onvif_manual",
                    Name = "网络相机（手动）",
                    Model = ext.Name,
                    SerialNumber = ext.Ip,
                    InterfaceType = "ONVIF",
                    DisplayName = $"🌐 网络相机（手动）",
                    ExtInfo = ext
                };
                ((OnvifCameraExt)_selectedCamera.ExtInfo!).RtspUrl = rtsp;
            }

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

                    ShowStatus("相机连接成功");
                }
                else
                {
                    ShowStatus("相机连接失败", true);
                }
            }
            catch (Exception ex)
            {
                ShowStatus($"连接异常: {ex.Message}", true);
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
                    ShowStatus("开始采集");
                    UpdateUIState();
                }
                else
                {
                    ShowStatus("开始采集失败", true);
                }
            }
            catch (Exception ex)
            {
                ShowStatus($"开始采集异常: {ex.Message}", true);
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

                ShowStatus("配置已保存");
            }
            catch (Exception ex)
            {
                ShowStatus($"保存配置失败: {ex.Message}", true);
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
            // 取消订阅可见性改变事件
            IsVisibleChanged -= CameraConfigWindow_IsVisibleChanged;
            
            // 停止预览更新定时器
            _previewUpdateTimer?.Stop();
            _previewUpdateTimer = null;
            
            // 清理待处理帧
            lock (_frameLock)
            {
                _pendingFrame = null;
            }
            
            // 停止本地预览，但不停止相机采集（主窗口可能仍在使用）
            // 取消事件订阅，避免内存泄漏
            _cameraManager.ImageGrabbed -= CameraManager_ImageGrabbed;
            _cameraManager.ConnectionStatusChanged -= CameraManager_ConnectionStatusChanged;
            _cameraManager.ErrorOccurred -= CameraManager_ErrorOccurred;
            
            // 允许窗口关闭
            // 注意：不调用 e.Cancel = true，让窗口正常关闭
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

            // 连接按钮（网络相机可在未发现设备时凭 RTSP 地址直接连接）
            bool canConnect = !IsConnected &&
                              (_selectedCamera != null ||
                               (CameraTypeComboBox.SelectedIndex == 2 && !string.IsNullOrWhiteSpace(RtspUrlTextBox?.Text)));
            ConnectButton.IsEnabled = canConnect;
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

        #region 状态显示

        /// <summary>
        /// 在状态栏显示消息
        /// </summary>
        private void ShowStatus(string message, bool isError = false)
        {
            Dispatcher.Invoke(() =>
            {
                if (StatusText != null)
                {
                    StatusText.Text = message;
                    StatusText.Foreground = isError ? new SolidColorBrush(Colors.Red) : new SolidColorBrush(Colors.Black);
                }
            });
        }

        #endregion

        #region 相机类型切换

        /// <summary>
        /// 相机类型切换
        /// </summary>
        private void CameraTypeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // 防止初始化时的空引用
            if (_cameraManager == null || CameraTypeComboBox == null)
                return;

            // 如果当前有连接，先断开
            if (_cameraManager.IsConnected)
            {
                _cameraManager.Disconnect();
                IsConnected = false;
            }

            // 根据选择设置相机服务
            if (CameraTypeComboBox.SelectedIndex == 0)
            {
                // 笔记本摄像头
                _cameraManager.SetCameraService(new WebCameraService());
                ShowStatus("已切换到笔记本摄像头模式");
                if (RtspPanel != null) RtspPanel.Visibility = Visibility.Collapsed;
            }
            else if (CameraTypeComboBox.SelectedIndex == 2)
            {
                // 网络相机（ONVIF 自动发现 + RTSP）
                _cameraManager.SetCameraService(new OnvifCameraService());
                ShowStatus("已切换到网络相机模式（ONVIF 自动发现）");
                if (RtspPanel != null) RtspPanel.Visibility = Visibility.Visible;
            }
            else
            {
                // GigE 工业相机
                _cameraManager.SetCameraService(new HikvisionCameraService());
                ShowStatus("已切换到GigE相机模式");
                if (RtspPanel != null) RtspPanel.Visibility = Visibility.Collapsed;
            }

            // 清空设备列表
            _cameras?.Clear();
            if (DeviceListPanel != null)
                DeviceListPanel.Children.Clear();
            _selectedCamera = null;
            UpdateUIState();
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
