using System.Text.Json.Nodes;
using Mirador.Core.OpenVr;

namespace Mirador.Core.Tests.OpenVr;

public sealed class OpenLaminaStoreTests : IDisposable
{
    private readonly TempDir dir = new();

    public void Dispose() => dir.Dispose();

    private OpenLaminaStore Store() => new(dir.Path);

    private ulong Generation(int document)
    {
        var state = File.ReadAllBytes(dir.File("state.bin"));
        return BitConverter.ToUInt64(state, 8 + (8 * document));
    }

    [Fact]
    public void A_store_that_is_not_there_is_never_made()
    {
        var missing = new OpenLaminaStore(dir.File("none"));

        Assert.False(missing.Exists);
        Assert.Null(missing.Read(OpenLaminaStore.Scene));
        Assert.False(missing.Update(OpenLaminaStore.Dashboard, data => data["open"] = true));
        Assert.False(Directory.Exists(dir.File("none")));
    }

    [Fact]
    public void A_first_write_makes_the_document_and_sets_the_state_file_up()
    {
        Assert.True(Store().Update(OpenLaminaStore.Dashboard, data => data["open"] = true));

        Assert.Equal("{\n\t\"format\": 1,\n\t\"data\": {\n\t\t\"open\": true\n\t}\n}", File.ReadAllText(dir.File("dashboard.json")).ReplaceLineEndings("\n"));
        var state = File.ReadAllBytes(dir.File("state.bin"));
        Assert.Equal(16 * 1024, state.Length);
        Assert.Equal("OCST"u8.ToArray(), state[..4]);
        Assert.Equal(1u, BitConverter.ToUInt32(state, 4));
        Assert.Equal(1ul, Generation(4));
        Assert.False(File.Exists(dir.File("dashboard.json.tmp")));
    }

    [Fact]
    public void A_write_keeps_what_else_is_in_the_document_and_raises_only_its_own_generation()
    {
        File.WriteAllText(dir.File("dashboard.json"), """{"format":1,"data":{"active_pid":77,"scene_start":133769000000000000,"open":false}}""");
        var store = Store();
        store.Update(OpenLaminaStore.Dashboard, data => data["open"] = true);

        store.Update(OpenLaminaStore.Dashboard, data => data["host_pid"] = 5);

        var data = store.Read(OpenLaminaStore.Dashboard)!;
        Assert.Equal(77, (int)data["active_pid"]!);
        Assert.Equal(133769000000000000ul, (ulong)data["scene_start"]!);
        Assert.True((bool)data["open"]!);
        Assert.Equal(5, (int)data["host_pid"]!);
        Assert.Equal(2ul, Generation(4));
        Assert.Equal(0ul, Generation(3));
    }

    [Fact]
    public void A_state_file_already_in_use_keeps_its_generations()
    {
        var state = new byte[16 * 1024];
        "OCST"u8.CopyTo(state);
        BitConverter.TryWriteBytes(state.AsSpan(4), 1u);
        BitConverter.TryWriteBytes(state.AsSpan(8 + (8 * 3)), 9ul);
        BitConverter.TryWriteBytes(state.AsSpan(8 + (8 * 4)), 41ul);
        File.WriteAllBytes(dir.File("state.bin"), state);

        Store().Update(OpenLaminaStore.Dashboard, data => data["open"] = true);

        Assert.Equal(9ul, Generation(3));
        Assert.Equal(42ul, Generation(4));
    }

    [Fact]
    public void A_document_of_a_later_format_is_neither_read_nor_written()
    {
        const string Later = """{"format":2,"data":{"open":true}}""";
        File.WriteAllText(dir.File("dashboard.json"), Later);

        Assert.Null(Store().Read(OpenLaminaStore.Dashboard));
        Assert.False(Store().Update(OpenLaminaStore.Dashboard, data => data["open"] = false));
        Assert.Equal(Later, File.ReadAllText(dir.File("dashboard.json")));
    }

    [Fact]
    public void A_state_file_of_a_later_format_stops_the_write()
    {
        var state = new byte[16 * 1024];
        "OCST"u8.CopyTo(state);
        BitConverter.TryWriteBytes(state.AsSpan(4), 2u);
        File.WriteAllBytes(dir.File("state.bin"), state);

        Assert.False(Store().Update(OpenLaminaStore.Dashboard, data => data["open"] = true));
        Assert.False(File.Exists(dir.File("dashboard.json")));
    }

    [Fact]
    public void A_document_that_does_not_parse_is_left_alone()
    {
        File.WriteAllText(dir.File("dashboard.json"), "{ not json");

        Assert.Null(Store().Read(OpenLaminaStore.Dashboard));
        Assert.False(Store().Update(OpenLaminaStore.Dashboard, data => data["open"] = true));
        Assert.Equal("{ not json", File.ReadAllText(dir.File("dashboard.json")));
    }

    [Fact]
    public void A_document_without_data_reads_as_empty()
    {
        File.WriteAllText(dir.File("scene.json"), """{"format":1}""");

        Assert.Empty(Store().Read(OpenLaminaStore.Scene)!);
    }

    [Fact]
    public void A_document_the_store_does_not_have_is_a_mistake()
    {
        Assert.Throws<ArgumentException>(() => Store().Update("games", _ => { }));
    }
}
