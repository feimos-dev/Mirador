using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using Mirador.Core.Lighthouse;

namespace Mirador.Core.Tests.Lighthouse;

public sealed class SteamVrSettingsTests : IDisposable
{
    private const string Key = "autoLaunchSteamVROnButtonPress";

    private readonly TempDir dir = new();
    private readonly FakeTimeProvider time = new(new DateTimeOffset(2026, 11, 18, 9, 30, 0, TimeSpan.Zero));

    public void Dispose() => dir.Dispose();

    private string File => dir.File("config", "steamvr.vrsettings");

    private string BackupDir => dir.File("Mirador", "backups", "steamvr");

    private SteamVrSettings Settings() => new(File, BackupDir, time);

    private void Write(string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(File)!);
        System.IO.File.WriteAllText(File, text);
    }

    /// <summary>A file as SteamVR writes one: several sections, numbers, arrays, nested objects.</summary>
    private const string Typical = """
        {
           "dashboard" : {
              "enableDashboard" : true
           },
           "driver_lighthouse" : {
              "trackedDevices" : [ "LHR-1", "LHR-2" ],
              "uiOverrides" : { "LHR-1" : { "role" : 2 } }
           },
           "power" : {
              "pauseCompositorOnStandby" : false,
              "turnOffControllersTimeout" : 300.0
           },
           "steamvr" : {
              "mirrorViewGeometry" : "0 0 1920 1080",
              "supersampleScale" : 1.5
           }
        }
        """;

    [Fact]
    public void Reads_the_key_in_the_power_section()
    {
        Write("""{ "power" : { "autoLaunchSteamVROnButtonPress" : false, "x" : 1 }, "steamvr" : {} }""");
        Assert.False(Settings().AutoLaunchOnButton());

        Write("""{ "power" : { "autoLaunchSteamVROnButtonPress" : true } }""");
        Assert.True(Settings().AutoLaunchOnButton());
    }

    [Fact]
    public void A_missing_key_or_file_reads_as_null()
    {
        Assert.Null(Settings().AutoLaunchOnButton());

        Write("""{ "steamvr" : {} }""");
        Assert.Null(Settings().AutoLaunchOnButton());

        Write(Typical);
        Assert.Null(Settings().AutoLaunchOnButton());

        // Only the power section's key counts.
        Write("""{ "steamvr" : { "autoLaunchSteamVROnButtonPress" : false } }""");
        Assert.Null(Settings().AutoLaunchOnButton());
    }

    [Fact]
    public void Setting_false_keeps_every_other_key()
    {
        Write(Typical);
        var before = JsonNode.Parse(Typical)!;

        Settings().SetAutoLaunchOnButton(false);

        var after = JsonNode.Parse(System.IO.File.ReadAllText(File))!;
        Assert.False((bool)after["power"]![Key]!);
        after["power"]!.AsObject().Remove(Key);
        Assert.True(JsonNode.DeepEquals(before, after));
        Assert.False(Settings().AutoLaunchOnButton());
    }

    [Fact]
    public void Setting_creates_the_power_section_when_missing()
    {
        Write("""{ "steamvr" : { "supersampleScale" : 1.5 } }""");

        Settings().SetAutoLaunchOnButton(false);

        var after = JsonNode.Parse(System.IO.File.ReadAllText(File))!;
        Assert.False((bool)after["power"]![Key]!);
        Assert.Equal(1.5, (double)after["steamvr"]!["supersampleScale"]!);
    }

    [Fact]
    public void Removing_restores_a_file_without_the_key()
    {
        Write(Typical);
        Settings().SetAutoLaunchOnButton(false);

        Settings().SetAutoLaunchOnButton(null);

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(Typical), JsonNode.Parse(System.IO.File.ReadAllText(File))));
        Assert.Null(Settings().AutoLaunchOnButton());

        // A power section made only for the key goes with it.
        Write("""{ "steamvr" : {} }""");
        Settings().SetAutoLaunchOnButton(false);
        Settings().SetAutoLaunchOnButton(null);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{ "steamvr" : {} }"""), JsonNode.Parse(System.IO.File.ReadAllText(File))));

        // A value that was there before goes back as it was.
        Write("""{ "power" : { "autoLaunchSteamVROnButtonPress" : true } }""");
        Settings().SetAutoLaunchOnButton(false);
        Settings().SetAutoLaunchOnButton(true);
        Assert.True(Settings().AutoLaunchOnButton());
    }

    [Fact]
    public void A_backup_is_written_before_the_first_change()
    {
        Write(Typical);

        Settings().SetAutoLaunchOnButton(false);

        var backup = Path.Combine(BackupDir, "steamvr.vrsettings.mirador-" + time.GetLocalNow().ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(Typical, System.IO.File.ReadAllText(backup));

        // A second change in the same second keeps that first copy.
        Settings().SetAutoLaunchOnButton(null);
        Assert.Equal(Typical, System.IO.File.ReadAllText(backup));
        Assert.Single(Directory.GetFiles(BackupDir));
    }

    [Theory]
    [InlineData("{ \"power\" : { ")]
    [InlineData("not json")]
    [InlineData("{ // a comment\n \"power\" : {} }")]
    [InlineData("[ 1, 2 ]")]
    [InlineData("{ \"power\" : 5 }")]
    public void Malformed_json_is_never_overwritten(string text)
    {
        Write(text);
        var bytes = System.IO.File.ReadAllBytes(File);

        Assert.Throws<SteamVrSettingsException>(() => Settings().AutoLaunchOnButton());
        Assert.Throws<SteamVrSettingsException>(() => Settings().SetAutoLaunchOnButton(false));

        Assert.Equal(bytes, System.IO.File.ReadAllBytes(File));
        Assert.False(Directory.Exists(BackupDir) && Directory.GetFiles(BackupDir).Length > 0);
    }
}
