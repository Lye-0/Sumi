using System.Diagnostics;
using System.IO;
using System.Text;
using Sumi.Core;

Console.OutputEncoding = Encoding.UTF8;
if (args.Contains("--fixture"))
{
    var form = new System.Windows.Forms.Form { Text = "Sumi · テスト画像", Width = 680, Height = 380,
        StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen, BackColor = Color.White };
    form.Paint += (_, e) => { using var font = new Font("Segoe UI", 62, FontStyle.Bold);
        e.Graphics.DrawString("12 + 30 = ?", font, Brushes.Black, new PointF(35, 90)); };
    using var show = new System.Windows.Forms.Timer { Interval = 500 };
    show.Tick += (_, _) => { form.Show(); form.Activate(); show.Stop(); }; show.Start();
    System.Windows.Forms.Application.Run(form); return;
}
if (args.Contains("--child"))
{
    Console.InputEncoding = Encoding.UTF8;
    Console.Write(await Console.In.ReadToEndAsync());
    Console.Error.Write("stderr is separate");
    return;
}
if (args.Contains("--sleep-child"))
{
    Console.Write(Environment.ProcessId); Console.Out.Flush();
    await Task.Delay(60_000); return;
}
int checks = 0;
void Check(bool condition, string description)
{ if (!condition) throw new Exception("FAIL: " + description); checks++; Console.WriteLine("PASS " + description); }
async Task Reject(Func<Task> action, string description)
{ try { await action(); } catch (InvalidOperationException) { Check(true, description); return; } throw new Exception("FAIL: " + description); }

Check(PixelRect.Between(80, 40, -20, -10) == new PixelRect(-20, -10, 100, 50), "reverse drag / negative desktop coordinates");
Check(new PixelRect(-20, -10, 100, 50).Intersect(new(0, 0, 50, 100)) == new PixelRect(0, 0, 50, 40), "monitor intersection preserves physical pixels");
Check(Hotkey.Parse("Ctrl + Alt + s") == new Hotkey(3, 83), "hotkey normalization");
try { Hotkey.Parse("Win+S"); throw new Exception("FAIL reserved key"); } catch (FormatException) { Check(true, "reserved Windows key rejected"); }
Check(OllamaCli.AnswerText("Thinking...\nprivate thought") == "", "partial reasoning suppressed");
Check(OllamaCli.AnswerText("Thinking...\nthought\n...done thinking.\n\n答え") == "答え", "legacy CLI reasoning removed");
Check(OllamaCli.AnswerText("<think>thought</think>\n42") == "42", "plain CLI reasoning removed");
Check(OllamaCli.AnswerText("\u001b[?25l答え\u001b[0m") == "答え", "ANSI removed");
foreach (string prefix in new[] { "T", "Thinking..", "<thi", "<think>thought" })
    Check(OllamaCli.AnswerText(prefix) == "", "stream prefix " + prefix);
