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
        var deserializer = new DeserializerBuilder()
            .Build();

        var config = deserializer.Deserialize<SOPYamlConfig>(yaml);

        if (config?.Sop == null)
        {
            throw new InvalidOperationException("YAML配置格式错误：缺少'sop'根节点");
        }

        return ConvertToWorkflow(config.Sop, sourcePath);
    }

    /// <summary>
    /// 转换为SOPWorkflow
    /// </summary>
    private static SOPWorkflow ConvertToWorkflow(SopyamlSop yamlSop, string sourcePath)
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
            var step = new SOPStep
            {
                StepId = stepOrder,
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

            workflow.Steps.Add(step);
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

        return workflow;
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
                condition.Type = ConditionType.ObjectPresent;
                condition.Parameters["RegionId"] = detection.Region ?? "";
                condition.Parameters["HandSide"] = detection.Hand ?? "right";
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

            default:
                return null;
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

        var serializer = new SerializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
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
                if (condition.Parameters.ContainsKey("RegionId"))
                {
                    detection.Method = "hand_in_region";
                    detection.Region = condition.Parameters.GetValueOrDefault("RegionId", "")?.ToString();
                    detection.Hand = condition.Parameters.GetValueOrDefault("HandSide", "right")?.ToString();
                }
                else
                {
                    detection.Method = "object_present";
                    detection.TargetObject = condition.TargetObject;
                }
                break;

            case ConditionType.ObjectInZone:
                detection.Method = "object_in_zone";
                detection.TargetObject = condition.TargetObject;
                detection.Region = condition.ZoneId;
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
}
