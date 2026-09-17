using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using Sumi.Core;

namespace Sumi;

internal sealed class ThinkingView : Expander
{
    private readonly StackPanel _items = new();
    private readonly Dictionary<int, TextBox> _texts = new();
    private readonly Func<string, Task> _copy;
    public ThinkingView(Func<string, Task> copy)
    {
        _copy = copy; Header = "思考内容"; Content = _items;
        Visibility = Visibility.Collapsed;
    }
    public void Update(ThinkingUpdate[] attempts)
    {
        Visibility = attempts.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        foreach (var attempt in attempts)
        {
            if (!_texts.TryGetValue(attempt.Attempt, out var box))
            {
                var label = attempt.Attempt == 1 ? "1回目" : "再試行";
                box = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
                    MaxHeight = 240, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
                AutomationProperties.SetName(box, $"思考内容 · {label}");
                _texts.Add(attempt.Attempt, box);
                _items.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 6, 0, 6) });
                _items.Children.Add(box);
                var captured = box;
                var button = new Button { Content = "思考内容をコピー", HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(0, 6, 0, 8), Padding = new Thickness(10, 6, 10, 6) };
                button.Click += async (_, _) => await _copy(captured.Text);
                _items.Children.Add(button);
            }
            if (box.Text == attempt.Text) continue;
            bool follow = box.VerticalOffset >= box.ExtentHeight - box.ViewportHeight - 2;
            double offset = box.VerticalOffset;
            box.Text = attempt.Text;
            if (follow) box.ScrollToEnd(); else box.ScrollToVerticalOffset(offset);
        }
    }
    public void Clear()
    { _items.Children.Clear(); _texts.Clear(); IsExpanded = false; Visibility = Visibility.Collapsed; }
}
