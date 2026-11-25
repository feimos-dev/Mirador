using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mirador.Core.Lighthouse;

public sealed class SteamVrSettingsException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class SteamVrSettings(string vrsettingsPath, string backupDir, TimeProvider time)
{
    private const string Section = "power";
    private const string Key = "autoLaunchSteamVROnButtonPress";

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

    public bool? AutoLaunchOnButton()
    {
        if (Read() is not { } root)
        {
            return null;
        }

        return Power(root) is { } power && power[Key] is JsonValue value && value.TryGetValue<bool>(out var on) ? on : null;
    }

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

        var text = root.ToJsonString(
            new JsonSerializerOptions { WriteIndented = true, IndentSize = 3, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        var temp = vrsettingsPath + ".mirador-new";
        Directory.CreateDirectory(Path.GetDirectoryName(vrsettingsPath)!);
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
            _ = root.Count;
            return root;
        }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException)
        {
            throw new SteamVrSettingsException($"Mirador couldn't read SteamVR's settings ({vrsettingsPath}): {e.Message.Trim().TrimEnd('.')}. It leaves them as they are.", e);
        }
    }

    private JsonObject? Power(JsonObject root) => root[Section] switch
    {
        null => null,
        JsonObject power => power,
        _ => throw new SteamVrSettingsException($"SteamVR's settings ({vrsettingsPath}) have a power section Mirador doesn't know, so it leaves them as they are."),
    };

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
