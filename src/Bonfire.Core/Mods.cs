using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Bonfire.Core;

public record GameInstall(Game Game, string Directory)
{
    public override string ToString() => Game.Name + " · " + Directory;
}
public record ModFile(string Path, string Hash, string? OriginalHash);
public record ModRecord(string Id, string Name, string Version, string GameId, string Directory, DateTimeOffset Created, List<ModFile> Files, string State);
public record ModInfo(string Name, string Version, string Status, string Evidence, string? ManagedId = null);

public static class SteamGames
{
    private static readonly Dictionary<string, string> Apps = new() { ["211420"] = "ds1", ["570940"] = "dsr", ["236430"] = "ds2", ["335300"] = "ds2s", ["374320"] = "ds3", ["1245620"] = "er", ["814380"] = "sekiro" };
    public static List<GameInstall> Scan()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (OperatingSystem.IsWindows())
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (key?.GetValue("SteamPath") is string steam && System.IO.Directory.Exists(steam)) roots.Add(steam);
        }
        var standard = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
        if (System.IO.Directory.Exists(standard)) roots.Add(standard);
        foreach (var root in roots.ToArray())
        {
            var libraries = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(libraries)) continue;
            foreach (Match match in Regex.Matches(File.ReadAllText(libraries), "\"path\"\\s+\"([^\"]+)\"")) roots.Add(match.Groups[1].Value.Replace(@"\\", @"\"));
        }
        var result = new List<GameInstall>();
        foreach (var root in roots)
        foreach (var (appId, gameId) in Apps)
        {
            var manifest = Path.Combine(root, "steamapps", "appmanifest_" + appId + ".acf");
            if (!File.Exists(manifest)) continue;
            var match = Regex.Match(File.ReadAllText(manifest), "\"installdir\"\\s+\"([^\"]+)\"");
            if (!match.Success) continue;
            var dir = Path.Combine(root, "steamapps", "common", match.Groups[1].Value);
            var game = Game.ById(gameId);
            foreach (var candidate in new[] { dir, Path.Combine(dir, "Game"), Path.Combine(dir, "DATA") })
                if (File.Exists(Path.Combine(candidate, game.ProcessName + ".exe"))) { result.Add(new(game, candidate)); break; }
        }
        return result.DistinctBy(i => i.Directory, StringComparer.OrdinalIgnoreCase).ToList();
    }
}

