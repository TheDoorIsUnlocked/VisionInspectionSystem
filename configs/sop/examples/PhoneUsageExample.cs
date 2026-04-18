using VisionInspection.Modules.SOP;
using VisionInspection.Modules.SOP.Models;
using VisionInspection.Modules.SOP.Services;
using VisionInspection.Core.Models;

namespace VisionInspection.Examples;

/// <summary>
/// 手机使用场景示例
/// 演示：拿起手机 -> 打电话 -> 放下手机
/// </summary>
public class PhoneUsageExample
{
    private SOPModule _sopModule = null!;
    private RegionConfigLoader _regionLoader = null!;

    /// <summary>
    /// 初始化示例
    /// </summary>
    public async Task InitializeAsync()
    {
        Console.WriteLine("=== 手机使用场景 SOP 示例 ===\n");

        // 1. 加载区域配置
        Console.WriteLine("1. 加载区域配置...");
        _regionLoader = new RegionConfigLoader();
        var configPath = "configs/sop/regions/phone_usage_regions.json";
        var regionConfig = await _regionLoader.LoadFromJsonAsync(configPath);
        Console.WriteLine($"   已加载 {regionConfig.Regions.Count} 个区域");
        Console.WriteLine($"   区域: {string.Join(", ", regionConfig.Regions.Keys)}");

        // 2. 创建SOP模块
        Console.WriteLine("\n2. 初始化SOP模块...");
        _sopModule = new SOPModule();

        // 3. 订阅事件
        SubscribeToEvents();

        // 4. 加载SOP流程
        Console.WriteLine("\n3. 加载SOP流程...");
        var yamlPath = "configs/sop/sop_phone_usage.yaml";
        _sopModule.StartWorkflowFromYaml(yamlPath);
        Console.WriteLine($"   工作流: {_sopModule.CurrentWorkflow?.Name}");
        Console.WriteLine($"   步骤数: {_sopModule.CurrentWorkflow?.Steps.Count}");

        // 5. 设置检测模式为姿态检测
        _sopModule.DetectionMode = SOPDetectionMode.PoseBased;
        Console.WriteLine($"   检测模式: {_sopModule.DetectionMode}");

        Console.WriteLine("\n=== 初始化完成 ===\n");
    }

    /// <summary>
    /// 订阅SOP事件
    /// </summary>
    private void SubscribeToEvents()
    {
        // 步骤变化事件
        _sopModule.StepChanged += (s, e) =>
        {
            Console.WriteLine($"[步骤变化] {e.PreviousStepId} -> {e.CurrentStepId} ({e.CurrentStepName})");
        };

        // 状态变化事件
        _sopModule.StateChanged += (s, e) =>
        {
            Console.WriteLine($"[状态变化] {e.OldState} -> {e.NewState}");
        };

        // 姿态检测事件
        _sopModule.PoseDetected += (s, e) =>
        {
            Console.WriteLine($"[姿态检测] 检测到 {e.Poses.Count} 个人体");
            foreach (var pose in e.Poses)
            {
                var leftHand = pose.LeftWrist;
                var rightHand = pose.RightWrist;
                Console.WriteLine($"   人体 #{pose.TrackId}: 左手({leftHand?.X:F0},{leftHand?.Y:F0}) 右手({rightHand?.X:F0},{rightHand?.Y:F0})");

                // 检查手是否在特定区域
                CheckHandRegions(pose);
            }
        };

        // 违规检测事件
        _sopModule.ViolationDetected += (s, e) =>
        {
            Console.WriteLine($"[违规检测] {e.Violation.Type}: {e.Violation.Description}");
        };

        // 步骤完成事件
        _sopModule.StepCompleted += (s, e) =>
        {
            Console.WriteLine($"[步骤完成] 步骤 {e.StepId} 完成，耗时: {e.DurationMs}ms");
        };

        // 工作流完成事件
        _sopModule.WorkflowCompleted += (s, e) =>
        {
            Console.WriteLine($"[工作流完成] 总耗时: {e.TotalDurationMs}ms");
        };
    }

