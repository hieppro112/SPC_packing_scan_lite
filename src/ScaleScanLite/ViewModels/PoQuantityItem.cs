using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using ScaleScanLite.Core.Logic;
using ScaleScanLite.Core.Models;

namespace ScaleScanLite.ViewModels;

/// <summary>
/// Một PO đang chờ cân, hiển thị thành thẻ riêng: mã PO, số lượng ban đầu (master), ô số lượng thực tế và khối lượng chuẩn của PO đó.
/// Gõ số mới thì áp dụng ngay; gõ sai (rỗng, chữ, ≤ 0) thì báo đỏ và giữ số hợp lệ gần nhất.
/// </summary>
internal sealed partial class PoQuantityItem : ObservableObject
{
    private readonly Func<string, double, bool> _apply;
    private readonly PoInfo _po;
    private string _lastValidText;

    public PoQuantityItem(PoInfo po, double masterQuantity, bool showLabel, Func<string, double, bool> apply)
    {
        _po = po;
        Aufnr = po.Aufnr;
        Description = string.Join(" · ", new[] { po.Phcd, po.Phtx, po.Pstx }.Where(x => !string.IsNullOrWhiteSpace(x)));
        MasterQuantity = masterQuantity;
        ShowLabel = showLabel;
        _apply = apply;
        _lastValidText = Format(po.Quantity);
        _input = _lastValidText;   // gán thẳng trường để không kích hoạt áp dụng
        _isEdited = Math.Abs(po.Quantity - masterQuantity) > 1e-9;
        _expectedText = ExpectedFor(po.Quantity);
    }

    public string Aufnr { get; }

    /// <summary>PHCD · PHTX · PSTX của PO (bỏ phần trống).</summary>
    public string Description { get; }

    public double MasterQuantity { get; }

    /// <summary>Số lượng ban đầu theo master, luôn hiển thị để so với số lượng đã chỉnh.</summary>
    public string MasterText => Format(MasterQuantity);

    /// <summary>Gói nhiều PO thì hiện thẻ riêng cho từng PO.</summary>
    public bool ShowLabel { get; }

    [ObservableProperty] private string _input;
    [ObservableProperty] private bool _isInvalid;
    [ObservableProperty] private bool _isEdited;

    /// <summary>Khối lượng chuẩn của riêng PO này theo số lượng đang nhập.</summary>
    [ObservableProperty] private string _expectedText;

    partial void OnInputChanged(string value)
    {
        if (TryParse(value, out var quantity) && _apply(Aufnr, quantity))
        {
            _lastValidText = value.Trim();
            IsInvalid = false;
            IsEdited = Math.Abs(quantity - MasterQuantity) > 1e-9;
            ExpectedText = ExpectedFor(quantity);
        }
        else
        {
            IsInvalid = true;
        }
    }

    /// <summary>Rời ô mà còn sai thì trả về số hợp lệ gần nhất để không hiển thị khác với số sẽ được lưu.</summary>
    public void RevertIfInvalid()
    {
        if (IsInvalid)
            Input = _lastValidText;
    }

    private string ExpectedFor(double quantity)
        => WeightCalculator.ExpectedKg(_po.NetWeight, _po.PackQty, quantity) is double e ? $"{e:0.###} kg" : "Chưa có dữ liệu";

    private static bool TryParse(string text, out double value)
        => double.TryParse((text ?? "").Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static string Format(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