public sealed class ModManager(GameInstall install)
{
    private string Root => Path.GetFullPath(install.Directory);
    private string Records => SafePath(Root, ".bonfire-mods");
    public static string SafePath(string root, string relative)
    {
        root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var parts = relative.Replace('\\', '/').Split('/');
        if (Path.IsPathRooted(relative) || parts.Any(s => s is "" or "." or ".." || s.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || s.EndsWith(' ') || s.EndsWith('.')))
            throw new InvalidDataException("安装包包含不安全的路径：" + relative);
        var path = Path.GetFullPath(Path.Combine(root, Path.Combine(parts)));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("文件路径越界。");
        var at = new DirectoryInfo(root);
        while (at != null) { if (at.Exists && at.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("不支持符号链接或目录联接：" + at.FullName); at = at.Parent; }
        var cursor = root;
        foreach (var part in parts)
        {
            cursor = Path.Combine(cursor, part);
            if ((File.Exists(cursor) || System.IO.Directory.Exists(cursor)) && File.GetAttributes(cursor).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("不支持符号链接路径：" + cursor);
        }
        return path;
    }
    private string RecordFolder(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("Mod 记录 ID 无效。");
        return SafePath(Records, id);
    }
    private static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
    }
    private void VerifyInstall()
    {
        SaveScanner.EnsureGameClosed(install.Game);
        if (!File.Exists(Path.Combine(Root, install.Game.ProcessName + ".exe"))) throw new IOException("请选择包含游戏主程序的目录。");
    }
    private static bool Protected(string relative)
    {
        var name = Path.GetFileName(relative);
        return relative.Split('/', '\\').Any(s => s.Equals(".bonfire-mods", StringComparison.OrdinalIgnoreCase)) ||
            Game.All.Any(g => name.Equals(g.ProcessName + ".exe", StringComparison.OrdinalIgnoreCase)) ||
            name.StartsWith("steam_api", StringComparison.OrdinalIgnoreCase) || Game.Identify(name).Length > 0;
    }
    private List<ZipArchiveEntry> ZipFiles(ZipArchive zip)
    {
        var files = zip.Entries.Where(e => !e.FullName.EndsWith('/') && !e.FullName.EndsWith('\\')).ToList();
        if (files.Count == 0 || files.Count > 5000 || files.Sum(f => f.Length) > 2L * 1024 * 1024 * 1024) throw new IOException("安装包为空或超过 5000 个文件 / 2 GB 限制。");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var target = SafePath(Root, file.FullName);
            if (!paths.Add(target) || Protected(file.FullName) || ((file.ExternalAttributes >> 16) & 0xf000) == 0xa000) throw new IOException("安装包含重复、受保护文件或符号链接：" + file.FullName);
        }
        return files;
    }
    public List<string> Preview(string archive)
    {
        VerifyInstall(); using var zip = ZipFile.OpenRead(archive); return ZipFiles(zip).Select(e => e.FullName).ToList();
    }
    private void SaveRecord(ModRecord record)
    {
        var folder = RecordFolder(record.Id); System.IO.Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "manifest.json"); var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(record, Vault.Json)); File.Move(temp, path, true);
    }
    private List<ModRecord> ReadRecords()
    {
        if (!System.IO.Directory.Exists(Records)) return [];
        var records = new List<ModRecord>();
        foreach (var dir in System.IO.Directory.EnumerateDirectories(Records))
        {
            if (!Guid.TryParseExact(Path.GetFileName(dir), "N", out _)) continue;
            var path = SafePath(dir, "manifest.json"); if (!File.Exists(path)) continue;
            var record = JsonSerializer.Deserialize<ModRecord>(File.ReadAllText(path)) ?? throw new InvalidDataException("损坏的 Mod 清单。");
            if (!string.Equals(RecordFolder(record.Id), dir, StringComparison.OrdinalIgnoreCase) || !string.Equals(Path.GetFullPath(record.Directory), Root, StringComparison.OrdinalIgnoreCase) || record.GameId != install.Game.Id)
                throw new InvalidDataException("Mod 清单与游戏目录不匹配。");
            foreach (var file in record.Files) { _ = SafePath(Root, file.Path); if (Protected(file.Path)) throw new InvalidDataException("清单包含受保护文件。"); }
            records.Add(record);
        }
        return records;
    }
    public List<ModInfo> Inventory()
    {
        var records = ReadRecords();
        var result = records.Where(r => r.State != "removed").Select(r => new ModInfo(r.Name, r.Version, r.State == "installed" ? "由篝火管理" : "中断，可尝试移除恢复", $"{r.Files.Count} 个文件 · {r.Created.ToLocalTime():yyyy-MM-dd HH:mm}", r.Id)).ToList();
        (string name, string[] markers)[] known = [
            ("Seamless Co-op", ["SeamlessCoop/ds1sc.dll", "SeamlessCoop/ds2sc.dll", "SeamlessCoop/ds3sc.dll", "SeamlessCoop/ersc.dll", "SeamlessCoop/elden_ring_seamless_coop.dll"]),
            ("Sekiro Online", ["mods/SekiroOnline.dll", "SekiroOnline.dll"]),
            ("Mod Engine 2", ["modengine2_launcher.exe", "modengine2/bin/modengine2.dll"]),
            ("Mod Engine", ["modengine.ini"]),
            ("Elden Mod Loader", ["mod_loader_config.ini"]),
            ("Dynamic Key Prompts", ["DynamicKeyPrompts-README.txt"]),
            ("Blue Acolyte", ["BlueAcolyte.dll", "BlueAcolyte/BlueAcolyte.dll"])
        ];
        foreach (var (name, markers) in known)
        {
            var marker = markers.FirstOrDefault(m => File.Exists(SafePath(Root, m))); if (marker == null) continue;
            if (records.Any(r => r.State != "removed" && r.Files.Any(f => f.Path.Replace('\\', '/').Equals(marker, StringComparison.OrdinalIgnoreCase)))) continue;
            var version = FileVersionInfo.GetVersionInfo(SafePath(Root, marker)).ProductVersion;
            result.Add(new(name, string.IsNullOrWhiteSpace(version) ? "未知" : version, "手工安装 · 只读识别", marker));
        }
        foreach (var marker in new[] { "dinput8.dll", "dxgi.dll", "d3d11.dll", "winmm.dll", "version.dll" })
            if (File.Exists(SafePath(Root, marker))) result.Add(new("代理 DLL / 加载器", FileVersionInfo.GetVersionInfo(SafePath(Root, marker)).ProductVersion ?? "未知", "候选项 · 需人工确认", marker));
        return result;
    }
    public ModRecord Install(string archive, string name, string version)
    {
        VerifyInstall(); using var zip = ZipFile.OpenRead(archive); var entries = ZipFiles(zip);
        var current = ReadRecords().Where(r => r.State != "removed").ToList();
        if (entries.Any(e => current.Any(r => r.Files.Any(f => f.Path.Replace('\\', '/').Equals(e.FullName.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)))))
            throw new IOException("安装包与已管理的 Mod 文件冲突，请先移除旧版，再安装新版。");
        var record = new ModRecord(Guid.NewGuid().ToString("N"), string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(archive) : name.Trim(), string.IsNullOrWhiteSpace(version) ? "未知（未提供）" : version.Trim(), install.Game.Id, Root, DateTimeOffset.UtcNow, [], "prepared");
        var folder = RecordFolder(record.Id); System.IO.Directory.CreateDirectory(folder);
        // Copy and verify every original before modifying any game file.
        foreach (var entry in entries)
        {
            var target = SafePath(Root, entry.FullName);
            var staged = SafePath(folder, "new/" + entry.FullName); System.IO.Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
            using (var input = entry.Open()) using (var output = File.Create(staged)) input.CopyTo(output);
            string? originalHash = null;
            if (File.Exists(target))
            {
                originalHash = HashFile(target); var original = SafePath(folder, "original/" + entry.FullName);
                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(original)!); File.Copy(target, original);
                if (HashFile(original) != originalHash) throw new IOException("原文件发生变化，已停止安装。");
            }
            record.Files.Add(new(entry.FullName, HashFile(staged), originalHash));
        }
        SaveRecord(record);
        try
        {
            foreach (var file in record.Files)
            {
                VerifyInstall(); var target = SafePath(Root, file.Path);
                if ((File.Exists(target) ? HashFile(target) : null) != file.OriginalHash) throw new IOException("目标文件发生变化，已停止安装：" + file.Path);
                AtomicCopy(SafePath(folder, "new/" + file.Path), target);
            }
            record = record with { State = "installed" }; SaveRecord(record); return record;
        }
        catch
        {
            try { Remove(record.Id); } catch { SaveRecord(record with { State = "interrupted" }); }
            throw;
        }
    }
    private static void AtomicCopy(string source, string destination)
    {
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temp = destination + ".bonfire-" + Guid.NewGuid().ToString("N");
        try { File.Copy(source, temp); File.Move(temp, destination, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public void Remove(string id)
    {
        VerifyInstall(); var record = ReadRecords().SingleOrDefault(r => r.Id == id && r.State != "removed") ?? throw new IOException("没有可移除的安装记录。");
        var folder = RecordFolder(id);
        // Preflight all hashes first; user-edited files are never silently discarded.
        foreach (var file in record.Files)
        {
            var target = SafePath(Root, file.Path); var current = File.Exists(target) ? HashFile(target) : null;
            if (current != file.Hash && current != file.OriginalHash) throw new IOException("文件在安装后被修改，已停止移除：" + file.Path);
            if (file.OriginalHash != null && HashFile(SafePath(folder, "original/" + file.Path)) != file.OriginalHash) throw new IOException("原文件备份校验失败。");
        }
        SaveRecord(record with { State = "removing" });
        foreach (var file in record.Files)
        {
            VerifyInstall(); var target = SafePath(Root, file.Path);
            var current = File.Exists(target) ? HashFile(target) : null;
            if (current == file.OriginalHash) continue;
            if (current != file.Hash) throw new IOException("文件在移除过程中发生变化：" + file.Path);
            if (file.OriginalHash != null) AtomicCopy(SafePath(folder, "original/" + file.Path), target);
            else if (File.Exists(target)) File.Delete(target);
        }
        SaveRecord(record with { State = "removed" });
    }
}
