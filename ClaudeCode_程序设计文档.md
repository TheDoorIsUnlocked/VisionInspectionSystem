# VisionInspectionSystem - Claude Code 程序设计文档

## 一、项目概述

### 1.1 项目定位
工业视觉检测系统，用于SOP（标准作业程序）流程监控，主要功能包括：
- 手部姿态检测（21关键点）
- 物体检测
- ROI区域检测
- 实时相机视频流处理

### 1.2 技术栈
- **框架**: .NET 8 + WPF
- **UI库**: HandyControl
- **图像处理**: SkiaSharp
- **AI推理**: ONNX Runtime + YOLO
- **架构模式**: MVVM (CommunityToolkit.Mvvm)

## 二、项目结构

```
VisionInspectionSystem/
├── src/
│   ├── Core/VisionInspection.Core/          # 核心层
│   │   ├── Models/                          # 数据模型
│   │   ├── Services/                        # 核心服务接口
│   │   ├── ViewModels/                      # 基础ViewModel
│   │   └── Interfaces/                      # 接口定义
│   │
│   ├── Modules/
│   │   ├── VisionInspection.Modules.SOP/    # SOP检测模块 ⭐核心
│   │   │   ├── SOPModule.cs                 # SOP主模块
│   │   │   ├── SOPDetectionStarter.cs       # 检测启动器
│   │   │   ├── Services/
│   │   │   │   ├── YoloHandDetectionService.cs      # YOLO手部检测 ⭐核心
│   │   │   │   ├── YoloPoseHandEstimationService.cs # YOLO姿态估计
│   │   │   │   ├── MediaPipeHandDetector.cs         # MediaPipe检测器
│   │   │   │   └── DWPoseHandDetector.cs            # DWPose检测器
│   │   │   └── Models/
│   │   │       ├── HandPoseEstimationModels.cs      # 手部模型定义
│   │   │       └── SOPYamlConfig.cs                 # SOP配置
│   │   │
│   │   ├── VisionInspection.Modules.Detection/      # 通用检测模块
│   │   └── VisionInspection.Modules.Cascade/        # 级联检测模块
│   │
│   └── UI/VisionInspection.UI/              # UI层
│       ├── ViewModels/
│       │   └── MainViewModel.cs             # 主ViewModel ⭐核心
│       ├── Views/
│       │   ├── SOPModuleView.xaml           # SOP模块视图
│       │   ├── SOPConfigWindow.xaml         # SOP配置窗口
│       │   ├── CameraConfigWindow.xaml      # 相机配置窗口
│       │   └── HandDetectionParamsWindow.xaml # 手部检测参数窗口
│       ├── Controls/
│       │   └── ROIEditorControl.xaml.cs     # ROI编辑器控件
│       └── Services/
│           ├── CameraManager.cs             # 相机管理器 ⭐核心
│           └── YoloDetectionService.cs      # YOLO检测服务
│
├── test/SOPTest/                            # 测试项目
└── yolo_models/                             # 模型文件目录
    └── yolo11n-pose-hands.onnx              # 当前使用的模型
```

## 三、核心架构

### 3.1 数据流向
```
相机帧 → CameraManager → MainViewModel.OnCameraImageGrabbed
                              ↓
                    克隆帧 → SOPModule.ProcessAsync
                              ↓
                    YoloHandDetectionService.DetectHands
                              ↓
                    时序平滑 → 绘制骨架 → 显示
```

### 3.2 关键类职责

| 类名 | 职责 | 重要方法 |
|------|------|----------|
| **CameraManager** | 相机生命周期管理 | `Connect()`, `StartGrabbing()`, `ImageGrabbed`事件 |
| **MainViewModel** | 主业务逻辑 | `OnCameraImageGrabbed()`, `PerformSOPDetectionAsync()` |
| **SOPModule** | SOP流程控制 | `ProcessAsync()`, `InitializeAsync()` |
| **YoloHandDetectionService** | 手部检测核心 | `DetectHands()`, `CalculateSmoothedPose()` |
| **ROIEditorControl** | 图像显示和ROI绘制 | `OnPaintSurface()` |

