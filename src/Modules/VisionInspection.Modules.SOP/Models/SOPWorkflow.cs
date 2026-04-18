namespace VisionInspection.Modules.SOP.Models;

/// <summary>
/// SOP 流程定义（一个完整的作业指导书）
/// </summary>
public class SOPWorkflow
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Version { get; set; } = "1.0.0";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
    public List<SOPStep> Steps { get; set; } = new();
    public SOPGlobalSettings Settings { get; set; } = new();
    public SOPGlobalSettings GlobalSettings { get; set; } = new();
    public List<ZoneDefinition> Regions { get; set; } = new();
}

/// <summary>
/// SOP 步骤定义
/// </summary>
public class SOPStep
{
    public int StepId { get; set; }
    public string StepName { get; set; } = "";
    public string Description { get; set; } = "";
    public int ExpectedDurationSec { get; set; } = 10;
    public int TimeoutSec { get; set; } = 30;
    public int TimeoutSeconds { get; set; } = 30;
    public int Order { get; set; }
    public List<StepCondition> PassConditions { get; set; } = new();
    public List<ViolationRule> ViolationRules { get; set; } = new();
    public List<int> RequiredPreviousSteps { get; set; } = new();
    public List<string> NextSteps { get; set; } = new();
}

/// <summary>
/// 步骤通过条件
/// </summary>
public class StepCondition
{
    public ConditionType Type { get; set; }
    public string TargetObject { get; set; } = "";
    public string? ZoneId { get; set; }
    public float MinConfidence { get; set; } = 0.7f;
    public int StableFrames { get; set; } = 5;
    public Dictionary<string, object> Parameters { get; set; } = new();
}

public enum ConditionType
{
    ObjectPresent,
    ObjectInZone,
    ObjectStable,
    ObjectAbsent,
    SequenceComplete,
    TimeElapsed,
    Custom
}

/// <summary>
/// 违规规则
/// </summary>
public class ViolationRule
{
    public ViolationType Type { get; set; }
    public string Description { get; set; } = "";
    public int Severity { get; set; } = 1;
    public Dictionary<string, object> Parameters { get; set; } = new();
}

public enum ViolationType
{
    SkipStep,
    WrongOrder,
    Timeout,
    ForbiddenObject,
    ObjectRemoved,
    ZoneIntrusion,
    Custom
}

/// <summary>
/// SOP 全局设置
/// </summary>
public class SOPGlobalSettings
{
    public bool EnableSkipDetection { get; set; } = true;
    public bool EnableTimeoutDetection { get; set; } = true;
    public int StableFrameCount { get; set; } = 5;
    public float PositionTolerance { get; set; } = 20f;
    public bool AutoResetOnComplete { get; set; } = false;
    public int ResetDelaySec { get; set; } = 5;
}

/// <summary>
/// 区域定义
/// </summary>
public class ZoneDefinition
{
    public string ZoneId { get; set; } = "";
    public string Name { get; set; } = "";
    public float X { get; set; }
    public float Y { get; set; }
    public float Width { get; set; }
    public float Height { get; set; }
}
