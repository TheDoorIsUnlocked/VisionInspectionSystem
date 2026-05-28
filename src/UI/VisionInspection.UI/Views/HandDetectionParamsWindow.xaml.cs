using System;
using System.Windows;
using System.Windows.Controls;

namespace VisionInspection.UI.Views
{
    /// <summary>
    /// HandDetectionParamsWindow.xaml 的交互逻辑
    /// 手部检测高级参数配置窗口
    /// </summary>
    public partial class HandDetectionParamsWindow : Window
    {
        #region 属性
        
        /// <summary>
        /// 推理间隔（跳帧数）
        /// </summary>
        public int InferenceInterval { get; private set; } = 2;
        
        /// <summary>
        /// 平滑窗口大小
        /// </summary>
        public int SmoothWindowSize { get; private set; } = 5;
        
        /// <summary>
        /// 平滑系数（Alpha）
        /// </summary>
        public float SmoothAlpha { get; private set; } = 0.7f;
        
        /// <summary>
        /// 骨架置信度阈值
        /// </summary>
        public float SkeletonConfidenceThreshold { get; private set; } = 0.3f;
        
        #endregion

        #region 构造函数
        
        public HandDetectionParamsWindow()
        {
            InitializeComponent();
        }
        
        public HandDetectionParamsWindow(int inferenceInterval, int smoothWindowSize, float smoothAlpha, float skeletonThreshold)
        {
            InitializeComponent();
            
            // 设置初始值
            InferenceInterval = inferenceInterval;
            SmoothWindowSize = smoothWindowSize;
            SmoothAlpha = smoothAlpha;
            SkeletonConfidenceThreshold = skeletonThreshold;
            
            // 更新UI
            InferenceIntervalSlider.Value = inferenceInterval;
            SmoothWindowSlider.Value = smoothWindowSize;
            SmoothAlphaSlider.Value = smoothAlpha;
            SkeletonThresholdSlider.Value = skeletonThreshold;
            
            UpdateValueDisplays();
        }
        
        #endregion

        #region 事件处理
        
        private void InferenceIntervalSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            InferenceInterval = (int)e.NewValue;
            InferenceIntervalValue.Text = InferenceInterval.ToString();
        }
        
        private void SmoothWindowSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            SmoothWindowSize = (int)e.NewValue;
            SmoothWindowValue.Text = SmoothWindowSize.ToString();
        }
        
        private void SmoothAlphaSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            SmoothAlpha = (float)e.NewValue;
            SmoothAlphaValue.Text = SmoothAlpha.ToString("F2");
        }
        
        private void SkeletonThresholdSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            SkeletonConfidenceThreshold = (float)e.NewValue;
            SkeletonThresholdValue.Text = SkeletonConfidenceThreshold.ToString("F2");
        }
        
        private void PresetRealtime_Click(object sender, RoutedEventArgs e)
        {
            // 实时优先预设
            InferenceIntervalSlider.Value = 1;
            SmoothWindowSlider.Value = 3;
            SmoothAlphaSlider.Value = 0.85;
            SkeletonThresholdSlider.Value = 0.2;
            UpdateValueDisplays();
        }
        
        private void PresetBalanced_Click(object sender, RoutedEventArgs e)
        {
            // 平衡模式预设
            InferenceIntervalSlider.Value = 2;
            SmoothWindowSlider.Value = 5;
            SmoothAlphaSlider.Value = 0.7;
            SkeletonThresholdSlider.Value = 0.3;
            UpdateValueDisplays();
        }
        
        private void PresetStable_Click(object sender, RoutedEventArgs e)
        {
            // 稳定优先预设
            InferenceIntervalSlider.Value = 3;
            SmoothWindowSlider.Value = 8;
            SmoothAlphaSlider.Value = 0.5;
            SkeletonThresholdSlider.Value = 0.4;
            UpdateValueDisplays();
        }
        
        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
        
        private void ConfirmButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }
        
        #endregion

        #region 私有方法
        
        private void UpdateValueDisplays()
        {
            InferenceIntervalValue.Text = InferenceInterval.ToString();
            SmoothWindowValue.Text = SmoothWindowSize.ToString();
            SmoothAlphaValue.Text = SmoothAlpha.ToString("F2");
            SkeletonThresholdValue.Text = SkeletonConfidenceThreshold.ToString("F2");
        }
        
        #endregion
    }
}