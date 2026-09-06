using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VisionInspection.Modules.SOP.Models;
using VisionInspection.UI.Services;
using VisionInspection.UI.ViewModels;

namespace VisionInspection.UI.Views
{
    /// <summary>
    /// SOP 流程预览与编辑窗口：
    /// 直接读写 YAML 的完整配置模型（SOPYamlConfig），支持预览/修改步骤、全局设置、
    /// 模型与监控配置；区域坐标仍由「🎯 标定区域」工具维护，此处仅预览。
    /// </summary>
    public partial class SOPConfigWindow : Window
    {
        private SOPYamlConfig? _config;      // 完整 YAML 模型（steps/regions/model/monitoring/settings 全保留）
        private string _yamlPath = "";
        private int _selectedIndex = -1;     // 当前编辑的步骤索引
        private bool _isLoading;             // 程序填充控件时屏蔽事件
        private bool _hadModel;              // 原配置是否存在 model 节点
        private bool _hadMonitoring;         // 原配置是否存在 monitoring 节点

        /// <summary>保存成功后最终写入的 YAML 路径（供调用方重载工作流）</summary>
        public string? SavedYamlPath { get; private set; }

        // ===== 运行时手部检测参数（不写入 YAML，供 SOPModuleView 读取）=====
        public int HandInferenceInterval { get; private set; } = 2;
        public int HandSmoothWindowSize { get; private set; } = 5;
        public float HandSmoothAlpha { get; private set; } = 0.7f;
        public float HandSkeletonThreshold { get; private set; } = 0.3f;
        public bool HandEnableFaceFilter { get; private set; } = true;
        public float HandFaceFilterUpperRatio { get; private set; } = 0.38f;
        public bool HandEnableStructureCheck { get; private set; } = true;
        public float HandStructureWristTipRatio { get; private set; } = 0.18f;
        public float HandDetectionConfidence { get; private set; } = 0.08f;
        public float HandMinBoxAreaRatio { get; private set; } = 0.0005f;
        public bool HandRotationAugmentation { get; private set; } = true;

        public SOPConfigWindow(string? yamlPath = null)
        {
            InitializeComponent();
            InitializeRuntimeDefaults();
            FillStepCameraModelCombos();
            RefreshYamlFilesList();

            if (!string.IsNullOrEmpty(yamlPath) && File.Exists(yamlPath))
            {
                try
                {
                    LoadYaml(yamlPath);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"加载 YAML 失败: {ex.Message}", "警告",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    SetEmptyState();
                }
            }
            else
            {
                SetEmptyState();
            }
        }

        /// <summary>运行时手部检测默认值（与旧版一致）</summary>
        private void InitializeRuntimeDefaults()
        {
            EnableHandPoseCheckBox.IsChecked = true;
            MaxHandsComboBox.SelectedIndex = 1; // 默认 2 只手
            UseGpuCheckBox.IsChecked = true;
        }

        // ==================== YAML 文件列表 ====================

