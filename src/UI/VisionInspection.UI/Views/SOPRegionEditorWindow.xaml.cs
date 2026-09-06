using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.Win32;
using SkiaSharp;
using VisionInspection.Core.Models;
using VisionInspection.Core.Services;
using VisionInspection.Core.ViewModels;
using VisionInspection.Modules.SOP.Models;
using VisionInspection.UI.Controls;
using VisionInspection.UI.Services;

namespace VisionInspection.UI.Views;

/// <summary>
/// SOP 区域标定窗口（多相机）：每个相机一个页签，复用通用 ROIEditorControl。
/// 从 YAML 加载区域时按区域所属相机（region.camera）自动分配到对应相机画面，
/// 可分别拖拽/改坐标；保存时写回各区域的 camera 字段（主相机缺省不写）。
/// </summary>
public partial class SOPRegionEditorWindow : Window
{
    /// <summary>单相机编辑器封装：帧 + ROI 管理器 + 视图模型 + 画面控件 + 页签</summary>
    private class CameraEditor
    {
        public string CameraId { get; init; } = "main_camera";
        public string DisplayName { get; set; } = "主相机";
        public SKBitmap? Frame { get; set; }
        public ROIManager RoiManager { get; } = new();
        public ROIEditorViewModel Vm { get; set; } = null!;
        public ROIEditorControl? EditorControl { get; set; }
        public TabItem? Tab { get; set; }
    }

    private readonly List<CameraEditor> _editors = new();
    private CameraEditor? _current;
    private string? _yamlPath;

    /// <summary>保存成功后最终写入的 YAML 路径（供调用方重载工作流）</summary>
    public string? SavedYamlPath { get; private set; }

    public SOPRegionEditorWindow(Dictionary<string, SKBitmap> frames, string? yamlPath)
    {
        InitializeComponent();
        DataContext = this;

        // 1) 为每路相机创建页签（主相机优先，其余按相机ID排序）
        var frameList = frames ?? new Dictionary<string, SKBitmap>();
        var ordered = frameList
            .OrderBy(kv => kv.Key == CameraManager.PrimaryCameraId ? 0 : 1)
            .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (ordered.Count == 0)
        {
            // 无任何帧：仍提供主相机占位画布，保证可打开标定
            ordered.Add(new KeyValuePair<string, SKBitmap>(CameraManager.PrimaryCameraId,
                CameraFrameConverter.CreatePlaceholderFrame()));
        }
        foreach (var (camId, frame) in ordered)
        {
            AddCameraTab(camId, frame);
        }

        // 2) 从 YAML 按区域所属相机加载
        _yamlPath = yamlPath;
        if (!string.IsNullOrEmpty(yamlPath) && File.Exists(yamlPath))
        {
            LoadRegionsFromYaml(yamlPath);
            YamlPathText.Text = yamlPath;
        }
        else
        {
            YamlPathText.Text = "(未选择 YAML，请点「打开YAML」指定文件)";
        }

        // 3) 默认选中第一个页签（主相机）
        if (CameraTabs.Items.Count > 0)
        {
            CameraTabs.SelectedIndex = 0;
            _current = _editors.FirstOrDefault();
        }
    }

