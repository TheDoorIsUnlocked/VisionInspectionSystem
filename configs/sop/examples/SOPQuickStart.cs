using VisionInspection.Modules.SOP;
using VisionInspection.Modules.SOP.Models;
using VisionInspection.Core.Services;
using VisionInspection.Core.Models;
using Microsoft.Extensions.Configuration;

namespace VisionInspection.Examples;

/// <summary>
/// SOP检测快速入门示例
/// </summary>
public class SOPQuickStart
{
    public static async Task Main(string[] args)
    {
        Console.WriteLine("========================================");
        Console.WriteLine("    SOP检测系统 - 快速入门示例");
        Console.WriteLine("========================================\n");

        // 创建SOP检测启动器
        var starter = new SOPDetectionStarter();

        try
        {
            // ========== 步骤1: 设置日志回调 ==========
            Console.WriteLine("【步骤1】配置检测参数...");
            starter.OnLog = message => Console.WriteLine($"   [LOG] {message}");
            starter.DetectionMode = SOPDetectionMode.PoseBased; // 使用姿态检测模式
            Console.WriteLine("   ✓ 检测模式: 姿态检测\n");

            // ========== 步骤2: 创建配置 ==========
            Console.WriteLine("【步骤2】创建配置...");
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["SOPModule:ModelPath"] = "models/yolov8n.onnx",
                    ["SOPModule:UseGpu"] = "false",
                    ["SOPModule:ConfidenceThreshold"] = "0.6",
                    ["SOPModule:IouThreshold"] = "0.45",
                    ["SOPModule:PoseEstimation:Enabled"] = "true",
                    ["SOPModule:PoseEstimation:ModelPath"] = "models/yolo-pose.onnx"
                })
                .Build();
            Console.WriteLine("   ✓ 配置创建完成\n");

            // ========== 步骤3: 创建模拟摄像头服务 ==========
            Console.WriteLine("【步骤3】初始化摄像头...");
            var cameraService = new MockCameraService();
            Console.WriteLine("   ✓ 摄像头初始化完成\n");

            // ========== 步骤4: 初始化SOP检测 ==========
            Console.WriteLine("【步骤4】初始化SOP检测...");

            // 配置文件路径
            string regionConfigPath = "configs/sop/regions/phone_usage_regions.json";
            string sopYamlPath = "configs/sop/sop_phone_usage.yaml";

            // 检查配置文件是否存在
            if (!File.Exists(regionConfigPath))
            {
                Console.WriteLine($"   ✗ 区域配置文件不存在: {regionConfigPath}");
                Console.WriteLine("   请确保配置文件路径正确\n");
                return;
            }
            if (!File.Exists(sopYamlPath))
            {
                Console.WriteLine($"   ✗ SOP流程配置文件不存在: {sopYamlPath}");
                Console.WriteLine("   请确保配置文件路径正确\n");
                return;
            }

            // 订阅事件
            starter.StepChanged += (s, e) =>
            {
                Console.WriteLine($"\n   [事件] 步骤变化: {e.PreviousStepId} -> {e.CurrentStepId}");
            };

            starter.StateChanged += (s, e) =>
            {
                Console.WriteLine($"   [事件] 状态变化: {e.OldState} -> {e.NewState}");
            };

            starter.ViolationDetected += (s, e) =>
            {
                Console.WriteLine($"   [警告] 违规检测: {e.Violation.Type}");
                Console.WriteLine($"          描述: {e.Violation.Description}");
            };

            starter.PoseDetected += (s, e) =>
            {
                if (e.Poses.Count > 0)
                {
                    Console.WriteLine($"   [检测] 发现 {e.Poses.Count} 个人体");
                    foreach (var pose in e.Poses)
                    {
                        var leftHand = pose.LeftWrist;
                        var rightHand = pose.RightWrist;
                        Console.WriteLine($"          人体#{pose.TrackId}: 左手({leftHand?.X:F0},{leftHand?.Y:F0}) 右手({rightHand?.X:F0},{rightHand?.Y:F0})");
                    }
                }
            };

            // 初始化
            await starter.InitializeAsync(configuration, cameraService, regionConfigPath);
            Console.WriteLine("   ✓ SOP检测初始化完成\n");

            // ========== 步骤5: 加载工作流 ==========
            Console.WriteLine("【步骤5】加载工作流...");
            starter.StartWorkflow(sopYamlPath);
            Console.WriteLine($"   ✓ 工作流: {starter.CurrentWorkflowName}");
            Console.WriteLine($"   ✓ 检测模式: {starter.DetectionMode}\n");

