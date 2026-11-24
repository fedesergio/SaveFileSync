using System.Windows.Media.Imaging;

namespace SaveFileSync.App;

/// <summary>Loads cached covers as small, frozen bitmaps so many of them stay cheap in memory.</summary>
internal static class CoverImage
{
    /// <summary>Pixel width covers are decoded at (a tile is shown at about this size).</summary>
    public const int TileWidth = 180;

    /// <summary>Null when the file is missing or is not an image Windows can decode (e.g. WebP without its codec).</summary>
    public static BitmapSource? Load(string? path, int decodeWidth)
    {
        if (path is null)
        {
            return null;
        }

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad; // reads it fully, so the file is not locked afterwards
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bitmap.DecodePixelWidth = decodeWidth;
            bitmap.UriSource = new Uri(path);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception)
        {
            // The imaging stack throws a variety of exception types for unreadable files; a missing cover is harmless.
            return null;
        }
    }
}
