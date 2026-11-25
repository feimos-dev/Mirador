namespace Mirador.Core.Tests;

public class AppSettingsTests
{
    [Fact]
    public void Round_trip()
    {
        using var dir = new TempDir();
        var store = new AppSettingsStore(dir.File("settings.json"));
        var settings = new AppSettings(CloseToTray: false, CheckForUpdates: false, LastUpdateCheck: new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero));

        store.Save(settings);

        Assert.Equal(settings, store.Load());
    }

    [Fact]
    public void Defaults_without_a_file()
    {
        using var dir = new TempDir();

        Assert.Equal(new AppSettings(true, true, null), new AppSettingsStore(dir.File("settings.json")).Load());
    }

    [Fact]
    public void A_broken_file_gives_the_defaults_and_is_kept()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("settings.json"), "{");

        Assert.Equal(new AppSettings(), new AppSettingsStore(dir.File("settings.json")).Load());
        Assert.True(File.Exists(dir.File("settings.json.bad")));
    }

    [Fact]
    public void State_mode_is_saved_as_text()
    {
        using var dir = new TempDir();
        var store = new AppSettingsStore(dir.File("settings.json"));
        var settings = new AppSettings(BaseStationsOnAtMiradorStart: true, BaseStationsOffAtMiradorQuit: true, BaseStationsState: StationStateMode.Live);

        store.Save(settings);

        var text = File.ReadAllText(dir.File("settings.json"));
        Assert.Contains("\"baseStationsState\": \"Live\"", text);
        Assert.Contains("\"baseStationsOnAtMiradorStart\": true", text);
        Assert.Contains("\"baseStationsOffAtMiradorQuit\": true", text);
        Assert.Equal(settings, store.Load());
    }

    [Fact]
    public void Old_file_gets_start_minimized_on()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("settings.json"), """{"closeToTray": false}""");

        Assert.True(new AppSettingsStore(dir.File("settings.json")).Load().StartMinimized);
    }

    [Fact]
    public void Start_mirador_for_games_defaults_to_on_and_round_trips()
    {
        using var dir = new TempDir();
        var store = new AppSettingsStore(dir.File("settings.json"));
        Assert.True(new AppSettings().StartMiradorForGames);
        Assert.True(store.Load().StartMiradorForGames);

        store.Save(new AppSettings(StartMiradorForGames: false));

        Assert.False(store.Load().StartMiradorForGames);

        Assert.Contains("\"startMiradorForGames\": false", File.ReadAllText(dir.File("settings.json")));
    }

    [Fact]
    public void Lighthouse_settings_are_off_on_a_fresh_install()
    {
        var settings = new AppSettings();

        Assert.False(settings.BaseStationsOnAtStart);
        Assert.False(settings.BaseStationsOffAtStop);
        Assert.False(settings.BaseStationsOnAtMiradorStart);
        Assert.False(settings.BaseStationsOffAtMiradorQuit);
        Assert.False(settings.ControllersOffAtStop);
    }
}
