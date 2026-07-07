using System.Collections.Concurrent;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures.TextureWraps;
using VideoSyncPrototype.Phone.Core;

namespace VideoSyncPrototype.Phone.Apps.LillypadGo;

// Loads the bundled Pokémon Showdown fx sprites (single static PNGs) used by move animations.
// Each is streamed to a texture on first use; the animation code tweens/rotates the quad itself.
internal static class MoveFxSprites
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

    private static readonly ConcurrentDictionary<string, Loaded> ready = new();
    private static readonly ConcurrentDictionary<string, byte> loading = new();
    private static readonly ConcurrentDictionary<string, byte> failed = new();

    private static string BaseDir =>
        Path.Combine(Plugin.PluginInterface.AssemblyLocation.DirectoryName ?? string.Empty, "Assets", "pokemon", "fx");

    public static bool TryGet(string name, out ImTextureID handle, out float aspect)
    {
        if (ready.TryGetValue(name, out var loaded))
        {
            handle = loaded.Handle;
            aspect = loaded.Aspect;
            return true;
        }

        handle = default;
        aspect = 1f;
        if (failed.ContainsKey(name) || !loading.TryAdd(name, 0))
        {
            return false;
        }

        _ = LoadAsync(name);
        return false;
    }

    private static async Task LoadAsync(string name)
    {
        try
        {
            var path = Path.Combine(BaseDir, name.Contains('.') ? name : name + ".png");
            var bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
            var wrap = await Plugin.TextureProvider.CreateFromImageAsync(bytes, path).ConfigureAwait(false);
            var aspect = wrap.Height > 0 ? wrap.Width / (float)wrap.Height : 1f;
            if (!ready.TryAdd(name, new Loaded(wrap.Handle, aspect)))
            {
                wrap.Dispose();
            }
            else
            {
                textures.Add(wrap);
            }
        }
        catch (Exception exception)
        {
            failed.TryAdd(name, 0);
            AepLog.Warning($"[LillypadGo] failed to load fx sprite {name}: {exception.Message}");
        }
        finally
        {
            loading.TryRemove(name, out _);
        }
    }

    // Keeps the wraps alive (we hand out raw handles) and lets us dispose on teardown.
    private static readonly ConcurrentBag<IDalamudTextureWrap> textures = new();

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
