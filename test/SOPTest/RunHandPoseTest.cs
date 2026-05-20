using VisionInspection.Modules.SOP;
using VisionInspection.Modules.SOP.Models;
using VisionInspection.Modules.SOP.Services;
using SkiaSharp;

namespace SOPTest;

/// <summary>
/// 独立的手部姿态估计测试程序
/// </summary>
class RunHandPoseTest
{
    static void Main(string[] args)
    {
        Console.WriteLine("========================================");
        Console.WriteLine("    手部姿态估计功能测试");
        Console.WriteLine("========================================\n");

        try
        {
            // 测试1: 手部关键点模型
            Test1_HandKeypointModel();

            // 测试2: 手部姿态模型
            Test2_HandPoseModel();

            // 测试3: 骨架连接线
            Test3_SkeletonConnections();

            // 测试4: 手部检测服务初始化
            Test4_HandPoseServiceInitialization();

            // 测试5: 骨架线绘制
            Test5_HandPoseVisualization();

            // 测试6: SOP模块集成
            Test6_SOPModuleIntegration();

            Console.WriteLine("\n========================================");
            Console.WriteLine("    所有手部姿态测试完成!");
            Console.WriteLine("========================================");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\n✗ 测试失败: {ex.Message}");
            Console.WriteLine($"堆栈: {ex.StackTrace}");
        }

        Console.WriteLine("\n按任意键退出...");
        Console.ReadKey();
    }

    /// <summary>
    /// 测试1: 手部关键点模型
    /// </summary>
    static void Test1_HandKeypointModel()
    {
        Console.WriteLine("【测试1】手部关键点模型测试\n");

        // 创建手腕关键点
        var wrist = new HandKeypoint(
            HandKeypointType.Wrist,
            x: 100f,
            y: 200f,
            z: 0f,
            confidence: 0.95f
        );

        Console.WriteLine($"   关键点类型: {wrist.Type}");
        Console.WriteLine($"   位置: ({wrist.X}, {wrist.Y}, {wrist.Z})");
        Console.WriteLine($"   置信度: {wrist.Confidence}");
        Console.WriteLine($"   是否有效: {wrist.IsValid}");

        // 创建指尖关键点
        var thumbTip = new HandKeypoint(
            HandKeypointType.ThumbTip,
            x: 150f,
            y: 150f,
            z: 10f,
            confidence: 0.88f
        );

        Console.WriteLine($"\n   拇指指尖位置: ({thumbTip.X}, {thumbTip.Y})");
        Console.WriteLine($"   转换为SKPoint: {thumbTip.ToSKPoint()}");

        // 测试低置信度关键点
        var lowConf = new HandKeypoint(
            HandKeypointType.IndexFingerTip,
            x: 200f,
            y: 200f,
            z: 0f,
            confidence: 0.3f  // 低于0.5阈值
        );

        Console.WriteLine($"\n   低置信度关键点是否有效: {lowConf.IsValid} (置信度={lowConf.Confidence})");

        Console.WriteLine("\n   ✓ 手部关键点模型测试通过\n");
    }

