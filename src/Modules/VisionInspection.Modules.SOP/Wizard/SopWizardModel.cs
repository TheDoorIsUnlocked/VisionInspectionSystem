using System.Collections.Generic;
using System.Text.RegularExpressions;
using VisionInspection.Modules.SOP.Models;
using YamlDotNet.Serialization;

namespace VisionInspection.Modules.SOP.Wizard;

/// <summary>
/// 动作参数种类（决定向导里用哪种输入控件）
/// </summary>
public enum SopParamKind
{
    None,
    /// <summary>区域下拉（来自已标定区域）</summary>
    Region,
    /// <summary>物体类别（COCO 下拉 + 可手填）</summary>
    Object,
    /// <summary>手别：any / left / right</summary>
    Hand,
    /// <summary>停留/等待秒数</summary>
    Duration,
    /// <summary>自由文本（method / action 等自定义参数）</summary>
    Text
}

/// <summary>
/// 单个动作参数的描述（驱动向导 UI 生成输入框）
/// </summary>
public class SopParamSpec
{
    public SopParamSpec(string name, string display, SopParamKind kind, bool required = true)
    {
        Name = name;
        Display = display;
        Kind = kind;
        Required = required;
    }

    /// <summary>对应 SopyamlDetection 的字段名（from_region / to_region / region / target_object / hand / duration_ms）</summary>
    public string Name { get; }

    /// <summary>界面显示名，如"从区域"</summary>
    public string Display { get; }

    public SopParamKind Kind { get; }

    public bool Required { get; }
}

/// <summary>
/// 动作模板：把底层检测方法翻译成大白话，供小白选择
/// </summary>
public class SopActionTemplate
{
    /// <summary>模板唯一键（pickup / putdown / hand_in_region ...）</summary>
    public string Key { get; set; } = "";

    /// <summary>大白话名称，如"拿起某物"</summary>
    public string Label { get; set; } = "";

    /// <summary>底层 method（对应 SOPYamlConfig 的 detection.method）</summary>
    public string Method { get; set; } = "";

    /// <summary>一句话说明，帮助用户理解这个动作在检测什么</summary>
    public string Hint { get; set; } = "";

    /// <summary>是否为"完成"收尾步骤（time_elapsed，duration_ms=0）</summary>
    public bool IsCompletion { get; set; }

    /// <summary>该动作需要的参数</summary>
    public List<SopParamSpec> Params { get; set; } = new();

    /// <summary>默认步骤名（用户可在向导里改）</summary>
    public string DefaultStepName { get; set; } = "";
}

/// <summary>
/// 向导里用户编排的单个动作步骤
/// </summary>
public class WizardAction
{
    public WizardAction() { }

    public WizardAction(SopActionTemplate template)
    {
        Template = template;
        StepName = template.DefaultStepName;
    }

    /// <summary>所选动作模板</summary>
    public SopActionTemplate Template { get; set; } = new();

    /// <summary>步骤显示名（如"拿起水杯"）</summary>
    public string StepName { get; set; } = "";

    /// <summary>参数值：key = SopParamSpec.Name</summary>
    public Dictionary<string, string> Params { get; set; } = new();

    /// <summary>该步超时秒数（0 = 不限制），生成 YAML 时写入 step.timeout</summary>
    public int TimeoutSec { get; set; } = 30;

    /// <summary>
    /// 该步骤使用的相机 ID（如 main_camera / cam_2），生成 YAML 时写入 step.camera。
    /// 缺省主相机；状态机评估该步检测条件时取该相机画面的检测结果。
    /// </summary>
    public string CameraId { get; set; } = "main_camera";

    /// <summary>
    /// 该步骤使用的 YOLO 模型路径；null/空 = 使用全局模型（sop.model.path）。
    /// 生成 YAML 时写入 step.model。支持每个相机/步骤指定不同模型。
    /// </summary>
    public string? ModelPath { get; set; }

    public string GetParam(string name) => Params.TryGetValue(name, out var v) ? v : "";
}

