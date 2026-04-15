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
            e.PropertyName == nameof(ROIEditorViewModel.HoveredROI) ||
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
        var path = roi.GetPath();
        var bbox = roi.GetBoundingBox();
        bool isHovered = (roi == ViewModel?.HoveredROI);

        // 只绘制边框，不填充背景
        if (isSelected)
        {
            // 绘制选中边框（更粗，亮蓝色）
            using var strokePaint = new SKPaint
            {
                Color = new SKColor(0, 200, 255), // 亮蓝色
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 4,
                IsAntialias = true
            };
            canvas.DrawPath(path, strokePaint);

            // 绘制选中标记（角落的小方块）
            DrawSelectionHandles(canvas, bbox);
        }
        else if (isHovered)
        {
            // 悬停边框：黄色
            using var hoverStrokePaint = new SKPaint
            {
                Color = new SKColor(255, 220, 0),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 3,
                IsAntialias = true
            };
            canvas.DrawPath(path, hoverStrokePaint);
        }
        else
        {
            // 普通ROI绘制
            using var paint = new SKPaint
            {
                Color = roi.Color,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 2,
                IsAntialias = true
            };
            canvas.DrawPath(path, paint);
        }

        // 绘制ROI名称（带背景）
        using var bgPaint = new SKPaint
        {
            Color = isSelected ? new SKColor(0, 150, 255, 220) : new SKColor(0, 0, 0, 180),
            Style = SKPaintStyle.Fill
        };
        using var textPaint = new SKPaint
        {
            Color = SKColors.White,
            TextSize = isSelected ? 14 : 12,
            IsAntialias = true,
            FakeBoldText = isSelected
        };

        var text = roi.ROIName;
        var textBounds = new SKRect();
        textPaint.MeasureText(text, ref textBounds);
        var bgRect = new SKRect(bbox.Left, bbox.Top - 22, bbox.Left + textBounds.Width + 10, bbox.Top - 2);
        canvas.DrawRect(bgRect, bgPaint);
        canvas.DrawText(text, bbox.Left + 5, bbox.Top - 6, textPaint);
    }

    private void DrawSelectionHandles(SKCanvas canvas, SKRect bbox)
    {
        using var handlePaint = new SKPaint
        {
            Color = new SKColor(0, 150, 255),
            Style = SKPaintStyle.Fill
        };

        float handleSize = 8;
        // 四个角
        canvas.DrawRect(new SKRect(bbox.Left - handleSize/2, bbox.Top - handleSize/2, bbox.Left + handleSize/2, bbox.Top + handleSize/2), handlePaint);
        canvas.DrawRect(new SKRect(bbox.Right - handleSize/2, bbox.Top - handleSize/2, bbox.Right + handleSize/2, bbox.Top + handleSize/2), handlePaint);
        canvas.DrawRect(new SKRect(bbox.Left - handleSize/2, bbox.Bottom - handleSize/2, bbox.Left + handleSize/2, bbox.Bottom + handleSize/2), handlePaint);
        canvas.DrawRect(new SKRect(bbox.Right - handleSize/2, bbox.Bottom - handleSize/2, bbox.Right + handleSize/2, bbox.Bottom + handleSize/2), handlePaint);
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

        // 更新鼠标位置
        ViewModel.MousePosition = skPoint;

        // 检查鼠标悬停在哪个ROI上
        ROI? hoveredROI = null;
        foreach (var roi in ViewModel.ROIs)
        {
            if (roi.ContainsPoint(skPoint))
            {
                hoveredROI = roi;
                break;
            }
        }

        if (ViewModel.HoveredROI != hoveredROI)
        {
            ViewModel.HoveredROI = hoveredROI;
            InvalidateVisual();
        }

        // 更新鼠标光标
        if (hoveredROI != null || _isDragging)
        {
            Cursor = Cursors.Hand;
        }
        else if (ViewModel.IsCreatingROI)
        {
            Cursor = Cursors.Cross;
        }
        else
        {
            Cursor = Cursors.Arrow;
        }

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
