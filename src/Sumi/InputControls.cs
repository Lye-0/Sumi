using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Sumi;

/// <summary>A key recorder, not a free-form text editor. Text remains a valid saved shortcut.</summary>
public sealed class ShortcutBox : TextBox
{
    public ShortcutBox()
    {
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        AllowDrop = false;
        ContextMenu = new ContextMenu();
        InputMethod.SetIsInputMethodEnabled(this, false);
        ToolTip = "クリックして Ctrl / Alt / Shift と英数字を押してください。Esc で終了します。";
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        SelectAll();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Tab) { base.OnPreviewKeyDown(e); return; }
        e.Handled = true;
        if (key == Key.Escape) { Keyboard.ClearFocus(); return; }
        if (e.IsRepeat) return;
        var modifiers = Keyboard.Modifiers;
        if ((modifiers & ModifierKeys.Windows) != 0 || modifiers == ModifierKeys.None) return;
        var vk = KeyInterop.VirtualKeyFromKey(key);
        // Matches the hotkey parser: letter or number row, plus Ctrl / Alt / Shift.
        if (!(vk is >= 0x41 and <= 0x5A or >= 0x30 and <= 0x39)) return;
        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        parts.Add(((char)vk).ToString());
        Text = string.Join("+", parts);
        MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
    }
}

/// <summary>Digits only, including paste, IME and drag/drop paths. Range checking happens on save.</summary>
public sealed class DigitsBox : TextBox
{
    public DigitsBox()
    {
        AllowDrop = false;
        InputMethod.SetIsInputMethodEnabled(this, false);
        DataObject.AddPastingHandler(this, (_, e) =>
        {
            if (!e.SourceDataObject.GetDataPresent(DataFormats.UnicodeText)
                || e.SourceDataObject.GetData(DataFormats.UnicodeText) is not string text || !IsDigits(text))
                e.CancelCommand();
        });
    }
    private static bool IsDigits(string text) => text.All(c => c is >= '0' and <= '9');
    protected override void OnPreviewTextInput(TextCompositionEventArgs e)
    { if (!IsDigits(e.Text)) e.Handled = true; base.OnPreviewTextInput(e); }
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    { if (e.Key == Key.Space) e.Handled = true; base.OnPreviewKeyDown(e); }
}
