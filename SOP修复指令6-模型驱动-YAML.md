# SOP检测系统修复指令6：YAML 驱动自训练模型

> **项目路径**: `E:\yolo\YoloDotNet-master\VisionInspectionSystem`
> **生成日期**: 2026-05-14
> **优先级**: P0（不改不能用，因为缺这个功能，螺钉/垫片等自训练模型无处挂载）

---

## 当前问题诊断

```
当前架构：
  YAML配置 (sop yaml)
    └── model  ← 不存在！自训练模型无处指定
              ↓
  SOPModule
    └── _config.ModelPath = "models/sop_yolov8.onnx"  ← 硬编码路径，永远加载同一个模型
              ↓
  Yolo (YoloDotNet)
    └── 只能加载通用COCO模型，检测不到自己的螺钉/垫片

缺失链路：
  YAML配置 model → SOPModule 按工作流动态切换模型 → YoloDotNet 加载对应 ONNX
```

### 具体问题

**问题1：YAML 配置中无模型字段**
`SopyamlSop` 类没有 `Model` 字段。所以 YAML 里配不了针对自训练模型的路径、类别、阈值。

**问题2：SOPModule 硬编码单个模型路径**
```csharp
// SOPModuleConfig
public class SOPModuleConfig
{
    public string ModelPath { get; set; } = "models/sop_yolov8.onnx";
    // ...
}
```
只读 `appsettings.json` 一次，永远用这个路径。

**问题3：切换工作流时不出不重新加载模型**
```csharp
// SOPModule.StartWorkflow()
public void StartWorkflow(SOPWorkflow workflow)
{
    // 只做了：注入区域定义
    _stateMachine.UpdateZones(workflow.Regions);
    _stateMachine.Start(workflow);
    // ← 没有根据模型切换 YOLO！
}
```

**问题4：SOPWorkflow 中没有模型字段**
`SOPWorkflow` 类是 `model` 字段，所以 ModelManager 的模型列表和 SOP 工作流之间也没有关联。

---

## 修复目标（实现后的效果）

```yaml
sop:
  name: "转向器装配SOP"
  version: "1.0"

  # ⭐ 新增：该产品配置自己的模型
  model:
    path: "yolo_models/sop_steering_housing.onnx"    # 自训练ONNX模型路径
    type: "ObjectDetection"                             # 模型类型
    confidence: 0.6                                     # 置信度阈值（覆盖yaml settings）
    iou: 0.45                                           # NMS IoU阈值
    use_gpu: true                                       # 是否用GPU
    classes:                                             # 该模型的检测类别
      - "shell"          # 壳体
      - "bolt"           # 螺栓
      - "gasket"         # 垫片
      - "cover"          # 盖板
      - "torque_wrench"  # 扭矩扳手
      - "screwdriver"    # 螺丝刀
      - "assembly_ok"    # 装配完成状态

  regions:
    fixture_zone:
      x1: 300,  y1: 250,  x2: 500,  y2: 450

  steps: ...
```

**换产品 = 换 YAML = 自动换模型**，无需改代码。

---

## 修复步骤

### 修复6.1：YAML 模型字段（SOPYamlConfig.cs）

#### 修改1：在 `SopyamlSop` 类中添加 model 配置类

**文件**: `src\Modules\VisionInspection.Modules.SOP\Models\SOPYamlConfig.cs`

**操作**: 在 `SopyamlSop` 类定义中（当前第15行左右），在现有字段下方添加：

```csharp
    [YamlMember(Alias = "visualization")]
    public SopyamlVisualization? Visualization { get; set; }

    // ⭐ 新增：SOP模型配置
    [YamlMember(Alias = "model")]
    public SopyamlModel? Model { get; set; }
}
```

**操作**: 在 `SopyamlSop` 类**下方**添加新的模型配置类：

```csharp
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
```

#### 修改2：在 `ConvertToWorkflow()` 中转换模型配置

**文件**: `src\Modules\VisionInspection.Modules.SOP\Models\SOPYamlConfig.cs`

**操作**: 在 `ConvertToWorkflow` 方法末尾（当前在 `workflow.Steps.Add(step)` 循环结束后、`return workflow` 之前），添加模型转换逻辑：

