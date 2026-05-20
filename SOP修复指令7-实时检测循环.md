# SOP检测系统修复指令7：接入实时相机循环 + 完整运行流程

> **项目路径**: `E:\yolo\YoloDotNet-master\VisionInspectionSystem`
> **生成日期**: 2026-05-15
> **优先级**: P0（不改的话 SOP 检测只能跑一帧假图，无法实际使用）

---

## 一、当前问题总览

### 问题全景图

```
当前代码中有两套独立的检测系统，互不相连：

系统A：实时相机检测（能跑，但不走SOP）
  相机采集 → OnCameraImageGrabbed → YoloDetectionService → 画框显示
  ✅ 实时逐帧
  ✅ 接了真实相机
  ❌ 不走 SOP 状态机，不判步骤

系统B：SOP检测（有状态机，但只跑一帧假图）
  点按钮 → RunSOPDetectionAsync → CreateTestImage() → SOPModule.ProcessAsync → 显示结果
  ✅ 有完整状态机（步骤判定/违规检测/区域检测）
  ❌ 只执行一次，不循环
  ❌ 用的是 640x480 蓝色矩形+红色圆形假图
  ❌ 不接相机
```

### 修复目标

```
修复后：SOP 检测挂到实时相机循环上

  相机采集 → OnCameraImageGrabbed
                ↓
        IsSOPDetecting?
          ├── Yes → SOPModule.ProcessAsync(真实相机帧)
          │           ├── YOLO 推理
          │           ├── 状态机步骤判定
          │           ├── 违规检测
          │           ├── 画框 + 步骤信息叠加显示
          │           └── 更新 SOPModuleView（步骤进度条）
          │
          └── No  → IsRealTimeDetecting?
                      ├── Yes → YoloDetectionService（现有通用检测）
                      └── No  → 只显示原始画面
```

---

## 二、修改文件清单

| 文件 | 改动类型 | 说明 |
|------|---------|------|
| `MainViewModel.cs` | 新增字段+新增方法+修改回调 | SOP实时检测循环 |
| `MainWindow.xaml` | 新增菜单项 | SOP启动/停止按钮 |
| `SOPModuleView.xaml.cs` | 改造 | 从模拟改为接收真实SOP事件 |
| `configs/sop_config.json` | 新建 | 默认SOP配置文件 |

---

## 三、修改步骤

### 修复7.1：MainViewModel 新增 SOP 实时检测

**文件**: `src\UI\VisionInspection.UI\ViewModels\MainViewModel.cs`

#### 步骤1：新增字段（在现有字段区域添加）

找到这段代码：
```csharp
    [ObservableProperty]
    private bool _isRealTimeDetecting = false;
```

在其**下方**添加：
```csharp
    [ObservableProperty]
    private bool _isSOPDetecting = false;

    [ObservableProperty]
    private string _sopWorkflowPath = "";

    private bool _isSOPProcessingFrame = false;
    private readonly SemaphoreSlim _sopInferenceLock = new(1, 1);
```

#### 步骤2：修改 OnCameraImageGrabbed 回调，接入 SOP

找到这段代码：
```csharp
    private async void OnCameraImageGrabbed(object? sender, CameraImageData e)
    {
        // 检查应用程序是否仍在运行
        if (System.Windows.Application.Current == null || _isDisposed)
            return;
            
        // 将相机图像转换为 SKBitmap 并显示
        var skBitmap = ConvertCameraImageToSKBitmap(e);
        if (skBitmap != null)
        {
            // 在UI线程更新图像
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                // 再次检查，防止在Invoke执行前程序已关闭
                if (!_isDisposed && RoiEditorViewModel != null)
                {
                    RoiEditorViewModel.CurrentImage = skBitmap;
                }
            });
            
            // 如果启用了实时检测，执行YOLO推理
            if (IsRealTimeDetecting && _detectionService.IsInitialized && !_isProcessingFrame)
            {
                await PerformRealTimeDetectionAsync(skBitmap);
            }
        }
    }
```

