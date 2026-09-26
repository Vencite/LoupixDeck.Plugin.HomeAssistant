using System.Collections.Concurrent;
using SkiaSharp;

namespace LoupixDeck.Plugin.HomeAssistant.Commands;

internal static class FolderIcons
{
    private static readonly ConcurrentDictionary<string, byte[]> Cache = new(StringComparer.Ordinal);
    private static readonly SKTypeface CaptionFont = LoadCaptionFont();

    private static SKTypeface LoadCaptionFont()
    {
        using Stream stream = typeof(FolderIcons).Assembly.GetManifestResourceStream(
            "LoupixDeck.Plugin.HomeAssistant.Assets.Fonts.DejaVuSansMono.ttf")
            ?? throw new InvalidOperationException("Embedded folder caption font is missing.");
        return SKTypeface.FromStream(stream)
            ?? throw new InvalidOperationException("Embedded folder caption font cannot be loaded.");
    }

    public static byte[]? Get(string name, string label, int textSize = 12)
    {
        string key = $"{name}|{label}|{textSize}";
        if (Cache.TryGetValue(key, out byte[]? image)) return image;
        if (Cache.Count > 256) Cache.Clear();
        using Stream? stream = typeof(FolderIcons).Assembly.GetManifestResourceStream(
            $"LoupixDeck.Plugin.HomeAssistant.Assets.FolderIcons.{name}.png");
        if (stream is null) return null;
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        byte[] original = buffer.ToArray();
        using SKBitmap? source = SKBitmap.Decode(original);
        if (source is null) return Cache.GetOrAdd(key, original);
        int left = source.Width, top = source.Height, right = -1, bottom = -1;
        for (int y = 0; y < source.Height; y++)
            for (int x = 0; x < source.Width; x++)
                if (source.GetPixel(x, y).Alpha > 16)
                {
                    left = Math.Min(left, x); top = Math.Min(top, y);
                    right = Math.Max(right, x); bottom = Math.Max(bottom, y);
                }
        if (right < left) return Cache.GetOrAdd(key, original);
        using SKSurface? surface = SKSurface.Create(new SKImageInfo(90, 90));
        if (surface is null) return Cache.GetOrAdd(key, original);
        surface.Canvas.Clear(SKColors.Transparent);
        float scale = Math.Min(34f / (right - left + 1), 34f / (bottom - top + 1));
        float drawWidth = (right - left + 1) * scale, drawHeight = (bottom - top + 1) * scale;
        surface.Canvas.DrawBitmap(source, new SKRect(left, top, right + 1, bottom + 1),
            new SKRect(45 - drawWidth / 2, 34 - drawHeight / 2,
                45 + drawWidth / 2, 34 + drawHeight / 2), SKSamplingOptions.Default);
        using var font = new SKFont(CaptionFont, textSize) { Edging = SKFontEdging.Antialias };
        float width = font.MeasureText(label);
        if (width > 82) font.Size *= 82 / width;
        using var paint = new SKPaint { Color = SKColors.White, IsAntialias = true };
        surface.Canvas.DrawText(label, 45, 82, SKTextAlign.Center, font, paint);
        using SKImage scaled = surface.Snapshot();
        using SKData png = scaled.Encode(SKEncodedImageFormat.Png, 100);
        return Cache.GetOrAdd(key, png.ToArray());
    }
}
