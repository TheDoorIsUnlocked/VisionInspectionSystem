using System.Text.Json;
using VisionInspection.Modules.SOP.Models;

namespace VisionInspection.Modules.SOP.Services;

/// <summary>
/// SOP 工作流管理器
/// </summary>
public class SOPWorkflowManager
{
    private readonly string _configDirectory;
    private readonly List<SOPWorkflow> _workflows = new();
    private readonly JsonSerializerOptions _jsonOptions;

    public IReadOnlyList<SOPWorkflow> Workflows => _workflows.AsReadOnly();

    public event EventHandler<SOPWorkflowEventArgs>? WorkflowAdded;
    public event EventHandler<SOPWorkflowEventArgs>? WorkflowUpdated;
    public event EventHandler<SOPWorkflowEventArgs>? WorkflowDeleted;

    public SOPWorkflowManager(string configDirectory = "configs/sop")
    {
        _configDirectory = configDirectory;
        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        Directory.CreateDirectory(_configDirectory);
        LoadAllWorkflows();
    }

    /// <summary>
    /// 加载所有工作流
    /// </summary>
    public void LoadAllWorkflows()
    {
        _workflows.Clear();

        if (!Directory.Exists(_configDirectory))
            return;

        var files = Directory.GetFiles(_configDirectory, "*.json");
        foreach (var file in files)
        {
            try
            {
                var json = File.ReadAllText(file);
                var workflow = JsonSerializer.Deserialize<SOPWorkflow>(json, _jsonOptions);
                if (workflow != null)
                {
                    _workflows.Add(workflow);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"加载SOP工作流失败 {file}: {ex.Message}");
            }
        }

        // 如果没有工作流，创建示例
        if (_workflows.Count == 0)
        {
            var demoWorkflow = CreateDemoWorkflow();
            SaveWorkflow(demoWorkflow);
            _workflows.Add(demoWorkflow);
        }
    }

    /// <summary>
    /// 保存工作流
    /// </summary>
    public void SaveWorkflow(SOPWorkflow workflow)
    {
        workflow.UpdatedAt = DateTime.Now;
        var fileName = $"sop_{workflow.Id}.json";
        var filePath = Path.Combine(_configDirectory, fileName);

        var json = JsonSerializer.Serialize(workflow, _jsonOptions);
        File.WriteAllText(filePath, json);

        var existing = _workflows.FirstOrDefault(w => w.Id == workflow.Id);
        if (existing != null)
        {
            _workflows.Remove(existing);
            _workflows.Add(workflow);
            WorkflowUpdated?.Invoke(this, new SOPWorkflowEventArgs(workflow));
        }
        else
        {
            _workflows.Add(workflow);
            WorkflowAdded?.Invoke(this, new SOPWorkflowEventArgs(workflow));
        }
    }

    /// <summary>
    /// 删除工作流
    /// </summary>
    public bool DeleteWorkflow(string workflowId)
    {
        var workflow = _workflows.FirstOrDefault(w => w.Id == workflowId);
        if (workflow == null)
            return false;

        var fileName = $"sop_{workflowId}.json";
        var filePath = Path.Combine(_configDirectory, fileName);

        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }

