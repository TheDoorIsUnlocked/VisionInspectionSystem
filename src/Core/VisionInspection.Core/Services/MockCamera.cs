using SkiaSharp;
using VisionInspection.Core.Interfaces;

namespace VisionInspection.Core.Services;

public class MockCamera : ICamera
{
    public string Id { get; }
    public string Name { get; }
    public bool IsConnected { get; private set; }
    public CameraInfo Info { get; }

    public MockCamera(string id, string name)
    {
        Id = id;
        Name = name;
        IsConnected = false;
        Info = new CameraInfo
        {
            Model = "Mock Camera",
            IpAddress = "127.0.0.1",
            Width = 1920,
            Height = 1080,
            FrameRate = 30.0
        };
    }

    public Task<bool> ConnectAsync()
    {
        IsConnected = true;
        return Task.FromResult(true);
    }

    public Task DisconnectAsync()
    {
        IsConnected = false;
        return Task.CompletedTask;
    }

    public Task<SKBitmap?> CaptureAsync()
    {
        if (!IsConnected)
            return Task.FromResult<SKBitmap?>(null);

        // 创建一个测试图像
        var bitmap = new SKBitmap(640, 480);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);

        // 绘制一些测试内容
        using var paint = new SKPaint
        {
            Color = SKColors.Blue,
            Style = SKPaintStyle.Fill
        };

        // 绘制一个矩形
        canvas.DrawRect(100, 100, 400, 200, paint);

        // 绘制文字
        using var textPaint = new SKPaint
        {
            Color = SKColors.Black,
            TextSize = 24
        };
        canvas.DrawText("Mock Camera Test", 200, 350, textPaint);

        return Task.FromResult<SKBitmap?>(bitmap);
    }
}