替换为：
```csharp
    private async void OnCameraImageGrabbed(object? sender, CameraImageData e)
    {
        // 检查应用程序是否仍在运行
        if (System.Windows.Application.Current == null || _isDisposed)
            return;
            
        // 将相机图像转换为 SKBitmap 并显示
        var skBitmap = ConvertCameraImageToSKBitmap(e);
        if (skBitmap != null)
        {
            // 在UI线程更新图像
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                if (!_isDisposed && RoiEditorViewModel != null)
                {
                    RoiEditorViewModel.CurrentImage = skBitmap;
                }
            });
            
            // ⭐ 优先走 SOP 实时检测
            if (IsSOPDetecting && _sopModule != null && !_isSOPProcessingFrame)
            {
                await PerformSOPDetectionAsync(skBitmap);
            }
            // 否则走通用实时检测
            else if (IsRealTimeDetecting && _detectionService.IsInitialized && !_isProcessingFrame)
            {
                await PerformRealTimeDetectionAsync(skBitmap);
            }
        }
    }
```

#### 步骤3：新增 SOP 实时检测方法

在 `PerformRealTimeDetectionAsync` 方法**下方**添加：

```csharp
    /// <summary>
    /// 执行 SOP 实时检测（每帧调用）
    /// </summary>
    private async Task PerformSOPDetectionAsync(SKBitmap bitmap)
    {
        // 使用信号量防止并发
        if (!await _sopInferenceLock.WaitAsync(0))
            return;

        try
        {
            _isSOPProcessingFrame = true;

            if (_sopModule == null) return;

            // 构建帧数据
            var frames = new Dictionary<string, CaptureFrame>
            {
                ["main_camera"] = new CaptureFrame
                {
                    CameraId = "main_camera",
                    Image = bitmap,
                    Timestamp = DateTime.Now,
                    FrameNumber = _frameCount++
                }
            };

            // 执行 SOP 检测
            var result = await _sopModule.ProcessAsync(frames);

            if (result is SOPModuleResult sopResult)
            {
                // 在 UI 线程更新
                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    if (_isDisposed) return;

                    // 更新状态栏
                    SopStatus = sopResult.StepResults.Message;
                    CurrentStep = sopResult.StepResults.CurrentStep;
                    TotalSteps = sopResult.StepResults.TotalSteps;

                    // 更新检测结果列表
                    DetectionResults = sopResult.Detections
                        .Select(d => new DetectedObject
                        {
                            ClassName = d.Label?.Name ?? "unknown",
                            Confidence = (float)d.Confidence,
                            BoundingBox = new float[]
                            {
                                d.BoundingBox.Left,
                                d.BoundingBox.Top,
                                d.BoundingBox.Width,
                                d.BoundingBox.Height
                            },
                            PixelBoundingBox = d.BoundingBox
                        })
                        .ToList();

                    HighConfidenceCount = DetectionResults.Count(o => o.Confidence >= 0.5);

                    // 绘制检测框 + SOP 步骤信息
                    var resultBitmap = DrawSOPDetectionResults(bitmap, sopResult);
                    if (resultBitmap != null && RoiEditorViewModel != null)
                    {
                        RoiEditorViewModel.CurrentImage = resultBitmap;
                    }

                    // 计算推理 FPS
                    _inferenceFrameCount++;
                    var elapsed = DateTime.Now - _lastInferenceTime;
                    if (elapsed.TotalSeconds >= 1)
                    {
                        InferenceFps = _inferenceFrameCount / elapsed.TotalSeconds;
                        _inferenceFrameCount = 0;
                        _lastInferenceTime = DateTime.Now;
                    }

                    Status = $"SOP检测中 | 步骤 {CurrentStep}/{TotalSteps} | " +
                             $"检测到 {DetectionResults.Count} 个对象 | " +
                             $"FPS: {InferenceFps:F1} | " +
                             $"耗时: {sopResult.ElapsedMs}ms";
                });
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"SOP实时检测异常: {ex.Message}");
        }
        finally
        {
            _isSOPProcessingFrame = false;
            _sopInferenceLock.Release();
        }
    }

    /// <summary>
    /// 绘制 SOP 检测结果（检测框 + 步骤状态 + 违规警告）
    /// </summary>
    private SKBitmap? DrawSOPDetectionResults(SKBitmap sourceBitmap, SOPModuleResult sopResult)
    {
        try
        {
            var resultBitmap = sourceBitmap.Copy();
            using var canvas = new SKCanvas(resultBitmap);

            // 1. 绘制检测框
            foreach (var detection in sopResult.Detections)
            {
                var label = detection.Label?.Name ?? "unknown";
                var conf = detection.Confidence;
                var box = detection.BoundingBox;

                // 框颜色：高置信度绿色，低置信度黄色
                var color = conf > 0.7 ? SKColors.LimeGreen :
                           conf > 0.5 ? SKColors.Yellow : SKColors.Orange;

                using var boxPaint = new SKPaint
                {
                    Color = color,
                    StrokeWidth = 3,
                    IsAntialias = true,
                    Style = SKPaintStyle.Stroke
                };
                canvas.DrawRect(box, boxPaint);

                // 标签
                var labelText = $"{label} {conf * 100:F0}%";
                using var textPaint = new SKPaint
                {
                    Color = SKColors.White,
                    TextSize = 18,
                    IsAntialias = true,
                    FakeBoldText = true
                };
                using var bgPaint = new SKPaint
                {
                    Color = color.WithAlpha(200),
                    Style = SKPaintStyle.Fill
                };

                var textBounds = new SKRect();
                textPaint.MeasureText(labelText, ref textBounds);
                canvas.DrawRect(box.Left, box.Top - textBounds.Height - 6, textBounds.Width + 8, textBounds.Height + 6, bgPaint);
                canvas.DrawText(labelText, box.Left + 4, box.Top - 4, textPaint);
            }

            // 2. 顶部状态栏（半透明黑底）
            using var headerBg = new SKPaint { Color = new SKColor(0, 0, 0, 180), Style = SKPaintStyle.Fill };
            canvas.DrawRect(0, 0, resultBitmap.Width, 50, headerBg);

            using var headerText = new SKPaint
            {
                Color = SKColors.White,
                TextSize = 22,
                IsAntialias = true,
                FakeBoldText = true
            };

            var stepName = sopResult.StepResults.Message ?? "等待";
            var stepInfo = $"SOP 步骤 {sopResult.StepResults.CurrentStep}/{sopResult.StepResults.TotalSteps}: {stepName}";
            canvas.DrawText(stepInfo, 15, 35, headerText);

            // 右上角显示 PASS/NG
            var passText = sopResult.IsPass ? "PASS" : "NG";
            var passColor = sopResult.IsPass ? SKColors.LimeGreen : SKColors.Red;
            using var passPaint = new SKPaint
            {
                Color = passColor,
                TextSize = 28,
                IsAntialias = true,
                FakeBoldText = true
            };
            var passWidth = passPaint.MeasureText(passText);
            canvas.DrawText(passText, resultBitmap.Width - passWidth - 15, 38, passPaint);

            // 3. 违规警告（如果有）
            if (sopResult.Violations.Count > 0)
            {
                var lastViolation = sopResult.Violations.Last();
                using var warnBg = new SKPaint { Color = new SKColor(255, 0, 0, 160), Style = SKPaintStyle.Fill };
                canvas.DrawRect(0, resultBitmap.Height - 50, resultBitmap.Width, 50, warnBg);

                using var warnText = new SKPaint
                {
                    Color = SKColors.White,
                    TextSize = 20,
                    IsAntialias = true,
                    FakeBoldText = true
                };
                canvas.DrawText($"⚠ 违规: {lastViolation.Description}", 15, resultBitmap.Height - 18, warnText);
            }

            return resultBitmap;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"绘制SOP结果异常: {ex.Message}");
            return null;
        }
    }
```

