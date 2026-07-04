using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VideoSyncPrototype.Windows;

public readonly record struct SyncPayload(
    [property: JsonPropertyName("v")] string VideoId,
    [property: JsonPropertyName("s")] long StartUnixSeconds,
    [property: JsonPropertyName("o")] double OffsetSeconds,
    [property: JsonPropertyName("r")] double PlaybackRate,
    [property: JsonPropertyName("sc")] SyncScreen? Screen = null,
    [property: JsonPropertyName("a")] SyncAudio? Audio = null,
    [property: JsonPropertyName("op")] SyncOptions? Options = null)
{
    public double GetCurrentVideoSeconds()
    {
        var elapsed = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - this.StartUnixSeconds;
        return this.OffsetSeconds + (elapsed * this.PlaybackRate);
    }
}

public readonly record struct SyncScreen(
    [property: JsonPropertyName("e")] bool Enabled,
    [property: JsonPropertyName("x")] float X,
    [property: JsonPropertyName("y")] float Y,
    [property: JsonPropertyName("z")] float Z,
    [property: JsonPropertyName("t")] float Rotation,
    [property: JsonPropertyName("w")] float Width,
    [property: JsonPropertyName("d")] float Distance,
    [property: JsonPropertyName("h")] float HeightOffset,
    [property: JsonPropertyName("c")] bool ActorOcclusion,
    [property: JsonPropertyName("p")] float OcclusionPadding);

public readonly record struct SyncAudio(
    [property: JsonPropertyName("v")] float Volume,
    [property: JsonPropertyName("m")] bool Muted,
    [property: JsonPropertyName("s")] bool Spatial,
    [property: JsonPropertyName("r")] float Range);

public readonly record struct SyncOptions(
    [property: JsonPropertyName("f")] bool VideoFullscreen,
    [property: JsonPropertyName("b")] bool AdBlock,
    [property: JsonPropertyName("h")] bool HideBrowser);

public static class SyncCode
{
    private const string Prefix = "VS2:";
    private const string LegacyJsonPrefix = "F14YT1-";
    private const byte BinaryVersion = 2;

    public static string Encode(SyncPayload payload)
    {
        return Prefix + Base64UrlEncode(EncodeBinary(payload));
    }

