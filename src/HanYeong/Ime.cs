using HanYeong.Core;
using static HanYeong.Native;

namespace HanYeong;

/// <summary>Reads the Korean IME mode of the focused window (works across processes via the default IME window).</summary>
static class Ime
{
    public static IntPtr FocusedWindow()
    {
        IntPtr fg = GetForegroundWindow();
        uint thread = GetWindowThreadProcessId(fg, out _);
        var info = new GUITHREADINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<GUITHREADINFO>() };
        return GetGUIThreadInfo(thread, ref info) && info.hwndFocus != IntPtr.Zero ? info.hwndFocus : fg;
    }

    /// <summary>Current mode, or null when the window does not answer.</summary>
    public static Layout? GetLayout()
    {
        IntPtr imeWnd = ImmGetDefaultIMEWnd(FocusedWindow());
        if (imeWnd == IntPtr.Zero) return null;
        IntPtr ok = SendMessageTimeout(imeWnd, WM_IME_CONTROL, IMC_GETCONVERSIONMODE, IntPtr.Zero, SMTO_ABORTIFHUNG, 100, out IntPtr mode);
        if (ok == IntPtr.Zero) return null;
        return ((long)mode & IME_CMODE_HANGUL) != 0 ? Layout.Korean : Layout.English;
    }
}
