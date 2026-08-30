using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VisionInspection.Modules.SOP.Models;
using VisionInspection.Modules.SOP.Wizard;

namespace VisionInspection.UI.ViewModels;

/// <summary>
/// SOP 生成向导视图模型：把"傻瓜化"的分步输入转换为 SopWizardInput，并最终生成 YAML。
/// </summary>
public partial class SopWizardViewModel : ObservableObject
{
    #region 步骤导航
    [ObservableProperty] private int _currentStep = 1;
    public int TotalSteps => 4;
    public string StepTitle => _currentStep switch
    {
        1 => "① 基本信息",
        2 => "② 选择区域",
        3 => "③ 编排动作",
        4 => "④ 生成与保存",
        _ => ""
    };
    #endregion

    #region ① 基本信息
    [ObservableProperty] private string _sopName = "";
    [ObservableProperty] private string _sopDescription = "";
    [ObservableProperty] private bool _includePersonEntry = true;
    /// <summary>是否启用超时检测（settings.enableTimeoutDetection；false 时每步超时不生效）</summary>
    [ObservableProperty] private bool _enableTimeoutDetection = true;

    /// <summary>第①步定义的区域名称预设（英文标识）</summary>
    [ObservableProperty] private ObservableCollection<string> _regionPresets = new();
    /// <summary>第①步定义的物体名称预设（英文标识）</summary>
    [ObservableProperty] private ObservableCollection<string> _objectPresets = new();
    [ObservableProperty] private string _newRegionPreset = "";
    [ObservableProperty] private string _newObjectPreset = "";
    #endregion

    #region 场景模板（一键预填动作）
    [ObservableProperty] private string _selectedScenario = "无（自定义）";
    public List<string> ScenarioNames => new List<string> { "无（自定义）" }.Concat(SopScenarios.All.Select(s => s.Name)).ToList();

    [RelayCommand]
    private void ApplyScenario(string? name)
    {
        var key = name ?? SelectedScenario;
        var preset = SopScenarios.All.FirstOrDefault(s => s.Name == key);
        if (preset == null)
        {
            StatusMessage = "已选择自定义，可手动添加动作。";
            return;
        }
        Actions.Clear();
        foreach (var (tplKey, stepName) in preset.Steps)
        {
            var tpl = SopActionCatalog.Find(tplKey);
            if (tpl == null) continue;
            var vm = new WizardActionVM(this, tpl) { StepName = stepName };
            Actions.Add(vm);
        }
        // 自动添加场景需要的区域/物体预设（避免用户手填英文名）
        foreach (var rp in preset.RegionPresets.Where(p => !RegionPresets.Contains(p)))
            RegionPresets.Add(rp);
        foreach (var op in preset.ObjectPresets.Where(p => !ObjectPresets.Contains(p)))
            ObjectPresets.Add(op);
        IncludePersonEntry = true;
        StatusMessage = $"已套用「{preset.Name}」模板：{preset.RegionHint}";
    }
    #endregion

    #region ② 选择区域
    [ObservableProperty] private ObservableCollection<RegionSourceItem> _regionSources = new();
    [ObservableProperty] private RegionSourceItem? _selectedRegionSource;
    [ObservableProperty] private string _regionStatus = "点击下方「刷新区域来源」加载已标定区域的 SOP 文件";
    /// <summary>来源 SOP 文件里的所有区域（供第②步映射下拉）</summary>
    [ObservableProperty] private ObservableCollection<RegionOption> _sourceRegionOptions = new();
    /// <summary>区域预设 -> 来源区域 的映射（key=预设名, value=来源区域id）</summary>
    [ObservableProperty] private ObservableCollection<RegionPresetMapping> _regionPresetMappings = new();
    /// <summary>画面分辨率宽（用于计算默认居中 200×200 区域框）</summary>
    [ObservableProperty] private string _defaultImageWidth = "1920";
    /// <summary>画面分辨率高（用于计算默认居中 200×200 区域框）</summary>
    [ObservableProperty] private string _defaultImageHeight = "1080";
    /// <summary>保存成功事件（供窗口通知主界面刷新配方下拉）</summary>
    public event Action? RecipeSaved;
    #endregion

