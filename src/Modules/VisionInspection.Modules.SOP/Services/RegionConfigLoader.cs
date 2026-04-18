using System.Text.Json;
using SkiaSharp;
using VisionInspection.Modules.SOP.Models;

namespace VisionInspection.Modules.SOP.Services;

/// <summary>
/// 区域配置加载器
/// </summary>
public class RegionConfigLoader
{
    private RegionConfig? _config;
    private readonly Dictionary<string, SKRect> _regionCache = new();

    /// <summary>
    /// 从JSON文件加载区域配置
    /// </summary>
    public async Task<RegionConfig> LoadFromJsonAsync(string jsonPath)
    {
        if (!File.Exists(jsonPath))
        {
            throw new FileNotFoundException($"区域配置文件不存在: {jsonPath}");
        }

        var json = await File.ReadAllTextAsync(jsonPath);
        _config = JsonSerializer.Deserialize<RegionConfig>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        if (_config == null)
        {
            throw new InvalidOperationException("无法解析区域配置文件");
        }

        // 缓存区域坐标
        CacheRegions();

        return _config;
    }

    /// <summary>
    /// 从JSON文件加载（同步版本）
    /// </summary>
    public RegionConfig LoadFromJson(string jsonPath)
    {
        return LoadFromJsonAsync(jsonPath).GetAwaiter().GetResult();
    }

    /// <summary>
    /// 获取指定区域的SKRect
    /// </summary>
    public SKRect? GetRegion(string regionId)
    {
        if (_regionCache.TryGetValue(regionId, out var rect))
        {
            return rect;
        }
        return null;
    }

    /// <summary>
    /// 获取所有区域
    /// </summary>
    public Dictionary<string, SKRect> GetAllRegions()
    {
        return new Dictionary<string, SKRect>(_regionCache);
    }

    /// <summary>
    /// 获取区域定义
    /// </summary>
    public RegionDefinition? GetRegionDefinition(string regionId)
    {
        if (_config?.Regions.TryGetValue(regionId, out var definition) == true)
        {
            return definition;
        }
        return null;
    }

    /// <summary>
    /// 获取关键点定义
    /// </summary>
    public KeypointDefinition? GetKeypointDefinition(string keypointId)
    {
        if (_config?.Keypoints.TryGetValue(keypointId, out var definition) == true)
        {
            return definition;
        }
        return null;
    }

    /// <summary>
    /// 获取动作规则
    /// </summary>
    public ActionRule? GetActionRule(string ruleId)
    {
        if (_config?.Rules.TryGetValue(ruleId, out var rule) == true)
        {
            return rule;
        }
        return null;
    }

    /// <summary>
    /// 检查点是否在指定区域内
    /// </summary>
    public bool IsPointInRegion(float x, float y, string regionId)
    {
        var region = GetRegion(regionId);
        if (!region.HasValue)
        {
            return false;
        }

        var rect = region.Value;
        return x >= rect.Left && x <= rect.Right && y >= rect.Top && y <= rect.Bottom;
    }

    /// <summary>
    /// 获取配置中的所有区域ID
    /// </summary>
    public List<string> GetRegionIds()
    {
        return _regionCache.Keys.ToList();
    }

    /// <summary>
    /// 获取图像尺寸
    /// </summary>
    public (int width, int height) GetImageSize()
    {
        if (_config?.ImageSize != null)
        {
            return (_config.ImageSize.Width, _config.ImageSize.Height);
        }
        return (1280, 720); // 默认值
    }

    /// <summary>
    /// 根据实际图像尺寸缩放区域坐标
    /// </summary>
    public Dictionary<string, SKRect> ScaleRegionsToSize(int targetWidth, int targetHeight)
    {
        var (originalWidth, originalHeight) = GetImageSize();
        var scaleX = (float)targetWidth / originalWidth;
        var scaleY = (float)targetHeight / originalHeight;

        var scaledRegions = new Dictionary<string, SKRect>();
        foreach (var (id, rect) in _regionCache)
        {
            scaledRegions[id] = new SKRect(
                rect.Left * scaleX,
                rect.Top * scaleY,
                rect.Right * scaleX,
                rect.Bottom * scaleY
            );
        }

        return scaledRegions;
    }

    /// <summary>
    /// 缓存所有区域坐标
    /// </summary>
    private void CacheRegions()
    {
        _regionCache.Clear();
        if (_config?.Regions == null) return;

        foreach (var (id, definition) in _config.Regions)
        {
            _regionCache[id] = definition.Coordinates.ToSKRect();
        }
    }
}

/// <summary>
/// 区域配置管理器（单例）
/// </summary>
public class RegionConfigManager
{
    private static RegionConfigManager? _instance;
    private readonly RegionConfigLoader _loader = new();

    public static RegionConfigManager Instance => _instance ??= new RegionConfigManager();

    private RegionConfigManager() { }

    /// <summary>
    /// 当前加载的配置
    /// </summary>
    public RegionConfig? CurrentConfig { get; private set; }

    /// <summary>
    /// 加载配置文件
    /// </summary>
    public async Task LoadConfigAsync(string jsonPath)
    {
        CurrentConfig = await _loader.LoadFromJsonAsync(jsonPath);
    }

    /// <summary>
    /// 获取区域
    /// </summary>
    public SKRect? GetRegion(string regionId) => _loader.GetRegion(regionId);

    /// <summary>
    /// 获取所有区域
    /// </summary>
    public Dictionary<string, SKRect> GetAllRegions() => _loader.GetAllRegions();

    /// <summary>
    /// 检查点是否在区域内
    /// </summary>
    public bool IsPointInRegion(float x, float y, string regionId) =>
        _loader.IsPointInRegion(x, y, regionId);
}
