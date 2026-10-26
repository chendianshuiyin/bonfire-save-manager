using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Bonfire.Core;

public record Game(string Id, string Name, string FileName, string RelativePath, bool Documents, string ProcessName)
{
    public override string ToString() => Name;
    public static readonly Game[] All = [
        new("ds1", "黑暗之魂 · 受死版", "DRAKS0005.sl2", @"NBGI\DarkSouls", true, "DARKSOULS"),
        new("dsr", "黑暗之魂 · 重制版", "DRAKS0005.sl2", @"NBGI\DARK SOULS REMASTERED", true, "DarkSoulsRemastered"),
        new("ds2", "黑暗之魂 II", "DARKSII0000.sl2", "DarkSoulsII", false, "DarkSoulsII"),
        new("ds2s", "黑暗之魂 II · 原罪学者", "DS2SOFS0000.sl2", "DarkSoulsII", false, "DarkSoulsII"),
        new("ds3", "黑暗之魂 III", "DS30000.sl2", "DarkSoulsIII", false, "DarkSoulsIII"),
        new("er", "艾尔登法环", "ER0000.sl2", "EldenRing", false, "eldenring"),
        new("sekiro", "只狼", "S0000.sl2", "Sekiro", false, "sekiro")];
    public static Game ById(string id)
    {
        var parts = id.Split('~');
        var game = All.FirstOrDefault(g => g.Id == parts[0]) ?? throw new InvalidDataException("未知游戏版本。");
        if (parts.Length == 1) return game;
        if (parts.Length != 2 || !ValidModExtension(parts[1])) throw new InvalidDataException("无效 Mod 存档类型。");
        var mode = parts[1] == "co2" ? "联机 Mod · .co2" : "Mod · ." + parts[1];
        return game with { Id = id, Name = game.Name + " / " + mode, FileName = Path.ChangeExtension(game.FileName, parts[1]) };
    }
    public static bool ValidModExtension(string extension) => Regex.IsMatch(extension, @"\A[a-z0-9_-]{1,16}\z") && extension is not "sl2" and not "bak" and not "tmp" and not "json";
    public static Game[] Identify(string path)
    {
        var filename = Path.GetFileName(path);
        var extension = Path.GetExtension(filename).TrimStart('.').ToLowerInvariant();
        return All.Where(g => string.Equals(Path.GetFileNameWithoutExtension(g.FileName), Path.GetFileNameWithoutExtension(filename), StringComparison.OrdinalIgnoreCase))
            .Where(_ => extension == "sl2" || ValidModExtension(extension))
            .Select(g => extension == "sl2" ? g : ById(g.Id + "~" + extension)).ToArray();
    }
}

public record SaveFile(Game Game, string Path)
{
    public string GameName => Game.Name;
    public string Account => System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(Path)) ?? "未知";
    public override string ToString() => $"{Game.Name} · {Account}";
}

public record Snapshot(string Id, string GameId, string Label, string Account, DateTimeOffset CreatedAt, string Sha256, long Size)
{
    public string GameName => Game.ById(GameId).Name;
    public string Time => CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    public string SizeText => $"{Size / 1048576.0:F2} MB";
    public string Kind => Label.StartsWith("替换前 · ") ? "自动备份" : "手动保存";
}

public static class SaveScanner
{
    public static List<SaveFile> Scan()
    {
        var result = new List<SaveFile>();
        foreach (var game in Game.All)
        {
            var folder = Environment.GetFolderPath(game.Documents ? Environment.SpecialFolder.MyDocuments : Environment.SpecialFolder.ApplicationData);
            var root = Path.Combine(folder, game.RelativePath);
            if (!Directory.Exists(root)) continue;
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint, MaxRecursionDepth = 3 };
            foreach (var path in Directory.EnumerateFiles(root, Path.GetFileNameWithoutExtension(game.FileName) + ".*", options))
            {
                var profile = Game.Identify(path).FirstOrDefault(g => g.Id.Split('~')[0] == game.Id);
                if (profile == null) continue;
                // Unknown extensions must still contain a supported save container.
                try { _ = Vault.ReadSave(path); result.Add(new(profile, path)); }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { }
            }
        }
        return result;
    }

    public static void EnsureGameClosed(Game game)
    {
        var processes = Process.GetProcessesByName(game.ProcessName);
        try { if (processes.Length > 0) throw new IOException($"请先完全退出 {game.Name} 再操作存档。"); }
        finally { foreach (var process in processes) process.Dispose(); }
    }
}

