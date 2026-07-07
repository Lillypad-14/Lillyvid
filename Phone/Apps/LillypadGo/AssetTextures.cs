using System.Collections.Concurrent;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures.TextureWraps;
using VideoSyncPrototype.Phone.Core;

namespace VideoSyncPrototype.Phone.Apps.LillypadGo;

// Streams simple single-frame PNGs bundled under Assets/pokemon (item icons, gym badges) as
// textures, keyed by their relative path. Async + cached, mirroring BiomeBgTextures; callers fall
// back to a drawn placeholder until a sprite is ready.
internal static class AssetTextures
{
    private readonly struct Loaded
    {
        public Loaded(ImTextureID handle, float aspect)
        {
            Handle = handle;
            Aspect = aspect;
        }

        public ImTextureID Handle { get; }
        public float Aspect { get; }
    }

    private static readonly ConcurrentDictionary<string, Loaded> ready = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, byte> loading = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, byte> failed = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentBag<IDalamudTextureWrap> textures = new();

    private static string BaseDir =>
        Path.Combine(Plugin.PluginInterface.AssemblyLocation.DirectoryName ?? string.Empty, "Assets", "pokemon");

    // relativePath is like "items/pokeball.png" or "badges/Water.png".
    public static bool TryGet(string relativePath, out ImTextureID handle, out float aspect)
    {
        if (ready.TryGetValue(relativePath, out var loaded))
        {
            handle = loaded.Handle;
            aspect = loaded.Aspect;
            return true;
        }

        handle = default;
        aspect = 1f;
        if (failed.ContainsKey(relativePath) || !loading.TryAdd(relativePath, 0))
        {
            return false;
        }

        _ = LoadAsync(relativePath);
        return false;
    }

    private static async Task LoadAsync(string relativePath)
    {
        try
        {
            var path = Path.Combine(BaseDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
            var bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
            var wrap = await Plugin.TextureProvider.CreateFromImageAsync(bytes, path).ConfigureAwait(false);
            var aspect = wrap.Height > 0 ? wrap.Width / (float)wrap.Height : 1f;
            if (ready.TryAdd(relativePath, new Loaded(wrap.Handle, aspect)))
            {
                textures.Add(wrap);
            }
            else
            {
                wrap.Dispose();
            }
        }
        catch (Exception exception)
        {
            failed.TryAdd(relativePath, 0);
            AepLog.Warning($"[LillypadGo] failed to load asset {relativePath}: {exception.Message}");
        }
        finally
        {
            loading.TryRemove(relativePath, out _);
        }
    }

    public static void Dispose()
    {
        foreach (var wrap in textures)
        {
            wrap.Dispose();
        }

        textures.Clear();
        ready.Clear();
        loading.Clear();
        failed.Clear();
    }
}
