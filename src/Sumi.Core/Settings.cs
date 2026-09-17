using System.Text.Json;

namespace Sumi.Core;

public enum Delivery { Panel = 0, Clipboard = 1, Both = 2, Minimal = 3, MinimalBoth = 4 }
public enum Theme { Dark, Light }

public sealed record Settings
{
    public string Prompt { get; init; } = "画像の内容を日本語で簡潔に説明してください。";
    public string Model { get; init; } = "gemma4:12b";
    public string OllamaPath { get; init; } = "";
    public string Hotkey { get; init; } = "Ctrl+Alt+S";
    public Delivery Delivery { get; init; } = Delivery.Panel;
    public Theme Theme { get; init; } = Theme.Dark;
    public bool Glass { get; init; } = true;
    public bool SaveImages { get; init; }
    public bool CopyImages { get; init; }
    public bool ShowThinking { get; init; }
    public string ImageDirectory { get; init; } = "";
    public int DisplaySeconds { get; init; } = 12;
    public int TimeoutSeconds { get; init; } = 300;
}

public sealed class SettingsStore(string root)
{
    public string Root { get; } = Path.GetFullPath(root);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public Settings Load()
    {
        var path = Path.Combine(Root, "settings.json");
        if (!File.Exists(path)) return new();
        var value = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), Json)
            ?? throw new InvalidDataException("設定ファイルを読み取れません。");
        if (value.Prompt == null || value.Model == null || value.Hotkey == null || value.OllamaPath == null || value.ImageDirectory == null
            || !Enum.IsDefined(value.Theme) || !Enum.IsDefined(value.Delivery)
            || value.DisplaySeconds is < 3 or > 120 || value.TimeoutSeconds is < 30 or > 1800)
            throw new InvalidDataException("設定値が範囲外です。設定ファイルを確認してください。");
        return value;
    }
    public void Save(Settings value)
    {
        Directory.CreateDirectory(Root);
        var path = Path.Combine(Root, "settings.json");
        var temp = path + ".new";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, Json));
        File.Move(temp, path, true);
    }
}

public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public static PixelRect Between(int x1, int y1, int x2, int y2) =>
        new(Math.Min(x1, x2), Math.Min(y1, y2), Math.Abs(x2 - x1), Math.Abs(y2 - y1));
    public PixelRect Intersect(PixelRect other)
    {
        int x = Math.Max(X, other.X), y = Math.Max(Y, other.Y);
        return new(x, y, Math.Max(0, Math.Min(X + Width, other.X + other.Width) - x),
            Math.Max(0, Math.Min(Y + Height, other.Y + other.Height) - y));
    }
}

public readonly record struct Hotkey(uint Modifiers, uint Key)
{
    public static Hotkey Parse(string input)
    {
        var tokens = input.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        uint mods = 0;
        if (tokens.Length < 2) throw new FormatException("Ctrl / Alt / Shift と英数字を組み合わせてください。");
        foreach (var part in tokens[..^1])
            mods |= part.ToUpperInvariant() switch { "CTRL" => 2u, "ALT" => 1u, "SHIFT" => 4u,
                _ => throw new FormatException("使用できる修飾キーは Ctrl / Alt / Shift です。") };
        var key = tokens[^1].ToUpperInvariant();
        if (key.Length != 1 || !char.IsAsciiLetterOrDigit(key[0]))
            throw new FormatException("最後のキーは A–Z または 0–9 を指定してください。");
        return new(mods, key[0]);
    }
}