        _workflows.Remove(workflow);
        WorkflowDeleted?.Invoke(this, new SOPWorkflowEventArgs(workflow));
        return true;
    }

    /// <summary>
    /// 获取工作流
    /// </summary>
    public SOPWorkflow? GetWorkflow(string workflowId)
    {
        return _workflows.FirstOrDefault(w => w.Id == workflowId);
    }

    /// <summary>
    /// 创建工作流
    /// </summary>
    public SOPWorkflow CreateWorkflow(string name, string description = "")
    {
        var workflow = new SOPWorkflow
        {
            Name = name,
            Description = description,
            Steps = new List<SOPStep>(),
            Settings = new SOPGlobalSettings()
        };

        SaveWorkflow(workflow);
        return workflow;
    }

    /// <summary>
    /// 添加步骤
    /// </summary>
    public void AddStep(string workflowId, SOPStep step)
    {
        var workflow = GetWorkflow(workflowId);
        if (workflow == null)
            throw new ArgumentException($"工作流不存在: {workflowId}");

        // 自动分配步骤ID
        if (step.StepId == 0)
        {
            step.StepId = workflow.Steps.Count > 0 ? workflow.Steps.Max(s => s.StepId) + 1 : 1;
        }

        workflow.Steps.Add(step);
        workflow.Steps = workflow.Steps.OrderBy(s => s.StepId).ToList();
        SaveWorkflow(workflow);
    }

    /// <summary>
    /// 更新步骤
    /// </summary>
    public void UpdateStep(string workflowId, SOPStep updatedStep)
    {
        var workflow = GetWorkflow(workflowId);
        if (workflow == null)
            throw new ArgumentException($"工作流不存在: {workflowId}");

        var existingStep = workflow.Steps.FirstOrDefault(s => s.StepId == updatedStep.StepId);
        if (existingStep == null)
            throw new ArgumentException($"步骤不存在: {updatedStep.StepId}");

        workflow.Steps.Remove(existingStep);
        workflow.Steps.Add(updatedStep);
        workflow.Steps = workflow.Steps.OrderBy(s => s.StepId).ToList();
        SaveWorkflow(workflow);
    }

    /// <summary>
    /// 删除步骤
    /// </summary>
    public bool DeleteStep(string workflowId, int stepId)
    {
        var workflow = GetWorkflow(workflowId);
        if (workflow == null)
            return false;

        var step = workflow.Steps.FirstOrDefault(s => s.StepId == stepId);
        if (step == null)
            return false;

        workflow.Steps.Remove(step);
        SaveWorkflow(workflow);
        return true;
    }

    /// <summary>
    /// 导出工作流到文件
    /// </summary>
    public void ExportWorkflow(string workflowId, string exportPath)
    {
        var workflow = GetWorkflow(workflowId);
        if (workflow == null)
            throw new ArgumentException($"工作流不存在: {workflowId}");

        var json = JsonSerializer.Serialize(workflow, _jsonOptions);
        File.WriteAllText(exportPath, json);
    }

    /// <summary>
    /// 从文件导入工作流
    /// </summary>
    public SOPWorkflow ImportWorkflow(string importPath)
    {
        var json = File.ReadAllText(importPath);
        var workflow = JsonSerializer.Deserialize<SOPWorkflow>(json, _jsonOptions);
        if (workflow == null)
            throw new InvalidOperationException("导入失败：无效的JSON格式");

        workflow.Id = Guid.NewGuid().ToString();
        workflow.Name = $"{workflow.Name}_导入";
        workflow.CreatedAt = DateTime.Now;
        workflow.UpdatedAt = DateTime.Now;

        SaveWorkflow(workflow);
        return workflow;
    }

    /// <summary>
    /// 创建示例工作流（装配流程）
    /// </summary>
    public static SOPWorkflow CreateDemoWorkflow()
    {
        return new SOPWorkflow
        {
            Id = Guid.NewGuid().ToString(),
            Name = "转向器装配SOP",
            Description = "汽车零部件转向器装配标准作业流程",
            Version = "1.0.0",
            CreatedAt = DateTime.Now,
            UpdatedAt = DateTime.Now,
            Settings = new SOPGlobalSettings
            {
                EnableSkipDetection = true,
                EnableTimeoutDetection = true,
                StableFrameCount = 5,
                PositionTolerance = 20f,
                AutoResetOnComplete = false
            },
            Steps = new List<SOPStep>
            {
                new()
                {
                    StepId = 1,
                    StepName = "取壳体",
                    Description = "从料盒取转向器壳体",
                    ExpectedDurationSec = 3,
                    TimeoutSec = 10,
                    PassConditions = new List<StepCondition>
                    {
                        new()
                        {
                            Type = ConditionType.ObjectPresent,
                            TargetObject = "shell",
                            MinConfidence = 0.7f,
                            StableFrames = 3
                        }
                    },
                    ViolationRules = new List<ViolationRule>()
                },
                new()
                {
                    StepId = 2,
                    StepName = "放入夹具",
                    Description = "将壳体放入夹具定位",
                    ExpectedDurationSec = 5,
                    TimeoutSec = 15,
                    PassConditions = new List<StepCondition>
                    {
                        new()
                        {
                            Type = ConditionType.ObjectInZone,
                            TargetObject = "shell",
                            ZoneId = "fixture_zone",
                            MinConfidence = 0.7f,
                            StableFrames = 5
                        }
                    },
                    ViolationRules = new List<ViolationRule>
                    {
                        new()
                        {
                            Type = ViolationType.ObjectRemoved,
                            Description = "壳体被移除",
                            Parameters = new Dictionary<string, object>
                            {
                                ["MustKeepClasses"] = new List<string> { "shell" }
                            }
                        }
                    },
                    RequiredPreviousSteps = new List<int> { 1 }
                },
                new()
                {
                    StepId = 3,
                    StepName = "取螺栓",
                    Description = "从螺栓盒取2颗螺栓",
                    ExpectedDurationSec = 8,
                    TimeoutSec = 20,
                    PassConditions = new List<StepCondition>
                    {
                        new()
                        {
                            Type = ConditionType.ObjectPresent,
                            TargetObject = "bolt",
                            MinConfidence = 0.7f,
                            StableFrames = 3
                        }
                    },
                    ViolationRules = new List<ViolationRule>(),
                    RequiredPreviousSteps = new List<int> { 2 }
                },
                new()
                {
                    StepId = 4,
                    StepName = "穿入螺栓",
                    Description = "将螺栓穿入壳体孔位",
                    ExpectedDurationSec = 10,
                    TimeoutSec = 25,
                    PassConditions = new List<StepCondition>
                    {
                        new()
                        {
                            Type = ConditionType.ObjectInZone,
                            TargetObject = "bolt",
                            ZoneId = "shell_hole_zone",
                            MinConfidence = 0.7f,
                            StableFrames = 5
                        }
                    },
                    ViolationRules = new List<ViolationRule>(),
                    RequiredPreviousSteps = new List<int> { 3 }
                },
                new()
                {
                    StepId = 5,
                    StepName = "扭矩扳手拧紧",
                    Description = "用扭矩扳手拧紧螺栓",
                    ExpectedDurationSec = 10,
                    TimeoutSec = 20,
                    PassConditions = new List<StepCondition>
                    {
                        new()
                        {
                            Type = ConditionType.ObjectPresent,
                            TargetObject = "torque_wrench",
                            MinConfidence = 0.7f,
                            StableFrames = 5
                        }
                    },
                    ViolationRules = new List<ViolationRule>(),
                    RequiredPreviousSteps = new List<int> { 4 }
                },
                new()
                {
                    StepId = 6,
                    StepName = "扭矩值确认",
                    Description = "查看扭矩扳手显示值确认在范围内",
                    ExpectedDurationSec = 3,
                    TimeoutSec = 10,
                    PassConditions = new List<StepCondition>
                    {
                        new()
                        {
                            Type = ConditionType.TimeElapsed,
                            Parameters = new Dictionary<string, object>
                            {
                                ["RequiredSeconds"] = 2.0
                            }
                        }
                    },
                    ViolationRules = new List<ViolationRule>
                    {
                        new()
                        {
                            Type = ViolationType.SkipStep,
                            Description = "跳过扭矩确认",
                            Severity = 3
                        }
                    },
                    RequiredPreviousSteps = new List<int> { 5 }
                },
                new()
                {
                    StepId = 7,
                    StepName = "放行",
                    Description = "将工件推入滑道放行",
                    ExpectedDurationSec = 3,
                    TimeoutSec = 10,
                    PassConditions = new List<StepCondition>
                    {
                        new()
                        {
                            Type = ConditionType.ObjectAbsent,
                            TargetObject = "shell",
                            MinConfidence = 0.5f
                        }
                    },
                    ViolationRules = new List<ViolationRule>(),
                    RequiredPreviousSteps = new List<int> { 6 }
                }
            }
        };
    }

    /// <summary>
    /// 获取可用的条件类型列表
    /// </summary>
    public static List<(ConditionType Type, string Name, string Description)> GetConditionTypes()
    {
        return new List<(ConditionType, string, string)>
        {
            (ConditionType.ObjectPresent, "目标存在", "检测到指定目标存在"),
            (ConditionType.ObjectInZone, "目标在区域内", "目标在指定区域内"),
            (ConditionType.ObjectStable, "目标稳定", "目标位置保持稳定"),
            (ConditionType.ObjectAbsent, "目标不存在", "指定目标不存在"),
            (ConditionType.TimeElapsed, "时间满足", "满足指定时间"),
            (ConditionType.SequenceComplete, "序列完成", "前置序列已完成")
        };
    }

    /// <summary>
    /// 获取可用的违规类型列表
    /// </summary>
    public static List<(ViolationType Type, string Name, string Description)> GetViolationTypes()
    {
        return new List<(ViolationType, string, string)>
        {
            (ViolationType.Timeout, "超时", "步骤执行超时"),
            (ViolationType.SkipStep, "跳步", "跳过必要步骤"),
            (ViolationType.WrongOrder, "顺序错误", "步骤顺序不正确"),
            (ViolationType.ForbiddenObject, "禁止对象", "出现禁止的对象"),
            (ViolationType.ObjectRemoved, "对象被移除", "必须保持的对象被移除"),
            (ViolationType.ZoneIntrusion, "区域入侵", "进入禁区")
        };
    }
}

public class SOPWorkflowEventArgs : EventArgs
{
    public SOPWorkflow Workflow { get; }

    public SOPWorkflowEventArgs(SOPWorkflow workflow)
    {
        Workflow = workflow;
    }
}
