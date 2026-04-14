namespace VisionInspection.Core.Interfaces;

/// <summary>
/// 相机管理器接口
/// </summary>
public interface ICameraManager
{
    /// <summary>
    /// 获取所有相机
    /// </summary>
    IReadOnlyList<ICamera> GetAllCameras();

    /// <summary>
    /// 根据ID获取相机
    /// </summary>
    ICamera? GetCamera(string cameraId);

    /// <summary>
    /// 相机连接状态改变事件
    /// </summary>
    event EventHandler<CameraConnectionEventArgs>? CameraConnectionChanged;
}

/// <summary>
/// 相机接口
/// </summary>
public interface ICamera
{
    string Id { get; }
    string Name { get; }
    bool IsConnected { get; }
    CameraInfo Info { get; }

    Task<bool> ConnectAsync();
    Task DisconnectAsync();
    Task<SKBitmap?> CaptureAsync();
}

/// <summary>
/// 相机信息
/// </summary>
public class CameraInfo
{
    public string Model { get; set; } = "";
    public string IpAddress { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public double FrameRate { get; set; }
}

/// <summary>
/// 相机连接事件参数
/// </summary>
public class CameraConnectionEventArgs : EventArgs
{
    public string CameraId { get; set; } = "";
    public bool IsConnected { get; set; }
}
