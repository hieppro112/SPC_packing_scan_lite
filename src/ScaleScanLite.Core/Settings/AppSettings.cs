namespace ScaleScanLite.Core.Settings;

public sealed class AppSettings
{
    public DatabaseSettings Database { get; set; } = new();
    public ScaleSettings Scale { get; set; } = new();
    public WeighingSettings Weighing { get; set; } = new();
    public UiSettings Ui { get; set; } = new();
    public ScannerSettings Scanner { get; set; } = new();
    public string ErrorLogFolder { get; set; } = "";
    public bool DemoMode { get; set; }
}

public sealed class DatabaseSettings
{
    public string Server { get; set; } = "192.168.122.2";
    public string PoDatabase { get; set; } = "MANUFASPCPD";
    public string PoUser { get; set; } = "";
    /// <summary>Mật khẩu đã mã hóa DPAPI (Base64). Không lưu mật khẩu dạng thường.</summary>
    public string PoPasswordProtected { get; set; } = "";
    public string HistoryDatabase { get; set; } = "F2Database";
    public string HistoryUser { get; set; } = "";
    public string HistoryPasswordProtected { get; set; } = "";
    public int TimeoutSeconds { get; set; } = 15;
}

public sealed class ScaleSettings
{
    public string PortName { get; set; } = "COM8";
    public int BaudRate { get; set; } = 9600;
    public string RequestCommand { get; set; } = "SP";
    public int PollIntervalMs { get; set; } = 500;
}

public sealed class WeighingSettings
{
    /// <summary>Sai lệch tuyệt đối cho phép (kg).</summary>
    public double ToleranceKg { get; set; } = 0.05;
    /// <summary>Sai lệch tương đối cho phép (% khối lượng chuẩn). Dùng giá trị lớn hơn của hai ngưỡng.</summary>
    public double TolerancePercent { get; set; } = 2.0;
    public int StableCount { get; set; } = 5;
    public double StableSpreadKg { get; set; } = 0.005;
    public double MinWeightKg { get; set; } = 0.01;
    public int WaitTimeoutSeconds { get; set; } = 60;
    public int MinPlainCodeLength { get; set; } = 11;
}

/// <summary>Máy quét barcode USB (chế độ bàn phím). Scale Pro chỉ "nghe" thêm, không chặn phím gửi tới app khác.</summary>
public sealed class ScannerSettings
{
    public bool Enabled { get; set; }

    /// <summary>Mã nhận dạng máy quét dạng VID_xxxx&amp;PID_yyyy (lấy bằng nút "Nhận diện máy quét").</summary>
    public string DeviceId { get; set; } = "";

    /// <summary>Khoảng cách tối đa giữa hai ký tự của cùng một lượt quét (ms). Người gõ tay chậm hơn nhiều.</summary>
    public int MaxCharGapMs { get; set; } = 100;

    public int MinLength { get; set; } = 4;
}

public sealed class UiSettings
{
    public int PageSize { get; set; } = 10;
    /// <summary>false: tìm theo đầu mã PO (nhanh, dùng được index); true: tìm chứa chuỗi.</summary>
    public bool SearchContains { get; set; }
}