## 四、关键技术实现

### 4.1 手部检测流程（⭐核心）

**文件**: `YoloHandDetectionService.cs`

```csharp
// 1. 模型推理
var results = _yolo.RunInference(processedImage);

// 2. 关键点提取（21点）
var keypoints = ExtractKeypoints(results);

// 3. 时序平滑（双重平滑）
// 第1步：多帧移动平均
float avgX = keypoints.Average(k => k.X);
// 第2步：指数平滑
float smoothedX = _alpha * avgX + (1 - _alpha) * prevKp.X;

// 4. 返回平滑后的手部姿态
return smoothedHandPose;
```

**关键参数**:
```csharp
public int SmoothWindowSize { get; set; } = 5;      // 平滑窗口（帧数）
public float SmoothAlpha { get; set; } = 0.65f;     // 指数平滑系数（0.65=响应更快）
public int InferenceInterval { get; set; } = 1;     // 推理间隔（1=每帧检测）
public int DetectionHoldFrames { get; set; } = 8;   // 检测保持帧数
public float DetectionConfidenceThreshold { get; set; } = 0.15f; // 置信度阈值
```

### 4.2 缓存骨架机制（⭐解决闪烁的关键）

**文件**: `MainViewModel.cs`

```csharp
// 缓存最后一次手部骨架结果
private HandPoseEstimationResult? _lastHandPoseResult;
private int _noHandsFrameCount = 0;
private const int MaxNoHandsFrames = 8;

// 相机帧回调 - 每帧都执行
private void OnCameraImageGrabbed(object? sender, CameraImageData e)
{
    var skBitmap = ConvertCameraImageToSKBitmap(e);
    var inferenceBitmap = skBitmap.Copy();
    
    // 关键：在原始帧上直接绘制缓存的骨架
    if (_lastHandPoseResult != null)
    {
        using var canvas = new SKCanvas(skBitmap);
        DrawHandPoses(canvas, _lastHandPoseResult);
    }
    
    // 显示带骨架的帧
    RoiEditorViewModel.CurrentImage = skBitmap;
    
    // 异步推理，完成后更新缓存
    _inferenceQueue.Writer.TryWrite(inferenceBitmap);
}

// 推理完成后更新缓存
private async Task PerformSOPDetectionAsync(SKBitmap bitmap)
{
    var result = await _sopModule.ProcessAsync(frames);
    
    if (result.HandPoseResult?.Hands.Count > 0)
    {
        _lastHandPoseResult = result.HandPoseResult;
        _noHandsFrameCount = 0;
    }
    else
    {
        _noHandsFrameCount++;
        if (_noHandsFrameCount >= MaxNoHandsFrames)
        {
            _lastHandPoseResult = null; // 清除骨架
        }
    }
}
```

### 4.3 绘制手部骨架

**文件**: `MainViewModel.cs` (DrawHandPoses方法)

```csharp
private void DrawHandPoses(SKCanvas canvas, HandPoseEstimationResult result)
{
    foreach (var hand in result.Hands)
    {
        // 只绘制手腕到5个指尖的连线（简化显示）
        var wrist = hand.Keypoints[0];
        var fingertips = new[] { 4, 8, 12, 16, 20 }; // 5个指尖索引
        
        foreach (var tipIdx in fingertips)
        {
            var tip = hand.Keypoints[tipIdx];
            canvas.DrawLine(wrist.X, wrist.Y, tip.X, tip.Y, linePaint);
            // 绘制指尖点
            canvas.DrawCircle(tip.X, tip.Y, 8, pointPaint);
        }
    }
}
```

## 五、配置文件

### 5.1 SOP配置 (sop_config.yaml)
```yaml
sop_name: "装配流程"
steps:
  - id: 1
    name: "取螺丝"
    description: "从料盒中取出螺丝"
    detection_mode: "UnifiedDetection"  # 统一检测模式
    
detection:
  hand_pose:
    enabled: true
    model_path: "models/yolo11n-pose-hands.onnx"
    confidence_threshold: 0.15
    
  object_detection:
    enabled: false  # 当前已屏蔽
```

