using System.IO;
using System.Text;
using System.Text.Json.Serialization;

namespace VrcdnManager.Services;

public record VrcxPhotoAuthor(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("displayName")] string DisplayName);

public record VrcxPhotoWorld(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("instanceId")] string? InstanceId);

public record VrcxPhotoMetadata(
    [property: JsonPropertyName("author")] VrcxPhotoAuthor? Author,
    [property: JsonPropertyName("world")] VrcxPhotoWorld? World,
    [property: JsonPropertyName("players")] List<VrcxPhotoAuthor>? Players);

/// <summary>
/// VRCX embeds a JSON blob (author, world/instance, full player list with id+displayName)
/// directly into VRChat screenshots as a PNG text chunk, at capture time - no need to
/// correlate against VRCX's own database by timestamp, the photo already carries it. Reads
/// the raw PNG chunk structure directly (tEXt/iTXt/zTXt) rather than depending on exiftool.exe
/// or a heavier imaging library.
/// </summary>
public static class PngMetadataReader
{
    public static VrcxPhotoMetadata? TryReadVrcxMetadata(string pngPath)
    {
        string? json = TryReadTextChunk(pngPath, "Description");
        if (json is null) return null;

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<VrcxPhotoMetadata>(json);
        }
        catch (System.Text.Json.JsonException)
        {
            return null; // not VRCX-tagged, or a format this doesn't understand - not fatal
        }
    }

    private static string? TryReadTextChunk(string pngPath, string keyword)
    {
        using var fs = new FileStream(pngPath, FileMode.Open, FileAccess.Read);
        using var br = new BinaryReader(fs);

        Span<byte> signature = stackalloc byte[8];
        if (fs.Read(signature) != 8 || signature[1] != 'P' || signature[2] != 'N' || signature[3] != 'G')
            return null;

        while (fs.Position < fs.Length)
        {
            uint length = ReadUInt32BigEndian(br);
            string type = Encoding.ASCII.GetString(br.ReadBytes(4));
            byte[] data = br.ReadBytes((int)length);
            br.ReadBytes(4); // CRC, not verified

            if (type is "tEXt" or "iTXt" or "zTXt")
            {
                string? text = type switch
                {
                    "tEXt" => DecodeTEXt(data, keyword),
                    "iTXt" => DecodeITXt(data, keyword),
                    _ => null, // zTXt (compressed) not used by VRCX as of this writing
                };
                if (text is not null) return text;
            }

            if (type == "IEND") break;
        }
        return null;
    }

    private static string? DecodeTEXt(byte[] data, string keyword)
    {
        int nullIndex = Array.IndexOf(data, (byte)0);
        if (nullIndex < 0) return null;
        string key = Encoding.Latin1.GetString(data, 0, nullIndex);
        if (key != keyword) return null;
        return Encoding.Latin1.GetString(data, nullIndex + 1, data.Length - nullIndex - 1);
    }

    private static string? DecodeITXt(byte[] data, string keyword)
    {
        int pos = Array.IndexOf(data, (byte)0);
        if (pos < 0) return null;
        string key = Encoding.UTF8.GetString(data, 0, pos);
        if (key != keyword) return null;

        pos++; // skip null
        pos += 2; // compression flag + method bytes
        pos = Array.IndexOf(data, (byte)0, pos) + 1; // skip language tag
        pos = Array.IndexOf(data, (byte)0, pos) + 1; // skip translated keyword
        return Encoding.UTF8.GetString(data, pos, data.Length - pos);
    }

    private static uint ReadUInt32BigEndian(BinaryReader br)
    {
        Span<byte> bytes = stackalloc byte[4];
        br.Read(bytes);
        return (uint)((bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3]);
    }
}
