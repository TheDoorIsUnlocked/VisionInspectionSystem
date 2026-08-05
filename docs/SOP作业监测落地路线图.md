# SOP 作业监测落地路线图

> 目标：从"能画出 21 点手部骨架"升级到"像参考图一样对产线作业进行 SOP 合规监测"。
> 参考图能力：多目标检测（desktop/conveyor/frame/pressTool/tape）+ 手部骨架叠加 + 右侧 S1~S4 步骤面板（结果/耗时/良率）。

## 一、当前已具备的能力

| 模块 | 状态 | 说明 |
|------|------|------|
| 手部 21 点检测 | ✅ 可用 | DWPose 后端已启用（`yolox_l.onnx` + `dw-ll_ucoco_384.onnx`），`MainViewModel` 已改画完整 21 点 |
| 手部跟踪（Kalman） | ✅ 可用 | `DWPoseHandDetector` 内部已实现 |
| 工作流定义（JSON） | ✅ 可用 | `SOPWorkflowManager` + `SOPWorkflow`/`SOPStep`，支持 CRUD |
| YAML 工作流解析 | ⚠️ 半可用 | `SOPYamlConfig` 能解析，但只支持 5 种基础条件 |
| 状态机/步骤推进 | ⚠️ 半可用 | `SOPStateMachine` 能按步骤推进，但**只接收物体检测结果** |
| 物体检测（YOLO） | ❌ 被注释 | `SOPModule.ProcessUnifiedDetection` 里 YOLO 分支被 `// 临时屏蔽物体检测` 整段注释 |
| 手部姿态进入状态机 | ❌ 未接入 | `_lastHandPoseResult` 仅用于事件和 UI 绘制，没传给 `SOPStateMachine` |
| 手部动作条件评估 | ❌ 未实现 | `StepConditionEvaluator` 没有 HandInRegion/HandMoveFromTo/HandStable 等条件 |
| SOP 专用检测模型 | ❌ 不存在 | `yolo_models/` 只有通用 COCO 模型，没有 `pressTool`/`tape`/`frame` 等自定义类别 |
| 区域标定 UI | ❌ 不存在 | 区域硬编码在 `SOPModule.SetupDefaultRegions`，用户无法动态画 ROI |

## 二、参考图与当前差距

参考图同时需要三类信息才能做"作业监测"：

1. **目标在哪里** → 需要 YOLO 检测 `pressTool` / `tape` / `frame` / `desktop` / `conveyor` 等。
2. **手在哪里、在做什么** → 需要把手部 21 点送进状态机，判断"手进区域 A""手从 A 移到 B""手拿住物体"等。
3. **步骤是否合规** → 需要状态机把上述两类感知融合，驱动 S1/S2/S3/S4 步骤推进并记录耗时/违规。

当前差距集中在：**物体检测被关、手部数据进不了状态机、状态机不会评估手部动作**。

## 三、分阶段落地路线

### P0 —— 打通感知与状态机（必须先做）

| # | 任务 | 关键改动位置 | 工作量 |
|---|------|------------|--------|
| P0.1 | **恢复 YOLO 物体检测** | `SOPModule.ProcessUnifiedDetection` 取消注释 YOLO 分支 | 小 |
| P0.2 | **准备 SOP 专用检测模型** | 训练/转换一个包含 `pressTool`、`tape`、`frame`、`desktop`、`conveyor`、`product` 等类别的 ONNX 模型，放到 `yolo_models/` | 大 |
| P0.3 | **把手部结果传给状态机** | 修改 `SOPStateMachine.ProcessFrame` 签名，新增 `HandPoseEstimationResult?` 参数；`SOPModule.ProcessFrameInternal` 把 `_lastHandPoseResult` 传进去 | 中 |
| P0.4 | **新增手部条件类型** | 在 `ConditionType` 增加 `HandInRegion`、`HandNotInRegion`、`HandStable`、`HandMoveFromTo`、`HandNearObject`；在 `StepConditionEvaluator` 实现对应评估 | 中 |
| P0.5 | **让 YAML 支持手部动作** | `SOPYamlConfig.ConvertDetectionToCondition` 增加 `hand_in_region`、`hand_move`、`hand_stable` 等 method 映射 | 中 |