#### 步骤4：新增 SOP 启动/停止命令

在 `StopRealTimeDetection` 方法**下方**添加：

```csharp
    #region SOP实时检测

    /// <summary>
    /// 初始化并启动 SOP 实时检测
    /// </summary>
    [RelayCommand]
    public async Task StartSOPDetectionAsync()
    {
        try
        {
            IsBusy = true;

            // 1. 检查相机
            if (!_cameraManager.IsConnected)
            {
                MessageBox.Show("请先连接相机", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!_cameraManager.IsGrabbing)
            {
                MessageBox.Show("请先开始相机采集", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 2. 选择 YAML 工作流文件
            if (string.IsNullOrEmpty(SopWorkflowPath))
            {
                var openFileDialog = new OpenFileDialog
                {
                    Title = "选择 SOP 工作流配置文件",
                    Filter = "YAML文件|*.yaml;*.yml|JSON文件|*.json|所有文件|*.*",
                    InitialDirectory = System.IO.Path.Combine(
                        AppDomain.CurrentDomain.BaseDirectory, "configs", "sop")
                };

                if (openFileDialog.ShowDialog() != true)
                {
                    Status = "取消选择 SOP 配置";
                    return;
                }
                SopWorkflowPath = openFileDialog.FileName;
            }

            Status = "正在初始化 SOP 模块...";

            // 3. 初始化 SOP 模块（如果还没初始化）
            if (_sopModule == null)
            {
                var config = new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        // 模型路径由 YAML 工作流指定，这里给个 fallback
                        ["SOPModule:ModelPath"] = "yolo_models/yolov8s.onnx",
                        ["SOPModule:UseGpu"] = "true",
                        ["SOPModule:ConfidenceThreshold"] = "0.6",
                        ["SOPModule:IouThreshold"] = "0.45"
                    })
                    .Build();

                _sopModule = new SOPModule();
                await _sopModule.InitializeAsync(config, _cameraManager.CurrentCameraService!);

                // 订阅事件
                _sopModule.StepChanged += OnSOPStepChanged;
                _sopModule.ViolationDetected += OnSOPViolationDetected;
                _sopModule.WorkflowCompleted += OnSOPWorkflowCompleted;
            }

            // 4. 加载 YAML 工作流
            Status = $"正在加载工作流: {System.IO.Path.GetFileName(SopWorkflowPath)}...";
            _sopModule.StartWorkflowFromYaml(SopWorkflowPath);

            // 5. 启动实时检测
            IsSOPDetecting = true;
            IsRealTimeDetecting = false; // 关闭通用检测，避免冲突
            _inferenceFrameCount = 0;
            _lastInferenceTime = DateTime.Now;

            SopStatus = "SOP 实时检测运行中";
            Status = $"SOP 实时检测已启动 | 工作流: {System.IO.Path.GetFileName(SopWorkflowPath)}";
        }
        catch (Exception ex)
        {
            SopStatus = $"启动失败: {ex.Message}";
            Status = $"SOP 启动错误: {ex.Message}";
            MessageBox.Show($"SOP 启动失败:\n{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 停止 SOP 实时检测
    /// </summary>
    [RelayCommand]
    public void StopSOPDetection()
    {
        IsSOPDetecting = false;
        _sopModule?.StopWorkflow();
        InferenceFps = 0;
        SopStatus = "SOP 检测已停止";
        Status = "SOP 检测已停止";
    }

    /// <summary>
    /// 重置当前 SOP 工作流（从第一步重新开始）
    /// </summary>
    [RelayCommand]
    public void ResetSOPWorkflow()
    {
        _sopModule?.ResetWorkflow();
        SopStatus = "SOP 工作流已重置";
        Status = "SOP 工作流已重置，从第一步重新开始";
    }

    /// <summary>
    /// 切换 SOP 工作流（选择另一个 YAML）
    /// </summary>
    [RelayCommand]
    public async Task SwitchSOPWorkflowAsync()
    {
        // 先停止当前检测
        var wasDetecting = IsSOPDetecting;
        if (wasDetecting) StopSOPDetection();

        // 清空路径，让 StartSOPDetectionAsync 重新弹文件选择
        SopWorkflowPath = "";

        // 重新启动
        if (wasDetecting)
        {
            await StartSOPDetectionAsync();
        }
    }

    // --- SOP 事件处理 ---

    private void OnSOPStepChanged(object? sender, StepChangedEventArgs e)
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            if (_isDisposed) return;
            CurrentStep = e.NewStepId;
            SopStatus = $"步骤 {e.NewStepId}: {e.StepName}";
            Status = $"SOP 步骤推进: {e.PreviousStepId} → {e.NewStepId} ({e.StepName})";
        });
    }

    private void OnSOPViolationDetected(object? sender, ViolationEventArgs e)
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            if (_isDisposed) return;
            SopStatus = $"⚠ 违规: {e.Violation.Description}";
            Status = $"⚠ SOP 违规: [{e.Violation.Type}] {e.Violation.Description}";
        });
    }

    private void OnSOPWorkflowCompleted(object? sender, SOPCompletedEventArgs e)
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            if (_isDisposed) return;
            SopStatus = e.IsPass ? "✅ SOP 全部通过" : "❌ SOP 检测不通过";
            Status = e.IsPass
                ? $"SOP 完成: 全部 {TotalSteps} 步通过"
                : $"SOP 完成: 有 {e.ViolationCount} 个违规";
        });
    }

    #endregion
```

