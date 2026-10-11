using System.Runtime.InteropServices;
using static HanYeong.Native;

namespace HanYeong;

readonly record struct KeyEvent(int Vk, bool IsDown);

/// <summary>
/// Low-level keyboard and mouse hooks. Callbacks run on the installing (UI) thread and must
/// return quickly, otherwise Windows drops the hook.
/// </summary>
sealed class GlobalHooks : IDisposable
{
    readonly HookProc _keyboardProc;
    readonly HookProc _mouseProc;
    readonly IntPtr _keyboardHook;
    readonly IntPtr _mouseHook;

    /// <summary>Return true to swallow the key.</summary>
    public Func<KeyEvent, bool>? KeyPressed;
    public Action? MouseClicked;

    public GlobalHooks()
    {
        // Keep the delegates in fields so the GC does not collect them while Windows holds the pointers.
        _keyboardProc = OnKeyboard;
        _mouseProc = OnMouse;
        IntPtr module = GetModuleHandle(null);
        _keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardProc, module, 0);
        _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, module, 0);
        if (_keyboardHook == IntPtr.Zero || _mouseHook == IntPtr.Zero)
            throw new InvalidOperationException($"Hook install failed: {Marshal.GetLastWin32Error()}");
    }

    IntPtr OnKeyboard(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            if (data.dwExtraInfo != InputSender.Signature)
            {
                int msg = (int)wParam;
                bool isDown = msg is WM_KEYDOWN or WM_SYSKEYDOWN;
                try
                {
                    if (KeyPressed?.Invoke(new KeyEvent((int)data.vkCode, isDown)) == true) return 1;
                }
                catch (Exception ex)
                {
                    Log.Write($"key handler failed: {ex}");
                }
            }
        }
        return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    IntPtr OnMouse(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (int)wParam is WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN or WM_XBUTTONDOWN)
        {
            try { MouseClicked?.Invoke(); }
            catch (Exception ex) { Log.Write($"mouse handler failed: {ex}"); }
        }
        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        UnhookWindowsHookEx(_keyboardHook);
        UnhookWindowsHookEx(_mouseHook);
    }
}
