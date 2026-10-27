using System.Text;
using static ScaleScanLite.Services.Input.RawInputNative;

namespace ScaleScanLite.Services.Input;

/// <summary>Đổi mã phím (virtual key) mà máy quét gửi thành ký tự, theo bàn phím của cửa sổ đang được chọn (đúng thứ app kia nhận).</summary>
internal static class KeyTranslator
{
    public static char? Translate(ushort virtualKey, ushort scanCode, bool shift)
    {
        if (virtualKey == VK_RETURN)
            return '\r';
        if (virtualKey == VK_TAB)
            return '\t';

        var state = new byte[256];
        if (shift)
            state[VK_SHIFT] = 0x80;
        if ((GetKeyState(VK_CAPITAL) & 1) != 0)
            state[VK_CAPITAL] = 0x01;

        var buffer = new StringBuilder(8);
        // Cờ 0x4: không làm thay đổi trạng thái phím chết của hệ thống.
        int result = ToUnicodeEx(virtualKey, scanCode, state, buffer, buffer.Capacity, 0x4, ForegroundLayout());
        return result == 1 ? buffer[0] : null;
    }

    private static IntPtr ForegroundLayout()
    {
        var window = GetForegroundWindow();
        uint threadId = window == IntPtr.Zero ? 0 : GetWindowThreadProcessId(window, out _);
        return GetKeyboardLayout(threadId);
    }
}
