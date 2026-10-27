using ScaleScanLite.Core.Settings;

namespace ScaleScanLite.Services.Scale;

/// <summary>Kênh giao tiếp với cân (cổng COM thật hoặc cân giả lập).</summary>
internal interface IScalePort : IDisposable
{
    event Action<string>? LineReceived;

    event Action<bool, string>? ConnectionChanged;

    bool IsConnected { get; }

    /// <summary>Bắt đầu vòng lặp nền: tự kết nối, tự nối lại khi mất cổng.</summary>
    void Start(ScaleSettings settings);

    void Stop();

    bool Send(string command);
}
