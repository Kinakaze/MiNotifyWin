using System.ComponentModel;
using System.Runtime.InteropServices;

namespace MiPushDesk.Services;

public sealed class TrayIcon : IDisposable
{
    private const uint CallbackMessage = 0x8000 + 73;
    private static readonly uint TaskbarCreated = RegisterWindowMessage("TaskbarCreated");
    private static readonly uint ShowRequested = RegisterWindowMessage("MiPushDesk.Show");
    private readonly nint _window;
    private readonly nint _icon;
    private readonly SubclassProc _callback;
    private readonly Action<string> _action;
    private readonly Func<(bool Paused, bool Startup)> _settings;
    private bool _visible;
    private string _tip = "MiPush Desk";
    private string? _balloonKey;
    public TrayIcon(nint window, Action<string> action, Func<(bool Paused, bool Startup)> settings)
    {
        _window = window; _action = action; _settings = settings;
        _icon = LoadImage(0, Path.Combine(AppContext.BaseDirectory, "Assets", "MiPushDesk.ico"), 1, 32, 32, 0x10);
        if (_icon == 0) throw new Win32Exception("无法读取托盘图标。");
        _callback = WindowMessage;
        if (!SetWindowSubclass(window, _callback, 73, 0)) throw new Win32Exception("无法创建托盘回调。");
    }
    public void Show()
    {
        if (_visible) return;
        var data = Data();
        if (!ShellNotifyIcon(0, ref data)) throw new Win32Exception("无法显示托盘图标。");
        _visible = true;
    }
    public void Update(string text)
    {
        _tip = text[..Math.Min(text.Length, 120)];
        if (!_visible) return;
        var data = Data();
        ShellNotifyIcon(1, ref data);
    }
    public void Balloon(string title, string text, bool sound, string? key = null)
    {
        _balloonKey = key;
        var data = Data();
        data.Flags |= 0x10;
        data.InfoTitle = title[..Math.Min(title.Length, 63)];
        data.Info = text[..Math.Min(text.Length, 255)];
        data.InfoFlags = 1u | (sound ? 0u : 0x10u);
        ShellNotifyIcon(1, ref data);
    }
    public void ClearBalloon(string? key = null)
    {
        if (key is not null && key != _balloonKey) return;
        _balloonKey = null;
        var data = Data();
        data.Flags |= 0x10;
        ShellNotifyIcon(1, ref data);
    }
    private nint WindowMessage(nint window, uint message, nuint word, nint parameter, nuint subclass, nuint reference)
    {
        if (message == ShowRequested) { _action("show"); return 0; }
        if (message == TaskbarCreated && _visible) { var data = Data(); ShellNotifyIcon(0, ref data); }
        if (message == CallbackMessage)
        {
            var notification = (uint)parameter & 0xffff;
            if (notification == 0x0405 && _balloonKey is { } key) _action("message:" + key);
            else if (notification is 0x0202 or 0x0203 or 0x0400 or 0x0405) _action("show");
            if (notification is 0x0205 or 0x007b) ShowMenu();
            return 0;
        }
        return DefSubclassProc(window, message, word, parameter);
    }
    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        uint selected;
        try
        {
            var state = _settings();
            AppendMenu(menu, 0, 1, "打开 MiPush Desk");
            AppendMenu(menu, 0, 2, state.Paused ? "恢复通知" : "暂停通知");
            AppendMenu(menu, state.Startup ? 8u : 0u, 3, "开机自启动");
            AppendMenu(menu, 0x800, 0, null);
            AppendMenu(menu, 0, 4, "退出并停止接收");
            SetForegroundWindow(_window);
            GetCursorPos(out var point);
            selected = TrackPopupMenuEx(menu, 0x102, point.X, point.Y, _window, 0);
        }
        finally { DestroyMenu(menu); PostMessage(_window, 0, 0, 0); }
        if (selected is >= 1 and <= 4) _action(new[] { "show", "pause", "startup", "exit" }[selected - 1]);
    }
    private NotifyIconData Data() => new()
    {
        Size = (uint)Marshal.SizeOf<NotifyIconData>(), Window = _window, Id = 1, Flags = 7,
        Callback = CallbackMessage, Icon = _icon, Tip = _tip, Info = "", InfoTitle = ""
    };
    public void Dispose()
    {
        if (_visible) { var data = Data(); ShellNotifyIcon(2, ref data); }
        _visible = false;
        RemoveWindowSubclass(_window, _callback, 73);
        DestroyIcon(_icon);
        GC.SuppressFinalize(this);
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size; public nint Window; public uint Id; public uint Flags; public uint Callback; public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State; public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags; public Guid Guid; public nint BalloonIcon;
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X; public int Y; }
    private delegate nint SubclassProc(nint window, uint message, nuint word, nint parameter, nuint subclass, nuint reference);
    [DllImport("comctl32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowSubclass(nint window, SubclassProc callback, nuint id, nuint reference);
    [DllImport("comctl32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RemoveWindowSubclass(nint window, SubclassProc callback, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint window, uint message, nuint word, nint parameter);
    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);
    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll", EntryPoint = "LoadImageW", CharSet = CharSet.Unicode)] private static extern nint LoadImage(nint instance, string name, int type, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll")] private static extern nint CreatePopupMenu();
    [DllImport("user32.dll", EntryPoint = "AppendMenuW", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(nint menu, uint flags, nuint id, string? label);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(nint menu);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenuEx(nint menu, uint flags, int horizontal, int vertical, nint window, nint parameters);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint window, uint message, nuint word, nint parameter);
}
