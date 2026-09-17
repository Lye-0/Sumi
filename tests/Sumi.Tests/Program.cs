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

Check(!new Settings().ShowThinking, "thinking display defaults off");
var stock = new ImageStock();
stock.Add([1]); stock.Add([2, 3]);
Check(stock.Count == 2 && stock.Bytes == 3 && stock.Snapshot()[1].SequenceEqual(new byte[] { 2, 3 }), "stock preserves capture order and byte count");
stock.Clear(); Check(stock.Count == 0 && stock.Bytes == 0, "stock clear releases all references");
for (int n = 0; n < ImageStock.MaxImages; n++) stock.Add([1]);
await Reject(() => { stock.Add([2]); return Task.CompletedTask; }, "stock count is bounded");
Check(Hotkey.Parse(new Settings().StockHotkey) != Hotkey.Parse(new Settings().Hotkey), "default stock and send shortcuts differ");
Check(OllamaCli.ThinkingText("Thinking...\nreason\n...done thinking.\nD", true) == "reason", "CLI thinking separated from answer");
Check(OllamaCli.ThinkingText("<think>reason</think>D", true) == "reason", "tagged thinking separated from answer");
Check(OllamaCli.ThinkingText("<think>unfinished", true) == "unfinished", "unfinished thinking retained at EOF");
Check(OllamaCli.ThinkingText("<think>reason</thi") == "reason", "partial closing marker hidden during streaming");
Check(OllamaCli.ThinkingText("D") == "", "plain answer is never thinking");
Exception? panelFailure = null;
var panelThread = new Thread(() =>
{
    try
    {
        var type = typeof(Sumi.App).Assembly.GetType("Sumi.AnswerWindow")!;
        var window = (System.Windows.Window)Activator.CreateInstance(type, new Settings { ShowThinking = true }, (Action)(() => { }), (Func<string, Task>)(_ => Task.CompletedTask))!;
        var update = type.GetMethod("Update")!;
        var data = new[] { new ThinkingUpdate(1, new string('x', 30000)), new ThinkingUpdate(2, "retry") };
        update.Invoke(window, ["error", true, true, data]);
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var expander = (System.Windows.Controls.Expander)type.GetField("_thinking", flags)!.GetValue(window)!;
        var expanderStyle = new System.Windows.Style(typeof(System.Windows.Controls.Expander));
        window.Resources[typeof(System.Windows.Controls.Expander)] = expanderStyle;
        window.Resources["TextBrush"] = System.Windows.Media.Brushes.White;
        var heading = (System.Windows.Controls.TextBlock)((System.Windows.Controls.StackPanel)expander.Content).Children[0];
        Check(ReferenceEquals(expander.Style, expanderStyle) && ReferenceEquals(expander.Foreground, System.Windows.Media.Brushes.White)
            && ReferenceEquals(heading.Foreground, System.Windows.Media.Brushes.White), "thinking subclass uses themed expander style and dark foreground");
        window.Resources["TextBrush"] = System.Windows.Media.Brushes.Black;
        Check(ReferenceEquals(expander.Foreground, System.Windows.Media.Brushes.Black) && ReferenceEquals(heading.Foreground, System.Windows.Media.Brushes.Black), "thinking labels follow live light theme changes");
        var timer = (System.Windows.Threading.DispatcherTimer)type.GetField("_timer", flags)!.GetValue(window)!;
        var copy = (System.Windows.Controls.Button)type.GetField("_copy", flags)!.GetValue(window)!;
        Check(expander.Visibility == System.Windows.Visibility.Visible && !timer.IsEnabled && !copy.IsEnabled, "error preserves accordion, disables final copy and auto-dismiss");
        update.Invoke(window, ["D", false, false, data]);
        expander.IsExpanded = true;
        update.Invoke(window, ["D", true, false, data]);
        Check(!expander.IsExpanded && copy.IsEnabled, "successful completion collapses thinking and enables final copy");
        expander.IsExpanded = true;
        Check(!timer.IsEnabled, "expanded thinking prevents auto-dismiss");
        type.GetMethod("ClearThinking")!.Invoke(window, null);
        Check(expander.Visibility == System.Windows.Visibility.Collapsed, "disabling thinking hides retained view");
        window.Close();
        var minimal = (System.Windows.Window)Activator.CreateInstance(type, new Settings { Delivery = Delivery.MinimalBoth, ShowThinking = true }, (Action)(() => { }), (Func<string, Task>)(_ => Task.CompletedTask))!;
        update.Invoke(minimal, ["D", true, false, data]);
        var root = (System.Windows.Controls.StackPanel)((System.Windows.Controls.Border)minimal.Content).Child;
        var thinking = (System.Windows.Controls.Expander)type.GetField("_thinking", flags)!.GetValue(minimal)!;
        Check(minimal.Width < window.Width && root.Children.Count == 1, "minimal notification contains only answer/thinking scroll area");
        Check(!((System.Windows.Controls.StackPanel)thinking.Content).Children.OfType<System.Windows.Controls.Button>().Any(), "minimal thinking has no copy buttons");
        var compactThought = ((System.Windows.Controls.StackPanel)thinking.Content).Children.OfType<System.Windows.Controls.TextBox>().Single();
        Check(compactThought.MaxLines == 2 && compactThought.MaxHeight == 32 && compactThought.Text.Contains("retry"), "minimal thinking shares a two-line scroll viewport across retries");
        double MeasureAnswer(string text)
        {
            var sample = (System.Windows.Window)Activator.CreateInstance(type, new Settings { Delivery = Delivery.Minimal }, (Action)(() => { }), (Func<string, Task>)(_ => Task.CompletedTask))!;
            update.Invoke(sample, [text, true, false, Array.Empty<ThinkingUpdate>()]);
            var scroll = (System.Windows.Controls.ScrollViewer)type.GetField("_answerScroll", flags)!.GetValue(sample)!;
            scroll.Measure(new System.Windows.Size(220, double.PositiveInfinity));
            var height = scroll.DesiredSize.Height; sample.Close(); return height;
        }
        var oneLine = MeasureAnswer("one"); var twoLines = MeasureAnswer("one\ntwo"); var threeLines = MeasureAnswer("one\ntwo\nthree");
        Check(oneLine <= 18 && twoLines > oneLine && twoLines <= 34 && threeLines == twoLines, "minimal answer grows from one to two lines then caps height");
        minimal.Close();
    }
    catch (Exception ex) { panelFailure = ex; }
});
panelThread.SetApartmentState(ApartmentState.STA); panelThread.Start(); panelThread.Join();
if (panelFailure != null) throw panelFailure;

