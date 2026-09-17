using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Sumi.Core;
using Forms = System.Windows.Forms;

namespace Sumi;

internal sealed class AnswerWindow : Window
{
    private readonly TextBlock _body;
    private readonly TextBlock _caption;
    private readonly DispatcherTimer _timer = new();
    private readonly Button _copy;
    private string _answer = "";
    public AnswerWindow(Settings settings, Action showFull, Func<string, Task> copy)
    {
        Title = "Sumi · 回答"; Width = 380; SizeToContent = SizeToContent.Height;
        FontFamily = new System.Windows.Media.FontFamily("Yu Gothic UI, Segoe UI"); FontSize = 14;
        SetResourceReference(BackgroundProperty, "WindowBrush"); SetResourceReference(ForegroundProperty, "TextBrush");
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = App.UiTest; ShowActivated = false; Topmost = true;
        var stack = new StackPanel { Margin = new Thickness(22, 18, 22, 18) };
        _caption = new TextBlock { Text = "Sumi  /  回答", FontSize = 12, Margin = new Thickness(0, 0, 0, 14) };
        _caption.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); stack.Children.Add(_caption);
        _body = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 15, LineHeight = 25 };
        stack.Children.Add(new ScrollViewer { Content = _body, MaxHeight = 270, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 0) };
        _copy = new Button { Content = "コピー", Padding = new Thickness(12, 7, 12, 7) };
        _copy.Click += async (_, _) => await copy(_answer);
        var full = new Button { Content = "全文", Margin = new Thickness(8, 0, 8, 0), Padding = new Thickness(12, 7, 12, 7) };
        full.Click += (_, _) => { showFull(); Close(); };
        var close = new Button { Content = "閉じる", Padding = new Thickness(12, 7, 12, 7) }; close.Click += (_, _) => Close();
        controls.Children.Add(_copy); controls.Children.Add(full); controls.Children.Add(close); stack.Children.Add(controls);
        var border = new Border { Child = stack, CornerRadius = new CornerRadius(18), BorderThickness = new Thickness(1) };
        border.SetResourceReference(Border.BorderBrushProperty, "LineBrush"); Content = border;
        border.SetResourceReference(Border.BackgroundProperty, "CanvasBrush");
        _timer.Interval = TimeSpan.FromSeconds(Math.Clamp(settings.DisplaySeconds, 3, 120));
        _timer.Tick += (_, _) => Close();
        MouseEnter += (_, _) => _timer.Stop();
        MouseLeave += (_, _) => { if (_finished) _timer.Start(); };
        Closed += (_, _) => _timer.Stop();
        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            Native.SetWindowLong(h, -20, Native.GetWindowLong(h, -20) | 0x08000000 | (App.UiTest ? 0 : 0x80));
            Native.ConfigureFrame(this, settings.Theme);
        };
        ContentRendered += (_, _) =>
        {
            Position();
            border.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
        };
        SizeChanged += (_, _) => { if (IsLoaded) Position(); };
    }
    private bool _finished;
    public void Update(string text, bool finished)
    {
        _answer = text; _body.Text = text; _finished = finished; _copy.IsEnabled = finished;
        _caption.Text = finished ? "Sumi  /  回答" : "Sumi  /  回答を受信中";
        if (finished) _timer.Start();
    }
    private void Position()
    {
        var h = new WindowInteropHelper(this).Handle;
        if (h == 0) return;
        Native.GetCursorPos(out var p);
        _screen ??= Forms.Screen.FromPoint(new System.Drawing.Point(p.X, p.Y));
        var work = _screen.WorkingArea;
        // Move to the target monitor first so Windows supplies its actual DPI.
        if (!_positioned) { Native.SetWindowPos(h, new nint(-1), work.Right - 400, work.Top + 32, 0, 0, 0x0011); _positioned = true; }
        double scale = Native.GetDpiForWindow(h) / 96d;
        int w = (int)Math.Ceiling(ActualWidth * scale), height = (int)Math.Ceiling(ActualHeight * scale);
        Native.SetWindowPos(h, new nint(-1), work.Right - w - 24, work.Bottom - height - 24, w, height, 0x0010);
    }
    private Forms.Screen? _screen;
    private bool _positioned;
}
