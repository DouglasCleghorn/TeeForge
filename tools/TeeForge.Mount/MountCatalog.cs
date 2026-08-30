using System.Text.Json;

namespace TeeForge.Mount;

internal static class MountCatalog
{
    private static readonly string StateDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TeeForge");
    private static readonly string CatalogPath = Path.Combine(StateDirectory, "disk-catalog.json");

    internal static void Remember(Guid id, string path)
    {
        Directory.CreateDirectory(StateDirectory);
        Dictionary<Guid, string> catalog = Read();
        catalog[id] = Path.GetFullPath(path);
        File.WriteAllText(CatalogPath, JsonSerializer.Serialize(catalog, JsonOptions));
    }

    internal static string? TryResolve(Guid id)
    {
        Dictionary<Guid, string> catalog = Read();
        return catalog.TryGetValue(id, out string? path) && File.Exists(path) ? path : null;
    }

    private static Dictionary<Guid, string> Read()
    {
        if (!File.Exists(CatalogPath))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<Guid, string>>(File.ReadAllText(CatalogPath), JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
}