var releaseRunner = new FakeRunner();
var releaseProvider = new OllamaCli("ollama.exe", releaseRunner);
await releaseProvider.ReleaseUsedModelsAsync(default);
Check(releaseRunner.Calls.Count == 0, "unused provider does not stop any model");
await releaseProvider.PrepareAsync("gemma4:12b", default);
await releaseProvider.PrepareAsync("gemma4:12b", default);
await releaseProvider.PrepareAsync("second:local", default);
Check(releaseProvider.UsedModels.Count == 2, "distinct used models retained across preparations");
releaseRunner.FailStop = true;
await Reject(() => releaseProvider.ReleaseUsedModelsAsync(default), "stop failures surfaced");
Check(releaseProvider.UsedModels.Count == 2 && releaseRunner.Calls.Count(c => c.Args[0] == "stop") == 2, "failed releases remain retryable and all models attempted");
releaseRunner.FailStop = false;
await releaseProvider.ReleaseUsedModelsAsync(default);
Check(releaseProvider.UsedModels.Count == 0 && releaseRunner.Calls.Where(c => c.Args[0] == "stop").All(c => c.Args.Count == 2 && c.Input == ""), "release uses CLI stop with exact model argument");
var releasedCalls = releaseRunner.Calls.Count;
await releaseProvider.ReleaseUsedModelsAsync(default);
Check(releaseRunner.Calls.Count == releasedCalls, "successful releases are not repeated");
await releaseProvider.PrepareAsync("gemma4:12b", default);
using (var stopped = new CancellationTokenSource())
{
    stopped.Cancel();
    try { await releaseProvider.ReleaseUsedModelsAsync(stopped.Token); throw new Exception("FAIL cancelled stop"); }
    catch (OperationCanceledException) { Check(releaseProvider.UsedModels.Count == 1, "cancelled release retains pending model"); }
}

