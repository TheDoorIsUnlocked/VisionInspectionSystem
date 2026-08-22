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

## VS 设计器 XDG0003 / XDG0010（Button 样式丢失）约定（重要！）
- **根因**：主程序集 `VisionInspection.UI` 引用了 OnnxRuntime/OpenCvSharp 原生库/相机SDK。设计器实例化 converter（CLR 类型）需加载该 DLL → 连带原生依赖在设计师沙箱加载失败 → 报 XDG0003；资源若放在独立 `ResourceDictionary.xaml` 文件里，该文件单独加载失败会连锁 XDG0010（找不到 converter/样式、按钮无样式）。
- **约定（已落地并验证）**：
  1. **设计期需实例化的 CLR 类型（IValueConverter/IMultiValueConverter 等）必须放在独立轻量程序集 `VisionInspection.Converters`**（`src/UI/VisionInspection.Converters/`，net8.0-windows+UseWPF，**无原生依赖**），命名空间保持 `VisionInspection.UI.Converters`、AssemblyName=`VisionInspection.Converters`。XAML 用 `clr-namespace:VisionInspection.UI.Converters;assembly=VisionInspection.Converters` 引用。
  2. **converter 实例 + 按钮样式（ButtonPrimary/Success/Danger/Info）等要内联进 `App.xaml` 的 `Application.Resources`**（全局资源，设计期最稳）。**不要**把资源放到"同项目内的独立 `ResourceDictionary.xaml` 文件 + App.xaml 用 `MergedDictionaries` 引用"——这种独立字典属于带原生依赖的 UI 项目，设计器单独加载它时常失败，导致 MainWindow 找不到里面的所有 converter/样式（连锁 XDG0010、按钮无样式）。`SharedResources.xaml` 已删除。
  3. 新增/修改 converter：加到 `VisionInspection.Converters` 工程（一个 .cs 文件可定义多类，见 `BooleanToVisibilityConverter.cs` 内含 BooleanToVisibilityConverter/InverseBooleanToVisibilityConverter/BooleanAndConverter[多值]/Boolean2BooleanReConverter），不要放回主工程。
  4. 用户"git 恢复"诉求的最佳实践：**不整体 revert（会丢功能），而是把资源组织方式恢复到 git 里验证过的结构**（如 `fa2e01b^` 的内联 App.xaml），同时保留后续功能与新增资源。
  5. **删除 ResourceDictionary 文件后的致命坑（已踩过）**：只改 App.xaml 并删文件不够——**每个 xaml 的 `<Window.Resources>`/`<UserControl.Resources>` 里若用 `MergedDictionaries Source="...该文件"` 引用它，必须同步删掉这段本地引用**，否则运行时报 `XDG-0001 查找资源字典失败` → 整窗加载崩溃、程序直接起不来，并连锁所有 app 级资源"找不到"。排查时不要只信一次性 grep 结论，删文件后要逐一确认并立即 `dotnet build` 验证无"资源字典找不到"类错误。

## 运行时资源/模型目录定位约定（重要！）
- **绝不要用 `Path.Combine(BaseDirectory, ".."*N)` 硬编码回退层数来定位"项目根下的目录"**（如 `yolo_models`）。本会话实测：固定 5 层 `..` 从 `bin\Debug\net8.0-windows\` 回退会**少算一层**，落到 `E:\yolo\YoloDotNet-master\yolo_models`（只有 3 个无关模型），漏掉真正在 `VisionInspectionSystem\yolo_models\` 的 14 个标准 YOLO onnx → 模型扫不到、对话框无模型可选、"无法加载模型"。
- **正确做法**：`FindYoloModelsDirectories(startDir)` 从 `AppDomain.CurrentDomain.BaseDirectory` 向上逐级遍历祖先目录，收集所有含 `yolo_models` 子目录的路径（去重、由近及远）。这样无论调试/发布目录深度如何都正确。
- **"无法加载模型"排查优先级**：① db 是否空（`models.db` 不被 git 跟踪，程序长期崩会导致从未导入）；② 模型目录是否真的被扫到（用上述探测法）；③ VS 输出窗口的 `System.Text.Json` first-chance 异常通常只是 `ExtractClassNames` 解析 onnx 元数据的噪声，被 `catch{}` 兜底，**不是真因**，别被带偏。

## 手部检测模型路径约定（重要！）
- **`hand_landmark_sparse_Nx3x224x224.onnx`**（MediaPipe 手指精修）真正位于 **`VisionInspectionSystem/models/`（小写 `models/`）**，以及 `src/UI/VisionInspection.UI/models/` 和 bin 输出 `bin/Debug/net8.0-windows/models/`。**不是**上层 `E:\yolo\YoloDotNet-master\Models\`（大写 M，那是父项目的模型目录，只有 yolov8s-pose 等被 csproj 链接进来的）。
- **`yolov8s-pose.onnx`**（YOLOv8-pose 找手腕）由 csproj 从 `..\..\..\..\Models\yolov8s-pose.onnx` 链接进输出 `Models\yolov8s-pose.onnx`（大写 M）。
- 排查时**注意大小写 `models/` vs `Models/` 是不同目录**，别查错路径误以为模型缺失。
- 手部检测后端枚举 `HandDetectionBackend`：`Auto`(MediaPipe→YOLO→YoloPose→DWPose) / `MediaPipe` / `Yolo` / `DWPose` / `YoloPose`(新增，默认)。`sop_config.json` 的 `HandDetectionBackend` 字段控制；改完需重启程序（DLL/配置被运行进程锁定）。
- **YoloPose 局限**：首阶段仍依赖 yolov8-pose 检测手腕，手臂极度外伸/远距/遮挡时手腕可能漏检 → 仍会识别不到手。彻底方案是专用手部检测器（MediaPipe palm_detection / yolov8n-hand.onnx），项目目前缺失这些模型。

## SOP 音频播放约定（ng.wav / ok.wav，重要！）
- **绝不要用 `System.Media.SoundPlayer` 播放音频文件**：它仅支持 8/16-bit PCM，24-bit PCM（原 ng.wav 就是 24-bit/44100Hz/立体声）会**静默失败**（Play() 异步、错误发生在内部线程、不抛到调用方）→ 表现为"不响且无任何报错"。
- **正确做法**：用 **NAudio**（`WaveOutEvent` + `AudioFileReader`，底层 WaveOut/Media Foundation）播放，原生支持任意位深 PCM（8/16/24/32-bit）与 IEEE float，无需预先转格式。
- 实现落点：`MainViewModel.cs` 的 `_sopAudioCache`（WaveOutEvent+AudioFileReader 元组字典，常驻缓存不释放）与 `PlaySopSound`（重复播放 `reader.Position=0` 后 `Play()`，正在播放先 `Stop()` 重播避免叠加）。`scripts/convert_wav_to_16bit.py` 已不再必需，仅作历史遗留。
- 引入 NAudio 需在 `VisionInspection.UI.csproj` 加 `PackageReference Include="NAudio"`（已加 2.2.1）；用户须在 VS 还原并重新生成。
- 替换音源（ng.wav/ok.wav，项目根 `..\..\..\ng.wav`）后需重新生成（csproj PreserveNewest 拷贝），否则 bin 副本仍是旧文件（PreserveNewest 时间戳陷阱）。
