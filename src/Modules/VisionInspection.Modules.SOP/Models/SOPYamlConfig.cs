using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace VisionInspection.Modules.SOP.Models;

/// <summary>
/// YAML格式的SOP配置（与实战指南一致）
/// </summary>
public class SOPYamlConfig
{
    [YamlMember(Alias = "sop")]
    public SopyamlSop? Sop { get; set; }
}

public class SopyamlSop
{
    [YamlMember(Alias = "name")]
    public string Name { get; set; } = "";

    [YamlMember(Alias = "version")]
    public string Version { get; set; } = "1.0";

    [YamlMember(Alias = "description")]
    public string? Description { get; set; }

    [YamlMember(Alias = "steps")]
    public List<SopyamlStep> Steps { get; set; } = new();

    [YamlMember(Alias = "regions")]
    public Dictionary<string, SopyamlRegion>? Regions { get; set; }

    [YamlMember(Alias = "forbidden_zones")]
    public Dictionary<string, SopyamlRegion>? ForbiddenZones { get; set; }

    [YamlMember(Alias = "settings")]
    public SopyamlSettings? Settings { get; set; }

    [YamlMember(Alias = "violations")]
    public List<SopyamlViolationDefinition>? Violations { get; set; }

    [YamlMember(Alias = "keypoints")]
    public SopyamlKeypoints? Keypoints { get; set; }

    [YamlMember(Alias = "visualization")]
    public SopyamlVisualization? Visualization { get; set; }

    // ⭐ 新增：SOP模型配置
    [YamlMember(Alias = "model")]
    public SopyamlModel? Model { get; set; }
}

public class SopyamlStep
{
    [YamlMember(Alias = "id")]
    public string Id { get; set; } = "";

    [YamlMember(Alias = "name")]
    public string Name { get; set; } = "";

    [YamlMember(Alias = "timeout")]
    public int Timeout { get; set; } = 30;

    [YamlMember(Alias = "transitions")]
    public List<string> Transitions { get; set; } = new();

    [YamlMember(Alias = "detection")]
    public SopyamlDetection? Detection { get; set; }

    [YamlMember(Alias = "description")]
    public string? Description { get; set; }

    [YamlMember(Alias = "violation_rules")]
    public List<SopyamlViolationRule>? ViolationRules { get; set; }

    /// <summary>
    /// 该步骤中禁止出现的YOLO检测类别（防跳步）
    /// </summary>
    [YamlMember(Alias = "forbidden_objects")]
    public List<string>? ForbiddenObjects { get; set; }

    /// <summary>
    /// 该步骤中必须保持存在的YOLO检测类别（防零件被移除）
    /// </summary>
    [YamlMember(Alias = "must_keep")]
    public List<string>? MustKeep { get; set; }

    /// <summary>
    /// 该步骤（通常是最终成品校验步）必须放置/存在的物料清单。
    /// 任意一项在画面（或指定区域内）缺失即判"漏放"。
    /// 这是通用的数据驱动规则——每个配方声明自己的物料即可跨产品复用。
    /// </summary>
    [YamlMember(Alias = "required_objects")]
    public List<SopyamlRequiredObject>? RequiredObjects { get; set; }
}

/// <summary>
/// 必放物料项（漏放校验用）
/// </summary>
public class SopyamlRequiredObject
{
    /// <summary>
    /// 必须存在的 YOLO 检测类别名
    /// </summary>
    [YamlMember(Alias = "object")]
    public string Object { get; set; } = "";

    /// <summary>
    /// 可选：限定该物料必须出现的区域 ID（不填则为全局存在即可）
    /// </summary>
    [YamlMember(Alias = "zone")]
    public string? Zone { get; set; }

    /// <summary>
    /// 最小置信度（0-1），默认 0.5
    /// </summary>
    [YamlMember(Alias = "min_confidence")]
    public float MinConfidence { get; set; } = 0.5f;
}

public class SopyamlSettings
{
    [YamlMember(Alias = "detectionMode")]
    public string? DetectionMode { get; set; }

