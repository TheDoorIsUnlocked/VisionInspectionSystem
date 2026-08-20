using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VisionInspection.Modules.SOP.Models;
using YamlDotNet.Serialization;

namespace VisionInspection.Modules.SOP.Wizard;

/// <summary>
/// SOP YAML 生成器：把向导输入（傻瓜化参数）转换为引擎可直接读取的 SOP YAML。
/// 复用 SOPYamlConfig 模型与引擎同一套序列化器，保证格式零误差。
/// </summary>
public static class SopYamlGenerator
{
    /// <summary>
    /// 由向导输入构建 SOPYamlConfig（内存模型，尚未序列化）
    /// </summary>
    public static SOPYamlConfig BuildConfig(SopWizardInput input)
    {
        var steps = new List<SopyamlStep>();
        int order = 0;

        // 可选：第一步"等待人员就位"
        if (input.IncludePersonEntry)
        {
            steps.Add(MakeStep(ref order, "等待人员就位", "等待人员开始操作",
                new SopyamlDetection
                {
                    Method = "person_present",
                    TargetObject = "person",
                    MinConfidence = 0.6f,
                    StableFrames = 3,
                    Description = "检测到人员在场（COCO person 类别）"
                }));
        }

        // 用户编排的动作
        foreach (var action in input.Actions)
        {
            var det = BuildDetection(action);
            steps.Add(MakeStep(ref order, action.StepName, TemplateHint(action), det));
        }

        // 最后一步"完成"（time_elapsed, duration_ms=0），状态机到此判定全部通过并循环
        steps.Add(MakeStep(ref order, "完成", "所有步骤完成后立即判定通过",
            new SopyamlDetection
            {
                Method = "time_elapsed",
                DurationMs = 0,
                Description = "所有步骤完成后立即判定通过（duration_ms=0 表示到达此步即触发 SOP 全部通过，自动循环下一轮）"
            }));

        // 串联 transitions（仅作可读性保留；状态机实际按 StepId 顺序线性推进）
        for (int i = 0; i < steps.Count; i++)
        {
            steps[i].Transitions = i < steps.Count - 1
                ? new List<string> { steps[i + 1].Id }
                : new List<string> { steps[0].Id }; // 完成 -> 回到第一步，循环
        }

        var sop = new SopyamlSop
        {
            Name = input.Name,
            Version = "1.0",
            Description = input.Description,
            Steps = steps,
            Regions = input.Regions,
            Settings = new SopyamlSettings
            {
                DetectionMode = "pose_based",
                ConfidenceThreshold = 0.6f,
                StableFrames = 3,
                EnableTimeoutDetection = false, // 不限制每步时间，做完才推进
                EnableSkipDetection = true,
                AutoResetOnComplete = true,
                ResetDelaySec = 1
            },
            Visualization = new SopyamlVisualization
            {
                ShowRegions = true,
                ShowKeypoints = true,
                ShowSkeleton = true,
                ShowTrackingId = true,
                RegionAlpha = 0.3f,
                KeypointRadius = 5
            }
        };

        return new SOPYamlConfig { Sop = sop };
    }

    /// <summary>
    /// 生成 YAML 字符串
    /// </summary>
    public static string GenerateYaml(SopWizardInput input)
    {
        var config = BuildConfig(input);
        // 与引擎完全一致：不使用全局命名约定，完全由 [YamlMember(Alias)] 控制字段名；
        // OmitNull 去掉未赋值的空字段，使生成文件更接近手写风格、避免大量空行。
        var serializer = new SerializerBuilder()
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
            .Build();
        return serializer.Serialize(config);
    }

    /// <summary>
    /// 生成并写入文件
    /// </summary>
    public static void SaveToFile(SopWizardInput input, string filePath)
    {
        var yaml = GenerateYaml(input);
        File.WriteAllText(filePath, yaml);
    }

    /// <summary>
    /// 端到端校验：用引擎的反序列化器重新读取生成的 YAML，确保能被 SOP 引擎正常解析。
    /// 返回 (成功, 提示信息, 解析出的步骤数)
    /// </summary>
    public static (bool ok, string message, int stepCount) Validate(SopWizardInput input)
    {
        try
        {
            var yaml = GenerateYaml(input);
            var wf = SOPYamlConverter.ParseYaml(yaml);
            int expected = input.Actions.Count + (input.IncludePersonEntry ? 1 : 0) + 1;
            if (wf.Steps.Count != expected)
            {
                return (false, $"步骤数不匹配：生成 {expected} 步，引擎解析出 {wf.Steps.Count} 步", wf.Steps.Count);
            }
            return (true, "校验通过：生成的 YAML 可被 SOP 引擎正常解析。", wf.Steps.Count);
        }
        catch (Exception ex)
        {
            return (false, $"校验失败：{ex.Message}", 0);
        }
    }

