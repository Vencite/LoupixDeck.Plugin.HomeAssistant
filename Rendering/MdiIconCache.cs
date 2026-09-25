using System.Collections.Concurrent;
using System.Text.Json;
using System.Xml.Linq;
using SkiaSharp;

namespace LoupixDeck.Plugin.HomeAssistant.Rendering;

/// <summary>Loads only requested MDI paths; rendering reads cached PNG bytes without network I/O.</summary>
internal static class MdiIconCache
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(6) };
    private static readonly ConcurrentDictionary<string, byte[]> Images = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, byte> Pending = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, DateTimeOffset> RetryAfter = new(StringComparer.Ordinal);
    public static event Action? IconLoaded;

    public static byte[]? Get(string? name)
    {
        string? id = Commands.ButtonDisplayOptions.StripMdiPrefix(name);
        if (id is null || id.Length > 80 || id.Any(c => !(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-')))
            return null;
        if (Images.TryGetValue(id, out byte[]? image)) return image;
        if (RetryAfter.TryGetValue(id, out DateTimeOffset next) && next > DateTimeOffset.UtcNow) return null;
        if (Pending.TryAdd(id, 0)) _ = Task.Run(() => LoadAsync(id));
        return null;
    }

    private static async Task LoadAsync(string id)
    {
        try
        {
            using HttpResponseMessage response = await Http.GetAsync(
                $"https://api.iconify.design/mdi.json?icons={id}", HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > 128_000) return;
            await using Stream stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using JsonDocument json = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
            if (!json.RootElement.GetProperty("icons").TryGetProperty(id, out JsonElement icon) ||
                !icon.TryGetProperty("body", out JsonElement body) || body.GetString() is not { Length: > 0 and < 100_000 } svg)
                return;
            byte[]? image = Rasterize(svg);
            if (image is null) return;
            Images[id] = image;
            IconLoaded?.Invoke();
        }
        catch (Exception) { /* Keep the existing host symbol or domain fallback. */ }
        finally
        {
            RetryAfter[id] = DateTimeOffset.UtcNow.AddMinutes(5);
            Pending.TryRemove(id, out _);
        }
    }

    internal static byte[]? Rasterize(string body)
    {
        XElement root = XElement.Parse("<svg>" + body + "</svg>");
        XElement[] paths = root.Elements().Where(element => element.Name.LocalName == "path").ToArray();
        if (paths.Length == 0 || paths.Length != root.Elements().Count()) return null;
        using SKSurface? surface = SKSurface.Create(new SKImageInfo(96, 96));
        if (surface is null) return null;
        surface.Canvas.Clear(SKColors.Transparent);
        surface.Canvas.Scale(4, 4);
        using var paint = new SKPaint { Color = SKColors.White, IsAntialias = true };
        foreach (XElement element in paths)
        {
            string? data = (string?)element.Attribute("d");
            if (string.IsNullOrWhiteSpace(data)) return null;
            using SKPath? path = SKPath.ParseSvgPathData(data);
            if (path is null) return null;
            surface.Canvas.DrawPath(path, paint);
        }
        using SKImage image = surface.Snapshot();
        using SKData png = image.Encode(SKEncodedImageFormat.Png, 100);
        return png.ToArray();
    }
}
