# Claude Code 开发攻略 - VisionInspectionSystem

## 一、项目初始化建议

### 1.1 首次启动时的关键指令

```bash
# 1. 先让Claude Code了解项目结构
@Claude 请阅读以下文档了解项目：
- VisionInspectionSystem/ClaudeCode_程序设计文档.md
- VisionInspectionSystem/产品需求文档_SOP-AI作业监控系统.md
- VisionInspectionSystem/SOP手部检测调试经验总结.md

# 2. 确认当前代码状态
@Claude 请分析当前项目状态，列出：
- 已完成功能
- 待实现功能
- 当前存在的问题

# 3. 制定开发计划
@Claude 根据需求文档，制定下一步开发计划，优先级排序
```

### 1.2 推荐的Context设置

在 `.claude/settings.local.json` 中配置：
```json
{
  "context": {
    "project_type": "WPF .NET 8 Industrial Vision System",
    "key_files": [
      "src/UI/VisionInspection.UI/ViewModels/MainViewModel.cs",
      "src/Modules/VisionInspection.Modules.SOP/Services/YoloHandDetectionService.cs",
      "src/UI/VisionInspection.UI/Services/CameraManager.cs"
    ],
    "architecture": "MVVM with CommunityToolkit.Mvvm",
    "ui_framework": "WPF + HandyControl + SkiaSharp"
  }
}
```

---

## 二、高效开发技巧

### 2.1 任务分解策略

**不要一次性给太多任务**，按这个粒度拆分：

```
❌ 错误："实现整个SOP流程监控功能"

✅ 正确：
1. "在SOPModuleView右侧添加8步SOP步骤列表UI"
2. "实现步骤状态枚举和当前步骤跟踪逻辑"
3. "添加步骤完成检测的回调机制"
4. "将步骤状态与UI绑定显示"
```

### 2.2 代码修改原则

**每次修改后必须验证**：
```bash
# 修改后立即编译
@Claude 编译项目，确保没有错误

# 关键修改后运行测试
@Claude 运行测试，验证功能正常
```

### 2.3 调试技巧

**遇到问题时，提供完整信息**：
```
❌ 错误："程序崩溃了"

✅ 正确：
"运行SOP检测时程序崩溃，错误信息：
- 异常类型：System.NullReferenceException
- 发生位置：MainViewModel.cs:line 245
- 堆栈跟踪：[完整堆栈]
- 复现步骤：1.启动程序 2.点击运行 3.等待3秒后崩溃"
```

---

## 三、核心模块开发指南

### 3.1 添加新功能的标准流程

以"添加SOP步骤面板"为例：

```bash
# Step 1: 创建/修改Model
@Claude 在 VisionInspection.Modules.SOP/Models 下创建 SOPStep.cs
包含：StepId, StepName, Description, Status, CompletedTime

# Step 2: 创建/修改ViewModel
@Claude 在 SOPModuleViewModel 中添加 Steps 集合和 CurrentStep 属性

# Step 3: 修改View
@Claude 在 SOPModuleView.xaml 右侧添加步骤列表面板

# Step 4: 连接逻辑
@Claude 在 SOPModule 中添加步骤状态流转逻辑

# Step 5: 测试验证
@Claude 编译并运行，验证步骤面板显示正常
```

### 3.2 UI开发建议

**WPF布局问题**：
```
问题：控件不显示或布局错乱
解决：
@Claude 请检查SOPModuleView.xaml的Grid布局，
确保右侧步骤面板的ColumnDefinition宽度设置正确，
并检查Binding路径是否正确
```

**样式问题**：
```
问题：颜色/字体不符合设计规范
解决：
@Claude 根据产品需求文档中的颜色规范，
将步骤完成状态的颜色改为 #4CAF50（绿色），
当前步骤高亮背景改为 #2196F3（蓝色）
```

### 3.3 AI模型集成建议

**添加新的物体检测模型**：
```bash
# 1. 先确认模型格式
@Claude 检查 yolo_models/ 目录下的模型文件格式

# 2. 创建检测服务
@Claude 参考 YoloHandDetectionService.cs，
创建 ObjectDetectionService.cs 用于检测包装物料

# 3. 定义检测类别
@Claude 创建 ObjectType 枚举：
- InstructionManual（说明书）
- PhoneCase（手机壳）
- SealedBag（密实袋）
- Phone（手机）
- Charger（充电器）
- Box（包装盒）
- BoxLid（盒盖）
- PackagingPaper（包装纸）

# 4. 集成到SOPModule
@Claude 在 SOPModule.ProcessAsync 中调用物体检测
```

---

## 四、常见问题解决

### 4.1 编译错误

**错误 CS0246: 找不到类型或命名空间**：
```bash
@Claude 在 VisionInspection.Modules.SOP.csproj 中添加缺失的引用，
确保引用了 VisionInspection.Core 项目
```

**错误 XLS0414: 资源字典找不到**：
```bash
@Claude 检查 App.xaml 中的资源字典引用路径，
确保 HandyControl 主题资源正确加载
```

### 4.2 运行时错误

**相机连接失败**：
```bash
@Claude 检查 CameraManager.Connect() 方法，
确认是否正确处理了相机连接异常，
添加更详细的错误日志输出
```

