using System;
using System.IO;
using System.Text;

namespace LazerSR.Hook.ReplayUpload;

/// <summary>
/// <c>.osr</c> 리플레이 파일 헤더의 <b>플레이어 이름</b>만 읽는다. 소유권 확인용 — realm의
/// <c>RealmUser</c> 메타데이터가 어긋나도(과거 남의 리플레이가 잘못 올라가던 버그) 파일 자체에
/// 박혀 있는 이름과 대조하면 확실하다.
/// <para>
/// osr 포맷: <c>byte mode · int32 version · string beatmapMD5 · string playerName · ...</c>
/// 문자열은 <c>0x00</c>(빈 값) 또는 <c>0x0b</c> + ULEB128 길이 + UTF-8 바이트.
/// </para>
/// </summary>
internal static class OsrHeader
{
    /// <summary>
    /// <paramref name="path"/>의 osr 헤더 플레이어 이름이 <paramref name="expectedUsername"/>와
    /// (대소문자 무시) 일치하면 true. 파싱 실패·불일치는 전부 false — 애매하면 올리지 않는다.
    /// </summary>
    public static bool PlayerNameMatches(string path, string expectedUsername)
    {
        string? name = TryReadPlayerName(path);
        if (name == null)
        {
            HookLog.Write($"[LazerSR] OsrHeader: could not read player name from {path}");
            return false;
        }

        return string.Equals(name.Trim(), expectedUsername.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    public static string? TryReadPlayerName(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            using var br = new BinaryReader(fs, Encoding.UTF8);

            br.ReadByte();       // mode
            br.ReadInt32();      // version
            ReadOsuString(br);   // beatmap md5
            return ReadOsuString(br); // player name
        }
        catch (Exception e)
        {
            HookLog.Write($"[LazerSR] OsrHeader.TryReadPlayerName({path}) failed: {e}");
            return null;
        }
    }

    private static string? ReadOsuString(BinaryReader br)
    {
        byte marker = br.ReadByte();
        if (marker == 0x00) return string.Empty;
        if (marker != 0x0b) throw new InvalidDataException($"unexpected string marker 0x{marker:x2}");

        int length = ReadUleb128(br);
        byte[] bytes = br.ReadBytes(length);
        if (bytes.Length != length) throw new EndOfStreamException();
        return Encoding.UTF8.GetString(bytes);
    }

    private static int ReadUleb128(BinaryReader br)
    {
        int result = 0;
        int shift = 0;
        while (true)
        {
            byte b = br.ReadByte();
            result |= (b & 0x7f) << shift;
            if ((b & 0x80) == 0) break;
            shift += 7;
            if (shift > 35) throw new InvalidDataException("ULEB128 too long");
        }
        return result;
    }
}