#### 步骤5：修改 Dispose，清理 SOP 资源

找到 Dispose 方法中的这段代码：
```csharp
            // 取消订阅检测错误事件
            _detectionService.DetectionError -= OnDetectionError;
```

在其**下方**添加：
```csharp
            // 取消订阅 SOP 事件
            if (_sopModule != null)
            {
                _sopModule.StepChanged -= OnSOPStepChanged;
                _sopModule.ViolationDetected -= OnSOPViolationDetected;
                _sopModule.WorkflowCompleted -= OnSOPWorkflowCompleted;
                _sopModule.Dispose();
                _sopModule = null;
            }
```

---

### 修复7.2：MainWindow.xaml 新增 SOP 菜单项

**文件**: `src\UI\VisionInspection.UI\MainWindow.xaml`

找到现有的 SOP 菜单：
```xml
            <MenuItem Header="SOP">
                <MenuItem Header="⚙️ 初始化SOP" Command="{Binding InitializeSOPModuleCommand}"/>
                <MenuItem Header="▶️ 运行检测" Command="{Binding RunSOPDetectionCommand}"/>
            </MenuItem>
```

替换为：
```xml
            <MenuItem Header="SOP">
                <MenuItem Header="▶️ 启动SOP实时检测" Command="{Binding StartSOPDetectionCommand}"/>
                <MenuItem Header="⏹️ 停止SOP检测" Command="{Binding StopSOPDetectionCommand}"/>
                <MenuItem Header="🔄 重置工作流" Command="{Binding ResetSOPWorkflowCommand}"/>
                <MenuItem Header="📂 切换工作流" Command="{Binding SwitchSOPWorkflowCommand}"/>
                <Separator/>
                <MenuItem Header="⚙️ 初始化SOP（旧版）" Command="{Binding InitializeSOPModuleCommand}"/>
                <MenuItem Header="▶️ 单帧检测（旧版）" Command="{Binding RunSOPDetectionCommand}"/>
            </MenuItem>
```

