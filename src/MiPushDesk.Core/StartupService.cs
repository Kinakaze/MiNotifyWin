using Microsoft.Win32;

namespace MiPushDesk.Core;

public sealed class StartupService(string executable, string registryPath = @"Software\Microsoft\Windows\CurrentVersion\Run")
{
    public const string ValueName = "MiPushDesk";
    public string Command => BuildCommand(executable);
    public static string BuildCommand(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('"') || path.Any(char.IsControl) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("启动程序路径无效。");
        return "\"" + Path.GetFullPath(path) + "\" --tray";
    }
    public bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(registryPath);
            return string.Equals(key?.GetValue(ValueName) as string, Command, StringComparison.OrdinalIgnoreCase);
        }
    }
    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(registryPath);
        if (enabled) key.SetValue(ValueName, Command, RegistryValueKind.String);
        else key.DeleteValue(ValueName, false);
    }
}
