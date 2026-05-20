# SOP检测系统 Bug 修复指令（喂给 Trae）

> **项目路径**: `E:\yolo\YoloDotNet-master\VisionInspectionSystem`
> **生成日期**: 2026-05-14
> **说明**: 以下是 5 个 bug 的精确修复指令，按优先级排序，请按顺序修复。
> **约束**: 修改时保持现有的命名空间、using 引用不变，只改指定的代码段。

---

## 修复1（P0）：删除重复的 ZoneDefinition 类

**问题**: `ZoneDefinition` 在两个文件中重复定义，编译会报歧义错误。

### 文件: `src\Modules\VisionInspection.Modules.SOP\Services\StepConditionEvaluator.cs`

**操作**: 删除该文件底部的 `ZoneDefinition` 类定义（大约最后 10 行），因为 `SOPWorkflow.cs` 中已经有同名类。

删除这段代码：
```csharp
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
```

### 文件: `src\Modules\VisionInspection.Modules.SOP\Services\StepConditionEvaluator.cs`

**操作**: 在文件顶部添加 using，确保引用正确的 ZoneDefinition：
```csharp
using VisionInspection.Modules.SOP.Models;  // 已有的话就不用加
```

---

## 修复2（P0）：区域定义从 mock 改为真实数据注入

**问题**: `StepConditionEvaluator` 和 `ViolationDetector` 中的 `GetZoneDefinition()` 方法永远返回硬编码的固定坐标 (0.2, 0.2, 0.3, 0.3)，导致 `object_in_zone` 条件类型完全失效。

### 文件1: `src\Modules\VisionInspection.Modules.SOP\Services\StepConditionEvaluator.cs`

**修改构造函数**，注入区域列表：

当前代码：
```csharp
public class StepConditionEvaluator
{
    private readonly SOPStateMachine _stateMachine;

    public StepConditionEvaluator(SOPStateMachine stateMachine)
    {
        _stateMachine = stateMachine;
    }
```

替换为：
```csharp
public class StepConditionEvaluator
{
    private readonly SOPStateMachine _stateMachine;
    private readonly Dictionary<string, ZoneDefinition> _zones;

    public StepConditionEvaluator(SOPStateMachine stateMachine, IReadOnlyList<ZoneDefinition>? zones = null)
    {
        _stateMachine = stateMachine;
        _zones = (zones ?? new List<ZoneDefinition>()).ToDictionary(z => z.ZoneId, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 动态更新区域定义（用于热切换配置）
    /// </summary>
    public void UpdateZones(IReadOnlyList<ZoneDefinition> zones)
    {
        _zones.Clear();
        foreach (var zone in zones)
        {
            _zones[zone.ZoneId] = zone;
        }
    }
```

**修改 GetZoneDefinition 方法**：

当前代码：
```csharp
    private ZoneDefinition? GetZoneDefinition(string zoneId)
    {
        // TODO: 从配置中读取区域定义
        // 这里返回模拟数据
        return new ZoneDefinition
        {
            ZoneId = zoneId,
            Name = zoneId,
            X = 0.2f,
            Y = 0.2f,
            Width = 0.3f,
            Height = 0.3f
        };
    }
```

替换为：
```csharp
    private ZoneDefinition? GetZoneDefinition(string zoneId)
    {
        if (_zones.TryGetValue(zoneId, out var zone))
        {
            return zone;
        }

        Console.WriteLine($"[SOP] 警告: 未找到区域定义 '{zoneId}'，请检查 YAML 配置中的 regions 字段");
        return null;
    }
```

### 文件2: `src\Modules\VisionInspection.Modules.SOP\Services\ViolationDetector.cs`

**同样修改构造函数和 GetZoneDefinition**：

当前代码：
```csharp
public class ViolationDetector
{
    private readonly SOPStateMachine _stateMachine;
    private readonly Dictionary<string, DateTime> _violationCooldown = new();

    public ViolationDetector(SOPStateMachine stateMachine)
    {
        _stateMachine = stateMachine;
    }
```

替换为：
```csharp
public class ViolationDetector
{
    private readonly SOPStateMachine _stateMachine;
    private readonly Dictionary<string, DateTime> _violationCooldown = new();
    private readonly Dictionary<string, ZoneDefinition> _zones;

    public ViolationDetector(SOPStateMachine stateMachine, IReadOnlyList<ZoneDefinition>? zones = null)
    {
        _stateMachine = stateMachine;
        _zones = (zones ?? new List<ZoneDefinition>()).ToDictionary(z => z.ZoneId, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 动态更新区域定义
    /// </summary>
    public void UpdateZones(IReadOnlyList<ZoneDefinition> zones)
    {
        _zones.Clear();
        foreach (var zone in zones)
        {
            _zones[zone.ZoneId] = zone;
        }
    }
```

