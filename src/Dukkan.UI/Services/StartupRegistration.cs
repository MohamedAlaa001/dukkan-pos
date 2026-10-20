using Microsoft.Win32;

namespace Dukkan.UI;

/// <summary>Per-user "start with Windows" entry (HKCU Run key; no admin rights needed).</summary>
public static class StartupRegistration
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "Dukkan";

    public static bool IsEnabled
    {
        get
        {
            try { using var k = Registry.CurrentUser.OpenSubKey(RunKey); return k?.GetValue(ValueName) is string; }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException) { return false; }
        }
    }

    public static void Set(bool enabled)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled && Environment.ProcessPath is { } exe) k.SetValue(ValueName, "\"" + exe + "\"");
            else k.DeleteValue(ValueName, false);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException) { }
    }
}
