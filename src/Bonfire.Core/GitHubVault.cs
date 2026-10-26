using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Bonfire.Core;

public record CloudSave(string Id, string Path, long Size, string BlobSha)
{
    public string Display
    {
        get
        {
            var match = Regex.Match(Path, @"snapshots/(?<date>\d{8}-\d{6})_(?<game>[a-z0-9~_-]+)_[a-f0-9]{32}\.zip");
            if (!match.Success) return Path;
            var date = DateTime.ParseExact(match.Groups["date"].Value, "yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal).ToLocalTime();
            return $"{date:yyyy-MM-dd HH:mm:ss}  ·  {Game.ById(match.Groups["game"].Value).Name}  ·  {Id[..8]}";
        }
    }
}

public sealed class GitHubVault(string repository)
{
    public string Repository { get; } = NormalizeRepository(repository);
    public static string NormalizeRepository(string value)
    {
        var name = value.Trim().TrimEnd('/');
        if (name.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase)) name = name[19..];
        else if (name.StartsWith("git@github.com:", StringComparison.OrdinalIgnoreCase)) name = name[15..];
        if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        if (!Regex.IsMatch(name, @"\A[A-Za-z0-9-]+/[A-Za-z0-9_.-]+\z") || name.Split('/')[1] is "." or "..")
            throw new ArgumentException("请输入 GitHub 仓库链接、SSH 地址或 owner/repository。仅支持 github.com。");
        return name;
    }