    /// <summary>创建一路相机的页签（画面 + 区域列表）</summary>
    private void AddCameraTab(string cameraId, SKBitmap frame)
    {
        var editor = new CameraEditor
        {
            CameraId = cameraId,
            DisplayName = GetCameraDisplayName(cameraId),
            Frame = frame
        };
        editor.Vm = new ROIEditorViewModel(editor.RoiManager);
        editor.RoiManager.ROIChanged += OnRoiChanged;

        // 画面控件
        var roiEditor = new ROIEditorControl
        {
            ViewModel = editor.Vm,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        editor.EditorControl = roiEditor;
        if (frame != null)
        {
            editor.Vm.CurrentImage = frame;
        }
        // 页签布局完成后完整适配窗口，便于标定全图区域
        roiEditor.Loaded += (_, _) => roiEditor.FitToWindow();

        // 右侧区域列表（绑定该相机的 VM；必须用 Binding 而非直接赋值 ItemsSource，
        // 因为 ROIManager.ROIs 是 IReadOnlyList（非 ObservableCollection），
        // 列表刷新依赖 VM 的 PropertyChanged(ROIs) 通知，代码赋值只取一次快照导致新增不显示）
        var listBox = new ListBox
        {
            BorderThickness = new Thickness(0),
            ItemTemplate = (DataTemplate)FindResource("RegionListItemTemplate")
        };
        listBox.SetBinding(ItemsControl.ItemsSourceProperty,
            new Binding(nameof(ROIEditorViewModel.ROIs)) { Source = editor.Vm });
        var selBinding = new Binding(nameof(ROIEditorViewModel.SelectedROI))
        {
            Source = editor.Vm,
            Mode = BindingMode.TwoWay
        };
        listBox.SetBinding(ListBox.SelectedItemProperty, selBinding);

        var header = new TextBlock
        {
            Text = "区域列表（名称 = 区域ID）",
            FontWeight = FontWeights.Bold,
            Padding = new Thickness(10, 8, 10, 8),
            Background = new SolidColorBrush(Color.FromRgb(245, 245, 245))
        };
        var rightGrid = new Grid();
        rightGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        rightGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        rightGrid.Children.Add(header);
        Grid.SetRow(header, 0);
        rightGrid.Children.Add(listBox);
        Grid.SetRow(listBox, 1);

        var border = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(221, 221, 221)),
            BorderThickness = new Thickness(1, 0, 0, 0),
            Child = rightGrid
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
        grid.Children.Add(roiEditor);
        grid.Children.Add(border);
        Grid.SetColumn(border, 1);

        var tab = new TabItem { Header = editor.DisplayName, Content = grid };
        editor.Tab = tab;
        CameraTabs.Items.Add(tab);
        _editors.Add(editor);
    }

    /// <summary>按相机ID取编辑器；不存在则创建占位画布（YAML 已有该相机区域但当前无帧时）</summary>
    private CameraEditor GetOrCreateEditor(string cameraId)
    {
        var existing = _editors.FirstOrDefault(e => string.Equals(e.CameraId, cameraId, StringComparison.OrdinalIgnoreCase));
        if (existing != null) return existing;

        AddCameraTab(cameraId, CameraFrameConverter.CreatePlaceholderFrame());
        return _editors[^1];
    }

    private static string GetCameraDisplayName(string cameraId)
    {
        if (cameraId == CameraManager.PrimaryCameraId)
            return "📷 主相机";
        var slot = CameraManager.Instance.Slots.FirstOrDefault(s =>
            string.Equals(s.CameraId, cameraId, StringComparison.OrdinalIgnoreCase));
        return slot != null && !string.IsNullOrEmpty(slot.DisplayName)
            ? $"📷 {slot.DisplayName}"
            : $"📷 {cameraId}";
    }

    /// <summary>每画完一个区域自动退出"新建"模式，避免连续误画</summary>
    private void OnRoiChanged(object? sender, ROIChangedEventArgs e)
    {
        if (e.ChangeType == ROIChangeType.Added && _current != null)
            _current.Vm.CancelCreatingROI();
    }