    [YamlMember(Alias = "confidenceThreshold")]
    public float ConfidenceThreshold { get; set; } = 0.6f;

    [YamlMember(Alias = "stableFrames")]
    public int StableFrames { get; set; } = 3;

    [YamlMember(Alias = "timeoutSeconds")]
    public int TimeoutSeconds { get; set; } = 30;

    [YamlMember(Alias = "enableSkipDetection")]
    public bool EnableSkipDetection { get; set; } = true;

    [YamlMember(Alias = "enableTimeoutDetection")]
    public bool EnableTimeoutDetection { get; set; } = true;

    /// <summary>
    /// 所有步骤通过后是否自动重置状态机，循环检测下一轮。
    /// 默认 true，符合持续监控场景；用户可在 yaml 中显式设为 false。
    /// </summary>
    [YamlMember(Alias = "auto_reset_on_complete")]
    public bool AutoResetOnComplete { get; set; } = true;

    /// <summary>
    /// 自动重置前的延迟秒数（让 UI 显示 PASS 一段时间再开始下一轮）。
    /// </summary>
    [YamlMember(Alias = "reset_delay_sec")]
    public int ResetDelaySec { get; set; } = 3;
}

public class SopyamlDetection
{
    [YamlMember(Alias = "method")]
    public string Method { get; set; } = "";

    [YamlMember(Alias = "hand")]
    public string? Hand { get; set; }

    [YamlMember(Alias = "region")]
    public string? Region { get; set; }

    [YamlMember(Alias = "from_region")]
    public string? FromRegion { get; set; }

    [YamlMember(Alias = "to_region")]
    public string? ToRegion { get; set; }

    [YamlMember(Alias = "action")]
    public string? Action { get; set; }

    [YamlMember(Alias = "min_confidence")]
    public float MinConfidence { get; set; } = 0.6f;

    [YamlMember(Alias = "target_object")]
    public string? TargetObject { get; set; }

    [YamlMember(Alias = "stable_frames")]
    public int StableFrames { get; set; } = 5;

    [YamlMember(Alias = "duration_ms")]
    public int DurationMs { get; set; } = 1000;

    [YamlMember(Alias = "tolerance")]
    public float Tolerance { get; set; } = 20f;

    [YamlMember(Alias = "description")]
    public string? Description { get; set; }
}

public class SopyamlRegion
{
    [YamlMember(Alias = "x1")]
    public float X1 { get; set; }

    [YamlMember(Alias = "y1")]
    public float Y1 { get; set; }

    [YamlMember(Alias = "x2")]
    public float X2 { get; set; }

    [YamlMember(Alias = "y2")]
    public float Y2 { get; set; }

    [YamlMember(Alias = "name")]
    public string? Name { get; set; }

    [YamlMember(Alias = "description")]
    public string? Description { get; set; }

    [YamlMember(Alias = "color")]
    public string? Color { get; set; }
}

public class SopyamlViolationRule
{
    [YamlMember(Alias = "type")]
    public string Type { get; set; } = "";

    [YamlMember(Alias = "description")]
    public string? Description { get; set; }
}

public class SopyamlViolationDefinition
{
    [YamlMember(Alias = "id")]
    public string Id { get; set; } = "";

    [YamlMember(Alias = "name")]
    public string Name { get; set; } = "";

    [YamlMember(Alias = "description")]
    public string? Description { get; set; }

    [YamlMember(Alias = "severity")]
    public string Severity { get; set; } = "warning";
}

public class SopyamlKeypoints
{
    [YamlMember(Alias = "required")]
    public List<string>? Required { get; set; }
}

public class SopyamlVisualization
{
    [YamlMember(Alias = "show_regions")]
    public bool ShowRegions { get; set; } = true;

    [YamlMember(Alias = "show_keypoints")]
    public bool ShowKeypoints { get; set; } = true;

    [YamlMember(Alias = "show_skeleton")]
    public bool ShowSkeleton { get; set; } = true;

