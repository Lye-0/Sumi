namespace Sumi.Core;

public sealed record ThinkingUpdate(int Attempt, string Text);
public sealed class MissingAnswerException(string message) : InvalidOperationException(message);

// CLI callbacks run off the UI thread. The UI samples snapshots at a bounded rate.
public sealed class ThinkingHistory : IProgress<ThinkingUpdate>
{
    private readonly object _gate = new();
    private readonly SortedDictionary<int, string> _attempts = new();
    public void Report(ThinkingUpdate value)
    {
        if (value.Text.Length == 0) return;
        lock (_gate) _attempts[value.Attempt] = value.Text;
    }
    public ThinkingUpdate[] Snapshot()
    {
        lock (_gate) return _attempts.Select(p => new ThinkingUpdate(p.Key, p.Value)).ToArray();
    }
    public string ClipboardFallback(Delivery delivery) => delivery is not (Delivery.Clipboard or Delivery.Both) ? ""
        : Snapshot().LastOrDefault(p => !string.IsNullOrWhiteSpace(p.Text))?.Text ?? "";
}
