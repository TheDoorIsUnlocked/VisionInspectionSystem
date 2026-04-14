using SkiaSharp;

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
/// 矩形 ROI
/// </summary>
public class RectangleROI : ROI
{
    public SKRectI Rect { get; set; }
    public float Rotation { get; set; }

    public RectangleROI()
    {
        ShapeType = ROIShapeType.Rectangle;
    }

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