    #region ③ 编排动作
    [ObservableProperty] private ObservableCollection<WizardActionVM> _actions = new();
    [ObservableProperty] private string _addActionTemplateKey = "";
    #endregion

    #region ④ 生成与保存
    [ObservableProperty] private string _yamlPreview = "";
    /// <summary>开启后 YAML 预览框可手动编辑（高级模式）</summary>
    [ObservableProperty] private bool _isYamlEditable;
    /// <summary>手动编辑后的 YAML 文本（生成时从 YamlPreview 同步）</summary>
    [ObservableProperty] private string _editableYaml = "";
    [ObservableProperty] private string _validationMessage = "填写前 3 步后，点「生成并校验」预览 YAML。";
    [ObservableProperty] private bool _validationOk;
    [ObservableProperty] private string _savedPath = "";
    [ObservableProperty] private string _statusMessage = "";
    #endregion

    #region 供 UI 绑定的下拉数据
    /// <summary>可手动添加的动作模板（排除自动追加的"完成"）</summary>
    public List<SopActionTemplate> AddableTemplates => SopActionCatalog.All.Where(t => !t.IsCompletion).ToList();
    public List<HandOption> HandOptions { get; } = new()
    {
        new HandOption("任意手", "any"),
        new HandOption("左手", "left"),
        new HandOption("右手", "right")
    };
    /// <summary>动作编辑器中"区域"下拉的数据源（来自第①步的区域预设）</summary>
    public ObservableCollection<string> RegionOptions => RegionPresets;
    /// <summary>动作编辑器中"物体"下拉的数据源（来自第①步的物体预设）</summary>
    public ObservableCollection<string> ObjectOptions => ObjectPresets;
    #endregion

    public SopWizardViewModel()
    {
        LoadRegionSources();
        RegionPresets.CollectionChanged += (_, _) => RebuildRegionPresetMappings();
    }

    #region 区域来源命令
    [RelayCommand]
    private void LoadRegionSources()
    {
        RegionSources.Clear();
        foreach (var (path, name) in SopRegionLibrary.ListSopFiles())
            RegionSources.Add(new RegionSourceItem { Path = path, Name = name });

        if (RegionSources.Count == 0)
        {
            RegionStatus = "未找到任何 SOP 文件（configs/sop 下）。请先用「🎯 标定区域」标定一个 SOP 的区域。";
            return;
        }
        SelectedRegionSource = RegionSources[0];
        RegionStatus = $"找到 {RegionSources.Count} 个 SOP 文件，已默认载入第一个的区域。";
    }

    partial void OnSelectedRegionSourceChanged(RegionSourceItem? value)
    {
        if (value == null) { SourceRegionOptions.Clear(); RegionPresetMappings.Clear(); return; }
        LoadRegionsFromSource(value.Path);
    }

    private void LoadRegionsFromSource(string yamlPath)
    {
        SourceRegionOptions.Clear();
        // 首项：不映射 → 生成默认居中 200×200 区域框（非强制映射）
        SourceRegionOptions.Add(new RegionOption { Id = "", Display = "（默认：居中 200×200）" });
        var dict = SopRegionLibrary.LoadRegions(yamlPath);
        if (dict.Count == 0)
        {
            RegionStatus = "该 SOP 文件没有已标定的区域。未映射的区域预设将使用默认居中 200×200 框，之后可用「🎯 标定区域」调整。";
            RebuildRegionPresetMappings();
            return;
        }
        foreach (var (id, region) in dict.OrderBy(k => k.Key))
        {
            var display = string.IsNullOrWhiteSpace(region.Name) ? id : $"{region.Name}（{id}）";
            SourceRegionOptions.Add(new RegionOption { Id = id, Display = display });
        }
        RebuildRegionPresetMappings();
        RegionStatus = $"已载入 {dict.Count} 个区域；可为每个区域预设选择来源区域，不选则使用默认居中 200×200 框。";
    }

