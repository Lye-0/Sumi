using System.Windows;
using System.Windows.Media.Imaging;

namespace Sumi;

internal static class Branding
{
    private static readonly Uri IconUri = new("/Sumi;component/Assets/Sumi.ico", UriKind.Relative);
    public static BitmapFrame WindowIcon { get; } = LoadWindowIcon();
    private static BitmapFrame LoadWindowIcon()
    {
        using var stream = Application.GetResourceStream(IconUri)!.Stream;
        var image = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        image.Freeze(); return image;
    }
    public static System.Drawing.Icon CreateTrayIcon()
    {
        using var stream = Application.GetResourceStream(IconUri)!.Stream;
        using var icon = new System.Drawing.Icon(stream, System.Windows.Forms.SystemInformation.SmallIconSize);
        return (System.Drawing.Icon)icon.Clone();
    }
}
