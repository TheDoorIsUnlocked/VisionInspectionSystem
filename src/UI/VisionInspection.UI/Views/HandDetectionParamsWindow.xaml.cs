using System;
using System.Windows;
using System.Windows.Controls;

namespace VisionInspection.UI.Views
{
    public partial class HandDetectionParamsWindow : Window
    {
        #region 属性

        public int InferenceInterval { get; private set; } = 2;
        public int SmoothWindowSize { get; private set; } = 5;
        public float SmoothAlpha { get; private set; } = 0.7f;
        public float SkeletonConfidenceThreshold { get; private set; } = 0.3f;

        // 面部过滤
        public bool EnableFaceFilter { get; private set; } = true;
        public float FaceFilterUpperRatio { get; private set; } = 0.38f;

        // 手部结构验证
        public bool EnableHandStructureCheck { get; private set; } = true;
        public float HandStructureWristTipRatio { get; private set; } = 0.18f;

        // 检测置信度与尺寸
        public float DetectionConfidenceThreshold { get; private set; } = 0.08f;
        public float MinBoxAreaRatio { get; private set; } = 0.0005f;

        #endregion

        #region 构造函数

        public HandDetectionParamsWindow()
        {
            InitializeComponent();
        }

        public HandDetectionParamsWindow(
            int inferenceInterval, int smoothWindowSize, float smoothAlpha, float skeletonThreshold,
            bool enableFaceFilter, float faceFilterUpperRatio,
            bool enableHandStructureCheck, float handStructureWristTipRatio,
            float detectionConfidenceThreshold, float minBoxAreaRatio)
        {
            InitializeComponent();

            InferenceInterval = inferenceInterval;
            SmoothWindowSize = smoothWindowSize;
            SmoothAlpha = smoothAlpha;
            SkeletonConfidenceThreshold = skeletonThreshold;
            EnableFaceFilter = enableFaceFilter;
            FaceFilterUpperRatio = faceFilterUpperRatio;
            EnableHandStructureCheck = enableHandStructureCheck;
            HandStructureWristTipRatio = handStructureWristTipRatio;
            DetectionConfidenceThreshold = detectionConfidenceThreshold;
            MinBoxAreaRatio = minBoxAreaRatio;

            InferenceIntervalSlider.Value = inferenceInterval;
            SmoothWindowSlider.Value = smoothWindowSize;
            SmoothAlphaSlider.Value = smoothAlpha;
            SkeletonThresholdSlider.Value = skeletonThreshold;
            EnableFaceFilterCheckBox.IsChecked = enableFaceFilter;
            FaceFilterUpperSlider.Value = faceFilterUpperRatio;
            EnableHandStructureCheckBox.IsChecked = enableHandStructureCheck;
            WristTipRatioSlider.Value = handStructureWristTipRatio;
            DetectionConfidenceSlider.Value = detectionConfidenceThreshold;
            MinBoxAreaSlider.Value = minBoxAreaRatio;

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

        private void FaceFilterCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            EnableFaceFilter = EnableFaceFilterCheckBox.IsChecked == true;
        }

        private void FaceFilterUpperSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            FaceFilterUpperRatio = (float)e.NewValue;
            FaceFilterUpperValue.Text = FaceFilterUpperRatio.ToString("F2");
        }

        private void HandStructureCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            EnableHandStructureCheck = EnableHandStructureCheckBox.IsChecked == true;
        }

        private void WristTipRatioSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            HandStructureWristTipRatio = (float)e.NewValue;
            WristTipRatioValue.Text = HandStructureWristTipRatio.ToString("F2");
        }

        private void DetectionConfidenceSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            DetectionConfidenceThreshold = (float)e.NewValue;
            DetectionConfidenceValue.Text = DetectionConfidenceThreshold.ToString("F2");
        }

        private void MinBoxAreaSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            MinBoxAreaRatio = (float)e.NewValue;
            MinBoxAreaValue.Text = MinBoxAreaRatio.ToString("F4");
        }

        private void PresetRealtime_Click(object sender, RoutedEventArgs e)
        {
            InferenceIntervalSlider.Value = 1;
            SmoothWindowSlider.Value = 3;
            SmoothAlphaSlider.Value = 0.85;
            SkeletonThresholdSlider.Value = 0.2;
            UpdateValueDisplays();
        }

        private void PresetBalanced_Click(object sender, RoutedEventArgs e)
        {
            InferenceIntervalSlider.Value = 2;
            SmoothWindowSlider.Value = 5;
            SmoothAlphaSlider.Value = 0.7;
            SkeletonThresholdSlider.Value = 0.3;
            UpdateValueDisplays();
        }

        private void PresetStable_Click(object sender, RoutedEventArgs e)
        {
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
            FaceFilterUpperValue.Text = FaceFilterUpperRatio.ToString("F2");
            WristTipRatioValue.Text = HandStructureWristTipRatio.ToString("F2");
            DetectionConfidenceValue.Text = DetectionConfidenceThreshold.ToString("F2");
            MinBoxAreaValue.Text = MinBoxAreaRatio.ToString("F4");
        }

        #endregion
    }
}