    /// <summary>根据当前 RegionPresets 和 SourceRegionOptions 重建映射表（尽量保留已有选择）</summary>
    private void RebuildRegionPresetMappings()
    {
        var existing = RegionPresetMappings.ToDictionary(m => m.PresetName, m => m.SourceRegionId);
        RegionPresetMappings.Clear();
        foreach (var preset in RegionPresets)
        {
            var mapping = new RegionPresetMapping(preset, this);
            if (existing.TryGetValue(preset, out var savedId) && SourceRegionOptions.Any(r => r.Id == savedId))
                mapping.SourceRegionId = savedId;
            else if (SourceRegionOptions.Any(r => r.Id == preset))
                mapping.SourceRegionId = preset; // 自动匹配同名源区域
            RegionPresetMappings.Add(mapping);
        }
    }
    #endregion

    #region ① 名称预设管理命令
    [RelayCommand]
    private void AddRegionPreset()
    {
        if (!SopPresetValidator.IsValid(NewRegionPreset, out var reason))
        {
            StatusMessage = $"⚠️ 区域预设：{reason}";
            return;
        }
        var name = NewRegionPreset.Trim();
        if (RegionPresets.Contains(name))
        {
            StatusMessage = "⚠️ 该区域预设已存在。";
            return;
        }
        RegionPresets.Add(name);
        NewRegionPreset = "";
        StatusMessage = $"已添加区域预设：{name}";
    }

    [RelayCommand]
    private void RemoveRegionPreset(string? name)
    {
        if (name == null) return;
        RegionPresets.Remove(name);
        // 同时清理已引用该预设的动作参数
        foreach (var action in Actions)
        {
            foreach (var p in action.ParamEditors.Where(p => p.Spec.Kind == SopParamKind.Region && p.Value == name))
                p.Value = "";
        }
        StatusMessage = $"已移除区域预设：{name}";
    }

    [RelayCommand]
    private void AddObjectPreset()
    {
        if (!SopPresetValidator.IsValid(NewObjectPreset, out var reason))
        {
            StatusMessage = $"⚠️ 物体预设：{reason}";
            return;
        }
        var name = NewObjectPreset.Trim();
        if (ObjectPresets.Contains(name))
        {
            StatusMessage = "⚠️ 该物体预设已存在。";
            return;
        }
        ObjectPresets.Add(name);
        NewObjectPreset = "";
        StatusMessage = $"已添加物体预设：{name}";
    }

    [RelayCommand]
    private void RemoveObjectPreset(string? name)
    {
        if (name == null) return;
        ObjectPresets.Remove(name);
        foreach (var action in Actions)
        {
            foreach (var p in action.ParamEditors.Where(p => p.Spec.Kind == SopParamKind.Object && p.Value == name))
                p.Value = "";
        }
        StatusMessage = $"已移除物体预设：{name}";
    }
    #endregion

    #region 步骤导航命令
    [RelayCommand]
    private void Next()
    {
        if (!CanLeaveStep(CurrentStep)) return;
        if (CurrentStep < TotalSteps) CurrentStep++;
    }

    [RelayCommand]
    private void Prev()
    {
        if (_currentStep > 1) CurrentStep--;
    }

    private bool CanLeaveStep(int step)
    {
        if (step == 1 && string.IsNullOrWhiteSpace(SopName))
        {
            StatusMessage = "⚠️ 请先填写 SOP 名称。";
            return false;
        }
        if (step == 3 && Actions.Count == 0)
        {
            StatusMessage = "⚠️ 请至少添加一个动作（第③步）。";
            return false;
        }
        return true;
    }
    #endregion

    #region 动作命令
    [RelayCommand]
    private void AddAction(string? templateKey)
    {
        var key = templateKey ?? AddActionTemplateKey;
        var tpl = SopActionCatalog.Find(key);
        if (tpl == null || tpl.IsCompletion) return; // "完成"由生成器自动追加
        var vm = new WizardActionVM(this, tpl);
        Actions.Add(vm);
        StatusMessage = $"已添加动作：{tpl.Label}";
    }

