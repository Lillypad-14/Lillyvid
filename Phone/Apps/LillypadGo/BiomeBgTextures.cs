using System.Collections.Concurrent;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures.TextureWraps;
using VideoSyncPrototype.Phone.Core;

namespace VideoSyncPrototype.Phone.Apps.LillypadGo;

// Streams the bundled per-biome arena backgrounds (Pokémon Showdown bg-*.png) as textures,
// keyed by Biome. Consumed by BiomeBackdrop, which falls back to the procedural scene until ready.
internal static class BiomeBgTextures
{
    private readonly struct Loaded
    {
        public Loaded(ImTextureID handle, float aspect)
        {
            Handle = handle;
            Aspect = aspect;
        }

        public ImTextureID Handle { get; }
        public float Aspect { get; } // width / height
    }

    private static readonly ConcurrentDictionary<Biome, Loaded> ready = new();
    private static readonly ConcurrentDictionary<Biome, byte> loading = new();
    private static readonly ConcurrentDictionary<Biome, byte> failed = new();
    private static readonly ConcurrentBag<IDalamudTextureWrap> textures = new();

    private static string BaseDir =>
        Path.Combine(Plugin.PluginInterface.AssemblyLocation.DirectoryName ?? string.Empty, "Assets", "pokemon", "bg");

    public static bool TryGet(Biome biome, out ImTextureID handle, out float aspect)
    {
        if (ready.TryGetValue(biome, out var loaded))
        {
            handle = loaded.Handle;
            aspect = loaded.Aspect;
            return true;
        }

        handle = default;
        aspect = 1.5f;
        if (failed.ContainsKey(biome) || !loading.TryAdd(biome, 0))
        {
            return false;
        }

        _ = LoadAsync(biome);
        return false;
    }

    private static async Task LoadAsync(Biome biome)
    {
        try
        {
            var path = Path.Combine(BaseDir, biome.ToString().ToLowerInvariant() + ".png");
            var bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
            var wrap = await Plugin.TextureProvider.CreateFromImageAsync(bytes, path).ConfigureAwait(false);
            var aspect = wrap.Height > 0 ? wrap.Width / (float)wrap.Height : 1.5f;
            if (ready.TryAdd(biome, new Loaded(wrap.Handle, aspect)))
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
            failed.TryAdd(biome, 0);
            AepLog.Warning($"[LillypadGo] failed to load biome bg {biome}: {exception.Message}");
        }
        finally
        {
            loading.TryRemove(biome, out _);
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
