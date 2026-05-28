# VisionInspectionSystem 项目记忆

## 当前状态
- **最后更新**: 2025-01-XX
- **Git提交**: 245016e - 修复相机配置界面卡顿问题，添加图像水平翻转功能，支持SOP YAML工作流配置

## 已完成功能

### 1. 相机配置界面优化 ✅
- 修复了打开配置界面时相机卡顿的问题
- 实现了智能事件订阅（窗口隐藏时取消订阅）
- 限制预览更新频率为10fps，避免资源竞争
- 修复了图像颜色偏蓝问题（RGB/BGR格式匹配）

### 2. 图像水平翻转功能 ✅
- 添加 `IsImageFlippedHorizontally` 属性到 ROIEditorViewModel
- 在 ROIEditorControl 中实现水平翻转渲染
- 在 MainWindow 工具栏添加 "↔️" 切换按钮
- 解决180度旋转后左右手相反的问题

### 3. SOP YAML工作流配置 ✅
- SOPModuleView 支持从 YAML 文件加载步骤
- 自动搜索 `configs/sop/*.yaml` 文件
- MainViewModel 根据 YAML 路径启动对应工作流
- 添加 sop_assembly_pose.yaml 和 sop_steering_housing.yaml

## 待办任务 (Tasks)

### 高优先级
1. [ ] 创建符合PRD的手机包装8步SOP YAML配置
2. [ ] 取消注释物体检测代码并接入SOP状态机
3. [ ] 实现步骤完成时的历史缩略图截图功能
4. [ ] YoloHandDetectionService 实现左右手区分

### 中优先级
5. [ ] 优化相机配置界面UI布局
6. [ ] 添加更多相机参数调节选项

## 关键文件位置

### 相机相关
- `src/UI/VisionInspection.UI/Views/CameraConfigWindow.xaml.cs` - 相机配置窗口
- `src/UI/VisionInspection.UI/Services/WebCameraService.cs` - 摄像头服务
- `src/Core/VisionInspection.Core/Services/CameraManager.cs` - 相机管理器

### 图像处理
- `src/Core/VisionInspection.Core/ViewModels/ROIEditorViewModel.cs` - ROI编辑器VM
- `src/UI/VisionInspection.UI/Controls/ROIEditorControl.xaml.cs` - ROI编辑器控件

### SOP工作流
- `src/UI/VisionInspection.UI/Views/SOPModuleView.xaml.cs` - SOP模块视图
- `src/UI/VisionInspection.UI/ViewModels/MainViewModel.cs` - 主视图模型
- `configs/sop/` - YAML配置文件目录

## 技术要点

### 相机图像格式
- WebCameraService 将 BGR 转换为 RGB
- CameraConfigWindow 使用 PixelFormats.Rgb24 显示

### 事件订阅模式
- 使用 `IsVisibleChanged` 智能管理事件订阅
- 窗口隐藏时自动取消订阅，避免性能问题

### YAML工作流格式
```yaml
workflow_name: "工作流名称"
steps:
  - id: 1
    name: "步骤名称"
    description: "步骤描述"
    order: 1
```

## 常用命令
```bash
# 编译
dotnet build src/UI/VisionInspection.UI/VisionInspection.UI.csproj

# 运行
dotnet run --project src/UI/VisionInspection.UI/VisionInspection.UI.csproj

# Git提交
git add -A
git commit -m "描述"
```

## 注意事项
1. 相机配置窗口关闭时只是 Hide()，不是真正关闭
2. 主窗口和配置窗口共享 CameraManager 单例
3. 图像数据是共享的 byte[]，需要复制后再处理
