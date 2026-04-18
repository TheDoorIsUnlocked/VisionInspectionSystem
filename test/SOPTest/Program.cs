using VisionInspection.Modules.SOP;
using VisionInspection.Modules.SOP.Models;
using VisionInspection.Core.Services;
using VisionInspection.Core.Models;
using Microsoft.Extensions.Configuration;
using SkiaSharp;

namespace SOPTest;

/// <summary>
/// SOP检测测试程序
/// </summary>
class Program
{
    static async Task Main(string[] args)
    {
        Console.WriteLine("========================================");
        Console.WriteLine("    SOP检测系统 - 测试程序");
        Console.WriteLine("========================================\n");

        // 创建SOP检测启动器
        var starter = new SOPDetectionStarter();

        try
        {
            // ========== 步骤1: 配置参数 ==========
            Console.WriteLine("【步骤1】配置检测参数...");
            starter.OnLog = message => Console.WriteLine($"   [LOG] {message}");
            starter.DetectionMode = SOPDetectionMode.PoseBased;
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

            // ========== 步骤3: 创建模拟摄像头 ==========
            Console.WriteLine("【步骤3】初始化模拟摄像头...");
            var cameraService = new MockCameraService();
            Console.WriteLine("   ✓ 摄像头初始化完成\n");

            // ========== 步骤4: 检查配置文件 ==========
            Console.WriteLine("【步骤4】检查配置文件...");

            // 获取配置文件路径（相对于输出目录）
            string basePath = AppContext.BaseDirectory;
            string regionConfigPath = Path.Combine(basePath, "configs", "sop", "regions", "phone_usage_regions.json");
            string sopYamlPath = Path.Combine(basePath, "configs", "sop", "sop_phone_usage.yaml");

            // 如果文件不存在，尝试从源代码目录复制
            if (!File.Exists(regionConfigPath))
            {
                string sourceRegionPath = Path.Combine(basePath, "..", "..", "..", "..", "configs", "sop", "regions", "phone_usage_regions.json");
                if (File.Exists(sourceRegionPath))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(regionConfigPath)!);
                    File.Copy(sourceRegionPath, regionConfigPath, true);
                    Console.WriteLine($"   已复制区域配置: {regionConfigPath}");
                }
            }

