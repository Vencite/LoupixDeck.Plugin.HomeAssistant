using System.Collections.Concurrent;

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
        return Cache.GetOrAdd(name, buffer.ToArray());
    }
}