    [YamlMember(Alias = "show_tracking_id")]
    public bool ShowTrackingId { get; set; } = true;

    [YamlMember(Alias = "region_alpha")]
    public float RegionAlpha { get; set; } = 0.3f;

    [YamlMember(Alias = "keypoint_radius")]
    public int KeypointRadius { get; set; } = 5;
}

/// <summary>
/// SOP模型配置
/// </summary>
public class SopyamlModel
{
    /// <summary>
    /// ONNX模型文件的绝对路径或相对路径（相对于yolo_models目录）
    /// </summary>
    [YamlMember(Alias = "path")]
    public string Path { get; set; } = "yolo_models/sop_yolov8n.onnx";

    /// <summary>
    /// 模型类型: ObjectDetection / Segmentation / PoseEstimation / OBBDetection
    /// </summary>
    [YamlMember(Alias = "type")]
    public string Type { get; set; } = "ObjectDetection";

    /// <summary>
    /// 检测置信度阈值 (0-1)
    /// </summary>
    [YamlMember(Alias = "confidence")]
    public float Confidence { get; set; } = 0.6f;

    /// <summary>
    /// NMS IoU阈值 (0-1)
    /// </summary>
    [YamlMember(Alias = "iou")]
    public float Iou { get; set; } = 0.45f;

    /// <summary>
    /// 是否使用GPU
    /// </summary>
    [YamlMember(Alias = "use_gpu")]
    public bool UseGpu { get; set; } = true;

    /// <summary>
    /// GPU设备ID
    /// </summary>
    [YamlMember(Alias = "gpu_id")]
    public int GpuId { get; set; } = 0;

    /// <summary>
    /// 该模型可检测的类别名称列表
    /// </summary>
    [YamlMember(Alias = "classes")]
    public List<string> Classes { get; set; } = new();
}

/// <summary>
/// YAML配置转换器
/// </summary>
public static class SOPYamlConverter
{
    /// <summary>
    /// 从YAML文件加载SOP配置
    /// </summary>
    public static SOPWorkflow LoadFromYaml(string yamlPath)
    {
        if (!File.Exists(yamlPath))
        {
            throw new FileNotFoundException($"YAML配置文件不存在: {yamlPath}");
        }

        var yaml = File.ReadAllText(yamlPath);
        return ParseYaml(yaml, yamlPath);
    }