            if (!File.Exists(sopYamlPath))
            {
                string sourceYamlPath = Path.Combine(basePath, "..", "..", "..", "..", "configs", "sop", "sop_phone_usage.yaml");
                if (File.Exists(sourceYamlPath))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(sopYamlPath)!);
                    File.Copy(sourceYamlPath, sopYamlPath, true);
                    Console.WriteLine($"   已复制SOP配置: {sopYamlPath}");
                }
            }

            // 检查文件是否存在
            if (!File.Exists(regionConfigPath))
            {
                Console.WriteLine($"   ✗ 区域配置文件不存在: {regionConfigPath}");
                Console.WriteLine("   请确保配置文件存在\n");
                return;
            }
            if (!File.Exists(sopYamlPath))
            {
                Console.WriteLine($"   ✗ SOP流程配置文件不存在: {sopYamlPath}");
                Console.WriteLine("   请确保配置文件存在\n");
                return;
            }

            Console.WriteLine($"   ✓ 区域配置: {regionConfigPath}");
            Console.WriteLine($"   ✓ SOP配置: {sopYamlPath}\n");

            // ========== 步骤5: 订阅事件 ==========
            Console.WriteLine("【步骤5】订阅事件...");

            starter.StepChanged += (s, e) =>
            {
                Console.WriteLine($"\n   >>> 步骤变化: {e.PreviousStepId} -> {e.CurrentStepId}");
            };

            starter.StateChanged += (s, e) =>
            {
                Console.WriteLine($"   >>> 状态变化: {e.OldState} -> {e.NewState}");
            };

            starter.ViolationDetected += (s, e) =>
            {
                Console.WriteLine($"\n   !!! 违规检测: {e.Violation.Type}");
                Console.WriteLine($"       描述: {e.Violation.Description}");
            };

            starter.PoseDetected += (s, e) =>
            {
                if (e.Poses.Count > 0)
                {
                    Console.WriteLine($"\n   [姿态] 检测到 {e.Poses.Count} 个人体:");
                    foreach (var pose in e.Poses)
                    {
                        var leftHand = pose.LeftWrist;
                        var rightHand = pose.RightWrist;
                        var nose = pose.Nose;
                        Console.WriteLine($"       人体#{pose.TrackId}:");
                        Console.WriteLine($"         - 左手: ({leftHand?.X:F0}, {leftHand?.Y:F0})");
                        Console.WriteLine($"         - 右手: ({rightHand?.X:F0}, {rightHand?.Y:F0})");
                        Console.WriteLine($"         - 鼻子: ({nose?.X:F0}, {nose?.Y:F0})");

                        // 检查手是否在特定区域
                        CheckHandInRegions(starter, pose);
                    }
                }
            };

            Console.WriteLine("   ✓ 事件订阅完成\n");

            // ========== 步骤6: 初始化SOP ==========
            Console.WriteLine("【步骤6】初始化SOP检测...");
            await starter.InitializeAsync(configuration, cameraService, regionConfigPath);
            Console.WriteLine("   ✓ SOP检测初始化完成\n");

            // ========== 步骤7: 加载工作流 ==========
            Console.WriteLine("【步骤7】加载工作流...");
            starter.StartWorkflow(sopYamlPath);
            Console.WriteLine($"   ✓ 工作流: {starter.CurrentWorkflowName}");
            Console.WriteLine($"   ✓ 检测模式: {starter.DetectionMode}\n");

            // ========== 步骤8: 显示区域信息 ==========
            Console.WriteLine("【步骤8】区域配置信息:");
            var regions = starter.GetAllRegions();
            foreach (var (id, rect) in regions)
            {
                Console.WriteLine($"   - {id}: ({rect.Left:F0}, {rect.Top:F0}) - ({rect.Right:F0}, {rect.Bottom:F0})");
            }
            Console.WriteLine();

            // ========== 步骤9: 开始检测循环 ==========
            Console.WriteLine("【步骤9】开始检测循环...");
            Console.WriteLine("   按 Enter 键停止检测\n");

            var cts = new CancellationTokenSource();
            var detectionTask = Task.Run(async () =>
            {
                int frameCount = 0;
                while (!cts.Token.IsCancellationRequested)
                {
                    try
                    {
                        var frames = await cameraService.CaptureAsync();
                        var result = await starter.ProcessAsync(frames);

                        if (result != null)
                        {
                            frameCount++;
                            if (frameCount % 10 == 0) // 每10帧输出一次
                            {
                                Console.WriteLine($"   [帧{frameCount}] 步骤: {result.StepResults.CurrentStep}/{result.StepResults.TotalSteps} - {result.StepResults.Message}");
                            }
                        }

                        await Task.Delay(100, cts.Token); // 100ms = 10 FPS
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
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

            Console.WriteLine("\n【步骤10】检测已停止");
            Console.WriteLine("   ✓ 检测循环已停止\n");

            Console.WriteLine("========================================");
            Console.WriteLine("    SOP检测测试完成");
            Console.WriteLine("========================================");
            Console.WriteLine("\n按任意键退出...");
            Console.ReadKey();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\n✗ 错误: {ex.Message}");
            Console.WriteLine($"堆栈: {ex.StackTrace}");
            Console.WriteLine("\n按任意键退出...");
            Console.ReadKey();
        }
        finally
        {
            starter.Dispose();
        }
    }

    /// <summary>
    /// 检查手是否在特定区域
    /// </summary>
    static void CheckHandInRegions(SOPDetectionStarter starter, HumanPose pose)
    {
        var leftHand = pose.LeftWrist;
        var rightHand = pose.RightWrist;

        // 检查手机放置区
        if (leftHand?.IsValid == true)
        {
            var phoneTable = starter.GetRegion("phone_table");
            if (phoneTable.HasValue && IsPointInRect(leftHand.X, leftHand.Y, phoneTable.Value))
            {
                Console.WriteLine($"         -> 左手在手机放置区!");
            }
        }
        if (rightHand?.IsValid == true)
        {
            var phoneTable = starter.GetRegion("phone_table");
            if (phoneTable.HasValue && IsPointInRect(rightHand.X, rightHand.Y, phoneTable.Value))
            {
                Console.WriteLine($"         -> 右手在手机放置区!");
            }
        }

        // 检查耳边区域
        if (leftHand?.IsValid == true)
        {
            var earRegion = starter.GetRegion("ear_region");
            if (earRegion.HasValue && IsPointInRect(leftHand.X, leftHand.Y, earRegion.Value))
            {
                Console.WriteLine($"         -> 左手在耳边区域（打电话）!");
            }
        }
        if (rightHand?.IsValid == true)
        {
            var earRegion = starter.GetRegion("ear_region");
            if (earRegion.HasValue && IsPointInRect(rightHand.X, rightHand.Y, earRegion.Value))
            {
                Console.WriteLine($"         -> 右手在耳边区域（打电话）!");
            }
        }
    }

    /// <summary>
    /// 检查点是否在矩形内
    /// </summary>
    static bool IsPointInRect(float x, float y, SKRect rect)
    {
        return x >= rect.Left && x <= rect.Right && y >= rect.Top && y <= rect.Bottom;
    }
}

/// <summary>
/// 模拟摄像头服务
/// </summary>
public class MockCameraService : ICameraService
{
    private int _frameNumber = 0;
    private readonly Random _random = new();

    public bool IsConnected { get; private set; } = true;
    public bool IsGrabbing { get; private set; } = true;
    public CameraInfo CurrentCamera { get; private set; } = new CameraInfo { Id = "mock_camera", Name = "模拟摄像头" };

    public event EventHandler<bool>? ConnectionStatusChanged;
    public event EventHandler<byte[]>? ImageGrabbed;
    public event EventHandler<CameraImageData>? ImageDataGrabbed;
    public event EventHandler<string>? ErrorOccurred;

    public Task<List<CameraInfo>> EnumCamerasAsync()
    {
        return Task.FromResult(new List<CameraInfo>
        {
            new CameraInfo { Id = "mock_camera", Name = "模拟摄像头", Model = "Mock" }
        });
    }

    public Task<bool> ConnectAsync(CameraInfo camera)
    {
        IsConnected = true;
        ConnectionStatusChanged?.Invoke(this, true);
        return Task.FromResult(true);
    }

    public void Disconnect()
    {
        IsConnected = false;
        ConnectionStatusChanged?.Invoke(this, false);
    }

    public Task<bool> StartGrabbingAsync()
    {
        IsGrabbing = true;
        return Task.FromResult(true);
    }

    public void StopGrabbing()
    {
        IsGrabbing = false;
    }

    public Task<bool> SetExposureTimeAsync(float exposureTime) => Task.FromResult(true);
    public Task<bool> SetGainAsync(float gain) => Task.FromResult(true);
    public Task<float> GetExposureTimeAsync() => Task.FromResult(10000f);
    public Task<float> GetGainAsync() => Task.FromResult(0f);
    public Task<(float Min, float Max)> GetExposureTimeRangeAsync() => Task.FromResult((100f, 100000f));
    public Task<(float Min, float Max)> GetGainRangeAsync() => Task.FromResult((0f, 24f));

    public void Dispose() { }

    public Task<Dictionary<string, CaptureFrame>> CaptureAsync()
    {
        _frameNumber++;

        // 创建模拟帧 - 模拟人体姿态
        var frames = new Dictionary<string, CaptureFrame>
        {
            ["main_camera"] = new CaptureFrame
            {
                Image = CreateMockImageWithPose(),
                Timestamp = DateTime.Now,
                FrameNumber = _frameNumber
            }
        };

        return Task.FromResult(frames);
    }

    /// <summary>
    /// 创建带有人体姿态标记的模拟图像
    /// </summary>
    private SKBitmap CreateMockImageWithPose()
    {
        var bitmap = new SKBitmap(1280, 720);

        using (var canvas = new SKCanvas(bitmap))
        {
            // 背景
            canvas.Clear(new SKColor(30, 30, 30));

            // 绘制区域
            DrawRegions(canvas);

            // 模拟人体姿态（随机位置）
            DrawMockHumanPose(canvas);
        }

        return bitmap;
    }

    /// <summary>
    /// 绘制区域
    /// </summary>
    private void DrawRegions(SKCanvas canvas)
    {
        using var paint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2,
            IsAntialias = true
        };

        // 手机放置区 - 绿色
        paint.Color = new SKColor(76, 175, 80);
        canvas.DrawRect(200, 400, 150, 150, paint);
        DrawLabel(canvas, "📱 手机放置区", 205, 395, paint.Color);

        // 耳边区域 - 蓝色
        paint.Color = new SKColor(33, 150, 243);
        canvas.DrawRect(450, 150, 100, 100, paint);
        DrawLabel(canvas, "👂 耳边区域", 455, 145, paint.Color);

        // 面部区域 - 橙色
        paint.Color = new SKColor(255, 152, 0);
        canvas.DrawRect(400, 100, 200, 200, paint);
        DrawLabel(canvas, "😊 面部区域", 405, 95, paint.Color);
    }

    /// <summary>
    /// 绘制模拟人体姿态
    /// </summary>
    private void DrawMockHumanPose(SKCanvas canvas)
    {
        using var paint = new SKPaint
        {
            Color = new SKColor(0, 255, 0),
            StrokeWidth = 3,
            IsAntialias = true
        };

        using var pointPaint = new SKPaint
        {
            Color = new SKColor(255, 0, 0),
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };

        // 模拟人体中心位置（随时间移动）
        float centerX = 500 + (float)Math.Sin(_frameNumber * 0.1) * 100;
        float centerY = 300;

        // 绘制人体骨架（简化版）
        // 头部
        canvas.DrawCircle(centerX, centerY - 80, 20, pointPaint);

        // 身体
        canvas.DrawLine(centerX, centerY - 60, centerX, centerY + 40, paint);

        // 左臂
        canvas.DrawLine(centerX, centerY - 40, centerX - 40, centerY, paint);
        canvas.DrawLine(centerX - 40, centerY, centerX - 60, centerY + 60, paint);
        canvas.DrawCircle(centerX - 60, centerY + 60, 8, pointPaint); // 左手

        // 右臂（模拟拿起手机动作）
        float rightHandX = centerX + 80 + (float)Math.Sin(_frameNumber * 0.2) * 50;
        float rightHandY = centerY - 20 + (float)Math.Cos(_frameNumber * 0.2) * 30;

        canvas.DrawLine(centerX, centerY - 40, centerX + 40, centerY - 20, paint);
        canvas.DrawLine(centerX + 40, centerY - 20, rightHandX, rightHandY, paint);
        canvas.DrawCircle(rightHandX, rightHandY, 8, pointPaint); // 右手

        // 绘制手部轨迹提示
        using var trailPaint = new SKPaint
        {
            Color = new SKColor(255, 255, 0, 128),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2,
            IsAntialias = true
        };
        canvas.DrawCircle(rightHandX, rightHandY, 15, trailPaint);

        // 显示坐标
        using var textPaint = new SKPaint
        {
            Color = SKColors.White,
            TextSize = 14,
            IsAntialias = true
        };
        canvas.DrawText($"右手: ({rightHandX:F0}, {rightHandY:F0})", 10, 30, textPaint);
        canvas.DrawText($"帧: {_frameNumber}", 10, 50, textPaint);
    }

    /// <summary>
    /// 绘制标签
    /// </summary>
    private void DrawLabel(SKCanvas canvas, string text, float x, float y, SKColor color)
    {
        using var paint = new SKPaint
        {
            Color = color,
            TextSize = 14,
            IsAntialias = true
        };
        canvas.DrawText(text, x, y, paint);
    }
}
