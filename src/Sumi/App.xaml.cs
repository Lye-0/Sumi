using System.IO;
using System.Windows;
using Sumi.Core;

namespace Sumi;

public partial class App : System.Windows.Application
{
    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private RegisteredWaitHandle? _showWait;
    public static string DataRoot { get; private set; } = "";
    internal static bool UiTest { get; private set; }
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
#if DEBUG
        UiTest = e.Args.Contains("--ui-test");
#endif
        var dataIndex = Array.IndexOf(e.Args, "--data-dir");
        DataRoot = dataIndex >= 0 && dataIndex + 1 < e.Args.Length ? Path.GetFullPath(e.Args[dataIndex + 1])
            : Environment.GetEnvironmentVariable("SUMI_DATA_DIR") is { Length: > 0 } configured ? Path.GetFullPath(configured)
#if DEBUG
            : Path.Combine(AppContext.BaseDirectory, ".dev-data");
#else
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sumi");
#endif
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\Sumi.Show");
        _mutex = new Mutex(true, "Local\\Sumi.Desktop", out bool created);
        if (!created) { _showEvent.Set(); _mutex.Dispose(); _mutex = null; Shutdown(); return; }
        var store = new SettingsStore(DataRoot);
        Settings settings;
        string? warning = null;
        try { settings = store.Load(); }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        { settings = new(); warning = "設定を復元できませんでした。元のファイルは変更していません。\n" + ex.Message; }
        var window = new MainWindow(store, settings, warning);
        MainWindow = window;
        window.Show();
        _showWait = ThreadPool.RegisterWaitForSingleObject(_showEvent, (_, _) => Dispatcher.BeginInvoke(new Action(() =>
        { if (!Dispatcher.HasShutdownStarted) { window.Show(); window.WindowState = WindowState.Normal; window.Activate(); } })), null, -1, false);
    }
    protected override void OnExit(ExitEventArgs e)
    {
        _showWait?.Unregister(null); _showEvent?.Dispose();
        _mutex?.ReleaseMutex(); _mutex?.Dispose(); base.OnExit(e);
    }
}
