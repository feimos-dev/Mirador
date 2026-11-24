using System.IO.MemoryMappedFiles;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mirador.Core.OpenVr;

/// <summary>
/// OpenLamina's shared store, the folder its programs keep their common state in: one JSON document per subject
/// (<c>{"format":1,"data":{…}}</c>) and <c>state.bin</c>, which holds a generation number per document that readers
/// watch. Mirador reads documents and writes the dashboard's, by the store's own rules: under the store's file lock,
/// the document replaced whole by a renamed copy, then its generation raised. Mirador never makes the folder: without
/// OpenLamina there is nothing to tell.
/// </summary>
public sealed class OpenLaminaStore(string directory)
{
    /// <summary>The scene document: the OpenVR game that runs.</summary>
    public const string Scene = "scene";

    /// <summary>The dashboard document: who runs the dashboard and whether it is open.</summary>
    public const string Dashboard = "dashboard";

    /// <summary>The newest document format Mirador reads and writes.</summary>
    private const int Format = 1;

    private const int StateBytes = 16 * 1024;

    /// <summary>How long a writer waits for the store's lock, as OpenLamina's own writers do.</summary>
    private static readonly TimeSpan LockLimit = TimeSpan.FromSeconds(2);

    /// <summary>How long a rename is tried again while another program has the document open.</summary>
    private static readonly TimeSpan ReplaceLimit = TimeSpan.FromMilliseconds(500);

    private static readonly JsonSerializerOptions Tabs = new() { WriteIndented = true, IndentCharacter = '\t', IndentSize = 1 };

    /// <summary>The documents in <c>state.bin</c>'s order of generations.</summary>
    private static readonly string[] Documents = ["settings", "applications", "chaperone", "scene", "dashboard", "overlays", "offsets"];

    /// <summary>The store of the signed-in user's OpenLamina.</summary>
    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenLamina", "store");

    /// <summary>Whether OpenLamina has made its store on this PC.</summary>
    public bool Exists => Directory.Exists(directory);

    /// <summary>
    /// The data of <paramref name="document"/>; null when the store or the document isn't there, can't be read now, or
    /// is a later OpenLamina's.
    /// </summary>
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

    /// <summary>
    /// Changes <paramref name="document"/> with <paramref name="edit"/>, keeping what else is in it. False when the
    /// store isn't there or the document couldn't be written (another program holds the lock, the document is a later
    /// OpenLamina's or can't be read): nothing is changed then.
    /// </summary>
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
                        return false; // a later OpenLamina's, or not a document: left as it is
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

                // Readers look at a document again only when its generation has changed.
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

    /// <summary>The <c>data</c> object of a document of a format Mirador knows; null otherwise.</summary>
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

    /// <summary>The store's lock: the first byte of <c>store.lock</c>, tried every 2 ms.</summary>
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

    /// <summary>
    /// <c>state.bin</c>, mapped: "OCST", the format, then eight generations. Set up when it is new; null when it is a
    /// later OpenLamina's. Call with the store's lock held.
    /// </summary>
    private MemoryMappedViewAccessor? OpenState()
    {
        var file = new FileStream(
            Path.Combine(directory, "state.bin"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        MemoryMappedViewAccessor view;
        try
        {
            // A mapping larger than the file extends it.
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
