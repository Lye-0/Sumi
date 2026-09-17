namespace Sumi.Core;

public sealed class ImageStock
{
    public const int MaxImages = 20;
    public const long MaxBytes = 128 * 1024 * 1024;
    private readonly List<byte[]> _images = [];
    public int Count => _images.Count;
    public long Bytes { get; private set; }
    public void Add(byte[] png)
    {
        if (_images.Count >= MaxImages || png.LongLength > MaxBytes - Bytes)
            throw new InvalidOperationException("ストックの上限（20枚・128MB）です。送信するか破棄してください。");
        _images.Add(png); Bytes += png.LongLength;
    }
    public byte[][] Snapshot() => _images.ToArray();
    public void Clear() { _images.Clear(); Bytes = 0; }
}