找到这段代码：
```csharp
            workflow.Steps.Add(step);
        }

        return workflow;
    }
```

替换为：
```csharp
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
                workflow.Description = $"模型: {Path.GetFileName(yamlSop.Model.Path)}";
            }
            else
            {
                workflow.Description += $" | 模型: {Path.GetFileName(yamlSop.Model.Path)}";
            }
        }

        return workflow;
    }
```

---

### 修复6.2：SOPWorkflow 添加 Model 字段

**文件**: `src\Modules\VisionInspection.Modules.SOP\Models\SOPWorkflow.cs`

**修改前**：
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

**修改后**：
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

    // ⭐ 新增：该工作流配置的 YOLO 模型
    public SOPModelConfig? Model { get; set; }
}
```

**操作**: 在同一文件 (`SOPWorkflow.cs`) 中添加新的 `SOPModelConfig` 类，放在 `ZoneDefinition` 类之后：

```csharp
/// <summary>
/// SOP 检测模型配置
/// 定义该工作流/产品使用哪一个 YOLO 模型
/// </summary>
public class SOPModelConfig
{
    /// <summary>
    /// ONNX 模型文件路径
    /// </summary>
    public string Path { get; set; } = "yolo_models/sop_yolov8n.onnx";

    /// <summary>
    /// 模型类型标识
    /// </summary>
    public string Type { get; set; } = "ObjectDetection";

    /// <summary>
    /// 检测置信度阈值 (0-1)
    /// </summary>
    public float Confidence { get; set; } = 0.6f;

    /// <summary>
    /// NMS IoU 阈值 (0-1)
    /// </summary>
    public float Iou { get; set; } = 0.45f;

    /// <summary>
    /// 是否使用 GPU 加速
    /// </summary>
    public bool UseGpu { get; set; } = true;

    /// <summary>
    /// GPU 设备 ID
    /// </summary>
    public int GpuId { get; set; } = 0;

    /// <summary>
    /// 该模型可检测的类别名称列表
    /// 用于 SOP 条件评估时做类别名称校验
    /// </summary>
    public List<string> Classes { get; set; } = new();
}
```

---

### 修复6.3：SOPModule 支持工作流级模型切换

**文件**: `src\Modules\VisionInspection.Modules.SOP\SOPModule.cs`

#### 修改1：改造 `InitializeYoloAsync`，支持动态重新加载

**当前代码**（约第100-115行）：
```csharp
    private async Task InitializeYoloAsync()
    {
        if (!File.Exists(_config.ModelPath))
        {
            // 模型文件不存在，使用模拟模式
            _yolo = null;
            return;
        }

        await Task.Run(() =>
        {
            var options = new YoloOptions
            {
                ExecutionProvider = _config.UseGpu
                    ? new CudaExecutionProvider(_config.ModelPath, 0)
                    : new CpuExecutionProvider(_config.ModelPath),
                ImageResize = ImageResize.Proportional,
                SamplingOptions = new(SKFilterMode.Nearest, SKMipmapMode.None)
            };

            _yolo = new Yolo(options);
        });
    }
