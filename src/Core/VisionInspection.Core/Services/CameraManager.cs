using VisionInspection.Core.Interfaces;

namespace VisionInspection.Core.Services;

public class CameraManager : ICameraManager
{
    private readonly List<ICamera> _cameras = new();

    public IReadOnlyList<ICamera> GetAllCameras()
    {
        return _cameras.AsReadOnly();
    }

    public ICamera? GetCamera(string cameraId)
    {
        return _cameras.FirstOrDefault(c => c.Id == cameraId);
    }

    public event EventHandler<CameraConnectionEventArgs>? CameraConnectionChanged;

    /// <summary>
    /// 添加相机
    /// </summary>
    public void AddCamera(ICamera camera)
    {
        _cameras.Add(camera);
    }

    /// <summary>
    /// 移除相机
    /// </summary>
    public void RemoveCamera(string cameraId)
    {
        var camera = GetCamera(cameraId);
        if (camera != null)
        {
            _cameras.Remove(camera);
        }
    }

    /// <summary>
    /// 连接所有相机
    /// </summary>
    public async Task ConnectAllAsync()
    {
        foreach (var camera in _cameras)
        {
            await camera.ConnectAsync();
        }
    }
}