### P1 —— 动作语义与防错规则

| # | 任务 | 关键改动位置 | 说明 |
|---|------|------------|------|
| P1.1 | **手部时序跟踪器** | 新建 `HandActionTracker` | 跨帧跟踪同一只手，判断"进入区域→停留→离开"完整动作，避免单帧抖动 |
| P1.2 | **定义基础动作原语** | 新建 `HandActionPrimitives` | `Reach`（伸手到区域）、`Grasp`（指尖闭合/抓取）、`Hold`（稳定持物）、`Move`（区域 A→B）、`Place`（放手）、`Release`（撤离） |
| P1.3 | **手-物交互判断** | 新建 `HandObjectInteractionEvaluator` | 判断"手是否在工具 bbox 内""手是否夹着零件" |
| P1.4 | **跳步/顺序/超时违规** | 复用 `ViolationDetector` | 已部分实现，需结合手部动作增强（如"手提前进入后续步骤区域"判定跳步） |
| P1.5 | **启用 PoseConditionEvaluator 或替换** | `SOPModule` | 当前 `_poseEvaluator` 仅支持全身姿态（`HumanPose`），而 DWPose 输出是 `HandPoseEstimationResult`；建议新增 `HandConditionEvaluator` 专门消费手部结果 |

### P2 —— UI 与可视化（接近参考图效果）

| # | 任务 | 关键改动位置 | 说明 |
|---|------|------------|------|
| P2.1 | **主画面叠加区域框** | `MainViewModel.DrawSOPDetection` 或新增 `DrawRegions` | 用不同颜色画 ROI 区域，并显示区域名 |
| P2.2 | **主画面叠加当前步骤/违规** | `MainViewModel.DrawHandPoses` 附近 | 在画面顶部或底部显示"S3: 拧紧螺栓 [进行中]"以及红色违规横幅 |
| P2.3 | **右侧步骤面板数据绑定** | `SOPModuleView.xaml` / `MainViewModel` | 参考图右侧的 S1~S4 表格：步骤名、结果（OK/NG/—）、耗时、周期时间 |
| P2.4 | **良率/统计面板** | `SOPModuleView.xaml` | 本次运行良率、总周期数、平均 CT |
| P2.5 | **日志区保留并增强** | `SOPModuleView` 已有日志 TextBox | 增加步骤切换/违规/条件满足的日志输出 |

### P3 —— 工程化与产线可用

| # | 任务 | 说明 |
|---|------|------|
| P3.1 | **区域标定工具** | 在视频画面上用鼠标画 ROI，保存到工作流 JSON/YAML |
| P3.2 | **工作流设计器** | 可视化添加步骤、选择条件类型、选择左手/右手/物体、设置超时 |
| P3.3 | **模型热切换** | `StartWorkflow` 已支持按 workflow.Model 加载模型，需确保部署时路径正确 |
| P3.4 | **异常恢复/人工确认** | 违规时暂停、人工确认后继续；支持强制放行/强制重测 |
| P3.5 | **数据落盘与追溯** | 每次作业生成一条记录：步骤耗时、违规列表、关键帧截图、最终良率 |

## 四、P0 详细改动点（可直接落地）

### P0.1 恢复 YOLO 检测

文件：`src/Modules/VisionInspection.Modules.SOP/SOPModule.cs`

```csharp
private void ProcessUnifiedDetection(CaptureFrame frame, SOPModuleResult result, DateTime timestamp)
{
    // 1. 物体检测
    List<ObjectDetection> detections = new();
    if (_yolo != null)
    {
        detections = _yolo.RunInference(frame.Image, _config.ConfidenceThreshold)
                           .Cast<ObjectDetection>()
                           .ToList();
        result.Detections = detections;
    }

    // 2. 手部姿态检测（现有代码）
    // ...

    // 3. 把感知结果喂给状态机
    _stateMachine?.ProcessFrame(detections, _lastHandPoseResult, timestamp);
}
```