/// <summary>
/// 向导最终输入（生成 YAML 的数据源）
/// </summary>
public class SopWizardInput
{
    /// <summary>SOP 名称</summary>
    public string Name { get; set; } = "";

    /// <summary>SOP 描述</summary>
    public string Description { get; set; } = "";

    /// <summary>是否在第一步前插入"等待人员就位"（person_present）</summary>
    public bool IncludePersonEntry { get; set; } = true;

    /// <summary>是否启用超时检测（settings.enableTimeoutDetection；false 时每步 timeout 不生效）</summary>
    public bool EnableTimeoutDetection { get; set; } = true;

    /// <summary>本流程使用的区域名称预设（英文标识，用于第③步下拉选择）</summary>
    public List<string> RegionPresets { get; set; } = new();

    /// <summary>本流程使用的物体名称预设（英文标识，用于第③步下拉选择）</summary>
    public List<string> ObjectPresets { get; set; } = new();

    /// <summary>选中的区域（预设名 -> 区域定义，含坐标）</summary>
    public Dictionary<string, SopyamlRegion> Regions { get; set; } = new();

    /// <summary>编排好的动作序列（顺序 = 流程顺序）</summary>
    public List<WizardAction> Actions { get; set; } = new();
}

/// <summary>区域/物体名称预设校验（禁止中文、空格及特殊字符，只允许英文标识符）</summary>
public static class SopPresetValidator
{
    /// <summary>匹配仅含字母、数字、下划线、连字符，且不以数字开头的名称</summary>
    private static readonly Regex ValidNameRegex = new("^[a-zA-Z_][a-zA-Z0-9_-]*$", RegexOptions.Compiled);

    /// <summary>匹配任意 CJK 统一表意文字（中文）</summary>
    private static readonly Regex ChineseRegex = new("[\u4e00-\u9fa5]", RegexOptions.Compiled);

    public static bool IsValid(string name, out string reason)
    {
        reason = "";
        if (string.IsNullOrWhiteSpace(name))
        {
            reason = "名称不能为空。";
            return false;
        }
        var trimmed = name.Trim();
        if (ChineseRegex.IsMatch(trimmed))
        {
            reason = "禁止使用中文，请使用英文/数字/下划线/连字符。";
            return false;
        }
        if (!ValidNameRegex.IsMatch(trimmed))
        {
            reason = "只能包含字母、数字、下划线和连字符，且不能以数字开头。";
            return false;
        }
        return true;
    }
}

/// <summary>
/// 常用 COCO 物体类别（仅作参考；向导第①步的物体预设优先）
/// </summary>
public static class SopCocoClasses
{
    public static readonly List<string> Common = new()
    {
        "person", "cup", "bottle", "book", "cell phone", "laptop",
        "keyboard", "mouse", "remote", "scissors", "toothbrush",
        "banana", "apple", "sandwich", "bowl", "fork", "knife", "spoon",
        "chair", "couch", "tv", "tie", "backpack", "umbrella", "handbag",
        "wine glass", "fork", "knife", "spoon", "bowl"
    };
}

