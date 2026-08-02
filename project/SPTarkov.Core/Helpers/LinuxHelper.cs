using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using SPTarkov.Core.Configuration;
using SPTarkov.Core.SPT;

namespace SPTarkov.Core.Helpers;

public class LinuxHelper(ILogger<LinuxHelper> logger, ConfigHelper configHelper)
{
    /// <summary>
    /// reconstruct path used when installing EFT on linux to work on linux, using symlinks in dosdevice in the winePrefix
    /// </summary>
    /// <param name="windowsLikePath"></param>
    /// <returns></returns>
    public string FixWithPrefixValidation(string? windowsLikePath)
    {
        var pathAndDrive = windowsLikePath?.Replace(@"\\", "/").Split(":");
        var s = Path.Join(
            configHelper.GetConfig().LinuxSettings.PrefixPath,
            "dosdevices",
            $"{pathAndDrive![0].ToLower()}:", // [0] is drive letter.
            pathAndDrive[1] // [1] path to game on that drive
        );
        return s;
    }

    /// <summary>
    /// Runs an executable or Wine tool (<c>winecfg</c>, <c>winetricks</c>, <c>regedit</c>, etc.) inside the configured Wine/Proton
    /// prefix via <c>umu-run</c>.
    /// </summary>
    /// <example>
    /// <code>
    /// RunInPrefix("EscapeFromTarkov.exe", args);           // launch any executable in the current working dir
    /// RunInPrefix("winecfg");                              // open the winecfg menu
    /// RunInPrefix("winetricks", ["-q", "win11"]);          // set the prefix's Windows version to Windows 11
    /// RunInPrefix("winetricks", ["-q", "dotnetdesktop9"]); // install .NET Desktop 9
    /// RunInPrefix("regedit");                              // open the regedit tool
    /// </code>
    /// </example>
    public bool RunInPrefix(string cmd = "", List<string>? args = null)
    {
        // This looks something like: "/home/{username}/Games/tarkov"
        // However this could be anything the user sets it too when they use MadBytes script.
        var prefixPath = configHelper.GetConfig().LinuxSettings.PrefixPath;

        // This looks something like this: "/home/{username}/.local/bin/umu-run"
        var umuPath = configHelper.GetConfig().LinuxSettings.UmuPath;

        // this looks something like this: "GE-Proton10-24"
        var proton = configHelper.GetConfig().LinuxSettings.ProtonVersion;

        if (string.IsNullOrEmpty(prefixPath) || string.IsNullOrEmpty(umuPath) || string.IsNullOrEmpty(proton))
        {
            logger.LogError("Prefix path or umu path or proton version are required");
            return false;
        }

        // this looks something like: "/home/{username}/Games/tarkov/drive_c/SPTarkov"
        var sptPath = configHelper.GetConfig().GamePath;

        ProcessStartInfo? process;

        // I don't know if this actually helps in any way, but some use it
        // User must install gamemode from package manager, try catch below will log it
        if (configHelper.GetConfig().LinuxSettings.GameMode)
        {
            process = new ProcessStartInfo
            {
                FileName = "gamemoderun",
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = sptPath,
                Environment = { { "WINEPREFIX", prefixPath }, { "PROTONPATH", proton } },
                ArgumentList = { umuPath, cmd },
            };
        }
        else
        {
            process = new ProcessStartInfo
            {
                FileName = "python3",
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = sptPath,
                Environment = { { "WINEPREFIX", prefixPath }, { "PROTONPATH", proton } },
                ArgumentList = { umuPath, cmd },
            };
        }

        // Add these individually so they are not wrapped in ""
        if (args != null)
        {
            foreach (var arg in args)
            {
                process.ArgumentList.Add(arg);
            }
        }

        var launchSettings = ParseLaunchSettings();
        foreach (var launchSetting in launchSettings)
        {
            if (launchSetting.Key.StartsWith("-"))
            {
                // This should be an argument
                process.ArgumentList.Add(
                    string.IsNullOrWhiteSpace(launchSetting.Value) ? launchSetting.Key : $"{launchSetting.Key}={launchSetting.Value}"
                );
            }
            else
            {
                // This should be an environment variable
                process.Environment.Add(launchSetting.Key, launchSetting.Value);
            }
        }

        try
        {
            Process.Start(process);
            logger.LogInformation("Game process started on linux");
        }
        catch (Exception ex)
        {
            logger.LogError("Starting game process failed: {Exception}", ex);
            return false;
        }

        return true;
    }

    private Dictionary<string, string> ParseLaunchSettings()
    {
        var launchSettings = configHelper.GetConfig().LinuxSettings.LaunchSettings;
        var result = new Dictionary<string, string>();

        if (string.IsNullOrEmpty(launchSettings))
        {
            return result;
        }

        try
        {
            launchSettings = launchSettings.Trim();
            var tokens = new List<string>();
            var current = new StringBuilder();
            var inQuotes = false;

            // Tokenize the string while respecting quoted values
            foreach (var ch in launchSettings)
            {
                if (ch == '"')
                {
                    inQuotes = !inQuotes;
                    current.Append(ch);
                }
                else if (ch == ' ' && !inQuotes)
                {
                    if (current.Length > 0)
                    {
                        tokens.Add(current.ToString());
                        current.Clear();
                    }
                }
                else
                {
                    current.Append(ch);
                }
            }

            if (current.Length > 0)
            {
                tokens.Add(current.ToString());
            }

            // Parse each token into name and value
            foreach (var token in tokens)
            {
                var eqIndex = token.IndexOf('=');
                var name = eqIndex >= 0 ? token[..eqIndex] : token;
                var value = eqIndex >= 0 ? token[(eqIndex + 1)..] : string.Empty;

                // Remove surrounding quotes if the value is quoted
                if (value.StartsWith('"') && value.EndsWith('"') && value.Length >= 2)
                {
                    value = value[1..^1];
                }

                result.Add(name, value);
            }
        }
        catch (Exception e)
        {
            logger.LogWarning("Unable to parse launch settings of: {setting}, please format correctly: {e}", launchSettings, e);
            return new Dictionary<string, string>();
        }

        return result;
    }

    public Task<List<string>> GetProtonVersions()
    {
        // Should contain things like "GE-Proton10-24" or "GE-Proton10-21"
        // Could be named slightly different if user downloads "custom" ones like "EM-10.0-30"
        if (!Directory.Exists(Paths.ProtonPath))
        {
            logger.LogError("Proton path not found, make sure to run lutris or steam first");
            // we want this to throw an exception, so just log this
        }

        var directoryContents = Directory.GetDirectories(Paths.ProtonPath);
        var listStripped = new List<string>();

        foreach (var directory in directoryContents)
        {
            // remove LegacyRuntime
            if (directory.Contains("LegacyRuntime"))
            {
                continue;
            }

            // split on / and get last
            listStripped.Add(directory.Split("/").Last());
        }

        return Task.FromResult(listStripped);
    }

    [DllImport("libc", EntryPoint = "setenv", SetLastError = true)]
    public static extern int SetEnvironmentVariableNative(string name, string value, int overwrite);
}
