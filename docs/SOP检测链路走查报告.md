# SOP 检测链路走查报告（相机 → YOLO/DWPose → 状态机 → 违规 → UI）

> 走查时间：2026-08-05
> 结论先行：**整条代码链路本身是通的**；"识别不到电线/物体"的真正根因不在代码逻辑，而在**专用模型缺失 + 占位区域坐标 + 静默回退**，其中"静默回退"已在本轮补上界面告警。

---

## 一、链路总览（实际代码路径）

```
相机采集 (CameraManager.ImageGrabbed)
   └─ OnCameraImageGrabbed            [MainViewModel.cs:185]
        ├─ ConvertCameraImageToSKBitmap(e)         → 相机帧转 SKBitmap
        ├─ if (IsSOPDetecting) skBitmap.Copy()     → 克隆用于推理
        └─ _inferenceQueue.Writer.TryWrite(bmp)    → 送入推理队列
             └─ 推理线程 StartInferenceWorker      [MainViewModel.cs:237]
                  └─ PerformSOPDetectionAsync(bmp) [MainViewModel.cs:490]
                       └─ SOPModule.ProcessAsync(frames)
                            ├─ 前置检查：状态机必须 Running，否则直接返回"未运行"
                            └─ ProcessFrameInternal
                                 ├─ ProcessUnifiedDetection
                                 │    ├─ YOLO.RunObjectDetection(frame.Image)      → 物体检测
                                 │    └─ HandPoseService.DetectHandsAsync(frame.Image) → DWPose 手部 21 点
                                 └─ SOPStateMachine.ProcessFrame(detections, hand, ts)
                                      ├─ UpdateTrackedObjects        → 物体跨帧跟踪
                                      ├─ ViolationDetector.DetectViolations → 违规（含漏放）
                                      └─ StepConditionEvaluator.EvaluateConditions → 步骤条件
                                           └─ IsPass → CompleteCurrentStep（步骤推进）
                            回写 DrawSOPDetectionResults → 画框 + 手部 + 步骤信息 → UI
```

---

## 二、已确认正常的部分（链路没断）

| 模块 | 结论 | 证据 |
|---|---|---|
| 状态机启动 | `Start()` 会把状态置 `Running` | SOPStateMachine.cs:49 |
| 帧链路贯通 | 相机→队列→推理线程→ProcessAsync 全通 | MainViewModel.cs:185/237/490/518 |
| 步骤 ID 映射 | YAML `"1"` → int `1`，状态机能 `FirstOrDefault(StepId==1)` | SOPYamlConfig.cs:397 |
| 图像类型 | `SKBitmap` 直接喂 YOLO（YoloDotNet 支持） | YoloDetectionService.cs:179（同款调用） |
| 手部检测 | DWPose 模型齐全（`yolox_l.onnx`+`dw-ll_ucoco_384.onnx`），21 点可用 | 磁盘确认存在 |
| 手部条件评估 | hand_in_region / hand_move / hand_stable / time_elapsed 逻辑完整 | StepConditionEvaluator.cs |
| 漏放规则 | `MissingRequiredObject` + 防误报（画面无任何必放物料时不误报）已落地 | ViolationDetector.cs:192 |

---

## 三、断点 / 根因（按优先级）

### 🔴 P0 — 专用模型不存在 → 物体检测整体失效（"识别不到电线"的真正根因）

- 示例配方 `sop_phone_packaging_demo.yaml` 指向 `model.path: yolo_models/sop_custom_yolov8s.onnx`，**该文件不存在**（磁盘上 `yolo_models/` 无任何 `sop_*` / `phone_*` / `custom_*` 文件）。
- `SOPModule.StartWorkflow` 中 `ResolveModelPath(...)` 返回 `null` → 仅写日志 → **静默回退**到初始化时加载的通用 COCO `yolov8s.onnx`（该文件存在，45MB）。
- COCO 模型的类别是 `person / car / bottle / ...`，**没有** `phone / case_top / case_bottom / manual / charger / cable`。
- 后果：所有产品专属物体永远检测不到；凡是依赖物体类别的步骤/规则（`object_present`、`object_in_zone`、`hand_near_object`、漏放校验）全部失效或误判；**界面上却没有任何报错提示**。