    /// <summary>
    /// 从 YAML 读取现有 regions，按区域所属相机（region.camera）自动分配到对应相机画面。
    /// </summary>
    private void LoadRegionsFromYaml(string path)
    {
        try
        {
            // 用完整 YAML 模型（保留"区域是否显式写 camera"的原始信息：
            // SopyamlRegion.Camera 为 null = 未写；领域模型会把缺省值填充为 "main_camera"，无法区分）
            var config = SOPYamlConverter.LoadFullConfig(path);
            if (config?.Sop?.Regions == null) return;

            // 推断"区域 → 归属相机"：区域未显式写 camera 时，
            // 按引用该区域的步骤（step.camera）或跨相机规则（condition.camera）归属相机。
            var regionCameraMap = BuildRegionCameraMap(config);

            foreach (var (zoneId, region) in config.Sop.Regions)
            {
                string cam;
                if (!string.IsNullOrWhiteSpace(region.Camera))
                {
                    cam = region.Camera.Trim();                      // ① 区域显式 camera 优先
                }
                else if (regionCameraMap.TryGetValue(zoneId, out var inferred))
                {
                    cam = inferred;                                  // ② 从步骤/跨相机规则推断
                }
                else
                {
                    cam = CameraManager.PrimaryCameraId;             // ③ 缺省主相机
                }

                var editor = GetOrCreateEditor(cam);
                editor.RoiManager.AddROI(new RectangleROI
                {
                    // 必须用 region key（zoneId，如 box_region）而非 Name（显示名），
                    // 否则保存后 region key 变成中文，步骤条件里的 region: "box_region" 找不到匹配。
                    ROIName = zoneId,
                    Rect = new SKRectI(
                        (int)Math.Round(region.X1),
                        (int)Math.Round(region.Y1),
                        (int)Math.Round(region.X2),
                        (int)Math.Round(region.Y2))
                });
            }
            StatusText.Text = $"已加载 {config.Sop.Regions.Count} 个区域，按所属相机分配到 {_editors.Count} 路画面";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"加载 YAML 区域失败: {ex.Message}", "警告",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// 构建"区域ID → 归属相机"映射（基于原始 YAML 模型）：
    /// - 遍历所有步骤：detection 引用的区域（region / from_region / to_region）归属该步骤的 camera
    /// - 遍历跨相机规则：条件引用的区域归属该条件的 camera
    /// 仅供区域未显式声明 camera 时的归属推断。
    /// </summary>
    private static Dictionary<string, string> BuildRegionCameraMap(SOPYamlConfig config)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (config?.Sop == null) return map;

        foreach (var step in config.Sop.Steps)
        {
            var cam = string.IsNullOrWhiteSpace(step.Camera) ? CameraManager.PrimaryCameraId : step.Camera.Trim();
            var d = step.Detection;
            if (d == null) continue;
            TryAdd(map, d.Region, cam);
            TryAdd(map, d.FromRegion, cam);
            TryAdd(map, d.ToRegion, cam);
        }

        if (config.Sop.CrossCamera != null)
        {
            foreach (var rule in config.Sop.CrossCamera.Rules)
            {
                foreach (var c in rule.Conditions)
                {
                    var cam = string.IsNullOrWhiteSpace(c.Camera) ? CameraManager.PrimaryCameraId : c.Camera;
                    TryAdd(map, c.Region, cam);
                }
            }
        }

        return map;
    }

    private static void TryAdd(Dictionary<string, string> map, string? regionId, string cameraId)
    {
        if (!string.IsNullOrWhiteSpace(regionId) && !map.ContainsKey(regionId))
            map[regionId] = cameraId;
    }

    // ==================== 工具栏 ====================

    private void NewRegion_Click(object sender, RoutedEventArgs e) => _current?.Vm.StartCreatingROI();

    private void DeleteSelected_Click(object sender, RoutedEventArgs e) => _current?.Vm.DeleteSelectedROI();

    private void ClearAll_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null) return;
        if (MessageBox.Show($"确定清空「{_current.DisplayName}」的所有区域？", "确认",
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            _current.Vm.ClearAllROIs();
    }

    private void DeleteRegion_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is ROI roi && _current != null)
            _current.RoiManager.RemoveROI(roi.ROIId);
    }

    private void OpenYaml_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "YAML 文件 (*.yaml;*.yml)|*.yaml;*.yml|所有文件 (*.*)|*.*",
            Title = "选择 SOP YAML 文件"
        };
        if (dlg.ShowDialog() != true) return;

        _yamlPath = dlg.FileName;
        YamlPathText.Text = _yamlPath;

        // 清空全部相机页的区域后重新按所属相机加载
        foreach (var ed in _editors)
        {
            ed.RoiManager.Clear();
        }
        LoadRegionsFromYaml(_yamlPath);
        foreach (var ed in _editors)
        {
            ed.EditorControl?.InvalidateVisual();
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_yamlPath))
        {
            MessageBox.Show("请先点「打开YAML」选择要保存的文件。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // 矩形 ROI -> SopyamlRegion 字典（区域名称即 region key，供条件引用；带 camera 字段）
        var dict = new Dictionary<string, SopyamlRegion>(StringComparer.OrdinalIgnoreCase);
        foreach (var editor in _editors)
        {
            int autoIdx = 0;
            foreach (var roi in editor.RoiManager.ROIs)
            {
                if (roi is not RectangleROI r) continue;
                var rawId = string.IsNullOrWhiteSpace(r.ROIName) ? $"region_{++autoIdx}" : r.ROIName.Trim();
                var id = rawId;
                int n = 2;
                while (dict.ContainsKey(id)) id = $"{rawId}_{n++}";

                dict[id] = new SopyamlRegion
                {
                    X1 = r.Rect.Left,
                    Y1 = r.Rect.Top,
                    X2 = r.Rect.Right,
                    Y2 = r.Rect.Bottom,
                    Name = r.ROIName,
                    // ⭐ 区域所属相机：主相机不写（缺省语义），其余写入 camera
                    Camera = editor.CameraId == CameraManager.PrimaryCameraId ? null : editor.CameraId
                };
            }
        }

        try
        {
            SOPYamlConverter.SaveRegions(_yamlPath, dict);
            SavedYamlPath = _yamlPath;
            MessageBox.Show($"已保存 {dict.Count} 个区域（{_editors.Count} 路相机）到:\n{_yamlPath}\n\n重新运行/重载工作流后生效。",
                "保存成功", MessageBoxButton.OK, MessageBoxImage.Information);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"保存失败: {ex.Message}", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    /// <summary>切换相机页签：更新当前编辑器并适配画面</summary>
    private void CameraTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var idx = CameraTabs.SelectedIndex;
        _current = idx >= 0 && idx < _editors.Count ? _editors[idx] : null;
        if (_current?.EditorControl != null)
        {
            _current.EditorControl.FitToWindow();
            StatusText.Text = $"当前相机：{_current.DisplayName}（{_current.RoiManager.ROIs.Count} 个区域）";
        }
    }

    /// <summary>右侧 X/Y/W/H 参数框按 Enter 时立即提交绑定并移焦到画面，便于查看效果</summary>
    private void RoiParamTextBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter && sender is TextBox tb)
        {
            UpdateTextBindingSource(tb);
            _current?.EditorControl?.Focus();
            e.Handled = true;
        }
    }

    /// <summary>右侧 X/Y/W/H 参数框失去焦点时显式写回绑定（兼容某些默认 TwoWay 不生效的场景）</summary>
    private void RoiParamTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb)
        {
            UpdateTextBindingSource(tb);
        }
    }

    private static void UpdateTextBindingSource(TextBox tb)
    {
        var expr = tb.GetBindingExpression(TextBox.TextProperty);
        if (expr != null)
        {
            try
            {
                expr.UpdateSource();
            }
            catch (FormatException)
            {
                // 输入非数字，恢复源值（红框会提示）
                expr.UpdateTarget();
            }
        }
    }
}

/// <summary>把 ROI 转为坐标摘要文本（DataTemplate 用）</summary>
public class RoiRectConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is RectangleROI r)
            return $"X{r.Rect.Left} Y{r.Rect.Top}  {r.Rect.Width}×{r.Rect.Height}";
        return "";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => null;
}
