using System.Text.Json.Serialization;

namespace Mirador.Core;

public sealed record AppSettings(
    bool CloseToTray = true,
    bool CheckForUpdates = true,
    DateTimeOffset? LastUpdateCheck = null,
    bool BaseStationsOffAtStop = false,
    bool BaseStationsOnAtMiradorStart = false,
    bool BaseStationsOffAtMiradorQuit = false,
    StationStateMode BaseStationsState = StationStateMode.LastScan,
    bool StartMinimized = true,
    bool ControllersOffAtStop = false,
    bool StartMiradorForGames = true,
    bool KeepSteamVrFromButtonStart = false,
    bool? SteamVrButtonStartBefore = null,
    bool MinimizeToTray = false,
    bool HomeEnvironment = true,
    bool Dashboard = true);

public enum StationStateMode
{
    LastScan,

    Live,
}

public sealed class AppSettingsStore(string path)
{
    public AppSettings Load() => JsonFile.Load(path, AppSettingsJsonContext.Default.AppSettings) ?? new AppSettings();

    public void Save(AppSettings settings) => JsonFile.Save(path, settings, AppSettingsJsonContext.Default.AppSettings);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true, WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class AppSettingsJsonContext : JsonSerializerContext;
