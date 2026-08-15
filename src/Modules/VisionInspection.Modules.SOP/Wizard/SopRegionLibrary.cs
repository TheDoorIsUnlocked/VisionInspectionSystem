using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VisionInspection.Modules.SOP.Models;
using YamlDotNet.Serialization;

namespace VisionInspection.Modules.SOP.Wizard;

/// <summary>
/// 区域数据源：列出可选的区域来源 SOP 文件，并读取其中已标定的区域定义。
/// 向导"复用已标定区域"即从此处下拉选择，零坐标输入。
/// </summary>
public static class SopRegionLibrary
{
    /// <summary>
    /// 从运行时基目录向上遍历，定位 configs/sop 目录（与模型搜索同理，兼容调试/发布不同深度）
    /// </summary>
    public static string? ResolveSopDir()
    {
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var cur = baseDir;
        for (int i = 0; i < 8; i++)
        {
            var cand = Path.Combine(cur, "configs", "sop");
            if (Directory.Exists(cand)) return cand;
            var parent = Path.GetDirectoryName(cur);
            if (parent == null) break;
            cur = parent;
        }
        return Path.Combine(baseDir, "configs", "sop");
    }

    /// <summary>
    /// 列出 configs/sop 下所有 SOP 文件（作为"区域来源"候选）
    /// </summary>
    public static List<(string Path, string Name)> ListSopFiles()
    {
        var dir = ResolveSopDir();
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            return new List<(string, string)>();

        return Directory.EnumerateFiles(dir, "*.yaml")
            .Concat(Directory.EnumerateFiles(dir, "*.yml"))
            .Select(p => (Path: p, Name: ReadSopName(p) ?? Path.GetFileName(p)))
            .ToList();
    }

    /// <summary>
    /// 读取某个 SOP 文件里已标定的区域（id -> 区域定义，含坐标）
    /// </summary>
    public static Dictionary<string, SopyamlRegion> LoadRegions(string yamlPath)
    {
        var dict = new Dictionary<string, SopyamlRegion>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(yamlPath)) return dict;

        try
        {
            var wf = SOPYamlConverter.LoadFromYaml(yamlPath);
            foreach (var z in wf.Regions)
            {
                dict[z.ZoneId] = new SopyamlRegion
                {
                    X1 = z.X,
                    Y1 = z.Y,
                    X2 = z.X + z.Width,
                    Y2 = z.Y + z.Height,
                    Name = z.Name
                };
            }
        }
        catch
        {
            // 读取失败返回空，由调用方提示
        }
        return dict;
    }

    private static string? ReadSopName(string path)
    {
        try
        {
            var yaml = File.ReadAllText(path);
            var cfg = new DeserializerBuilder()
                .IgnoreUnmatchedProperties()
                .Build()
                .Deserialize<SOPYamlConfig>(yaml);
            return cfg?.Sop?.Name;
        }
        catch
        {
            return null;
        }
    }
}