> 这是"太烂了 / 识别不到电线"的核心。它不是 bug，是**模型还没训练** + **失败被静默吞掉**。

### 🟠 P1 — 模型缺失是"静默"的（本轮已修复）

- 原本只 `File.AppendAllText` 写日志，操作员在界面上看不到任何信号，只能看到"什么都没识别到"。
- **本轮修复**：新增 `SOPModule.ModelWarning` 事件，模型缺失/回退时弹窗 + 状态栏明确提示"回退到 COCO、产品类别不可用、请训练专用模型"。

### 🟡 P2 — 区域坐标是占位值

- 示例配方 `regions` 是 1280×720 的占位坐标（`phone_tray / finished_zone ...`），**未用 ROI 工具按真机标定**。
- `hand_in_region` / `hand_move` / `object_in_zone` 都靠这些区域判定，坐标不匹配 → 步骤不推进、漏放区域校验错位。

### 🟡 P3 — 必须先连相机 + 开始采集，否则 SOP 不启动

- `StartSOPDetectionAsync` 会检查 `IsConnected` / `IsGrabbing`，否则弹"请先连接相机 / 请先开始相机采集"并返回。
- 很多"点了运行没反应"其实是相机没先开始采集。

### 🟢 P4 — 存在一条废弃测试路径 `RunSOPDetectionAsync`（用 `CreateTestImage`）

- 与实时路径并存，容易混淆。该路径不走相机、用测试图，建议后续清理或改名避免误用。

### 🟢 P5（潜在）— DWPose 的 TrackId / HandType 与跨帧跟踪

- DWPose 是逐帧全身姿态，没有时序跟踪，`TrackId` 可能恒为同一值，跨帧手部轨迹可能把左右手混为一条；`SelectHand` 要求 `IsValidGesture(8)`（≥15 个有效关键点），DWPose 21 点应满足。
- 需实机确认 `hand_move`（步骤 2–6）能正常推进。若推进异常，优先排查此处。

---

## 四、本轮已落地的修复

1. `SOPModule` 新增 `ModelWarning` 事件；`StartWorkflow` 在专用模型缺失时触发，明确告知"回退 COCO、产品类别失效、需训练专用模型"。
2. `MainViewModel` 订阅 `ModelWarning`，弹窗 + 状态栏提示。
3. 两个工程编译 0 错误（SOP 模块 0 警告；UI 仅既有 NU1701 包兼容警告）。

---

## 五、你真机上要让"识别"真正工作，必须做（非代码）

1. **训练专用模型**：用 `scripts/train_sop_detector.py`（已默认 `yolo26s.pt`）针对你的物料（phone/case_top/case_bottom/manual/charger/cable 等）标注训练，导出 `yolo_models/sop_xxx.onnx`，并把配方 `model.path` 指向它。
2. **用 ROI 工具按真机标定 `regions`**：坐标必须对应你相机实际画面分辨率与工位布局。
3. **启动顺序**：先连相机 → 开始采集 → 再点 ▶️ 运行（SOP 模块"运行"按钮走的是 `StartSOPDetectionAsync` 实时路径）。
4. **切换产品**：用标题栏"📦 产品配方"下拉框选对应 YAML；漏放等规则自动随配方生效。

---

## 六、下一步建议（按价值排序）

- [ ] 训练并导出专用模型（最大价值，直接解决"识别不到"）。
- [ ] 实机标定 regions（解决步骤不推进）。
- [ ] 实机验证 hand_move 步骤推进（排查 P5 的 TrackId 跟踪）。
- [ ] 清理废弃的 `RunSOPDetectionAsync` 测试路径（消除混淆）。
- [ ] 可选：把示例 YAML 的 `model.path` 临时指向现存的 `yolov8s.onnx` 做端到端冒烟（会看到 COCO 能认的类，但产品类仍无），确认管线在真机上跑通。