Check(OllamaCli.ParseModels("NAME ID SIZE\ngemma4:12b abc 7 GB\nx:cloud xyz -\nq:8b def 3 GB\n").SequenceEqual(new[] { "gemma4:12b", "q:8b" }), "model listing / cloud tags excluded");
var start = ProcessRunner.StartInfo("ollama.exe", ["run", "gemma4:12b"]);
Check(!start.UseShellExecute && start.CreateNoWindow && start.ArgumentList.SequenceEqual(new[] { "run", "gemma4:12b" }), "no shell, no model tuning flags");
Check(start.Environment["OLLAMA_HOST"] == "127.0.0.1:11434", "local endpoint pinned in child only");
Exception? inputFailure = null;
var inputThread = new Thread(() =>
{
    try
    {
        var box = new Sumi.DigitsBox();
        foreach (var sample in new[] { ("42", false), ("4a2", true), ("１２", true), ("12\n", true), ("-1", true) })
        {
            var data = new System.Windows.DataObject(System.Windows.DataFormats.UnicodeText, sample.Item1);
            var paste = new System.Windows.DataObjectPastingEventArgs(data, false, System.Windows.DataFormats.UnicodeText);
            box.RaiseEvent(paste);
            Check(paste.CommandCancelled == sample.Item2, "numeric paste accepts only ASCII digits: " + sample.Item1.Replace("\n", "\\n"));
        }
        Check(!box.AllowDrop && !System.Windows.Input.InputMethod.GetIsInputMethodEnabled(box), "numeric field blocks drag/drop and IME bypass");
        var shortcut = new Sumi.ShortcutBox();
        Check(shortcut.IsReadOnly && !shortcut.AllowDrop, "shortcut recorder blocks free text and file drops");
    }
    catch (Exception ex) { inputFailure = ex; }
});
inputThread.SetApartmentState(ApartmentState.STA); inputThread.Start(); inputThread.Join();
if (inputFailure != null) throw new Exception("Input control regression failed", inputFailure);
var root = Path.Combine(Path.GetTempPath(), "sumi-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var store = new SettingsStore(root);
    store.Save(new Settings { Prompt = "日本語\n\"引用\"", Delivery = Delivery.Both });
    Check(store.Load().Prompt == "日本語\n\"引用\"" && store.Load().Delivery == Delivery.Both, "atomic settings round trip");
    var file = Path.Combine(root, "検証 画像.png"); await File.WriteAllTextAsync(file, "fixture");
    var fake = new FakeRunner(); var provider = new OllamaCli("ollama.exe", fake);
    await provider.PrepareAsync("gemma4:12b", default);
    Check(fake.Calls[^1].Args.SequenceEqual(new[] { "run", "gemma4:12b" }) && fake.Calls[^1].Input == "", "preload = default run with EOF");
    var injection = "日本語\n$(do-not-run); \"quoted\"";
    await provider.AnswerAsync("gemma4:12b", injection, file, null, default);
    Check(fake.Calls[^1].Input == Path.GetFullPath(file) + "\n" + injection, "image and exact multiline prompt passed on stdin");
    Check(fake.Calls[^1].Args.Count == 2, "prompt cannot become flags or shell commands");
    await Reject(() => provider.PrepareAsync("not-installed", default), "missing model never reaches run / pull");
    fake.Remote = true;
    await Reject(() => provider.PrepareAsync("gemma4:12b", default), "cloud alias rejected by show metadata");
    var exe = Environment.ProcessPath!;
    var runner = new ProcessRunner();
    var echo = await runner.RunAsync(exe, ["--child"], injection, null, default);
    Check(echo.Output == injection && echo.Error == "stderr is separate", "real redirected process UTF8 / stdout-stderr separation");
    int childId = 0;
    using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(1));
    try { await runner.RunAsync(exe, ["--sleep-child"], "", new DirectProgress(s => int.TryParse(s, out childId)), cancel.Token); throw new Exception("FAIL cancellation"); }
    catch (OperationCanceledException) { }
    bool alive;
    try { using var child = Process.GetProcessById(childId); alive = !child.HasExited; } catch (ArgumentException) { alive = false; }
    Check(childId > 0 && !alive, "cancellation terminates and reaps owned child");
    if (args.Contains("--live"))
    {
        using (var bitmap = new Bitmap(640, 300))
        {
            using var graphics = Graphics.FromImage(bitmap); graphics.Clear(Color.White);
            using var font = new Font("Segoe UI", 62, FontStyle.Bold);
            graphics.DrawString("12 + 30 = ?", font, Brushes.Black, new PointF(40, 90));
            bitmap.Save(file, System.Drawing.Imaging.ImageFormat.Png);
        }
        var live = new OllamaCli(OllamaCli.ResolveExecutable(""));
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var watch = Stopwatch.StartNew(); await live.PrepareAsync("gemma4:12b", timeout.Token);
        Console.WriteLine($"LIVE preload {watch.Elapsed.TotalSeconds:F1}s"); watch.Restart();
        var answer = await live.AnswerAsync("gemma4:12b", "画像の計算式の答えを数字だけで答えてください。", file, null, timeout.Token);
        Console.WriteLine($"LIVE answer ({watch.Elapsed.TotalSeconds:F1}s): {answer}");
        Check(answer.Contains("42"), "real gemma4:12b reads synthetic image via CLI with default settings");
    }
}
finally { Directory.Delete(root, true); }
Console.WriteLine($"{checks} checks passed.");

sealed class DirectProgress(Action<string> action) : IProgress<string> { public void Report(string value) => action(value); }
sealed class FakeRunner : ICommandRunner
{
    public List<(IReadOnlyList<string> Args, string Input)> Calls { get; } = [];
    public bool Remote { get; set; }
    public Task<CommandResult> RunAsync(string exe, IReadOnlyList<string> args, string input, IProgress<string>? progress, CancellationToken token)
    {
        Calls.Add((args, input));
        string output = args[0] switch { "ls" => "NAME ID SIZE\ngemma4:12b abc 7 GB\n",
            "show" => Remote ? "Remote URL https://ollama.com\nvision" : "Capabilities\n    vision\n    thinking\n",
            _ => input.Length == 0 ? "" : "<think>test</think>\n42" };
        return Task.FromResult(new CommandResult(0, output, ""));
    }
}
