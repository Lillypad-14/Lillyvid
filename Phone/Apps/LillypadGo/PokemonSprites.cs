using System.Collections.Concurrent;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures.TextureWraps;
using VideoSyncPrototype.Phone.Core;

namespace VideoSyncPrototype.Phone.Apps.LillypadGo;

// Loads and animates the bundled Pokémon spritesheets (gen5ani frames packed into one
// horizontal PNG per creature). Each sheet is streamed to a texture on first use; the current
// frame is picked from the manifest's per-frame delays so animation is driven purely by time.
internal static class PokemonSprites
{
    private sealed class SheetMeta
    {
        public int Frames { get; set; }
        public int W { get; set; }
        public int H { get; set; }
        public int[] Delays { get; set; } = Array.Empty<int>();

        private int[]? cumulative;
        private int total;

        public int FrameAt(float timeSeconds)
        {
            if (Frames <= 1 || Delays.Length == 0)
            {
                return 0;
            }

            if (cumulative is null)
            {
                cumulative = new int[Delays.Length];
                var acc = 0;
                for (var i = 0; i < Delays.Length; i++)
                {
                    acc += Math.Max(1, Delays[i]);
                    cumulative[i] = acc;
                }

                total = acc;
            }

            var t = (int)(timeSeconds * 1000f) % total;
            for (var i = 0; i < cumulative.Length; i++)
            {
                if (t < cumulative[i])
                {
                    return Math.Min(i, Frames - 1);
                }
            }

            return Frames - 1;
        }
    }

    private sealed class Entry
    {
        public SheetMeta? Front { get; set; }
        public SheetMeta? Back { get; set; }
    }

    // A resolved frame ready to hand to ImDrawList.AddImage.
    public readonly struct Frame
    {
        public Frame(ImTextureID handle, Vector2 uv0, Vector2 uv1, float aspect)
        {
            Handle = handle;
            Uv0 = uv0;
            Uv1 = uv1;
            Aspect = aspect;
        }

        public ImTextureID Handle { get; }
        public Vector2 Uv0 { get; }
        public Vector2 Uv1 { get; }
        public float Aspect { get; } // width / height of a single frame
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private static readonly ConcurrentDictionary<string, IDalamudTextureWrap> ready = new();
    private static readonly ConcurrentDictionary<string, byte> loading = new();
    private static readonly ConcurrentDictionary<string, byte> failed = new();
    private static Dictionary<string, Entry>? manifest;
    private static bool manifestTried;

    private static string BaseDir =>
        Path.Combine(Plugin.PluginInterface.AssemblyLocation.DirectoryName ?? string.Empty, "Assets", "pokemon");

    private static Dictionary<string, Entry>? Manifest()
    {
        if (manifestTried)
        {
            return manifest;
        }

        manifestTried = true;
        try
        {
            var path = Path.Combine(BaseDir, "manifest.json");
            manifest = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(path), JsonOptions);
        }
        catch (Exception exception)
        {
            AepLog.Warning($"[LillypadGo] sprite manifest missing: {exception.Message}");
            manifest = null;
        }

        return manifest;
    }

    public static bool TryGetFrame(string id, bool back, float timeSeconds, out Frame frame)
    {
        frame = default;
        var m = Manifest();
        if (m is null || !m.TryGetValue(id, out var entry))
        {
            return false;
        }

        // Fall back to the other facing if the requested one is absent.
        if (back && entry.Back is null)
        {
            back = false;
        }
        else if (!back && entry.Front is null)
        {
            back = entry.Back is not null;
        }

        var meta = back ? entry.Back : entry.Front;
        if (meta is null || meta.Frames <= 0)
        {
            return false;
        }

        var key = (back ? "b:" : "f:") + id;
        if (!ready.TryGetValue(key, out var tex))
        {
            BeginLoad(key, id, back);
            return false;
        }

        var idx = meta.FrameAt(timeSeconds);
        var u0 = idx / (float)meta.Frames;
        var u1 = (idx + 1) / (float)meta.Frames;
        frame = new Frame(tex.Handle, new Vector2(u0, 0f), new Vector2(u1, 1f), meta.W / (float)Math.Max(1, meta.H));
        return true;
    }

    private static void BeginLoad(string key, string id, bool back)
    {
        if (failed.ContainsKey(key) || !loading.TryAdd(key, 0))
        {
            return;
        }

        _ = LoadAsync(key, id, back);
    }

    private static async Task LoadAsync(string key, string id, bool back)
    {
        try
        {
            var path = Path.Combine(BaseDir, back ? "back" : "front", id + ".png");
            var bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
            var wrap = await Plugin.TextureProvider.CreateFromImageAsync(bytes, path).ConfigureAwait(false);
            if (!ready.TryAdd(key, wrap))
            {
                wrap.Dispose();
            }
        }
        catch (Exception exception)
        {
            failed.TryAdd(key, 0);
            AepLog.Warning($"[LillypadGo] failed to load sprite {key}: {exception.Message}");
        }
        finally
        {
            loading.TryRemove(key, out _);
        }
    }

    public static void Dispose()
    {
        foreach (var wrap in ready.Values)
        {
            wrap.Dispose();
        }

        ready.Clear();
        loading.Clear();
        failed.Clear();
    }
}