```

**替换为**：
```csharp
    private YoloOptions? _pendingYoloOptions;
    private float _pendingConfidence = 0.6f;
    private float _pendingIou = 0.45f;
    private string _lastLoadedModelPath = "";

    /// <summary>
    /// 初始化 YOLO（启动时调用一次）
    /// </summary>
    private async Task InitializeYoloAsync()
    {
        await LoadYoloAsync(_config.ModelPath, _config.UseGpu, _config.ModelPath);
    }

    /// <summary>
    /// 按指定路径加载/切换 YOLO 模型
    /// </summary>
    private async Task LoadYoloAsync(string modelPath, bool useGpu, string? gpuDevice = null)
    {
        // 如果路径没变且模型已加载，跳过
        if (_yolo != null && modelPath == _lastLoadedModelPath)
        {
            Console.WriteLine($"[SOP] 模型未变化，跳过重载: {modelPath}");
            return;
        }

        // 如果已经有一个占位（pending），先完成它
        if (_pendingYoloOptions != null)
        {
            Console.WriteLine("[SOP] 警告: 等待上一个模型加载完成...");
            await Task.Delay(500);
        }

        // 先加载（不锁定），放到 pending 里
        _pendingYoloOptions = null; // 占位

        await Task.Run(() =>
        {
            try
            {
                lock (_lockObject)
                {
                    // 释放旧模型
                    _yolo?.Dispose();
                    _yolo = null;

                    if (!File.Exists(modelPath))
                    {
                        Console.WriteLine($"[SOP] 警告: 模型文件不存在: {modelPath}");
                        _lastLoadedModelPath = "";
                        return;
                    }

                    var options = new YoloOptions
                    {
                        ExecutionProvider = useGpu
                            ? new CudaExecutionProvider(modelPath, gpuDevice ?? "0")
                            : new CpuExecutionProvider(modelPath),
                        ImageResize = ImageResize.Proportional,
                        SamplingOptions = new(SKFilterMode.Nearest, SKMipmapMode.None)
                    };

                    _yolo = new Yolo(options);
                    _lastLoadedModelPath = modelPath;
                    Console.WriteLine($"[SOP] 模型加载成功: {Path.GetFileName(modelPath)}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SOP] 模型加载失败: {ex.Message}");
                _yolo = null;
                _lastLoadedModelPath = "";
            }
        });
    }
```

#### 修改2：在 `StartWorkflow` 中检查并切换模型

**当前方法**（约第430-450行）：
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
            Console.WriteLine("[SOP] 警告: 工作流中没有区域定义");
        }

        _stateMachine.Start(workflow);
    }
```

**替换为**：
```csharp
    public void StartWorkflow(SOPWorkflow workflow)
    {
        if (_stateMachine == null)
            throw new InvalidOperationException("状态机未初始化");

        var previousWorkflow = _currentWorkflow;
        _currentWorkflow = workflow;

        // ⭐ 注入区域定义（已有）
        if (workflow.Regions != null && workflow.Regions.Count > 0)
        {
            _stateMachine.UpdateZones(workflow.Regions);
            Console.WriteLine($"[SOP] 已加载 {workflow.Regions.Count} 个区域定义");
        }
        else
        {
            Console.WriteLine("[SOP] 警告: 工作流中没有区域定义");
        }

        // ⭐ 检查并切换 YOLO 模型
        if (workflow.Model != null && !string.IsNullOrWhiteSpace(workflow.Model.Path))
        {
            var modelPath = workflow.Model.Path;

            // 支持相对路径：相对于项目根目录下 yolo_models/ 目录
            if (!Path.IsPathRooted(modelPath))
            {
                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                var projectRoot = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", ".."));
                var candidate1 = Path.Combine(projectRoot, "yolo_models", modelPath);
                var candidate2 = Path.Combine(projectRoot, modelPath);

                if (File.Exists(candidate1))
                    modelPath = candidate1;
                else if (File.Exists(modelPath))
                { /* 相对路径指的就是当前模型路径，保持不变 */ }
                else if (File.Exists(candidate2))
                    modelPath = candidate2;
            }

            if (modelPath != _lastLoadedModelPath)
            {
                Console.WriteLine($"[SOP] 切换模型: {_lastLoadedModelPath} → {modelPath}");
                Console.WriteLine($"[SOP] 模型配置: confidence={workflow.Model.Confidence}, iou={workflow.Model.Iou}, gpu={workflow.Model.UseGpu}");

                // 同步更新运行时置信度/IoU（本地生效，非配置文件）
                _config.ConfidenceThreshold = workflow.Model.Confidence;
                _config.IouThreshold = workflow.Model.Iou;

                // 后台加载新模型（不影响当前帧处理）
                _ = Task.Run(() => LoadYoloAsync(modelPath, workflow.Model.UseGpu, workflow.Model.GpuId.ToString()));
                Console.WriteLine($"[SOP] 模型正在后台加载... 当前帧仍使用前一个模型");
            }
            else
            {
                Console.WriteLine($"[SOP] 模型未变化，跳过: {Path.GetFileName(modelPath)}");
            }
        }

        _stateMachine.Start(workflow);
    }
```

#### 修改3：`ProcessObjectDetection` 优先用工作流覆盖的阈值

**当前代码**（约第303-312行）：
```csharp
    private void ProcessObjectDetection(CaptureFrame frame, SOPModuleResult result, DateTime timestamp)
    {
        if (_yolo == null || _stateMachine == null) return;

        var detections = _yolo.RunObjectDetection(
            frame.Image,
            confidence: _config.ConfidenceThreshold,
            iou: _config.IouThreshold);

        _stateMachine.ProcessFrame(detections.ToList(), timestamp);
        result.Detections = detections.ToList();
    }
```

**替换为**：
```csharp
    private async Task ProcessObjectDetection(CaptureFrame frame, SOPModuleResult result, DateTime timestamp)
    {
        if (_stateMachine == null) return;

        // 如果 YOLO 还没加载好（刚切换工作流，模型在后台加载中），用空结果
        if (_yolo == null)
        {
            Console.WriteLine("[SOP] 模型加载中，本帧跳过检测");
            return;
        }

        // 使用当前工作流覆盖的阈值，否则用全局配置
        float confidence = _currentWorkflow?.Model?.Confidence ?? _config.ConfidenceThreshold;
        float iou = _currentWorkflow?.Model?.Iou ?? _config.IouThreshold;

        var detections = _yolo.RunObjectDetection(
            frame.Image,
            confidence: confidence,
            iou: iou);

        _stateMachine.ProcessFrame(detections.ToList(), timestamp);
        result.Detections = detections.ToList();
    }
```

#### 修改4：`ProcessAsync` 中 `ProcessObjectDetection` 需 await

因为方法从 `RunObjectDetection` 改成了 `async Task`，调用处需要加 `await`。

**当前代码**（约第280-305行）：
```csharp
    public Task<ModuleResult> ProcessAsync(Dictionary<string, CaptureFrame> frames)
    {
        // ... 前置检查 ...
        lock (_lockObject)
        {
            ProcessFrameInternal(mainFrame, result);
        }
        // ...
    }
```

**替换 lock 内部为**：
```csharp
        lock (_lockObject)
        {
            ProcessFrameInternal(mainFrame, result).GetAwaiter().GetResult();
        }
```

---

### 修复6.4：HANDY BONUS - YAML 配置示例（真实注塑件场景）

**新建文件**: `configs\sop\sop_steering_housing.yaml`

```yaml
sop:
  name: "转向器壳体总装SOP"
  version: "1.2"
  description: "转向器壳体装配，共6步。使用自训练SOP模型检测工业零件。"

  # ⭐ 模型节点：指定这个产品要用的自训练模型
  model:
    path: "yolo_models/sop_steering_housing.onnx"
    type: "ObjectDetection"
    confidence: 0.6          # 工业场景建议0.5-0.7，太高漏检太低误报
    iou: 0.45
    use_gpu: true
    gpu_id: 0
    classes:
      - "hand"           # 人手
      - "screwdriver"    # 螺丝刀
      - "torque_wrench"  # 扭矩扳手
      - "part_base"      # 转向器壳体（底座）
      - "part_cover"     # 盖板
      - "part_screw"     # 螺栓
      - "part_gasket"    # O型密封圈
      - "assembly_ok"    # 装配完成标记

  settings:
    confidenceThreshold: 0.6
    stableFrames: 10
    timeoutSeconds: 30

  regions:
    fixture_zone:
      x1: 300,  y1: 250,  x2: 500,  y2: 450
      name: "夹具定位区"
    seal_zone:
      x1: 350,  y1: 300,  x2: 500,  y2: 380
      name: "密封面区域"
    tool_zone:
      x1: 550,  y1: 200,  x2: 750,  y2: 400
      name: "工具架区域"

  forbidden_zones:
    danger_left:
      x1: 0,    y1: 0,    x2: 100,  y2: 1000
      name: "左侧危险区"

  steps:
    - id: "step_1"
      name: "取壳体放入夹具"
      timeout: 30
      transitions: ["step_2"]
      detection:
        method: "object_in_zone"
        target_object: "part_base"
        region: "fixture_zone"
        min_confidence: 0.7
        stable_frames: 10
      forbidden_objects: ["screwdriver", "torque_wrench"]

    - id: "step_2"
      name: "装密封垫片"
      timeout: 30
      transitions: ["step_3"]
      detection:
        method: "object_in_zone"
        target_object: "part_gasket"
        region: "seal_zone"
        min_confidence: 0.65
        stable_frames: 10
      must_keep:
        - "part_base"

    - id: "step_3"
      name: "装盖板"
      timeout: 30
      transitions: ["step_4"]
      detection:
        method: "object_in_zone"
        target_object: "part_cover"
        region: "fixture_zone"
        min_confidence: 0.7
        stable_frames: 10
      must_keep:
        - "part_base"
        - "part_gasket"

    - id: "step_4"
      name: "穿螺栓"
      timeout: 45
      transitions: ["step_5"]
      detection:
        method: "object_in_zone"
        target_object: "part_screw"
        region: "fixture_zone"
        min_confidence: 0.6
        stable_frames: 5
      must_keep:
        - "part_base"
        - "part_gasket"
        - "part_cover"

    - id: "step_5"
      name: "扭矩拧紧"
      timeout: 60
      transitions: ["step_6"]
      detection:
        method: "object_in_zone"
        target_object: "torque_wrench"
        region: "fixture_zone"
        min_confidence: 0.65
        stable_frames: 15
      must_keep:
        - "part_base"
        - "part_cover"
        - "part_screw"

    - id: "step_6"
      name: "装配完成确认"
      timeout: 15
      transitions: []
      detection:
        method: "object_present"
        target_object: "assembly_ok"
        min_confidence: 0.75
        stable_frames: 5
```

**另一个产品的YAML示例如下**（不需要改任何代码，只有 YAML 变了）

**新建文件**: `configs\sop\sop_motor_endcap.yaml`

```yaml
sop:
  name: "电机端盖装配"
  version: "1.0"

  # ⭐ 换个模型！同一个 SOP 模块，不同产品用不同模型
  model:
    path: "yolo_models/sop_motor_endcap.onnx"
    confidence: 0.6
    iou: 0.45
    use_gpu: true

  settings:
    confidenceThreshold: 0.6
    stableFrames: 10
    timeoutSeconds: 30

  regions:
    fixture_zone:
      x1: 300, y1: 250, x2: 500, y2: 450

  steps:
    - id: "step_1"
      name: "放端盖"
      timeout: 20
      transitions: ["step_2"]
      detection:
        method: "object_in_zone"
        target_object: "part_base"
        region: "fixture_zone"
        min_confidence: 0.7
        stable_frames: 10

    - id: "step_2"
      name: "拧螺丝"
      timeout: 40
      transitions: ["step_3"]
      detection:
        method: "object_in_zone"
        target_object: "screwdriver"
        region: "fixture_zone"
        min_confidence: 0.65
        stable_frames: 15
      must_keep:
        - "part_base"

    - id: "step_3"
      name: "完成确认"
      timeout: 15
      transitions: []
      detection:
        method: "object_present"
        target_object: "assembly_ok"
        min_confidence: 0.75
        stable_frames: 5
```

---

### 修复6.5：ModelInfo 添加 ClassNameVerification 字段（可选，扩展）

**文件**: `src\Core\VisionInspection.Core\Services\IModelManager.cs`

在 `ModelInfo` 类中添加一个新字段（用于 UI 展示）：

```csharp
    /// <summary>
    /// 类别名称验证列表（加载模型后自动填充，供 SOP 条件校验使用）
    /// </summary>
    public List<string> ClassNames { get; set; } = new();
```

---

### 修复后验证清单

#### 1. YAML 解析验证

```csharp
// 测试：从 YAML 加载工作流并检查 Model 字段
var workflow = SOPYamlConverter.LoadFromYaml("configs/sop/sop_steering_housing.yaml");

// 验证 Model 字段是否存在
Console.WriteLine($"Model.Path: {workflow.Model?.Path}");  // 应输出 yolo_models/sop_steering_housing.onnx
Console.WriteLine($"Model.Classes.Count: {workflow.Model?.Classes.Count}");  // 应输出 8
Console.WriteLine($"Model.Confidence: {workflow.Model?.Confidence}");  // 应输出 0.6

// 验证第一个步骤
Console.WriteLine($"Step count: {workflow.Steps.Count}");  // 应输出 6
Console.WriteLine($"Step1.PassConditions: {workflow.Steps[0].PassConditions.Count}");  // 应输出 1
Console.WriteLine($"Step1.ViolationRules: {workflow.Steps[0].ViolationRules.Count}");  // 应输出 1
Console.WriteLine($"Step1.Regions.Count: {workflow.Regions.Count}");  // 应输出 3
```

#### 2. 模型切换验证

```csharp
// 测试：启动 A 产品工作流 → 再切换到 B 产品工作流
sopModule.StartWorkflowFromYaml("configs/sop/sop_steering_housing.yaml");
// 控制台应输出：
// [SOP] 初次加载模型: sop_steering_housing.onnx
// [SOP] 模型加载成功: sop_steering_housing.onnx

Thread.Sleep(3000); // 等模型加载完成

sopModule.StartWorkflowFromYaml("configs/sop/sop_motor_endcap.yaml");
// 控制台应输出：
// [SOP] 切换模型: sop_steering_housing.onnx → sop_motor_endcap.onnx
// [SOP] 配置: confidence=0.6, iou=0.45, gpu=True
// [SOP] 模型正在后台加载... 当前帧仍使用前一个模型
```

#### 3. 关键点搜索验证

```
搜索 "SOPModelConfig" → 应该出现在 SOPWorkflow.cs 和 SOPYamlConfig.cs 两个文件中
搜索 "SopyamlModel" → 应该只出现在 SOPYamlConfig.cs 中
搜索 "LoadYoloAsync" → 应该出现在 SOPModule.cs 中且被 StartWorkflow 调用
搜索 "Model != null && !string.IsNullOrWhiteSpace(workflow.Model.Path)" → 
    应该出现在 SOPModule.StartWorkflow() 中
搜索 "_lastLoadedModelPath" → 应该出现在 SOPModule.cs 中（用于模型复用判断）
```

#### 4. YAML 文件存在验证

```bash
# 确保两个示例 YAML 在正确位置
ls configs/sop/sop_steering_housing.yaml  # 应该存在
ls configs/sop/sop_motor_endcap.yaml      # 应该存在
```

#### 5. Compile 验证

```bash
cd E:\yolo\YoloDotNet-master\VisionInspectionSystem
dotnet build
# 应该 0 errors
```

---

## 改动波及的文件总览

| 文件 | 改动类型 | 说明 |
|------|---------|------|
| `Models\SOPYamlConfig.cs` | 新增类 + 修改转换器 | 添加 `SopyamlModel` + `SOPModelConfig` + `ConvertToWorkflow` 增加模型转换 |
| `Models\SOPWorkflow.cs` | 新增字段 | `Model` 属性 + `SOPModelConfig` 类 |
| `Classes\SOPModule.cs` | 新增字段 + 改造方法 | `_lastLoadedModelPath` / `LoadYoloAsync` / `StartWorkflow` 模型切换逻辑 |
| `configs\sop\sop_steering_housing.yaml` | 新建 | 真实工位示例 YAML |
| `configs\sop\sop_motor_endcap.yaml` | 新建 | 另一安装配示例 YAML |

---

## 施工注意事项

1. **model.path 支持相对路径**：优先检查 `项目根/yolo_models/` 文件夹，找不到再试原路径——这样换电脑一样能跑
2. **模型切换是异步的**：新模型在后台加载，当前帧继续用旧模型，避免检测中断。模型加载完成后下一帧自动生效
3. **置信度阈值用工作流覆盖值**：YAML 里的 `model.confidence` 比 `appsettings.json` 中的更具体（按产品优化），优先级更高
4. **如果工作流里没配 model 字段**：`workflow.Model == null`，fallback 到 `_config` 的路径，兼容旧配置