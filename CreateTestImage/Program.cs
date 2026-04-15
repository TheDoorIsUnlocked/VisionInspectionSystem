using SkiaSharp;

// 创建一个简单的测试图像
var info = new SKImageInfo(800, 600);
using var surface = SKSurface.Create(info);
using var canvas = surface.Canvas;

// 填充背景
canvas.Clear(new SKColor(0, 102, 204));

// 绘制一些形状作为测试内容
using var paint = new SKPaint();
paint.Color = SKColors.White;
paint.TextSize = 48;
paint.IsAntialias = true;

// 绘制文字
canvas.DrawText("Vision Inspection System", 150, 100, paint);

paint.TextSize = 24;
canvas.DrawText("Test Image for ROI Detection", 250, 150, paint);

// 绘制一些矩形（模拟检测目标）
paint.Color = new SKColor(255, 200, 0);
paint.Style = SKPaintStyle.Fill;
canvas.DrawRect(100, 250, 150, 100, paint);
canvas.DrawRect(400, 250, 150, 100, paint);
canvas.DrawRect(250, 400, 300, 120, paint);

// 绘制圆形
paint.Color = new SKColor(0, 255, 100);
canvas.DrawCircle(550, 300, 60, paint);

// 保存图像
using var image = surface.Snapshot();
using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90);

var outputPath = args.Length > 0 ? args[0] : "test_image.jpg";
using var stream = System.IO.File.OpenWrite(outputPath);
data.SaveTo(stream);

Console.WriteLine($"测试图像已创建: {outputPath}");
