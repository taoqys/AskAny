using System.Diagnostics;
using Microsoft.Win32;

namespace AskAny.Services;

public static class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "AskAny";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
            return key?.GetValue(ValueName) is string value &&
                   !string.IsNullOrWhiteSpace(value);
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    public static bool SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
            if (key is null)
            {
                return false;
            }

            if (enabled)
            {
                key.SetValue(ValueName, BuildStartupCommand(), RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(ValueName, false);
            }

            return true;
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or
            System.Security.SecurityException or
            IOException)
        {
            return false;
        }
    }

    private static string BuildStartupCommand()
    {
        var executablePath =
            Process.GetCurrentProcess().MainModule?.FileName ??
            Environment.ProcessPath ??
            string.Empty;
        return string.IsNullOrWhiteSpace(executablePath)
            ? string.Empty
            : $"\"{executablePath}\" --startup";
    }
}
