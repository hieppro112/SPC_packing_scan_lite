using System.Text;

namespace ScaleScanLite.Core.Logic;

/// <summary>
/// Ghép các ký tự do máy quét "gõ" thành một mã. Máy quét gõ rất nhanh và kết thúc bằng Enter (hoặc Tab);
/// khoảng cách giữa hai ký tự quá lớn nghĩa là không phải một lượt quét nên bộ đệm bị xóa.
/// </summary>
public sealed class ScanBurstAssembler
{
    private const int MaxLength = 512;

    private readonly StringBuilder _buffer = new();
    private readonly int _maxGapMs;
    private readonly int _minLength;
    private long _lastTick;

    public ScanBurstAssembler(int maxGapMs, int minLength)
    {
        _maxGapMs = Math.Max(1, maxGapMs);
        _minLength = Math.Max(1, minLength);
    }

    /// <summary>Thêm một ký tự; trả về mã hoàn chỉnh khi gặp ký tự kết thúc và mã đủ dài, ngược lại trả về null.</summary>
    public string? Add(char ch, long nowMs)
    {
        if (_buffer.Length > 0 && nowMs - _lastTick > _maxGapMs)
            _buffer.Clear();

        _lastTick = nowMs;

        if (ch is '\r' or '\n' or '\t')
        {
            var code = _buffer.ToString().Trim();
            _buffer.Clear();
            return code.Length >= _minLength ? code : null;
        }

        if (char.IsControl(ch))
            return null;

        if (_buffer.Length < MaxLength)
            _buffer.Append(ch);

        return null;
    }

    public void Reset() => _buffer.Clear();
}
