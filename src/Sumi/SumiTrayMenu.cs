using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Threading;
using Sumi.Core;

namespace Sumi;

internal sealed class SumiTrayMenu : ContextMenu
{
    private readonly TextBlock _phase = new() { FontWeight = FontWeights.SemiBold };
    private readonly TextBlock _detail = new() { FontSize = 12, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap, MaxWidth = 300 };
    private readonly MenuItem _send, _stock, _recent, _clear, _toggle, _open, _exit, _release;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    public SumiTrayMenu(Func<TrayMenuState> state, Action open, Func<Task> send, Func<Task> stock,
        Action recent, Action clear, Func<Task> toggle, Func<bool, Task> exit, Action<string> error)
    {
        SetResourceReference(StyleProperty, "TrayContextMenu");
        Placement = PlacementMode.MousePoint;
        var heading = new StackPanel(); heading.Children.Add(_phase); heading.Children.Add(_detail);
        _detail.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        var header = new MenuItem { Header = heading, IsEnabled = false, Focusable = false };
        header.SetResourceReference(StyleProperty, "TrayHeading"); Items.Add(header);
        Items.Add(new Separator());
        _send = Add("撮影して送信", send); _stock = Add("撮影してストック", stock);
        Items.Add(new Separator());
        _recent = Add("直近の回答", () => { recent(); return Task.CompletedTask; });
        _clear = Add("ストックをすべて破棄", () => { clear(); return Task.CompletedTask; });
        Items.Add(new Separator());
        _toggle = Add("一時停止", toggle);
        _open = Add("Sumiを開く", () => { open(); return Task.CompletedTask; });
        Items.Add(new Separator());
        _exit = Add("終了", () => exit(false)); _release = Add("モデルを解放して終了", () => exit(true));
        _exit.SetResourceReference(ForegroundProperty, "TrayDangerBrush");
        _release.SetResourceReference(ForegroundProperty, "TrayDangerBrush");
        _timer.Tick += (_, _) => Update(state());
        Opened += (_, _) =>
        {
            Update(state()); _timer.Start();
        };
        Closed += (_, _) => _timer.Stop();
        Update(state());

        MenuItem Add(string title, Func<Task> action)
        {
            var item = new MenuItem { Header = title };
            item.Click += async (_, _) =>
            {
                IsOpen = false;
                try { await action(); } catch (Exception ex) { error(ex.Message); }
            };
            Items.Add(item); return item;
        }
    }
    public void Update(TrayMenuState state)
    {
        // Popups have a separate resource tree: refresh the active theme on open.
        foreach (var key in new[] { "WindowBrush", "TextBrush", "MutedBrush", "InputBrush", "LineBrush", "TrayDangerBrush" })
            if (System.Windows.Application.Current.TryFindResource(key) is { } brush && !ReferenceEquals(Resources[key], brush))
                Resources[key] = brush;
        _phase.Text = $"Sumi · {state.Phase}";
        _detail.Text = $"{(string.IsNullOrWhiteSpace(state.Model) ? "モデル未選択" : state.Model)} · ストック {state.StockCount}枚";
        _send.InputGestureText = state.SendKey; _stock.InputGestureText = state.StockKey;
        _send.IsEnabled = state.CanCapture; _stock.IsEnabled = state.CanStock;
        _recent.IsEnabled = state.HasAnswer && !state.Exiting;
        _clear.IsEnabled = state.CanClear;
        _toggle.Header = state.ToggleLabel; _toggle.IsEnabled = state.CanToggle;
        _open.IsEnabled = _exit.IsEnabled = _release.IsEnabled = !state.Exiting;
    }
}
