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

    public static byte[] PrepareForOcr(byte[] imageBytes, int minReadableWidth = 1200, int maxWidth = 3200, int maxHeight = 3200)
    {
        using var source = SKBitmap.Decode(imageBytes);
        if (source is null)
        {
            return imageBytes;
        }

        var upscale = source.Width < minReadableWidth ? (double)minReadableWidth / source.Width : 1.0;
        var maxScale = Math.Min((double)maxWidth / source.Width, (double)maxHeight / source.Height);
        var scale = Math.Max(1.0, Math.Min(upscale, maxScale));
        var targetWidth = Math.Max(1, (int)Math.Round(source.Width * scale));
        var targetHeight = Math.Max(1, (int)Math.Round(source.Height * scale));

        using var normalized = new SKBitmap(new SKImageInfo(targetWidth, targetHeight, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(normalized))
        using (var paint = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true })
        {
            canvas.Clear(SKColors.White);
            canvas.DrawBitmap(source, new SKRect(0, 0, targetWidth, targetHeight), paint);
        }

        using var image = SKImage.FromBitmap(normalized);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data?.ToArray() ?? imageBytes;
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
