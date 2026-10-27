using ScaleScanLite.Core.Abstractions;
using ScaleScanLite.Core.Logic;

namespace ScaleScanLite.Services.Scale;

/// <summary>Bọc <see cref="IScalePort"/>: gửi lệnh yêu cầu định kỳ khi đang chờ cân và đổi dòng văn bản thành số kg.</summary>
internal sealed class ScaleService : IScaleReader, IDisposable
{
    private readonly IScalePort _port;
    private readonly SettingsService _settings;
    private readonly Timer _pollTimer;
    private volatile bool _polling;

    public ScaleService(IScalePort port, SettingsService settings)
    {
        _port = port;
        _settings = settings;
        _pollTimer = new Timer(_ => Poll(), null, Timeout.Infinite, Timeout.Infinite);

        _port.LineReceived += OnLine;
        _port.ConnectionChanged += (connected, message) => ConnectionChanged?.Invoke(connected, message);
    }

    public event Action<double>? ReadingReceived;

    public event Action<bool, string>? ConnectionChanged;

    public bool IsConnected => _port.IsConnected;

    public void Start() => _port.Start(_settings.Current.Scale);

    /// <summary>Áp dụng lại cổng/baud/chu kỳ sau khi người dùng đổi cấu hình.</summary>
    public void Restart()
    {
        _port.Start(_settings.Current.Scale);
        if (_polling)
            ScheduleRepeating();
    }

    public void StartPolling()
    {
        _polling = true;
        ScheduleRepeating();
    }

    public void StopPolling()
    {
        _polling = false;
        _pollTimer.Change(Timeout.Infinite, Timeout.Infinite);
    }

    private void ScheduleRepeating()
        => _pollTimer.Change(0, _settings.Current.Scale.PollIntervalMs);

    private void Poll()
    {
        if (_polling)
            _port.Send(_settings.Current.Scale.RequestCommand);
    }

    private void OnLine(string line)
    {
        if (ScaleLineParser.TryParse(line, out var kg))
            ReadingReceived?.Invoke(kg);
    }

    public void Dispose()
    {
        _pollTimer.Dispose();
        _port.Dispose();
    }
}
