using SkiaSharp;
using SkiaSharp.Views.WPF;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using VisionInspection.Core.Models;
using VisionInspection.Core.ViewModels;

namespace VisionInspection.UI.Controls;

public partial class ROIEditorControl : SKElement
{
    // 静态字体管理器，用于支持中文显示
    private static SKTypeface? _chineseTypeface;
    private static SKFont? _chineseFont;
    
    static ROIEditorControl()
    {
        // 尝试加载系统中文字体
        try
        {
            // 优先尝试常见中文字体
            string[] chineseFonts = { "Microsoft YaHei", "SimHei", "SimSun", "PingFang SC", "Source Han Sans CN" };
            foreach (var fontName in chineseFonts)
            {
                _chineseTypeface = SKTypeface.FromFamilyName(fontName);
                if (_chineseTypeface != null)
                    break;
            }
            
            // 如果没有找到中文字体，使用默认字体
            if (_chineseTypeface == null)
            {
                _chineseTypeface = SKTypeface.Default;
            }
        }
        catch
        {
            _chineseTypeface = SKTypeface.Default;
        }
    }
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

    // 平移拖动相关
    private bool _isPanning;
    private SKPoint _panStart;
    private SKPoint _panStartOffset;

    // 缩放相关
    private float _zoomScale = 1.0f;
    private SKPoint _panOffset = new SKPoint(0, 0);
    private const float MinZoom = 0.1f;
    private const float MaxZoom = 10.0f;
    private const float ZoomStep = 0.1f;
    private bool _isInitialZoomSet = false; // 标记是否已设置初始缩放

    // 双击检测
    private DateTime _lastClickTime;
    private const int DoubleClickInterval = 300; // 毫秒

    public ROIEditorControl()
    {
        PaintSurface += OnPaintSurface;
        MouseDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseUp += OnMouseUp;

        // 确保控件可以接收焦点和滚轮事件
        Focusable = true;

        // 在加载时添加滚轮事件处理
        Loaded += (s, e) =>
        {
            // 添加PreviewMouseWheel事件处理，确保能捕获滚轮事件
            this.PreviewMouseWheel += OnPreviewMouseWheel;

            // 向上遍历父元素，确保没有其他控件拦截滚轮事件
            var parent = VisualTreeHelper.GetParent(this);
            while (parent != null)
            {
                if (parent is UIElement element)
                {
                    element.PreviewMouseWheel += (sender, args) =>
                    {
                        // 如果鼠标在ROIEditorControl上，不传播滚轮事件
                        if (this.IsMouseOver)
                        {
                            OnPreviewMouseWheel(sender, args);
                            args.Handled = true;
                        }
                    };
                }
                parent = VisualTreeHelper.GetParent(parent);
            }
        };
    }

    /// <summary>
    /// 当前缩放比例
    /// </summary>
    public float ZoomScale => _zoomScale;

    /// <summary>
    /// 当前平移偏移
    /// </summary>
    public SKPoint PanOffset => _panOffset;

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
        // 当CurrentImage改变时，重新计算居中偏移和缩放比例
        if (e.PropertyName == nameof(ROIEditorViewModel.CurrentImage))
        {
            if (ViewModel?.CurrentImage != null && ActualWidth > 0 && ActualHeight > 0)
            {
                // 只在首次加载图像时设置初始缩放，避免用户缩放后被重置
                if (!_isInitialZoomSet)
                {
                    // 计算图像适应控件大小的缩放比例（UniformToFill模式 - 填充整个控件）
                    float imageWidth = ViewModel.CurrentImage.Width;
                    float imageHeight = ViewModel.CurrentImage.Height;
                    float controlWidth = (float)ActualWidth;
                    float controlHeight = (float)ActualHeight;
                    
                    float scaleX = controlWidth / imageWidth;
                    float scaleY = controlHeight / imageHeight;
                    
                    // 使用较大的缩放比例，使图像填充整个控件（可能会裁剪部分图像）
                    _zoomScale = Math.Max(scaleX, scaleY);
                    
                    // 计算居中偏移（相对于控件中心）
                    _panOffset = new SKPoint(-imageWidth / 2, -imageHeight / 2);
                    
                    _isInitialZoomSet = true;
                }
            }
            else if (ViewModel?.CurrentImage == null)
            {
                // 图像被清空时，重置初始缩放标志
                _isInitialZoomSet = false;
            }
            InvalidateVisual();
        }
        // 当其他属性改变时重绘
        else if (e.PropertyName == nameof(ROIEditorViewModel.ROIs) ||
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

        if (ViewModel.CurrentImage == null) return;

        // 保存当前状态
        canvas.Save();

        var imageWidth = ViewModel.CurrentImage.Width;
        var imageHeight = ViewModel.CurrentImage.Height;

        // 应用变换：先平移到控件中心，然后缩放，再旋转/翻转，最后平移使图像居中
        canvas.Translate(info.Width / 2.0f, info.Height / 2.0f);  // 移到控件中心
        canvas.Scale(_zoomScale);  // 应用缩放
        
        // 应用图像旋转（围绕图像中心）
        if (ViewModel.ImageRotationAngle != 0)
        {
            canvas.RotateDegrees(ViewModel.ImageRotationAngle, 0, 0);
        }
        
        // 应用水平翻转（左右对调）
        if (ViewModel.IsImageFlippedHorizontally)
        {
            canvas.Scale(-1, 1, 0, 0);  // X轴翻转
        }
        
        canvas.Translate(_panOffset.X, _panOffset.Y);  // 平移使图像居中

        // 绘制图像（使用原始尺寸，缩放由canvas.Scale处理）
        var drawRect = new SKRect(0, 0, imageWidth, imageHeight);
        canvas.DrawBitmap(ViewModel.CurrentImage, drawRect);

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

        // 恢复状态
        canvas.Restore();

        // 绘制缩放比例信息
        DrawZoomInfo(canvas, info.Width, info.Height);
    }

