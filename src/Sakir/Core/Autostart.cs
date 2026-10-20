using System.Diagnostics;

namespace Sakir.Core;

/// <summary>
/// Login autostart via a Scheduled Task rather than the Run registry key.
///
/// The Run key cannot launch an elevated process without a UAC prompt, and CPU
/// temperature needs elevation - a scheduled task registered with the highest
/// available privileges starts silently at logon.
/// </summary>
public static class Autostart
{
    private const string TaskName = "Sakir";

    public static bool IsEnabled()
    {
        var psi = new ProcessStartInfo("schtasks.exe", $"/Query /TN \"{TaskName}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using Process? p = Process.Start(psi);
        if (p is null) return false;
        p.WaitForExit(4000);
        return p.ExitCode == 0;
    }

    public static void Set(bool enabled)
    {
        if (!enabled)
        {
            Run($"/Delete /TN \"{TaskName}\" /F");
            return;
        }

        string exe = Environment.ProcessPath
                     ?? throw new InvalidOperationException("Cannot determine the executable path.");

        // /RL HIGHEST asks for the highest privileges the account can get, which
        // is what lets the CPU sensor driver load without prompting at logon.
        string args = $"/Create /TN \"{TaskName}\" /TR \"\\\"{exe}\\\" --tray\" /SC ONLOGON /RL HIGHEST /F";
        string err = Run(args);
        if (!string.IsNullOrWhiteSpace(err))
            throw new InvalidOperationException(
                "Could not register the startup task. Creating an elevated task usually "
                + "requires running Sakir as administrator once.\r\n\r\n" + err.Trim());
    }

    private static string Run(string args)
    {
        var psi = new ProcessStartInfo("schtasks.exe", args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using Process? p = Process.Start(psi);
        if (p is null) return "schtasks.exe could not be started.";
        string stderr = p.StandardError.ReadToEnd();
        p.StandardOutput.ReadToEnd();
        p.WaitForExit(6000);
        return p.ExitCode == 0 ? "" : (string.IsNullOrWhiteSpace(stderr) ? $"exit code {p.ExitCode}" : stderr);
    }
}
