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
    private readonly bool _minimal;
    public ThinkingView(Func<string, Task> copy, bool minimal = false)
    {
        _minimal = minimal;
        _copy = copy; Header = "思考内容"; Content = _items;
        // Implicit styles use the exact runtime type: this subclass must opt in
        // to the application's Expander template instead of the OS default.
        SetResourceReference(StyleProperty, typeof(Expander));
        SetResourceReference(ForegroundProperty, "TextBrush");
        if (minimal)
        {
            FontSize = 11; Opacity = 0.5;
            SetResourceReference(StyleProperty, "MinimalThinking");
            MouseEnter += (_, _) => Opacity = 0.85;
            MouseLeave += (_, _) => Opacity = IsExpanded ? 0.85 : 0.5;
            Expanded += (_, _) => Opacity = 0.85;
            Collapsed += (_, _) => Opacity = IsMouseOver ? 0.85 : 0.5;
        }
        Visibility = Visibility.Collapsed;
    }
    public void Update(ThinkingUpdate[] attempts)
    {
        Visibility = attempts.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (_minimal)
        {
            if (!_texts.TryGetValue(0, out var compact))
            {
                compact = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
                    MinLines = 1, MaxLines = 2, MaxHeight = 32, FontSize = 11,
                    Padding = new Thickness(0), BorderThickness = new Thickness(0),
                    Background = System.Windows.Media.Brushes.Transparent,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
                TextBlock.SetLineHeight(compact, 16);
                TextBlock.SetLineStackingStrategy(compact, LineStackingStrategy.BlockLineHeight);
                AutomationProperties.SetName(compact, "思考内容");
                _texts.Add(0, compact); _items.Children.Add(compact);
            }
            // All attempts share one two-line viewport; retries must not add height.
            var text = attempts.Length == 1 ? attempts[0].Text : string.Join("\n\n", attempts.Select(a =>
                $"{(a.Attempt == 1 ? "1回目" : "再試行")}\n{a.Text}"));
            if (compact.Text != text)
            {
                var offset = compact.VerticalOffset;
                compact.Text = text; compact.ScrollToVerticalOffset(offset);
            }
            return;
        }
        foreach (var attempt in attempts)
        {
            if (!_texts.TryGetValue(attempt.Attempt, out var box))
            {
                var label = attempt.Attempt == 1 ? "1回目" : "再試行";
                box = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
                    MaxHeight = 240, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
                AutomationProperties.SetName(box, $"思考内容 · {label}");
                _texts.Add(attempt.Attempt, box);
                var heading = new TextBlock { Text = label, Margin = new Thickness(0, 6, 0, 6) };
                heading.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
                _items.Children.Add(heading);
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