    /// <summary>
    /// 绘制缩放比例信息
    /// </summary>
    private void DrawZoomInfo(SKCanvas canvas, int width, int height)
    {
        using var paint = new SKPaint
        {
            Color = new SKColor(0, 0, 0, 180),
            Style = SKPaintStyle.Fill
        };

        using var textPaint = new SKPaint
        {
            Color = SKColors.White,
            TextSize = 12,
            IsAntialias = true
        };

        var text = $"{_zoomScale:P0}";
        var textBounds = new SKRect();
        textPaint.MeasureText(text, ref textBounds);

        var padding = 6;
        var bgRect = new SKRect(
            width - textBounds.Width - padding * 2 - 10,
            height - 30,
            width - 10,
            height - 10
        );

        canvas.DrawRect(bgRect, paint);
        canvas.DrawText(text, bgRect.Left + padding, bgRect.Bottom - padding, textPaint);
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
                StrokeWidth = 4 / _zoomScale, // 根据缩放调整线宽
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
                StrokeWidth = 3 / _zoomScale,
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
                StrokeWidth = 2 / _zoomScale,
                IsAntialias = true
            };
            canvas.DrawPath(path, paint);
        }

        // 绘制ROI名称（带背景）- 使用支持中文的字体
        using var bgPaint = new SKPaint
        {
            Color = isSelected ? new SKColor(0, 150, 255, 220) : new SKColor(0, 0, 0, 180),
            Style = SKPaintStyle.Fill
        };
        
        // 创建支持中文的字体
        float fontSize = (isSelected ? 14 : 12) / _zoomScale;
        using var font = new SKFont(_chineseTypeface, fontSize);
        font.Embolden = isSelected;
        
        using var textPaint = new SKPaint
        {
            Color = SKColors.White,
            IsAntialias = true
        };

        var text = roi.ROIName;
        var textBounds = new SKRect();
        font.MeasureText(text, out textBounds);
        var bgRect = new SKRect(bbox.Left, bbox.Top - 22 / _zoomScale, bbox.Left + textBounds.Width + 10 / _zoomScale, bbox.Top - 2 / _zoomScale);
        canvas.DrawRect(bgRect, bgPaint);
        canvas.DrawText(text, bbox.Left + 5 / _zoomScale, bbox.Top - 6 / _zoomScale, SKTextAlign.Left, font, textPaint);
    }

    private void DrawSelectionHandles(SKCanvas canvas, SKRect bbox)
    {
        using var handlePaint = new SKPaint
        {
            Color = new SKColor(0, 150, 255),
            Style = SKPaintStyle.Fill
        };

        float handleSize = 8 / _zoomScale;
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
            StrokeWidth = 2 / _zoomScale,
            IsAntialias = true,
            PathEffect = SKPathEffect.CreateDash(new[] { 5f / _zoomScale, 5f / _zoomScale }, 0)
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

    /// <summary>
    /// 将屏幕坐标转换为图像坐标（考虑缩放、平移和控件中心）
    /// </summary>
    private SKPoint ScreenToCanvas(Point screenPoint)
    {
        if (ViewModel?.CurrentImage == null) return new SKPoint(0, 0);

        var imageWidth = ViewModel.CurrentImage.Width;
        var imageHeight = ViewModel.CurrentImage.Height;
        var controlWidth = ActualWidth;
        var controlHeight = ActualHeight;

        // 计算控件中心到图像左上角的偏移（与绘制时一致）
        // 绘制时: canvas.Translate(info.Width / 2.0f, info.Height / 2.0f);
        //         canvas.Scale(_zoomScale);
        //         canvas.Translate(_panOffset.X, _panOffset.Y);
        // 逆变换: 先减去控件中心，除以缩放，再减去平移偏移

        float centerX = (float)(controlWidth / 2.0f);
        float centerY = (float)(controlHeight / 2.0f);

        // 逆变换计算
        float imageX = (float)((screenPoint.X - centerX) / _zoomScale - _panOffset.X);
        float imageY = (float)((screenPoint.Y - centerY) / _zoomScale - _panOffset.Y);

        return new SKPoint(imageX, imageY);
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel == null) return;

        var point = e.GetPosition(this);
        var skPoint = ScreenToCanvas(point);

        // 检测双击（快速两次点击）
        var now = DateTime.Now;
        var timeSinceLastClick = (now - _lastClickTime).TotalMilliseconds;
        _lastClickTime = now;

        if (timeSinceLastClick < DoubleClickInterval)
        {
            // 双击 - 重置视图
            ResetView();
            e.Handled = true;
            return;
        }

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
        else
        {
            // 非ROI创建模式下，左键拖动进行平移
            _isPanning = true;
            _panStart = new SKPoint((float)point.X, (float)point.Y);
            _panStartOffset = _panOffset;
            CaptureMouse();
        }
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (ViewModel == null) return;

        var point = e.GetPosition(this);
        var skPoint = ScreenToCanvas(point);

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
        else if (_isPanning)
        {
            Cursor = Cursors.SizeAll;
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
        else if (_isPanning)
        {
            // 平移图像
            var deltaX = (float)point.X - _panStart.X;
            var deltaY = (float)point.Y - _panStart.Y;
            _panOffset.X = _panStartOffset.X + deltaX;
            _panOffset.Y = _panStartOffset.Y + deltaY;
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

        if (_isPanning)
        {
            _isPanning = false;
            ReleaseMouseCapture();
        }

        if (ViewModel.IsEditing)
        {
            ViewModel.EndDrawingCommand.Execute(null);
            InvalidateVisual();
        }
    }

    /// <summary>
    /// 鼠标滚轮缩放（使用Preview事件确保能捕获）
    /// </summary>
    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (ViewModel?.CurrentImage == null) return;

        var mousePos = e.GetPosition(this);
        var controlCenterX = ActualWidth / 2;
        var controlCenterY = ActualHeight / 2;

        // 计算新的缩放比例
        float newScale;
        if (e.Delta > 0)
        {
            newScale = Math.Min(_zoomScale * 1.1f, MaxZoom);
        }
        else
        {
            newScale = Math.Max(_zoomScale * 0.9f, MinZoom);
        }

        // 以鼠标位置为中心缩放的算法（基于控件中心坐标系）
        // 1. 将鼠标位置转换为相对于控件中心的坐标
        float mouseOffsetX = (float)(mousePos.X - controlCenterX);
        float mouseOffsetY = (float)(mousePos.Y - controlCenterY);
        
        // 2. 计算缩放比例
        float scaleRatio = newScale / _zoomScale;
        
        // 3. 调整平移偏移，使鼠标位置保持不变
        // 新的平移偏移 = 旧平移偏移 - (鼠标偏移 * (1 - 缩放比例))
        _panOffset.X = _panOffset.X - mouseOffsetX * (1 - scaleRatio) / newScale;
        _panOffset.Y = _panOffset.Y - mouseOffsetY * (1 - scaleRatio) / newScale;
        
        _zoomScale = newScale;

        InvalidateVisual();
        e.Handled = true;
    }

    /// <summary>
    /// 重置视图
    /// </summary>
    public void ResetView()
    {
        // 重置初始缩放标志，允许重新计算初始缩放
        _isInitialZoomSet = false;
        
        if (ViewModel?.CurrentImage != null && ActualWidth > 0 && ActualHeight > 0)
        {
            float imageWidth = ViewModel.CurrentImage.Width;
            float imageHeight = ViewModel.CurrentImage.Height;
            float controlWidth = (float)ActualWidth;
            float controlHeight = (float)ActualHeight;
            
            float scaleX = controlWidth / imageWidth;
            float scaleY = controlHeight / imageHeight;
            
            // 使用较大的缩放比例，使图像填充整个控件
            _zoomScale = Math.Max(scaleX, scaleY);
            
            // 计算居中偏移（相对于控件中心）
            _panOffset = new SKPoint(-imageWidth / 2, -imageHeight / 2);
            
            _isInitialZoomSet = true;
        }
        else
        {
            _zoomScale = 1.0f;
            _panOffset = new SKPoint(0, 0);
        }
        InvalidateVisual();
    }

    /// <summary>
    /// 设置缩放比例
    /// </summary>
    public void SetZoom(float scale)
    {
        _zoomScale = Math.Clamp(scale, MinZoom, MaxZoom);
        InvalidateVisual();
    }

    /// <summary>
    /// 适应窗口大小（完整显示图像）
    /// </summary>
    public void FitToWindow()
    {
        if (ViewModel?.CurrentImage == null) return;

        var actualWidth = ActualWidth;
        var actualHeight = ActualHeight;

        if (actualWidth <= 0 || actualHeight <= 0) return;

        // 重置初始缩放标志
        _isInitialZoomSet = false;

        // 计算适应窗口的缩放比例（完整显示）
        var scaleX = actualWidth / ViewModel.CurrentImage.Width;
        var scaleY = actualHeight / ViewModel.CurrentImage.Height;
        _zoomScale = (float)Math.Min(scaleX, scaleY);

        // 居中显示（相对于控件中心）
        _panOffset = new SKPoint(-ViewModel.CurrentImage.Width / 2, -ViewModel.CurrentImage.Height / 2);

        _isInitialZoomSet = true;
        InvalidateVisual();
    }
}
