using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using VisionInspection.Core.Models;
using VisionInspection.Core.Services;
using VisionInspection.UI.ViewModels;
using VisionInspection.Modules.SOP;
using VisionInspection.Modules.SOP.Models;
using SkiaSharp;

namespace VisionInspection.UI.Views
{
    /// <summary>
    /// SOP模块视图
    /// </summary>
    public partial class SOPModuleView : UserControl
    {
        private List<SOPStepItem> _steps = new();
        private int _currentStepIndex = -1;

        // 计时器相关
        private DispatcherTimer? _timer;
        private DateTime _timerStartTime;
        private bool _isTimerRunning = false;

        // YAML工作流路径
        private string _currentYamlPath = "";

        // SOP检测模式配置 - 默认启用手部检测，最大手数为2
        private SOPDetectionModeConfig _detectionModeConfig = new()
        {
            DetectionMode = "UnifiedDetection",
            EnableHandPoseEstimation = true,
            MaxNumHands = 2,
            UseGpu = true
        };

        public SOPModuleView()
        {
            InitializeComponent();
            LoadDefaultWorkflow();
            LoadRecipeList();
            InitializeTimer();
        }

        /// <summary>
        /// 初始化计时器
        /// </summary>
        private void InitializeTimer()
        {
            _timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(100) // 100ms更新一次
            };
            _timer.Tick += Timer_Tick;
        }

        /// <summary>
        /// 计时器Tick事件
        /// </summary>
        private void Timer_Tick(object? sender, EventArgs e)
        {
            if (_isTimerRunning)
            {
                var elapsed = DateTime.Now - _timerStartTime;
                TimerTextBlock.Text = FormatTimeSpan(elapsed);
            }
        }

        /// <summary>
        /// 格式化时间显示
        /// </summary>
        private static string FormatTimeSpan(TimeSpan time)
        {
            if (time.TotalHours >= 1)
                return $"{time.Hours:D2}:{time.Minutes:D2}:{time.Seconds:D2}";
            else
                return $"{time.Minutes:D2}:{time.Seconds:D2}";
        }

        /// <summary>
        /// 格式化单步耗时（小于1秒显示毫秒，否则显示秒）
        /// </summary>
        private static string FormatStepDuration(TimeSpan duration)
        {
            if (duration.TotalSeconds < 1)
                return $"{duration.TotalMilliseconds:F0}ms";
            return $"{duration.TotalSeconds:F1}s";
        }

        /// <summary>
        /// 开始计时
        /// </summary>
        public void StartTimer()
        {
            _timerStartTime = DateTime.Now;
            _isTimerRunning = true;
            _timer?.Start();
            TimerTextBlock.Text = "00:00";
            TimerTextBlock.Foreground = new SolidColorBrush(Color.FromRgb(250, 140, 22));
        }

        /// <summary>
        /// 停止计时
        /// </summary>
        public void StopTimer()
        {
            _isTimerRunning = false;
            _timer?.Stop();
        }

        /// <summary>
        /// 重置计时器
        /// </summary>
        public void ResetTimer()
        {
            _isTimerRunning = false;
            _timer?.Stop();
            TimerTextBlock.Text = "00:00";
            TimerTextBlock.Foreground = new SolidColorBrush(Colors.Gray);
        }

        /// <summary>
        /// 获取主视图模型
        /// </summary>
        private MainViewModel? GetMainViewModel()
        {
            if (DataContext is MainViewModel vm)
                return vm;
            // 尝试从父窗口获取
            var window = Window.GetWindow(this);
            if (window?.DataContext is MainViewModel mainVm)
                return mainVm;
            return null;
        }

        /// <summary>
        /// 初始化示例步骤（回退方案）
        /// </summary>
        private void InitializeSampleSteps()
        {
            AddStep("1", "检测车辆", "使用YOLO模型检测画面中是否存在车辆", "🔍", "detect_vehicle");
            AddStep("2", "车牌识别", "检测到车辆后，识别车牌号码", "📄", "recognize_plate");
            AddStep("3", "安全验证", "验证车牌是否在白名单中", "✓", "safety_verify");
            AddStep("4", "触发PLC", "验证通过，触发PLC开门", "🔌", "trigger_plc");

            UpdateStepDisplay();
        }

        /// <summary>
        /// 加载默认工作流（优先从YAML加载，失败则使用示例步骤）
        /// </summary>
        private void LoadDefaultWorkflow()
        {
            var yamlPath = FindDefaultYamlFile();
            if (!string.IsNullOrEmpty(yamlPath))
            {
                try
                {
                    LoadStepsFromYaml(yamlPath);
                    _currentYamlPath = yamlPath;
                    AddLog($"已加载工作流: {System.IO.Path.GetFileName(yamlPath)}");
                    return;
                }
                catch (Exception ex)
                {
                    AddLog($"YAML加载失败: {ex.Message}，使用默认示例步骤");
                }
            }

            InitializeSampleSteps();
        }

        /// <summary>
        /// 查找默认YAML文件
        /// </summary>
        private static string? FindDefaultYamlFile()
        {
            string[] searchDirs = { "configs/sop", "configs" };

            foreach (var dir in searchDirs)
            {
                if (!Directory.Exists(dir)) continue;

                var yamlFiles = Directory.GetFiles(dir, "*.yaml");
                if (yamlFiles.Length > 0)
                    return yamlFiles[0];
            }

            return null;
        }

