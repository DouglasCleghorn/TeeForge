using System.Text.Json;

namespace TeeForge.Mount;

internal sealed record MountState(
    string Id,
    string ImagePath,
    string MountPoint,
    string ProxyName,
    int ProcessId,
    bool ReadOnly,
    DateTimeOffset StartedAt);

internal static class MountStateStore
{
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TeeForge",
        "mounts");

    internal static string GetPath(string id) => Path.Combine(DirectoryPath, id + ".json");

    internal static string GetErrorPath(string id) => Path.Combine(DirectoryPath, id + ".error.log");

    internal static void Write(MountState state)
    {
        Directory.CreateDirectory(DirectoryPath);
        File.WriteAllText(GetPath(state.Id), JsonSerializer.Serialize(state, Options));
    }

    internal static MountState? Read(string id)
    {
        string path = GetPath(id);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<MountState>(File.ReadAllText(path), Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static IReadOnlyList<MountState> List()
    {
        if (!Directory.Exists(DirectoryPath))
        {
            return [];
        }

        var result = new List<MountState>();
        foreach (string path in Directory.EnumerateFiles(DirectoryPath, "*.json"))
        {
            try
            {
                MountState? state = JsonSerializer.Deserialize<MountState>(File.ReadAllText(path), Options);
                if (state is not null)
                {
                    result.Add(state);
                }
            }
            catch (JsonException)
            {
            }
        }

        return result.OrderBy(static state => state.StartedAt).ToArray();
    }

    internal static void Delete(string id)
    {
        string path = GetPath(id);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        string errorPath = GetErrorPath(id);
        if (File.Exists(errorPath))
        {
            File.Delete(errorPath);
        }
    }

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
}