**手部检测不工作**：
```bash
@Claude 检查以下几点：
1. YoloHandDetectionService 是否正确初始化
2. 模型文件路径是否正确（yolo_models/yolo11n-pose-hands.onnx）
3. 相机图像是否正确传递到检测服务
4. 在 DetectHands 方法中添加调试日志
```

**内存泄漏**：
```bash
@Claude 检查 SKBitmap 的 Dispose 情况，
确保所有创建的 SKBitmap 都使用了 using 语句或在 finally 中释放
```

### 4.3 性能问题

**界面卡顿**：
```bash
@Claude 分析 MainViewModel.OnCameraImageGrabbed 方法，
检查是否有耗时操作阻塞UI线程，
考虑使用 Channel 进行异步处理
```

**检测延迟高**：
```bash
@Claude 检查 YoloHandDetectionService 的推理耗时，
确认：
1. 是否使用了GPU加速（CUDA）
2. 模型输入尺寸是否过大
3. 是否有过多的后处理操作
```

---

## 五、Git工作流建议

### 5.1 提交规范

```bash
# 功能提交
@Claude 提交代码，消息："feat: 添加SOP步骤列表面板

- 在SOPModuleView右侧添加8步列表UI
- 实现步骤状态枚举（Pending/Active/Completed）
- 添加步骤完成回调机制"

# 修复提交
@Claude 提交代码，消息："fix: 修复相机窗口重复打开问题

- 使用单例模式复用CameraConfigWindow
- 窗口关闭时取消事件订阅
- 防止多个窗口实例订阅同一事件"

# 优化提交
@Claude 提交代码，消息："perf: 优化手部检测性能

- 移除热路径DebugLog磁盘I/O
- 调整平滑参数减少延迟
- 延迟从50ms降至27ms"
```

### 5.2 分支策略

```bash
# 新功能开发
@Claude 创建分支 feature/sop-step-panel
实现SOP步骤面板功能
完成后合并到master

# Bug修复
@Claude 创建分支 fix/camera-window-issue
修复相机窗口问题
完成后合并到master
```

---

## 六、测试验证清单

### 6.1 功能测试

```bash
# 手部检测测试
@Claude 验证以下场景：
1. 单手检测是否正常
2. 双手同时检测是否正常
3. 手部快速移动时跟踪是否流畅
4. 手部离开画面后骨架是否正确清除

# SOP流程测试
@Claude 验证以下场景：
1. 步骤列表是否正确显示8步
2. 当前步骤是否正确高亮
3. 步骤完成后是否显示OK标记
4. 全部完成后是否显示PASS

# 相机测试
@Claude 验证以下场景：
1. 相机是否能正常连接
2. 多次打开配置窗口是否正常
3. 相机断开后是否能自动重连
```

### 6.2 性能测试

```bash
@Claude 测试以下性能指标：
1. 手部检测帧率是否≥30 FPS
2. 检测延迟是否<30ms
3. 内存占用是否稳定（无泄漏）
4. CPU占用是否合理
```

---

## 七、与Claude Code沟通的最佳实践

### 7.1 有效的指令格式

```
✅ 好的指令：
"在SOPModuleView.xaml的Grid右侧添加一个宽度为250的步骤列表面板，
包含一个ListView显示8个步骤，每个步骤项显示序号、名称和状态图标。
参考HandDetectionParamsWindow.xaml的样式设计。"

❌ 差的指令：
"做个步骤列表"
```

### 7.2 提供上下文

```
✅ 好的上下文：
"当前SOPModule使用UnifiedDetection模式，
在ProcessAsync方法中同时调用手部检测和物体检测。
现在需要在检测结果返回后，根据检测到的物体类型判断当前步骤是否完成。"

❌ 差的上下文：
"怎么判断步骤完成"
```

### 7.3 逐步确认

```bash
# 修改前确认
@Claude 我计划修改MainViewModel.cs添加步骤状态跟踪，
这个方案是否可行？

# 修改后确认
@Claude 已完成步骤面板UI添加，请检查代码是否符合MVVM规范，
特别是数据绑定部分是否正确？
```

---

## 八、重要文件速查

| 文件 | 用途 | 修改频率 |
|-----|------|---------|
| MainViewModel.cs | 主业务逻辑 | 高 |
| SOPModule.cs | SOP流程控制 | 高 |
| YoloHandDetectionService.cs | 手部检测 | 中 |
| SOPModuleView.xaml | SOP模块UI | 高 |
| CameraManager.cs | 相机管理 | 低 |
| ROIEditorControl.xaml.cs | 图像显示 | 中 |

---

## 九、参考资源

### 9.1 项目文档
- `ClaudeCode_程序设计文档.md` - 技术架构
- `产品需求文档_SOP-AI作业监控系统.md` - 产品需求
- `SOP手部检测调试经验总结.md` - 调试经验

### 9.2 外部资源
- YOLO11n-pose-hands模型文档
- SkiaSharp API文档
- HandyControl控件文档
- ONNX Runtime文档

---

## 十、紧急联系

遇到以下情况立即寻求帮助：
1. 编译错误无法解决
2. 运行时崩溃找不到原因
3. 性能问题严重影响使用
4. 数据丢失或损坏

---

**祝开发顺利！** 🚀
