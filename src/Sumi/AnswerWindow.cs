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
    private readonly ThinkingView _thinking;
    private readonly ScrollViewer _scroll;
    private readonly ScrollViewer _answerScroll;
    private bool _error;
    private readonly bool _minimal;
    private string _answer = "";
    public AnswerWindow(Settings settings, Action showFull, Func<string, Task> copy)
    {
        _minimal = settings.Delivery == Delivery.Minimal;
        Title = "Sumi · 回答"; Width = _minimal ? 240 : 380; SizeToContent = SizeToContent.Height;
        FontFamily = new System.Windows.Media.FontFamily("Yu Gothic UI, Segoe UI"); FontSize = 14;
        SetResourceReference(BackgroundProperty, "WindowBrush"); SetResourceReference(ForegroundProperty, "TextBrush");
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = App.UiTest; ShowActivated = false; Topmost = true;
        var stack = new StackPanel { Margin = _minimal ? new Thickness(10, 7, 10, 7) : new Thickness(22, 18, 22, 18) };
        _caption = new TextBlock { Text = "Sumi  /  回答", FontSize = 12, Margin = new Thickness(0, 0, 0, 14) };
        _caption.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); if (!_minimal) stack.Children.Add(_caption);
        _body = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 15, LineHeight = 25 };
        if (_minimal) { _body.FontSize = 12; _body.LineHeight = 17; _body.LineStackingStrategy = LineStackingStrategy.BlockLineHeight; }
        _answerScroll = new ScrollViewer { Content = _body, MaxHeight = _minimal ? 34 : double.PositiveInfinity,
            VerticalScrollBarVisibility = _minimal ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var content = new StackPanel(); content.Children.Add(_answerScroll);
        _thinking = new ThinkingView(copy, _minimal); content.Children.Add(_thinking);
        _scroll = new ScrollViewer { Content = content, MaxHeight = 350, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        stack.Children.Add(_scroll);
        _thinking.Expanded += (_, _) => _timer.Stop();
        _thinking.Collapsed += (_, _) => RestartTimer();
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 0) };
        _copy = new Button { Content = "コピー", Padding = new Thickness(12, 7, 12, 7) };
        _copy.Click += async (_, _) => await copy(_answer);
        var full = new Button { Content = "直近の回答", Margin = new Thickness(8, 0, 8, 0), Padding = new Thickness(12, 7, 12, 7) };
        full.Click += (_, _) => { showFull(); Close(); };
        var close = new Button { Content = "閉じる", Padding = new Thickness(12, 7, 12, 7) }; close.Click += (_, _) => Close();
        controls.Children.Add(_copy); controls.Children.Add(full); controls.Children.Add(close); if (!_minimal) stack.Children.Add(controls);
        var border = new Border { Child = stack, CornerRadius = new CornerRadius(18), BorderThickness = new Thickness(1) };
        if (_minimal)
        {
            border.CornerRadius = new CornerRadius(8); border.BorderThickness = new Thickness(0.5);
            MouseRightButtonUp += (_, e) => { e.Handled = true; Close(); };
            PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) { e.Handled = true; Close(); } };
        }
        border.SetResourceReference(Border.BorderBrushProperty, "LineBrush"); Content = border;
        border.SetResourceReference(Border.BackgroundProperty, "CanvasBrush");
        _timer.Interval = TimeSpan.FromSeconds(Math.Clamp(settings.DisplaySeconds, 3, 120));
        _timer.Tick += (_, _) => Close();
        MouseEnter += (_, _) => _timer.Stop();
        MouseLeave += (_, _) => RestartTimer();
        GotKeyboardFocus += (_, _) => _timer.Stop();
        LostKeyboardFocus += (_, _) => RestartTimer();
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
            if (!_minimal) border.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
        };
        SizeChanged += (_, _) => { if (IsLoaded) Position(); };
    }
    private bool _finished;
    private void RestartTimer()
    {
        _timer.Stop();
        if (_finished && !_error && !_thinking.IsExpanded && !IsMouseOver && !IsKeyboardFocusWithin) _timer.Start();
    }
    public void ClearThinking() => _thinking.Clear();
    public void Update(string text, bool finished, bool error = false, ThinkingUpdate[]? thoughts = null)
    {
        bool completing = finished && !_finished;
        bool follow = _scroll.VerticalOffset >= _scroll.ScrollableHeight - 2;
        var offset = _scroll.VerticalOffset;
        _answer = error ? "" : text;
        if (_body.Text != text)
        {
            var answerOffset = _answerScroll.VerticalOffset;
            _body.Text = text;
            // Keep the first two lines visible unless the reader explicitly scrolls.
            if (_minimal) _answerScroll.ScrollToVerticalOffset(answerOffset);
        }
        _finished = finished; _error = error; _copy.IsEnabled = finished && !error && text.Length > 0;
        _thinking.Update(thoughts ?? []);
        if (completing && !error) _thinking.IsExpanded = false;
        _caption.Text = error ? "Sumi  /  回答を取得できませんでした" : finished ? "Sumi  /  回答" : "Sumi  /  生成中";
        if (follow && !_thinking.IsExpanded) _scroll.ScrollToEnd(); else _scroll.ScrollToVerticalOffset(offset);
        if (finished) RestartTimer(); else _timer.Stop();
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
        _scroll.MaxHeight = Math.Max(80, Math.Min(_minimal ? 180 : 350, work.Height / scale - 160));
        int w = (int)Math.Ceiling(ActualWidth * scale), height = (int)Math.Ceiling(ActualHeight * scale);
        Native.SetWindowPos(h, new nint(-1), work.Right - w - 24, work.Bottom - height - 24, w, height, 0x0010);
    }
    private Forms.Screen? _screen;
    private bool _positioned;
}
