using Bonfire.Core;
using System.IO.Compression;

var root = Path.Combine(Path.GetTempPath(), "BonfireTests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var passed = 0;
void Check(bool ok, string label) { if (!ok) throw new Exception("FAIL: " + label); Console.WriteLine("PASS: " + label); passed++; }
void Reject(Action action, string label) { try { action(); } catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException) { Check(true, label); return; } throw new Exception("FAIL: " + label); }
byte[] Fixture(byte fill) { var bytes = Enumerable.Repeat(fill, 4096).ToArray(); "BND4"u8.CopyTo(bytes); return bytes; }
try
{
    if (args.Length == 1 && args[0] == "--inspect-local")
    {
        foreach (var save in SaveScanner.Scan())
        {
            var report = SaveInspector.Inspect(Vault.ReadSave(save.Path), save.Game);
            Console.WriteLine(save.Game.Name + ": " + System.Text.Json.JsonSerializer.Serialize(report, new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        }
        return;
    }
    if (args.Length == 1 && args[0] == "--inspect-mods")
    {
        foreach (var install in SteamGames.Scan()) Console.WriteLine(install.Game.Name + ": " + System.Text.Json.JsonSerializer.Serialize(new ModManager(install).Inventory(), new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        return;
    }
    var game = Game.ById("ds3"); var vault = new Vault(Path.Combine(root, "vault"));
    var source = Path.Combine(root, game.FileName); File.WriteAllBytes(source, Fixture(1));
    var first = vault.Capture(source, game, "Before boss");
    Check(vault.List().Count == 1 && vault.Read(first).SequenceEqual(Fixture(1)), "Capture is byte-exact");
    File.WriteAllBytes(source, Fixture(2));
    var automatic = vault.Restore(first, new(game, source));
    Check(File.ReadAllBytes(source).SequenceEqual(Fixture(1)), "Restore replaces target exactly");
    Check(vault.Read(automatic).SequenceEqual(Fixture(2)), "Pre-restore backup preserves old target");
    vault.Restore(automatic, new(game, source));
    Check(File.ReadAllBytes(source).SequenceEqual(Fixture(2)), "Automatic backup can undo replacement");
    Reject(() => vault.Restore(first, new(Game.ById("dsr"), source)), "Reject cross-game restore");
    Reject(() => vault.Folder("../escape"), "Reject path traversal");
    Reject(() => Vault.Validate(new byte[4096]), "Reject invalid container");
    Reject(() => Vault.Validate("BND4"u8.ToArray()), "Reject truncated container");
    var export = Path.Combine(root, "export.sl2"); vault.Export(first, export);
    Check(File.ReadAllBytes(export).SequenceEqual(Fixture(1)), "Export preserves bytes");
    Reject(() => vault.Export(first, export), "Export does not overwrite existing files");
    Reject(() => vault.Capture(source, Game.ById("ds2"), "Wrong version"), "Reject mismatched filename");
    vault.Store(first, vault.Read(first)); Check(vault.List().Count == 3, "Repeated import is idempotent");
    File.WriteAllBytes(Path.Combine(vault.Folder(first.Id), "save.sl2"), Fixture(3));
    Reject(() => vault.Restore(first, new(game, source)), "Reject corrupted backup before touching target");
    Check(File.ReadAllBytes(source).SequenceEqual(Fixture(2)), "Corruption failure leaves target intact");
    Reject(() => new GitHubVault("owner/repo/extra"), "Reject invalid repository");
    Check(new GitHubVault("https://github.com/owner/repo.git/").Repository == "owner/repo", "Accept HTTPS repository URL");
    Check(new GitHubVault("git@github.com:owner/repo.git").Repository == "owner/repo", "Accept SSH repository URL");
    Reject(() => new GitHubVault("https://evil.example/owner/repo"), "Reject non-GitHub hosts");
    foreach (var profile in Game.All)
    {
        Check(Game.Identify(profile.FileName).Any(g => g.Id == profile.Id), "Identify " + profile.Id);
        Check(Game.Identify(Path.ChangeExtension(profile.FileName, "co2")).Any(g => g.Id == profile.Id + "~co2"), "Identify co-op " + profile.Id);
    }
    Check(Game.Identify("ER0000.custom").Single().Id == "er~custom", "Identify custom mod extension");
    Check(Game.Identify("ER0000.sl2.bak").Length == 0, "Ignore backup suffixes");
    Check(Game.Identify("ER0000.bak").Length == 0, "Ignore backup files");
    var mod = Game.ById("ds3~co2"); var modPath = Path.Combine(root, mod.FileName); File.WriteAllBytes(modPath, Fixture(5));
    var modSave = vault.Capture(modPath, mod, "Co-op");
    Reject(() => vault.Restore(modSave, new(game, source)), "Reject mod to vanilla replacement");
    var modExport = Path.Combine(root, "export.co2"); vault.Export(modSave, modExport);
    Check(File.ReadAllBytes(modExport).SequenceEqual(Fixture(5)), "Mod export preserves bytes");
    using (var locked = new FileStream(modPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        Reject(() => vault.Capture(modPath, mod, "Locked"), "Reject locked save files");
    var gameDir = Path.Combine(root, "fake-game"); Directory.CreateDirectory(gameDir);
    File.WriteAllText(Path.Combine(gameDir, "DarkSoulsIII.exe"), "not an executable");
    File.WriteAllText(Path.Combine(gameDir, "config.ini"), "original");
    var manager = new ModManager(new(game, gameDir));
    string Archive(string filename, params (string path, string content)[] files)
    {
        var path = Path.Combine(root, filename);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var file in files) { using var writer = new StreamWriter(zip.CreateEntry(file.path).Open()); writer.Write(file.content); }
        return path;
    }
    var package = Archive("mod.zip", ("config.ini", "new config"), ("mods/example.txt", "mod payload"));
    var record = manager.Install(package, "Synthetic Mod", "1.2.3");
    Check(File.ReadAllText(Path.Combine(gameDir, "config.ini")) == "new config", "Mod installation writes planned files");
    Check(manager.Inventory().Any(i => i.ManagedId == record.Id && i.Version == "1.2.3"), "Mod inventory retains version and ownership");
    Reject(() => manager.Install(package, "Conflict", "2"), "Reject overlapping mod ownership");
    File.WriteAllText(Path.Combine(gameDir, "config.ini"), "user edited");
    Reject(() => manager.Remove(record.Id), "Preserve user-edited mod files on uninstall");
    Check(File.Exists(Path.Combine(gameDir, "mods/example.txt")), "Uninstall conflict is detected before deleting any file");
    File.WriteAllBytes(Path.Combine(gameDir, "config.ini"), File.ReadAllBytes(Path.Combine(gameDir, ".bonfire-mods", record.Id, "new", "config.ini")));
    manager.Remove(record.Id);
    Check(File.ReadAllText(Path.Combine(gameDir, "config.ini")) == "original", "Mod uninstall restores original bytes");
    Check(!File.Exists(Path.Combine(gameDir, "mods/example.txt")), "Mod uninstall removes only tracked new files");
    Reject(() => manager.Preview(Archive("traversal.zip", ("../escape.txt", "bad"))), "Reject ZIP path traversal");
    Reject(() => manager.Preview(Archive("game-replace.zip", ("DarkSoulsIII.exe", "bad"))), "Reject overwriting game executable");
    Reject(() => manager.Preview(Archive("saves.zip", ("DS30000.sl2", "bad"))), "Reject save files in mod package");
    Reject(() => manager.Preview(Archive("duplicate.zip", ("a.ini", "a"), ("A.ini", "b"))), "Reject case-insensitive duplicate ZIP paths");
    Check(SaveInspector.Inspect(Fixture(1), game).Characters.Count == 0, "Malformed parser input degrades without throwing");
    var rosterFile = new byte[64 + 11 * 32 + 10 * 32 + 7000]; "BND4"u8.CopyTo(rosterFile);
    void Put32(byte[] data, int at, uint value) => System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(at, 4), value);
    Put32(rosterFile, 12, 11);
    int cursor = 64 + 11 * 32;
    for (int i = 0; i < 11; i++)
    {
        int size = i == 10 ? 7000 : 32; Put32(rosterFile, 64 + 32 * i + 8, (uint)size); Put32(rosterFile, 64 + 32 * i + 16, (uint)cursor);
        if (i == 10)
        {
            rosterFile[cursor + 356] = 1; System.Text.Encoding.Unicode.GetBytes("TestHero").CopyTo(rosterFile, cursor + 366);
            Put32(rosterFile, cursor + 400, 55); Put32(rosterFile, cursor + 404, 3660);
            rosterFile[cursor + 366 + 49] = 30; rosterFile[cursor + 366 + 48] = 5;
        }
        System.Security.Cryptography.MD5.HashData(rosterFile.AsSpan(cursor + 16, size - 16)).CopyTo(rosterFile, cursor); cursor += size;
    }
    var parsed = SaveInspector.Inspect(rosterFile, Game.ById("er"));
    Check(parsed.Characters.Count == 1 && parsed.Characters[0].Name == "TestHero" && parsed.Characters[0].Level == "55", "Parse Elden Ring roster using verified summary structure");
    Check(parsed.Characters[0].PlayTime == "1时 1分" && parsed.Characters[0].Location == "地图 m30_05_00_00", "Parse playtime and map ID");
    rosterFile[^1] = 1;
    Check(SaveInspector.Inspect(rosterFile, Game.ById("er")).Characters.Count == 0, "Reject parser entries with invalid internal checksum");
    if (args.Length == 2 && args[0] == "--cloud")
    {
        var cloud = new GitHubVault(args[1]);
        try { await new GitHubVault("octocat/Hello-World").CheckPrivate(); throw new Exception("Public repository unexpectedly accepted"); }
        catch (IOException) { Check(true, "Reject public repository before transfer"); }
        var current = vault.Capture(source, game, "Synthetic integration test");
        await cloud.Upload(vault, current); await cloud.Upload(vault, current);
        var entries = await cloud.List(); var entry = entries.Single(c => c.Id == current.Id);
        var downloadedVault = new Vault(Path.Combine(root, "downloaded"));
        var downloaded = await cloud.Download(downloadedVault, entry);
        Check(downloadedVault.Read(downloaded).SequenceEqual(Fixture(2)), "Private GitHub upload/download round-trip");
        var syncVault = new Vault(Path.Combine(root, "synced"));
        var synced = await cloud.Sync(syncVault);
        Check(synced.Downloaded >= 1 && syncVault.Read(current).SequenceEqual(Fixture(2)), "Sync downloads missing snapshots without replacing game saves");
        var repeated = await cloud.Sync(syncVault);
        Check(repeated.Uploaded == 0 && repeated.Downloaded == 0, "Repeated sync is idempotent");
        using var doc = System.Text.Json.JsonDocument.Parse(await GitHubVault.RunGh(["api", $"repos/{args[1]}/contents/{entry.Path}"]));
        var body = System.Text.Json.JsonSerializer.Serialize(new { message = "Remove synthetic integration fixture", sha = doc.RootElement.GetProperty("sha").GetString() });
        await GitHubVault.RunGh(["api", "--method", "DELETE", $"repos/{args[1]}/contents/{entry.Path}", "--input", "-"], body);
        Console.WriteLine("Synthetic cloud fixture removed from current branch.");
    }
    Console.WriteLine($"{passed} checks passed.");
}
finally { Directory.Delete(root, true); }
