using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mirador.Core.Lighthouse;

/// <summary>SteamVR's settings file couldn't be read as JSON; it was left as it is.</summary>
public sealed class SteamVrSettingsException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// SteamVR's own setting that starts SteamVR when a controller turns on: <c>power.autoLaunchSteamVROnButtonPress</c> in
/// <c>&lt;Steam&gt;\config\steamvr.vrsettings</c>. Every other key is kept as it is. Before a change the file is copied
/// to <c>steamvr.vrsettings.mirador-&lt;yyyyMMdd-HHmmss&gt;</c> in <paramref name="backupDir"/>, and the new file is
/// written aside, then moved over the old one. A file that isn't a JSON object is never written. SteamVR rewrites the
/// file when it exits, so the caller changes it only while SteamVR is closed.
/// </summary>
/// <param name="vrsettingsPath">SteamVR's settings file.</param>
/// <param name="backupDir">Where the copies before a change go.</param>
/// <param name="time">The clock, for the copies' names.</param>
public sealed class SteamVrSettings(string vrsettingsPath, string backupDir, TimeProvider time)
{
    private const string Section = "power";
    private const string Key = "autoLaunchSteamVROnButtonPress";

    /// <summary><c>&lt;Steam&gt;\config\steamvr.vrsettings</c>; null when Steam isn't installed.</summary>
    public static string? DefaultPath()
    {
        try
        {
            return Mirador.Ipc.SteamVr.SteamPathFromRegistry() is { Length: > 0 } steam
                ? Path.GetFullPath(Path.Combine(steam, "config", "steamvr.vrsettings"))
                : null;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The setting; null when the file or the key isn't there (SteamVR then starts on a button).</summary>
    /// <exception cref="SteamVrSettingsException">The file isn't a JSON object, or its power section isn't one.</exception>
    public bool? AutoLaunchOnButton()
    {
        if (Read() is not { } root)
        {
            return null;
        }

        return Power(root) is { } power && power[Key] is JsonValue value && value.TryGetValue<bool>(out var on) ? on : null;
    }

    /// <summary>
    /// Writes the setting; null removes it (and the power section, when nothing else is left in it). A missing file is
    /// made with the setting alone.
    /// </summary>
    /// <exception cref="SteamVrSettingsException">The file isn't a JSON object, or its power section isn't one; nothing was written.</exception>
    public void SetAutoLaunchOnButton(bool? value)
    {
        var root = Read() ?? new JsonObject();
        var power = Power(root);
        if (value is { } on)
        {
            if (power is null)
            {
                power = new JsonObject();
                root[Section] = power;
            }

            power[Key] = on;
        }
        else if (power is not null)
        {
            power.Remove(Key);
            if (power.Count == 0)
            {
                root.Remove(Section);
            }
        }

        // As SteamVR writes it: three spaces, and characters as they are.
        var text = root.ToJsonString(
            new JsonSerializerOptions { WriteIndented = true, IndentSize = 3, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        var temp = vrsettingsPath + ".mirador-new";
        File.WriteAllText(temp, text);
        try
        {
            if (File.Exists(vrsettingsPath))
            {
                BackUp();
                File.Replace(temp, vrsettingsPath, destinationBackupFileName: null);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(vrsettingsPath)!);
                File.Move(temp, vrsettingsPath);
            }
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    /// <summary>The file as a JSON object; null when there is no file.</summary>
    private JsonObject? Read()
    {
        if (!File.Exists(vrsettingsPath))
        {
            return null;
        }

        try
        {
            var root = JsonNode.Parse(File.ReadAllText(vrsettingsPath)) as JsonObject
                ?? throw new SteamVrSettingsException($"SteamVR's settings ({vrsettingsPath}) aren't a JSON object, so Mirador leaves them as they are.");
            _ = root.Count; // reads the whole object here, so that a duplicate key fails here too
            return root;
        }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException)
        {
            throw new SteamVrSettingsException($"Mirador couldn't read SteamVR's settings ({vrsettingsPath}): {e.Message.Trim().TrimEnd('.')}. It leaves them as they are.", e);
        }
    }

    /// <exception cref="SteamVrSettingsException">The power section isn't a JSON object.</exception>
    private JsonObject? Power(JsonObject root) => root[Section] switch
    {
        null => null,
        JsonObject power => power,
        _ => throw new SteamVrSettingsException($"SteamVR's settings ({vrsettingsPath}) have a power section Mirador doesn't know, so it leaves them as they are."),
    };

    /// <summary>A copy of the file as it is, unless one was already made this second.</summary>
    private void BackUp()
    {
        Directory.CreateDirectory(backupDir);
        var name = Path.GetFileName(vrsettingsPath) + ".mirador-" + time.GetLocalNow().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var backup = Path.Combine(backupDir, name);
        if (!File.Exists(backup))
        {
            File.Copy(vrsettingsPath, backup);
        }
    }
}