---

### 修复7.3：SOPModule 补充缺失的事件和方法

**文件**: `src\Modules\VisionInspection.Modules.SOP\SOPModule.cs`

#### 步骤1：添加事件定义

在 SOPModule 类顶部（`public string Name => "SOPModule";` 之前），添加：

```csharp
    // ⭐ SOP 事件（供 UI 订阅）
    public event EventHandler<StepChangedEventArgs>? StepChanged;
    public event EventHandler<ViolationEventArgs>? ViolationDetected;
    public event EventHandler<SOPCompletedEventArgs>? WorkflowCompleted;
    public event EventHandler<PoseDetectedEventArgs>? PoseDetected;
```

#### 步骤2：在 InitializeAsync 中订阅状态机事件

找到 `_stateMachine = new SOPStateMachine();` 之后，添加事件订阅：

```csharp
            _stateMachine = new SOPStateMachine();

            // ⭐ 订阅状态机事件，转发给外部
            _stateMachine.StepCompleted += (s, e) =>
            {
                StepChanged?.Invoke(this, new StepChangedEventArgs
                {
                    PreviousStepId = e.PreviousStepId,
                    NewStepId = e.NewStepId,
                    StepName = e.StepName
                });
            };

            _stateMachine.ViolationOccurred += (s, e) =>
            {
                ViolationDetected?.Invoke(this, new ViolationEventArgs
                {
                    Violation = e.Violation
                });
            };

            _stateMachine.WorkflowCompleted += (s, e) =>
            {
                WorkflowCompleted?.Invoke(this, new SOPCompletedEventArgs
                {
                    IsPass = e.IsPass,
                    ViolationCount = e.ViolationCount
                });
            };
```

