using SkiaSharp;
using SkiaSharp.Views.WPF;
using System.Windows;
using System.Windows.Input;
using VisionInspection.Core.Models;
using VisionInspection.Core.ViewModels;

namespace VisionInspection.UI.Controls;

public partial class ROIEditorControl : SKElement
{
    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel),
        typeof(ROIEditorViewModel),
        typeof(ROIEditorControl),
        new PropertyMetadata(null, OnViewModelChanged)
    );

    public ROIEditorViewModel ViewModel
    {
        get => (ROIEditorViewModel)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    private bool _isDragging;
    private SKPoint _dragStart;
    private ROI? _draggedROI;

    public ROIEditorControl()
    {
        PaintSurface += OnPaintSurface;
        MouseDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseUp += OnMouseUp;
    }

    private SKRect CalculateImageRect(SKBitmap image, int controlWidth, int controlHeight)
    {
        float imageAspect = (float)image.Width / image.Height;
        float controlAspect = (float)controlWidth / controlHeight;

        float drawWidth, drawHeight;
        float drawX, drawY;

        if (imageAspect > controlAspect)
        {
            // 图像更宽，以控制区宽度为准
            drawWidth = controlWidth;
            drawHeight = controlWidth / imageAspect;
            drawX = 0;
            drawY = (controlHeight - drawHeight) / 2;
        }
        else
        {
            // 图像更高，以控制区高度为准
            drawWidth = controlHeight * imageAspect;
            drawHeight = controlHeight;
            drawX = (controlWidth - drawWidth) / 2;
            drawY = 0;
        }

        return new SKRect(drawX, drawY, drawX + drawWidth, drawY + drawHeight);
    }

    private static void OnViewModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ROIEditorControl control)
        {
            // 取消旧ViewModel的事件订阅
            if (e.OldValue is ROIEditorViewModel oldViewModel)
            {
                oldViewModel.PropertyChanged -= control.OnViewModelPropertyChanged;
            }
            
            // 订阅新ViewModel的事件
            if (e.NewValue is ROIEditorViewModel newViewModel)
            {
                newViewModel.PropertyChanged += control.OnViewModelPropertyChanged;
            }
            
            control.InvalidateVisual();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // 当CurrentImage、ROIs等属性改变时重绘
        if (e.PropertyName == nameof(ROIEditorViewModel.CurrentImage) ||
            e.PropertyName == nameof(ROIEditorViewModel.ROIs) ||
            e.PropertyName == nameof(ROIEditorViewModel.SelectedROI) ||
            e.PropertyName == nameof(ROIEditorViewModel.IsEditing) ||
            e.PropertyName == nameof(ROIEditorViewModel.StartPoint) ||
            e.PropertyName == nameof(ROIEditorViewModel.EndPoint))
        {
            InvalidateVisual();
        }
    }

    private void OnPaintSurface(object? sender, SkiaSharp.Views.Desktop.SKPaintSurfaceEventArgs e)
    {
        if (ViewModel == null) return;

        var canvas = e.Surface.Canvas;
        var info = e.Info;

        // 清空画布
        canvas.Clear(SKColors.DarkGray);

        // 绘制当前图像（保持比例）
        if (ViewModel.CurrentImage != null)
        {
            var imageRect = CalculateImageRect(ViewModel.CurrentImage, info.Width, info.Height);
            canvas.DrawBitmap(ViewModel.CurrentImage, imageRect);
        }

        // 绘制所有ROI
        foreach (var roi in ViewModel.ROIs)
        {
            DrawROI(canvas, roi, roi == ViewModel.SelectedROI);
        }

        // 绘制正在编辑的ROI
        if (ViewModel.IsEditing)
        {
            DrawEditingROI(canvas);
        }
    }

    private void DrawROI(SKCanvas canvas, Core.Models.ROI roi, bool isSelected)
    {
        using var paint = new SKPaint
        {
            Color = isSelected ? SKColors.Blue : roi.Color,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = isSelected ? 3 : 2,
            IsAntialias = true
        };

        var path = roi.GetPath();
        canvas.DrawPath(path, paint);

        // 绘制ROI名称
        using var textPaint = new SKPaint
        {
            Color = SKColors.Black,
            TextSize = 12,
            IsAntialias = true
        };

        var bbox = roi.GetBoundingBox();
        canvas.DrawText(roi.ROIName, bbox.Left, bbox.Top - 5, textPaint);
    }

    private void DrawEditingROI(SKCanvas canvas)
    {
        using var paint = new SKPaint
        {
            Color = SKColors.Green,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2,
            IsAntialias = true,
            PathEffect = SKPathEffect.CreateDash(new[] { 5f, 5f }, 0)
        };

        if (ViewModel.CurrentShapeType == Core.Models.ROIShapeType.Rectangle)
        {
            var rect = new SKRect(
                Math.Min(ViewModel.StartPoint.X, ViewModel.EndPoint.X),
                Math.Min(ViewModel.StartPoint.Y, ViewModel.EndPoint.Y),
                Math.Max(ViewModel.StartPoint.X, ViewModel.EndPoint.X),
                Math.Max(ViewModel.StartPoint.Y, ViewModel.EndPoint.Y)
            );
            canvas.DrawRect(rect, paint);
        }
        else if (ViewModel.CurrentShapeType == Core.Models.ROIShapeType.Circle)
        {
            var center = new SKPoint(
                (ViewModel.StartPoint.X + ViewModel.EndPoint.X) / 2,
                (ViewModel.StartPoint.Y + ViewModel.EndPoint.Y) / 2
            );
            var radius = (float)Math.Sqrt(
                Math.Pow(ViewModel.EndPoint.X - ViewModel.StartPoint.X, 2) +
                Math.Pow(ViewModel.EndPoint.Y - ViewModel.StartPoint.Y, 2)
            ) / 2;
            canvas.DrawCircle(center, radius, paint);
        }
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel == null) return;

        var point = e.GetPosition(this);
        var skPoint = new SKPoint((float)point.X, (float)point.Y);

        // 检查是否点击了ROI
        foreach (var roi in ViewModel.ROIs)
        {
            if (roi.ContainsPoint(skPoint))
            {
                ViewModel.SelectedROI = roi;
                _isDragging = true;
                _dragStart = skPoint;
                _draggedROI = roi;
                return;
            }
        }

        // 只有在创建ROI模式下才允许绘制新ROI
        if (ViewModel.IsCreatingROI)
        {
            ViewModel.StartDrawingCommand.Execute(skPoint);
        }
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (ViewModel == null) return;

        var point = e.GetPosition(this);
        var skPoint = new SKPoint((float)point.X, (float)point.Y);

        if (_isDragging && _draggedROI != null)
        {
            // 拖动ROI
            var deltaX = skPoint.X - _dragStart.X;
            var deltaY = skPoint.Y - _dragStart.Y;
            _draggedROI.Translate(deltaX, deltaY);
            _dragStart = skPoint;
            InvalidateVisual();
        }
        else if (ViewModel.IsEditing)
        {
            // 更新正在绘制的ROI
            ViewModel.UpdateDrawingCommand.Execute(skPoint);
            InvalidateVisual();
        }
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel == null) return;

        _isDragging = false;
        _draggedROI = null;

        if (ViewModel.IsEditing)
        {
            ViewModel.EndDrawingCommand.Execute(null);
            InvalidateVisual();
        }
    }
}