public sealed class Vault
{
    public const int MaxSaveSize = 32 * 1024 * 1024;
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public string Root { get; }
    public Vault(string root) { Root = Path.GetFullPath(root); Directory.CreateDirectory(Root); }
    public static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));
    public static void Validate(byte[] data)
    {
        if (data.Length < 1024 || data.Length > MaxSaveSize ||
            (Encoding.ASCII.GetString(data, 0, 4) is not "BND4" and not "BND3"))
            throw new InvalidDataException("不是支持的存档容器（BND3/BND4），或文件大小异常。不会写入游戏目录。");
    }
    public static byte[] ReadSave(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaxSaveSize) throw new InvalidDataException("存档超过 32 MB 限制。");
        var data = new byte[checked((int)stream.Length)];
        stream.ReadExactly(data); Validate(data); return data;
    }
    public string Folder(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("存档 ID 无效。");
        return Path.Combine(Root, id);
    }
    public List<Snapshot> List()
    {
        var items = new List<Snapshot>();
        foreach (var directory in Directory.EnumerateDirectories(Root))
        {
            var path = Path.Combine(directory, "manifest.json");
            if (!File.Exists(path)) continue;
            var item = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(path)) ?? throw new InvalidDataException($"损坏的清单：{path}");
            if (!string.Equals(Folder(item.Id), directory, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("备份清单与目录不匹配。");
            _ = Game.ById(item.GameId); items.Add(item);
        }
        return items.OrderByDescending(s => s.CreatedAt).ToList();
    }
    public Snapshot Capture(string path, Game game, string label)
    {
        SaveScanner.EnsureGameClosed(game);
        var data = ReadSave(path);
        if (!string.Equals(Path.GetFileName(path), game.FileName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"此版本的存档文件应命名为 {game.FileName}。");
        var item = new Snapshot(Guid.NewGuid().ToString("N"), game.Id,
            string.IsNullOrWhiteSpace(label) ? "未命名存档" : label.Trim(),
            Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(path))) ?? "未知", DateTimeOffset.UtcNow, Hash(data), data.LongLength);
        Store(item, data); return item;
    }
    public void Store(Snapshot item, byte[] data)
    {
        if (string.IsNullOrWhiteSpace(item.GameId) || item.Label == null || item.Label.Length > 2000 || item.Account == null || item.Account.Length > 512) throw new InvalidDataException("备份清单字段无效。");
        _ = Game.ById(item.GameId); Validate(data);
        if (data.LongLength != item.Size || Hash(data) != item.Sha256) throw new InvalidDataException("SHA-256 校验失败，存档可能已损坏。");
        var folder = Folder(item.Id);
        if (Directory.Exists(folder))
        {
            var existing = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(Path.Combine(folder, "manifest.json")));
            if (existing != item) throw new InvalidDataException("存在相同 ID 的不同存档。");
            _ = Read(item); return;
        }
        var staging = Path.Combine(Root, ".pending-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            File.WriteAllBytes(Path.Combine(staging, "save.sl2"), data);
            File.WriteAllText(Path.Combine(staging, "manifest.json"), JsonSerializer.Serialize(item, Json));
            Directory.Move(staging, folder);
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }
    public byte[] Read(Snapshot item)
    {
        var data = ReadSave(Path.Combine(Folder(item.Id), "save.sl2"));
        if (Hash(data) != item.Sha256 || data.LongLength != item.Size) throw new InvalidDataException("存档校验失败，操作已停止。");
        return data;
    }
    public Snapshot Restore(Snapshot item, SaveFile target)
    {
        if (item.GameId != target.Game.Id) throw new InvalidDataException("不能跨游戏版本替换存档。");
        SaveScanner.EnsureGameClosed(target.Game);
        var data = Read(item);
        if (!File.Exists(target.Path)) throw new FileNotFoundException("请先在游戏内创建存档，再扫描目标位置。", target.Path);
        var backup = Capture(target.Path, target.Game, "替换前 · " + item.Label);
        // Stage in the target directory so the final replacement stays on one volume.
        var staging = target.Path + ".bonfire-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(data); stream.Flush(true); }
            SaveScanner.EnsureGameClosed(target.Game);
            if (Hash(ReadSave(target.Path)) != backup.Sha256) throw new IOException("目标存档在备份后发生变化，已取消替换，请重试。");
            File.Replace(staging, target.Path, null);
        }
        finally { if (File.Exists(staging)) File.Delete(staging); }
        return backup;
    }
    public void Export(Snapshot item, string destination)
    {
        var full = Path.GetFullPath(destination);
        if (full.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("不能导出到备份库内部。");
        if (SaveScanner.Scan().Any(s => string.Equals(Path.GetFullPath(s.Path), full, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("这是游戏正在使用的存档位置，请使用“替换游戏存档”。");
        // Export only to a new file; replacement must go through the backup flow.
        var data = Read(item);
        using var output = new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        output.Write(data); output.Flush(true);
    }
}