            // ========== 步骤6: 处理帧 ==========
            Console.WriteLine("【步骤6】开始处理帧...");
            Console.WriteLine("   按 Enter 键停止检测\n");

            // 创建检测循环
            var cts = new CancellationTokenSource();
            var detectionTask = Task.Run(async () =>
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    var frames = await cameraService.CaptureAsync();
                    var result = await starter.ProcessAsync(frames);

                    if (result != null)
                    {
                        Console.WriteLine($"   [结果] 步骤: {result.StepResults.CurrentStep}/{result.StepResults.TotalSteps} - {result.StepResults.Message}");
                    }

                    await Task.Delay(1000, cts.Token); // 每秒处理一帧
                }
            }, cts.Token);

            // 等待用户输入
            Console.ReadLine();
            cts.Cancel();

            try
            {
                await detectionTask;
            }
            catch (OperationCanceledException)
            {
                // 正常取消
            }

            Console.WriteLine("\n【步骤7】检测已停止");
            Console.WriteLine("   ✓ 检测循环已停止\n");

            Console.WriteLine("========================================");
            Console.WriteLine("    SOP检测示例运行完成");
            Console.WriteLine("========================================");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\n✗ 错误: {ex.Message}");
            Console.WriteLine($"  {ex.StackTrace}");
        }
        finally
        {
            starter.Dispose();
        }
    }
}

/// <summary>
/// 模拟摄像头服务（用于测试）
/// </summary>
public class MockCameraService : ICameraService
{
    private readonly Random _random = new();
    private int _frameNumber = 0;

    public string CameraId => "mock_camera";
    public string CameraName => "模拟摄像头";
    public bool IsConnected => true;
    public bool IsGrabbing => true;

    public Task<bool> ConnectAsync(CameraConfig config)
    {
        return Task.FromResult(true);
    }

    public Task DisconnectAsync()
    {
        return Task.CompletedTask;
    }

    public Task<Dictionary<string, CaptureFrame>> CaptureAsync()
    {
        _frameNumber++;

        // 创建模拟帧
        var frames = new Dictionary<string, CaptureFrame>
        {
            ["main_camera"] = new CaptureFrame
            {
                Image = CreateMockImage(),
                Timestamp = DateTime.Now,
                FrameNumber = _frameNumber
            }
        };

        return Task.FromResult(frames);
    }

    public Task StartCaptureAsync()
    {
        return Task.CompletedTask;
    }

    public Task StopCaptureAsync()
    {
        return Task.CompletedTask;
    }

    public void Dispose()
    {
    }

    /// <summary>
    /// 创建模拟图像
    /// </summary>
    private SkiaSharp.SKBitmap CreateMockImage()
    {
        // 创建1280x720的空白图像
        var bitmap = new SkiaSharp.SKBitmap(1280, 720);

        // 填充背景色
        using (var canvas = new SkiaSharp.SKCanvas(bitmap))
        {
            canvas.Clear(new SkiaSharp.SKColor(40, 40, 40));

            // 绘制区域标记
            DrawRegionMarkers(canvas);
        }

        return bitmap;
    }

    /// <summary>
    /// 绘制区域标记
    /// </summary>
    private void DrawRegionMarkers(SkiaSharp.SKCanvas canvas)
    {
        using var paint = new SkiaSharp.SKPaint
        {
            Style = SkiaSharp.SKPaintStyle.Stroke,
            StrokeWidth = 3,
            IsAntialias = true
        };

        // 手机放置区
        paint.Color = new SkiaSharp.SKColor(76, 175, 80); // 绿色
        canvas.DrawRect(200, 400, 150, 150, paint);
        DrawLabel(canvas, "手机放置区", 210, 390, paint.Color);

        // 耳边区域
        paint.Color = new SkiaSharp.SKColor(33, 150, 243); // 蓝色
        canvas.DrawRect(450, 150, 100, 100, paint);
        DrawLabel(canvas, "耳边区域", 460, 140, paint.Color);

        // 面部区域
        paint.Color = new SkiaSharp.SKColor(255, 152, 0); // 橙色
        canvas.DrawRect(400, 100, 200, 200, paint);
        DrawLabel(canvas, "面部区域", 410, 90, paint.Color);
    }

    /// <summary>
    /// 绘制标签
    /// </summary>
    private void DrawLabel(SkiaSharp.SKCanvas canvas, string text, float x, float y, SkiaSharp.SKColor color)
    {
        using var paint = new SkiaSharp.SKPaint
        {
            Color = color,
            TextSize = 16,
            IsAntialias = true
        };
        canvas.DrawText(text, x, y, paint);
    }
}
