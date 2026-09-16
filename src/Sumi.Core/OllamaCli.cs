using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Sumi.Core;

public interface IModelProvider
{
    Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken token);
    Task PrepareAsync(string model, CancellationToken token);
    Task<string> AnswerAsync(string model, string prompt, string imagePath, IProgress<string>? progress, CancellationToken token);
    string Preview(string model);
}

public sealed record CommandResult(int ExitCode, string Output, string Error);

public interface ICommandRunner
{
    Task<CommandResult> RunAsync(string executable, IReadOnlyList<string> arguments, string input,
        IProgress<string>? output, CancellationToken token);
}

public sealed class ProcessRunner : ICommandRunner
{
    public static ProcessStartInfo StartInfo(string exe, IReadOnlyList<string> arguments)
    {
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var arg in arguments) info.ArgumentList.Add(arg);
        // Applies to this child only. Never use an inherited remote Ollama endpoint.
        info.Environment["OLLAMA_HOST"] = "127.0.0.1:11434";
        info.Environment["OLLAMA_NOHISTORY"] = "1";
        info.Environment["NO_COLOR"] = "1";
        return info;
    }

    public async Task<CommandResult> RunAsync(string executable, IReadOnlyList<string> arguments, string input,
        IProgress<string>? output, CancellationToken token)
    {
        using var process = new Process { StartInfo = StartInfo(executable, arguments) };
        token.ThrowIfCancellationRequested();
        process.Start();
        using var registration = token.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        });
        var stdout = PumpAsync(process.StandardOutput, output, () => Kill(process));
        var stderr = PumpAsync(process.StandardError, null, () => Kill(process));
        try
        {
            await process.StandardInput.WriteAsync(input.AsMemory(), token);
            process.StandardInput.Close(); // EOF makes `ollama run MODEL` non-interactive, including preloading.
            await process.WaitForExitAsync(token);
            var result = new CommandResult(process.ExitCode, await stdout, await stderr);
            token.ThrowIfCancellationRequested();
            return result;
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(stdout, stderr);
        }
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }
    private static async Task<string> PumpAsync(StreamReader reader, IProgress<string>? progress, Action overflow)
    {
        var text = new StringBuilder();
        var buffer = new char[2048];
        int count;
        while ((count = await reader.ReadAsync(buffer)) > 0)
        {
            text.Append(buffer, 0, count);
            // Bound diagnostics/output; an accidental enormous response must not exhaust memory.
            if (text.Length > 2_000_000) { overflow(); throw new InvalidDataException("CLI出力が上限を超えました。"); }
            progress?.Report(text.ToString());
        }
        return text.ToString();
    }
}

public sealed partial class OllamaCli(string executable, ICommandRunner? runner = null) : IModelProvider
{
    private readonly ICommandRunner _runner = runner ?? new ProcessRunner();
    public string Preview(string model) => $"ollama run {model}";
    public static string ResolveExecutable(string custom)
    {
        if (!string.IsNullOrWhiteSpace(custom))
        {
            var full = Path.GetFullPath(custom.Trim().Trim('"'));
            if (!File.Exists(full) || !full.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                throw new FileNotFoundException("Ollamaの実行ファイルを指定してください。");
            return full;
        }
        var candidates = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => Path.Combine(p.Trim('"'), "ollama.exe"))
            .Concat([Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Ollama", "ollama.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Ollama", "ollama.exe")]);
        return candidates.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException("Ollamaが見つかりません。詳細設定でollama.exeを指定してください。");
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken token)
    {
        var result = await RunAsync(["ls"], "", null, token);
        return ParseModels(result.Output);
    }
    public static IReadOnlyList<string> ParseModels(string output) => Clean(output).Split('\n')
        .Select(l => l.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        .Where(p => p.Length >= 2 && p[0] != "NAME" && !p[0].Contains(":cloud", StringComparison.OrdinalIgnoreCase)
            && !p[0].EndsWith("-cloud", StringComparison.OrdinalIgnoreCase))
        .Select(p => p[0]).Distinct().ToArray();

    private async Task ValidateAsync(string model, CancellationToken token)
    {
        if (!(await ListModelsAsync(token)).Contains(model))
            throw new InvalidOperationException("導入済みのローカルモデルを選択してください。Sumiからモデルのダウンロードは行いません。");
        var result = await RunAsync(["show", model], "", null, token);
        var info = Clean(result.Output);
        if (info.Contains("Remote URL", StringComparison.OrdinalIgnoreCase)
            || info.Contains("Remote model", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("初版はローカルモデルのみ対応しています。");
        if (!Regex.IsMatch(info, @"(?m)^\s*vision\s*$"))
            throw new InvalidOperationException("このモデルの画像入力対応を確認できません。vision対応モデルを選んでください。");
    }
    public async Task PrepareAsync(string model, CancellationToken token)
    {
        await ValidateAsync(model, token);
        await RunAsync(["run", model], "", null, token);
    }
    public async Task<string> AnswerAsync(string model, string prompt, string imagePath, IProgress<string>? progress, CancellationToken token)
    {
        if (!File.Exists(imagePath)) throw new FileNotFoundException("撮影画像が見つかりません。", imagePath);
        await ValidateAsync(model, token);
        // Pass text as stdin, never shell code or additional command-line arguments.
        var adapter = progress == null ? null : new InlineProgress(s => progress.Report(AnswerText(s)));
        var result = await RunAsync(["run", model], $"{Path.GetFullPath(imagePath)}\n{prompt}", adapter, token);
        var answer = AnswerText(result.Output).Trim();
        if (answer.Length == 0) throw new InvalidOperationException("モデルから回答本文を取得できませんでした。");
        return answer;
    }
    private async Task<CommandResult> RunAsync(string[] args, string input, IProgress<string>? progress, CancellationToken token)
    {
        var result = await _runner.RunAsync(executable, args, input, progress, token);
        if (result.ExitCode != 0)
        {
            var error = Clean(result.Error).Trim();
            if (error.Length > 1500) error = error[^1500..];
            throw new InvalidOperationException($"Ollama CLIが失敗しました（{result.ExitCode}）。\n{error}");
        }
        return result;
    }
    public static string Clean(string text) => Ansi().Replace(text, "").Replace("\r", "");
    public static string AnswerText(string raw)
    {
        var text = Clean(raw).TrimStart();
        const string opening = "Thinking...";
        const string closing = "...done thinking.";
        if (text.Length == 0 || opening.StartsWith(text, StringComparison.Ordinal)
            || "<think>".StartsWith(text, StringComparison.Ordinal)) return "";
        if (text.StartsWith(opening, StringComparison.Ordinal))
        {
            var end = text.IndexOf(closing, StringComparison.Ordinal);
            return end < 0 ? "" : text[(end + closing.Length)..].TrimStart();
        }
        if (text.StartsWith("<think>", StringComparison.Ordinal))
        {
            var end = text.IndexOf("</think>", StringComparison.Ordinal);
            return end < 0 ? "" : text[(end + 8)..].TrimStart();
        }
        return text;
    }
    [GeneratedRegex(@"\x1B(?:\[[0-?]*[ -/]*[@-~]|\][^\x07]*(?:\x07))")]
    private static partial Regex Ansi();
    private sealed class InlineProgress(Action<string> action) : IProgress<string>
    { public void Report(string value) => action(value); }
}