**修改 ViolationDetector 底部的 GetZoneDefinition**：

当前代码：
```csharp
    private ZoneDefinition? GetZoneDefinition(string zoneId)
    {
        return new ZoneDefinition
        {
            ZoneId = zoneId,
            Name = zoneId,
            X = 0.2f,
            Y = 0.2f,
            Width = 0.3f,
            Height = 0.3f
        };
    }
```

替换为：
```csharp
    private ZoneDefinition? GetZoneDefinition(string zoneId)
    {
        if (_zones.TryGetValue(zoneId, out var zone))
        {
            return zone;
        }

        Console.WriteLine($"[SOP] 警告: 违规检测器未找到区域定义 '{zoneId}'");
        return null;
    }
```

### 文件3: `src\Modules\VisionInspection.Modules.SOP\Models\SOPStateMachine.cs`

**修改构造函数**，将区域列表传递给内部的 evaluator 和 detector：

当前代码：
```csharp
    public SOPStateMachine()
    {
        _conditionEvaluator = new StepConditionEvaluator(this);
        _violationDetector = new ViolationDetector(this);
    }
```

替换为：
```csharp
    public SOPStateMachine(IReadOnlyList<ZoneDefinition>? zones = null)
    {
        _conditionEvaluator = new StepConditionEvaluator(this, zones);
        _violationDetector = new ViolationDetector(this, zones);
    }

    /// <summary>
    /// 更新区域定义（配置热切换时调用）
    /// </summary>
    public void UpdateZones(IReadOnlyList<ZoneDefinition> zones)
    {
        _conditionEvaluator.UpdateZones(zones);
        _violationDetector.UpdateZones(zones);
    }
```

### 文件4: `src\Modules\VisionInspection.Modules.SOP\SOPModule.cs`

**修改状态机初始化**，传入区域：

找到这行（在 InitializeAsync 方法中）：
```csharp
            // 初始化状态机
            _stateMachine = new SOPStateMachine();
```

替换为：
```csharp
            // 初始化状态机（区域列表在 StartWorkflow 时注入）
            _stateMachine = new SOPStateMachine();
```

**修改 StartWorkflow 方法**，加载工作流时同步注入区域：

当前代码：
```csharp
    public void StartWorkflow(SOPWorkflow workflow)
    {
        if (_stateMachine == null)
            throw new InvalidOperationException("状态机未初始化");

        _currentWorkflow = workflow;
        _stateMachine.Start(workflow);
    }
```

替换为：
```csharp
    public void StartWorkflow(SOPWorkflow workflow)
    {
        if (_stateMachine == null)
            throw new InvalidOperationException("状态机未初始化");

        _currentWorkflow = workflow;

        // 将工作流中的区域定义注入状态机
        if (workflow.Regions != null && workflow.Regions.Count > 0)
        {
            _stateMachine.UpdateZones(workflow.Regions);
            Console.WriteLine($"[SOP] 已加载 {workflow.Regions.Count} 个区域定义");
        }
        else
        {
            Console.WriteLine("[SOP] 警告: 工作流中没有区域定义，object_in_zone 条件将无法工作");
        }

        _stateMachine.Start(workflow);
    }
```

---

## 修复3（P0）：对象跟踪算法从 Hash 匹配改为 IOU 匹配

**问题**: `BoundingBox.GetHashCode()` 每帧产生不同值 → 同一物体被当成新对象 → `IsStable()` 永远 false → 内存泄漏。

### 文件: `src\Modules\VisionInspection.Modules.SOP\Models\SOPStateMachine.cs`

**替换 UpdateTrackedObjects 方法**：

当前代码：
```csharp
    private void UpdateTrackedObjects(List<ObjectDetection> detections, DateTime timestamp)
    {
        foreach (var detection in detections)
        {
            var labelName = detection.Label?.Name ?? "unknown";
            var key = $"{labelName}_{detection.BoundingBox.GetHashCode()}";
            if (_trackedObjects.TryGetValue(key, out var tracked))
            {
                tracked.Update(detection.BoundingBox, timestamp);
            }
            else
            {
                _trackedObjects[key] = new TrackedObject(labelName, detection.BoundingBox, timestamp);
            }
        }

        // 清理过期跟踪
        var expired = _trackedObjects.Where(kv => (timestamp - kv.Value.LastUpdate).TotalSeconds > 2).Select(kv => kv.Key).ToList();
        foreach (var key in expired)
        {
            _trackedObjects.Remove(key);
        }
    }
```