Check(PixelRect.Between(80, 40, -20, -10) == new PixelRect(-20, -10, 100, 50), "reverse drag / negative desktop coordinates");
Check(new PixelRect(-20, -10, 100, 50).Intersect(new(0, 0, 50, 100)) == new PixelRect(0, 0, 50, 40), "monitor intersection preserves physical pixels");
Check(Hotkey.Parse("Ctrl + Alt + s") == new Hotkey(3, 83), "hotkey normalization");
try { Hotkey.Parse("Win+S"); throw new Exception("FAIL reserved key"); } catch (FormatException) { Check(true, "reserved Windows key rejected"); }
Check(OllamaCli.AnswerText("Thinking...\nprivate thought") == "", "partial reasoning suppressed");
Check(OllamaCli.AnswerText("Thinking...\nthought\n...done thinking.\n\n答え") == "答え", "legacy CLI reasoning removed");
Check(OllamaCli.AnswerText("<think>thought</think>\n42") == "42", "plain CLI reasoning removed");
Check(OllamaCli.AnswerText("\u001b[?25l答え\u001b[0m") == "答え", "ANSI removed");
Check(OllamaCli.FinalAnswerText("T") == "T" && OllamaCli.FinalAnswerText("<") == "<", "completed short answers are not mistaken for streamed markers");
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
    foreach (var delivery in Enum.GetValues<Delivery>())
    {
        store.Save(new Settings { Delivery = delivery });
        Check(store.Load().Delivery == delivery, $"delivery setting round trip: {delivery}");
    }
    store.Save(new Settings { Prompt = "日本語\n\"引用\"", Delivery = Delivery.Both });
    Check(store.Load().Prompt == "日本語\n\"引用\"" && store.Load().Delivery == Delivery.Both, "atomic settings round trip");
    var file = Path.Combine(root, "検証 画像.png"); await File.WriteAllTextAsync(file, "fixture");
    var secondFile = Path.Combine(root, "検証 画像2.png"); await File.WriteAllTextAsync(secondFile, "fixture2");
    var multiRunner = new FakeRunner();
    multiRunner.Responses.Enqueue(new(0, "Thinking...only", ""));
    multiRunner.Responses.Enqueue(new(0, "both", ""));
    var multiAnswer = await new OllamaCli("ollama.exe", multiRunner).AnswerAsync("gemma4:12b", "both images", new[] { file, secondFile }, null, default);
    Check(multiAnswer == "both" && multiRunner.Calls.Where(c => c.Args[0] == "run").All(c => c.Input == $"{file}\n{secondFile}\nboth images"), "multi-image CLI input and retries preserve order and prompt");
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
    var normal = new FakeRunner();
    await new OllamaCli("ollama.exe", normal).AnswerAsync("gemma4:12b", injection, file, null, default);
    Check(normal.GenerationCount == 1, "successful response does not retry");

    var recovery = new FakeRunner();
    recovery.Responses.Enqueue(new(0, "Thinking...\nAnswer: (D)\n\n", ""));
    recovery.Responses.Enqueue(new(0, "Thinking...\nreason\n...done thinking.\n(D) complicated\n", ""));
    var notices = new List<string>(); var streamed = new List<string>();
    var recovered = await new OllamaCli("ollama.exe", recovery).AnswerAsync("gemma4:12b", injection, file,
        new DirectProgress(streamed.Add), default, new DirectProgress(notices.Add));
    Check(recovered == "(D) complicated" && recovery.GenerationCount == 2, "thinking-only output retries once and returns final answer");
    Check(notices.Count == 1 && notices[0].Contains("思考のみ") && streamed.All(s => !s.Contains("Answer:")), "retry reason reported without exposing thinking as an answer");
    var generations = recovery.Calls.Where(c => c.Args[0] == "run").ToArray();
    Check(generations.All(c => c.Input == Path.GetFullPath(file) + "\n" + injection
        && c.Args.SequenceEqual(new[] { "run", "gemma4:12b" })), "retry preserves exact image, prompt and default command");

    var emptyRecovery = new FakeRunner();
    emptyRecovery.Responses.Enqueue(new(0, "\n\n", ""));
    emptyRecovery.Responses.Enqueue(new(0, "C", ""));
    Check(await new OllamaCli("ollama.exe", emptyRecovery).AnswerAsync("gemma4:12b", injection, file, null, default) == "C"
        && emptyRecovery.GenerationCount == 2, "empty output also retries once");

    var exhausted = new FakeRunner();
    exhausted.Responses.Enqueue(new(0, "Thinking...\nfirst", ""));
    exhausted.Responses.Enqueue(new(0, "<think>second", ""));
    try { await new OllamaCli("ollama.exe", exhausted).AnswerAsync("gemma4:12b", injection, file, null, default); throw new Exception("FAIL retry exhaustion"); }
    catch (InvalidOperationException ex) { Check(ex.Message.Contains("思考のみ") && ex.Message.Contains("1回再試行"), "retry exhaustion explains thinking-only failure"); }
    Check(exhausted.GenerationCount == 2, "retry limit is exactly two total generations");

    var failed = new FakeRunner(); failed.Responses.Enqueue(new(1, "", "fixture failure"));
    await Reject(() => new OllamaCli("ollama.exe", failed).AnswerAsync("gemma4:12b", injection, file, null, default), "CLI errors remain errors");
    Check(failed.GenerationCount == 1, "CLI errors do not trigger automatic retry");

    using var cancelled = new CancellationTokenSource();
    var cancelledRun = new FakeRunner { AfterGeneration = _ => cancelled.Cancel() };
    cancelledRun.Responses.Enqueue(new(0, "Thinking...\npartial", ""));
    try { await new OllamaCli("ollama.exe", cancelledRun).AnswerAsync("gemma4:12b", injection, file, null, cancelled.Token); throw new Exception("FAIL cancellation"); }
    catch (OperationCanceledException) { Check(cancelledRun.GenerationCount == 1, "cancelled generation never retries"); }

    using var between = new CancellationTokenSource();
    var betweenRun = new FakeRunner(); betweenRun.Responses.Enqueue(new(0, "", ""));
    try { await new OllamaCli("ollama.exe", betweenRun).AnswerAsync("gemma4:12b", injection, file, null, between.Token,
        new DirectProgress(_ => between.Cancel())); throw new Exception("FAIL cancel before retry"); }
    catch (OperationCanceledException) { Check(betweenRun.GenerationCount == 1, "cancellation between attempts prevents next CLI launch"); }
    var exe = Environment.ProcessPath!;
    var thoughtRun = new FakeRunner();
    thoughtRun.Responses.Enqueue(new(0, "Thinking...\nfirst thought", ""));
    thoughtRun.Responses.Enqueue(new(0, "<think>retry thought", ""));
    var history = new ThinkingHistory();
    await Reject(() => new OllamaCli("ollama.exe", thoughtRun).AnswerAsync("gemma4:12b", "answer", file, null, default, thinking: history), "thinking-only still fails after retry");
    Check(history.Snapshot().SequenceEqual(new[] { new ThinkingUpdate(1, "first thought"), new ThinkingUpdate(2, "retry thought") }), "both attempts survive final-answer failure");
    Check(history.ClipboardFallback(Delivery.Clipboard) == "retry thought" && history.ClipboardFallback(Delivery.Both) == "retry thought" && history.ClipboardFallback(Delivery.MinimalBoth) == "retry thought", "clipboard fallback uses latest nonempty thinking for both clipboard modes");
    Check(history.ClipboardFallback(Delivery.Panel) == "" && new ThinkingHistory().ClipboardFallback(Delivery.Clipboard) == "", "no clipboard fallback for panel-only or missing thoughts");
    Check(history.ClipboardFallback(Delivery.Minimal) == "", "minimal notification does not copy to clipboard");
    var recoverThought = new FakeRunner();
    recoverThought.Responses.Enqueue(new(0, "<think>one", ""));
    recoverThought.Responses.Enqueue(new(0, "<think>two</think>D", ""));
    var recoveredHistory = new ThinkingHistory();
    var recoveredAnswer = await new OllamaCli("ollama.exe", recoverThought).AnswerAsync("gemma4:12b", "answer", file, null, default, thinking: recoveredHistory);
    Check(recoveredAnswer == "D" && recoveredHistory.Snapshot().Length == 2, "copyable final answer excludes all thoughts");
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
    if (args.Contains("--live-multi"))
    {
        foreach (var item in new[] { (Path: file, Text: "731"), (Path: secondFile, Text: "284") })
        {
            using var bitmap = new Bitmap(400, 180);
            using var graphics = Graphics.FromImage(bitmap); graphics.Clear(Color.White);
            using var font = new Font("Segoe UI", 62, FontStyle.Bold);
            graphics.DrawString(item.Text, font, Brushes.Black, new PointF(30, 30));
            bitmap.Save(item.Path, System.Drawing.Imaging.ImageFormat.Png);
        }
        var live = new OllamaCli(OllamaCli.ResolveExecutable(""));
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var answer = await live.AnswerAsync("gemma4:12b", "添付された2枚の画像に書かれた数字を、画像の順番に1行で列挙してください。最終回答には数字のみを書いてください。", new[] { file, secondFile }, null, timeout.Token);
        Console.WriteLine($"LIVE multi: {answer}");
        Check(answer.Contains("731") && answer.Contains("284"), "real gemma4 receives both images in one CLI invocation");
    }
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
    public bool FailStop { get; set; }
    public Queue<CommandResult> Responses { get; } = new();
    public int GenerationCount { get; private set; }
    public Action<int>? AfterGeneration { get; init; }
    public Task<CommandResult> RunAsync(string exe, IReadOnlyList<string> args, string input, IProgress<string>? progress, CancellationToken token)
    {
        Calls.Add((args, input));
        token.ThrowIfCancellationRequested();
        if (args[0] == "stop" && FailStop) return Task.FromResult(new CommandResult(1, "", "stop failed"));
        if (args[0] == "run" && input.Length > 0)
        {
            GenerationCount++;
            var result = Responses.Count > 0 ? Responses.Dequeue() : new CommandResult(0, "<think>test</think>\n42", "");
            progress?.Report(result.Output); AfterGeneration?.Invoke(GenerationCount);
            return Task.FromResult(result);
        }
        string output = args[0] switch { "ls" => "NAME ID SIZE\ngemma4:12b abc 7 GB\nsecond:local def 1 GB\n",
            "show" => Remote ? "Remote URL https://ollama.com\nvision" : "Capabilities\n    vision\n    thinking\n",
            _ => input.Length == 0 ? "" : "<think>test</think>\n42" };
        return Task.FromResult(new CommandResult(0, output, ""));
    }
}