**注意**: 如果 SOPStateMachine 当前没有这些事件定义（StepCompleted / ViolationOccurred / WorkflowCompleted），需要在 SOPStateMachine.cs 中添加。查看当前代码中是否已有 `StepChanged` / `ViolationDetected` 等事件名——如果有则直接用现有名称替换上面的事件名。

#### 步骤3：添加 ResetWorkflow 和 StopWorkflow 方法（如果不存在）

```csharp
    /// <summary>
    /// 重置工作流（回到第一步）
    /// </summary>
    public void ResetWorkflow()
    {
        _stateMachine?.Reset();
        if (_currentWorkflow != null)
        {
            _stateMachine?.Start(_currentWorkflow);
        }
        Console.WriteLine("[SOP] 工作流已重置");
    }

    /// <summary>
    /// 停止工作流
    /// </summary>
    public void StopWorkflow()
    {
        _stateMachine?.Stop();
        Console.WriteLine("[SOP] 工作流已停止");
    }
```

#### 步骤4：添加事件参数类（如果不存在）

在 SOPModule.cs 文件末尾或单独的 Events.cs 文件中添加：

```csharp
/// <summary>
/// 步骤变化事件参数
/// </summary>
public class StepChangedEventArgs : EventArgs
{
    public int PreviousStepId { get; set; }
    public int NewStepId { get; set; }
    public string StepName { get; set; } = "";
}

/// <summary>
/// 违规事件参数
/// </summary>
public class ViolationEventArgs : EventArgs
{
    public ViolationRecord Violation { get; set; } = new();
}

/// <summary>
/// SOP 完成事件参数
/// </summary>
public class SOPCompletedEventArgs : EventArgs
{
    public bool IsPass { get; set; }
    public int ViolationCount { get; set; }
}
```

**注意**: 如果这些事件类已经存在于项目的其他位置（搜索 `StepChangedEventArgs`），则跳过这步，直接用已有的。

---

### 修复7.4：SOPModuleView 从模拟改为接收真实数据

**文件**: `src\UI\VisionInspection.UI\Views\SOPModuleView.xaml.cs`

#### 步骤1：删掉硬编码的示例步骤

找到构造函数中的这段：
```csharp
        public SOPModuleView()
        {
            InitializeComponent();
            InitializeSampleSteps();
        }
```

替换为：
```csharp
        public SOPModuleView()
        {
            InitializeComponent();
            // 不再加载示例步骤，等待 LoadFromWorkflow 注入真实步骤
        }
```

#### 步骤2：新增从 SOPWorkflow 加载步骤的方法