替换为：
```csharp
    private int _nextTrackId = 0;

    private void UpdateTrackedObjects(List<ObjectDetection> detections, DateTime timestamp)
    {
        var matchedKeys = new HashSet<string>();
        var matchedDetections = new HashSet<int>();

        // 第一轮：用 IOU 匹配已有跟踪对象
        for (int i = 0; i < detections.Count; i++)
        {
            var detection = detections[i];
            var labelName = detection.Label?.Name ?? "unknown";

            string? bestKey = null;
            float bestIOU = 0.3f; // IOU 阈值，低于此值视为新对象

            foreach (var (key, tracked) in _trackedObjects)
            {
                if (matchedKeys.Contains(key)) continue;
                if (!tracked.ClassName.Equals(labelName, StringComparison.OrdinalIgnoreCase)) continue;

                float iou = CalculateIOU(tracked.BoundingBox, detection.BoundingBox);
                if (iou > bestIOU)
                {
                    bestIOU = iou;
                    bestKey = key;
                }
            }

            if (bestKey != null)
            {
                // 匹配成功，更新位置
                _trackedObjects[bestKey].Update(detection.BoundingBox, timestamp);
                matchedKeys.Add(bestKey);
                matchedDetections.Add(i);
            }
        }

        // 第二轮：未匹配的检测结果创建新跟踪
        for (int i = 0; i < detections.Count; i++)
        {
            if (matchedDetections.Contains(i)) continue;

            var detection = detections[i];
            var labelName = detection.Label?.Name ?? "unknown";
            var newKey = $"{labelName}_{_nextTrackId++}";
            _trackedObjects[newKey] = new TrackedObject(labelName, detection.BoundingBox, timestamp);
        }

        // 清理过期跟踪（2秒未更新的对象视为离开画面）
        var expired = _trackedObjects
            .Where(kv => (timestamp - kv.Value.LastUpdate).TotalSeconds > 2)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var key in expired)
        {
            _trackedObjects.Remove(key);
        }
    }

    /// <summary>
    /// 计算两个矩形的 IOU（交并比）
    /// </summary>
    private static float CalculateIOU(SKRect a, SKRect b)
    {
        float intersectLeft = Math.Max(a.Left, b.Left);
        float intersectTop = Math.Max(a.Top, b.Top);
        float intersectRight = Math.Min(a.Right, b.Right);
        float intersectBottom = Math.Min(a.Bottom, b.Bottom);

        if (intersectRight <= intersectLeft || intersectBottom <= intersectTop)
            return 0f;

        float intersectArea = (intersectRight - intersectLeft) * (intersectBottom - intersectTop);
        float areaA = a.Width * a.Height;
        float areaB = b.Width * b.Height;
        float unionArea = areaA + areaB - intersectArea;

        return unionArea > 0 ? intersectArea / unionArea : 0f;
    }
```

---

## 修复4（P1）：统一重复属性

**问题**: `SOPWorkflow` 有 `Settings` 和 `GlobalSettings` 两个同类型属性；`SOPStep` 有 `TimeoutSec` 和 `TimeoutSeconds` 两个同含义属性。配置绑定时容易混乱。

### 文件: `src\Modules\VisionInspection.Modules.SOP\Models\SOPWorkflow.cs`

**修改 SOPWorkflow 类**：

当前代码：
```csharp
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
```

替换为：
```csharp
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
    public List<ZoneDefinition> Regions { get; set; } = new();
}
```

**注意**: 删除 `GlobalSettings` 后，需要全局搜索 `GlobalSettings` 并替换为 `Settings`。
涉及的文件（搜索 `GlobalSettings` 关键字）：
- `SOPYamlConfig.cs` 中的 `ConvertToWorkflow` 方法（约第 240 行）
- `SOPWorkflowManager.cs` 中的 `CreateDemoWorkflow` 方法
- 其他引用 `workflow.GlobalSettings` 的地方

**修改 SOPStep 类**：

当前代码：
```csharp
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
```

替换为：
```csharp
public class SOPStep
{
    public int StepId { get; set; }
    public string StepName { get; set; } = "";
    public string Description { get; set; } = "";
    public int ExpectedDurationSec { get; set; } = 10;
    public int TimeoutSec { get; set; } = 30;
    public int Order { get; set; }
    public List<StepCondition> PassConditions { get; set; } = new();
    public List<ViolationRule> ViolationRules { get; set; } = new();
    public List<int> RequiredPreviousSteps { get; set; } = new();
    public List<string> NextSteps { get; set; } = new();
}
```