        /// <summary>
        /// 扫描 configs/sop 下的所有配方 YAML，填充产品配方下拉框。
        /// 显示名优先取 YAML 内的 sop.name，回退为文件名。
        /// </summary>
        private void LoadRecipeList()
        {
            RecipeComboBox.Items.Clear();

            string[] searchDirs = { "configs/sop", "configs" };
            var found = new List<string>();
            foreach (var dir in searchDirs)
            {
                if (!Directory.Exists(dir)) continue;
                found.AddRange(Directory.GetFiles(dir, "*.yaml"));
                found.AddRange(Directory.GetFiles(dir, "*.yml"));
            }
            found = found.Distinct().OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();

            foreach (var f in found)
            {
                var baseName = System.IO.Path.GetFileNameWithoutExtension(f);
                var display = baseName;
                try
                {
                    var text = File.ReadAllText(f);
                    // [^\r\n#]+ 仅取本行 name 值：排除换行（防止吞掉 version/description）和行内 # 注释
                    var m = Regex.Match(text, @"^\s{0,4}name\s*:\s*([^\r\n#]+)$", RegexOptions.Multiline);
                    if (m.Success)
                    {
                        var val = m.Groups[1].Value.Trim().Trim('"').Trim('\'');
                        if (!string.IsNullOrEmpty(val))
                            display = $"{val}  ·  {baseName}";
                    }
                }
                catch
                {
                    // 解析失败则回退到文件名
                }

                RecipeComboBox.Items.Add(new RecipeItem
                {
                    DisplayName = display,
                    FullPath = System.IO.Path.GetFullPath(f)
                });
            }

            // 选中当前已加载的配方
            if (!string.IsNullOrEmpty(_currentYamlPath))
            {
                var cur = System.IO.Path.GetFullPath(_currentYamlPath);
                foreach (RecipeItem item in RecipeComboBox.Items)
                {
                    if (item.FullPath.Equals(cur, StringComparison.OrdinalIgnoreCase))
                    {
                        RecipeComboBox.SelectedItem = item;
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// 刷新产品配方下拉列表（生成/另存新配方后可调用，无需重启程序）
        /// </summary>
        public void RefreshRecipeList() => LoadRecipeList();

        /// <summary>
        /// 产品配方下拉框切换：重载对应工作流并同步给运行时
        /// </summary>
        private void RecipeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var item = RecipeComboBox.SelectedItem as RecipeItem;
            if (item == null) return;

            // 联动重命名/删除按钮可用状态
            var hasSelection = !string.IsNullOrEmpty(item.FullPath);
            RenameRecipeMenuItem.IsEnabled = hasSelection;
            DeleteRecipeMenuItem.IsEnabled = hasSelection;

            var target = System.IO.Path.GetFullPath(item.FullPath);
            var current = string.IsNullOrEmpty(_currentYamlPath) ? "" : System.IO.Path.GetFullPath(_currentYamlPath);
            if (target.Equals(current, StringComparison.OrdinalIgnoreCase)) return;

            try
            {
                LoadStepsFromYaml(item.FullPath);
                _currentYamlPath = item.FullPath;

                // 同步给 MainViewModel，运行时将使用新配方
                var vm = GetMainViewModel();
                if (vm != null) vm.SopWorkflowPath = _currentYamlPath;

                AddLog($"已切换产品配方: {item.DisplayName}");
            }
            catch (Exception ex)
            {
                AddLog($"切换配方失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 展开配方管理菜单（新建 / 重命名 / 删除）
        /// </summary>
        private void RecipeActionsButton_Click(object sender, RoutedEventArgs e)
        {
            if (RecipeActionsContextMenu != null)
            {
                RecipeActionsContextMenu.PlacementTarget = RecipeActionsButton;
                RecipeActionsContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                RecipeActionsContextMenu.IsOpen = true;
            }
        }

        /// <summary>
        /// 重命名当前配方：仅修改 YAML 中的 sop.name（保留文件名与全部内容）
        /// </summary>
        private void RenameRecipeButton_Click(object sender, RoutedEventArgs e)
        {
            if (RecipeComboBox.SelectedItem is not RecipeItem sel) return;
            if (string.IsNullOrEmpty(sel.FullPath) || !System.IO.File.Exists(sel.FullPath))
            {
                MessageBox.Show("该配方文件不存在，无法重命名", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var current = ReadSopName(sel.FullPath);
            var dlg = new InputDialog("重命名配方", "请输入新的产品名称：", current)
            {
                Owner = Window.GetWindow(this)
            };

            if (dlg.ShowDialog() != true) return;

            var newName = dlg.ResultText;
            if (string.IsNullOrEmpty(newName) || newName == current) return;

            try
            {
                ReplaceSopNameInYaml(sel.FullPath, newName);
                AddLog($"配方已重命名为: {newName}");
                LoadRecipeList();
                foreach (RecipeItem item in RecipeComboBox.Items)
                {
                    if (item.FullPath.Equals(System.IO.Path.GetFullPath(sel.FullPath), StringComparison.OrdinalIgnoreCase))
                    {
                        RecipeComboBox.SelectedItem = item;
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"重命名失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 删除当前选中的配方文件（确认后删除，不可恢复）
        /// </summary>
        private void DeleteRecipeButton_Click(object sender, RoutedEventArgs e)
        {
            if (RecipeComboBox.SelectedItem is not RecipeItem sel) return;
            if (string.IsNullOrEmpty(sel.FullPath) || !System.IO.File.Exists(sel.FullPath))
            {
                MessageBox.Show("该配方文件不存在", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var r = MessageBox.Show(
                $"确定要删除配方「{sel.DisplayName}」吗？\n文件：{sel.FullPath}\n\n此操作不可恢复！",
                "删除确认", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (r != MessageBoxResult.Yes) return;

            try
            {
                System.IO.File.Delete(sel.FullPath);
                AddLog($"已删除配方文件: {System.IO.Path.GetFileName(sel.FullPath)}");
                LoadRecipeList();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"删除失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 读取 YAML 中 sop 根节点下的 name（保留引号剥离）
        /// </summary>
        private static string ReadSopName(string path)
        {
            var lines = System.IO.File.ReadAllLines(path);
            var sopIndent = -1;
            var inSop = false;

            foreach (var l in lines)
            {
                var t = l.TrimStart();
                var ind = l.Length - t.Length;

                if (!inSop)
                {
                    if (t == "sop:" || t.StartsWith("sop:"))
                    {
                        inSop = true;
                        sopIndent = ind;
                    }
                    continue;
                }

                if (ind > sopIndent && t.StartsWith("name:"))
                {
                    var v = t.Substring("name:".Length).Trim();
                    if (v.Length >= 2 && v[0] == '"' && v[v.Length - 1] == '"')
                        v = v.Substring(1, v.Length - 2);
                    return v;
                }

                if (ind <= sopIndent && t.Length > 0) break;
            }

            return System.IO.Path.GetFileNameWithoutExtension(path);
        }

        /// <summary>
        /// 新建配方按钮：弹出对话框，生成新的 YAML 并刷新下拉框
        /// </summary>
        private void NewRecipeButton_Click(object sender, RoutedEventArgs e)
        {
            var templates = new List<RecipeItem>();
            foreach (RecipeItem item in RecipeComboBox.Items)
                templates.Add(item);

            var dlg = new NewRecipeWindow(templates)
            {
                Owner = Window.GetWindow(this)
            };

            if (dlg.ShowDialog() != true) return;

            var productName = dlg.ProductName;
            var templatePath = dlg.TemplatePath;

            try
            {
                var sopDir = System.IO.Path.Combine("configs", "sop");
                if (!System.IO.Directory.Exists(sopDir))
                    System.IO.Directory.CreateDirectory(sopDir);

                var fileName = BuildRecipeFileName(sopDir, productName);
                var fullPath = System.IO.Path.Combine(sopDir, fileName);

                if (string.IsNullOrEmpty(templatePath))
                {
                    // 空白模板：写入最小可用骨架
                    System.IO.File.WriteAllText(fullPath, BuildBlankRecipeYaml(productName), System.Text.Encoding.UTF8);
                    AddLog($"已新建空白配方: {fileName}");
                }
                else
                {
                    // 基于现有配方：复制整文件后仅替换 sop.name
                    System.IO.File.Copy(templatePath, fullPath, overwrite: false);
                    ReplaceSopNameInYaml(fullPath, productName);
                    AddLog($"已基于模板新建配方: {fileName}（源: {System.IO.Path.GetFileName(templatePath)}）");
                }

                LoadRecipeList();

                // 自动选中并切换到新配方
                var target = System.IO.Path.GetFullPath(fullPath);
                foreach (RecipeItem item in RecipeComboBox.Items)
                {
                    if (item.FullPath.Equals(target, StringComparison.OrdinalIgnoreCase))
                    {
                        RecipeComboBox.SelectedItem = item;
                        break;
                    }
                }

                AddLog("提示：可在「🎯 标定区域」中定义区域、在「⚙️ 配置」中调整步骤，并替换 model.path 为专用模型");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"新建配方失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                AddLog($"新建配方失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 生成不冲突的配方文件名：sop_产品名.yaml，冲突时追加 _2/_3...
        /// </summary>
        private static string BuildRecipeFileName(string sopDir, string productName)
        {
            var baseName = "sop_" + SanitizeFileName(productName);
            if (baseName.Length <= 4) // 仅 "sop_"
                baseName = "sop_new";

            var name = baseName;
            var i = 2;
            while (System.IO.File.Exists(System.IO.Path.Combine(sopDir, name + ".yaml")))
            {
                name = $"{baseName}_{i}";
                i++;
            }
            return name + ".yaml";
        }

        /// <summary>
        /// 清洗文件名中的非法字符，空格转下划线
        /// </summary>
        private static string SanitizeFileName(string name)
        {
            var invalid = System.IO.Path.GetInvalidFileNameChars();
            var sb = new System.Text.StringBuilder();
            foreach (var c in name)
                sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
            var s = sb.ToString().Trim().Replace(' ', '_');
            return string.IsNullOrEmpty(s) ? "new" : s;
        }

        /// <summary>
        /// 仅替换 YAML 中 sop 根节点下的 name（保留其余字段与注释）
        /// </summary>
        private static void ReplaceSopNameInYaml(string path, string newName)
        {
            var lines = System.IO.File.ReadAllLines(path);
            var sopIndent = -1;
            var inSop = false;

            for (var i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].TrimStart();
                var indent = lines[i].Length - trimmed.Length;

                if (!inSop)
                {
                    if (trimmed == "sop:" || trimmed.StartsWith("sop:"))
                    {
                        inSop = true;
                        sopIndent = indent;
                    }
                    continue;
                }

                // sop 块内的第一个 name: 子项即产品名
                if (indent > sopIndent && trimmed.StartsWith("name:"))
                {
                    lines[i] = new string(' ', indent) + $"name: \"{newName}\"";
                    break;
                }

                // 遇到下一个顶层块（缩进 <= sop）则停止，避免误改其它段
                if (indent <= sopIndent && trimmed.Length > 0)
                    break;
            }

            System.IO.File.WriteAllLines(path, lines);
        }

        /// <summary>
        /// 空白模板：最小可用骨架（步骤1 + 成品漏放校验步）
        /// </summary>
        private static string BuildBlankRecipeYaml(string productName)
        {
            return $@"sop:
  name: ""{productName}""
  version: ""1.0""
  description: ""通过界面新建的空白配方，请在 ROI 标定工具中定义区域并补全步骤""
  settings:
    detectionMode: ""UnifiedDetection""
    confidenceThreshold: 0.5
    stableFrames: 3
    timeoutSeconds: 30
    enableSkipDetection: true
    enableTimeoutDetection: true
  regions: {{}}
  steps:
    - id: ""1""
      name: ""步骤1""
      description: ""请在此定义第一个作业步骤（如取料）""
      timeout: 30
      detection:
        method: ""time_elapsed""
        stable_frames: 2
    - id: ""2""
      name: ""成品校验（漏放检测）""
      description: ""最终成品区漏放校验：请补全 required_objects 列出必放物料""
      timeout: 30
      detection:
        method: ""time_elapsed""
        stable_frames: 2
      required_objects: []
  model:
    path: ""yolo_models/sop_custom_yolov8s.onnx""
    type: ""ObjectDetection""
    confidence: 0.5
    iou: 0.45
    useGpu: true
    classes: []
  monitoring:
    enabled: true
    person_classes: [""person""]
    helmet_classes: [""helmet"", ""safe_helmet"", ""hard_hat""]
    regions: []
    min_confidence: 0.5
    cooldown_seconds: 5
";
        }

        /// <summary>
        /// 从YAML文件加载SOP步骤
        /// </summary>
        private void LoadStepsFromYaml(string yamlPath)
        {
            var workflow = SOPYamlConverter.LoadFromYaml(yamlPath);
            LoadWorkflowSteps(workflow);
        }

        /// <summary>
        /// 从SOPWorkflow加载步骤到UI列表
        /// </summary>
        private void LoadWorkflowSteps(SOPWorkflow workflow)
        {
            _steps.Clear();

            foreach (var step in workflow.Steps.OrderBy(s => s.Order))
            {
                AddStep(
                    step.StepId.ToString(),
                    step.StepName,
                    step.Description ?? "",
                    GetStepIcon(step.Order),
                    step.StepId.ToString()
                );
            }

            UpdateStepDisplay();
        }

        /// <summary>
        /// 获取步骤图标
        /// </summary>
        private static string GetStepIcon(int order)
        {
            return order switch
            {
                1 => "1️⃣",
                2 => "2️⃣",
                3 => "3️⃣",
                4 => "4️⃣",
                5 => "5️⃣",
                6 => "6️⃣",
                7 => "7️⃣",
                8 => "8️⃣",
                _ => "📋"
            };
        }

        /// <summary>
        /// 添加步骤
        /// </summary>
        public void AddStep(string stepNumber, string name, string description, string icon, string? id = null)
        {
            var stepItem = new SOPStepItem
            {
                Id = id ?? stepNumber,
                StepNumber = stepNumber,
                Name = name,
                Description = description,
                Icon = icon,
                Status = StepStatus.Pending
            };
            _steps.Add(stepItem);
        }

        /// <summary>
        /// 更新步骤显示
        /// </summary>
        private void UpdateStepDisplay()
        {
            StepsPanel.Children.Clear();

            for (int i = 0; i < _steps.Count; i++)
            {
                var step = _steps[i];
                var stepControl = CreateStepControl(step, i == _steps.Count - 1);
                StepsPanel.Children.Add(stepControl);
            }
        }

        /// <summary>
        /// 创建步骤控件 - 水平列表样式
        /// </summary>
        private Border CreateStepControl(SOPStepItem step, bool isLast)
        {
            var border = new Border
            {
                Background = GetStepBackground(step.Status),
                BorderBrush = GetStepBorderBrush(step.Status),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(0, 0, 0, isLast ? 0 : 8),
                Padding = new Thickness(12, 10, 12, 10),
                Cursor = System.Windows.Input.Cursors.Hand
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });      // 编号
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // 名称
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });      // 状态

            // 左侧：步骤编号圆形背景
            var numberBorder = new Border
            {
                Background = GetStepNumberBackground(step.Status),
                Width = 28,
                Height = 28,
                CornerRadius = new CornerRadius(14),
                Child = new TextBlock
                {
                    Text = step.StepNumber,
                    FontSize = 13,
                    FontWeight = FontWeights.Bold,
                    Foreground = GetStepNumberForeground(step.Status),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                },
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(numberBorder, 0);
            grid.Children.Add(numberBorder);

            // 中间：步骤名称和描述
            var contentPanel = new StackPanel 
            { 
                Margin = new Thickness(12, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            var nameText = new TextBlock
            {
                Text = step.Name,
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Foreground = GetStepForeground(step.Status)
            };
            var descText = new TextBlock
            {
                Text = step.Description,
                FontSize = 11,
                Foreground = new SolidColorBrush(Colors.Gray),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 2, 0, 0)
            };
            contentPanel.Children.Add(nameText);
            contentPanel.Children.Add(descText);
            Grid.SetColumn(contentPanel, 1);
            grid.Children.Add(contentPanel);

            // 右侧：状态指示器 + 单步耗时
            var rightPanel = new StackPanel
            {
                Orientation = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            var statusIndicator = CreateHorizontalStatusIndicator(step.Status);
            rightPanel.Children.Add(statusIndicator);

            if (step.Duration > TimeSpan.Zero)
            {
                var durationText = new TextBlock
                {
                    Text = FormatStepDuration(step.Duration),
                    FontSize = 10,
                    Foreground = new SolidColorBrush(Colors.Gray),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 4, 0, 0)
                };
                rightPanel.Children.Add(durationText);
            }
            Grid.SetColumn(rightPanel, 2);
            grid.Children.Add(rightPanel);

            border.Child = grid;
            return border;
        }

        /// <summary>
        /// 创建水平状态指示器
        /// </summary>
        private UIElement CreateHorizontalStatusIndicator(StepStatus status)
        {
            var panel = new StackPanel 
            { 
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };

            switch (status)
            {
                case StepStatus.Completed:
                    panel.Children.Add(new Border
                    {
                        Background = new SolidColorBrush(Color.FromRgb(82, 196, 26)),
                        CornerRadius = new CornerRadius(10),
                        Padding = new Thickness(8, 3, 8, 3),
                        Child = new TextBlock
                        {
                            Text = "OK",
                            FontSize = 11,
                            Foreground = new SolidColorBrush(Colors.White),
                            FontWeight = FontWeights.Bold
                        }
                    });
                    break;
                case StepStatus.Running:
                    panel.Children.Add(new Border
                    {
                        Background = new SolidColorBrush(Color.FromRgb(24, 144, 255)),
                        CornerRadius = new CornerRadius(10),
                        Padding = new Thickness(8, 3, 8, 3),
                        Child = new TextBlock
                        {
                            Text = "进行中",
                            FontSize = 11,
                            Foreground = new SolidColorBrush(Colors.White),
                            FontWeight = FontWeights.Bold
                        }
                    });
                    break;
                case StepStatus.Failed:
                    panel.Children.Add(new Border
                    {
                        Background = new SolidColorBrush(Color.FromRgb(255, 77, 79)),
                        CornerRadius = new CornerRadius(10),
                        Padding = new Thickness(8, 3, 8, 3),
                        Child = new TextBlock
                        {
                            Text = "NG",
                            FontSize = 11,
                            Foreground = new SolidColorBrush(Colors.White),
                            FontWeight = FontWeights.Bold
                        }
                    });
                    break;
                default:
                    panel.Children.Add(new TextBlock
                    {
                        Text = "待执行",
                        FontSize = 11,
                        Foreground = new SolidColorBrush(Colors.Gray),
                        VerticalAlignment = VerticalAlignment.Center
                    });
                    break;
            }

            return panel;
        }

        /// <summary>
        /// 获取步骤编号背景色
        /// </summary>
        private Brush GetStepNumberBackground(StepStatus status)
        {
            return status switch
            {
                StepStatus.Completed => new SolidColorBrush(Color.FromRgb(82, 196, 26)),
                StepStatus.Running => new SolidColorBrush(Color.FromRgb(24, 144, 255)),
                StepStatus.Failed => new SolidColorBrush(Color.FromRgb(255, 77, 79)),
                _ => new SolidColorBrush(Color.FromRgb(217, 217, 217))
            };
        }

        /// <summary>
        /// 获取步骤编号前景色
        /// </summary>
        private Brush GetStepNumberForeground(StepStatus status)
        {
            return status switch
            {
                StepStatus.Pending => new SolidColorBrush(Colors.Gray),
                _ => new SolidColorBrush(Colors.White)
            };
        }

        /// <summary>
        /// 创建紧凑状态指示器
        /// </summary>
        private UIElement CreateCompactStatusIndicator(StepStatus status)
        {
            return status switch
            {
                StepStatus.Completed => new TextBlock
                {
                    Text = "OK",
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.FromRgb(82, 196, 26)),
                    FontWeight = FontWeights.Bold,
                    VerticalAlignment = VerticalAlignment.Center
                },
                StepStatus.Running => new Border
                {
                    Width = 8,
                    Height = 8,
                    Background = new SolidColorBrush(Color.FromRgb(24, 144, 255)),
                    CornerRadius = new CornerRadius(4)
                },
                StepStatus.Failed => new TextBlock
                {
                    Text = "NG",
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.FromRgb(255, 77, 79)),
                    FontWeight = FontWeights.Bold,
                    VerticalAlignment = VerticalAlignment.Center
                },
                _ => new TextBlock
                {
                    Text = "待",
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Colors.Gray),
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
        }

        /// <summary>
        /// 创建状态指示器
        /// </summary>
        private UIElement CreateStatusIndicator(StepStatus status)
        {
            return status switch
            {
                StepStatus.Completed => new TextBlock
                {
                    Text = "✓",
                    FontSize = 24,
                    Foreground = new SolidColorBrush(Colors.Green),
                    FontWeight = FontWeights.Bold
                },
                StepStatus.Running => CreateProgressRing(),
                StepStatus.Failed => new TextBlock
                {
                    Text = "✗",
                    FontSize = 24,
                    Foreground = new SolidColorBrush(Colors.Red),
                    FontWeight = FontWeights.Bold
                },
                _ => new TextBlock
                {
                    Text = "○",
                    FontSize = 24,
                    Foreground = new SolidColorBrush(Colors.LightGray)
                }
            };
        }

        /// <summary>
        /// 创建进度环
        /// </summary>
        private UIElement CreateProgressRing()
        {
            // 使用简单的旋转动画替代ProgressBarRing样式
            var grid = new Grid
            {
                Width = 40,
                Height = 40
            };

            // 外圈
            var ellipse = new System.Windows.Shapes.Ellipse
            {
                Width = 32,
                Height = 32,
                Stroke = new SolidColorBrush(Color.FromRgb(24, 144, 255)),
                StrokeThickness = 3,
                StrokeDashArray = new DoubleCollection { 10, 5 },
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            // 旋转动画
            var rotateTransform = new RotateTransform(0, 16, 16);
            ellipse.RenderTransform = rotateTransform;

            var animation = new System.Windows.Media.Animation.DoubleAnimation
            {
                From = 0,
                To = 360,
                Duration = TimeSpan.FromSeconds(1),
                RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever
            };

            rotateTransform.BeginAnimation(RotateTransform.AngleProperty, animation);

            grid.Children.Add(ellipse);
            return grid;
        }

        /// <summary>
        /// 获取步骤背景色
        /// </summary>
        private Brush GetStepBackground(StepStatus status)
        {
            return status switch
            {
                StepStatus.Completed => new SolidColorBrush(Color.FromRgb(246, 255, 237)),
                StepStatus.Running => new SolidColorBrush(Color.FromRgb(230, 247, 255)),
                StepStatus.Failed => new SolidColorBrush(Color.FromRgb(255, 241, 240)),
                _ => new SolidColorBrush(Colors.White)
            };
        }

        /// <summary>
        /// 获取步骤边框颜色
        /// </summary>
        private Brush GetStepBorderBrush(StepStatus status)
        {
            return status switch
            {
                StepStatus.Completed => new SolidColorBrush(Color.FromRgb(183, 235, 143)),
                StepStatus.Running => new SolidColorBrush(Color.FromRgb(145, 213, 255)),
                StepStatus.Failed => new SolidColorBrush(Color.FromRgb(255, 163, 158)),
                _ => new SolidColorBrush(Color.FromRgb(217, 217, 217))
            };
        }

        /// <summary>
        /// 获取步骤前景色
        /// </summary>
        private Brush GetStepForeground(StepStatus status)
        {
            return status switch
            {
                StepStatus.Completed => new SolidColorBrush(Color.FromRgb(82, 196, 26)),
                StepStatus.Running => new SolidColorBrush(Color.FromRgb(24, 144, 255)),
                StepStatus.Failed => new SolidColorBrush(Color.FromRgb(255, 77, 79)),
                _ => new SolidColorBrush(Color.FromRgb(102, 102, 102))
            };
        }

        /// <summary>
        /// 设置当前步骤
        /// </summary>
        public void SetCurrentStep(int stepIndex)
        {
            _currentStepIndex = stepIndex;
            for (int i = 0; i < _steps.Count; i++)
            {
                if (i < stepIndex)
                    _steps[i].Status = StepStatus.Completed;
                else if (i == stepIndex)
                    _steps[i].Status = StepStatus.Running;
                else
                    _steps[i].Status = StepStatus.Pending;
            }
            UpdateStepDisplay();
            StepTextBlock.Text = $"{stepIndex + 1}/{_steps.Count}";
        }

        /// <summary>
        /// 设置步骤结果
        /// </summary>
        public void SetStepResult(int stepIndex, bool success, string message = "")
        {
            if (stepIndex >= 0 && stepIndex < _steps.Count)
            {
                _steps[stepIndex].Status = success ? StepStatus.Completed : StepStatus.Failed;
                _steps[stepIndex].ResultMessage = message;
                UpdateStepDisplay();
            }
        }

        /// <summary>
        /// 添加日志
        /// </summary>
        public void AddLog(string message)
        {
            var timestamp = DateTime.Now.ToString("HH:mm:ss");
            LogTextBox.AppendText($"[{timestamp}] {message}\n");
            LogTextBox.ScrollToEnd();
        }

        /// <summary>
        /// 设置状态
        /// </summary>
        public void SetStatus(string status, Brush? foreground = null)
        {
            StatusTextBlock.Text = status;
            if (foreground != null)
            {
                StatusTextBlock.Foreground = foreground;
            }
        }

        /// <summary>
        /// 设置检测结果
        /// </summary>
        public void SetResult(string result, Brush? foreground = null)
        {
            ResultTextBlock.Text = result;
            if (foreground != null)
            {
                ResultTextBlock.Foreground = foreground;
            }
        }

        /// <summary>
        /// 配置按钮点击
        /// </summary>
        /// <summary>
        /// 打开 SOP 区域标定窗口：传入当前相机帧与当前 YAML，保存后重载工作流
        /// </summary>
        private void RegionButton_Click(object sender, RoutedEventArgs e)
        {
            var vm = GetMainViewModel();
            if (vm == null) return;

            // 相机预览帧实时写入 RoiEditorViewModel.CurrentImage；
            // 仅运行 SOP 检测时才会同步到 MainViewModel.CurrentImage。
            // 优先取相机预览帧，保证"只开相机、未运行 SOP"也能标定。
            var frame = vm.RoiEditorViewModel?.CurrentImage ?? vm.CurrentImage;
            if (frame == null)
            {
                MessageBox.Show("请先开启相机并采集一帧画面，再标定区域。", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // 复制一帧，避免被实时帧复用导致绘制异常
            var snapshot = frame.Copy();
            var win = new SOPRegionEditorWindow(snapshot, _currentYamlPath);
            if (win.ShowDialog() == true && !string.IsNullOrEmpty(win.SavedYamlPath))
            {
                ReloadFromYaml(win.SavedYamlPath);
            }
        }

        /// <summary>
        /// 用指定 YAML 重新加载步骤与区域显示
        /// </summary>
        private void ReloadFromYaml(string path)
        {
            try
            {
                LoadStepsFromYaml(path);
                _currentYamlPath = path;
                AddLog($"区域已更新，已重载工作流: {System.IO.Path.GetFileName(path)}");
            }
            catch (Exception ex)
            {
                AddLog($"重载工作流失败: {ex.Message}");
            }
        }

        private void ConfigButton_Click(object sender, RoutedEventArgs e)
        {
            // 打开 SOP 流程预览与编辑窗口：传入当前配方 YAML，保存后自动重载
            var configWindow = new SOPConfigWindow(string.IsNullOrEmpty(_currentYamlPath) ? null : _currentYamlPath);
            configWindow.Owner = Window.GetWindow(this);

            // 传递当前运行时检测配置（手部检测等）
            configWindow.SetDetectionModeConfig(_detectionModeConfig);

            if (configWindow.ShowDialog() == true)
            {
                // YAML 已保存 → 重载工作流步骤
                if (!string.IsNullOrEmpty(configWindow.SavedYamlPath))
                {
                    try
                    {
                        LoadStepsFromYaml(configWindow.SavedYamlPath);
                        _currentYamlPath = configWindow.SavedYamlPath;
                        AddLog($"SOP流程已修改并重载: {System.IO.Path.GetFileName(_currentYamlPath)}");
                    }
                    catch (Exception ex)
                    {
                        AddLog($"重载工作流失败: {ex.Message}");
                    }
                }

                // 应用运行时检测配置
                _detectionModeConfig = configWindow.GetDetectionModeConfig();
                UpdateSOPDetectionMode();
                UpdateStepDisplay();
            }
        }

        /// <summary>
        /// 更新SOP模块检测模式
        /// </summary>
        private void UpdateSOPDetectionMode()
        {
            var viewModel = GetMainViewModel();

            // 统一检测模式：所有配置都映射到 UnifiedDetection
            var mode = SOPDetectionMode.UnifiedDetection;

            // 重要：无论 SOPModule 是否已创建，都要保存配置到 MainViewModel
            // 这样当 SOPModule 稍后被创建时可以应用正确的模式
            if (viewModel != null)
            {
                Console.WriteLine($"[SOPModuleView] 保存检测配置到MainViewModel: {_detectionModeConfig.DetectionMode}, 手部检测={_detectionModeConfig.EnableHandPoseEstimation}");
                viewModel.SaveSOPDetectionConfig(
                    _detectionModeConfig.DetectionMode,
                    _detectionModeConfig.EnableHandPoseEstimation,
                    _detectionModeConfig.MaxNumHands,
                    _detectionModeConfig.EnableFaceFilter,
                    _detectionModeConfig.FaceFilterUpperRatio,
                    _detectionModeConfig.EnableHandStructureCheck,
                    _detectionModeConfig.HandStructureWristTipRatio,
                    _detectionModeConfig.DetectionConfidenceThreshold,
                    _detectionModeConfig.MinBoxAreaRatio,
                    _detectionModeConfig.RotationAugmentation
                );
            }

            // 如果 SOPModule 已创建，立即更新它的配置
            if (viewModel?.SOPModuleInstance != null)
            {
                Console.WriteLine($"[SOPModuleView] 更新已存在的SOPModule实例: {mode}");

                // 更新SOP模块配置
                viewModel.SOPModuleInstance.UpdateDetectionMode(mode, _detectionModeConfig.EnableHandPoseEstimation);

                // 如果启用手部检测，更新手部检测配置
                if (_detectionModeConfig.EnableHandPoseEstimation)
                {
                    viewModel.SOPModuleInstance.UpdateHandPoseConfig(new VisionInspection.Modules.SOP.Models.HandPoseEstimationConfig
                    {
                        MaxNumHands = _detectionModeConfig.MaxNumHands,
                        UseGpu = _detectionModeConfig.UseGpu,
                        ConfidenceThreshold = 0.5f,
                        EnableFaceFilter = _detectionModeConfig.EnableFaceFilter,
                        FaceFilterUpperRatio = _detectionModeConfig.FaceFilterUpperRatio,
                        EnableHandStructureCheck = _detectionModeConfig.EnableHandStructureCheck,
                        HandStructureWristTipRatio = _detectionModeConfig.HandStructureWristTipRatio,
                        DetectionConfidenceThreshold = _detectionModeConfig.DetectionConfidenceThreshold,
                        MinBoxAreaRatio = _detectionModeConfig.MinBoxAreaRatio,
                        RotationAugmentation = _detectionModeConfig.RotationAugmentation
                    });
                }
            }
        }

        /// <summary>
        /// 运行按钮点击
        /// </summary>
        private async void RunButton_Click(object sender, RoutedEventArgs e)
        {
            // 连接到主视图的 SOP 实时检测
            var viewModel = GetMainViewModel();
            if (viewModel == null)
            {
                MessageBox.Show("无法获取主视图模型", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // 如果已经在运行，则停止
            if (viewModel.IsSOPDetecting)
            {
                _ = viewModel.StopSOPDetectionAsync();
                SetStatus("检测已停止", new SolidColorBrush(Colors.Gray));
                SetResult("--", new SolidColorBrush(Colors.Gray));
                StopTimer();
                UpdateRunButtonState(false);
                AddLog("SOP 实时检测已停止");
                return;
            }

            // 检查相机状态 - 直接使用 CameraManager 单例
            var cameraManager = CameraManager.Instance;
            if (!cameraManager.IsConnected)
            {
                MessageBox.Show("请先连接相机", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!cameraManager.IsGrabbing)
            {
                MessageBox.Show("请先开始相机采集", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 启动 SOP 实时检测
            SetStatus("启动中...", new SolidColorBrush(Color.FromRgb(24, 144, 255)));
            AddLog("正在启动 SOP 实时检测...");

            // 传递当前YAML工作流路径给MainViewModel
            viewModel.SopWorkflowPath = _currentYamlPath;

            await viewModel.StartSOPDetectionCommand.ExecuteAsync(null);

            // 更新 UI 状态
            if (viewModel.IsSOPDetecting)
            {
                SetStatus("实时检测中", new SolidColorBrush(Color.FromRgb(82, 196, 26)));
                SetResult("检测中...", new SolidColorBrush(Colors.Gray));
                ClearStepDurations();
                StartTimer();
                UpdateRunButtonState(true);
                AddLog($"SOP 实时检测已启动 | 模式: {viewModel.SOPModuleInstance?.DetectionMode}");
            }
            else
            {
                SetStatus("启动失败", new SolidColorBrush(Colors.Red));
                SetResult("NG", new SolidColorBrush(Colors.Red));
                UpdateRunButtonState(false);
                AddLog("SOP 实时检测启动失败");
            }

            // 订阅 SOP 事件以更新结果
            SubscribeToSOPEvents(viewModel);
        }

        /// <summary>
        /// 订阅 SOP 事件
        /// </summary>
        private void SubscribeToSOPEvents(MainViewModel viewModel)
        {
            if (viewModel.SOPModuleInstance == null) return;

            // 取消旧订阅
            UnsubscribeFromSOPEvents(viewModel);

            // 订阅新事件
            viewModel.SOPModuleInstance.WorkflowCompleted += OnSOPWorkflowCompleted;
            viewModel.SOPModuleInstance.ViolationDetected += OnSOPViolationDetected;
            viewModel.SOPModuleInstance.StepChanged += OnSOPStepChanged;
        }

        /// <summary>
        /// 取消 SOP 事件订阅
        /// </summary>
        private void UnsubscribeFromSOPEvents(MainViewModel viewModel)
        {
            if (viewModel.SOPModuleInstance == null) return;

            viewModel.SOPModuleInstance.WorkflowCompleted -= OnSOPWorkflowCompleted;
            viewModel.SOPModuleInstance.ViolationDetected -= OnSOPViolationDetected;
            viewModel.SOPModuleInstance.StepChanged -= OnSOPStepChanged;
        }

        /// <summary>
        /// 更新运行按钮状态
        /// </summary>
        private void UpdateRunButtonState(bool isRunning)
        {
            if (isRunning)
            {
                RunButton.Content = "⏹️ 停止";
                RunButton.Background = new SolidColorBrush(Color.FromRgb(255, 77, 79)); // 红色
            }
            else
            {
                RunButton.Content = "▶️ 运行";
                RunButton.Background = new SolidColorBrush(Color.FromRgb(82, 196, 26)); // 绿色
            }
        }

        /// <summary>
        /// SOP 工作流完成事件处理
        /// </summary>
        private void OnSOPWorkflowCompleted(object? sender, VisionInspection.Modules.SOP.Models.SOPCompletedEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                StopTimer();
                // 将步骤时间轴的耗时统计合并到上方流程卡片
                SyncStepDurationsFromStateMachine();
                var isPass = !e.HasViolations;
                if (isPass)
                {
                    SetResult("PASS", new SolidColorBrush(Color.FromRgb(82, 196, 26)));
                    SetStatus("检测完成", new SolidColorBrush(Color.FromRgb(82, 196, 26)));
                    AddLog($"✅ SOP 检测全部通过 | 总耗时: {TimerTextBlock.Text}");
                }
                else
                {
                    SetResult("FAIL", new SolidColorBrush(Colors.Red));
                    SetStatus("检测失败", new SolidColorBrush(Colors.Red));
                    AddLog($"❌ SOP 检测不通过，违规数: {e.Violations.Count} | 总耗时: {TimerTextBlock.Text}");
                }

                // ⭐ 修复：最后一步完成时 SOPStateMachine.CompleteCurrentStep 走 Completed 分支，
                // 不会触发 StepChanged 事件；这里手动把最后一步 UI 标为 Completed，
                // 否则步骤 5 会一直显示"进行中"。
                if (_steps.Count > 0)
                {
                    SetStepResult(_steps.Count - 1, isPass);
                }
            });
        }

        /// <summary>
        /// SOP 违规检测事件处理
        /// </summary>
        private void OnSOPViolationDetected(object? sender, VisionInspection.Modules.SOP.Models.ViolationEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                SetResult("NG", new SolidColorBrush(Colors.Red));
                AddLog($"⚠ 违规: [{e.Violation.Type}] {e.Violation.Description}");
            });
        }

        /// <summary>
        /// SOP 步骤变化事件处理
        /// </summary>
        private void OnSOPStepChanged(object? sender, VisionInspection.Modules.SOP.Models.StepChangedEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                SetCurrentStep(e.CurrentStepId - 1); // 转换为0-based索引
                AddLog($"步骤推进: {e.PreviousStepId} → {e.CurrentStepId} ({e.StepName})");
                // 同步已结束步骤的耗时到对应卡片
                SyncStepDurationsFromStateMachine();
            });
        }

        /// <summary>
        /// 运行模拟
        /// </summary>
        private async void RunSimulation()
        {
            SetStatus("运行中", new SolidColorBrush(Color.FromRgb(24, 144, 255)));
            SetResult("检测中...", new SolidColorBrush(Colors.Gray));
            AddLog("开始SOP检测流程...");

            for (int i = 0; i < _steps.Count; i++)
            {
                SetCurrentStep(i);
                AddLog($"执行步骤 {i + 1}: {_steps[i].Name}...");

                // 模拟处理时间
                await System.Threading.Tasks.Task.Delay(1500);

                // 模拟结果（90%成功率）
                var success = new Random().Next(10) > 0;
                SetStepResult(i, success, success ? "成功" : "失败");

                if (success)
                {
                    AddLog($"步骤 {i + 1} 完成: {_steps[i].Name}");
                }
                else
                {
                    AddLog($"步骤 {i + 1} 失败: {_steps[i].Name}");
                    SetStatus("检测失败", new SolidColorBrush(Colors.Red));
                    SetResult("NG", new SolidColorBrush(Colors.Red));
                    return;
                }
            }

            SetStatus("检测完成", new SolidColorBrush(Color.FromRgb(82, 196, 26)));
            SetResult("OK", new SolidColorBrush(Color.FromRgb(82, 196, 26)));
            AddLog("SOP检测流程完成，结果: OK");
        }

        #region 步骤耗时同步

        /// <summary>
        /// 从 SOP 状态机 StepHistory 同步各步骤实际耗时到上方流程卡片
        /// </summary>
        private void SyncStepDurationsFromStateMachine()
        {
            var vm = GetMainViewModel();
            var history = vm?.SOPModuleInstance?.StateMachine?.StepHistory;
            if (history == null) return;

            bool changed = false;
            foreach (var record in history)
            {
                var idx = record.StepId - 1;
                if (idx >= 0 && idx < _steps.Count && _steps[idx].Duration != record.Duration)
                {
                    _steps[idx].Duration = record.Duration;
                    changed = true;
                }
            }

            if (changed)
                UpdateStepDisplay();
        }

        /// <summary>
        /// 清空所有步骤的耗时显示（每次重新运行前调用）
        /// </summary>
        private void ClearStepDurations()
        {
            bool changed = false;
            foreach (var step in _steps)
            {
                if (step.Duration > TimeSpan.Zero)
                {
                    step.Duration = TimeSpan.Zero;
                    changed = true;
                }
            }

            if (changed)
                UpdateStepDisplay();
        }

        #endregion
    }

    /// <summary>
    /// SOP步骤项
    /// </summary>
    public class SOPStepItem
    {
        public string Id { get; set; } = "";
        public string StepNumber { get; set; } = "";
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public string Icon { get; set; } = "";
        public StepStatus Status { get; set; }
        public string ResultMessage { get; set; } = "";
        /// <summary>该步骤实际耗时（从状态机 StepHistory 同步）</summary>
        public TimeSpan Duration { get; set; }
    }

    /// <summary>
    /// 步骤状态
    /// </summary>
    public enum StepStatus
    {
        Pending,
        Running,
        Completed,
        Failed
    }

    /// <summary>
    /// 产品配方下拉项
    /// </summary>
    public class RecipeItem
    {
        public string DisplayName { get; set; } = "";
        public string FullPath { get; set; } = "";
        public override string ToString() => DisplayName;
    }
}
