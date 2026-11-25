using System.IO.MemoryMappedFiles;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mirador.Core.OpenVr;

public sealed class OpenLaminaStore(string directory)
{
    public const string Scene = "scene";

    public const string Dashboard = "dashboard";

    private const int Format = 1;

    private const int StateBytes = 16 * 1024;

    private static readonly TimeSpan LockLimit = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan ReplaceLimit = TimeSpan.FromMilliseconds(500);

    private static readonly JsonSerializerOptions Tabs = new() { WriteIndented = true, IndentCharacter = '\t', IndentSize = 1 };

    private static readonly string[] Documents = ["settings", "applications", "chaperone", "scene", "dashboard", "overlays", "offsets"];

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenLamina", "store");

    public bool Exists => Directory.Exists(directory);

    public JsonObject? Read(string document)
    {
        try
        {
            using var stream = new FileStream(
                Path.Combine(directory, document + ".json"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Data(JsonNode.Parse(stream));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public bool Update(string document, Action<JsonObject> edit)
    {
        var index = Array.IndexOf(Documents, document);
        if (index < 0)
        {
            throw new ArgumentException($"The store has no document \"{document}\".", nameof(document));
        }

        if (!Exists)
        {
            return false;
        }

        try
        {
            using var lockFile = new FileStream(
                Path.Combine(directory, "store.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
            if (!Lock(lockFile))
            {
                return false;
            }

            try
            {
                using var state = OpenState();
                if (state is null)
                {
                    return false;
                }

                var path = Path.Combine(directory, document + ".json");
                var data = new JsonObject();
                if (File.Exists(path))
                {
                    JsonNode? root;
                    using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    {
                        root = JsonNode.Parse(stream);
                    }

                    if (root is not JsonObject || Data(root) is not { } read)
                    {
                        return false;
                    }

                    data = (JsonObject)read.DeepClone();
                }

                edit(data);
                var text = new JsonObject { ["format"] = Format, ["data"] = data }.ToJsonString(Tabs);
                var temp = path + ".tmp";
                File.WriteAllText(temp, text, new UTF8Encoding(false));
                if (!Replace(temp, path))
                {
                    File.Delete(temp);
                    return false;
                }

                var at = 8 + (8 * index);
                state.Write(at, state.ReadUInt64(at) + 1);
                return true;
            }
            finally
            {
                lockFile.Unlock(0, 1);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    private static JsonObject? Data(JsonNode? root)
    {
        if (root is not JsonObject document)
        {
            return null;
        }

        if (document["format"] is JsonValue format && format.TryGetValue<long>(out var version) && version > Format)
        {
            return null;
        }

        return document["data"] as JsonObject ?? new JsonObject();
    }

    private static bool Lock(FileStream lockFile)
    {
        var deadline = Environment.TickCount64 + (long)LockLimit.TotalMilliseconds;
        while (true)
        {
            try
            {
                lockFile.Lock(0, 1);
                return true;
            }
            catch (IOException) when (Environment.TickCount64 < deadline)
            {
                Thread.Sleep(2);
            }
            catch (IOException)
            {
                return false;
            }
        }
    }

    private MemoryMappedViewAccessor? OpenState()
    {
        var file = new FileStream(
            Path.Combine(directory, "state.bin"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        MemoryMappedViewAccessor view;
        try
        {
            using var mapping = MemoryMappedFile.CreateFromFile(file, null, StateBytes, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, leaveOpen: false);
            view = mapping.CreateViewAccessor(0, StateBytes, MemoryMappedFileAccess.ReadWrite);
        }
        catch
        {
            file.Dispose();
            throw;
        }

        var magic = new byte[4];
        view.ReadArray(0, magic, 0, 4);
        var isStore = magic.AsSpan().SequenceEqual("OCST"u8);
        var format = view.ReadUInt32(4);
        if (isStore && format > Format)
        {
            view.Dispose();
            return null;
        }

        if (!isStore || format != Format)
        {
            view.WriteArray(0, new byte[StateBytes], 0, StateBytes);
            view.Write(4, (uint)Format);
            view.WriteArray(0, "OCST"u8.ToArray(), 0, 4);
        }

        return view;
    }

    private static bool Replace(string from, string to)
    {
        var deadline = Environment.TickCount64 + (long)ReplaceLimit.TotalMilliseconds;
        while (true)
        {
            try
            {
                File.Move(from, to, overwrite: true);
                return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException && Environment.TickCount64 < deadline)
            {
                Thread.Sleep(5);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }
}
