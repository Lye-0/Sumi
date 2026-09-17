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
    private HotkeyRegistration? _stockHotkey;
    private readonly ImageStock _stock = new();
    private Forms.NotifyIcon? _tray;
    private System.Drawing.Icon? _trayIcon;
    private SumiTrayMenu? _trayMenu;
    private bool _preparing;
    private CancellationTokenSource? _operation;
    private CancellationTokenSource? _refresh;
    private CaptureSession? _capture;
    private AnswerWindow? _answer;
    private bool _loaded, _ready, _busy, _exiting, _exitAllowed, _panelDismissed, _syncingModels;
    private string _latest = "";
    private ThinkingHistory _thoughts = new();
    private readonly ThinkingView _recentThinking;
    private string _latestError = "";
    private string? _preparedModel;
    private readonly Dictionary<string, OllamaCli> _sessionProviders = new(StringComparer.OrdinalIgnoreCase);

    public MainWindow(SettingsStore store, Settings settings, string? warning)
    {
        _store = store; _settings = settings;
        InitializeComponent();
        _recentThinking = new ThinkingView(CopyTextAsync); RecentThinkingHost.Children.Add(_recentThinking);
        ThinkingCheck.IsChecked = settings.ShowThinking;
        PromptBox.Text = settings.Prompt; HotkeyBox.Text = settings.Hotkey;
        StockHotkeyBox.Text = settings.StockHotkey;
        DeliveryBox.SelectedValue = settings.Delivery;
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
            Native.ConfigureFrame(this, _settings.Theme);
            _hotkey = new HotkeyRegistration(this);
            _hotkey.Pressed += async () => await CaptureAsync();
            _stockHotkey = new HotkeyRegistration(this, 702);
            _stockHotkey.Pressed += async () => await CaptureStockAsync();
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
    { _operation?.Cancel(); _refresh?.Cancel(); _hotkey?.Dispose(); _stockHotkey?.Dispose(); _tray?.Dispose(); _trayIcon?.Dispose(); _trayIcon = null; _exitAllowed = true; }

    private void SetupTray()
    {
        _trayIcon = Branding.CreateTrayIcon();
        _tray = new Forms.NotifyIcon { Icon = _trayIcon, Text = "Sumi · 準備前", Visible = true };
        _trayMenu = new SumiTrayMenu(GetTrayState, ShowSettings, CaptureAsync, CaptureStockAsync,
            ShowRecent, ClearStock, ToggleTrayAsync, ExitAsync, message => { ShowSettings(); Status(message); });
        _tray.MouseUp += (_, e) =>
        {
            if (_exiting) return;
            if (e.Button == Forms.MouseButtons.Left) { _trayMenu.IsOpen = false; ShowSettings(); }
            else if (e.Button == Forms.MouseButtons.Right)
            {
                _trayMenu.Update(GetTrayState());
                _trayMenu.ShowFromTray();
            }
        };
    }
    private TrayMenuState GetTrayState() => new()
    {
        Ready = _ready, Busy = _busy, Preparing = _preparing, Capturing = _capture != null,
        Refreshing = _refresh != null, Exiting = _exiting, PreparedBefore = _preparedModel != null,
        HasAnswer = _latest.Length > 0 || _latestError.Length > 0 || _thoughts.Snapshot().Length > 0,
        StockCount = _stock.Count, Model = _preparedModel ?? _settings.Model,
        SendKey = _settings.Hotkey, StockKey = _settings.StockHotkey
    };
    private async Task ToggleTrayAsync()
    {
        if (_exiting || _refresh != null) return;
        if (_busy || _ready) Pause(); else await StartAsync();
    }
    private void Status(string text)
    {
#if DEBUG
        try { Directory.CreateDirectory(_store.Root); File.AppendAllText(Path.Combine(_store.Root, "development.log"), $"{DateTime.Now:O} {text}\n"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
#endif
        StatusText.Text = text;
        StockCount.Text = $"ストック  {_stock.Count}枚";
        ClearStockButton.IsEnabled = _stock.Count > 0 && !_busy && !_exiting;
        if (_tray != null) _tray.Text = (_busy ? "Sumi · 処理中" : _ready ? "Sumi · 待機中" : "Sumi · 停止中") + $" · ストック{_stock.Count}枚";
    }
    private void ShowSettings() { Show(); WindowState = WindowState.Normal; Activate(); }
    private void ShowRecent() { ShowSettings(); RecentExpander.IsExpanded = true; RecentAnswer.BringIntoView(); }
    private void Minimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void HideWindow(object sender, RoutedEventArgs e) => Hide();
    private void OnClosing(object? sender, CancelEventArgs e) { if (!_exitAllowed) { e.Cancel = true; Hide(); } }

    private void Edited(object sender, TextChangedEventArgs e) => Dirty();
    private void EditedSelection(object sender, SelectionChangedEventArgs e) => Dirty();
    private void EditedCheck(object sender, RoutedEventArgs e) => Dirty();
    private void Dirty() { if (_loaded && !_syncingModels) SaveHint.Text = "未保存の変更があります。保存すると次の撮影から反映されます。"; }
    private void ProviderEdited(object sender, TextChangedEventArgs e) => Dirty();
    private void ModelChanged(object sender, SelectionChangedEventArgs e)
    { UpdatePreview(); if (_loaded && !_syncingModels) Dirty(); }
    private void UpdatePreview() { if (CommandText != null) CommandText.Text = $"ollama run {ModelBox.SelectedItem as string ?? "<モデル>"}"; }
    private void ToggleTheme(object sender, RoutedEventArgs e)
    {
        _settings = _settings with { Theme = _settings.Theme == Theme.Dark ? Theme.Light : Theme.Dark };
        ThemeButton.Content = _settings.Theme == Theme.Dark ? "ライト" : "ダーク";
        Appearance.Apply(_settings.Theme, GlassCheck.IsChecked == true); Dirty();
    }
    private void GlassChanged(object sender, RoutedEventArgs e)
    { if (_loaded) { Appearance.Apply(_settings.Theme, GlassCheck.IsChecked == true); Dirty(); } }

    private void ShortcutFocusEntered(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!_loaded) return;
        _hotkey?.Pause(); _stockHotkey?.Pause();
        ShortcutHint.Text = "キーを押してください · Escで終了 · 保存すると反映";
    }
    private void ShortcutFocusLeft(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!_loaded) return;
        ShortcutHint.Text = "キー欄をクリックして変更 · 保存すると反映";
        if (!_ready || _exiting) return;
        try { RegisterShortcuts(_settings); }
        catch (Exception ex) { _ready = false; UpdateControls(); Status(ex.Message); }
    }

    private void BrowseImageDirectory(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        { Title = "撮影画像の保存先", Multiselect = false };
        var path = ImageDirectoryBox.Text.Trim();
        if (Directory.Exists(path)) dialog.InitialDirectory = path;
        else dialog.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        if (dialog.ShowDialog(this) == true) ImageDirectoryBox.Text = dialog.FolderName;
    }
    private void BrowseOllama(object sender, RoutedEventArgs e)
    {
        if (_ready || _busy || _refresh != null) return;
        var dialog = new Microsoft.Win32.OpenFileDialog
        { Title = "Ollama実行ファイルを選択", Filter = "実行ファイル (*.exe)|*.exe", CheckFileExists = true, Multiselect = false };
        try
        {
            var path = OllamaCli.ResolveExecutable(OllamaPathBox.Text);
            dialog.InitialDirectory = Path.GetDirectoryName(path); dialog.FileName = Path.GetFileName(path);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { }
        if (dialog.ShowDialog(this) == true) OllamaPathBox.Text = dialog.FileName;
    }
    private void ShowInformation(object sender, RoutedEventArgs e)
    { new InformationWindow(_store, _settings) { Owner = this }.ShowDialog(); }

    private Settings ReadSettings()
    {
        if (string.IsNullOrWhiteSpace(PromptBox.Text)) throw new InvalidOperationException("画像への指示を入力してください。");
        var model = ModelBox.SelectedItem as string;
        if (string.IsNullOrWhiteSpace(model)) throw new InvalidOperationException("ローカルモデルを選択してください。");
        if (Hotkey.Parse(HotkeyBox.Text) == Hotkey.Parse(StockHotkeyBox.Text)) throw new InvalidOperationException("送信とストックのショートカットは別の組み合わせにしてください。");
        if (!int.TryParse(SecondsBox.Text, out var seconds) || seconds is < 3 or > 120)
            throw new InvalidOperationException("表示秒数は3〜120を指定してください。");
        if (!int.TryParse(TimeoutBox.Text, out var timeout) || timeout is < 30 or > 1800)
            throw new InvalidOperationException("待機上限は30〜1800秒を指定してください。");
        var imageDirectory = ImageDirectoryBox.Text.Trim();
        if (imageDirectory.Length > 0 && !Path.IsPathFullyQualified(imageDirectory))
            throw new InvalidOperationException("画像の保存先は絶対パスで指定してください。");
        return _settings with { Prompt = PromptBox.Text, Model = model, Hotkey = HotkeyBox.Text.Trim(), StockHotkey = StockHotkeyBox.Text.Trim(),
            Delivery = (Delivery)DeliveryBox.SelectedValue, Glass = GlassCheck.IsChecked == true,
            SaveImages = SaveImagesCheck.IsChecked == true, CopyImages = CopyImagesCheck.IsChecked == true, ShowThinking = ThinkingCheck.IsChecked == true,
            ImageDirectory = imageDirectory, OllamaPath = OllamaPathBox.Text.Trim(), DisplaySeconds = seconds, TimeoutSeconds = timeout };
    }
    private void SaveSettings()
    {
        var next = ReadSettings();
        bool rebind = _ready && (Hotkey.Parse(next.Hotkey) != Hotkey.Parse(_settings.Hotkey) || Hotkey.Parse(next.StockHotkey) != Hotkey.Parse(_settings.StockHotkey));
        if (rebind)
        {
            try { RegisterShortcuts(next); }
            catch { RestoreShortcuts(); throw; }
        }
        try { _store.Save(next); }
        catch { if (rebind) RestoreShortcuts(); throw; }
        _settings = next;
        if (!next.ShowThinking) { _thoughts = new(); _recentThinking.Clear(); _answer?.ClearThinking(); }
        SaveHint.Text = "保存済み  ·  × でトレイへ  ·  自動起動なし";
    }
    private void SaveClicked(object sender, RoutedEventArgs e)
    { try { SaveSettings(); Status(_ready ? "待機中 · 設定を保存しました。" : "設定を保存しました。モデルを準備して開始できます。"); } catch (Exception ex) { Status(ex.Message); } }
    private async void RefreshModels(object sender, RoutedEventArgs e) => await RefreshModelsAsync();
    private async Task RefreshModelsAsync()
    {
        if (_busy || _ready || _refresh != null || _exiting) return;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20)); _refresh = cts;
        UpdateControls();
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
        finally { if (_refresh == cts) { _refresh = null; UpdateControls(); } }
    }
    private void SetBusy(bool busy)
    {
        _busy = busy; UpdateControls();
    }
    private void UpdateControls()
    {
        ExitButton.IsEnabled = ReleaseExitButton.IsEnabled = SaveButton.IsEnabled = !_exiting;
        bool canPrepare = !_busy && !_ready && _refresh == null && !_exiting;
        StartButton.IsEnabled = canPrepare;
        ModelBox.IsEnabled = canPrepare; RefreshButton.IsEnabled = canPrepare;
        OllamaPathBox.IsEnabled = canPrepare; OllamaBrowseButton.IsEnabled = canPrepare;
        HotkeyBox.IsEnabled = StockHotkeyBox.IsEnabled = !_busy && !_exiting;
        ClearStockButton.IsEnabled = _stock.Count > 0 && !_busy && !_exiting;
        PauseButton.IsEnabled = (_busy || _ready) && !_exiting;
        PauseButton.Content = _busy ? "キャンセル" : "一時停止";
        if (_tray != null) _tray.Text = (_busy ? "Sumi · 処理中" : _ready ? "Sumi · 待機中" : "Sumi · 停止中") + $" · ストック{_stock.Count}枚";
    }
    private async void StartClicked(object sender, RoutedEventArgs e) => await StartAsync();
    private async Task StartAsync()
    {
        if (_busy || _ready || _refresh != null || _exiting) return;
        try { SaveSettings(); } catch (Exception ex) { Status(ex.Message); return; }
        _hotkey?.Pause(); _stockHotkey?.Pause(); _ready = false; _preparing = true; SetBusy(true);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(_settings.TimeoutSeconds)); _operation = cts;
        Status("モデルを準備中… 終わるとショートカットが有効になります。");
        try
        {
            var exe = OllamaCli.ResolveExecutable(_settings.OllamaPath);
            if (!_sessionProviders.TryGetValue(exe, out var provider))
                _sessionProviders.Add(exe, provider = new OllamaCli(exe));
            await provider.PrepareAsync(_settings.Model, cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            if (_exiting) return;
            RegisterShortcuts(_settings);
            _provider = provider; _preparedModel = _settings.Model; _ready = true;
            Status($"待機中 · {_settings.Hotkey} で範囲を選択。× で閉じても使えます。");
        }
        catch (OperationCanceledException) { Status("準備を中断しました（キャンセル、または待機上限）。"); }
        catch (Exception ex) { Status(ex.Message); }
        finally { _preparing = false; _operation = null; SetBusy(false); }
    }
    private void PauseClicked(object sender, RoutedEventArgs e) => Pause();
    private void Pause()
    {
        if (_exiting) return;
        _hotkey?.Pause(); _stockHotkey?.Pause(); _ready = false; _operation?.Cancel(); _capture?.Cancel();
        UpdateControls(); Status("停止中 · モデルを変更するか、そのまま再開できます。");
    }
    private void RegisterShortcuts(Settings settings)
    {
        _hotkey?.Pause(); _stockHotkey?.Pause();
        try { _hotkey!.Register(settings.Hotkey); _stockHotkey!.Register(settings.StockHotkey); }
        catch { _hotkey?.Pause(); _stockHotkey?.Pause(); throw; }
    }
    private void RestoreShortcuts()
    {
        try { RegisterShortcuts(_settings); }
        catch { _ready = false; UpdateControls(); throw; }
    }
    private void ClearStockClicked(object sender, RoutedEventArgs e) => ClearStock();
    private void ClearStock()
    {
        if (_busy || _exiting) return;
        _stock.Clear(); Status("ストックを破棄しました。");
    }
    private async Task CaptureStockAsync()
    {
        if (_exiting || _busy) return;
        if (!_ready) { ShowSettings(); Status("先にモデルを準備して開始してください。"); return; }
        if (_stock.Count >= ImageStock.MaxImages) { Status("ストックは20枚までです。送信するか破棄してください。"); return; }
        if (_trayMenu != null) _trayMenu.IsOpen = false;
        SetBusy(true); _answer?.Close();
        using var cts = new CancellationTokenSource(); _operation = cts;
        try
        {
            Hide(); await Task.Delay(120, cts.Token);
            CaptureResult? result;
            using (var capture = new CaptureSession())
            { _capture = capture; result = await capture.RunAsync(); }
            _capture = null; cts.Token.ThrowIfCancellationRequested();
            if (result != null) _stock.Add(result.Png);
            Status($"ストック：{_stock.Count}枚 · {_settings.Hotkey} で最後の1枚を撮影して送信します。");
        }
        catch (OperationCanceledException) { Status($"撮影を中断しました。ストック：{_stock.Count}枚"); }
        catch (Exception ex) { Status(ex.Message); if (!_exiting) ShowSettings(); }
        finally { _capture = null; _operation = null; SetBusy(false); }
    }
    private async Task CaptureAsync()
    {
        if (_exiting) return;
        if (_busy) { Status("処理中です。次の撮影は完了後に行えます。"); return; }
        if (!_ready || _provider == null) { ShowSettings(); Status("先にモデルを準備して開始してください。"); return; }
        var settings = _settings; var provider = _provider;
        if (_trayMenu != null) _trayMenu.IsOpen = false;
        SetBusy(true); _answer?.Close(); _answer = null; _panelDismissed = false;
        _thoughts = new(); _latest = ""; _latestError = ""; RecentAnswer.Text = ""; RecentError.Text = ""; _recentThinking.Clear();
        var thoughts = _thoughts;
        bool generationComplete = false;
        var thoughtTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        thoughtTimer.Tick += (_, _) =>
        {
            if (!settings.ShowThinking || !_settings.ShowThinking || _exiting) return;
            var snapshot = thoughts.Snapshot();
            _recentThinking.Update(snapshot);
            if (snapshot.Length > 0 && settings.Delivery != Delivery.Clipboard)
                ShowAnswer(_latest.Length == 0 ? "思考中…" : _latest, false, settings);
        };
        if (settings.ShowThinking) thoughtTimer.Start();
        using var cts = new CancellationTokenSource(); _operation = cts;
        var temps = new List<string>();
        bool fromStock = _stock.Count > 0;
        try
        {
            if (IsVisible) Hide();
            await Task.Delay(120, cts.Token); // Let our windows disappear before freezing the desktop.
            Status("最後の画像を選択中 · Escでキャンセルできます。");
            CaptureResult? result;
            using (var capture = new CaptureSession())
            { _capture = capture; result = await capture.RunAsync(); }
            _capture = null;
            cts.Token.ThrowIfCancellationRequested();
            if (result == null) { Status("撮影をキャンセルしました。ストックは保持しています。"); return; }
            var images = _stock.WithFinalImage(result.Png);
            cts.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
            var tempRoot = Path.Combine(_store.Root, "temp"); Directory.CreateDirectory(tempRoot);
            string? sideEffectWarning = null;
            foreach (var png in images)
            {
            var temp = Path.Combine(tempRoot, $"capture-{Guid.NewGuid():N}.png");
            temps.Add(temp);
            await File.WriteAllBytesAsync(temp, png, cts.Token);
            if (settings.SaveImages)
            {
                try
                {
                    var folder = string.IsNullOrWhiteSpace(settings.ImageDirectory)
                        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Sumi") : settings.ImageDirectory;
                    Directory.CreateDirectory(folder);
                    await File.WriteAllBytesAsync(Path.Combine(folder, $"Sumi-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.png"), png, cts.Token);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { sideEffectWarning = "画像の保存に失敗しました。"; }
            }
            if (settings.CopyImages)
            {
                try { await CopyImageAsync(png); }
                catch (System.Runtime.InteropServices.COMException) { sideEffectWarning = "画像をコピーできませんでした。"; }
            }
            }
            Status($"{images.Length}枚の画像から回答を生成中…");
            var progress = new Progress<string>(text =>
            {
                if (generationComplete || cts.IsCancellationRequested || _exiting || _operation != cts || string.IsNullOrWhiteSpace(text)) return;
                _latest = text; RecentAnswer.Text = text;
                if (settings.Delivery != Delivery.Clipboard) ShowAnswer(text, false, settings);
            });
            var generationStatus = new Progress<string>(text =>
            {
                if (!cts.IsCancellationRequested && !_exiting && _operation == cts) Status(text);
            });
            var answer = await provider.AnswerAsync(settings.Model, settings.Prompt, temps, progress, cts.Token, generationStatus,
                settings.ShowThinking || settings.Delivery is (Delivery.Clipboard or Delivery.Both or Delivery.MinimalBoth) ? thoughts : null);
            thoughtTimer.Stop();
            generationComplete = true;
            cts.Token.ThrowIfCancellationRequested();
            _latest = answer; RecentAnswer.Text = answer;
            if (fromStock) _stock.Clear();
            if (settings.Delivery is Delivery.Clipboard or Delivery.Both or Delivery.MinimalBoth)
            {
                try { await SetClipboardAsync(() => System.Windows.Clipboard.SetText(answer)); }
                catch (System.Runtime.InteropServices.COMException) { sideEffectWarning = "回答をコピーできませんでした。直近の回答から再試行できます。"; }
            }
            if (settings.Delivery != Delivery.Clipboard) ShowAnswer(answer, true, settings);
            Status(sideEffectWarning ?? "待機中 · 回答を受け取りました。");
            if (sideEffectWarning != null) { _latestError = sideEffectWarning; RecentError.Text = sideEffectWarning; }
        }
        catch (OperationCanceledException)
        {
            thoughtTimer.Stop(); _latest = ""; RecentAnswer.Text = "";
            _latestError = "生成を中断しました（キャンセル、または待機上限）。";
            RecentError.Text = _latestError; Status(_latestError);
            if (!_exiting) { _panelDismissed = false; ShowAnswer(_latestError, true, settings, true); }
        }
        catch (Exception ex)
        {
            thoughtTimer.Stop(); _latest = ""; RecentAnswer.Text = "";
            _latestError = ex.Message; RecentError.Text = ex.Message;
            if (!_exiting && ex is MissingAnswerException)
            {
                var fallback = thoughts.ClipboardFallback(settings.Delivery);
                if (fallback.Length > 0)
                {
                    try
                    {
                        await SetClipboardAsync(() => System.Windows.Clipboard.SetText(fallback));
                        _latestError += "\n回答本文がないため、最後に取得した思考内容をクリップボードへコピーしました。";
                    }
                    catch (System.Runtime.InteropServices.COMException)
                    { _latestError += "\n思考内容をクリップボードへコピーできませんでした。"; }
                }
            }
            RecentError.Text = _latestError;
            _panelDismissed = false; Status(_latestError);
            if (!_exiting) ShowAnswer(_latestError, true, settings, true);
        }
        finally
        {
            thoughtTimer.Stop();
            if (settings.ShowThinking && _settings.ShowThinking) _recentThinking.Update(thoughts.Snapshot());
            else _thoughts = new();
            _capture = null;
            foreach (var temp in temps) { try { File.Delete(temp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Status("一時画像を削除できませんでした。設定フォルダーのtempを確認してください。"); } }
            _operation = null; SetBusy(false);
        }
    }
    private void ShowAnswer(string text, bool finished, Settings settings, bool error = false)
    {
        if (_panelDismissed) return;
        if (_answer == null)
        {
            _answer = new AnswerWindow(settings, ShowRecent, CopyTextAsync);
            _answer.Closed += (_, _) => { _answer = null; _panelDismissed = true; };
            _answer.Update(text, finished, error, settings.ShowThinking && _settings.ShowThinking ? _thoughts.Snapshot() : []); _answer.Show();
        }
        else _answer.Update(text, finished, error, settings.ShowThinking && _settings.ShowThinking ? _thoughts.Snapshot() : []);
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
    { if (!string.IsNullOrWhiteSpace(_latest) || _latestError.Length > 0) { _panelDismissed = false; ShowAnswer(_latestError.Length > 0 ? _latestError : _latest, true, _settings, _latestError.Length > 0); } }
    private async void ExitClicked(object sender, RoutedEventArgs e) => await ExitAsync();
    private async void ReleaseExitClicked(object sender, RoutedEventArgs e) => await ExitAsync(true);
    private async Task ExitAsync(bool releaseModels = false)
    {
        if (_exiting) return;
        _exiting = true; if (_trayMenu != null) _trayMenu.IsOpen = false; _ready = false; _hotkey?.Pause(); _stockHotkey?.Pause(); _operation?.Cancel(); _refresh?.Cancel(); _capture?.Cancel();
        UpdateControls();
        // Await the process runner's finally, so no child CLI or temporary capture survives a normal exit.
        while (_operation != null || _refresh != null) await Task.Delay(40);
        if (releaseModels)
        {
            ShowSettings();
            Status("使用したモデルを解放しています…");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try
            {
                var errors = new List<string>();
                foreach (var provider in _sessionProviders.Values)
                {
                    try { await provider.ReleaseUsedModelsAsync(timeout.Token); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { errors.Add(ex.Message); }
                }
                if (errors.Count > 0) throw new InvalidOperationException(string.Join("\n", errors));
            }
            catch (Exception ex)
            {
                _exiting = false; UpdateControls();
                Status((ex is OperationCanceledException ? "モデルの解放が時間内に完了しませんでした。" : $"モデルを解放できませんでした。{ex.Message}")
                    + "\n「モデルを解放して終了」で再試行、または「終了」でSumiだけを終了できます。");
                return;
            }
        }
        _answer?.Close(); _hotkey?.Dispose(); _stockHotkey?.Dispose(); _tray?.ContextMenuStrip?.Dispose(); _tray?.Dispose(); _trayIcon?.Dispose(); _trayIcon = null;
        _stock.Clear(); _exitAllowed = true; System.Windows.Application.Current.Shutdown();
    }
}
