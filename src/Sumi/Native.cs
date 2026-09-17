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
    [StructLayout(LayoutKind.Sequential)] internal struct PointI { public int X, Y; }

    internal static void ConfigureFrame(Window window, Theme theme)
    {
        var h = new WindowInteropHelper(window).Handle;
        if (h == 0) return;
        int dark = theme == Theme.Dark ? 1 : 0, corners = 2;
        DwmSetWindowAttribute(h, 20, ref dark, 4);
        DwmSetWindowAttribute(h, 33, ref corners, 4);
        // WindowChrome owns the non-client frame. Do not extend DWM glass or
        // reset the WPF composition target here, especially during theme changes.
    }
}

internal static class Appearance
{
    public static void Apply(Theme theme, bool glass)
    {
        bool dark = theme == Theme.Dark;
        Set("WindowBrush", dark ? "#FF192129" : "#FFF1F6F8");
        // Glass-like layers remain inside WPF, over an always-opaque window.
        // Native full-window transparency can blank the surface during redraw.
        var canvas = glass
            ? (Brush)new LinearGradientBrush((Color)ColorConverter.ConvertFromString(dark ? "#26343E" : "#FAFDFE"),
                (Color)ColorConverter.ConvertFromString(dark ? "#192129" : "#ECF2F5"), 65)
            : new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? "#192129" : "#F1F6F8"));
        canvas.Freeze();
        System.Windows.Application.Current.Resources["CanvasBrush"] = canvas;
        Set("SurfaceBrush", dark ? (glass ? "#902B3742" : "#FF232D37") : (glass ? "#C8FFFFFF" : "#FFFFFFFF"));
        Set("InputBrush", dark ? (glass ? "#9010161E" : "#FF161D25") : (glass ? "#C4FFFFFF" : "#FFFFFFFF"));
        Set("LineBrush", dark ? "#48596F7D" : "#40778991");
        Set("TextBrush", dark ? "#F0F5F7" : "#202F38");
        Set("MutedBrush", dark ? "#ABBDCA" : "#596D79");
        Set("AccentBrush", dark ? "#B2E3D6" : "#235F53");
        Set("AccentTextBrush", dark ? "#102E28" : "#FFFFFF");
        foreach (Window w in System.Windows.Application.Current.Windows)
            if (w is MainWindow or AnswerWindow) Native.ConfigureFrame(w, theme);
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