    // ===== 内部辅助 =====

    private static SopyamlStep MakeStep(ref int order, string name, string desc, SopyamlDetection detection)
    {
        order++;
        return new SopyamlStep
        {
            Id = order.ToString(),
            Name = name,
            Description = desc,
            Timeout = 30,
            Detection = detection
        };
    }

    private static string TemplateHint(WizardAction action)
    {
        var t = action.Template;
        var parts = new List<string>();
        foreach (var p in t.Params)
        {
            var v = action.GetParam(p.Name);
            if (!string.IsNullOrWhiteSpace(v))
                parts.Add($"{p.Display}={v}");
        }
        var hint = string.IsNullOrEmpty(t.Hint) ? t.Method : t.Hint;
        return parts.Count == 0 ? hint : $"{hint}（{string.Join("，", parts)}）";
    }

    private static SopyamlDetection BuildDetection(WizardAction action)
    {
        var t = action.Template;
        var det = new SopyamlDetection
        {
            MinConfidence = 0.6f,
            StableFrames = 3
        };

        string Hand() => string.IsNullOrWhiteSpace(action.GetParam("hand")) ? "any" : action.GetParam("hand");
        string Obj() => string.IsNullOrWhiteSpace(action.GetParam("target_object")) ? "" : action.GetParam("target_object");

        switch (t.Key)
        {
            case "person_present":
                det.Method = "person_present";
                det.TargetObject = "person";
                break;

            case "pickup":
                det.Method = "hand_action";
                det.Action = "pickup";
                det.FromRegion = action.GetParam("from_region");
                det.TargetObject = Obj();
                det.Hand = Hand();
                break;

            case "putdown":
                det.Method = "hand_action";
                det.Action = "putdown";
                det.FromRegion = action.GetParam("from_region");
                det.ToRegion = action.GetParam("to_region");
                det.TargetObject = Obj();
                det.Hand = Hand();
                break;

            case "hand_in_region":
                det.Method = "hand_in_region";
                det.Region = action.GetParam("region");
                det.Hand = Hand();
                break;

            case "hand_not_in_region":
                det.Method = "hand_not_in_region";
                det.Region = action.GetParam("region");
                det.Hand = Hand();
                break;

            case "hand_stable":
            {
                det.Method = "hand_stable";
                det.Region = action.GetParam("region");
                det.Hand = Hand();
                det.Tolerance = 20f;
                var durStr = action.GetParam("duration_ms");
                if (double.TryParse(durStr, out var sec) && sec > 0)
                    det.StableFrames = Math.Clamp((int)(sec * 3), 3, 30);
                break;
            }

            case "hand_move":
                det.Method = "hand_move";
                det.FromRegion = action.GetParam("from_region");
                det.ToRegion = action.GetParam("to_region");
                det.Hand = Hand();
                break;

            case "hand_near_object":
                det.Method = "hand_near_object";
                det.TargetObject = Obj();
                det.Hand = Hand();
                det.Tolerance = 30f;
                break;

            case "object_in_zone":
                det.Method = "object_in_zone";
                det.TargetObject = Obj();
                det.Region = action.GetParam("region");
                break;

            case "object_present":
                det.Method = "object_present";
                det.TargetObject = Obj();
                break;

            case "complete":
                det.Method = "time_elapsed";
                det.DurationMs = 0;
                break;

            default:
                det.Method = t.Method;
                break;
        }

        // 空字符串统一置 null，避免 YAML 出现空字段
        if (string.IsNullOrWhiteSpace(det.FromRegion)) det.FromRegion = null;
        if (string.IsNullOrWhiteSpace(det.ToRegion)) det.ToRegion = null;
        if (string.IsNullOrWhiteSpace(det.Region)) det.Region = null;
        if (string.IsNullOrWhiteSpace(det.TargetObject)) det.TargetObject = null;
        if (string.IsNullOrWhiteSpace(det.Hand)) det.Hand = "any";

        return det;
    }
}
