using System.Collections.Concurrent;
using SkiaSharp;

namespace LoupixDeck.Plugin.HomeAssistant.Commands;

internal static class FolderIcons
{
    private static readonly ConcurrentDictionary<string, byte[]> Cache = new(StringComparer.Ordinal);

    public static byte[]? Get(string name)
    {
        if (Cache.TryGetValue(name, out byte[]? image)) return image;
        using Stream? stream = typeof(FolderIcons).Assembly.GetManifestResourceStream(
            $"LoupixDeck.Plugin.HomeAssistant.Assets.FolderIcons.{name}.png");
        if (stream is null) return null;
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        byte[] original = buffer.ToArray();
        using SKBitmap? source = SKBitmap.Decode(original);
        if (source is null) return Cache.GetOrAdd(name, original);
        int left = source.Width, top = source.Height, right = -1, bottom = -1;
        for (int y = 0; y < source.Height; y++)
            for (int x = 0; x < source.Width; x++)
                if (source.GetPixel(x, y).Alpha > 16)
                {
                    left = Math.Min(left, x); top = Math.Min(top, y);
                    right = Math.Max(right, x); bottom = Math.Max(bottom, y);
                }
        if (right < left) return Cache.GetOrAdd(name, original);
        using SKSurface? surface = SKSurface.Create(new SKImageInfo(90, 90));
        if (surface is null) return Cache.GetOrAdd(name, original);
        surface.Canvas.Clear(SKColors.Transparent);
        float scale = Math.Min(34f / (right - left + 1), 34f / (bottom - top + 1));
        float drawWidth = (right - left + 1) * scale, drawHeight = (bottom - top + 1) * scale;
        surface.Canvas.DrawBitmap(source, new SKRect(left, top, right + 1, bottom + 1),
            new SKRect(45 - drawWidth / 2, 21 - drawHeight / 2,
                45 + drawWidth / 2, 21 + drawHeight / 2), SKSamplingOptions.Default);
        using SKImage scaled = surface.Snapshot();
        using SKData png = scaled.Encode(SKEncodedImageFormat.Png, 100);
        return Cache.GetOrAdd(name, png.ToArray());
    }
}
