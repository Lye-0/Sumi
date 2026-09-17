namespace Sumi.Core;

public sealed record TrayMenuState
{
    public bool Ready { get; init; }
    public bool Busy { get; init; }
    public bool Preparing { get; init; }
    public bool Capturing { get; init; }
    public bool Refreshing { get; init; }
    public bool Exiting { get; init; }
    public bool PreparedBefore { get; init; }
    public bool HasAnswer { get; init; }
    public int StockCount { get; init; }
    public string Model { get; init; } = "";
    public string SendKey { get; init; } = "";
    public string StockKey { get; init; } = "";
    public string Phase => Exiting ? "終了中" : Capturing ? "撮影中" : Preparing ? "モデル準備中"
        : Busy ? "生成中" : Refreshing ? "モデル一覧を取得中" : Ready ? "待機中" : PreparedBefore ? "一時停止中" : "準備前";
    public string ToggleLabel => Busy ? (Capturing ? "撮影をキャンセル" : Preparing ? "準備をキャンセル" : "生成をキャンセル")
        : Ready ? "一時停止" : PreparedBefore ? "再開" : "モデルを準備して開始";
    public bool CanCapture => Ready && !Busy && !Exiting;
    public bool CanStock => CanCapture && StockCount < ImageStock.MaxImages;
    public bool CanClear => StockCount > 0 && !Busy && !Exiting;
    public bool CanToggle => !Refreshing && !Exiting;
}
