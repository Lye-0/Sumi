using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Sumi.Core;
using Forms = System.Windows.Forms;

namespace Sumi;

public partial class MainWindow : Window
{
    private readonly SettingsStore _store;
    private Settings _settings;
    private IModelProvider? _provider;
    private HotkeyRegistration? _hotkey;
    private Forms.NotifyIcon? _tray;
    private CancellationTokenSource? _operation;
    private CancellationTokenSource? _refresh;
    private CaptureSession? _capture;
    private AnswerWindow? _answer;
    private bool _loaded, _ready, _busy, _exiting, _exitAllowed, _panelDismissed, _syncingModels;
    private string _latest = "";
    private string? _preparedModel;

    public MainWindow(SettingsStore store, Settings settings, string? warning)
    {
        _store = store; _settings = settings;
        InitializeComponent();
        PromptBox.Text = settings.Prompt; HotkeyBox.Text = settings.Hotkey;
        DeliveryBox.SelectedIndex = (int)settings.Delivery;
        ModelBox.Items.Add(settings.Model); ModelBox.SelectedItem = settings.Model;
        GlassCheck.IsChecked = settings.Glass; SaveImagesCheck.IsChecked = settings.SaveImages;
        CopyImagesCheck.IsChecked = settings.CopyImages; ImageDirectoryBox.Text = settings.ImageDirectory;
        OllamaPathBox.Text = settings.OllamaPath;
        SecondsBox.Text = settings.DisplaySeconds.ToString(); TimeoutBox.Text = settings.TimeoutSeconds.ToString();
        Appearance.Apply(settings.Theme, settings.Glass);
        ThemeButton.Content = settings.Theme == Theme.Dark ? "ライト" : "ダーク";
        UpdatePreview();
        SourceInitialized += (_, _) =>
        {
            Native.Glass(this, _settings.Glass, _settings.Theme);
            _hotkey = new HotkeyRegistration(this);
            _hotkey.Pressed += async () => await CaptureAsync();
        };
        Loaded += async (_, _) =>
        {
            _loaded = true; SetupTray(); await RefreshModelsAsync();
            if (warning != null) Status(warning);
        };
        Closing += OnClosing;
        System.Windows.Application.Current.SessionEnding += OnApplicationSessionEnding;
    }
    // Windows shutdown must not be turned into close-to-tray.
    private void OnApplicationSessionEnding(object sender, SessionEndingCancelEventArgs e)
    { _operation?.Cancel(); _refresh?.Cancel(); _hotkey?.Dispose(); _tray?.Dispose(); _exitAllowed = true; }

