using Microsoft.Win32;

namespace TeeForge.Mount;

internal static class ShellIntegration
{
    private static readonly string[] Extensions = [".tfdisk", ".tfdiff"];

    internal static void Install()
    {
        string executable = GetExecutablePath();
        foreach (string extension in Extensions)
        {
            WriteVerb(extension, "TeeForge.Inspect", "Inspect TeeForge disk", executable, "inspect");
            WriteVerb(extension, "TeeForge.Mount", "Mount with TeeForge", executable, "mount");
            WriteVerb(
                extension,
                "TeeForge.MountReadOnly",
                "Mount read-only with TeeForge",
                executable,
                "mount",
                "--read-only");
        }
    }

    internal static void Uninstall()
    {
        foreach (string extension in Extensions)
        {
            string shellPath = GetShellPath(extension);
            Registry.CurrentUser.DeleteSubKeyTree(shellPath + "\\TeeForge.Inspect", throwOnMissingSubKey: false);
            Registry.CurrentUser.DeleteSubKeyTree(shellPath + "\\TeeForge.Mount", throwOnMissingSubKey: false);
            Registry.CurrentUser.DeleteSubKeyTree(shellPath + "\\TeeForge.MountReadOnly", throwOnMissingSubKey: false);
        }
    }

    private static void WriteVerb(
        string extension,
        string keyName,
        string title,
        string executable,
        string command,
        string? option = null)
    {
        string verbPath = GetShellPath(extension) + "\\" + keyName;
        using RegistryKey verb = Registry.CurrentUser.CreateSubKey(verbPath, writable: true);
        verb.SetValue(string.Empty, title, RegistryValueKind.String);
        using RegistryKey commandKey = verb.CreateSubKey("command", writable: true);
        string commandLine = $"\"{executable}\" {command} \"%1\"";
        if (option is not null)
        {
            commandLine += " " + option;
        }

        commandKey.SetValue(string.Empty, commandLine, RegistryValueKind.String);
    }

    private static string GetShellPath(string extension) =>
        "Software\\Classes\\SystemFileAssociations\\" + extension + "\\shell";

    private static string GetExecutablePath()
    {
        string executable = Path.ChangeExtension(typeof(ShellIntegration).Assembly.Location, ".exe");
        if (!File.Exists(executable))
        {
            throw new FileNotFoundException(
                "Shell integration requires an apphost build or published teeforge-mount.exe.",
                executable);
        }

        return executable;
    }
}
