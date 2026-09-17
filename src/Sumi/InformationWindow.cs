using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using Sumi.Core;

namespace Sumi;

internal sealed class InformationWindow : Window
{
    public InformationWindow(SettingsStore store, Settings settings)
    {
        Title = "Sumi · アプリ情報";
        Width = 620; Height = 700; MinWidth = 460; MinHeight = 400;
        MaxHeight = Math.Max(400, SystemParameters.WorkArea.Height - 40);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.CanResize;
        ShowInTaskbar = App.UiTest; FontSize = 14; FontFamily = new FontFamily("Yu Gothic UI, Segoe UI");
        SetResourceReference(BackgroundProperty, "WindowBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        WindowChrome.SetWindowChrome(this, new WindowChrome
        { CaptionHeight = 64, ResizeBorderThickness = new Thickness(6), GlassFrameThickness = new Thickness(0), UseAeroCaptionButtons = false });
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(64) });
        grid.RowDefinitions.Add(new RowDefinition());
        var header = new Grid { Margin = new Thickness(24, 12, 18, 10) };
        header.Children.Add(new TextBlock { Text = "Sumiについて", FontSize = 21, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        var close = new Button { Content = "×", Padding = new Thickness(12, 6, 12, 6), HorizontalAlignment = HorizontalAlignment.Right };
        System.Windows.Automation.AutomationProperties.SetName(close, "アプリ情報を閉じる");
        WindowChrome.SetIsHitTestVisibleInChrome(close, true);
        close.Click += (_, _) => Close(); header.Children.Add(close); grid.Children.Add(header);
        var content = new StackPanel { Margin = new Thickness(24, 0, 24, 24) };
        var note = new TextBlock { Text = "現在使用している場所です。パスは選択してコピーできます。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 20) };
        note.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); content.Children.Add(note);
        AddPath(content, "アプリ本体のフォルダー", AppContext.BaseDirectory);
        AddPath(content, "常駐・アプリの設定ファイル", Path.Combine(store.Root, "settings.json"),
            File.Exists(Path.Combine(store.Root, "settings.json")) ? "保存した指示、モデル、ショートカット、表示設定を含みます。" : "初めて設定を保存したときに作成されます。");
        AddPath(content, "撮影画像の一時フォルダー", Path.Combine(store.Root, "temp"), "送信用の画像は処理終了後に削除されます。");
        var imageFolder = string.IsNullOrWhiteSpace(settings.ImageDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Sumi") : settings.ImageDirectory;
        AddPath(content, "撮影画像の保存先", imageFolder, settings.SaveImages ? "画像のファイル保存は有効です。" : "画像のファイル保存は現在オフです。");
        string executable;
        try { executable = OllamaCli.ResolveExecutable(settings.OllamaPath); }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { executable = "未検出（詳細設定から選択できます）"; }
        AddPath(content, "Ollama実行ファイル", executable);
#if DEBUG
        AddPath(content, "開発ログ", Path.Combine(store.Root, "development.log"), "Debug版のみ。状態とエラーを記録します。");
#endif
        var resident = new TextBlock { Text = "PC起動時の自動起動設定はありません。常駐はSumiを起動して開始した間だけ有効で、終了すると撮影ショートカットも解除されます。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
        resident.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); content.Children.Add(resident);
        var relocation = new TextBlock
        {
            Text = "1. トレイまたは設定画面からSumiを完全終了します。\n\n"
                + "2. 本体フォルダー一式を、使いたい場所へコピーします。Sumi.exeと付属資料など、同梱ファイルをまとめて移します。\n\n"
                + "3. 新しい場所のSumi.exeを起動して動作を確認します。自分で作成したショートカットがあれば、リンク先も変更してください。\n\n"
                + "通常のRelease版では設定はユーザーのLocalAppData内に保存されるため、本体の配置先を変えても引き継がれます。\n\n"
                + "開発中のbinはビルド出力先です。恒久的な設置先として登録しません。Debug版の既定データは実行ファイル横の.dev-data、開発スクリプト使用時はリポジトリ内の.dev-dataです。--data-dirやSUMI_DATA_DIRの指定があれば、その指定が優先されます。",
            TextWrapping = TextWrapping.Wrap, LineHeight = 23, Margin = new Thickness(0, 4, 0, 12)
        };
        content.Children.Add(new Expander { Header = "本体の場所を変更するには", Content = relocation });
        var scroll = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 1); grid.Children.Add(scroll);
        var border = new Border { CornerRadius = new CornerRadius(18), BorderThickness = new Thickness(1), Child = grid };
        border.SetResourceReference(Border.BackgroundProperty, "CanvasBrush"); border.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
        Content = border;
        SourceInitialized += (_, _) => Native.ConfigureFrame(this, settings.Theme);
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(); } };
    }

    private static void AddPath(Panel parent, string title, string path, string? description = null)
    {
        parent.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
        var value = new TextBox { Text = path, IsReadOnly = true, IsReadOnlyCaretVisible = false,
            Style = (Style)System.Windows.Application.Current.FindResource("PathTextBox"), Cursor = Cursors.Arrow,
            Margin = new Thickness(0, 0, 0, description == null ? 18 : 5) };
        System.Windows.Automation.AutomationProperties.SetName(value, title);
        parent.Children.Add(value);
        if (description != null)
        {
            var hint = new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 0, 0, 18) };
            hint.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); parent.Children.Add(hint);
        }
    }
}
