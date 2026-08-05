using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;
using SkiaSharp;
using VisionInspection.Core.Models;
using VisionInspection.Core.Services;
using VisionInspection.Core.ViewModels;
using VisionInspection.Modules.SOP.Models;

namespace VisionInspection.UI.Views;

/// <summary>
/// SOP 区域标定窗口：复用通用 ROIEditorControl，把鼠标画的矩形区域导出为
/// SOP YAML 的 regions 段（供 hand_in_region / hand_move / object_in_zone 等步骤条件引用）。
/// </summary>
public partial class SOPRegionEditorWindow : Window
{
    private readonly ROIManager _roiManager = new();
    private readonly ROIEditorViewModel _editorVm;
    private string? _yamlPath;

    /// <summary>编辑器视图模型（供 XAML 绑定区域列表）</summary>
    public ROIEditorViewModel EditorVm => _editorVm;

    /// <summary>保存成功后最终写入的 YAML 路径（供调用方重载工作流）</summary>
    public string? SavedYamlPath { get; private set; }

    public SOPRegionEditorWindow(SKBitmap? frame, string? yamlPath)
    {
        InitializeComponent();
        DataContext = this;

        _editorVm = new ROIEditorViewModel(_roiManager);
        RoiEditor.ViewModel = _editorVm;
        _roiManager.ROIChanged += OnRoiChanged;

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

        if (frame != null)
            _editorVm.CurrentImage = frame;

        // 窗口布局完成后再完整适配窗口，便于标定全图区域
        Loaded += (_, _) => RoiEditor.FitToWindow();
    }

    /// <summary>每画完一个区域自动退出“新建”模式，避免连续误画</summary>
    private void OnRoiChanged(object? sender, ROIChangedEventArgs e)
    {
        if (e.ChangeType == ROIChangeType.Added)
            _editorVm.CancelCreatingROI();
    }

    /// <summary>从 YAML 读取现有 regions 并转为矩形 ROI 显示</summary>
    private void LoadRegionsFromYaml(string path)
    {
        try
        {
            var wf = SOPYamlConverter.LoadFromYaml(path);
            foreach (var z in wf.Regions)
            {
                _roiManager.AddROI(new RectangleROI
                {
                    // 必须用 ZoneId（region key，如 phone_table）而非 Name（显示名，如"手机放置区"），
                    // 否则保存后 region key 变成中文，步骤条件里的 from_region: "phone_table" 找不到匹配。
                    ROIName = z.ZoneId,
                    Rect = new SKRectI(
                        (int)Math.Round(z.X),
                        (int)Math.Round(z.Y),
                        (int)Math.Round(z.X + z.Width),
                        (int)Math.Round(z.Y + z.Height))
                });
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"加载 YAML 区域失败: {ex.Message}", "警告",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void NewRegion_Click(object sender, RoutedEventArgs e) => _editorVm.StartCreatingROI();
    private void DeleteSelected_Click(object sender, RoutedEventArgs e) => _editorVm.DeleteSelectedROI();

    private void ClearAll_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("确定清空所有区域？", "确认", MessageBoxButton.YesNo, MessageBoxImage.Question)
            == MessageBoxResult.Yes)
            _editorVm.ClearAllROIs();
    }

    private void DeleteRegion_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is ROI roi)
            _roiManager.RemoveROI(roi.ROIId);
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
        _roiManager.Clear();
        LoadRegionsFromYaml(_yamlPath);
        RoiEditor.InvalidateVisual();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_yamlPath))
        {
            MessageBox.Show("请先点「打开YAML」选择要保存的文件。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // 矩形 ROI -> SopyamlRegion 字典（区域名称即 region key，供条件引用）
        var dict = new Dictionary<string, SopyamlRegion>(StringComparer.OrdinalIgnoreCase);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int autoIdx = 0;
        foreach (var roi in _roiManager.ROIs)
        {
            if (roi is not RectangleROI r) continue;
            var rawId = string.IsNullOrWhiteSpace(r.ROIName) ? $"region_{++autoIdx}" : r.ROIName.Trim();
            var id = rawId;
            int n = 2;
            while (used.Contains(id)) id = $"{rawId}_{n++}";
            used.Add(id);

            dict[id] = new SopyamlRegion
            {
                X1 = r.Rect.Left,
                Y1 = r.Rect.Top,
                X2 = r.Rect.Right,
                Y2 = r.Rect.Bottom,
                Name = r.ROIName
            };
        }

        try
        {
            SOPYamlConverter.SaveRegions(_yamlPath, dict);
            SavedYamlPath = _yamlPath;
            MessageBox.Show($"已保存 {dict.Count} 个区域到:\n{_yamlPath}\n\n重新运行/重载工作流后生效。",
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