    [RelayCommand]
    private void RemoveAction(WizardActionVM? vm)
    {
        if (vm != null) Actions.Remove(vm);
    }

    [RelayCommand]
    private void MoveActionUp(WizardActionVM? vm)
    {
        if (vm == null) return;
        int i = Actions.IndexOf(vm);
        if (i > 0) { Actions.Move(i, i - 1); }
    }

    [RelayCommand]
    private void MoveActionDown(WizardActionVM? vm)
    {
        if (vm == null) return;
        int i = Actions.IndexOf(vm);
        if (i >= 0 && i < Actions.Count - 1) { Actions.Move(i, i + 1); }
    }
    #endregion

    #region 生成 / 保存
    public SopWizardInput BuildInput()
    {
        var input = new SopWizardInput
        {
            Name = SopName,
            Description = SopDescription,
            IncludePersonEntry = IncludePersonEntry,
            EnableTimeoutDetection = EnableTimeoutDetection,
            RegionPresets = RegionPresets.ToList(),
            ObjectPresets = ObjectPresets.ToList()
        };

        // 第②步：把区域预设映射到来源区域坐标；未映射的预设生成默认居中 200×200 区域框（非强制）
        var sourceDict = SelectedRegionSource == null
            ? new Dictionary<string, SopyamlRegion>()
            : SopRegionLibrary.LoadRegions(SelectedRegionSource.Path);
        foreach (var mapping in RegionPresetMappings)
        {
            if (!string.IsNullOrWhiteSpace(mapping.SourceRegionId)
                && sourceDict.TryGetValue(mapping.SourceRegionId, out var r))
            {
                input.Regions[mapping.PresetName] = new SopyamlRegion
                {
                    X1 = r.X1, Y1 = r.Y1, X2 = r.X2, Y2 = r.Y2,
                    Name = mapping.PresetName, Description = r.Description, Color = r.Color
                };
            }
            else
            {
                input.Regions[mapping.PresetName] = BuildDefaultRegion(mapping.PresetName);
            }
        }
        foreach (var a in Actions)
            input.Actions.Add(a.Model);
        return input;
    }

    /// <summary>生成默认区域框：以画面分辨率中心为基准，200×200 像素居中框</summary>
    private SopyamlRegion BuildDefaultRegion(string presetName)
    {
        if (!int.TryParse(DefaultImageWidth, out var w) || w <= 0) w = 1920;
        if (!int.TryParse(DefaultImageHeight, out var h) || h <= 0) h = 1080;
        const float BoxSize = 200f;
        float x1 = (w - BoxSize) / 2f;
        float y1 = (h - BoxSize) / 2f;
        return new SopyamlRegion
        {
            X1 = x1, Y1 = y1, X2 = x1 + BoxSize, Y2 = y1 + BoxSize,
            Name = presetName,
            Description = "默认区域框（居中 200×200），可用「🎯 标定区域」调整"
        };
    }

