using System.Windows;
using System.Windows.Controls;
using SkiaSharp;
using SkiaSharp.Views.Desktop;
using VisionInspection.UI.ViewModels;

namespace VisionInspection.UI.Controls
{
    /// <summary>
    /// 相机预览控件：绑定 CameraViewItem，按 Uniform 缩放绘制该路相机最新画面。
    /// 检测框已由 MainViewModel 在更新位图时叠加，本控件仅负责显示。
    /// </summary>
    public partial class CameraPreviewControl : UserControl
    {
        public CameraPreviewControl()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;
            SizeChanged += (_, _) => InvalidatePreview();
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.OldValue is CameraViewItem oldItem)
            {
                oldItem.PropertyChanged -= OnItemPropertyChanged;
            }
            if (e.NewValue is CameraViewItem newItem)
            {
                newItem.PropertyChanged += OnItemPropertyChanged;
            }
            InvalidatePreview();
        }

        private void OnItemPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(CameraViewItem.CurrentImage) ||
                e.PropertyName == nameof(CameraViewItem.IsConnected))
            {
                InvalidatePreview();
            }
        }

        private void InvalidatePreview()
        {
            PreviewCanvas.InvalidateVisual();
        }

        private void OnPaintSurface(object? sender, SKPaintSurfaceEventArgs e)
        {
            var canvas = e.Surface.Canvas;
            canvas.Clear(SKColors.Transparent);

            if (DataContext is not CameraViewItem item || item.CurrentImage == null)
                return;

            var image = item.CurrentImage;
            var info = e.Info;

            // Uniform 缩放：保持宽高比居中绘制
            var dest = CalculateImageRect(image, info.Width, info.Height);
            if (dest.Width <= 0 || dest.Height <= 0) return;

            canvas.DrawBitmap(image, dest);
        }

        /// <summary>计算保持宽高比的居中绘制区域（与 ROIEditorControl 一致）</summary>
        private static SKRect CalculateImageRect(SKBitmap image, int controlWidth, int controlHeight)
        {
            float imageAspect = (float)image.Width / image.Height;
            float controlAspect = (float)controlWidth / controlHeight;

            float drawWidth, drawHeight, drawX, drawY;
            if (imageAspect > controlAspect)
            {
                drawWidth = controlWidth;
                drawHeight = controlWidth / imageAspect;
                drawX = 0;
                drawY = (controlHeight - drawHeight) / 2;
            }
            else
            {
                drawWidth = controlHeight * imageAspect;
                drawHeight = controlHeight;
                drawX = (controlWidth - drawWidth) / 2;
                drawY = 0;
            }
            return new SKRect(drawX, drawY, drawX + drawWidth, drawY + drawHeight);
        }
    }
}