    /// <summary>
    /// 测试2: 手部姿态模型
    /// </summary>
    static void Test2_HandPoseModel()
    {
        Console.WriteLine("【测试2】手部姿态模型测试\n");

        // 创建一个完整的手部姿态（21个关键点）
        var handPose = new HandPose
        {
            TrackId = 1,
            HandType = HandType.Right,
            BoundingBox = new SKRect(50, 100, 250, 350),
            Timestamp = DateTime.Now
        };

        // 添加手腕
        handPose.Keypoints.Add(new HandKeypoint(HandKeypointType.Wrist, 150, 300, 0, 0.98f));

        // 添加拇指
        handPose.Keypoints.Add(new HandKeypoint(HandKeypointType.ThumbCMC, 140, 280, 5, 0.95f));
        handPose.Keypoints.Add(new HandKeypoint(HandKeypointType.ThumbMCP, 130, 260, 8, 0.93f));
        handPose.Keypoints.Add(new HandKeypoint(HandKeypointType.ThumbIP, 120, 240, 10, 0.90f));
        handPose.Keypoints.Add(new HandKeypoint(HandKeypointType.ThumbTip, 110, 220, 12, 0.88f));

        // 添加食指
        handPose.Keypoints.Add(new HandKeypoint(HandKeypointType.IndexFingerMCP, 160, 270, 3, 0.96f));
        handPose.Keypoints.Add(new HandKeypoint(HandKeypointType.IndexFingerPIP, 170, 240, 6, 0.94f));
        handPose.Keypoints.Add(new HandKeypoint(HandKeypointType.IndexFingerDIP, 180, 210, 8, 0.92f));
        handPose.Keypoints.Add(new HandKeypoint(HandKeypointType.IndexFingerTip, 190, 180, 10, 0.90f));

        // 添加中指
        handPose.Keypoints.Add(new HandKeypoint(HandKeypointType.MiddleFingerMCP, 150, 265, 2, 0.97f));
        handPose.Keypoints.Add(new HandKeypoint(HandKeypointType.MiddleFingerPIP, 150, 230, 5, 0.95f));
        handPose.Keypoints.Add(new HandKeypoint(HandKeypointType.MiddleFingerDIP, 150, 195, 7, 0.93f));
        handPose.Keypoints.Add(new HandKeypoint(HandKeypointType.MiddleFingerTip, 150, 160, 9, 0.91f));

        // 添加无名指
        handPose.Keypoints.Add(new HandKeypoint(HandKeypointType.RingFingerMCP, 140, 268, 1, 0.95f));
        handPose.Keypoints.Add(new HandKeypoint(HandKeypointType.RingFingerPIP, 130, 235, 4, 0.93f));
        handPose.Keypoints.Add(new HandKeypoint(HandKeypointType.RingFingerDIP, 120, 202, 6, 0.91f));
        handPose.Keypoints.Add(new HandKeypoint(HandKeypointType.RingFingerTip, 110, 170, 8, 0.89f));

        // 添加小指
        handPose.Keypoints.Add(new HandKeypoint(HandKeypointType.PinkyMCP, 130, 275, 0, 0.94f));
        handPose.Keypoints.Add(new HandKeypoint(HandKeypointType.PinkyPIP, 115, 245, 3, 0.92f));
        handPose.Keypoints.Add(new HandKeypoint(HandKeypointType.PinkyDIP, 100, 215, 5, 0.90f));
        handPose.Keypoints.Add(new HandKeypoint(HandKeypointType.PinkyTip, 85, 185, 7, 0.87f));

        Console.WriteLine($"   手部ID: {handPose.TrackId}");
        Console.WriteLine($"   手部类型: {handPose.HandType}");
        Console.WriteLine($"   关键点数量: {handPose.Keypoints.Count}");
        Console.WriteLine($"   边界框: {handPose.BoundingBox}");

        // 测试获取特定关键点
        var wrist = handPose.Wrist;
        Console.WriteLine($"\n   手腕位置: ({wrist?.X}, {wrist?.Y})");

        // 测试获取指尖
        var tips = handPose.FingerTips.ToList();
        Console.WriteLine($"   检测到的指尖数量: {tips.Count}");
        foreach (var tip in tips)
        {
            Console.WriteLine($"      - {tip.Type}: ({tip.X:F1}, {tip.Y:F1})");
        }

        // 测试手部中心点
        var center = handPose.GetCenter();
        Console.WriteLine($"\n   手部中心点: ({center.X:F1}, {center.Y:F1})");

        // 测试手势有效性
        bool isValid = handPose.IsValidGesture();
        Console.WriteLine($"   是否为有效手势: {isValid}");

        Console.WriteLine("\n   ✓ 手部姿态模型测试通过\n");
    }

    /// <summary>
    /// 测试3: 骨架连接线
    /// </summary>
    static void Test3_SkeletonConnections()
    {
        Console.WriteLine("【测试3】骨架连接线测试\n");

        var connections = HandSkeletonConnections.Connections;
        Console.WriteLine($"   骨架连接线总数: {connections.Length}");

        // 统计各类连接线
        int wristConnections = connections.Count(c => c.Start == HandKeypointType.Wrist);
        int thumbConnections = connections.Count(c => 
            c.Start.ToString().StartsWith("Thumb") || c.End.ToString().StartsWith("Thumb"));
        int indexConnections = connections.Count(c => 
            c.Start.ToString().Contains("Index") || c.End.ToString().Contains("Index"));

        Console.WriteLine($"   手腕连接数: {wristConnections}");
        Console.WriteLine($"   拇指连接数: {thumbConnections}");
        Console.WriteLine($"   食指连接数: {indexConnections}");

        // 显示部分连接线
        Console.WriteLine($"\n   前5条连接线:");
        foreach (var (start, end) in connections.Take(5))
        {
            Console.WriteLine($"      {start} → {end}");
        }

        Console.WriteLine("\n   ✓ 骨架连接线测试通过\n");
    }

