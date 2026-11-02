using System.Text;

namespace Torimaru.Core;

/// <summary>
/// PNG の tEXt チャンクを読み書きする。注釈データを PNG 自身に同梱するために使う。
/// ペイロードは Base64 にして ASCII 化し、tEXt の Latin-1 制約を確実に満たす。
/// </summary>
public static class PngTextChunk
{
    public const string DefaultKeyword = "TorimaruData";

    /// <summary>旧名 (ScreenCapture) 時代に保存した PNG のキーワード。読み取りのみ対応する。</summary>
    public const string LegacyKeyword = "ScreenCaptureData";

    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }

    private static uint Crc32(byte[] buffer, int offset, int length)
    {
        var c = 0xFFFFFFFFu;
        for (var i = offset; i < offset + length; i++)
        {
            c = CrcTable[(c ^ buffer[i]) & 0xFF] ^ (c >> 8);
        }

        return c ^ 0xFFFFFFFFu;
    }

    private static void WriteBigEndian(Stream stream, uint value)
    {
        stream.WriteByte((byte)(value >> 24));
        stream.WriteByte((byte)(value >> 16));
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)value);
    }

    private static uint ReadBigEndian(byte[] buffer, int offset)
        => ((uint)buffer[offset] << 24) | ((uint)buffer[offset + 1] << 16)
           | ((uint)buffer[offset + 2] << 8) | buffer[offset + 3];

    public static bool IsPng(byte[]? png)
    {
        if (png is null || png.Length < Signature.Length)
        {
            return false;
        }

        for (var i = 0; i < Signature.Length; i++)
        {
            if (png[i] != Signature[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// PNG バイト列にテキストを埋め込んだ新しいバイト列を返す。
    /// 同じキーワードの既存チャンクは置き換える（重複させない）。
    /// </summary>
    public static byte[] Embed(byte[] png, string text, string keyword = DefaultKeyword)
    {
        ArgumentNullException.ThrowIfNull(png);
        ArgumentNullException.ThrowIfNull(text);
        ValidateKeyword(keyword);

        if (!IsPng(png))
        {
            throw new ArgumentException("PNG 署名が見つかりません。", nameof(png));
        }

        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
        var chunkData = new byte[keyword.Length + 1 + payload.Length];
        Encoding.ASCII.GetBytes(keyword, 0, keyword.Length, chunkData, 0);
        chunkData[keyword.Length] = 0;
        Encoding.ASCII.GetBytes(payload, 0, payload.Length, chunkData, keyword.Length + 1);

        using var output = new MemoryStream(png.Length + chunkData.Length + 64);
        output.Write(Signature, 0, Signature.Length);

        var inserted = false;
        foreach (var chunk in EnumerateChunks(png))
        {
            if (chunk.Type == "tEXt" && ReadKeyword(png, chunk) == keyword)
            {
                continue;
            }

            if (chunk.Type == "IEND" && !inserted)
            {
                WriteChunk(output, "tEXt", chunkData);
                inserted = true;
            }

            output.Write(png, chunk.HeaderOffset, chunk.TotalLength);
        }

        if (!inserted)
        {
            throw new ArgumentException("IEND チャンクが見つかりません。", nameof(png));
        }

        return output.ToArray();
    }

    /// <summary>
    /// 埋め込まれたテキストを取り出す。現行キーワードが無ければ旧キーワードも試す。
    /// 改名前に保存した PNG を再編集できるようにするための経路。
    /// </summary>
    public static string? ExtractWithLegacy(byte[]? png)
        => Extract(png) ?? Extract(png, LegacyKeyword);

    /// <summary>埋め込まれたテキストを取り出す。無ければ null。</summary>
    public static string? Extract(byte[]? png, string keyword = DefaultKeyword)
    {
        if (!IsPng(png))
        {
            return null;
        }

        var bytes = png!;
        foreach (var chunk in EnumerateChunks(bytes))
        {
            if (chunk.Type != "tEXt" || ReadKeyword(bytes, chunk) != keyword)
            {
                continue;
            }

            var separator = Array.IndexOf(bytes, (byte)0, chunk.DataOffset, chunk.DataLength);
            if (separator < 0)
            {
                return null;
            }

            var start = separator + 1;
            var length = chunk.DataOffset + chunk.DataLength - start;
            if (length <= 0)
            {
                return string.Empty;
            }

            try
            {
                var base64 = Encoding.ASCII.GetString(bytes, start, length);
                return Encoding.UTF8.GetString(Convert.FromBase64String(base64));
            }
            catch (FormatException)
            {
                return null;
            }
        }

        return null;
    }

    private static void ValidateKeyword(string keyword)
    {
        if (string.IsNullOrEmpty(keyword) || keyword.Length > 79)
        {
            throw new ArgumentException("キーワードは 1〜79 文字である必要があります。", nameof(keyword));
        }

        foreach (var ch in keyword)
        {
            if (ch is < (char)32 or > (char)126)
            {
                throw new ArgumentException("キーワードに使えない文字が含まれています。", nameof(keyword));
            }
        }
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        WriteBigEndian(stream, (uint)data.Length);

        var typeAndData = new byte[4 + data.Length];
        Encoding.ASCII.GetBytes(type, 0, 4, typeAndData, 0);
        Buffer.BlockCopy(data, 0, typeAndData, 4, data.Length);

        stream.Write(typeAndData, 0, typeAndData.Length);
        WriteBigEndian(stream, Crc32(typeAndData, 0, typeAndData.Length));
    }

    private static string? ReadKeyword(byte[] png, ChunkInfo chunk)
    {
        var separator = Array.IndexOf(png, (byte)0, chunk.DataOffset, chunk.DataLength);
        return separator < 0
            ? null
            : Encoding.ASCII.GetString(png, chunk.DataOffset, separator - chunk.DataOffset);
    }

    /// <summary>長さ 4 + 型 4 + データ + CRC 4 で 1 チャンク。</summary>
    private readonly record struct ChunkInfo(string Type, int HeaderOffset, int DataOffset, int DataLength)
    {
        public int TotalLength => 12 + DataLength;
    }

    private static IEnumerable<ChunkInfo> EnumerateChunks(byte[] png)
    {
        var offset = Signature.Length;
        while (offset + 12 <= png.Length)
        {
            var length = ReadBigEndian(png, offset);
            if (length > int.MaxValue - 12 || offset + 12 + length > png.Length)
            {
                yield break;
            }

            var type = Encoding.ASCII.GetString(png, offset + 4, 4);
            yield return new ChunkInfo(type, offset, offset + 8, (int)length);

            offset += 12 + (int)length;
            if (type == "IEND")
            {
                yield break;
            }
        }
    }
}