    [RelayCommand]
    private void Generate()
    {
        if (string.IsNullOrWhiteSpace(SopName))
        {
            ValidationOk = false;
            ValidationMessage = "⚠️ 请先填写 SOP 名称（第①步）。";
            return;
        }
        if (Actions.Count == 0)
        {
            ValidationOk = false;
            ValidationMessage = "⚠️ 请至少添加一个动作（第③步）。";
            return;
        }

        var input = BuildInput();

        var regionPresetSet = new HashSet<string>(RegionPresets, StringComparer.OrdinalIgnoreCase);
        var objectPresetSet = new HashSet<string>(ObjectPresets, StringComparer.OrdinalIgnoreCase);

        foreach (var a in Actions)
        {
            foreach (var p in a.Template.Params)
            {
                var v = a.Model.GetParam(p.Name);
                if (string.IsNullOrWhiteSpace(v)) continue;

                if (p.Kind == SopParamKind.Region)
                {
                    if (!regionPresetSet.Contains(v))
                    {
                        ValidationOk = false;
                        ValidationMessage = $"⚠️ 动作「{a.StepName}」引用的区域「{v}」不是预设区域，请先在第①步添加。";
                        return;
                    }
                    // 区域映射非强制：未映射的预设会生成默认居中 200×200 框
                }
                else if (p.Kind == SopParamKind.Object)
                {
                    if (!objectPresetSet.Contains(v))
                    {
                        ValidationOk = false;
                        ValidationMessage = $"⚠️ 动作「{a.StepName}」引用的物体「{v}」不是预设物体，请先在第①步添加。";
                        return;
                    }
                }
            }
        }

        var (ok, msg, stepCount) = SopYamlGenerator.Validate(input);
        ValidationOk = ok;
        ValidationMessage = ok
            ? $"✅ {msg}（共 {stepCount} 步：{(input.IncludePersonEntry ? "等待 + " : "")}{Actions.Count} 个动作 + 完成）"
            : $"❌ {msg}";
        YamlPreview = SopYamlGenerator.GenerateYaml(input);
        EditableYaml = YamlPreview;
        if (ok) StatusMessage = "已生成 YAML，可点「保存到文件」。";
    }

    [RelayCommand]
    private void Save()
    {
        if (!ValidationOk)
        {
            StatusMessage = "⚠️ 请先「生成并校验」通过后再保存。";
            return;
        }
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "YAML 文件 (*.yaml)|*.yaml|所有文件 (*.*)|*.*",
            Title = "保存 SOP 流程文件",
            FileName = SanitizeFileName(SopName) + ".yaml",
            InitialDirectory = SopRegionLibrary.ResolveSopDir()
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            if (IsYamlEditable)
            {
                // 高级模式：保存用户手动编辑后的 YAML（必须先通过「重新校验」）
                File.WriteAllText(dlg.FileName, EditableYaml);
            }
            else
            {
                // 向导模式：根据第①-③步重新生成
                var input = BuildInput();
                SopYamlGenerator.SaveToFile(input, dlg.FileName);
            }
            SavedPath = dlg.FileName;
            // 同步到运行时 configs/sop，规避 PreserveNewest 副本不刷新坑，立即生效
            CopyToRuntimeSopDir(dlg.FileName);
            RecipeSaved?.Invoke(); // 通知主界面刷新产品配方下拉（无需重启）
            StatusMessage = $"✅ 已保存到：{dlg.FileName}（并已同步到运行时目录，重启/重载 SOP 模块即可加载）";
        }
        catch (Exception ex)
        {
            StatusMessage = $"❌ 保存失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private void CopyYaml()
    {
        var text = IsYamlEditable ? EditableYaml : YamlPreview;
        if (!string.IsNullOrEmpty(text))
        {
            Clipboard.SetText(text);
            StatusMessage = "已复制 YAML 到剪贴板。";
        }
    }

    [RelayCommand]
    private void RevalidateYaml()
    {
        if (string.IsNullOrWhiteSpace(EditableYaml))
        {
            ValidationOk = false;
            ValidationMessage = "⚠️ YAML 内容为空，无法校验。";
            return;
        }
        try
        {
            var wf = SOPYamlConverter.ParseYaml(EditableYaml);
            ValidationOk = true;
            ValidationMessage = $"✅ 手动编辑的 YAML 可被 SOP 引擎正常解析（共 {wf.Steps.Count} 步）。注意：第①-③步的向导数据可能已与当前 YAML 不一致。";
            StatusMessage = "手动编辑的 YAML 校验通过，可保存。";
        }
        catch (Exception ex)
        {
            ValidationOk = false;
            ValidationMessage = $"❌ YAML 解析失败：{ex.Message}";
            StatusMessage = "请修正 YAML 后再保存。";
        }
    }

    private static void CopyToRuntimeSopDir(string savedPath)
    {
        try
        {
            var runtimeDir = SopRegionLibrary.ResolveSopDir();
            if (string.IsNullOrEmpty(runtimeDir) || !Directory.Exists(runtimeDir)) return;
            var dest = Path.Combine(runtimeDir, Path.GetFileName(savedPath));
            if (string.Equals(Path.GetFullPath(dest), Path.GetFullPath(savedPath), StringComparison.OrdinalIgnoreCase))
                return; // 已经是运行时目录
            File.Copy(savedPath, dest, true);
        }
        catch { /* 同步失败不影响已保存的原文件 */ }
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var s = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(s) ? "sop_workflow" : s;
    }
    #endregion
}

/// <summary>区域来源文件项</summary>
public class RegionSourceItem
{
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public override string ToString() => Name;
}

/// <summary>第②步中区域预设到来源区域的映射</summary>
public partial class RegionPresetMapping : ObservableObject
{
    private readonly SopWizardViewModel _owner;