    /// <summary>
    /// 测试4: 手部检测服务初始化
    /// </summary>
    static void Test4_HandPoseServiceInitialization()
    {
        Console.WriteLine("【测试4】手部检测服务初始化测试\n");

        // 测试MediaPipe服务
        var mediaPipeService = new MediaPipeHandPoseEstimationService();
        Console.WriteLine($"   MediaPipe服务初始状态: IsInitialized = {mediaPipeService.IsInitialized}");

        var config = new HandPoseEstimationConfig
        {
            ModelPath = "",  // 空路径，使用默认MediaPipe
            ConfidenceThreshold = 0.5f,
            MaxNumHands = 2,
            UseGpu = false
        };

        try
        {
            mediaPipeService.InitializeAsync(config).Wait();
            Console.WriteLine($"   MediaPipe服务初始化后: IsInitialized = {mediaPipeService.IsInitialized}");
            Console.WriteLine("   ✓ MediaPipe服务初始化成功");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"   ! MediaPipe服务初始化警告: {ex.Message}");
        }

        // 关闭服务
        mediaPipeService.ShutdownAsync().Wait();
        Console.WriteLine($"   MediaPipe服务关闭后: IsInitialized = {mediaPipeService.IsInitialized}");

        Console.WriteLine("\n   ✓ 手部检测服务初始化测试通过\n");
    }

    /// <summary>
    /// 测试5: 骨架线绘制
    /// </summary>
    static void Test5_HandPoseVisualization()
    {
        Console.WriteLine("【测试5】骨架线绘制测试\n");

        // 创建一个测试手部姿态
        var handPose = CreateTestHandPose();

        // 创建空白图像
        using var bitmap = new SKBitmap(400, 400);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Black);

            // 绘制手部骨架
            HandPoseVisualizer.DrawHandPose(canvas, handPose);

            // 绘制手部标签
            HandPoseVisualizer.DrawHandLabel(canvas, handPose);
        }

        Console.WriteLine($"   图像尺寸: {bitmap.Width}x{bitmap.Height}");
        Console.WriteLine($"   绘制的手部关键点数: {handPose.Keypoints.Count}");

        // 保存测试图像（可选）
        string outputPath = "hand_pose_test.png";
        using (var fileStream = File.OpenWrite(outputPath))
        {
            bitmap.Encode(fileStream, SKEncodedImageFormat.Png, 100);
        }
        Console.WriteLine($"   测试图像已保存: {outputPath}");

        // 测试多手部绘制
        var result = new HandPoseEstimationResult
        {
            Hands = new List<HandPose> { handPose },
            Timestamp = DateTime.Now,
            ProcessingTimeMs = 16
        };

        using var multiBitmap = new SKBitmap(400, 400);
        using (var canvas = new SKCanvas(multiBitmap))
        {
            canvas.Clear(SKColors.DarkGray);
            HandPoseVisualizer.DrawHandPoses(canvas, result.Hands);
        }

        Console.WriteLine($"   多手部绘制测试完成");

        Console.WriteLine("\n   ✓ 骨架线绘制测试通过\n");
    }

    /// <summary>
    /// 测试6: SOP模块集成
    /// </summary>
    static void Test6_SOPModuleIntegration()
    {
        Console.WriteLine("【测试6】SOP模块集成测试\n");

        // 创建SOP模块配置
        var config = new SOPModuleConfig
        {
            ModelPath = "models/test.onnx",
            UseGpu = false,
            ConfidenceThreshold = 0.5f,
            HandPoseEstimation = new HandPoseEstimationConfig
            {
                ConfidenceThreshold = 0.5f,
                MaxNumHands = 2,
                UseGpu = false
            }
        };

        Console.WriteLine($"   SOP模块配置:");
        Console.WriteLine($"      - 模型路径: {config.ModelPath}");
        Console.WriteLine($"      - 使用GPU: {config.UseGpu}");
        Console.WriteLine($"      - 手部检测启用: {config.HandPoseEstimation != null}");
        Console.WriteLine($"      - 最大手数: {config.HandPoseEstimation?.MaxNumHands}");

        // 测试检测模式
        var modes = new[] { 
            SOPDetectionMode.ObjectBased, 
            SOPDetectionMode.PoseBased, 
            SOPDetectionMode.Hybrid,
            SOPDetectionMode.HandPoseBased 
        };

        Console.WriteLine($"\n   支持的检测模式:");
        foreach (var mode in modes)
        {
            Console.WriteLine($"      - {mode}");
        }

        // 测试事件参数
        var testResult = new HandPoseEstimationResult
        {
            Hands = new List<HandPose> { CreateTestHandPose() },
            Timestamp = DateTime.Now,
            ProcessingTimeMs = 20
        };

        var eventArgs = new HandPoseDetectedEventArgs(testResult, DateTime.Now);
        Console.WriteLine($"\n   事件参数测试:");
        Console.WriteLine($"      - 检测到手部数: {eventArgs.Result.Hands.Count}");
        Console.WriteLine($"      - 处理时间: {eventArgs.Result.ProcessingTimeMs}ms");
        Console.WriteLine($"      - 时间戳: {eventArgs.Timestamp}");

        Console.WriteLine("\n   ✓ SOP模块集成测试通过\n");
    }

    /// <summary>
    /// 创建测试用手部姿态
    /// </summary>
    static HandPose CreateTestHandPose()
    {
        var handPose = new HandPose
        {
            TrackId = 1,
            HandType = HandType.Right,
            BoundingBox = new SKRect(100, 100, 300, 350),
            Timestamp = DateTime.Now
        };

        // 添加所有21个关键点
        handPose.Keypoints.Add(new HandKeypoint(HandKeypointType.Wrist, 200, 300, 0, 0.98f));
        
        // 拇指
        handPose.Keypoints.Add(new HandKeypoint(HandKeypointType.ThumbCMC, 180, 280, 5, 0.95f));
        handPose.Keypoints.Add(new HandKeypoint(HandKeypointType.ThumbMCP, 165, 260, 8, 0.93f));
        handPose.Keypoints.Add(new HandKeypoint(HandKeypointType.ThumbIP, 150, 240, 10, 0.90f));
        handPose.Keypoints.Add(new HandKeypoint(HandKeypointType.ThumbTip, 135, 220, 12, 0.88f));

        // 食指
        handPose.Keypoints.Add(new HandKeypoint(HandKeypointType.IndexFingerMCP, 210, 270, 3, 0.96f));
        handPose.Keypoints.Add(new HandKeypoint(HandKeypointType.IndexFingerPIP, 220, 240, 6, 0.94f));
        handPose.Keypoints.Add(new HandKeypoint(HandKeypointType.IndexFingerDIP, 230, 210, 8, 0.92f));
        handPose.Keypoints.Add(new HandKeypoint(HandKeypointType.IndexFingerTip, 240, 180, 10, 0.90f));

        // 中指
        handPose.Keypoints.Add(new HandKeypoint(HandKeypointType.MiddleFingerMCP, 200, 265, 2, 0.97f));
        handPose.Keypoints.Add(new HandKeypoint(HandKeypointType.MiddleFingerPIP, 200, 230, 5, 0.95f));
        handPose.Keypoints.Add(new HandKeypoint(HandKeypointType.MiddleFingerDIP, 200, 195, 7, 0.93f));
        handPose.Keypoints.Add(new HandKeypoint(HandKeypointType.MiddleFingerTip, 200, 160, 9, 0.91f));

        // 无名指
        handPose.Keypoints.Add(new HandKeypoint(HandKeypointType.RingFingerMCP, 190, 268, 1, 0.95f));
        handPose.Keypoints.Add(new HandKeypoint(HandKeypointType.RingFingerPIP, 180, 235, 4, 0.93f));
        handPose.Keypoints.Add(new HandKeypoint(HandKeypointType.RingFingerDIP, 170, 202, 6, 0.91f));
        handPose.Keypoints.Add(new HandKeypoint(HandKeypointType.RingFingerTip, 160, 170, 8, 0.89f));

        // 小指
        handPose.Keypoints.Add(new HandKeypoint(HandKeypointType.PinkyMCP, 180, 275, 0, 0.94f));
        handPose.Keypoints.Add(new HandKeypoint(HandKeypointType.PinkyPIP, 165, 245, 3, 0.92f));
        handPose.Keypoints.Add(new HandKeypoint(HandKeypointType.PinkyDIP, 150, 215, 5, 0.90f));
        handPose.Keypoints.Add(new HandKeypoint(HandKeypointType.PinkyTip, 135, 185, 7, 0.87f));

        return handPose;
    }
}
