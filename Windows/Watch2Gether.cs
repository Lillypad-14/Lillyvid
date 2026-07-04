using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VideoSyncPrototype.Windows;

/// <summary>
/// A full snapshot of the host's in-world screen at the moment a room code is
/// generated. Everything a joiner needs so their TV lands at the exact same
/// position, rotation, size, stretch, and occlusion as the host's — carried
/// inside the room join code itself.
/// </summary>
public readonly record struct RoomScreenLayout(
    bool Enabled,
    float X,
    float Y,
    float Z,
    float Rotation,
    float Width,
    float Height,
    bool LockAspect,
    float Elevation,
    float Push,
    float Distance,
    float HeightOffset,
    bool ActorOcclusion,
    float OcclusionPadding);

public readonly record struct Watch2GetherRoom(
    [property: JsonPropertyName("u")] string RoomUrl,
    [property: JsonIgnore] RoomScreenLayout? Layout = null)
{
    public string NormalizedUrl
    {
        get
        {
            if (!Uri.TryCreate(this.RoomUrl.Trim(), UriKind.Absolute, out var uri))
            {
                return this.RoomUrl.Trim();
            }

            var builder = new UriBuilder(uri)
            {
                Scheme = Uri.UriSchemeHttps,
                Host = "w2g.tv",
                Fragment = string.Empty,
            };

            if (!string.IsNullOrWhiteSpace(Watch2GetherRoomParser.GetQueryValue(uri.Query, "r")))
            {
                builder.Path = "/";
            }
            if (builder.Path.StartsWith("/rooms/", StringComparison.OrdinalIgnoreCase))
            {
                builder.Query = string.Empty;
            }

            return builder.Uri.ToString().TrimEnd('/');
        }
    }
}

public static class Watch2GetherRoomCode
{
    // W2G2 is the current binary format that carries the room URL plus the host's
    // full screen layout. W2G1 (URL-only, base64 or the older JSON blob) still
    // decodes so codes shared before the layout was added keep working.
    private const string Prefix = "W2G2:";
    private const string LegacyPrefix = "W2G1:";
    private const byte BinaryVersion = 1;

    public static string Encode(Watch2GetherRoom room)
    {
        using var stream = new MemoryStream(96);
        using var writer = new BinaryWriter(stream, Encoding.UTF8);

        writer.Write(BinaryVersion);

        var layout = room.Layout.GetValueOrDefault();
        var hasLayout = room.Layout.HasValue;

        var flags = 0;
        if (hasLayout) flags |= 1 << 0;
        if (layout.Enabled) flags |= 1 << 1;
        if (layout.LockAspect) flags |= 1 << 2;
        if (layout.ActorOcclusion) flags |= 1 << 3;
        writer.Write((byte)flags);

        writer.Write(room.NormalizedUrl);

        if (hasLayout)
        {
            // Full floats so the joiner reproduces the host's placement exactly.
            writer.Write(layout.X);
            writer.Write(layout.Y);
            writer.Write(layout.Z);
            writer.Write(layout.Rotation);
            writer.Write(layout.Width);
            writer.Write(layout.Height);
            writer.Write(layout.Elevation);
            writer.Write(layout.Push);
            writer.Write(layout.Distance);
            writer.Write(layout.HeightOffset);
            writer.Write(layout.OcclusionPadding);
        }

        writer.Flush();
        return Prefix + Base64UrlEncode(stream.ToArray());
    }

    public static bool TryDecode(string text, out Watch2GetherRoom room)
    {
        room = default;
        text = text.Trim();

        if (text.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return TryDecodeBinary(text[Prefix.Length..], out room);
        }

        if (text.StartsWith(LegacyPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return TryDecodeLegacy(text[LegacyPrefix.Length..], out room);
        }

        return false;
    }

    private static bool TryDecodeBinary(string encoded, out Watch2GetherRoom room)
    {
        room = default;
        try
        {
            using var stream = new MemoryStream(Base64UrlDecode(encoded));
            using var reader = new BinaryReader(stream, Encoding.UTF8);

            var version = reader.ReadByte();
            if (version != BinaryVersion)
            {
                return false;
            }

            var flags = reader.ReadByte();
            var hasLayout = (flags & (1 << 0)) != 0;
            var url = reader.ReadString();

            RoomScreenLayout? layout = null;
            if (hasLayout)
            {
                layout = new RoomScreenLayout(
                    (flags & (1 << 1)) != 0,
                    reader.ReadSingle(),
                    reader.ReadSingle(),
                    reader.ReadSingle(),
                    reader.ReadSingle(),
                    reader.ReadSingle(),
                    reader.ReadSingle(),
                    (flags & (1 << 2)) != 0,
                    reader.ReadSingle(),
                    reader.ReadSingle(),
                    reader.ReadSingle(),
                    reader.ReadSingle(),
                    (flags & (1 << 3)) != 0,
                    reader.ReadSingle());
            }

            room = new Watch2GetherRoom(url, layout);
            return Watch2GetherRoomParser.IsWatch2GetherRoomUrl(room.RoomUrl);
        }
        catch
        {
            room = default;
            return false;
        }
    }

    private static bool TryDecodeLegacy(string encoded, out Watch2GetherRoom room)
    {
        room = default;
        try
        {
            var bytes = Base64UrlDecode(encoded);
            var decodedText = Encoding.UTF8.GetString(bytes);
            if (decodedText.TrimStart().StartsWith("{", StringComparison.Ordinal))
            {
                // Old W2G1 codes were a {"u":"..."} JSON blob (URL only, no layout).
                // Pull the URL out directly — binding the whole record would trip over
                // the JsonIgnore'd Layout constructor parameter.
                using var document = JsonDocument.Parse(bytes);
                var url = document.RootElement.TryGetProperty("u", out var urlElement)
                    ? urlElement.GetString() ?? string.Empty
                    : string.Empty;
                room = new Watch2GetherRoom(url);
            }
            else
            {
                room = new Watch2GetherRoom(decodedText);
            }

            return Watch2GetherRoomParser.IsWatch2GetherRoomUrl(room.RoomUrl);
        }
        catch
        {
            room = default;
            return false;
        }
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

public static class Watch2GetherRoomParser
{
    public static bool TryParse(string text, out Watch2GetherRoom room)
    {
        room = default;
        if (Watch2GetherRoomCode.TryDecode(text, out room))
        {
            return true;
        }

        foreach (var token in text.Split([' ', '\t', '\r', '\n', '<', '>', '"'], StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = token.TrimEnd('.', ',', ';', ':', ')', ']');
            if (!IsWatch2GetherRoomUrl(trimmed))
            {
                continue;
            }

            room = new Watch2GetherRoom(trimmed);
            return true;
        }

        return false;
    }

    public static bool IsWatch2GetherRoomUrl(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.Scheme is "http" or "https"
            && uri.Host.Equals("w2g.tv", StringComparison.OrdinalIgnoreCase)
            && ((uri.AbsolutePath.StartsWith("/rooms/", StringComparison.OrdinalIgnoreCase)
                    && uri.AbsolutePath.Length > "/rooms/".Length)
                || !string.IsNullOrWhiteSpace(GetQueryValue(uri.Query, "r"))
                || (uri.AbsolutePath.Contains("/room/", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(GetQueryValue(uri.Query, "access_key"))));
    }

    public static string GetQueryValue(string query, string key)
    {
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pieces = part.Split('=', 2);
            if (pieces.Length == 2 && Uri.UnescapeDataString(pieces[0]).Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                return Uri.UnescapeDataString(pieces[1].Replace("+", " "));
            }
        }

        return string.Empty;
    }
}
