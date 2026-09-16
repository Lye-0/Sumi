using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Sumi.Core;

namespace Sumi;

internal static class Native
{
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool RegisterHotKey(nint h, int id, uint mods, uint key);
    [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(nint h, int id);
    [DllImport("user32.dll")] internal static extern bool GetCursorPos(out PointI p);
    [DllImport("user32.dll")] private static extern uint GetMessagePos();
    internal static PointI MouseMessagePosition()
    { var packed = GetMessagePos(); return new PointI { X = unchecked((short)(packed & 0xffff)), Y = unchecked((short)(packed >> 16)) }; }
    [DllImport("user32.dll")] internal static extern bool SetWindowPos(nint h, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] internal static extern int GetWindowLong(nint h, int index);
    [DllImport("user32.dll")] internal static extern int SetWindowLong(nint h, int index, int value);
    [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(nint h);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint h, int attribute, ref int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(nint h, ref Margins margins);
    [StructLayout(LayoutKind.Sequential)] internal struct PointI { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Margins { public int L, R, T, B; }

    internal static void Glass(Window window, bool enabled, Theme theme)
    {
        var h = new WindowInteropHelper(window).Handle;
        if (h == 0) return;
        int dark = theme == Theme.Dark ? 1 : 0, corners = 2, backdrop = enabled ? 3 : 1;
        DwmSetWindowAttribute(h, 20, ref dark, 4);
        DwmSetWindowAttribute(h, 33, ref corners, 4);
        DwmSetWindowAttribute(h, 38, ref backdrop, 4);
        var margins = new Margins { L = -1, R = -1, T = -1, B = -1 };
        DwmExtendFrameIntoClientArea(h, ref margins);
        if (HwndSource.FromHwnd(h)?.CompositionTarget is { } target) target.BackgroundColor = Colors.Transparent;
    }
}

internal static class Appearance
{
    public static void Apply(Theme theme, bool glass)
    {
        bool dark = theme == Theme.Dark;
        Set("CanvasBrush", dark ? (glass ? "#EB192129" : "#FF192129") : (glass ? "#F0F1F6F8" : "#FFF1F6F8"));
        Set("SurfaceBrush", dark ? "#902B3742" : "#C8FFFFFF");
        Set("InputBrush", dark ? "#9010161E" : "#C4FFFFFF");
        Set("LineBrush", dark ? "#48596F7D" : "#40778991");
        Set("TextBrush", dark ? "#F0F5F7" : "#202F38");
        Set("MutedBrush", dark ? "#ABBDCA" : "#596D79");
        Set("AccentBrush", dark ? "#B2E3D6" : "#235F53");
        Set("AccentTextBrush", dark ? "#102E28" : "#FFFFFF");
        foreach (Window w in System.Windows.Application.Current.Windows) Native.Glass(w, glass, theme);
    }
    private static void Set(string key, string color) => System.Windows.Application.Current.Resources[key] =
        new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
}

internal sealed class HotkeyRegistration : IDisposable
{
    private readonly nint _handle;
    private readonly HwndSource _source;
    private int _id = 700;
    private bool _registered;
    public event Action? Pressed;
    public HotkeyRegistration(Window window)
    {
        _handle = new WindowInteropHelper(window).EnsureHandle();
        _source = HwndSource.FromHwnd(_handle);
        _source.AddHook(Hook);
    }
    public void Register(string text)
    {
        var hotkey = Hotkey.Parse(text);
        int next = _id == 700 ? 701 : 700;
        if (!Native.RegisterHotKey(_handle, next, hotkey.Modifiers | 0x4000u, hotkey.Key))
            throw new InvalidOperationException("このショートカットは使用中です。別の組み合わせを指定してください。");
        if (_registered) Native.UnregisterHotKey(_handle, _id);
        _id = next; _registered = true;
    }
    public void Pause() { if (_registered) Native.UnregisterHotKey(_handle, _id); _registered = false; }
    private nint Hook(nint hwnd, int message, nint w, nint l, ref bool handled)
    { if (message == 0x0312 && w.ToInt32() == _id && _registered) { handled = true; Pressed?.Invoke(); } return 0; }
    public void Dispose() { Pause(); _source.RemoveHook(Hook); }
}