/// <summary>
/// 动作模板目录（覆盖引擎全部 11 种 method，按大白话分组）
/// </summary>
public static class SopActionCatalog
{
    public static readonly List<SopActionTemplate> All = new()
    {
        new SopActionTemplate
        {
            Key = "person_present",
            Label = "等待人员就位",
            Method = "person_present",
            Hint = "画面里出现人（person）才进入下一步。一般作为流程第一步。",
            DefaultStepName = "等待人员就位",
            Params = new List<SopParamSpec>()
        },

        new SopActionTemplate
        {
            Key = "pickup",
            Label = "拿起某物",
            Method = "hand_action",
            Hint = "手从指定区域拿起某物体（手离开区域，或手拿着物体离开）。",
            DefaultStepName = "拿起",
            Params = new List<SopParamSpec>
            {
                new SopParamSpec("from_region", "从区域（物体原来放的位置）", SopParamKind.Region, true),
                new SopParamSpec("target_object", "物体（如 cup）", SopParamKind.Object, false),
                new SopParamSpec("hand", "用哪只手", SopParamKind.Hand, false)
            }
        },

        new SopActionTemplate
        {
            Key = "putdown",
            Label = "把某物放到某处",
            Method = "hand_action",
            Hint = "手把物体从某处（通常先经过上一步）放到目标区域。",
            DefaultStepName = "放回",
            Params = new List<SopParamSpec>
            {
                new SopParamSpec("from_region", "从区域（上一步的位置，可留空）", SopParamKind.Region, false),
                new SopParamSpec("to_region", "到区域（要放到的位置）", SopParamKind.Region, true),
                new SopParamSpec("target_object", "物体（如 cup）", SopParamKind.Object, false),
                new SopParamSpec("hand", "用哪只手", SopParamKind.Hand, false)
            }
        },

        new SopActionTemplate
        {
            Key = "hand_in_region",
            Label = "手伸进某区域",
            Method = "hand_in_region",
            Hint = "指定手进入某个区域即判定通过。",
            DefaultStepName = "手进入区域",
            Params = new List<SopParamSpec>
            {
                new SopParamSpec("region", "区域", SopParamKind.Region, true),
                new SopParamSpec("hand", "用哪只手", SopParamKind.Hand, false)
            }
        },

        new SopActionTemplate
        {
            Key = "hand_not_in_region",
            Label = "手离开某区域",
            Method = "hand_not_in_region",
            Hint = "指定手离开某个区域即判定通过。",
            DefaultStepName = "手离开区域",
            Params = new List<SopParamSpec>
            {
                new SopParamSpec("region", "区域", SopParamKind.Region, true),
                new SopParamSpec("hand", "用哪只手", SopParamKind.Hand, false)
            }
        },

        new SopActionTemplate
        {
            Key = "hand_stable",
            Label = "手在某区域稳定停留",
            Method = "hand_stable",
            Hint = "指定手在某个区域内保持稳定（不抖动）若干秒，用于确认\"放置/保持\"。",
            DefaultStepName = "手稳定停留",
            Params = new List<SopParamSpec>
            {
                new SopParamSpec("region", "区域", SopParamKind.Region, true),
                new SopParamSpec("hand", "用哪只手", SopParamKind.Hand, false),
                new SopParamSpec("duration_ms", "稳定停留秒数", SopParamKind.Duration, false)
            }
        },

        new SopActionTemplate
        {
            Key = "hand_move",
            Label = "手从A区移到B区",
            Method = "hand_move",
            Hint = "指定手从区域 A 移动到区域 B。",
            DefaultStepName = "手移动",
            Params = new List<SopParamSpec>
            {
                new SopParamSpec("from_region", "从区域", SopParamKind.Region, true),
                new SopParamSpec("to_region", "到区域", SopParamKind.Region, true),
                new SopParamSpec("hand", "用哪只手", SopParamKind.Hand, false)
            }
        },

        new SopActionTemplate
        {
            Key = "hand_near_object",
            Label = "手靠近某物体",
            Method = "hand_near_object",
            Hint = "指定手靠近某个物体（手-物交互），用于\"操作按钮/拿工具\"等。",
            DefaultStepName = "手靠近物体",
            Params = new List<SopParamSpec>
            {
                new SopParamSpec("target_object", "物体", SopParamKind.Object, true),
                new SopParamSpec("hand", "用哪只手", SopParamKind.Hand, false)
            }
        },

        new SopActionTemplate
        {
            Key = "object_in_zone",
            Label = "某物体出现在某区域",
            Method = "object_in_zone",
            Hint = "指定物体出现在指定区域即判定通过（如杯子到达嘴边）。",
            DefaultStepName = "物体进入区域",
            Params = new List<SopParamSpec>
            {
                new SopParamSpec("target_object", "物体", SopParamKind.Object, true),
                new SopParamSpec("region", "区域", SopParamKind.Region, true)
            }
        },

        new SopActionTemplate
        {
            Key = "object_present",
            Label = "某物体出现在画面",
            Method = "object_present",
            Hint = "指定物体出现在整个画面即判定通过（不限定区域）。",
            DefaultStepName = "物体出现",
            Params = new List<SopParamSpec>
            {
                new SopParamSpec("target_object", "物体", SopParamKind.Object, true)
            }
        },

        new SopActionTemplate
        {
            Key = "custom",
            Label = "自定义动作（高级）",
            Method = "custom",
            Hint = "手动填写底层检测方法名与所需参数，覆盖引擎支持但未预设的场景。方法名必须是引擎认识的（如 hand_in_region / object_present / hand_action ...）。",
            DefaultStepName = "自定义动作",
            Params = new List<SopParamSpec>
            {
                new SopParamSpec("method", "检测方法（method）", SopParamKind.Text, true),
                new SopParamSpec("region", "区域（可选）", SopParamKind.Region, false),
                new SopParamSpec("from_region", "起始区域（可选）", SopParamKind.Region, false),
                new SopParamSpec("to_region", "目标区域（可选）", SopParamKind.Region, false),
                new SopParamSpec("target_object", "物体（可选）", SopParamKind.Object, false),
                new SopParamSpec("hand", "用哪只手（可选）", SopParamKind.Hand, false),
                new SopParamSpec("action", "动作（可选，如 pickup/putdown）", SopParamKind.Text, false),
                new SopParamSpec("tolerance", "容差像素（可选）", SopParamKind.Duration, false),
                new SopParamSpec("duration_ms", "等待毫秒（可选）", SopParamKind.Duration, false)
            }
        },

        new SopActionTemplate
        {
            Key = "complete",
            Label = "完成（流程结束）",
            Method = "time_elapsed",
            Hint = "放在最后一步：所有动作完成后立即判定整个流程通过，并自动循环检测下一轮。",
            DefaultStepName = "完成",
            IsCompletion = true,
            Params = new List<SopParamSpec>()
        }
    };