```csharp
        /// <summary>
        /// 从 SOPWorkflow 加载真实步骤（替代硬编码示例）
        /// </summary>
        public void LoadFromWorkflow(SOPWorkflow workflow)
        {
            _steps.Clear();
            StepsPanel.Children.Clear();

            if (workflow?.Steps == null || workflow.Steps.Count == 0)
            {
                AddLog("工作流中没有步骤");
                return;
            }

            foreach (var step in workflow.Steps)
            {
                var icon = GuessStepIcon(step.StepName);
                AddStep(
                    step.StepId.ToString(),
                    step.StepName,
                    step.Description ?? "",
                    icon
                );
            }

            UpdateStepDisplay();
            StepTextBlock.Text = $"0/{_steps.Count}";
            SetStatus("已加载", new SolidColorBrush(Color.FromRgb(24, 144, 255)));
            AddLog($"已加载工作流: {workflow.Name}，共 {workflow.Steps.Count} 步");
        }

        /// <summary>
        /// 根据步骤名称猜测图标
        /// </summary>
        private string GuessStepIcon(string stepName)
        {
            if (stepName.Contains("取") || stepName.Contains("拿") || stepName.Contains("放"))
                return "🤲";
            if (stepName.Contains("装") || stepName.Contains("安装"))
                return "🔧";
            if (stepName.Contains("拧") || stepName.Contains("扭矩"))
                return "🔩";
            if (stepName.Contains("检") || stepName.Contains("确认") || stepName.Contains("完成"))
                return "✅";
            if (stepName.Contains("密封") || stepName.Contains("垫片"))
                return "⭕";
            if (stepName.Contains("穿") || stepName.Contains("螺"))
                return "📌";
            return "🔍";
        }
```

#### 步骤3：改造 RunButton_Click，调用 ViewModel 的命令

找到：
```csharp
        private void RunButton_Click(object sender, RoutedEventArgs e)
        {
            // 模拟运行SOP检测
            RunSimulation();
        }
```

替换为：
```csharp
        private void RunButton_Click(object sender, RoutedEventArgs e)
        {
            // 触发 MainViewModel 的 SOP 启动命令
            var viewModel = DataContext as MainViewModel;
            if (viewModel != null && viewModel.StartSOPDetectionCommand.CanExecute(null))
            {
                viewModel.StartSOPDetectionCommand.Execute(null);
            }
            else
            {
                // 如果 ViewModel 不可用，降级到模拟模式
                RunSimulation();
            }
        }
```

---

### 修复7.5：创建默认配置文件

**新建文件**: `configs\sop_config.json`

```json
{
  "SOPModule": {
    "ModelPath": "yolo_models/yolov8s.onnx",
    "UseGpu": true,
    "ConfidenceThreshold": 0.6,
    "IouThreshold": 0.45,
    "PoseEstimation": {
      "Enabled": false,
      "ModelPath": "yolo_models/yolov8s-pose.onnx"
    }
  }
}
```

**注意**: 这只是 fallback 配置。实际模型路径由 YAML 工作流的 `model.path` 字段指定（修复指令6中已实现）。

---

## 四、修复后的完整使用流程

```
=== 用户操作手册 ===

第1步：准备 YAML 配置文件
    └── 复制 configs/sop/sop_steering_housing.yaml 为模板
    └── 修改 model.path 指向你的自训练 ONNX 模型
    └── 修改 steps 为你的产品 SOP 步骤
    └── 修改 regions 为你的工位区域坐标

第2步：准备 YOLO 模型
    └── 将自训练的 .onnx 模型放到 yolo_models/ 目录下
    └── 确认模型类别与 YAML 中的 model.classes 一致

第3步：启动程序
    └── 双击运行 → 登录 → 进入主界面

第4步：连接相机
    └── 菜单：相机 → 连接
    └── 菜单：相机 → 开始采集
    └── 此时画面应该显示实时图像

第5步：启动 SOP 检测
    └── 菜单：SOP → 启动SOP实时检测
    └── 弹出文件选择框 → 选择你的 YAML 文件
    └── 程序自动：
        ├── 初始化 SOPModule
        ├── 加载 YAML 工作流
        ├── 加载 YAML 中指定的 YOLO 模型
        ├── 注入区域定义
        └── 开始逐帧检测

第6步：观察检测过程
    └── 画面上方：显示当前步骤名称 + PASS/NG
    └── 画面中间：检测框 + 类别标签 + 置信度
    └── 画面下方（如有违规）：红色违规警告
    └── 右侧 SOP 面板：步骤进度条（绿色=完成，蓝色=进行中，灰色=等待）
    └── 状态栏：实时 FPS + 推理耗时

第7步：完成/异常处理
    └── 全部步骤通过 → 显示 ✅ PASS
    └── 发生违规 → 显示 ⚠ 违规类型 + 描述
    └── 手动重置：SOP → 重置工作流（从第一步重来）
    └── 换产品：SOP → 切换工作流（选另一个 YAML）
    └── 停止：SOP → 停止SOP检测
```