    /// <summary>
    /// 解析YAML字符串
    /// </summary>
    public static SOPWorkflow ParseYaml(string yaml, string sourcePath = "")
    {
        try
        {
            var deserializer = new DeserializerBuilder()
                .Build();

            var config = deserializer.Deserialize<SOPYamlConfig>(yaml);

            if (config?.Sop == null)
            {
                throw new InvalidOperationException("YAML配置格式错误：缺少'sop'根节点");
            }

            return ConvertToWorkflow(config.Sop, sourcePath);
        }
        catch (KeyNotFoundException knfEx)
        {
            Console.WriteLine($"[YAML ERROR] KeyNotFoundException during YAML parsing: {knfEx.Message}");
            Console.WriteLine($"[YAML ERROR] StackTrace: {knfEx.StackTrace}");
            throw new InvalidOperationException($"YAML解析失败 - 键未找到: {knfEx.Message}. 请检查YAML文件格式。", knfEx);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[YAML ERROR] Exception during YAML parsing: {ex.Message}");
            Console.WriteLine($"[YAML ERROR] StackTrace: {ex.StackTrace}");
            throw new InvalidOperationException($"YAML解析失败: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// 转换为SOPWorkflow
    /// </summary>
    private static SOPWorkflow ConvertToWorkflow(SopyamlSop yamlSop, string sourcePath)
    {
        try
        {
            var workflow = new SOPWorkflow
            {
                Id = Guid.NewGuid().ToString("N")[..8],
                Name = yamlSop.Name,
                Description = $"从YAML加载: {Path.GetFileName(sourcePath)}",
                Version = yamlSop.Version,
                CreatedAt = DateTime.Now,
                UpdatedAt = DateTime.Now,
                Steps = new List<SOPStep>(),
                Regions = new List<ZoneDefinition>(),
                Settings = new SOPGlobalSettings()
            };

        // 转换区域定义
        if (yamlSop.Regions != null)
        {
            foreach (var (regionId, region) in yamlSop.Regions)
            {
                workflow.Regions.Add(new ZoneDefinition
                {
                    ZoneId = regionId,
                    Name = region.Name ?? regionId,
                    X = region.X1,
                    Y = region.Y1,
                    Width = region.X2 - region.X1,
                    Height = region.Y2 - region.Y1
                });
            }
        }

        // 转换步骤
        int stepOrder = 1;
        foreach (var yamlStep in yamlSop.Steps)
        {
            // 使用yamlStep.Id作为StepId，如果Id为空则使用stepOrder
            int stepId = !string.IsNullOrEmpty(yamlStep.Id) 
                ? (int.TryParse(yamlStep.Id, out var parsedId) ? parsedId : stepOrder)
                : stepOrder;
            
            var step = new SOPStep
            {
                StepId = stepId,
                StepName = yamlStep.Name,
                Description = yamlStep.Description ?? $"步骤 {stepOrder}",
                Order = stepOrder++,
                TimeoutSec = yamlStep.Timeout,
                PassConditions = new List<StepCondition>(),
                ViolationRules = new List<ViolationRule>()
            };

            // 转换检测条件
            if (yamlStep.Detection != null)
            {
                var condition = ConvertDetectionToCondition(yamlStep.Detection);
                if (condition != null)
                {
                    step.PassConditions.Add(condition);
                }
            }

            // 转换 forbidden_objects → ViolationRule
            if (yamlStep.ForbiddenObjects != null && yamlStep.ForbiddenObjects.Count > 0)
            {
                step.ViolationRules.Add(new ViolationRule
                {
                    Type = ViolationType.ForbiddenObject,
                    Description = $"禁止出现: {string.Join(", ", yamlStep.ForbiddenObjects)}",
                    Severity = 2,
                    Parameters = new Dictionary<string, object>
                    {
                        ["ForbiddenClasses"] = yamlStep.ForbiddenObjects
                    }
                });
            }

            // 转换 must_keep → ViolationRule
            if (yamlStep.MustKeep != null && yamlStep.MustKeep.Count > 0)
            {
                step.ViolationRules.Add(new ViolationRule
                {
                    Type = ViolationType.ObjectRemoved,
                    Description = $"必须保持: {string.Join(", ", yamlStep.MustKeep)}",
                    Severity = 3,
                    Parameters = new Dictionary<string, object>
                    {
                        ["MustKeepClasses"] = yamlStep.MustKeep
                    }
                });
            }

            // 转换 required_objects → MissingRequiredObject（漏放）违规规则
            if (yamlStep.RequiredObjects != null && yamlStep.RequiredObjects.Count > 0)
            {
                var requiredList = new List<Dictionary<string, object>>();
                foreach (var ro in yamlStep.RequiredObjects)
                {
                    if (string.IsNullOrWhiteSpace(ro.Object)) continue;
                    requiredList.Add(new Dictionary<string, object>
                    {
                        ["class"] = ro.Object,
                        ["zone"] = ro.Zone ?? "",
                        ["min_confidence"] = ro.MinConfidence
                    });
                }

                if (requiredList.Count > 0)
                {
                    step.ViolationRules.Add(new ViolationRule
                    {
                        Type = ViolationType.MissingRequiredObject,
                        Description = $"漏放校验: {string.Join(", ", yamlStep.RequiredObjects.Where(o => !string.IsNullOrWhiteSpace(o.Object)).Select(o => o.Object))}",
                        Severity = 3,
                        Parameters = new Dictionary<string, object>
                        {
                            ["RequiredObjects"] = requiredList
                        }
                    });
                }
            }

            workflow.Steps.Add(step);
        }

        // ⭐ 转换全局设置
        if (yamlSop.Settings != null)
        {
            workflow.Settings = new SOPGlobalSettings
            {
                EnableSkipDetection = yamlSop.Settings.EnableSkipDetection,
                EnableTimeoutDetection = yamlSop.Settings.EnableTimeoutDetection,
                StableFrameCount = yamlSop.Settings.StableFrames,
                PositionTolerance = 20f, // 默认值
                // ⭐ 改为使用 yaml 字段（已带 snake_case alias，缺省时走 SopyamlSettings 模型默认值 true / 3），
                // 这样 SOP 完成后会自动循环检测下一轮，符合持续监控场景。
                AutoResetOnComplete = yamlSop.Settings.AutoResetOnComplete,
                ResetDelaySec = yamlSop.Settings.ResetDelaySec
            };
        }

        // ⭐ 转换模型配置
        if (yamlSop.Model != null && !string.IsNullOrEmpty(yamlSop.Model.Path))
        {
            workflow.Model = new SOPModelConfig
            {
                Path = yamlSop.Model.Path,
                Type = yamlSop.Model.Type,
                Confidence = yamlSop.Model.Confidence,
                Iou = yamlSop.Model.Iou,
                UseGpu = yamlSop.Model.UseGpu,
                GpuId = yamlSop.Model.GpuId,
                Classes = yamlSop.Model.Classes
            };

            if (string.IsNullOrEmpty(workflow.Description))
            {
                workflow.Description = $"模型: {System.IO.Path.GetFileName(yamlSop.Model.Path)}";
            }
            else
            {
                workflow.Description += $" | 模型: {System.IO.Path.GetFileName(yamlSop.Model.Path)}";
            }
        }

        // ⭐ 转换违规定义（如果有）
        if (yamlSop.Violations != null)
        {
            Console.WriteLine($"[SOP] 加载了 {yamlSop.Violations.Count} 个违规定义");
        }

        // ⭐ 转换关键点配置（如果有）
        if (yamlSop.Keypoints != null)
        {
            Console.WriteLine($"[SOP] 加载了关键点配置: {yamlSop.Keypoints.Required?.Count ?? 0} 个必需关键点");
        }

        // ⭐ 转换可视化配置（如果有）
        if (yamlSop.Visualization != null)
        {
            Console.WriteLine($"[SOP] 加载了可视化配置");
        }

        return workflow;
        }
        catch (KeyNotFoundException knfEx)
        {
            Console.WriteLine($"[CONVERT ERROR] KeyNotFoundException during workflow conversion: {knfEx.Message}");
            Console.WriteLine($"[CONVERT ERROR] StackTrace: {knfEx.StackTrace}");
            throw new InvalidOperationException($"工作流转换失败 - 键未找到: {knfEx.Message}", knfEx);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CONVERT ERROR] Exception during workflow conversion: {ex.Message}");
            Console.WriteLine($"[CONVERT ERROR] StackTrace: {ex.StackTrace}");
            throw new InvalidOperationException($"工作流转换失败: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// 转换检测配置为条件
    /// </summary>
    private static StepCondition? ConvertDetectionToCondition(SopyamlDetection detection)
    {
        var condition = new StepCondition
        {
            MinConfidence = detection.MinConfidence,
            StableFrames = detection.StableFrames,
            Parameters = new Dictionary<string, object>()
        };

        switch (detection.Method.ToLower())
        {
            case "hand_in_region":
                condition.Type = ConditionType.HandInRegion;
                condition.TargetObject = "hand";
                condition.ZoneId = detection.Region ?? "";
                condition.Parameters["HandSide"] = detection.Hand ?? "right";
                break;

            case "hand_not_in_region":
                condition.Type = ConditionType.HandNotInRegion;
                condition.TargetObject = "hand";
                condition.ZoneId = detection.Region ?? "";
                condition.Parameters["HandSide"] = detection.Hand ?? "right";
                break;

            case "hand_stable":
                condition.Type = ConditionType.HandStable;
                condition.Parameters["HandSide"] = detection.Hand ?? "right";
                condition.Parameters["Tolerance"] = detection.Tolerance;
                break;

            case "hand_move":
                condition.Type = ConditionType.HandMoveFromTo;
                condition.Parameters["HandSide"] = detection.Hand ?? "right";
                condition.Parameters["FromRegion"] = detection.FromRegion ?? "";
                condition.Parameters["ToRegion"] = detection.ToRegion ?? "";
                break;

            case "hand_near_object":
                condition.Type = ConditionType.HandNearObject;
                condition.TargetObject = detection.TargetObject ?? "";
                condition.Parameters["HandSide"] = detection.Hand ?? "right";
                condition.Parameters["Margin"] = detection.Tolerance;
                break;

            case "object_present":
                condition.Type = ConditionType.ObjectPresent;
                condition.TargetObject = detection.TargetObject ?? "";
                break;

            case "object_in_zone":
                condition.Type = ConditionType.ObjectInZone;
                condition.TargetObject = detection.TargetObject ?? "";
                condition.ZoneId = detection.Region ?? "";
                break;

            case "pose_stable":
                condition.Type = ConditionType.ObjectStable;
                condition.Parameters["Tolerance"] = detection.Tolerance;
                break;

            case "time_elapsed":
                condition.Type = ConditionType.TimeElapsed;
                condition.Parameters["RequiredSeconds"] = detection.StableFrames; // 复用字段
                break;

            case "person_present":
                // 人在场：COCO 原生支持 "person" 类别，无需专用模型
                condition.Type = ConditionType.ObjectPresent;
                condition.TargetObject = detection.TargetObject ?? "person";
                break;

            case "hand_action":
            {
                // 基于手部"取/放"语义转换成 HandMoveFromTo：
                //  - pickup  ：手曾位于 FromRegion（如手机放置区）且现已离开，
                //              或检测到手正拿着 target_object 且该物体已离开 FromRegion
                //  - putdown ：手到达 ToRegion（如手机放置区）
                condition.Type = ConditionType.HandMoveFromTo;
                var action = (detection.Action ?? "").ToLowerInvariant();
                if (action == "pickup")
                {
                    condition.Parameters["FromRegion"] = detection.FromRegion ?? "";
                    condition.Parameters["ToRegion"] = "";
                    // target_object 辅助判定：手机等物品离开放置区即视为拿起
                    condition.TargetObject = detection.TargetObject ?? "";
                }
                else if (action == "putdown")
                {
                    condition.Parameters["FromRegion"] = "";
                    condition.Parameters["ToRegion"] = detection.ToRegion ?? "";
                    condition.TargetObject = detection.TargetObject ?? "";
                }
                else
                {
                    // 未指定/未知 action：退化为普通手移动（from/to 任一即可）
                    condition.Parameters["FromRegion"] = detection.FromRegion ?? "";
                    condition.Parameters["ToRegion"] = detection.ToRegion ?? "";
                    condition.TargetObject = detection.TargetObject ?? "";
                }
                condition.Parameters["HandSide"] = detection.Hand ?? "any";
                // 保存 action 到 Parameters，供 ConvertConditionToDetection 回读
                condition.Parameters["Action"] = action;
                break;
            }

            default:
                throw new InvalidOperationException(
                    $"不支持的检测方法 '{detection.Method}'。支持的方法: person_present, hand_action, hand_in_region, hand_not_in_region, hand_stable, hand_move, hand_near_object, object_present, object_in_zone, pose_stable, time_elapsed");
        }

        return condition;
    }

    /// <summary>
    /// 将SOPWorkflow保存为YAML
    /// </summary>
    public static void SaveToYaml(SOPWorkflow workflow, string yamlPath)
    {
        var yamlSop = ConvertToYamlSop(workflow);
        var config = new SOPYamlConfig { Sop = yamlSop };

        // 不使用全局命名约定，完全由 [YamlMember(Alias = "...")] 控制字段名，
        // 避免 CamelCase 把 from_region / stable_frames 等改成 fromRegion / stableFrames。
        var serializer = new SerializerBuilder()
            .Build();

        var yaml = serializer.Serialize(config);
        File.WriteAllText(yamlPath, yaml);
    }

    /// <summary>
    /// 转换为YAML格式
    /// </summary>
    private static SopyamlSop ConvertToYamlSop(SOPWorkflow workflow)
    {
        var yamlSop = new SopyamlSop
        {
            Name = workflow.Name,
            Version = workflow.Version,
            Steps = new List<SopyamlStep>(),
            Regions = new Dictionary<string, SopyamlRegion>()
        };

        // 转换区域
        foreach (var region in workflow.Regions)
        {
            yamlSop.Regions[region.ZoneId] = new SopyamlRegion
            {
                X1 = region.X,
                Y1 = region.Y,
                X2 = region.X + region.Width,
                Y2 = region.Y + region.Height,
                Name = region.Name
            };
        }

        // 转换步骤
        foreach (var step in workflow.Steps.OrderBy(s => s.Order))
        {
            var yamlStep = new SopyamlStep
            {
                Id = $"step_{step.StepId}",
                Name = step.StepName,
                Timeout = step.TimeoutSec,
                Description = step.Description,
                Transitions = step.NextSteps?.ToList() ?? new List<string>()
            };

            // 转换第一个条件为检测配置
            if (step.PassConditions.Count > 0)
            {
                yamlStep.Detection = ConvertConditionToDetection(step.PassConditions[0]);
            }

            // 转换漏放（MissingRequiredObject）违规规则 → required_objects
            var missingRule = step.ViolationRules
                .FirstOrDefault(r => r.Type == ViolationType.MissingRequiredObject);
            if (missingRule != null &&
                missingRule.Parameters.TryGetValue("RequiredObjects", out var reqObj) &&
                reqObj is System.Collections.IEnumerable reqEnum)
            {
                var reqList = new List<SopyamlRequiredObject>();
                foreach (var item in reqEnum)
                {
                    if (item is Dictionary<string, object> d)
                    {
                        var zoneVal = d.GetValueOrDefault("zone", "")?.ToString();
                        var mcVal = d.GetValueOrDefault("min_confidence", 0.5f);
                        float mc = mcVal is float f ? f : Convert.ToSingle(mcVal);
                        reqList.Add(new SopyamlRequiredObject
                        {
                            Object = d.GetValueOrDefault("class", "")?.ToString() ?? "",
                            Zone = string.IsNullOrEmpty(zoneVal) ? null : zoneVal,
                            MinConfidence = mc
                        });
                    }
                }
                if (reqList.Count > 0)
                {
                    yamlStep.RequiredObjects = reqList;
                }
            }

            yamlSop.Steps.Add(yamlStep);
        }

        return yamlSop;
    }

    /// <summary>
    /// 转换条件为检测配置
    /// </summary>
    private static SopyamlDetection ConvertConditionToDetection(StepCondition condition)
    {
        var detection = new SopyamlDetection
        {
            MinConfidence = condition.MinConfidence,
            StableFrames = condition.StableFrames
        };

        switch (condition.Type)
        {
            case ConditionType.ObjectPresent:
                detection.Method = "object_present";
                detection.TargetObject = condition.TargetObject;
                break;

            case ConditionType.ObjectInZone:
                if (condition.TargetObject.Equals("hand", StringComparison.OrdinalIgnoreCase)
                    && condition.Parameters.ContainsKey("HandSide"))
                {
                    detection.Method = "hand_in_region";
                    detection.Region = condition.ZoneId;
                    detection.Hand = condition.Parameters.GetValueOrDefault("HandSide", "right")?.ToString();
                }
                else
                {
                    detection.Method = "object_in_zone";
                    detection.TargetObject = condition.TargetObject;
                    detection.Region = condition.ZoneId;
                }
                break;

            case ConditionType.HandInRegion:
                detection.Method = "hand_in_region";
                detection.Region = condition.ZoneId;
                detection.Hand = condition.Parameters.GetValueOrDefault("HandSide", "right")?.ToString();
                break;

            case ConditionType.HandNotInRegion:
                detection.Method = "hand_not_in_region";
                detection.Region = condition.ZoneId;
                detection.Hand = condition.Parameters.GetValueOrDefault("HandSide", "right")?.ToString();
                break;

            case ConditionType.HandStable:
                detection.Method = "hand_stable";
                detection.Hand = condition.Parameters.GetValueOrDefault("HandSide", "right")?.ToString();
                detection.Tolerance = condition.Parameters.GetValueOrDefault("Tolerance", 20f) is float fh ? fh : 20f;
                break;

            case ConditionType.HandMoveFromTo:
            {
                var fromRegion = condition.Parameters.GetValueOrDefault("FromRegion", "")?.ToString() ?? "";
                var toRegion = condition.Parameters.GetValueOrDefault("ToRegion", "")?.ToString() ?? "";
                var action = condition.Parameters.GetValueOrDefault("Action", "")?.ToString() ?? "";
                detection.Hand = condition.Parameters.GetValueOrDefault("HandSide", "right")?.ToString();

                if (!string.IsNullOrEmpty(action))
                {
                    // hand_action（pickup/putdown）：保留 action / target_object 语义，
                    // 避免往返保存后退化成 hand_move 丢失 from_region/target_object
                    detection.Method = "hand_action";
                    detection.Action = action;
                    detection.FromRegion = fromRegion;
                    detection.ToRegion = toRegion;
                    detection.TargetObject = condition.TargetObject;
                }
                else
                {
                    // 普通 hand_move
                    detection.Method = "hand_move";
                    detection.FromRegion = fromRegion;
                    detection.ToRegion = toRegion;
                }
                break;
            }

            case ConditionType.HandNearObject:
                detection.Method = "hand_near_object";
                detection.TargetObject = condition.TargetObject;
                detection.Hand = condition.Parameters.GetValueOrDefault("HandSide", "right")?.ToString();
                detection.Tolerance = condition.Parameters.GetValueOrDefault("Margin", 30f) is float fm ? fm : 30f;
                break;

            case ConditionType.ObjectStable:
                detection.Method = "pose_stable";
                detection.Tolerance = condition.Parameters.GetValueOrDefault("Tolerance", 20f) is float f ? f : 20f;
                break;

            case ConditionType.TimeElapsed:
                detection.Method = "time_elapsed";
                break;

            default:
                detection.Method = "unknown";
                break;
        }

        return detection;
    }

    /// <summary>
    /// 仅更新 YAML 中的 regions 段（区域标定工具用），其余字段（steps / model / settings 等）原样保留。
    /// 采用完整 SOPYamlConfig 往返序列化；模型已覆盖 YAML 全部字段，避免信息丢失。
    /// </summary>
    public static void SaveRegions(string yamlPath, Dictionary<string, SopyamlRegion> regions)
    {
        SOPYamlConfig config;
        if (File.Exists(yamlPath))
        {
            var yaml = File.ReadAllText(yamlPath);
            // 不使用全局命名约定，完全由 [YamlMember(Alias = "...")] 控制字段名，
            // 与 ParseYaml 和序列化器保持一致，避免 snake_case 字段在往返中丢失。
            var deserializer = new DeserializerBuilder()
                .IgnoreUnmatchedProperties()
                .Build();
            config = deserializer.Deserialize<SOPYamlConfig>(yaml) ?? new SOPYamlConfig();
        }
        else
        {
            config = new SOPYamlConfig();
        }

        config.Sop ??= new SopyamlSop();
        config.Sop.Regions = regions;

        // 不使用全局命名约定，完全由 [YamlMember(Alias = "...")] 控制字段名。
        var serializer = new SerializerBuilder()
            .Build();
        File.WriteAllText(yamlPath, serializer.Serialize(config));
    }
}