    public static SopActionTemplate? Find(string key) => All.FirstOrDefault(t => t.Key == key);
}

/// <summary>
/// 场景模板：一键预填常用动作序列，小白改改名字/区域即可
/// </summary>
public class SopScenarioPreset
{
    public string Name { get; set; } = "";
    /// <summary>需要的区域角色提示（帮助用户知道要去标定哪些区域）</summary>
    public string RegionHint { get; set; } = "";
    /// <summary>自动添加的区域预设（英文标识）</summary>
    public List<string> RegionPresets { get; set; } = new();
    /// <summary>自动添加的物体预设（英文标识）</summary>
    public List<string> ObjectPresets { get; set; } = new();
    /// <summary>动作序列：(模板Key, 默认步骤名)</summary>
    public List<(string TemplateKey, string StepName)> Steps { get; set; } = new();
}

/// <summary>
/// 内置场景模板库
/// </summary>
public static class SopScenarios
{
    public static readonly List<SopScenarioPreset> All = new()
    {
        new SopScenarioPreset
        {
            Name = "喝水流程",
            RegionHint = "需要区域：pickup_zone（拿起水杯区）、mouth_zone（嘴边区域）、return_zone（放回水杯区）",
            RegionPresets = new() { "pickup_zone", "mouth_zone", "return_zone" },
            ObjectPresets = new() { "cup" },
            Steps = new()
            {
                ("pickup", "拿起水杯"),
                ("object_in_zone", "用杯子喝水"),
                ("putdown", "放回水杯")
            }
        },
        new SopScenarioPreset
        {
            Name = "零件取放",
            RegionHint = "需要区域：pick_zone（取料区）、place_zone（放料区）",
            RegionPresets = new() { "pick_zone", "place_zone" },
            ObjectPresets = new() { "part" },
            Steps = new()
            {
                ("pickup", "从取料区拿起"),
                ("putdown", "放到放料区")
            }
        },
        new SopScenarioPreset
        {
            Name = "按钮操作",
            RegionHint = "需要区域：button_zone（按钮所在区）",
            RegionPresets = new() { "button_zone" },
            ObjectPresets = new() { "button" },
            Steps = new()
            {
                ("hand_near_object", "手靠近按钮"),
                ("hand_in_region", "手按在按钮区")
            }
        }
    };
}
