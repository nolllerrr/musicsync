using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MusicSync.Patch.Tests;

internal sealed class InstallationFixture : IDisposable
{
    internal static readonly Uri Endpoint = new("ws://127.0.0.1:5050/ws");

    private readonly string _temporary = Directory.CreateTempSubdirectory("musicsync-installer-").FullName;
    private readonly string _agent;

    internal InstallationFixture()
    {
        try
        {
            Directory.CreateDirectory(Path.Combine(Root, "resources"));
            WriteApplication(preload: "// original preload");
            Originals = TargetHashes();
            _agent = Path.Combine(_temporary, "agent.exe");
            File.WriteAllText(_agent, "Fixture agent payload");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private string Root => Path.Combine(_temporary, "YandexMusic");
    internal string Asar => Path.Combine(Root, "resources", "app.asar");
    internal string Exe => Path.Combine(Root, "Яндекс Музыка.exe");
    internal string Payload => Path.Combine(Root, "resources", "musicsync-together");
    internal string State => Path.Combine(Root, ".musicsync-together");
    internal string Receipt => Path.Combine(State, "receipt.json");
    internal PatchInstallation.Pair Originals { get; }

    internal PatchInstallation Open() => new(Root, Originals);

    internal string Run(bool install, Uri? endpoint = null) =>
        Open().Run(install, endpoint, agent: install ? _agent : null, music: null);

    internal PatchInstallation.Pair TargetHashes() => new(Hash(Asar), Hash(Exe));

    internal PatchInstallation.Pair BackupHashes() =>
        new(Hash(Path.Combine(State, "original.asar")), Hash(Path.Combine(State, "original.exe")));

    internal string[] Snapshot() =>
        Directory.EnumerateFileSystemEntries(Root, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .Select(path => Path.GetRelativePath(Root, path) + (Directory.Exists(path) ? "/" : " " + Hash(path)))
            .ToArray();

    internal JsonObject ReadReceipt() => JsonNode.Parse(File.ReadAllBytes(Receipt))!.AsObject();

    internal void InterruptAfterAsarReplacement(bool exeMissing = false)
    {
        var receipt = ReadReceipt();
        // The installer records Installing before moving the payload and replacing app.asar first.
        receipt["Phase"] = 1;
        File.WriteAllText(Receipt, receipt.ToJsonString());
        File.Move(Exe, Path.Combine(State, "patched.exe"));
        if (!exeMissing)
            File.Copy(Path.Combine(State, "original.exe"), Exe);
    }

    internal void LoseState() => Directory.Move(State, Path.Combine(_temporary, "lost-state"));

    internal void ReplaceWithUpdatedApplication() => WriteApplication(preload: "// externally updated preload");

    public void Dispose() => Directory.Delete(_temporary, recursive: true);

    private void WriteApplication(string preload)
    {
        var headerHash = WriteAsar(Asar, preload);
        File.Copy(typeof(InstallationFixture).Assembly.Location, Exe, overwrite: true);
        var integrity = JsonSerializer.SerializeToUtf8Bytes(new[]
        {
            new { file = "resources/app.asar", alg = "SHA256", value = headerHash }
        });

        // Keep the compiler's VERSIONINFO resource and add the named resource Electron expects.
        var update = BeginUpdateResourceW(Exe, deleteExistingResources: false);
        if (update == IntPtr.Zero)
            throw new Win32Exception();
        bool written = false;
        try
        {
            written = UpdateResourceW(update, "Integrity", "ElectronAsar", 0x0409, integrity, (uint)integrity.Length);
            if (!written)
                throw new Win32Exception();
        }
        finally
        {
            if (!EndUpdateResourceW(update, discard: !written) && written)
                throw new Win32Exception();
        }
    }

    private static string WriteAsar(string path, string preload)
    {
        (string Name, byte[] Bytes)[] entries =
        [
            ("package.json", """{"version":"5.126.1","main":"./index.js"}"""u8.ToArray()),
            ("index.js", "// fixture entry point"u8.ToArray()),
            ("preload.js", Encoding.UTF8.GetBytes(preload))
        ];
        var files = new Dictionary<string, object>();
        int offset = 0;
        foreach (var (name, bytes) in entries)
        {
            var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            files.Add(name, new
            {
                size = bytes.Length,
                offset = offset.ToString(CultureInfo.InvariantCulture),
                integrity = new { algorithm = "SHA256", hash, blockSize = 4 * 1024 * 1024, blocks = new[] { hash } }
            });
            offset += bytes.Length;
        }

        var header = JsonSerializer.SerializeToUtf8Bytes(new { files });
        int padding = (4 - header.Length % 4) % 4;
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write(4);
        writer.Write(8 + header.Length + padding);
        writer.Write(4 + header.Length + padding);
        writer.Write(header.Length);
        writer.Write(header);
        writer.Write(new byte[padding]);
        foreach (var (_, bytes) in entries)
            writer.Write(bytes);
        return Convert.ToHexStringLower(SHA256.HashData(header));
    }

    private static string Hash(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(file));
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr BeginUpdateResourceW(string file, [MarshalAs(UnmanagedType.Bool)] bool deleteExistingResources);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateResourceW(IntPtr update, string type, string name, ushort language, byte[] data, uint size);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EndUpdateResourceW(IntPtr update, [MarshalAs(UnmanagedType.Bool)] bool discard);
}
