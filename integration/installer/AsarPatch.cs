using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class AsarPatch
{
    internal const string Version = "5.126.1";
    private const int BlockSize = 4 * 1024 * 1024;
    private const string DirectoryName = "musicsync-together";
    internal static readonly string[] AddedFiles = ["bootstrap.cjs", "main.cjs", "renderer.js", "renderer.css"];
    internal const string PreloadName = "preload.cjs";

    internal sealed record Info(string Version, string Main, string HeaderHash);

    // This verifies the injection boundary only. Compatibility still requires an allowlisted whole-file hash pair.
    internal static Info Inspect(string sourcePath)
    {
        using var source = File.OpenRead(sourcePath);
        var header = ReadHeader(source);
        var dataStart = source.Position;
        var files = JsonNode.Parse(header)?["files"] as JsonObject ??
            throw new InvalidDataException("Invalid ASAR file tree.");
        var package = ReadPackage(source, dataStart, files);
        ReadFile(source, dataStart, files, "index.js");
        ReadFile(source, dataStart, files, "preload.js");
        return new Info(package["version"]?.GetValue<string>() ?? "", package["main"]?.GetValue<string>() ?? "", Hash(header));
    }

    internal static (string Original, string Patched) Build(string sourcePath, string destinationPath,
        string expectedVersion = Version)
    {
        using var source = File.OpenRead(sourcePath);
        var originalHeader = ReadHeader(source);
        var originalHash = Hash(originalHeader);
        var dataStart = source.Position;
        var header = JsonNode.Parse(originalHeader)!.AsObject();
        var files = header["files"]!.AsObject();
        var package = ReadPackage(source, dataStart, files);
        if (package["version"]?.GetValue<string>() != expectedVersion || package["main"]?.GetValue<string>() != "./index.js" ||
            files.ContainsKey(DirectoryName))
        {
            throw new InvalidDataException("Unexpected Yandex Music package version or entry point.");
        }

        ReadFile(source, dataStart, files, "index.js");
        package["main"] = "./musicsync-together/bootstrap.cjs";
        var preload = ReadFile(source, dataStart, files, "preload.js");
        var combinedPreload = preload.Concat("\n;\n"u8.ToArray())
            .Concat(ReadScript(PreloadName)).ToArray();
        var injected = new JsonObject();
        files.Add(DirectoryName, new JsonObject { ["files"] = injected });

        // Leave untouched files and their offsets intact, and append the changed and new entries.
        var appended = new List<byte[]>();
        var offset = source.Length - dataStart;
        Append(files, "package.json", JsonSerializer.SerializeToUtf8Bytes(package));
        Append(files, "preload.js", combinedPreload);
        foreach (var name in AddedFiles)
        {
            Append(injected, name, ReadScript(name));
        }

        var patchedHeader = JsonSerializer.SerializeToUtf8Bytes(header);
        using var destination = File.Open(destinationPath, FileMode.CreateNew, FileAccess.Write);
        WriteHeader(destination, patchedHeader);
        source.Position = dataStart;
        source.CopyTo(destination);
        foreach (var content in appended)
        {
            destination.Write(content);
        }
        destination.Flush(flushToDisk: true);
        return (originalHash, Hash(patchedHeader));

        void Append(JsonObject directory, string name, byte[] content)
        {
            var hash = Hash(content);
            var blocks = new JsonArray();
            for (var start = 0; start < content.Length; start += BlockSize)
            {
                blocks.Add(Hash(content.AsSpan(start, Math.Min(BlockSize, content.Length - start))));
            }
            if (content.Length == 0)
            {
                blocks.Add(hash);
            }

            directory[name] = new JsonObject
            {
                ["size"] = content.Length,
                ["offset"] = offset.ToString(CultureInfo.InvariantCulture),
                ["integrity"] = new JsonObject
                {
                    ["algorithm"] = "SHA256",
                    ["hash"] = hash,
                    ["blockSize"] = BlockSize,
                    ["blocks"] = blocks
                }
            };
            appended.Add(content);
            offset += content.Length;
        }
    }

    internal static byte[] ReadScript(string name)
    {
        using var script = typeof(AsarPatch).Assembly.GetManifestResourceStream("Electron." + name) ??
            throw new InvalidDataException($"MusicSync script is missing: {name}");
        var content = new byte[script.Length];
        script.ReadExactly(content);
        return content;
    }

    private static byte[] ReadHeader(Stream source)
    {
        Span<byte> prefix = stackalloc byte[16];
        source.ReadExactly(prefix);
        var sizePickle = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        var headerSize = BinaryPrimitives.ReadInt32LittleEndian(prefix[4..]);
        var headerPayloadSize = BinaryPrimitives.ReadInt32LittleEndian(prefix[8..]);
        var jsonSize = BinaryPrimitives.ReadInt32LittleEndian(prefix[12..]);
        if (sizePickle != 4 || jsonSize < 2 || jsonSize > 16 * 1024 * 1024 ||
            headerPayloadSize != 4 + AlignToFour(jsonSize) || headerSize != 4 + headerPayloadSize ||
            source.Length < 8L + headerSize)
        {
            throw new InvalidDataException("Invalid ASAR header.");
        }

        var header = new byte[jsonSize];
        source.ReadExactly(header);
        source.Position = 8 + headerSize;
        return header;
    }

    private static void WriteHeader(Stream destination, byte[] header)
    {
        var paddedSize = AlignToFour(header.Length);
        using var writer = new BinaryWriter(destination, Encoding.UTF8, leaveOpen: true);
        writer.Write(4);
        writer.Write(8 + paddedSize);
        writer.Write(4 + paddedSize);
        writer.Write(header.Length);
        writer.Write(header);
        writer.Write(new byte[paddedSize - header.Length]);
    }

    private static byte[] ReadFile(Stream source, long dataStart, JsonObject files, string name)
    {
        var entry = files[name] as JsonObject ?? throw new InvalidDataException($"Missing ASAR entry {name}.");
        if (!long.TryParse(entry["offset"]?.GetValue<string>(), NumberStyles.None, CultureInfo.InvariantCulture, out var offset) ||
            entry["size"] is not JsonValue sizeValue || !sizeValue.TryGetValue<int>(out var size) ||
            offset < 0 || size < 0 || size > 32 * 1024 * 1024 || offset > source.Length - dataStart - size ||
            entry["unpacked"]?.GetValue<bool>() == true || entry.ContainsKey("link"))
        {
            throw new InvalidDataException($"Invalid ASAR entry {name}.");
        }

        var bytes = new byte[size];
        source.Position = dataStart + offset;
        source.ReadExactly(bytes);
        var integrity = entry["integrity"] as JsonObject;
        if (integrity?["algorithm"]?.GetValue<string>() != "SHA256" ||
            Hash(bytes) != integrity["hash"]?.GetValue<string>() ||
            integrity["blockSize"] is not JsonValue blockValue || !blockValue.TryGetValue<int>(out var blockSize) ||
            blockSize <= 0 || blockSize > 32 * 1024 * 1024 || integrity["blocks"] is not JsonArray blocks ||
            blocks.Count != Math.Max(1, (size + (long)blockSize - 1) / blockSize))
        {
            throw new InvalidDataException($"ASAR entry integrity mismatch for {name}.");
        }
        for (var index = 0; index < blocks.Count; index++)
        {
            var blockStart = index * blockSize;
            if (Hash(bytes.AsSpan(blockStart, Math.Min(blockSize, size - blockStart))) != blocks[index]?.GetValue<string>())
                throw new InvalidDataException($"ASAR block integrity mismatch for {name}.");
        }
        return bytes;
    }

    private static JsonObject ReadPackage(Stream source, long dataStart, JsonObject files) =>
        JsonNode.Parse(ReadFile(source, dataStart, files, "package.json")) as JsonObject ??
        throw new InvalidDataException("Invalid ASAR package.json.");

    private static int AlignToFour(int size) => (size + 3) & ~3;

    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