    public static SyncPayload Decode(string code)
    {
        code = code.Trim();
        if (code.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return DecodeBinary(Base64UrlDecode(code[Prefix.Length..]));
        }

        if (code.StartsWith(LegacyJsonPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var bytes = Base64UrlDecode(code[LegacyJsonPrefix.Length..]);
            return JsonSerializer.Deserialize<SyncPayload>(bytes);
        }

        throw new FormatException($"Code must start with {Prefix} (or the older {LegacyJsonPrefix} format).");
    }

    /// <summary>
    /// Compact binary layout so codes fit comfortably in one chat line:
    /// version, two flag bytes (nullable sections + all booleans), length-prefixed
    /// video id, uint32 start time, float offset, then quantized screen and audio
    /// sections (0.1-yalm sizes, 0.05-yalm height, milliradian rotation, integer
    /// volume percent). Around 43 bytes -> ~58 base64 characters.
    /// </summary>
    private static byte[] EncodeBinary(SyncPayload payload)
    {
        using var stream = new MemoryStream(64);
        using var writer = new BinaryWriter(stream);

        writer.Write(BinaryVersion);

        var screen = payload.Screen.GetValueOrDefault();
        var audio = payload.Audio.GetValueOrDefault();
        var options = payload.Options.GetValueOrDefault();

        var flags1 = 0;
        if (payload.Screen.HasValue) flags1 |= 1 << 0;
        if (payload.Audio.HasValue) flags1 |= 1 << 1;
        if (payload.Options.HasValue) flags1 |= 1 << 2;
        if (payload.PlaybackRate == 0) flags1 |= 1 << 3;
        if (screen.Enabled) flags1 |= 1 << 4;
        if (screen.ActorOcclusion) flags1 |= 1 << 5;
        if (audio.Muted) flags1 |= 1 << 6;
        if (audio.Spatial) flags1 |= 1 << 7;
        writer.Write((byte)flags1);

        var flags2 = 0;
        if (options.VideoFullscreen) flags2 |= 1 << 0;
        if (options.AdBlock) flags2 |= 1 << 1;
        if (options.HideBrowser) flags2 |= 1 << 2;
        writer.Write((byte)flags2);

        var videoIdBytes = Encoding.ASCII.GetBytes(payload.VideoId);
        if (videoIdBytes.Length > 64)
        {
            throw new FormatException("Video id is too long to encode.");
        }

        writer.Write((byte)videoIdBytes.Length);
        writer.Write(videoIdBytes);

        writer.Write((uint)Math.Clamp(payload.StartUnixSeconds, 0, uint.MaxValue));
        writer.Write((float)payload.OffsetSeconds);

        if (payload.Screen.HasValue)
        {
            writer.Write(screen.X);
            writer.Write(screen.Y);
            writer.Write(screen.Z);
            writer.Write((short)Math.Clamp(MathF.Round(screen.Rotation * 1000f), short.MinValue, short.MaxValue));
            writer.Write((byte)Math.Clamp(MathF.Round(screen.Width * 10f), 0f, 255f));
            writer.Write((byte)Math.Clamp(MathF.Round(screen.Distance * 10f), 0f, 255f));
            writer.Write((byte)Math.Clamp(MathF.Round(screen.HeightOffset * 20f), 0f, 255f));
            writer.Write((byte)Math.Clamp(MathF.Round(screen.OcclusionPadding), 0f, 255f));
        }

        if (payload.Audio.HasValue)
        {
            writer.Write((byte)Math.Clamp(MathF.Round(audio.Volume * 100f), 0f, 100f));
            writer.Write((byte)Math.Clamp(MathF.Round(audio.Range), 0f, 255f));
        }

        writer.Flush();
        return stream.ToArray();
    }

    private static SyncPayload DecodeBinary(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using var reader = new BinaryReader(stream);

        var version = reader.ReadByte();
        if (version != BinaryVersion)
        {
            throw new FormatException($"Unsupported sync code version {version}.");
        }

        var flags1 = reader.ReadByte();
        var flags2 = reader.ReadByte();
        var hasScreen = (flags1 & (1 << 0)) != 0;
        var hasAudio = (flags1 & (1 << 1)) != 0;
        var hasOptions = (flags1 & (1 << 2)) != 0;
        var paused = (flags1 & (1 << 3)) != 0;

        var videoIdLength = reader.ReadByte();
        var videoId = Encoding.ASCII.GetString(reader.ReadBytes(videoIdLength));
        var startUnixSeconds = reader.ReadUInt32();
        var offsetSeconds = reader.ReadSingle();

        SyncScreen? screen = null;
        if (hasScreen)
        {
            screen = new SyncScreen(
                (flags1 & (1 << 4)) != 0,
                reader.ReadSingle(),
                reader.ReadSingle(),
                reader.ReadSingle(),
                reader.ReadInt16() / 1000f,
                reader.ReadByte() / 10f,
                reader.ReadByte() / 10f,
                reader.ReadByte() / 20f,
                (flags1 & (1 << 5)) != 0,
                reader.ReadByte());
        }

        SyncAudio? audio = null;
        if (hasAudio)
        {
            audio = new SyncAudio(
                reader.ReadByte() / 100f,
                (flags1 & (1 << 6)) != 0,
                (flags1 & (1 << 7)) != 0,
                reader.ReadByte());
        }

        SyncOptions? optionsValue = null;
        if (hasOptions)
        {
            optionsValue = new SyncOptions(
                (flags2 & (1 << 0)) != 0,
                (flags2 & (1 << 1)) != 0,
                (flags2 & (1 << 2)) != 0);
        }

        return new SyncPayload(
            videoId,
            startUnixSeconds,
            offsetSeconds,
            paused ? 0.0 : 1.0,
            screen,
            audio,
            optionsValue);
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static byte[] Base64UrlDecode(string text)
    {
        text = text.Replace('-', '+').Replace('_', '/');
        text = text.PadRight(text.Length + ((4 - text.Length % 4) % 4), '=');
        return Convert.FromBase64String(text);
    }
}
