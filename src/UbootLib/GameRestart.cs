using System;
using System.Diagnostics;
using System.IO;
using BepInEx;

namespace UbootLib;

/// <summary>
/// Manual restart used by the mod manager: mods are loaded as assemblies and cannot be
/// unloaded, so enable/disable changes only take effect on a fresh process.
///
/// The relaunch is delegated to a detached PowerShell that waits for this process to
/// exit and then opens Steam's `steam://rungameid/&lt;AppID&gt;` URL. Starting the game before
/// the old process is gone makes Steam focus the running instance instead of launching
/// a new one, hence the wait. A direct exe launch is kept as a fallback.
/// </summary>
internal static class GameRestart
{
    private const string SteamAppId = "490920";

    public static bool TryRestart()
    {
        try
        {
            var root = Paths.GameRootPath;
            var exe = Path.Combine(root, "Wolfpack.exe");
            var pid = Environment.ProcessId;

            var script =
                $"Wait-Process -Id {pid} -Timeout 60 -ErrorAction SilentlyContinue; " +
                "Start-Sleep -Seconds 2; " +
                $"try {{ Start-Process 'steam://rungameid/{SteamAppId}' }} catch {{ Start-Process '{Escape(exe)}' -WorkingDirectory '{Escape(root)}' }}";

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -NonInteractive -WindowStyle Hidden -Command \"" + script + "\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = root
            };

            using (var p = Process.Start(psi))
            {
                if (p == null) throw new InvalidOperationException("relaunch helper did not start");
            }

            UbootLibPlugin.KitLog.LogInfo($"[restart] relaunch scheduled after exit (steam app {SteamAppId}, pid {pid})");
            ModLoader.Shutdown();
            UnityEngine.Application.Quit();
            return true;
        }
        catch (Exception e)
        {
            UbootLibPlugin.KitLog.LogError($"[restart] failed: {e.GetType().Name}: {e.Message}");
            return false;
        }
    }

    private static string Escape(string value) => (value ?? "").Replace("'", "''");
}
