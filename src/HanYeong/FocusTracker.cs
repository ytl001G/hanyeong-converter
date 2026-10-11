using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace HanYeong;

/// <summary>
/// Follows the foreground app and the focused control via WinEvents: which executable is active,
/// the last real app before the tray was clicked, and whether focus is in a password field.
/// </summary>
sealed class FocusTracker : IDisposable
{
    const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    const uint EVENT_OBJECT_FOCUS = 0x8005;
    const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    const int GWL_STYLE = -16;
    const int ES_PASSWORD = 0x0020;

    delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    readonly WinEventProc _proc;
    readonly IntPtr _foregroundHook;
    readonly IntPtr _focusHook;
    readonly Dictionary<uint, string> _exeByPid = new();
    readonly uint _ownPid = (uint)Environment.ProcessId;
    int _focusVersion;
    volatile bool _isPassword;

    public FocusTracker()
    {
        _proc = OnWinEvent;
        _foregroundHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _proc, 0, 0, WINEVENT_OUTOFCONTEXT);
        _focusHook = SetWinEventHook(EVENT_OBJECT_FOCUS, EVENT_OBJECT_FOCUS, IntPtr.Zero, _proc, 0, 0, WINEVENT_OUTOFCONTEXT);
        Update(Native.GetForegroundWindow());
    }

    /// <summary>Executable of the foreground window, e.g. "notepad.exe".</summary>
    public string CurrentExe { get; private set; } = "";

    /// <summary>Last foreground app that is not the taskbar/tray or this program.</summary>
    public string LastAppExe { get; private set; } = "";

    public bool IsPasswordFocused => _isPassword;

    /// <summary>This program's own window (the settings dialog) is in front.</summary>
    public bool IsOwnAppForeground { get; private set; }

    void OnWinEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (evt == EVENT_SYSTEM_FOREGROUND) Update(hwnd);
        CheckPassword();
    }

    void Update(IntPtr hwnd)
    {
        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        CurrentExe = ExeFor(pid);
        IsOwnAppForeground = pid == _ownPid;
        if (pid != _ownPid && !IsShellSurface(hwnd) && CurrentExe.Length > 0) LastAppExe = CurrentExe;
    }

    void CheckPassword()
    {
        // Classic Win32 edit boxes say so in their style; everything else needs UI Automation,
        // which is a cross-process call, so it runs off the UI thread and the newest result wins.
        IntPtr focus = Ime.FocusedWindow();
        if ((GetWindowLong(focus, GWL_STYLE) & ES_PASSWORD) != 0 && ClassName(focus).Equals("Edit", StringComparison.OrdinalIgnoreCase))
        {
            _isPassword = true;
            return;
        }
        int version = Interlocked.Increment(ref _focusVersion);
        Task.Run(() =>
        {
            bool isPassword;
            try
            {
                isPassword = AutomationElement.FocusedElement?.Current.IsPassword == true;
            }
            catch (Exception)
            {
                isPassword = false;
            }
            if (version == Volatile.Read(ref _focusVersion)) _isPassword = isPassword;
        });
    }

    string ExeFor(uint pid)
    {
        if (_exeByPid.TryGetValue(pid, out var name)) return name;
        name = "";
        IntPtr process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process != IntPtr.Zero)
        {
            var buffer = new char[1024];
            int size = buffer.Length;
            if (QueryFullProcessImageName(process, 0, buffer, ref size)) name = Path.GetFileName(new string(buffer, 0, size));
            CloseHandle(process);
        }
        if (_exeByPid.Count > 500) _exeByPid.Clear();
        _exeByPid[pid] = name;
        return name;
    }

    static bool IsShellSurface(IntPtr hwnd) =>
        ClassName(hwnd) is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "NotifyIconOverflowWindow" or "TopLevelWindowForOverflowXamlIsland";

    static string ClassName(IntPtr hwnd)
    {
        var buffer = new char[256];
        int length = GetClassName(hwnd, buffer, buffer.Length);
        return new string(buffer, 0, Math.Max(length, 0));
    }

    public void Dispose()
    {
        UnhookWinEvent(_foregroundHook);
        UnhookWinEvent(_focusHook);
    }

    [DllImport("user32.dll")]
    static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmod, WinEventProc proc, uint pid, uint thread, uint flags);

    [DllImport("user32.dll")]
    static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)]
    static extern int GetClassName(IntPtr hwnd, char[] buffer, int max);

    [DllImport("kernel32.dll")]
    static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode)]
    static extern bool QueryFullProcessImageName(IntPtr process, int flags, char[] buffer, ref int size);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr handle);
}
