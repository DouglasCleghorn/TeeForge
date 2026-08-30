using System.Diagnostics;
using TeeForge.Sparse;

namespace TeeForge.Mount;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length == 0)
            {
                PrintUsage();
                return 2;
            }

            return args[0].ToLowerInvariant() switch
            {
                "--help" or "-h" or "help" => Help(),
                "inspect" => Inspect(args),
                "mount" => await MountAsync(args).ConfigureAwait(false),
                "unmount" => await UnmountAsync(args).ConfigureAwait(false),
                "list" => List(),
                "status" => Status(args),
                "shell" => Shell(args),
                "broker" => await BrokerAsync(args).ConfigureAwait(false),
                _ => UnknownCommand(args[0]),
            };
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            string? errorFile = GetOption(args, "--error-file");
            if (errorFile is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(errorFile)!);
                File.WriteAllText(errorFile, exception.ToString());
            }

            return 1;
        }
    }

    private static int Inspect(string[] args)
    {
        if (args.Length < 2)
        {
            throw new ArgumentException("inspect requires an image path.");
        }

        bool readOnly = !args.Contains("--writable", StringComparer.OrdinalIgnoreCase);
        string? parent = GetOption(args, "--parent");
        using DiskImageSession image = DiskImageSession.Open(args[1], readOnly, parent);
        MountCatalog.Remember(image.VirtualDisk.Id, image.ImagePath);
        Console.WriteLine($"Path: {image.ImagePath}");
        Console.WriteLine($"Type: {image.LogicalStream.GetType().Name}");
        Console.WriteLine($"Id: {image.VirtualDisk.Id}");
        Console.WriteLine($"DataWriteId: {image.VirtualDisk.DataWriteId}");
        Console.WriteLine($"BlockSize: {image.VirtualDisk.BlockSize}");
        Console.WriteLine($"VirtualCapacity: {image.VirtualDisk.VirtualCapacity}");
        Console.WriteLine($"Length: {image.LogicalStream.Length}");
        if (image.LogicalStream is DifferencingStream difference)
        {
            Console.WriteLine($"BaseId: {difference.BaseId}");
            Console.WriteLine($"BaseDataWriteId: {difference.BaseDataWriteId}");
            Console.WriteLine($"ParentPathHint: {difference.ParentPathHint ?? "(none)"}");
        }

        return 0;
    }

    private static async Task<int> MountAsync(string[] args)
    {
        if (args.Length < 2)
        {
            throw new ArgumentException("mount requires an image path.");
        }

        string imdisk = FindImDisk();
        string imagePath = Path.GetFullPath(args[1]);
        string mountPoint = GetOption(args, "--mount-point") is string requestedMountPoint
            ? NormalizeMountPoint(requestedMountPoint)
            : FindAvailableMountPoint();
        string? parent = GetOption(args, "--parent");
        bool readOnly = args.Contains("--read-only", StringComparer.OrdinalIgnoreCase);
        string id = Guid.NewGuid().ToString("N");
        string proxyName = "TeeForge_" + id;
        string errorPath = MountStateStore.GetErrorPath(id);
        if (File.Exists(errorPath))
        {
            File.Delete(errorPath);
        }

        string executable = Environment.ProcessPath ??
            throw new InvalidOperationException("Could not locate the current executable.");

        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("broker");
        start.ArgumentList.Add("--id");
        start.ArgumentList.Add(id);
        start.ArgumentList.Add("--image");
        start.ArgumentList.Add(imagePath);
        start.ArgumentList.Add("--mount-point");
        start.ArgumentList.Add(mountPoint);
        start.ArgumentList.Add("--proxy");
        start.ArgumentList.Add(proxyName);
        start.ArgumentList.Add("--imdisk");
        start.ArgumentList.Add(imdisk);
        start.ArgumentList.Add("--error-file");
        start.ArgumentList.Add(errorPath);
        if (readOnly)
        {
            start.ArgumentList.Add("--read-only");
        }

        if (parent is not null)
        {
            start.ArgumentList.Add("--parent");
            start.ArgumentList.Add(Path.GetFullPath(parent));
        }

        Process broker = Process.Start(start) ?? throw new InvalidOperationException("Could not start the mount broker.");
        string statePath = MountStateStore.GetPath(id);
        for (int attempt = 0; attempt < 100; attempt++)
        {
            if (File.Exists(statePath))
            {
                MountState state = MountStateStore.Read(id)!;
                Console.WriteLine($"Mounted {state.ImagePath} at {state.MountPoint} (id {state.Id}).");
                return 0;
            }

            if (broker.HasExited)
            {
                string detail = File.Exists(errorPath) ? Environment.NewLine + File.ReadAllText(errorPath) : string.Empty;
                throw new InvalidOperationException($"Mount broker exited with code {broker.ExitCode}.{detail}");
            }

            await Task.Delay(100).ConfigureAwait(false);
        }

        throw new TimeoutException("Timed out waiting for the mount broker.");
    }

    private static async Task<int> BrokerAsync(string[] args)
    {
        string id = RequireOption(args, "--id");
        string imagePath = RequireOption(args, "--image");
        string mountPoint = RequireOption(args, "--mount-point");
        string proxy = RequireOption(args, "--proxy");
        string imdisk = RequireOption(args, "--imdisk");
        string? parent = GetOption(args, "--parent");
        bool readOnly = args.Contains("--read-only", StringComparer.OrdinalIgnoreCase);
        await using DiskImageSession image = DiskImageSession.Open(imagePath, readOnly, parent);
        MountCatalog.Remember(image.VirtualDisk.Id, image.ImagePath);
        using var server = new ImDiskProxyServer(proxy, image, readOnly);

        var imdiskStart = new ProcessStartInfo(imdisk)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string argument in new[]
        {
            "-a", "-t", "proxy", "-o", readOnly ? "shm,ro" : "shm",
            "-f", proxy, "-m", mountPoint, "-S", "4096",
        })
        {
            imdiskStart.ArgumentList.Add(argument);
        }

        using Process imdiskProcess = Process.Start(imdiskStart) ??
            throw new InvalidOperationException("Could not start imdisk.exe.");
        await imdiskProcess.WaitForExitAsync().ConfigureAwait(false);
        if (imdiskProcess.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"imdisk.exe failed with code {imdiskProcess.ExitCode}. Run from an elevated terminal and verify the driver is installed.");
        }

        var state = new MountState(
            id,
            image.ImagePath,
            mountPoint,
            proxy,
            Environment.ProcessId,
            readOnly,
            DateTimeOffset.UtcNow);
        MountStateStore.Write(state);
        try
        {
            await server.RunAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            MountStateStore.Delete(id);
        }

        return 0;
    }

    private static async Task<int> UnmountAsync(string[] args)
    {
        if (args.Length < 2)
        {
            throw new ArgumentException("unmount requires a mount id or mount point.");
        }

        MountState? state = MountStateStore.Read(args[1]) ?? MountStateStore.List()
            .FirstOrDefault(item => item.MountPoint.Equals(args[1], StringComparison.OrdinalIgnoreCase));
        if (state is null)
        {
            throw new InvalidOperationException("No matching TeeForge mount was found.");
        }

        string imdisk = FindImDisk();
        var start = new ProcessStartInfo(imdisk)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("-d");
        start.ArgumentList.Add("-m");
        start.ArgumentList.Add(state.MountPoint);
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("Could not start imdisk.exe.");
        await process.WaitForExitAsync().ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"imdisk.exe failed with code {process.ExitCode}.");
        }

        MountStateStore.Delete(state.Id);
        Console.WriteLine($"Unmounted {state.MountPoint}.");
        return 0;
    }

    private static int List()
    {
        foreach (MountState state in MountStateStore.List())
        {
            Console.WriteLine($"{state.Id}  {state.MountPoint,-4}  {(state.ReadOnly ? "ro" : "rw"),2}  {state.ImagePath}");
        }

        return 0;
    }

    private static int Status(string[] args)
    {
        if (args.Length < 2)
        {
            return List();
        }

        MountState? state = MountStateStore.Read(args[1]);
        if (state is null)
        {
            Console.WriteLine("not found");
            return 1;
        }

        bool running;
        try
        {
            running = !Process.GetProcessById(state.ProcessId).HasExited;
        }
        catch (ArgumentException)
        {
            running = false;
        }

        Console.WriteLine(running ? "mounted" : "stale");
        Console.WriteLine($"Image: {state.ImagePath}");
        Console.WriteLine($"MountPoint: {state.MountPoint}");
        Console.WriteLine($"ReadOnly: {state.ReadOnly}");
        Console.WriteLine($"BrokerPid: {state.ProcessId}");
        return running ? 0 : 1;
    }

    private static string FindImDisk()
    {
        string? path = Environment.GetEnvironmentVariable("PATH");
        foreach (string directory in (path ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(directory.Trim('"'), "imdisk.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException(
            "imdisk.exe was not found. Install ImDisk explicitly and add its directory to PATH; TeeForge does not download or install drivers.");
    }

    private static string RequireOption(string[] args, string option) =>
        GetOption(args, option) ?? throw new ArgumentException($"Missing required option {option}.");

    private static string NormalizeMountPoint(string mountPoint)
    {
        if (mountPoint.Length != 2 || mountPoint[1] != ':' || !char.IsAsciiLetter(mountPoint[0]))
        {
            throw new ArgumentException("The first mount implementation requires an explicit drive letter such as T:.");
        }

        string normalized = string.Create(
            2,
            mountPoint,
            static (destination, value) =>
            {
                destination[0] = char.ToUpperInvariant(value[0]);
                destination[1] = ':';
            });
        if (Directory.Exists(normalized + Path.DirectorySeparatorChar))
        {
            throw new IOException($"Drive {normalized} is already in use.");
        }

        return normalized;
    }

    private static string FindAvailableMountPoint()
    {
        HashSet<char> used = DriveInfo.GetDrives()
            .Select(static drive => char.ToUpperInvariant(drive.Name[0]))
            .ToHashSet();
        for (char letter = 'Z'; letter >= 'D'; letter--)
        {
            if (!used.Contains(letter))
            {
                return string.Concat(letter, ':');
            }
        }

        throw new IOException("No unused drive letter from D: through Z: is available.");
    }

    private static int Shell(string[] args)
    {
        if (args.Length != 2)
        {
            throw new ArgumentException("shell requires install or uninstall.");
        }

        switch (args[1].ToLowerInvariant())
        {
            case "install":
                ShellIntegration.Install();
                Console.WriteLine("Installed TeeForge per-user shell verbs for .tfdisk and .tfdiff.");
                return 0;
            case "uninstall":
                ShellIntegration.Uninstall();
                Console.WriteLine("Removed TeeForge per-user shell verbs.");
                return 0;
            default:
                throw new ArgumentException("shell requires install or uninstall.");
        }
    }

    private static string? GetOption(string[] args, string option)
    {
        for (int index = 0; index + 1 < args.Length; index++)
        {
            if (args[index].Equals(option, StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }

        return null;
    }

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"Unknown command '{command}'.");
        PrintUsage();
        return 2;
    }

    private static int Help()
    {
        PrintUsage();
        return 0;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("TeeForge mount broker");
        Console.WriteLine("  teeforge-mount inspect <image> [--parent <path>]");
        Console.WriteLine("  teeforge-mount mount <image> [--mount-point X:] [--parent <path>] [--read-only]");
        Console.WriteLine("  teeforge-mount unmount <id|mount-point>");
        Console.WriteLine("  teeforge-mount list");
        Console.WriteLine("  teeforge-mount status [id]");
        Console.WriteLine("  teeforge-mount shell <install|uninstall>");
    }
}