### 5.2 相机配置 (camera_config.json)
```json
{
  "LastCameraId": "相机ID",
  "LastCameraSerialNumber": "序列号",
  "ExposureTime": 10000,
  "Gain": 0,
  "ExposureTimeMin": 100,
  "ExposureTimeMax": 100000,
  "GainMin": 0,
  "GainMax": 24
}
```

## 六、关键问题解决记录

### 6.1 已解决的问题

| 问题 | 原因 | 解决方案 |
|------|------|----------|
| 骨架闪烁 | 原始帧/骨架帧交替显示 | 缓存骨架 + 每帧绘制 |
| 跟踪延迟 | 平滑系数过高(0.5) | 调整为0.65，延迟50ms→27ms |
| 骨架残留 | 缓存永不清理 | 8帧无手后清除缓存 |
| 性能卡顿 | DebugLog磁盘I/O | 移除热路径日志调用 |
| 相机窗口问题 | 多次创建窗口实例 | 单例模式复用窗口 |

### 6.2 当前使用的模型
- **模型**: yolo11n-pose-hands.onnx
- **输入**: 640x640 RGB
- **输出**: 21个手部关键点 (x, y, confidence)
- **位置**: `VisionInspectionSystem/yolo_models/`

## 七、开发注意事项

### 7.1 性能优化
1. **禁止在热路径使用文件I/O** - 如DebugLog
2. **使用Channel进行异步队列** - 避免阻塞UI线程
3. **SkiaSharp绘制优化** - 使用缓存的SKPaint对象

### 7.2 内存管理
1. **SKBitmap必须Dispose** - 使用 `using` 语句
2. **相机帧及时释放** - 避免内存泄漏
3. **ONNX模型单例** - 不要重复加载模型

### 7.3 线程安全
1. **UI更新必须在UI线程** - 使用 `Dispatcher.Invoke`
2. **并发集合使用Channel** - 代替BlockingCollection
3. **锁粒度要小** - 避免长时间持有锁

## 八、常用调试技巧

### 8.1 查看手部检测结果
```csharp
// 在PerformSOPDetectionAsync中添加断点
if (sopResult.HandPoseResult?.Hands.Count > 0)
{
    var hand = sopResult.HandPoseResult.Hands[0];
    // 检查hand.Keypoints[0]手腕位置是否合理
}
```

### 8.2 性能分析
```csharp
// 测量推理耗时
var sw = Stopwatch.StartNew();
var result = await _sopModule.ProcessAsync(frames);
Debug.WriteLine($"推理耗时: {sw.ElapsedMilliseconds}ms");
```

### 8.3 可视化调试
- ROIEditorControl支持鼠标滚轮缩放
- 右键拖动平移图像
- 双击重置视图

## 九、待办事项（可能需要继续优化）

1. [ ] **多手跟踪** - 当前双手可能ID混乱
2. [ ] **遮挡处理** - 手被遮挡时的预测
3. [ ] **GPU加速** - 确认CUDA是否生效
4. [ ] **模型量化** - INT8量化提升速度
5. [ ] **SOP流程编辑器** - 可视化配置流程

## 十、Git提交历史

```
c2da814 修复手部跟踪延迟和骨架残留问题
- 移除无效的 PredictHandPoses 速度预测方法
- 添加 _noHandsFrameCount 计数器
- 移除热路径中的所有 DebugLog 调用
- 优化平滑参数: SmoothAlpha 0.5→0.65

f28fcc8 修复手部检测闪烁问题 - 实现缓存骨架绘制机制
- 添加 _lastHandPoseResult 缓存字段
- 重构 OnCameraImageGrabbed 绘制流程
- 每帧都显示带骨架的画面
```

---

**文档生成时间**: 2026-05-28  
**最后更新**: 2026-05-28  
**项目状态**: 手部检测功能已可用，SOP流程框架搭建完成
