using System.Drawing.Imaging;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Sumi.Core;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace Sumi;

internal sealed record CaptureResult(byte[] Png, PixelRect Bounds);

internal sealed class CaptureSession : IDisposable
{
    private readonly List<CaptureOverlay> _windows = [];
    private readonly TaskCompletionSource<CaptureResult?> _completion = new();
    private readonly Drawing.Bitmap _desktop;
    private readonly Drawing.Rectangle _bounds;
    private Native.PointI? _start;
    private bool _closed;
    public CaptureSession()
    {
        _bounds = Forms.SystemInformation.VirtualScreen;
        _desktop = new Drawing.Bitmap(_bounds.Width, _bounds.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var graphics = Drawing.Graphics.FromImage(_desktop);
        graphics.CopyFromScreen(_bounds.Location, Drawing.Point.Empty, _bounds.Size, Drawing.CopyPixelOperation.SourceCopy);
    }
    public Task<CaptureResult?> RunAsync()
    {
        try
        {
            foreach (var screen in Forms.Screen.AllScreens)
            {
                var b = screen.Bounds;
                using var crop = _desktop.Clone(new Drawing.Rectangle(b.X - _bounds.X, b.Y - _bounds.Y, b.Width, b.Height), _desktop.PixelFormat);
                using var ms = new MemoryStream();
                crop.Save(ms, ImageFormat.Png); ms.Position = 0;
                var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = ms; image.EndInit(); image.Freeze();
                var window = new CaptureOverlay(this, b, image);
                _windows.Add(window); window.Show();
            }
            Native.GetCursorPos(out var p);
            (_windows.FirstOrDefault(w => w.ScreenBounds.Contains(p.X, p.Y)) ?? _windows[0]).Activate();
            return _completion.Task;
        }
        catch { Finish(null); throw; }
    }
    public void Begin(Native.PointI p) { _start = p; Update(p); }
    public void Update(Native.PointI p)
    {
        if (_start is not { } start) return;
        var area = PixelRect.Between(start.X, start.Y, p.X, p.Y);
        foreach (var window in _windows) { window.Selection = area; window.Refresh(); }
    }
    public void End(Native.PointI p)
    {
        if (_closed || _start is not { } start) return;
        var rect = PixelRect.Between(start.X, start.Y, p.X, p.Y)
            .Intersect(new(_bounds.X, _bounds.Y, _bounds.Width, _bounds.Height));
        if (rect.Width < 3 || rect.Height < 3) { Finish(null); return; }
        try
        {
            using var image = _desktop.Clone(new Drawing.Rectangle(rect.X - _bounds.X, rect.Y - _bounds.Y, rect.Width, rect.Height), _desktop.PixelFormat);
            using var ms = new MemoryStream(); image.Save(ms, ImageFormat.Png);
            Finish(new(ms.ToArray(), rect));
        }
        catch (Exception ex) { CloseWindows(); _completion.TrySetException(ex); }
    }
    public void Cancel() => Finish(null);
    private void Finish(CaptureResult? result) { if (_closed) return; CloseWindows(); _completion.TrySetResult(result); }
    private void CloseWindows()
    {
        _closed = true;
        foreach (var window in _windows) { window.ReleaseMouseCapture(); window.Close(); }
    }
    public void Dispose() { Cancel(); _desktop.Dispose(); }
}

internal sealed class CaptureOverlay : Window
{
    private readonly CaptureSession _session;
    private readonly BitmapSource _image;
    private readonly CaptureSurface _surface;
    public Drawing.Rectangle ScreenBounds { get; }
    public PixelRect? Selection { get; set; }
    public CaptureOverlay(CaptureSession session, Drawing.Rectangle bounds, BitmapSource image)
    {
        _session = session; _image = image; ScreenBounds = bounds;
        _surface = new CaptureSurface(this); Content = _surface;
        Title = "Sumi · 範囲選択"; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        Width = bounds.Width; Height = bounds.Height; Left = bounds.X; Top = bounds.Y;
        ShowInTaskbar = App.UiTest; Topmost = true; Background = Brushes.Black; Cursor = Cursors.Cross;
        SourceInitialized += (_, _) =>
        {
            Native.SetWindowPos(new WindowInteropHelper(this).Handle, new nint(-1), bounds.X, bounds.Y, bounds.Width, bounds.Height, 0x0040);
        };
        Loaded += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            var source = HwndSource.FromHwnd(h);
            var size = source.CompositionTarget.TransformFromDevice.Transform(new Vector(bounds.Width, bounds.Height));
            Width = size.X; Height = size.Y;
            UpdateLayout();
            Native.SetWindowPos(h, new nint(-1), bounds.X, bounds.Y, bounds.Width, bounds.Height, 0x0040);
        };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; session.Cancel(); } };
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    { var p = Native.MouseMessagePosition(); base.OnMouseLeftButtonDown(e); CaptureMouse(); _session.Begin(p); e.Handled = true; }
    protected override void OnMouseMove(MouseEventArgs e)
    { base.OnMouseMove(e); if (IsMouseCaptured) _session.Update(Native.MouseMessagePosition()); }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    { var p = Native.MouseMessagePosition(); base.OnMouseLeftButtonUp(e); if (IsMouseCaptured) _session.End(p); e.Handled = true; }
    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e) { _session.Cancel(); e.Handled = true; }
    internal void Refresh() => _surface.InvalidateVisual();
    private void Draw(DrawingContext dc)
    {
        double width = _surface.ActualWidth, height = _surface.ActualHeight;
        var full = new Rect(0, 0, width, height);
        dc.DrawImage(_image, full);
        Rect? selected = null;
        if (Selection is { } area)
        {
            var r = area.Intersect(new(ScreenBounds.X, ScreenBounds.Y, ScreenBounds.Width, ScreenBounds.Height));
            if (r.Width > 0 && r.Height > 0)
            {
                selected = new Rect((r.X - ScreenBounds.X) * width / ScreenBounds.Width,
                    (r.Y - ScreenBounds.Y) * height / ScreenBounds.Height,
                    r.Width * width / ScreenBounds.Width, r.Height * height / ScreenBounds.Height);
            }
        }
        if (selected is { } s) dc.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromRgb(178, 227, 214)), 1.5), s);
        else
        {
            var text = new FormattedText("ドラッグして範囲を選択    ·    Esc で戻る", System.Globalization.CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight, new Typeface("Yu Gothic UI"), 14, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            var box = new Rect(Math.Max(16, (width - text.Width - 36) / 2), 28, text.Width + 36, 44);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(230, 22, 32, 39)), null, box, 16, 16);
            dc.DrawText(text, new Point(box.X + 18, box.Y + 12));
        }
    }
    private sealed class CaptureSurface(CaptureOverlay owner) : FrameworkElement
    {
        protected override void OnRender(DrawingContext dc) { base.OnRender(dc); owner.Draw(dc); }
    }
}
