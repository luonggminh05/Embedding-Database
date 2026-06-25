using SkiaSharp;

namespace RagApi.Services.Ingestion;

internal static class ImageUtilities
{
    public static bool TryGetDimensions(byte[] imageBytes, out int width, out int height)
    {
        width = 0;
        height = 0;

        using var bitmap = SKBitmap.Decode(imageBytes);
        if (bitmap is null)
        {
            return false;
        }

        width = bitmap.Width;
        height = bitmap.Height;
        return width > 0 && height > 0;
    }

    public static byte[] CompressForVision(byte[] imageBytes, int maxWidth = 2560, int maxHeight = 1440, int quality = 95)
    {
        using var source = SKBitmap.Decode(imageBytes);
        if (source is null)
        {
            return imageBytes;
        }

        var scale = Math.Min(1.0, Math.Min((double)maxWidth / source.Width, (double)maxHeight / source.Height));
        var targetWidth = Math.Max(1, (int)Math.Round(source.Width * scale));
        var targetHeight = Math.Max(1, (int)Math.Round(source.Height * scale));

        using var resized = source.Resize(new SKImageInfo(targetWidth, targetHeight), SKFilterQuality.Medium) ?? source.Copy();
        using var image = SKImage.FromBitmap(resized);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, quality);
        return data?.ToArray() ?? imageBytes;
    }
}
