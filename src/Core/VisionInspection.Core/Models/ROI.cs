using SkiaSharp;
using System.ComponentModel;

namespace VisionInspection.Core.Models;

/// <summary>
/// ROI 形状类型
/// </summary>
public enum ROIShapeType
{
    Rectangle,
    Circle,
    Ellipse,
    Polygon,
    Ring
}

/// <summary>
/// ROI 基类
/// </summary>
public abstract class ROI
{
    public string ROIId { get; set; } = Guid.NewGuid().ToString();
    public string ROIName { get; set; } = "";
    public ROIShapeType ShapeType { get; protected set; }
    public SKColor Color { get; set; } = SKColors.Red;
    public bool IsVisible { get; set; } = true;
    public bool IsEditable { get; set; } = true;

    public abstract SKPath GetPath();
    public abstract bool ContainsPoint(SKPoint point);
    public abstract SKRectI GetBoundingBox();
    public abstract SKBitmap ExtractROI(SKBitmap sourceImage);
    public abstract void Translate(float dx, float dy);
    public abstract void Scale(float scaleX, float scaleY, SKPoint center);
}

/// <summary>
/// 矩形 ROI（支持属性变更通知，供标定界面 X/Y/W/H 输入框双向绑定）
/// </summary>
public class RectangleROI : ROI, INotifyPropertyChanged
{
    private SKRectI _rect;

    public SKRectI Rect
    {
        get => _rect;
        set
        {
            if (_rect == value) return;
            _rect = value;
            OnPropertyChanged(nameof(Rect));
            OnPropertyChanged(nameof(Left));
            OnPropertyChanged(nameof(Top));
            OnPropertyChanged(nameof(Right));
            OnPropertyChanged(nameof(Bottom));
            OnPropertyChanged(nameof(Width));
            OnPropertyChanged(nameof(Height));
        }
    }

    // 可绑定坐标/尺寸属性（同步 Rect）
    public int Left
    {
        get => _rect.Left;
        set
        {
            if (_rect.Left == value) return;
            Rect = new SKRectI(value, _rect.Top, value + _rect.Width, _rect.Bottom);
        }
    }

    public int Top
    {
        get => _rect.Top;
        set
        {
            if (_rect.Top == value) return;
            Rect = new SKRectI(_rect.Left, value, _rect.Right, value + _rect.Height);
        }
    }

    public int Right
    {
        get => _rect.Right;
        set
        {
            if (_rect.Right == value) return;
            Rect = new SKRectI(_rect.Left, _rect.Top, value, _rect.Bottom);
        }
    }

    public int Bottom
    {
        get => _rect.Bottom;
        set
        {
            if (_rect.Bottom == value) return;
            Rect = new SKRectI(_rect.Left, _rect.Top, _rect.Right, value);
        }
    }

    public int Width
    {
        get => _rect.Width;
        set
        {
            if (_rect.Width == value) return;
            Rect = new SKRectI(_rect.Left, _rect.Top, _rect.Left + value, _rect.Bottom);
        }
    }

    public int Height
    {
        get => _rect.Height;
        set
        {
            if (_rect.Height == value) return;
            Rect = new SKRectI(_rect.Left, _rect.Top, _rect.Right, _rect.Top + value);
        }
    }

    public float Rotation { get; set; }

    public RectangleROI()
    {
        ShapeType = ROIShapeType.Rectangle;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged(string propertyName)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public override SKPath GetPath()
    {
        var path = new SKPath();
        var rect = new SKRect(Rect.Left, Rect.Top, Rect.Right, Rect.Bottom);

        if (Rotation != 0)
        {
            var matrix = SKMatrix.CreateRotationDegrees(Rotation, Rect.MidX, Rect.MidY);
            path.AddRect(rect);
            path.Transform(matrix);
        }
        else
        {
            path.AddRect(rect);
        }

        return path;
    }

    public override bool ContainsPoint(SKPoint point)
    {
        return Rect.Contains((int)point.X, (int)point.Y);
    }

    public override SKRectI GetBoundingBox()
    {
        return Rect;
    }

    public override SKBitmap ExtractROI(SKBitmap sourceImage)
    {
        var bbox = GetBoundingBox();
        var roiBitmap = new SKBitmap(bbox.Width, bbox.Height);

        using var canvas = new SKCanvas(roiBitmap);
        canvas.DrawBitmap(sourceImage, bbox, new SKRect(0, 0, bbox.Width, bbox.Height));

        return roiBitmap;
    }

    public override void Translate(float dx, float dy)
    {
        Rect = new SKRectI(
            Rect.Left + (int)dx,
            Rect.Top + (int)dy,
            Rect.Right + (int)dx,
            Rect.Bottom + (int)dy
        );
    }

    public override void Scale(float scaleX, float scaleY, SKPoint center)
    {
        var newWidth = (int)(Rect.Width * scaleX);
        var newHeight = (int)(Rect.Height * scaleY);
        var newLeft = (int)(center.X - (center.X - Rect.Left) * scaleX);
        var newTop = (int)(center.Y - (center.Y - Rect.Top) * scaleY);

        Rect = new SKRectI(newLeft, newTop, newLeft + newWidth, newTop + newHeight);
    }
}

/// <summary>
/// 圆形 ROI
/// </summary>
public class CircleROI : ROI
{
    public SKPoint Center { get; set; }
    public float Radius { get; set; }

    public CircleROI()
    {
        ShapeType = ROIShapeType.Circle;
    }

    public override SKPath GetPath()
    {
        var path = new SKPath();
        path.AddCircle(Center.X, Center.Y, Radius);
        return path;
    }

    public override bool ContainsPoint(SKPoint point)
    {
        var dx = point.X - Center.X;
        var dy = point.Y - Center.Y;
        return Math.Sqrt(dx * dx + dy * dy) <= Radius;
    }

    public override SKRectI GetBoundingBox()
    {
        return new SKRectI(
            (int)(Center.X - Radius),
            (int)(Center.Y - Radius),
            (int)(Center.X + Radius),
            (int)(Center.Y + Radius)
        );
    }

    public override SKBitmap ExtractROI(SKBitmap sourceImage)
    {
        var bbox = GetBoundingBox();
        var roiBitmap = new SKBitmap(bbox.Width, bbox.Height);

        using var canvas = new SKCanvas(roiBitmap);
        using var paint = new SKPaint { IsAntialias = true };

        var clipPath = new SKPath();
        clipPath.AddCircle(Radius, Radius, Radius);
        canvas.ClipPath(clipPath);

        canvas.DrawBitmap(sourceImage, bbox, new SKRect(0, 0, bbox.Width, bbox.Height));

        return roiBitmap;
    }

    public override void Translate(float dx, float dy)
    {
        Center = new SKPoint(Center.X + dx, Center.Y + dy);
    }

    public override void Scale(float scaleX, float scaleY, SKPoint center)
    {
        var distX = Center.X - center.X;
        var distY = Center.Y - center.Y;
        Center = new SKPoint(center.X + distX * scaleX, center.Y + distY * scaleY);
        Radius *= Math.Max(scaleX, scaleY);
    }
}
