using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MusicSync.Patch.Tests;

[TestClass]
public sealed class AsarValidationTests
{
    [TestMethod]
    [DataRow("5.126.1")]
    [DataRow("5.127.0")]
    public void BuildsExplicitlySelectedPackageVersionAndPreservesOriginal(string version)
    {
        using var fixture = new ArchiveFixture(version);
        var before = File.ReadAllBytes(fixture.Source);
        var original = AsarPatch.Inspect(fixture.Source);

        var (originalHash, patchedHash) = AsarPatch.Build(fixture.Source, fixture.Patched, version);

        var patched = AsarPatch.Inspect(fixture.Patched);
        Assert.AreEqual(version, patched.Version);
        Assert.AreEqual("./musicsync-together/bootstrap.cjs", patched.Main);
        Assert.AreEqual(original.HeaderHash, originalHash);
        Assert.AreEqual(patched.HeaderHash, patchedHash);
        Assert.AreNotEqual(originalHash, patchedHash);
        CollectionAssert.AreEqual(before, File.ReadAllBytes(fixture.Source));
    }

    [TestMethod]
    public void RejectsUnexpectedPackageVersionBeforeCreatingOutput()
    {
        using var fixture = new ArchiveFixture("9.999.0");
        Assert.ThrowsExactly<InvalidDataException>(() => AsarPatch.Build(fixture.Source, fixture.Patched, "5.127.0"));
        Assert.IsFalse(File.Exists(fixture.Patched));
    }

    [TestMethod]
    [DataRow("package.json")]
    [DataRow("index.js")]
    [DataRow("preload.js")]
    public void RejectsCorruptedInjectionBoundaryWithoutWritingSource(string entry)
    {
        using var fixture = new ArchiveFixture("5.127.0", files => files[entry]!["integrity"]!["hash"] = new string('0', 64));
        var before = File.ReadAllBytes(fixture.Source);

        Assert.ThrowsExactly<InvalidDataException>(() => AsarPatch.Inspect(fixture.Source));
        Assert.ThrowsExactly<InvalidDataException>(() => AsarPatch.Build(fixture.Source, fixture.Patched, "5.127.0"));
        CollectionAssert.AreEqual(before, File.ReadAllBytes(fixture.Source));
        Assert.IsFalse(File.Exists(fixture.Patched));
    }

    [TestMethod]
    [DataRow("algorithm")]
    [DataRow("block")]
    [DataRow("blockSize")]
    [DataRow("blockCount")]
    public void RejectsInvalidIntegrityMetadata(string field)
    {
        using var fixture = new ArchiveFixture("5.127.0", files =>
        {
            var integrity = files["index.js"]!["integrity"]!;
            switch (field)
            {
                case "algorithm": integrity["algorithm"] = "SHA512"; break;
                case "block": integrity["blocks"]![0] = new string('0', 64); break;
                case "blockSize": integrity["blockSize"] = 0; break;
                case "blockCount": integrity["blocks"] = new JsonArray(); break;
            }
        });
        Assert.ThrowsExactly<InvalidDataException>(() => AsarPatch.Inspect(fixture.Source));
    }

    [TestMethod]
    [DataRow("link")]
    [DataRow("offset")]
    [DataRow("size")]
    [DataRow("unpacked")]
    public void RejectsRedirectedOrOutOfRangeEntry(string field)
    {
        using var fixture = new ArchiveFixture("5.127.0", files =>
        {
            var entry = files["index.js"]!;
            switch (field)
            {
                case "link": entry["link"] = "../../external.js"; break;
                case "offset": entry["offset"] = "9999999999999"; break;
                case "size": entry["size"] = -1; break;
                case "unpacked": entry["unpacked"] = true; break;
            }
        });
        Assert.ThrowsExactly<InvalidDataException>(() => AsarPatch.Inspect(fixture.Source));
    }

    [TestMethod]
    public void RejectsExistingInjectionDirectory()
    {
        using var fixture = new ArchiveFixture("5.127.0", files =>
            files["musicsync-together"] = new JsonObject { ["files"] = new JsonObject() });
        Assert.ThrowsExactly<InvalidDataException>(() => AsarPatch.Build(fixture.Source, fixture.Patched, "5.127.0"));
        Assert.IsFalse(File.Exists(fixture.Patched));
    }

    [TestMethod]
    public void PeValidationReadsMatchingHeaderAndRejectsWrongHeaderWithoutMutation()
    {
        using var app = new InstallationFixture();
        var original = app.TargetHashes();
        var info = AsarPatch.Inspect(app.Asar);
        PeAsarIntegrity.Validate(app.Exe, info.HeaderHash);
        Assert.ThrowsExactly<InvalidDataException>(() => PeAsarIntegrity.Validate(app.Exe, new string('0', 64)));
        Assert.AreEqual(original, app.TargetHashes());
    }

    private sealed class ArchiveFixture : IDisposable
    {
        private readonly string directory = Directory.CreateTempSubdirectory("musicsync-asar-validation-").FullName;
        internal string Source => Path.Combine(directory, "app.asar");
        internal string Patched => Path.Combine(directory, "patched.asar");

        internal ArchiveFixture(string version, Action<JsonObject>? mutate = null)
        {
            var entries = new Dictionary<string, byte[]>
            {
                ["package.json"] = JsonSerializer.SerializeToUtf8Bytes(new { version, main = "./index.js" }),
                ["index.js"] = Encoding.UTF8.GetBytes("// original index"),
                ["preload.js"] = Encoding.UTF8.GetBytes("// original preload")
            };
            var files = new JsonObject();
            var offset = 0;
            foreach (var (name, bytes) in entries)
            {
                var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
                files[name] = new JsonObject
                {
                    ["size"] = bytes.Length,
                    ["offset"] = offset.ToString(CultureInfo.InvariantCulture),
                    ["integrity"] = new JsonObject
                    {
                        ["algorithm"] = "SHA256", ["hash"] = hash, ["blockSize"] = 4194304,
                        ["blocks"] = new JsonArray(JsonValue.Create(hash))
                    }
                };
                offset += bytes.Length;
            }
            mutate?.Invoke(files);
            var header = JsonSerializer.SerializeToUtf8Bytes(new JsonObject { ["files"] = files });
            var padding = (4 - header.Length % 4) % 4;
            using var writer = new BinaryWriter(File.Create(Source));
            writer.Write(4);
            writer.Write(8 + header.Length + padding);
            writer.Write(4 + header.Length + padding);
            writer.Write(header.Length);
            writer.Write(header);
            writer.Write(new byte[padding]);
            foreach (var bytes in entries.Values) writer.Write(bytes);
        }

        public void Dispose() => Directory.Delete(directory, recursive: true);
    }
}