    public static async Task<byte[]> RunGh(string[] args, string? input = null)
    {
        var start = new ProcessStartInfo("gh") { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        if (args[0] == "api") { start.ArgumentList.Add("--hostname"); start.ArgumentList.Add("github.com"); }
        start.Environment["GH_PROMPT_DISABLED"] = "1";
        using var process = Process.Start(start) ?? throw new IOException("无法启动 GitHub CLI，请安装 gh 并登录。");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using var output = new MemoryStream();
        var copy = process.StandardOutput.BaseStream.CopyToAsync(output, timeout.Token);
        var errors = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            if (input != null) await process.StandardInput.WriteAsync(input.AsMemory(), timeout.Token);
            process.StandardInput.Close();
            await Task.WhenAll(process.WaitForExitAsync(timeout.Token), copy);
            var error = await errors;
            if (process.ExitCode != 0) throw new IOException("GitHub 操作失败：" + error.Trim());
            return output.ToArray();
        }
        catch (OperationCanceledException) { if (!process.HasExited) process.Kill(true); throw new IOException("GitHub 请求超时，请检查网络后重试。"); }
    }
    public async Task CheckPrivate()
    {
        var result = await RunGh(["api", $"repos/{Repository}"]);
        using var doc = JsonDocument.Parse(result);
        if (!doc.RootElement.GetProperty("private").GetBoolean()) throw new IOException("该仓库不是私有仓库，已拒绝传输存档。");
    }
    public async Task<(int Uploaded, int Downloaded)> Sync(Vault vault, IProgress<string>? progress = null)
    {
        await CheckPrivate();
        var remote = await List();
        var local = vault.List();
        var localIds = local.Select(s => s.Id).ToHashSet();
        var remoteIds = remote.Select(s => s.Id).ToHashSet();
        int uploaded = 0, downloaded = 0;
        // Append-only synchronization never deletes snapshots or touches live game saves.
        foreach (var item in remote.Where(s => !localIds.Contains(s.Id)))
        {
            progress?.Report($"下载备份 {downloaded + 1}：{item.Display}");
            await Download(vault, item);
            downloaded++;
        }
        foreach (var item in local.Where(s => !remoteIds.Contains(s.Id)))
        {
            progress?.Report($"上传备份 {uploaded + 1}：{item.Label}");
            await Upload(vault, item);
            uploaded++;
        }
        return (uploaded, downloaded);
    }
    public async Task CreatePrivate()
    {
        await RunGh(["repo", "create", Repository, "--private", "--description", "Private Bonfire Save backups", "--add-readme"]);
        await CheckPrivate();
    }
    public async Task<List<CloudSave>> List()
    {
        await CheckPrivate();
        using var repo = JsonDocument.Parse(await RunGh(["api", $"repos/{Repository}"]));
        var branch = repo.RootElement.GetProperty("default_branch").GetString()!;
        using var doc = JsonDocument.Parse(await RunGh(["api", $"repos/{Repository}/git/trees/{Uri.EscapeDataString(branch)}?recursive=1"]));
        if (doc.RootElement.TryGetProperty("truncated", out var truncated) && truncated.GetBoolean()) throw new IOException("仓库文件过多，无法完整读取列表。");
        var result = new List<CloudSave>();
        foreach (var entry in doc.RootElement.GetProperty("tree").EnumerateArray())
        {
            var path = entry.GetProperty("path").GetString()!;
            var match = Regex.Match(path, @"\Asnapshots/\d{8}-\d{6}_[a-z0-9~_-]+_(?<id>[a-f0-9]{32})\.zip\z");
            if (entry.GetProperty("type").GetString() != "blob" || !match.Success) continue;
            result.Add(new(match.Groups["id"].Value, path, entry.GetProperty("size").GetInt64(), entry.GetProperty("sha").GetString()!));
        }
        return result.OrderByDescending(s => s.Path).ToList();
    }
    public async Task Upload(Vault vault, Snapshot item)
    {
        await CheckPrivate();
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open(), new UTF8Encoding(false))) writer.Write(JsonSerializer.Serialize(item, Vault.Json));
            using var data = zip.CreateEntry("save.sl2", CompressionLevel.Optimal).Open();
            data.Write(vault.Read(item));
        }
        var path = $"snapshots/{item.CreatedAt.UtcDateTime:yyyyMMdd-HHmmss}_{item.GameId}_{item.Id}.zip";
        // Same snapshot IDs are immutable; repeated uploads verify the existing content.
        var previous = (await List()).FirstOrDefault(s => s.Id == item.Id);
        if (previous != null)
        {
            var existing = await ReadCloud(previous);
            if (existing.Item1 != item || Vault.Hash(existing.Item2) != item.Sha256) throw new IOException("云端存在相同 ID 的不同内容。");
            return;
        }
        var body = JsonSerializer.Serialize(new { message = "Back up save " + item.Id, content = Convert.ToBase64String(memory.ToArray()) });
        await RunGh(["api", "--method", "PUT", $"repos/{Repository}/contents/{path}", "--input", "-"], body);
    }
    private async Task<(Snapshot, byte[])> ReadCloud(CloudSave cloud)
    {
        if (!Regex.IsMatch(cloud.Id, @"\A[a-f0-9]{32}\z") || !Regex.IsMatch(cloud.Path, @"\Asnapshots/\d{8}-\d{6}_[a-z0-9~_-]+_" + cloud.Id + @"\.zip\z")) throw new InvalidDataException("云端路径无效。");
        if (cloud.Size > Vault.MaxSaveSize + 1024 * 1024) throw new InvalidDataException("云端备份过大。");
        if (!Regex.IsMatch(cloud.BlobSha, @"\A[a-f0-9]{40}\z")) throw new InvalidDataException("云端 Blob SHA 无效。");
        using var blob = JsonDocument.Parse(await RunGh(["api", $"repos/{Repository}/git/blobs/{cloud.BlobSha}"]));
        if (blob.RootElement.GetProperty("encoding").GetString() != "base64") throw new InvalidDataException("云端文件编码不受支持。");
        var bytes = Convert.FromBase64String(blob.RootElement.GetProperty("content").GetString()!);
        if (bytes.Length > Vault.MaxSaveSize + 1024 * 1024) throw new InvalidDataException("云端备份过大。");
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        if (zip.Entries.Count != 2) throw new InvalidDataException("云端备份包结构无效。");
        var manifest = zip.GetEntry("manifest.json") ?? throw new InvalidDataException("缺少存档清单。");
        var save = zip.GetEntry("save.sl2") ?? throw new InvalidDataException("缺少存档文件。");
        if (manifest.Length > 65536 || save.Length > Vault.MaxSaveSize) throw new InvalidDataException("备份包内容过大。");
        using var reader = new StreamReader(manifest.Open());
        var item = JsonSerializer.Deserialize<Snapshot>(await reader.ReadToEndAsync()) ?? throw new InvalidDataException("无效清单。");
        if (item.Id != cloud.Id) throw new InvalidDataException("云端清单 ID 不匹配。");
        using var content = save.Open();
        var data = new byte[checked((int)save.Length)]; content.ReadExactly(data);
        Vault.Validate(data);
        if (item.Size != data.LongLength || item.Sha256 != Vault.Hash(data)) throw new InvalidDataException("云端存档 SHA-256 校验失败。");
        return (item, data);
    }
    public async Task<Snapshot> Download(Vault vault, CloudSave cloud)
    {
        await CheckPrivate();
        var (item, data) = await ReadCloud(cloud);
        vault.Store(item, data); return item;
    }
}
