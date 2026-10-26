using System.Text.Json;

namespace Bonfire.Core;

public static class DataPaths
{
    private record Location(string DataDirectory);
    public static string Resolve(string applicationDirectory, string localAppData, string? overridePath = null)
    {
        if (!string.IsNullOrWhiteSpace(overridePath)) return Absolute(overridePath);
        var location = Path.Combine(applicationDirectory, "data-location.json");
        if (File.Exists(location))
        {
            var config = JsonSerializer.Deserialize<Location>(File.ReadAllText(location), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (string.IsNullOrWhiteSpace(config?.DataDirectory)) throw new InvalidDataException("data-location.json 中缺少 DataDirectory。");
            return Absolute(config.DataDirectory);
        }
        var legacy = Path.Combine(localAppData, "BonfireSave");
        return File.Exists(Path.Combine(legacy, "settings.json")) ? legacy : Path.Combine(localAppData, "SoulsVault");
    }
    private static string Absolute(string path) => Path.IsPathFullyQualified(path) ? Path.GetFullPath(path) : throw new InvalidDataException("数据目录必须是绝对路径。");
}