---

## 五、数据流完整图（修复后）

```
┌─────────────┐
│  海康相机     │
│  (SDK采集)    │
└──────┬──────┘
       │ OnCameraImageGrabbed (每帧回调)
       ▼
┌─────────────────────────────────────┐
│  MainViewModel                       │
│                                      │
│  IsSOPDetecting == true?             │
│    │                                 │
│    ├── Yes → PerformSOPDetectionAsync│
│    │         │                       │
│    │         ▼                       │
│    │  ┌──────────────┐               │
│    │  │  SOPModule    │               │
│    │  │              │               │
│    │  │  1. YOLO推理  │ ← 自训练模型  │
│    │  │     ↓        │   (YAML指定)  │
│    │  │  2. 状态机    │               │
│    │  │     ├ 条件评估│               │
│    │  │     ├ 区域检测│ ← YAML regions│
│    │  │     ├ 违规检测│               │
│    │  │     └ 步骤推进│               │
│    │  │     ↓        │               │
│    │  │  3. 返回结果  │               │
│    │  └──────┬───────┘               │
│    │         │                       │
│    │         ▼                       │
│    │  DrawSOPDetectionResults        │
│    │  (画框+步骤+违规叠加)            │
│    │         │                       │
│    │         ▼                       │
│    │  更新 UI                        │
│    │  ├── RoiEditorViewModel.Image   │
│    │  ├── SOPModuleView (步骤面板)    │
│    │  ├── DetectionResults (列表)     │
│    │  └── Status (状态栏)             │
│    │                                 │
│    └── No → IsRealTimeDetecting?     │
│              └── YoloDetectionService│
│                  (通用检测，只画框)    │
└─────────────────────────────────────┘
```

---

## 六、关联文件改动总览

| 文件 | 改动类型 | 说明 |
|------|---------|------|
| `ViewModels\MainViewModel.cs` | 新增字段+方法+事件 | `IsSOPDetecting` / `PerformSOPDetectionAsync` / `DrawSOPDetectionResults` / `StartSOPDetectionAsync` / `StopSOPDetection` / `ResetSOPWorkflow` / `SwitchSOPWorkflowAsync` / 事件处理 |
| `MainWindow.xaml` | 修改菜单 | SOP 菜单：启动/停止/重置/切换 |
| `SOPModule.cs` | 新增事件+方法 | `StepChanged` / `ViolationDetected` / `WorkflowCompleted` 事件 + `ResetWorkflow` / `StopWorkflow` 方法 + 事件参数类 |
| `SOPModuleView.xaml.cs` | 改造 | `LoadFromWorkflow()` 从真实工作流加载步骤，删除硬编码示例 |
| `configs\sop_config.json` | 新建 | SOP 默认配置 |

---

## 七、编译验证

```bash
cd E:\yolo\YoloDotNet-master\VisionInspectionSystem
dotnet build
```

### 全局搜索验证

```
搜索 "IsSOPDetecting" → 应出现在 MainViewModel.cs（字段+赋值+判断）
搜索 "PerformSOPDetectionAsync" → MainViewModel.cs（定义+调用）
搜索 "DrawSOPDetectionResults" → MainViewModel.cs（定义+调用）
搜索 "StartSOPDetectionCommand" → MainViewModel.cs + MainWindow.xaml
搜索 "StopSOPDetectionCommand" → MainViewModel.cs + MainWindow.xaml
搜索 "LoadFromWorkflow" → SOPModuleView.xaml.cs
```

### 运行时验证

1. 启动程序 → 登录
2. 连接相机 → 开始采集 → 画面显示实时图像
3. SOP → 启动SOP实时检测 → 选择 YAML
4. 观察：
   - 画面上有检测框？（YOLO 在工作）
   - 顶部状态栏显示步骤？（状态机在工作）
   - 右侧面板步骤进度更新？（UI 事件在工作）
   - FPS 显示正常？（实时循环在工作）