    private void SetupTray()
    {
        _tray = new Forms.NotifyIcon { Icon = System.Drawing.SystemIcons.Application, Text = "Sumi · 準備前", Visible = true };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Sumiを開く", null, (_, _) => ShowSettings());
        menu.Items.Add("撮影", null, async (_, _) => await CaptureAsync());
        menu.Items.Add("直近の回答", null, (_, _) => ShowRecent());
        menu.Items.Add("一時停止", null, (_, _) => Pause());
        menu.Items.Add("終了", null, async (_, _) => await ExitAsync());
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ShowSettings();
    }
    private void Status(string text)
    {
#if DEBUG
        try { Directory.CreateDirectory(_store.Root); File.AppendAllText(Path.Combine(_store.Root, "development.log"), $"{DateTime.Now:O} {text}\n"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
#endif
        StatusText.Text = text;
        if (_tray != null) _tray.Text = _busy ? "Sumi · 処理中" : _ready ? "Sumi · 待機中" : "Sumi · 停止中";
    }
    private void ShowSettings() { Show(); WindowState = WindowState.Normal; Activate(); }
    private void ShowRecent() { ShowSettings(); RecentExpander.IsExpanded = true; RecentAnswer.BringIntoView(); }
    private void TitleDrag(object sender, MouseButtonEventArgs e)
    { if (e.OriginalSource is not Button && e.LeftButton == MouseButtonState.Pressed) { try { DragMove(); } catch (InvalidOperationException) { } } }
    private void Minimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void HideWindow(object sender, RoutedEventArgs e) => Hide();
    private void OnClosing(object? sender, CancelEventArgs e) { if (!_exitAllowed) { e.Cancel = true; Hide(); } }

    private void Edited(object sender, TextChangedEventArgs e) => Dirty();
    private void EditedSelection(object sender, SelectionChangedEventArgs e) => Dirty();
    private void EditedCheck(object sender, RoutedEventArgs e) => Dirty();
    private void Dirty() { if (_loaded && !_syncingModels) SaveHint.Text = "未保存の変更があります。保存すると次の撮影から反映されます。"; }
    private void ProviderEdited(object sender, TextChangedEventArgs e) { if (_loaded) { Dirty(); Pause(); } }
    private void ModelChanged(object sender, SelectionChangedEventArgs e)
    { UpdatePreview(); if (_loaded && !_syncingModels) { Dirty(); if (ModelBox.SelectedItem as string != _preparedModel) Pause(); } }
    private void UpdatePreview() { if (CommandText != null) CommandText.Text = $"ollama run {ModelBox.SelectedItem as string ?? "<モデル>"}"; }
    private void ToggleTheme(object sender, RoutedEventArgs e)
    {
        _settings = _settings with { Theme = _settings.Theme == Theme.Dark ? Theme.Light : Theme.Dark };
        ThemeButton.Content = _settings.Theme == Theme.Dark ? "ライト" : "ダーク";
        Appearance.Apply(_settings.Theme, GlassCheck.IsChecked == true); Dirty();
    }
    private void GlassChanged(object sender, RoutedEventArgs e)
    { if (_loaded) { Appearance.Apply(_settings.Theme, GlassCheck.IsChecked == true); Dirty(); } }

    private Settings ReadSettings()
    {
        if (string.IsNullOrWhiteSpace(PromptBox.Text)) throw new InvalidOperationException("画像への指示を入力してください。");
        var model = ModelBox.SelectedItem as string;
        if (string.IsNullOrWhiteSpace(model)) throw new InvalidOperationException("ローカルモデルを選択してください。");
        Hotkey.Parse(HotkeyBox.Text);
        if (!int.TryParse(SecondsBox.Text, out var seconds) || seconds is < 3 or > 120)
            throw new InvalidOperationException("表示秒数は3〜120を指定してください。");
        if (!int.TryParse(TimeoutBox.Text, out var timeout) || timeout is < 30 or > 1800)
            throw new InvalidOperationException("待機上限は30〜1800秒を指定してください。");
        var imageDirectory = ImageDirectoryBox.Text.Trim();
        if (imageDirectory.Length > 0 && !Path.IsPathFullyQualified(imageDirectory))
            throw new InvalidOperationException("画像の保存先は絶対パスで指定してください。");
        return _settings with { Prompt = PromptBox.Text, Model = model, Hotkey = HotkeyBox.Text.Trim(),
            Delivery = (Delivery)DeliveryBox.SelectedIndex, Glass = GlassCheck.IsChecked == true,
            SaveImages = SaveImagesCheck.IsChecked == true, CopyImages = CopyImagesCheck.IsChecked == true,
            ImageDirectory = imageDirectory, OllamaPath = OllamaPathBox.Text.Trim(), DisplaySeconds = seconds, TimeoutSeconds = timeout };
    }
    private void SaveSettings()
    {
        var next = ReadSettings();
        bool rebind = _ready && Hotkey.Parse(next.Hotkey) != Hotkey.Parse(_settings.Hotkey);
        if (rebind) _hotkey!.Register(next.Hotkey);
        try { _store.Save(next); }
        catch { if (rebind) _hotkey!.Register(_settings.Hotkey); throw; }
        _settings = next;
        SaveHint.Text = "保存済み  ·  × でトレイへ  ·  自動起動なし";
    }
    private void SaveClicked(object sender, RoutedEventArgs e)
    { try { SaveSettings(); Status(_ready ? "待機中 · 設定を保存しました。" : "設定を保存しました。モデルを準備して開始できます。"); } catch (Exception ex) { Status(ex.Message); } }
    private async void RefreshModels(object sender, RoutedEventArgs e) => await RefreshModelsAsync();
    private async Task RefreshModelsAsync()
    {
        if (_busy) return;
        _refresh?.Cancel();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20)); _refresh = cts;
        RefreshButton.IsEnabled = false; StartButton.IsEnabled = false;
        try
        {
            var selected = ModelBox.SelectedItem as string ?? _settings.Model;
            var provider = new OllamaCli(OllamaCli.ResolveExecutable(OllamaPathBox.Text));
            var models = await provider.ListModelsAsync(cts.Token);
            if (_refresh != cts || _exiting) return;
            _syncingModels = true;
            try { ModelBox.Items.Clear(); foreach (var m in models) ModelBox.Items.Add(m);
                ModelBox.SelectedItem = models.Contains(selected) ? selected : models.FirstOrDefault(); }
            finally { _syncingModels = false; }
            if (_ready && ModelBox.SelectedItem as string != _preparedModel) Pause();
            Status(_ready ? "待機中 · モデル一覧を更新しました。" : models.Count > 0 ? "モデルを選んで、準備を始めましょう。" : "ローカルモデルが見つかりません。");
        }
        catch (OperationCanceledException) { if (!_exiting) Status("モデル一覧の取得を中断しました。Ollamaの起動状態を確認してください。"); }
        catch (Exception ex) { Status(ex.Message); }
        finally { if (_refresh == cts) { _refresh = null; RefreshButton.IsEnabled = true; StartButton.IsEnabled = !_busy; } }
    }
    private void SetBusy(bool busy)
    {
        _busy = busy; StartButton.IsEnabled = !busy; ModelBox.IsEnabled = !busy; RefreshButton.IsEnabled = !busy;
        OllamaPathBox.IsEnabled = !busy; PauseButton.IsEnabled = busy || _ready; PauseButton.Content = busy ? "キャンセル" : "一時停止";
        if (_tray != null) _tray.Text = busy ? "Sumi · 処理中" : _ready ? "Sumi · 待機中" : "Sumi · 停止中";
    }
    private async void StartClicked(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        try { SaveSettings(); } catch (Exception ex) { Status(ex.Message); return; }
        _hotkey?.Pause(); _ready = false; SetBusy(true);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(_settings.TimeoutSeconds)); _operation = cts;
        Status("モデルを準備中… 終わるとショートカットが有効になります。");
        try
        {
            var exe = OllamaCli.ResolveExecutable(_settings.OllamaPath);
            var provider = new OllamaCli(exe);
            await provider.PrepareAsync(_settings.Model, cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            if (_exiting) return;
            _hotkey!.Register(_settings.Hotkey);
            _provider = provider; _preparedModel = _settings.Model; _ready = true;
            Status($"待機中 · {_settings.Hotkey} で範囲を選択。× で閉じても使えます。");
        }
        catch (OperationCanceledException) { Status("準備を中断しました（キャンセル、または待機上限）。"); }
        catch (Exception ex) { Status(ex.Message); }
        finally { _operation = null; SetBusy(false); }
    }
    private void PauseClicked(object sender, RoutedEventArgs e) => Pause();
    private void Pause()
    {
        _hotkey?.Pause(); _ready = false; _operation?.Cancel(); _capture?.Cancel();
        PauseButton.IsEnabled = _busy; Status("停止中 · 再開するときは「モデルを準備して開始」を押してください。");
    }
    private async Task CaptureAsync()
    {
        if (_exiting) return;
        if (_busy) { Status("処理中です。次の撮影は完了後に行えます。"); return; }
        if (!_ready || _provider == null) { ShowSettings(); Status("先にモデルを準備して開始してください。"); return; }
        var settings = _settings; var provider = _provider;
        SetBusy(true); _answer?.Close(); _answer = null; _panelDismissed = false;
        using var cts = new CancellationTokenSource(); _operation = cts;
        string? temp = null;
        try
        {
            if (IsVisible) Hide();
            await Task.Delay(120, cts.Token); // Let our windows disappear before freezing the desktop.
            Status("範囲を選択中 · Escでキャンセルできます。");
            CaptureResult? result;
            using (var capture = new CaptureSession())
            { _capture = capture; result = await capture.RunAsync(); }
            _capture = null;
            cts.Token.ThrowIfCancellationRequested();
            if (result == null) { Status("待機中 · 撮影をキャンセルしました。"); return; }
            cts.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
            var tempRoot = Path.Combine(_store.Root, "temp"); Directory.CreateDirectory(tempRoot);
            temp = Path.Combine(tempRoot, $"capture-{Guid.NewGuid():N}.png");
            await File.WriteAllBytesAsync(temp, result.Png, cts.Token);
            string? sideEffectWarning = null;
            if (settings.SaveImages)
            {
                try
                {
                    var folder = string.IsNullOrWhiteSpace(settings.ImageDirectory)
                        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Sumi") : settings.ImageDirectory;
                    Directory.CreateDirectory(folder);
                    await File.WriteAllBytesAsync(Path.Combine(folder, $"Sumi-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.png"), result.Png, cts.Token);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { sideEffectWarning = "画像の保存に失敗しました。"; }
            }
            if (settings.CopyImages)
            {
                try { await CopyImageAsync(result.Png); }
                catch (System.Runtime.InteropServices.COMException) { sideEffectWarning = "画像をコピーできませんでした。"; }
            }
            Status("回答を生成中…");
            var progress = new Progress<string>(text =>
            {
                if (cts.IsCancellationRequested || _exiting || _operation != cts || string.IsNullOrWhiteSpace(text)) return;
                if (settings.Delivery != Delivery.Clipboard) ShowAnswer(text, false, settings);
            });
            var answer = await provider.AnswerAsync(settings.Model, settings.Prompt, temp, progress, cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            _latest = answer; RecentAnswer.Text = answer;
            if (settings.Delivery != Delivery.Panel)
            {
                try { await SetClipboardAsync(() => System.Windows.Clipboard.SetText(answer)); }
                catch (System.Runtime.InteropServices.COMException) { sideEffectWarning = "回答をコピーできませんでした。直近の回答から再試行できます。"; }
            }
            if (settings.Delivery != Delivery.Clipboard) ShowAnswer(answer, true, settings);
            Status(sideEffectWarning ?? "待機中 · 回答を受け取りました。");
            if (sideEffectWarning != null) ShowAnswer(sideEffectWarning, true, settings);
        }
        catch (OperationCanceledException) { _answer?.Close(); _answer = null; Status(_ready ? "待機中 · 生成を中断しました（キャンセル、または待機上限）。" : "停止中 · 処理をキャンセルしました。"); }
        catch (Exception ex)
        {
            _answer?.Close(); _answer = null; _panelDismissed = false; Status(ex.Message);
            if (!_exiting) ShowAnswer(ex.Message, true, settings);
        }
        finally
        {
            _capture = null;
            if (temp != null) { try { File.Delete(temp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Status("一時画像を削除できませんでした。設定フォルダーのtempを確認してください。"); } }
            _operation = null; SetBusy(false);
        }
    }
    private void ShowAnswer(string text, bool finished, Settings settings)
    {
        if (_panelDismissed) return;
        if (_answer == null)
        {
            _answer = new AnswerWindow(settings, ShowRecent, CopyTextAsync);
            _answer.Closed += (_, _) => { _answer = null; _panelDismissed = true; };
            _answer.Update(text, finished); _answer.Show();
        }
        else _answer.Update(text, finished);
    }
    private static async Task SetClipboardAsync(Action action)
    {
        for (int i = 0; ; i++)
        {
            try { action(); return; }
            catch (System.Runtime.InteropServices.COMException) when (i < 4) { await Task.Delay(60); }
        }
    }
    private static Task CopyImageAsync(byte[] png)
    {
        using var ms = new MemoryStream(png); var image = new BitmapImage(); image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = ms; image.EndInit(); image.Freeze();
        return SetClipboardAsync(() => System.Windows.Clipboard.SetImage(image));
    }
    private async Task CopyTextAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        try { await SetClipboardAsync(() => System.Windows.Clipboard.SetText(text)); Status("回答をコピーしました。"); }
        catch (System.Runtime.InteropServices.COMException) { Status("クリップボードを使用できません。少し待って再試行してください。"); }
    }
    private async void CopyRecent(object sender, RoutedEventArgs e) => await CopyTextAsync(_latest);
    private void ShowRecentPanel(object sender, RoutedEventArgs e)
    { if (!string.IsNullOrWhiteSpace(_latest)) { _panelDismissed = false; ShowAnswer(_latest, true, _settings); } }
    private async void ExitClicked(object sender, RoutedEventArgs e) => await ExitAsync();
    private async Task ExitAsync()
    {
        if (_exiting) return;
        _exiting = true; _ready = false; _hotkey?.Pause(); _operation?.Cancel(); _refresh?.Cancel(); _capture?.Cancel();
        // Await the process runner's finally, so no child CLI or temporary capture survives a normal exit.
        while (_operation != null || _refresh != null) await Task.Delay(40);
        _answer?.Close(); _hotkey?.Dispose(); _tray?.ContextMenuStrip?.Dispose(); _tray?.Dispose();
        _exitAllowed = true; System.Windows.Application.Current.Shutdown();
    }
}
