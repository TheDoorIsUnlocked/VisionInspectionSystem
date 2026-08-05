# SOP-AI 作业监控系统 - 项目长期记忆

## 关键路径约定（重要！）
- **SOP yaml 实际生效路径**：`src/UI/VisionInspection.UI/configs/sop/*.yaml`（csproj 用 `CopyToOutputDirectory=PreserveNewest` 复制到 `bin/Debug/.../configs/sop/`）。
- **不是**项目根目录的 `configs/sop/*.yaml`（那里也有副本但程序不读！之前改错文件导致改动不生效）。
- 程序运行时读的是 `bin/Debug/net8.0-windows/configs/sop/` 下的副本。
- **PreserveNewest 陷阱**：如果 bin 下副本被程序"保存标定区域"功能写过（时间戳变新），后续 `dotnet build` 不会用源码覆盖它。需手动删 bin 下副本才能刷新。

## YAML 序列化/反序列化
- `SOPYamlConfig.cs` 的 `SopyamlDetection` 类所有属性都有 `[YamlMember(Alias="snake_case")]`，反序列化能正确匹配 snake_case 字段。
- 序列化器/反序列化器统一用**无命名约定**（不指定 NamingConvention），靠 [YamlMember] Alias 绑定字段名。
- **关键陷阱**：`SaveRegions` 的反序列化器曾残留 `CamelCaseNamingConvention`（序列化器已修但反序列化器漏改），导致 alias 被 naming convention 覆盖、snake_case 字段匹配失败、`IgnoreUnmatchedProperties` 静默丢弃 → 保存后 from_region/target_object/action 全变空。**序列化器和反序列化器必须用同一套命名约定。**
- **已修复往返 bug**：`ConvertConditionToDetection` 的 `HandMoveFromTo` 分支曾把 method 写成 `hand_move`、丢失 `action`/`target_object`，导致 hand_action 步骤保存后退化。现在根据 `Parameters["Action"]` 判断保存为 `hand_action`（含 action/target_object/from_region/to_region）还是 `hand_move`。

## 检测框时序平滑
- `DetectionTrackSmoother`（`src/UI/VisionInspection.UI/Services/`）：IOU 跟踪 + EMA 位置平滑 + 确认(confirmHits=2)/保持(maxMissed=4)机制，消除检测框闪烁。
- 仅在推理出新结果时 `Update()`，渲染时读 `GetActiveBoxes()`。

## 渲染管线
- 画面帧管线：相机→`OnCameraImageGrabbed`→每帧用 `_lastSopResult` 在最新帧上叠加渲染→`BeginInvoke` 异步设 `CurrentImage`。
- 推理结果不再覆盖 `CurrentImage`（避免画面退回上一帧）。
- `_lastSopResult` 老化 15 帧后清空。

## 架构要点
- SOP 配方驱动：YAML 声明区域/步骤/物料，引擎通用，切换=换 YAML。
- ViolationType 枚举含 MissingRequiredObject（漏放）。
- ConditionType 枚举含 ObjectPresent/HandInRegion/HandMoveFromTo 等。
- `hand_action` method 转成 `HandMoveFromTo` 条件类型；pickup=手握 target_object，putdown=target_object 到达 to_region。
- 模型缺失时静默回退 COCO（yolov8s.onnx）；`SOPModule.ModelWarning` 事件弹窗告警。