**注意**: 删除 `TimeoutSeconds` 后，需要全局搜索 `TimeoutSeconds` 并替换为 `TimeoutSec`。
涉及的文件：
- `SOPYamlConfig.cs` 中 `ConvertToWorkflow` → `step.TimeoutSeconds = yamlStep.Timeout` 改为 `step.TimeoutSec = yamlStep.Timeout`
- `SOPWorkflowManager.cs` 中 demo workflow 如果用了 `TimeoutSeconds`
- `ViolationDetector.cs` 中 `step.TimeoutSec`（确认用的是哪个）

---

## 修复5（P1）：YAML 配置中的 forbidden_objects 和 must_keep 转换

**问题**: 当前 YAML → C# 的转换（`SOPYamlConverter.ConvertToWorkflow`）只转换了 `detection` → `PassConditions`，没有转换 `forbidden_objects` 和 `must_keep` 到 `ViolationRules`。

### 文件: `src\Modules\VisionInspection.Modules.SOP\Models\SOPYamlConfig.cs`

**步骤1: 在 SopyamlStep 类中添加缺失的 YAML 字段**：

当前代码：
```csharp
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
}
```

替换为：
```csharp
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
```

**步骤2: 修改 ConvertToWorkflow 方法中的步骤转换逻辑**：

找到 `ConvertToWorkflow` 方法中转换步骤的 foreach 循环，当前代码大约是：

```csharp
            // 转换检测条件
            if (yamlStep.Detection != null)
            {
                var condition = ConvertDetectionToCondition(yamlStep.Detection);
                if (condition != null)
                {
                    step.PassConditions.Add(condition);
                }
            }

            workflow.Steps.Add(step);
```

替换为：
```csharp
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
```

---

## 修复后验证清单

修复完所有 5 个问题后，按以下步骤验证：

### 1. 编译测试
```bash
cd E:\yolo\YoloDotNet-master\VisionInspectionSystem
dotnet build
```
应该 0 errors。如果有 `ZoneDefinition` 歧义错误，说明修复1没做干净——检查是否还有其他文件定义了同名类。

### 2. 全局搜索验证
```
搜索 "GlobalSettings" → 应该全部替换为 "Settings"（或不存在）
搜索 "TimeoutSeconds" → 应该全部替换为 "TimeoutSec"（或不存在）
搜索 "GetHashCode()" → SOPStateMachine.cs 中不应再有（已改为 IOU）
搜索 "0.2f, 0.2f, 0.3f, 0.3f" → 不应存在（mock 数据已删）
```

### 3. 功能测试
用这个 YAML 测试（包含区域+禁止对象+保持检测）：

```yaml
sop:
  name: "修复验证测试"
  version: "1.0"
  
  regions:
    fixture_zone:
      x1: 300
      y1: 250
      x2: 500
      y2: 450
      name: "夹具区域"

  steps:
    - id: "step_1"
      name: "放壳体"
      timeout: 30
      transitions: ["step_2"]
      detection:
        method: "object_in_zone"
        target_object: "shell"
        region: "fixture_zone"
        min_confidence: 0.6
        stable_frames: 5

    - id: "step_2"
      name: "拧螺丝"
      timeout: 40
      transitions: ["step_3"]
      detection:
        method: "object_present"
        target_object: "screwdriver"
        min_confidence: 0.6
        stable_frames: 5
      forbidden_objects:
        - "torque_wrench"
      must_keep:
        - "shell"

    - id: "step_3"
      name: "完成"
      timeout: 10
      transitions: []
      detection:
        method: "time_elapsed"
        stable_frames: 2
```

验证要点：
- step_1：应该在 fixture_zone 区域 (300,250)-(500,450) 内检测 shell，而不是固定的 (0.2, 0.2)
- step_2：如果画面中出现 torque_wrench 应触发 ForbiddenObject 违规；如果 shell 消失应触发 ObjectRemoved 违规
- 对象跟踪：同一个 shell 在连续帧中应被识别为同一对象，FrameCount 应递增

---

## 关联文件清单（改动涉及的所有文件）

| 文件 | 改动类型 | 说明 |
|------|---------|------|
| `Models\SOPStateMachine.cs` | 重写方法+新增方法 | IOU 跟踪 + 区域注入 |
| `Models\SOPWorkflow.cs` | 删属性 | 删 GlobalSettings、TimeoutSeconds |
| `Models\SOPYamlConfig.cs` | 加字段+改转换 | forbidden_objects/must_keep 字段+转换逻辑 |
| `Services\StepConditionEvaluator.cs` | 改构造+删类+改方法 | 区域注入+删重复 ZoneDefinition+改 GetZoneDefinition |
| `Services\ViolationDetector.cs` | 改构造+改方法 | 区域注入+改 GetZoneDefinition |
| `SOPModule.cs` | 改方法 | StartWorkflow 注入区域 |
| 其他引用 GlobalSettings/TimeoutSeconds 的文件 | 全局替换 | 搜索替换 |