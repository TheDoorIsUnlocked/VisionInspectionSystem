using SkiaSharp;
using VisionInspection.Modules.SOP.Models;

namespace VisionInspection.Modules.SOP.Services;

/// <summary>
/// 手部姿态可视化工具 - 绘制绿色骨架线
/// </summary>
public static class HandPoseVisualizer
{
    // 颜色配置
    private static readonly SKColor SkeletonColor = new SKColor(0, 255, 0);      // 绿色骨架线
    private static readonly SKColor JointColor = new SKColor(0, 200, 0);        // 深绿色关节点
    private static readonly SKColor WristColor = new SKColor(255, 255, 0);      // 黄色手腕
    private static readonly SKColor TipColor = new SKColor(255, 100, 100);      // 红色指尖

    // 样式配置
    private const float SkeletonStrokeWidth = 3f;
    private const float JointRadius = 4f;
    private const float WristRadius = 6f;
    private const float TipRadius = 5f;

    /// <summary>
    /// 在图像上绘制手部姿态骨架
    /// </summary>
    public static void DrawHandPose(SKCanvas canvas, HandPose hand, float scaleX = 1f, float scaleY = 1f)
    {
        if (hand?.Keypoints == null || hand.Keypoints.Count == 0) return;

        // 绘制骨架连接线
        DrawSkeletonLines(canvas, hand, scaleX, scaleY);

        // 绘制关节点
        DrawJoints(canvas, hand, scaleX, scaleY);
    }

    /// <summary>
    /// 绘制多个手部姿态
    /// </summary>
    public static void DrawHandPoses(SKCanvas canvas, IEnumerable<HandPose> hands, float scaleX = 1f, float scaleY = 1f)
    {
        foreach (var hand in hands)
        {
            DrawHandPose(canvas, hand, scaleX, scaleY);
        }
    }

    /// <summary>
    /// 在SKBitmap上绘制手部姿态并返回新图像
    /// </summary>
    public static SKBitmap DrawHandPosesOnImage(SKBitmap image, HandPoseEstimationResult result)
    {
        // 创建可变的图像副本
        var outputImage = image.Copy();
        
        using (var canvas = new SKCanvas(outputImage))
        {
            DrawHandPoses(canvas, result.Hands);
        }

        return outputImage;
    }

    /// <summary>
    /// 绘制骨架连接线
    /// </summary>
    private static void DrawSkeletonLines(SKCanvas canvas, HandPose hand, float scaleX, float scaleY)
    {
        using var paint = new SKPaint
        {
            Color = SkeletonColor,
            StrokeWidth = SkeletonStrokeWidth,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke
        };

        foreach (var (start, end) in HandSkeletonConnections.Connections)
        {
            var startPoint = hand.GetKeypoint(start);
            var endPoint = hand.GetKeypoint(end);

            if (startPoint?.IsValid != true || endPoint?.IsValid != true)
                continue;

            canvas.DrawLine(
                startPoint.X * scaleX, startPoint.Y * scaleY,
                endPoint.X * scaleX, endPoint.Y * scaleY,
                paint);
        }
    }

    /// <summary>
    /// 绘制关节点
    /// </summary>
    private static void DrawJoints(SKCanvas canvas, HandPose hand, float scaleX, float scaleY)
    {
        foreach (var keypoint in hand.Keypoints)
        {
            if (!keypoint.IsValid) continue;

            var (color, radius) = GetJointStyle(keypoint.Type);
            
            using var paint = new SKPaint
            {
                Color = color,
                IsAntialias = true,
                Style = SKPaintStyle.Fill
            };

            canvas.DrawCircle(
                keypoint.X * scaleX, 
                keypoint.Y * scaleY, 
                radius, 
                paint);

            // 绘制外圈
            using var strokePaint = new SKPaint
            {
                Color = SKColors.White,
                StrokeWidth = 1.5f,
                IsAntialias = true,
                Style = SKPaintStyle.Stroke
            };
            
            canvas.DrawCircle(
                keypoint.X * scaleX, 
                keypoint.Y * scaleY, 
                radius, 
                strokePaint);
        }
    }

    /// <summary>
    /// 根据关键点类型获取样式
    /// </summary>
    private static (SKColor Color, float Radius) GetJointStyle(HandKeypointType type)
    {
        return type switch
        {
            HandKeypointType.Wrist => (WristColor, WristRadius),
            HandKeypointType.ThumbTip or 
            HandKeypointType.IndexFingerTip or 
            HandKeypointType.MiddleFingerTip or 
            HandKeypointType.RingFingerTip or 
            HandKeypointType.PinkyTip => (TipColor, TipRadius),
            _ => (JointColor, JointRadius)
        };
    }

    /// <summary>
    /// 绘制手部标签（左手/右手）
    /// </summary>
    public static void DrawHandLabel(SKCanvas canvas, HandPose hand, float scaleX = 1f, float scaleY = 1f)
    {
        var wrist = hand.Wrist;
        if (wrist == null || !wrist.IsValid) return;

        var label = hand.HandType switch
        {
            HandType.Left => "左手",
            HandType.Right => "右手",
            _ => "未知"
        };

        using var textPaint = new SKPaint
        {
            Color = SKColors.White,
            TextSize = 14,
            IsAntialias = true,
            Typeface = SKTypeface.Default
        };

        // 绘制背景
        var textBounds = new SKRect();
        textPaint.MeasureText(label, ref textBounds);
        
        var bgRect = new SKRect(
            wrist.X * scaleX - 2,
            wrist.Y * scaleY - textBounds.Height - 4,
            wrist.X * scaleX + textBounds.Width + 4,
            wrist.Y * scaleY + 2);

        using var bgPaint = new SKPaint
        {
            Color = new SKColor(0, 0, 0, 180),
            Style = SKPaintStyle.Fill
        };
        canvas.DrawRect(bgRect, bgPaint);

        // 绘制文字
        canvas.DrawText(label, wrist.X * scaleX, wrist.Y * scaleY - 4, textPaint);
    }

    /// <summary>
    /// 创建手部姿态的可视化覆盖层
    /// </summary>
    public static HandPoseVisualOverlay CreateHandPoseOverlay(HandPoseEstimationResult result, int imageWidth, int imageHeight)
    {
        var overlay = new HandPoseVisualOverlay
        {
            Width = imageWidth,
            Height = imageHeight
        };

        using var bitmap = new SKBitmap(imageWidth, imageHeight);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            DrawHandPoses(canvas, result.Hands);
            
            foreach (var hand in result.Hands)
            {
                DrawHandLabel(canvas, hand);
            }
        }

        overlay.OverlayImage = bitmap;
        return overlay;
    }
}

/// <summary>
/// 手部姿态可视化覆盖层
/// </summary>
public class HandPoseVisualOverlay
{
    public int Width { get; set; }
    public int Height { get; set; }
    public SKBitmap? OverlayImage { get; set; }
}