> 注意：当前代码里 `_yolo` 的调用方式需与项目实际 YOLO 封装对齐，以上为示意。

### P0.3 状态机接收手部结果

文件：`src/Modules/VisionInspection.Modules.SOP/Models/SOPStateMachine.cs`

```csharp
public void ProcessFrame(
    List<ObjectDetection> detections,
    HandPoseEstimationResult? handResult,
    DateTime timestamp)
{
    // 现有逻辑...
    UpdateTrackedObjects(detections, timestamp);

    // 新增：评估手部条件
    if (handResult != null)
    {
        _handConditionEvaluator?.Evaluate(currentStep, handResult);
    }

    var violations = _violationDetector.DetectViolations(currentStep, detections, timestamp);
    // ...

    var evaluation = _conditionEvaluator.EvaluateConditions(currentStep, detections, handResult);
    // ...
}
```

### P0.4 新增条件类型

文件：`src/Modules/VisionInspection.Modules.SOP/Models/SOPWorkflow.cs`

```csharp
public enum ConditionType
{
    ObjectPresent,
    ObjectInZone,
    ObjectStable,
    ObjectAbsent,
    SequenceComplete,
    TimeElapsed,
    HandInRegion,        // 手进入区域
    HandNotInRegion,     // 手离开/不在区域
    HandStable,          // 手在区域内稳定 N 帧
    HandMoveFromTo,      // 手从区域 A 移动到区域 B
    HandNearObject,      // 手靠近目标物体
    Custom
}
```

文件：`src/Modules/VisionInspection.Modules.SOP/Services/StepConditionEvaluator.cs`

新增 `CheckHandInRegion`、`CheckHandMoveFromTo`、`CheckHandStable`、`CheckHandNearObject` 四个方法，并更新 `CheckCondition` switch。

### P0.2 专用检测模型建议

1. **收集数据**：在你的工位上用手机/相机录 10~30 段作业视频（覆盖光照变化、角度变化、左右手习惯）。
2. **标注类别**：参考图出现 `desktop`、`conveyor_or`、`frame`、`pressTool`、`tape`，以及你的产品对象（如 `glass`、`panel`、`screwdriver`）。
3. **训练**：用 Ultralytics YOLOv8/YOLO11 训练，导出 `best.onnx`。
4. **放置**：放到 `yolo_models/sop_custom_yolov8s.onnx`。
5. **配置工作流 model**：

```yaml
model:
  path: "yolo_models/sop_custom_yolov8s.onnx"
  type: "ObjectDetection"
  confidence: 0.5
  iou: 0.45
  useGpu: true
  classes: ["desktop", "conveyor", "frame", "pressTool", "tape", "product"]
```

## 五、推荐的最小可行路径（MVP）

如果你想最快看到"监测效果"，按下面顺序：

1. **恢复 YOLO 检测**（P0.1，1 小时）。
2. **训练一个最小模型**（P0.2，1~2 天）：只标 3~5 类最关键物体。
3. **加 3 个手部条件**（P0.3 + P0.4，半天）：`HandInRegion`、`HandStable`、`HandMoveFromTo`。
4. **写一个 3 步骤 YAML 工作流**测试：
   - S1: 右手进入 `part_box` 区域 → 取料完成
   - S2: 右手移动到 `fixture` 区域并稳定 1 秒 → 放料完成
   - S3: 左手进入 `tool_rack` 取 `pressTool` 并在产品上稳定 → 压合完成
5. **UI 显示步骤结果**（P2.3，半天）。

做到这 5 步，画面和右侧面板就会非常接近参考图。

## 六、部署验证清单

每次改动后部署电脑需替换/更新的文件：

- `VisionInspection.Modules.SOP.dll`（核心逻辑改动）
- `VisionInspection.UI.dll`（UI 改动）
- `configs/sop_config.json`（如新增配置项）
- `configs/sop/*.json` 或新增 `*.yaml`（工作流定义）
- `yolo_models/sop_custom_*.onnx`（新增专用检测模型）
- `DWPose-onnx/models/*.onnx`（已存在，无需更新）