    public RegionPresetMapping(string presetName, SopWizardViewModel owner)
    {
        PresetName = presetName;
        _owner = owner;
    }

    public string PresetName { get; }
    public ObservableCollection<RegionOption> SourceOptions => _owner.SourceRegionOptions;

    [ObservableProperty] private string _sourceRegionId = "";
}

/// <summary>区域下拉选项</summary>
public class RegionOption
{
    public string Id { get; set; } = "";
    public string Display { get; set; } = "";
    public override string ToString() => Display;
}

/// <summary>手别下拉选项</summary>
public class HandOption
{
    public HandOption(string display, string value)
    {
        Display = display;
        Value = value;
    }
    public string Display { get; }
    public string Value { get; }
}

/// <summary>第③步中单个动作的视图模型</summary>
public partial class WizardActionVM : ObservableObject
{
    private readonly SopWizardViewModel _owner;
    public WizardActionVM(SopWizardViewModel owner, SopActionTemplate template)
    {
        _owner = owner;
        Model = new WizardAction(template);
        _stepName = template.DefaultStepName;
        _timeoutSec = Model.TimeoutSec;
        _enableTimeout = Model.TimeoutSec > 0;
        foreach (var p in template.Params)
        {
            var proxy = new ParamProxy(this, p);
            if (p.Kind == SopParamKind.Hand && string.IsNullOrEmpty(proxy.Value))
                proxy.Value = "any"; // 手别默认"任意手"
            ParamEditors.Add(proxy);
        }
    }

    public WizardAction Model { get; }
    public SopActionTemplate Template => Model.Template;
    public SopWizardViewModel Owner => _owner;
    public ObservableCollection<ParamProxy> ParamEditors { get; } = new();

    [ObservableProperty] private string _stepName;
    partial void OnStepNameChanged(string value) => Model.StepName = value;

    /// <summary>是否启用本步超时（取消勾选 = 0/不限制）</summary>
    [ObservableProperty] private bool _enableTimeout = true;
    /// <summary>本步超时秒数（启用时写入 Model.TimeoutSec）</summary>
    [ObservableProperty] private int _timeoutSec = 30;
    partial void OnEnableTimeoutChanged(bool value) => SyncTimeoutToModel();
    partial void OnTimeoutSecChanged(int value) => SyncTimeoutToModel();

    private void SyncTimeoutToModel() => Model.TimeoutSec = EnableTimeout ? Math.Max(0, TimeoutSec) : 0;
}

/// <summary>动作参数的单个编辑器（桥接模板参数与 WizardAction.Params 字典）</summary>
public partial class ParamProxy : ObservableObject
{
    private readonly WizardActionVM _owner;
    private readonly SopParamSpec _spec;
    public ParamProxy(WizardActionVM owner, SopParamSpec spec)
    {
        _owner = owner;
        _spec = spec;
    }
    public SopParamSpec Spec => _spec;
    public ObservableCollection<string> RegionOptions => _owner.Owner.RegionOptions;
    public ObservableCollection<string> ObjectOptions => _owner.Owner.ObjectOptions;
    public List<HandOption> HandOptions => _owner.Owner.HandOptions;

    [ObservableProperty] private string _value = "";
    partial void OnValueChanged(string value) => _owner.Model.Params[_spec.Name] = value ?? "";
}
