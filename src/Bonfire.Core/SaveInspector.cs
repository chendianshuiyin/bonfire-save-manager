// Read-only format handling adapted from sl2-analyzer (Apache-2.0).
// See THIRD-PARTY-NOTICES.md for authorship, source revisions, and license.
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Bonfire.Core;

public record CharacterInfo(int Slot, string Name, string Level, string PlayTime, string Location, string Extra);
public record Inspection(string Summary, List<CharacterInfo> Characters);

public static class SaveInspector
{
    private record Entry(int Offset, int Size);
    private static uint U32(byte[] b, int p) => p >= 0 && p <= b.Length - 4 ? BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p, 4)) : throw new InvalidDataException("存档结构超出边界。");
    private static ushort U16(byte[] b, int p) => p >= 0 && p <= b.Length - 2 ? BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(p, 2)) : throw new InvalidDataException("存档结构超出边界。");
    private static byte U8(byte[] b, int p) => p >= 0 && p < b.Length ? b[p] : throw new InvalidDataException("存档结构超出边界。");
    private static string Name(byte[] b, int p, int chars = 16)
    {
        if (p < 0 || p + chars * 2 > b.Length) return "";
        var value = Encoding.Unicode.GetString(b, p, chars * 2).Split('\0')[0];
        return value.Length > 0 && value.All(c => !char.IsControl(c) && c != '\uFFFD' && !char.IsSurrogate(c)) ? value : "";
    }
    private static string Time(uint seconds) => seconds <= 360000000 ? $"{seconds / 3600}时 {seconds % 3600 / 60}分" : "未识别";
    private static List<Entry> Entries(byte[] data)
    {
        if (data.Length < 64 || Encoding.ASCII.GetString(data, 0, 4) != "BND4") throw new InvalidDataException("当前解析器只支持 BND4。");
        var count = U32(data, 12); if (count is < 11 or > 64) throw new InvalidDataException("不支持的存档条目结构。");
        var items = new List<Entry>();
        for (int i = 0; i < count; i++)
        {
            int at = 64 + i * 32;
            var size = U32(data, at + 8); var high = U32(data, at + 12); var offset = U32(data, at + 16);
            if (high != 0 || size < 16 || (ulong)offset + size > (ulong)data.Length || offset < 64 + count * 32) throw new InvalidDataException("存档条目越界。");
            items.Add(new((int)offset, (int)size));
        }
        return items;
    }
    private static byte[] Blob(byte[] data, Entry entry)
    {
        var blob = data.AsSpan(entry.Offset, entry.Size).ToArray();
        if (!MD5.HashData(blob.AsSpan(16)).AsSpan().SequenceEqual(blob.AsSpan(0, 16))) throw new InvalidDataException("存档内部校验不匹配，跳过角色解析。");
        return blob;
    }
    private static byte[] Decode(byte[] blob, string game)
    {
        var key = game switch { "ds2" => "B7FD463E4A9C1102DF1739E5F3B2A50F", "ds2s" => "599F9B699640A55236EE2D70835EC744", "dsr" => "0123456789ABCDEFFEDCBA9876543210", "ds3" => "FD464D695E69A39A10E319A7ACE8B7FA", _ => null };
        if (key == null) return blob[16..];
        if (blob.Length < 48 || (blob.Length - 32) % 16 != 0) throw new InvalidDataException("加密存档长度不支持。");
        using var aes = Aes.Create(); aes.Key = Convert.FromHexString(key);
        var plain = aes.DecryptCbc(blob.AsSpan(32), blob.AsSpan(16, 16), PaddingMode.None);
        var length = U32(plain, 0);
        if (length == 0 || length > plain.Length - 4) throw new InvalidDataException("存档解密结果无效。");
        return plain.AsSpan(4, (int)length).ToArray();
    }
    public static Inspection Inspect(byte[] data, Game profile)
    {
        try
        {
            var entries = Entries(data); var game = profile.Id.Split('~')[0]; var characters = new List<CharacterInfo>();
            var menuIndex = game is "ds2" or "ds2s" ? 0 : 10;
            var menuBlob = Blob(data, entries[menuIndex]); var menu = Decode(menuBlob, game);
            for (int slot = 0; slot < 10; slot++)
            {
                try
                {
                    string name = "", level = "未识别", time = "未识别", location = "未识别", extra = "";
                    var entryIndex = game is "ds2" or "ds2s" ? slot + 1 : slot;
                    if (game == "ds3" && U8(menu, 4244 + slot) != 1) continue;
                    if (game == "sekiro" && U8(menu, 212 + slot) != 1) continue;
                    if (game == "er")
                    {
                        int active = checked(356 + (int)U32(menuBlob, 352));
                        if (U8(menuBlob, active + slot) != 1) continue;
                        int p = active + 10 + 588 * slot;
                        name = Name(menuBlob, p); var lv = U32(menuBlob, p + 34);
                        if (lv is >= 1 and <= 713) level = lv.ToString();
                        time = Time(U32(menuBlob, p + 38));
                        // Profile map ID is four little-endian bytes, least significant component first.
                        var map = menuBlob.AsSpan(p + 46, 4).ToArray();
                        if (map[3] is >= 10 and <= 61 && map.All(v => v < 100)) location = $"地图 m{map[3]:D2}_{map[2]:D2}_{map[1]:D2}_{map[0]:D2}";
                    }
                    var payload = Decode(Blob(data, entries[entryIndex]), game);
                    if (game == "ds3")
                    {
                        int p = 4254 + 554 * slot; name = Name(menu, p); time = Time(U32(menu, p + 38));
                        var rosterLevel = U32(menu, p + 34);
                        int[] offsets = [0, 4, 8, 12, 16, 20, 24, 28, 40];
                        for (int at = 40; at < payload.Length - 52; at += 4)
                        {
                            if (U32(payload, at) is < 1 or > 99) continue;
                            var values = offsets.Select(o => U32(payload, at + o)).ToArray(); var lv = U32(payload, at + 44);
                            if (lv == rosterLevel && lv is >= 1 and <= 802 && values.All(v => v is >= 1 and <= 99) && values.Sum(v => (long)v) - 89 == lv)
                            { level = lv.ToString(); extra = "持有灵魂 " + U32(payload, at + 48); break; }
                        }
                    }
                    else if (game is "ds2" or "ds2s")
                    {
                        name = Name(payload, 960); var lv = U16(payload, 56);
                        var values = Enumerable.Range(0, 9).Select(i => U16(payload, 32 + 2 * i)).ToArray();
                        if (name.Length == 0 || !values.All(v => v is >= 1 and <= 99) || values.Sum(v => (int)v) - 53 != lv) continue;
                        level = lv.ToString();
                        var expected = 1286 + 496 * slot;
                        if (Name(menu, expected) == name) time = Time(U32(menu, expected + 66));
                        else if (menu.AsSpan().IndexOf(Encoding.Unicode.GetBytes(name + "\0")) < 0) continue;
                        extra = $"持有灵魂 {U32(payload, 60)} · 累计灵魂 {U32(payload, 64)}";
                    }
                    else if (game == "sekiro")
                    {
                        name = "狼（游戏无角色昵称）"; level = "不适用"; time = Time(U32(payload, 0x33F80));
                        var attack = U32(payload, 0x3449C); var vitality = U32(payload, 0x34498);
                        extra = $"攻击力 {(attack is >= 1 and <= 99 ? attack.ToString() : "未识别")} · 体力 {(vitality is >= 1 and <= 20 ? vitality.ToString() : "未识别")} · 钱 {U32(payload, 0x344D0)}";
                    }
                    else if (game is "dsr" or "ds1")
                    {
                        // Require a named slot and matching menu record before presenting legacy fields.
                        var pattern = Convert.FromHexString("00FFFFFFFF000000000000000000000000FFFFFFFF");
                        for (int at = 420; at < payload.Length - pattern.Length; at++)
                        {
                            if (!payload.AsSpan(at, pattern.Length).SequenceEqual(pattern)) continue;
                            var n = Name(payload, at - 271, 13); var lv = U16(payload, at - 295);
                            if (n.Length == 0 || lv is < 1 or > 838) continue;
                            var encoded = Encoding.Unicode.GetBytes(n + "\0");
                            for (int m = 0; m < menu.Length - Math.Max(encoded.Length, 44); m += 2)
                            {
                                if (!menu.AsSpan(m, encoded.Length).SequenceEqual(encoded) || U32(menu, m + 36) != lv) continue;
                                name = n; level = lv.ToString(); time = Time(U32(menu, m + 40)); break;
                            }
                            if (name.Length > 0) break;
                        }
                    }
                    if (name.Length > 0) characters.Add(new(slot + 1, name, level, time, location, extra));
                }
                catch (Exception ex) when (ex is InvalidDataException or CryptographicException or OverflowException or ArgumentException) { }
            }
            var summary = characters.Count > 0 ? $"识别到 {characters.Count} 个角色。位置优先显示可确认的地图 ID；未映射字段显示未识别。" : "此存档尚未解析出可靠角色信息，或没有有效角色。备份和导出仍可使用。";
            if (profile.Id.Contains('~')) summary += " Mod 可能改变数据布局，解析结果仅供参考。";
            return new(summary, characters);
        }
        catch (Exception ex) when (ex is InvalidDataException or CryptographicException or OverflowException or ArgumentException)
        { return new("未识别：" + ex.Message + " 备份功能不依赖角色解析。", []); }
    }
}