    /// <summary>
    /// 检查手是否在特定区域
    /// </summary>
    private void CheckHandRegions(HumanPose pose)
    {
        var leftHand = pose.LeftWrist;
        var rightHand = pose.RightWrist;

        // 检查手机放置区
        if (leftHand?.IsValid == true &&
            _regionLoader.IsPointInRegion(leftHand.X, leftHand.Y, "phone_table"))
        {
            Console.WriteLine($"   -> 左手在手机放置区");
        }
        if (rightHand?.IsValid == true &&
            _regionLoader.IsPointInRegion(rightHand.X, rightHand.Y, "phone_table"))
        {
            Console.WriteLine($"   -> 右手在手机放置区");
        }

        // 检查耳边区域
        if (leftHand?.IsValid == true &&
            _regionLoader.IsPointInRegion(leftHand.X, leftHand.Y, "ear_region"))
        {
            Console.WriteLine($"   -> 左手在耳边区域（打电话）");
        }
        if (rightHand?.IsValid == true &&
            _regionLoader.IsPointInRegion(rightHand.X, rightHand.Y, "ear_region"))
        {
            Console.WriteLine($"   -> 右手在耳边区域（打电话）");
        }
    }

    /// <summary>
    /// 处理一帧图像
    /// </summary>
    public async Task ProcessFrameAsync(CaptureFrame frame)
    {
        var frames = new List<CaptureFrame> { frame };
        var result = await _sopModule.ProcessAsync(frames);

        // 输出处理结果
        if (result.HasViolation)
        {
            Console.WriteLine($"[警告] 检测到违规: {result.ErrorMessage}");
        }
    }

    /// <summary>
    /// 模拟完整流程
    /// </summary>
    public async Task SimulateFullProcess()
    {
        Console.WriteLine("\n=== 开始模拟手机使用流程 ===\n");

        // 模拟步骤1: 等待
        Console.WriteLine("【步骤1】等待人员就位...");
        await Task.Delay(1000);

        // 模拟步骤2: 拿起手机
        Console.WriteLine("\n【步骤2】模拟拿起手机...");
        Console.WriteLine("   - 右手移动到手机放置区");
        Console.WriteLine("   - 拿起手机");
        Console.WriteLine("   - 手离开放置区");
        await Task.Delay(2000);

        // 模拟步骤3: 打电话
        Console.WriteLine("\n【步骤3】模拟打电话...");
        Console.WriteLine("   - 手移动到耳边区域");
        Console.WriteLine("   - 保持1秒以上");
        await Task.Delay(3000);

        // 模拟步骤4: 放下手机
        Console.WriteLine("\n【步骤4】模拟放下手机...");
        Console.WriteLine("   - 手从耳边移回");
        Console.WriteLine("   - 手机放回放置区");
        await Task.Delay(2000);

        // 模拟步骤5: 完成
        Console.WriteLine("\n【步骤5】流程完成");
        Console.WriteLine("   - 回到初始状态");
        await Task.Delay(1000);

        Console.WriteLine("\n=== 模拟流程结束 ===");
    }

    /// <summary>
    /// 获取当前区域可视化信息
    /// </summary>
    public void PrintRegionInfo()
    {
        Console.WriteLine("\n=== 区域配置信息 ===");
        var regions = _regionLoader.GetAllRegions();
        foreach (var (id, rect) in regions)
        {
            var definition = _regionLoader.GetRegionDefinition(id);
            Console.WriteLine($"\n区域: {id}");
            Console.WriteLine($"  名称: {definition?.Name}");
            Console.WriteLine($"  坐标: ({rect.Left:F0}, {rect.Top:F0}) - ({rect.Right:F0}, {rect.Bottom:F0})");
            Console.WriteLine($"  尺寸: {rect.Width:F0} x {rect.Height:F0}");
            Console.WriteLine($"  描述: {definition?.Description}");
        }
    }

    /// <summary>
    /// 运行完整示例
    /// </summary>
    public static async Task RunExample()
    {
        var example = new PhoneUsageExample();

        try
        {
            // 初始化
            await example.InitializeAsync();

            // 打印区域信息
            example.PrintRegionInfo();

            // 模拟流程
            await example.SimulateFullProcess();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"错误: {ex.Message}");
            Console.WriteLine($"堆栈: {ex.StackTrace}");
        }
    }
}

/// <summary>
/// 程序入口
/// </summary>
public class Program
{
    public static async Task Main(string[] args)
    {
        await PhoneUsageExample.RunExample();
    }
}
