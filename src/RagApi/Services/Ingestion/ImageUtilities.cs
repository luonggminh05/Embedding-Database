using System;
using System.Collections.Generic;
using System.Linq;
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

    public static bool ShouldSkipOcr(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return true;
        }

        var minSide = Math.Min(width, height);
        var maxSide = Math.Max(width, height);
        return width < 20 || height < 20 || minSide * 15 < maxSide;
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

    public static List<byte[]> PrepareOcrVariants(byte[] imageBytes)
    {
        var variants = new List<byte[]>();

        // 1. Baseline upscale
        var baselineBytes = PrepareForOcr(imageBytes);
        variants.Add(baselineBytes);

        using var baselineBitmap = SKBitmap.Decode(baselineBytes);
        if (baselineBitmap is null)
        {
            return variants;
        }

        int width = baselineBitmap.Width;
        int height = baselineBitmap.Height;

        // 2. Padded upscale (e.g. 40px white margin all around)
        const int padding = 40;
        using (var padded = new SKBitmap(width + padding * 2, height + padding * 2, SKColorType.Rgba8888, SKAlphaType.Premul))
        {
            using (var canvas = new SKCanvas(padded))
            using (var paint = new SKPaint())
            {
                canvas.Clear(SKColors.White);
                canvas.DrawBitmap(baselineBitmap, padding, padding, paint);
            }
            using (var image = SKImage.FromBitmap(padded))
            using (var data = image.Encode(SKEncodedImageFormat.Png, 100))
            {
                if (data is not null) variants.Add(data.ToArray());
            }
        }

        // Calculate grayscale luminance and Otsu threshold on baseline
        var grayData = GetGrayscaleLuminance(baselineBitmap);
        int otsuThreshold = CalculateOtsuThreshold(grayData, width, height);

        // 3. Grayscale + Otsu threshold
        var thresholdBytes = ApplyThreshold(baselineBitmap, grayData, otsuThreshold, invert: false);
        if (thresholdBytes.Length > 0) variants.Add(thresholdBytes);

        // 4. Inverted threshold
        var invertedBytes = ApplyThreshold(baselineBitmap, grayData, otsuThreshold, invert: true);
        if (invertedBytes.Length > 0) variants.Add(invertedBytes);

        return variants;
    }

    private static byte[] GetGrayscaleLuminance(SKBitmap bitmap)
    {
        int width = bitmap.Width;
        int height = bitmap.Height;
        var gray = new byte[width * height];
        int index = 0;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                var color = bitmap.GetPixel(x, y);
                // Luminance formula: 0.299R + 0.587G + 0.114B
                double yVal = 0.299 * color.Red + 0.587 * color.Green + 0.114 * color.Blue;
                gray[index++] = (byte)Math.Clamp(Math.Round(yVal), 0, 255);
            }
        }
        return gray;
    }

    private static int CalculateOtsuThreshold(byte[] grayData, int width, int height)
    {
        var histogram = new int[256];
        foreach (var val in grayData)
        {
            histogram[val]++;
        }

        int total = width * height;
        double sum = 0;
        for (int i = 0; i < 256; i++)
        {
            sum += i * histogram[i];
        }

        double sumB = 0;
        int wB = 0;
        int wF = 0;

        double varMax = 0;
        int threshold = 127;

        for (int t = 0; t < 256; t++)
        {
            wB += histogram[t];
            if (wB == 0) continue;

            wF = total - wB;
            if (wF == 0) break;

            sumB += t * histogram[t];

            double mB = sumB / wB;
            double mF = (sum - sumB) / wF;

            // Between-class variance
            double varBetween = (double)wB * wF * (mB - mF) * (mB - mF);

            if (varBetween > varMax)
            {
                varMax = varBetween;
                threshold = t;
            }
        }

        return threshold;
    }

    private static byte[] ApplyThreshold(SKBitmap source, byte[] grayData, int threshold, bool invert)
    {
        int width = source.Width;
        int height = source.Height;
        using var thresholded = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        int index = 0;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                byte g = grayData[index++];
                bool isBelow = g < threshold;
                // If invert is true, below threshold becomes white (255), otherwise black (0)
                byte pixelColor = (byte)(isBelow ^ invert ? 0 : 255);
                thresholded.SetPixel(x, y, new SKColor(pixelColor, pixelColor, pixelColor, 255));
            }
        }

        using var image = SKImage.FromBitmap(thresholded);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data?.ToArray() ?? Array.Empty<byte>();
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
