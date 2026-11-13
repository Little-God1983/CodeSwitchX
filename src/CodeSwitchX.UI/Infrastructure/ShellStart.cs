using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace CodeSwitchX.UI.Infrastructure;

/// <summary>Starts a program, folder or shortcut the way Explorer would, for the Yard's menus (#423).</summary>
public static class ShellStart
{
    /// <summary>False when it could not be started (not found, refused, a bad path); never throws for that.</summary>
    public static bool TryStart(ProcessStartInfo start)
    {
        start.UseShellExecute = true;
        try
        {
            using var process = Process.Start(start);
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            return false;
        }
    }
}