        /// <summary>获取 SOP 配方目录（与 TryGetSopDirectory 一致，附带文件系统真实路径兜底）</summary>
        private string? GetSopDirectory()
        {
            var dir = TryGetSopDirectory();
            if (!string.IsNullOrEmpty(dir)) return dir;

            // 兜底：当前工作目录下查找 configs/sop
            try
            {
                var cwdDir = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "configs", "sop"));
                if (Directory.Exists(cwdDir)) return cwdDir;
            }
            catch { /* 忽略 */ }
            return null;
        }

        /// <summary>扫描 SOP 目录下所有 yaml/yml 文件，填充到 ComboBox</summary>
        private void RefreshYamlFilesList()
        {
            _isLoading = true;
            try
            {
                YamlFilesCombo.Items.Clear();
                var dir = GetSopDirectory();
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                {
                    YamlFilesCombo.Items.Add(new ComboBoxItem { Content = "（未找到 configs/sop 目录）", IsEnabled = false });
                    YamlFilesCombo.SelectedIndex = 0;
                    return;
                }

                var files = Directory.EnumerateFiles(dir, "*.yaml")
                    .Concat(Directory.EnumerateFiles(dir, "*.yml"))
                    .Distinct()
                    .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (files.Count == 0)
                {
                    YamlFilesCombo.Items.Add(new ComboBoxItem { Content = "（目录下没有 YAML 文件）", IsEnabled = false });
                    YamlFilesCombo.SelectedIndex = 0;
                    return;
                }

                foreach (var f in files)
                    YamlFilesCombo.Items.Add(new ComboBoxItem
                    {
                        Content = Path.GetFileName(f),
                        Tag = f,
                        ToolTip = f
                    });

                // 若当前已加载文件在列表中，则选中它
                if (!string.IsNullOrEmpty(_yamlPath))
                {
                    var match = YamlFilesCombo.Items.OfType<ComboBoxItem>()
                        .FirstOrDefault(i => string.Equals(i.Tag as string, _yamlPath, StringComparison.OrdinalIgnoreCase));
                    if (match != null)
                    {
                        YamlFilesCombo.SelectedItem = match;
                        return;
                    }
                }
                YamlFilesCombo.SelectedIndex = -1;
            }
            finally
            {
                _isLoading = false;
            }
        }

        private void YamlFilesCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoading) return;
            if (YamlFilesCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string path) return;

            if (!File.Exists(path))
            {
                MessageBox.Show($"文件不存在：{path}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                RefreshYamlFilesList();
                return;
            }

            // 已加载同一文件则跳过
            if (string.Equals(_yamlPath, path, StringComparison.OrdinalIgnoreCase)) return;

            try
            {
                LoadYaml(path);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"加载 YAML 失败: {ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void RefreshYamlButton_Click(object sender, RoutedEventArgs e)
        {
            RefreshYamlFilesList();
        }

        /// <summary>让 ComboBox 选中项与当前 _yamlPath 同步（不触发重新加载）</summary>
        private void SyncYamlComboSelection()
        {
            _isLoading = true;
            try
            {
                var match = YamlFilesCombo.Items.OfType<ComboBoxItem>()
                    .FirstOrDefault(i => string.Equals(i.Tag as string, _yamlPath, StringComparison.OrdinalIgnoreCase));
                YamlFilesCombo.SelectedItem = match; // 找不到则清空选中
            }
            finally
            {
                _isLoading = false;
            }
        }

        private void SetEmptyState()
        {
            MainContentGrid.IsEnabled = false;
            FilePathText.Text = "未加载文件 — 请点「📂 打开YAML」选择配方";
            StepsListPanel.Children.Clear();
            StepsListPanel.Children.Add(new TextBlock
            {
                Text = "尚未加载 YAML 文件",
                Foreground = new SolidColorBrush(Color.FromRgb(153, 153, 153)),
                Margin = new Thickness(8, 10, 8, 0)
            });
        }

        // ==================== 加载 ====================

        private void LoadYaml(string path)
        {
            _config = SOPYamlConverter.LoadFullConfig(path);
            _yamlPath = path;
            SavedYamlPath = null;
            MainContentGrid.IsEnabled = true;
            FilePathText.Text = path;
            SyncYamlComboSelection();

            _isLoading = true;
            try
            {
                LoadFlowInfoTab();
                LoadModelMonitoringTab();
                RefreshRegionsPanel();
                RefreshStepsList();
                var sop = _config.Sop ?? new SopyamlSop();
                SelectStep(sop.Steps.Count > 0 ? 0 : -1, commitCurrent: false);
            }
            finally
            {
                _isLoading = false;
            }
        }

        private void OpenButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Filter = "YAML 文件 (*.yaml;*.yml)|*.yaml;*.yml|所有文件 (*.*)|*.*",
                Title = "选择 SOP YAML 文件",
                InitialDirectory = TryGetSopDirectory()
            };

            if (dialog.ShowDialog() != true) return;

            try
            {
                LoadYaml(dialog.FileName);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"加载 YAML 失败: {ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private string? TryGetSopDirectory()
        {
            try
            {
                var dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "configs", "sop");
                if (Directory.Exists(dir)) return dir;
                var srcDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "configs", "sop");
                if (Directory.Exists(srcDir)) return Path.GetFullPath(srcDir);
            }
            catch { /* 忽略，让对话框用默认目录 */ }
            return null;
        }

        // ==================== 步骤列表 ====================

        private List<SopyamlStep> Steps => _config?.Sop?.Steps ?? new List<SopyamlStep>();

        private void RefreshStepsList()
        {
            StepsListPanel.Children.Clear();
            var steps = Steps;
            StepsHeader.Text = $"步骤列表 ({steps.Count})";

            for (int i = 0; i < steps.Count; i++)
                StepsListPanel.Children.Add(CreateStepCard(steps[i], i));

            if (steps.Count == 0)
            {
                StepsListPanel.Children.Add(new TextBlock
                {
                    Text = "（无步骤，点下方「➕ 添加步骤」新建）",
                    Foreground = new SolidColorBrush(Color.FromRgb(153, 153, 153)),
                    Margin = new Thickness(8, 10, 8, 0)
                });
            }
        }

        private Border CreateStepCard(SopyamlStep step, int index)
        {
            bool selected = index == _selectedIndex;
            var border = new Border
            {
                Background = selected ? new SolidColorBrush(Color.FromRgb(230, 247, 255)) : Brushes.White,
                BorderBrush = selected ? new SolidColorBrush(Color.FromRgb(24, 144, 255)) : new SolidColorBrush(Color.FromRgb(232, 232, 232)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(0, 0, 0, 8),
                Padding = new Thickness(10, 8, 10, 8),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            border.MouseLeftButtonUp += (s, e) => SelectStep(index);

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // 序号
            var numberText = new TextBlock
            {
                Text = (index + 1).ToString(),
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(24, 144, 255)),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(numberText, 0);
            grid.Children.Add(numberText);

            // 名称 + 摘要
            var namePanel = new StackPanel { Margin = new Thickness(8, 0, 8, 0) };
            namePanel.Children.Add(new TextBlock
            {
                Text = step.Name,
                FontWeight = FontWeights.Bold,
                FontSize = 13,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            namePanel.Children.Add(new TextBlock
            {
                Text = $"{step.Id} · {step.Detection?.Method ?? "无检测"} · 超时{(step.Timeout > 0 ? step.Timeout + "s" : "不限")}",
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(153, 153, 153)),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            Grid.SetColumn(namePanel, 1);
            grid.Children.Add(namePanel);

            // 操作按钮：编辑/上移/下移/删除
            var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            Button MakeButton(string content, string tooltip, bool enabled, RoutedEventHandler onClick)
            {
                var btn = new Button
                {
                    Content = content,
                    Width = 26,
                    Height = 26,
                    Margin = new Thickness(1),
                    Padding = new Thickness(0),
                    ToolTip = tooltip,
                    IsEnabled = enabled,
                    FontSize = 12
                };
                btn.Click += onClick;
                return btn;
            }

            buttonPanel.Children.Add(MakeButton("↑", "上移", index > 0, (s, e) => MoveStep(index, -1)));
            buttonPanel.Children.Add(MakeButton("↓", "下移", index < Steps.Count - 1, (s, e) => MoveStep(index, 1)));
            buttonPanel.Children.Add(MakeButton("🗑️", "删除", true, (s, e) => DeleteStep(index)));
            Grid.SetColumn(buttonPanel, 2);
            grid.Children.Add(buttonPanel);

            border.Child = grid;
            return border;
        }

        // ==================== 步骤编辑 ====================

        /// <summary>
        /// 填充步骤级"相机/模型"下拉：相机 = 主相机 + cam_2..cam_4（显示已连接槽位名）；
        /// 模型 = 扫描 yolo_models 目录下的 .onnx（含"（默认：全局模型）"空选项）。
        /// </summary>
        private void FillStepCameraModelCombos()
        {
            // 相机
            StepCameraCombo.Items.Clear();
            StepCameraCombo.Items.Add(new CameraOptionItem("main_camera", "主相机（相机1）"));
            var slots = Core.Services.CameraManager.Instance.Slots;
            for (int i = 2; i <= Core.Services.CameraManager.MaxCameraCount; i++)
            {
                string id = $"cam_{i}";
                var slot = slots.FirstOrDefault(s => string.Equals(s.CameraId, id, StringComparison.OrdinalIgnoreCase));
                string display = slot != null && !string.IsNullOrEmpty(slot.DisplayName)
                    ? $"{id}（{slot.DisplayName}）"
                    : $"{id}（未连接）";
                StepCameraCombo.Items.Add(new CameraOptionItem(id, display));
            }
            StepCameraCombo.SelectedIndex = 0;

            // 模型
            StepModelCombo.Items.Clear();
            StepModelCombo.Items.Add(new ModelOptionItem { Display = "（默认：全局模型）", Value = "" });
            try
            {
                foreach (var file in new ModelManager().ScanOnnxFiles())
                {
                    var idx = file.IndexOf("yolo_models", StringComparison.OrdinalIgnoreCase);
                    string rel = idx >= 0 ? file.Substring(idx).Replace('\\', '/') : file;
                    StepModelCombo.Items.Add(new ModelOptionItem { Display = rel, Value = rel });
                }
            }
            catch
            {
                // 扫描失败不影响窗口使用，仅保留"默认：全局模型"
            }
            StepModelCombo.SelectedIndex = 0;
        }

        /// <summary>按值选中 ComboBox 项（相机按 Id、模型按 Value）；未命中则选中第一项</summary>
        private static void SelectComboItemByValue(ComboBox combo, string value)
        {
            foreach (var item in combo.Items)
            {
                if (item is CameraOptionItem c && string.Equals(c.Id, value, StringComparison.OrdinalIgnoreCase))
                {
                    combo.SelectedItem = item;
                    return;
                }
                if (item is ModelOptionItem m && string.Equals(m.Value ?? "", value ?? "", StringComparison.OrdinalIgnoreCase))
                {
                    combo.SelectedItem = item;
                    return;
                }
            }
            combo.SelectedIndex = 0;
        }

        private void SelectStep(int index, bool commitCurrent = true)
        {
            var steps = Steps;
            if (index < 0 || index >= steps.Count)
            {
                _selectedIndex = -1;
                ClearStepEditor();
                RefreshStepsList();
                return;
            }

            // 切换前先提交当前编辑内容，避免丢失
            if (commitCurrent && _selectedIndex >= 0 && _selectedIndex != index && _selectedIndex < steps.Count)
                ApplyEditorToStep();

            _selectedIndex = index;
            _isLoading = true;
            try
            {
                LoadStepToEditor(steps[index]);
            }
            finally
            {
                _isLoading = false;
            }
            RefreshStepsList();
        }

        private void LoadStepToEditor(SopyamlStep step)
        {
            StepIdTextBox.Text = step.Id ?? "";
            StepNameTextBox.Text = step.Name ?? "";
            StepDescTextBox.Text = step.Description ?? "";
            TimeoutTextBox.Text = step.Timeout.ToString();
            bool timeoutEnabled = step.Timeout > 0;
            EnableTimeoutCheckBox.IsChecked = timeoutEnabled;
            TimeoutTextBox.IsEnabled = timeoutEnabled;
            TransitionsTextBox.Text = string.Join(", ", step.Transitions ?? new List<string>());

            var d = step.Detection ?? new SopyamlDetection();
            SelectComboByTag(DetectionMethodCombo, d.Method);
            TargetObjectTextBox.Text = d.TargetObject ?? "";
            SelectComboByTag(HandCombo, d.Hand ?? "any");
            SelectComboByTag(ActionCombo, d.Action ?? "pickup");
            RegionCombo.Text = d.Region ?? "";
            FromRegionCombo.Text = d.FromRegion ?? "";
            ToRegionCombo.Text = d.ToRegion ?? "";
            ToleranceTextBox.Text = d.Tolerance.ToString();
            DurationMsTextBox.Text = d.DurationMs.ToString();
            MinConfTextBox.Text = d.MinConfidence.ToString("0.##");
            StableFramesTextBox.Text = d.StableFrames.ToString();
            DetectionDescTextBox.Text = d.Description ?? "";

            ForbiddenTextBox.Text = string.Join(", ", step.ForbiddenObjects ?? new List<string>());
            MustKeepTextBox.Text = string.Join(", ", step.MustKeep ?? new List<string>());

            // ⭐ 步骤级相机/模型
            SelectComboItemByValue(StepCameraCombo, string.IsNullOrWhiteSpace(step.Camera) ? "main_camera" : step.Camera);
            SelectComboItemByValue(StepModelCombo, step.Model ?? "");

            UpdateDetectionFieldVisibility();
        }

        private void ClearStepEditor()
        {
            StepIdTextBox.Text = "";
            StepNameTextBox.Text = "";
            StepDescTextBox.Text = "";
            TimeoutTextBox.Text = "";
            EnableTimeoutCheckBox.IsChecked = true;
            TimeoutTextBox.IsEnabled = true;
            TransitionsTextBox.Text = "";
            TargetObjectTextBox.Text = "";
            RegionCombo.Text = "";
            FromRegionCombo.Text = "";
            ToRegionCombo.Text = "";
            ToleranceTextBox.Text = "";
            DurationMsTextBox.Text = "";
            MinConfTextBox.Text = "";
            StableFramesTextBox.Text = "";
            DetectionDescTextBox.Text = "";
            ForbiddenTextBox.Text = "";
            MustKeepTextBox.Text = "";
            // ⭐ 步骤级相机/模型复位为默认
            StepCameraCombo.SelectedIndex = 0;
            StepModelCombo.SelectedIndex = 0;
        }

        /// <summary>把编辑器内容写回当前选中的步骤（容错：非法输入保持原值）</summary>
        private void ApplyEditorToStep()
        {
            var steps = Steps;
            if (_selectedIndex < 0 || _selectedIndex >= steps.Count || _config?.Sop == null) return;
            var step = steps[_selectedIndex];

            step.Name = string.IsNullOrWhiteSpace(StepNameTextBox.Text) ? step.Name : StepNameTextBox.Text.Trim();
            step.Id = string.IsNullOrWhiteSpace(StepIdTextBox.Text)
                ? $"step_{_selectedIndex + 1}"
                : StepIdTextBox.Text.Trim();
            step.Description = string.IsNullOrWhiteSpace(StepDescTextBox.Text) ? null : StepDescTextBox.Text.Trim();
            // 未勾选"启用本步超时"或填 0 → 该步骤不限制超时
            if (EnableTimeoutCheckBox.IsChecked == true
                && int.TryParse(TimeoutTextBox.Text, out var timeout) && timeout > 0)
                step.Timeout = timeout;
            else
                step.Timeout = 0;
            step.Transitions = SplitList(TransitionsTextBox.Text) ?? new List<string>();

            step.Detection ??= new SopyamlDetection();
            var d = step.Detection;
            d.Method = GetSelectedMethod();

            // 仅更新当前方法相关的字段，无关字段保持原值
            if (TargetObjectPanel.Visibility == Visibility.Visible)
                d.TargetObject = string.IsNullOrWhiteSpace(TargetObjectTextBox.Text) ? null : TargetObjectTextBox.Text.Trim();
            if (HandPanel.Visibility == Visibility.Visible)
                d.Hand = GetSelectedTag(HandCombo) ?? "any";
            if (ActionPanel.Visibility == Visibility.Visible)
                d.Action = GetSelectedTag(ActionCombo) ?? "pickup";
            if (RegionPanel.Visibility == Visibility.Visible)
                d.Region = string.IsNullOrWhiteSpace(RegionCombo.Text) ? null : RegionCombo.Text.Trim();
            if (FromToPanel.Visibility == Visibility.Visible)
            {
                d.FromRegion = string.IsNullOrWhiteSpace(FromRegionCombo.Text) ? null : FromRegionCombo.Text.Trim();
                d.ToRegion = string.IsNullOrWhiteSpace(ToRegionCombo.Text) ? null : ToRegionCombo.Text.Trim();
            }
            if (TolerancePanel.Visibility == Visibility.Visible && float.TryParse(ToleranceTextBox.Text, out var tol))
                d.Tolerance = tol;
            if (DurationPanel.Visibility == Visibility.Visible && int.TryParse(DurationMsTextBox.Text, out var dur))
                d.DurationMs = dur;
            if (float.TryParse(MinConfTextBox.Text, out var conf)) d.MinConfidence = conf;
            if (int.TryParse(StableFramesTextBox.Text, out var sf)) d.StableFrames = sf;
            d.Description = string.IsNullOrWhiteSpace(DetectionDescTextBox.Text) ? null : DetectionDescTextBox.Text.Trim();

            step.ForbiddenObjects = SplitList(ForbiddenTextBox.Text);
            step.MustKeep = SplitList(MustKeepTextBox.Text);

            // ⭐ 步骤级相机/模型（主相机与空模型不写，保持缺省语义）
            step.Camera = StepCameraCombo.SelectedItem is CameraOptionItem camSel && camSel.Id != "main_camera"
                ? camSel.Id
                : null;
            step.Model = StepModelCombo.SelectedItem is ModelOptionItem modelSel && !string.IsNullOrWhiteSpace(modelSel.Value)
                ? modelSel.Value.Trim()
                : null;
        }

        private void ApplyStepButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedIndex < 0)
            {
                MessageBox.Show("请先在左侧列表选择一个步骤。", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (_config?.Sop == null || string.IsNullOrEmpty(_yamlPath))
            {
                MessageBox.Show("请先点「📂 打开YAML」加载一个配方文件。", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // 将当前编辑内容写回模型并立即保存到 YAML 文件（不关闭窗口，可继续编辑）
            try
            {
                CommitAllTabs();
                SOPYamlConverter.SaveFullConfig(_yamlPath, _config);
                SavedYamlPath = _yamlPath;
                RefreshStepsList();
                MessageBox.Show($"修改已保存到:\n{_yamlPath}", "保存成功",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"保存失败: {ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ==================== 步骤增删移动 ====================

        private void AddStepButton_Click(object sender, RoutedEventArgs e)
        {
            if (_config?.Sop == null) return;
            ApplyEditorToStep(); // 提交当前编辑

            var steps = _config.Sop.Steps;
            var step = new SopyamlStep
            {
                Id = $"step_{steps.Count + 1}",
                Name = $"新步骤{steps.Count + 1}",
                Description = "",
                Timeout = 30,
                Detection = new SopyamlDetection
                {
                    Method = "object_present",
                    MinConfidence = 0.6f,
                    StableFrames = 5
                }
            };
            steps.Add(step);
            SelectStep(steps.Count - 1, commitCurrent: false);
        }

        private void DeleteStep(int index)
        {
            var steps = Steps;
            if (index < 0 || index >= steps.Count) return;

            if (MessageBox.Show($"确定要删除步骤 \"{steps[index].Name}\" 吗？", "确认删除",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

            steps.RemoveAt(index);
            if (_selectedIndex == index)
            {
                // 删除的是当前编辑步骤 → 选中相邻步骤
                _selectedIndex = -1;
                SelectStep(Math.Min(index, steps.Count - 1), commitCurrent: false);
            }
            else
            {
                if (_selectedIndex > index) _selectedIndex--;
                RefreshStepsList();
            }
        }

        private void MoveStep(int index, int direction)
        {
            var steps = Steps;
            int newIndex = index + direction;
            if (newIndex < 0 || newIndex >= steps.Count) return;

            ApplyEditorToStep(); // 提交当前编辑
            (steps[index], steps[newIndex]) = (steps[newIndex], steps[index]);

            _selectedIndex = newIndex;
            _isLoading = true;
            try { LoadStepToEditor(steps[newIndex]); }
            finally { _isLoading = false; }
            RefreshStepsList();
        }

        // ==================== 流程信息 Tab ====================

        private void LoadFlowInfoTab()
        {
            var sop = _config!.Sop!;
            FlowNameTextBox.Text = sop.Name ?? "";
            FlowVersionTextBox.Text = sop.Version ?? "";
            FlowDescTextBox.Text = sop.Description ?? "";

            sop.Settings ??= new SopyamlSettings();
            var s = sop.Settings;
            SettingsConfTextBox.Text = s.ConfidenceThreshold.ToString("0.##");
            SettingsStableTextBox.Text = s.StableFrames.ToString();
            SettingsTimeoutTextBox.Text = s.TimeoutSeconds.ToString();
            ResetDelayTextBox.Text = s.ResetDelaySec.ToString();
            SettingsSkipCheckBox.IsChecked = s.EnableSkipDetection;
            SettingsTimeoutDetectCheckBox.IsChecked = s.EnableTimeoutDetection;
            AutoResetCheckBox.IsChecked = s.AutoResetOnComplete;
        }

        private void ApplyFlowInfoTab()
        {
            var sop = _config!.Sop!;
            sop.Name = string.IsNullOrWhiteSpace(FlowNameTextBox.Text) ? sop.Name : FlowNameTextBox.Text.Trim();
            sop.Version = string.IsNullOrWhiteSpace(FlowVersionTextBox.Text) ? sop.Version : FlowVersionTextBox.Text.Trim();
            sop.Description = string.IsNullOrWhiteSpace(FlowDescTextBox.Text) ? null : FlowDescTextBox.Text.Trim();

            sop.Settings ??= new SopyamlSettings();
            var s = sop.Settings;
            if (float.TryParse(SettingsConfTextBox.Text, out var conf)) s.ConfidenceThreshold = conf;
            if (int.TryParse(SettingsStableTextBox.Text, out var sf)) s.StableFrames = sf;
            if (int.TryParse(SettingsTimeoutTextBox.Text, out var ts)) s.TimeoutSeconds = ts;
            if (int.TryParse(ResetDelayTextBox.Text, out var rd)) s.ResetDelaySec = rd;
            s.EnableSkipDetection = SettingsSkipCheckBox.IsChecked ?? true;
            s.EnableTimeoutDetection = SettingsTimeoutDetectCheckBox.IsChecked ?? true;
            s.AutoResetOnComplete = AutoResetCheckBox.IsChecked ?? true;
        }

        // ==================== 区域与模型 Tab ====================

        private void RefreshRegionsPanel()
        {
            RegionsPanel.Children.Clear();
            var regions = _config?.Sop?.Regions;
            var keys = new List<string>();

            if (regions == null || regions.Count == 0)
            {
                RegionsPanel.Children.Add(new TextBlock
                {
                    Text = "（无区域 — 请用「🎯 标定区域」工具标定后重新打开）",
                    Foreground = new SolidColorBrush(Color.FromRgb(153, 153, 153)),
                    FontSize = 12
                });
            }
            else
            {
                foreach (var (regionId, r) in regions)
                {
                    keys.Add(regionId);
                    var card = new Border
                    {
                        Background = new SolidColorBrush(Color.FromRgb(250, 250, 250)),
                        BorderBrush = new SolidColorBrush(Color.FromRgb(232, 232, 232)),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(4),
                        Padding = new Thickness(10, 6, 10, 6),
                        Margin = new Thickness(0, 0, 0, 6)
                    };
                    card.Child = new TextBlock
                    {
                        Text = $"{regionId}  {r.Name ?? ""}   ({r.X1},{r.Y1}) - ({r.X2},{r.Y2})",
                        FontSize = 12
                    };
                    RegionsPanel.Children.Add(card);
                }
            }

            keys.Sort(StringComparer.OrdinalIgnoreCase);
            RegionCombo.ItemsSource = keys;
            FromRegionCombo.ItemsSource = keys;
            ToRegionCombo.ItemsSource = keys;
        }

        private void LoadModelMonitoringTab()
        {
            var sop = _config!.Sop!;

            // 模型：原配置没有时展示空，不主动创建节点
            var md = sop.Model;
            _hadModel = md != null;
            md ??= new SopyamlModel();
            FillModelPathCombo(); // ⭐ 扫描 yolo_models 目录填充模型下拉
            ModelPathCombo.Text = _hadModel ? md.Path : "";
            SelectComboByTag(ModelTypeCombo, md.Type);
            ModelConfTextBox.Text = md.Confidence.ToString("0.##");
            ModelIouTextBox.Text = md.Iou.ToString("0.##");
            ModelGpuCheckBox.IsChecked = md.UseGpu;
            ModelGpuIdTextBox.Text = md.GpuId.ToString();
            ModelClassesTextBox.Text = string.Join(", ", md.Classes ?? new List<string>());

            // 监控：同上，原配置没有时默认不启用，不主动创建节点
            var m = sop.Monitoring;
            _hadMonitoring = m != null;
            m ??= new SopyamlMonitoring { Enabled = false };
            MonitorEnabledCheckBox.IsChecked = _hadMonitoring && m.Enabled;
            PersonClassesTextBox.Text = string.Join(", ", m.PersonClasses ?? new List<string>());
            HelmetClassesTextBox.Text = string.Join(", ", m.HelmetClasses ?? new List<string>());
            MonitorRegionsTextBox.Text = m.Regions == null ? "" : string.Join(", ", m.Regions);
            MonitorConfTextBox.Text = m.MinConfidence.ToString("0.##");
            CooldownTextBox.Text = m.CooldownSeconds.ToString("0.##");
        }

        /// <summary>
        /// 填充模型路径下拉：扫描 yolo_models 目录下的 .onnx（相对路径形式，与配方 sop.model.path 风格一致）。
        /// </summary>
        private void FillModelPathCombo()
        {
            ModelPathCombo.Items.Clear();
            try
            {
                foreach (var file in new ModelManager().ScanOnnxFiles())
                {
                    var idx = file.IndexOf("yolo_models", StringComparison.OrdinalIgnoreCase);
                    string rel = idx >= 0 ? file.Substring(idx).Replace('\\', '/') : file;
                    ModelPathCombo.Items.Add(rel);
                }
            }
            catch
            {
                // 扫描失败不影响窗口使用
            }
        }

        private void ApplyModelMonitoringTab()
        {
            var sop = _config!.Sop!;

            // 模型：原本有节点、或用户填写了路径、或填写了类别，才写回（避免"只填类别不填路径"时丢失）
            if (_hadModel
                || !string.IsNullOrWhiteSpace(ModelPathCombo.Text)
                || !string.IsNullOrWhiteSpace(ModelClassesTextBox.Text))
            {
                var md = sop.Model ?? new SopyamlModel();
                md.Path = ModelPathCombo.Text.Trim();
                md.Type = GetSelectedTag(ModelTypeCombo) ?? "ObjectDetection";
                if (float.TryParse(ModelConfTextBox.Text, out var conf)) md.Confidence = conf;
                if (float.TryParse(ModelIouTextBox.Text, out var iou)) md.Iou = iou;
                md.UseGpu = ModelGpuCheckBox.IsChecked ?? true;
                if (int.TryParse(ModelGpuIdTextBox.Text, out var gpuId)) md.GpuId = gpuId;
                md.Classes = SplitList(ModelClassesTextBox.Text) ?? new List<string>();
                sop.Model = md;
            }
            else
            {
                sop.Model = null;
            }

            // 监控：原本有节点或用户勾选启用才写回（项目约定：monitoring 任务需显式节点）
            if (_hadMonitoring || MonitorEnabledCheckBox.IsChecked == true)
            {
                var m = sop.Monitoring ?? new SopyamlMonitoring();
                m.Enabled = MonitorEnabledCheckBox.IsChecked ?? false;
                m.PersonClasses = SplitList(PersonClassesTextBox.Text) ?? new List<string>();
                m.HelmetClasses = SplitList(HelmetClassesTextBox.Text) ?? new List<string>();
                m.Regions = SplitList(MonitorRegionsTextBox.Text);
                if (float.TryParse(MonitorConfTextBox.Text, out var mc)) m.MinConfidence = mc;
                if (double.TryParse(CooldownTextBox.Text, out var cd)) m.CooldownSeconds = cd;
                sop.Monitoring = m;
            }
            else
            {
                sop.Monitoring = null;
            }
        }

        // ==================== 保存 ====================

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            if (_config?.Sop == null || string.IsNullOrEmpty(_yamlPath))
            {
                MessageBox.Show("请先点「📂 打开YAML」加载一个配方文件。", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                CommitAllTabs();
                SOPYamlConverter.SaveFullConfig(_yamlPath, _config);
                SavedYamlPath = _yamlPath;
                MessageBox.Show($"已保存 {Steps.Count} 个步骤到:\n{_yamlPath}\n\n关闭后主界面将自动重载工作流。",
                    "保存成功", MessageBoxButton.OK, MessageBoxImage.Information);
                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"保存失败: {ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SaveAsButton_Click(object sender, RoutedEventArgs e)
        {
            if (_config?.Sop == null)
            {
                MessageBox.Show("请先点「📂 打开YAML」加载一个配方文件。", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dialog = new SaveFileDialog
            {
                Filter = "YAML 文件 (*.yaml)|*.yaml|所有文件 (*.*)|*.*",
                Title = "另存为 SOP YAML",
                InitialDirectory = TryGetSopDirectory(),
                FileName = $"sop_new_{DateTime.Now:yyyyMMdd_HHmm}.yaml"
            };

            if (dialog.ShowDialog() != true) return;

            try
            {
                CommitAllTabs();
                SOPYamlConverter.SaveFullConfig(dialog.FileName, _config);
                _yamlPath = dialog.FileName;
                FilePathText.Text = _yamlPath;
                SavedYamlPath = _yamlPath;
                MessageBox.Show($"已另存到:\n{_yamlPath}", "保存成功",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"另存失败: {ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>保存前把所有 Tab 的编辑内容统一写回模型</summary>
        private void CommitAllTabs()
        {
            ApplyEditorToStep();
            ApplyFlowInfoTab();
            ApplyModelMonitoringTab();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = SavedYamlPath != null;
            Close();
        }

        // ==================== 检测方法字段联动 ====================

        private void DetectionMethodCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoading) return;
            UpdateDetectionFieldVisibility();
        }

        /// <summary>根据检测方法显示/隐藏相关字段</summary>
        private void UpdateDetectionFieldVisibility()
        {
            var m = GetSelectedMethod();

            Visibility V(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

            TargetObjectPanel.Visibility = V(IsIn(m, "person_present", "object_present", "object_in_zone", "hand_action", "hand_near_object"));
            HandPanel.Visibility = V(IsIn(m, "hand_in_region", "hand_not_in_region", "hand_stable", "hand_move", "hand_action", "hand_near_object"));
            ActionPanel.Visibility = V(IsIn(m, "hand_action"));
            RegionPanel.Visibility = V(IsIn(m, "hand_in_region", "hand_not_in_region", "object_in_zone"));
            FromToPanel.Visibility = V(IsIn(m, "hand_move", "hand_action"));
            TolerancePanel.Visibility = V(IsIn(m, "hand_stable", "hand_near_object", "pose_stable"));
            DurationPanel.Visibility = V(IsIn(m, "time_elapsed"));
        }

        private static bool IsIn(string value, params string[] set) => set.Contains(value);

        // ==================== 控件辅助 ====================

        private string GetSelectedMethod() => GetSelectedTag(DetectionMethodCombo) ?? "object_present";

        private static string? GetSelectedTag(ComboBox combo)
            => (combo.SelectedItem as ComboBoxItem)?.Tag as string;

        private static void SelectComboByTag(ComboBox combo, string? tag)
        {
            foreach (var item in combo.Items.OfType<ComboBoxItem>())
            {
                if ((item.Tag as string) == tag)
                {
                    combo.SelectedItem = item;
                    return;
                }
            }
            if (combo.Items.Count > 0 && !combo.IsEditable)
                combo.SelectedIndex = 0;
        }

        /// <summary>按逗号/分号切分列表文本；空文本返回 null（写入 YAML 时省略）</summary>
        private static List<string>? SplitList(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var items = text.Split(new[] { ',', '，', ';', '；' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(s => s.Length > 0)
                .ToList();
            return items.Count > 0 ? items : null;
        }

        // ==================== 运行时手部检测设置 ====================

        private void EnableHandPoseCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            if (MaxHandsComboBox != null)
                MaxHandsComboBox.IsEnabled = true;
        }

        private void EnableHandPoseCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            if (MaxHandsComboBox != null)
                MaxHandsComboBox.IsEnabled = false;
        }

        private void EnableTimeoutCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            if (TimeoutTextBox != null)
            {
                TimeoutTextBox.IsEnabled = true;
                if (string.IsNullOrWhiteSpace(TimeoutTextBox.Text)) TimeoutTextBox.Text = "30";
            }
        }

        private void EnableTimeoutCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            if (TimeoutTextBox != null)
                TimeoutTextBox.IsEnabled = false;
        }

        /// <summary>获取检测模式配置（供 SOPModuleView 读取运行时参数）</summary>
        public SOPDetectionModeConfig GetDetectionModeConfig()
        {
            return new SOPDetectionModeConfig
            {
                DetectionMode = "UnifiedDetection",
                EnableHandPoseEstimation = EnableHandPoseCheckBox.IsChecked ?? true,
                MaxNumHands = int.TryParse((MaxHandsComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString(), out var maxHands) ? maxHands : 2,
                UseGpu = UseGpuCheckBox.IsChecked ?? true,
                EnableFaceFilter = HandEnableFaceFilter,
                FaceFilterUpperRatio = HandFaceFilterUpperRatio,
                EnableHandStructureCheck = HandEnableStructureCheck,
                HandStructureWristTipRatio = HandStructureWristTipRatio,
                DetectionConfidenceThreshold = HandDetectionConfidence,
                MinBoxAreaRatio = HandMinBoxAreaRatio,
                RotationAugmentation = HandRotationAugmentation
            };
        }

        /// <summary>设置检测模式配置（供 SOPModuleView 传入当前运行时参数）</summary>
        public void SetDetectionModeConfig(SOPDetectionModeConfig config)
        {
            EnableHandPoseCheckBox.IsChecked = config.EnableHandPoseEstimation;

            foreach (ComboBoxItem item in MaxHandsComboBox.Items)
            {
                if (item.Content?.ToString() == config.MaxNumHands.ToString())
                {
                    MaxHandsComboBox.SelectedItem = item;
                    break;
                }
            }

            UseGpuCheckBox.IsChecked = config.UseGpu;

            HandEnableFaceFilter = config.EnableFaceFilter;
            HandFaceFilterUpperRatio = config.FaceFilterUpperRatio;
            HandEnableStructureCheck = config.EnableHandStructureCheck;
            HandStructureWristTipRatio = config.HandStructureWristTipRatio;
            HandDetectionConfidence = config.DetectionConfidenceThreshold;
            HandMinBoxAreaRatio = config.MinBoxAreaRatio;
            HandRotationAugmentation = config.RotationAugmentation;
        }

        private void AdvancedParamsButton_Click(object sender, RoutedEventArgs e)
        {
            var paramsWindow = new HandDetectionParamsWindow(
                HandInferenceInterval,
                HandSmoothWindowSize,
                HandSmoothAlpha,
                HandSkeletonThreshold,
                HandEnableFaceFilter,
                HandFaceFilterUpperRatio,
                HandEnableStructureCheck,
                HandStructureWristTipRatio,
                HandDetectionConfidence,
                HandMinBoxAreaRatio);

            paramsWindow.Owner = this;

            if (paramsWindow.ShowDialog() == true)
            {
                HandInferenceInterval = paramsWindow.InferenceInterval;
                HandSmoothWindowSize = paramsWindow.SmoothWindowSize;
                HandSmoothAlpha = paramsWindow.SmoothAlpha;
                HandSkeletonThreshold = paramsWindow.SkeletonConfidenceThreshold;
                HandEnableFaceFilter = paramsWindow.EnableFaceFilter;
                HandFaceFilterUpperRatio = paramsWindow.FaceFilterUpperRatio;
                HandEnableStructureCheck = paramsWindow.EnableHandStructureCheck;
                HandStructureWristTipRatio = paramsWindow.HandStructureWristTipRatio;
                HandDetectionConfidence = paramsWindow.DetectionConfidenceThreshold;
                HandMinBoxAreaRatio = paramsWindow.MinBoxAreaRatio;
            }
        }
    }

    /// <summary>
    /// SOP检测模式配置（运行时手部检测参数，不写入 YAML）
    /// </summary>
    public class SOPDetectionModeConfig
    {
        public string DetectionMode { get; set; } = "ObjectBased";
        public bool EnableHandPoseEstimation { get; set; } = false;
        public int MaxNumHands { get; set; } = 1;
        public bool UseGpu { get; set; } = true;
        public bool EnableFaceFilter { get; set; } = true;
        public float FaceFilterUpperRatio { get; set; } = 0.38f;
        public bool EnableHandStructureCheck { get; set; } = true;
        public float HandStructureWristTipRatio { get; set; } = 0.18f;
        public float DetectionConfidenceThreshold { get; set; } = 0.08f;
        public float MinBoxAreaRatio { get; set; } = 0.0005f;
        public bool RotationAugmentation { get; set; } = true;
    }
}
