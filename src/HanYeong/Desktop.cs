using HanYeong.Core;

namespace HanYeong;

/// <summary>A key event to inject: a virtual key, or a Unicode character when <see cref="Vk"/> is 0.</summary>
readonly record struct Keystroke(ushort Vk, char Char, bool IsUp);

/// <summary>
/// What the controller reads from Windows and sends to it. The app uses <see cref="WindowsDesktop"/>;
/// the headless tests put a fake desktop with a simulated text box and IME here.
/// </summary>
interface IDesktop
{
    IntPtr ForegroundWindow { get; }
    bool IsDown(int vk);
    bool IsCapsLockOn { get; }
    /// <summary>The IME mode of the focused window, or null when it does not answer.</summary>
    Layout? ImeLayout { get; }
    string CurrentExe { get; }
    bool IsPasswordFocused { get; }
    bool IsOwnAppForeground { get; }
    /// <returns>false when Windows blocked the input (an elevated window).</returns>
    bool Send(IReadOnlyList<Keystroke> strokes);
    Task<string?> ReadSelectionAsync();
}

sealed class WindowsDesktop(FocusTracker focus) : IDesktop
{
    const int VK_CAPITAL = 0x14;

    public IntPtr ForegroundWindow => Native.GetForegroundWindow();
    public bool IsDown(int vk) => Native.IsDown(vk);
    public bool IsCapsLockOn => (Native.GetKeyState(VK_CAPITAL) & 1) != 0;
    public Layout? ImeLayout => Ime.GetLayout();
    public string CurrentExe => focus.CurrentExe;
    public bool IsPasswordFocused => focus.IsPasswordFocused;
    public bool IsOwnAppForeground => focus.IsOwnAppForeground;
    public bool Send(IReadOnlyList<Keystroke> strokes) => InputSender.SendNow(strokes);
    public Task<string?> ReadSelectionAsync() => SelectionReader.ReadAsync();
}
