using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal sealed class PatchInstallation
{
    private static readonly Pair Originals = new(
        "5A259722D60F746EC32C8BF6E880E375E888315080FB18E67C29B54B582C1215",
        "F5FB2817E331AA0B2DD0A6C7B0A360C89B5AE96AFC535CA085ABE514F8DE4AE4");
    private const string ProductVersion = "5.126.1.0";
    private const string AgentName = "MusicSync.Agent.exe";
    private const string ConfigName = "config.json";
    private const string PayloadName = "musicsync-together";
    private static readonly string[] StateFiles =
        ["original.asar", "original.exe", "patched.asar", "patched.exe", "restore.tmp", "receipt.next", "receipt.json"];

    private readonly string _root;
    private readonly string _bundle;
    private readonly string _resources;
    private readonly string _state;
    private readonly string _asar;
    private readonly string _exe;
    private readonly string _payload;

    internal PatchInstallation(string root, string bundle)
    {
        _root = root;
        _bundle = bundle;
        _resources = Path.Combine(root, "resources");
        _state = Path.Combine(root, ".musicsync-together");
        _asar = Path.Combine(_resources, "app.asar");
        _exe = Path.Combine(root, "Яндекс Музыка.exe");
        _payload = Path.Combine(_resources, PayloadName);
    }

    internal void Run(bool install)
    {
        CheckPaths();
        var lockPath = Path.Combine(_root, ".musicsync-together.lock");
        CheckPath(lockPath);
        using var installLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
            bufferSize: 4096, FileOptions.DeleteOnClose);
        using var music = new YandexMusicProcess(_exe);
        bool completed = false;
        try
        {
            Apply(install, music);
            completed = true;
        }
        finally
        {
            try { RestartIfSafe(music); }
            catch (Exception error) when (!completed) { Console.Error.WriteLine(error.Message); }
        }
    }

    private void Apply(bool install, YandexMusicProcess music)
    {
        CheckPaths();
        var receipt = ReadReceipt();

        if (receipt is { Phase: Phase.Installing or Phase.Removing })
        {
            music.Close();
            RestoreOriginals(receipt);
            receipt = null;
        }

        if (!File.Exists(_exe) || !File.Exists(_asar))
        {
            throw new InvalidOperationException("The Yandex Music executable and resources/app.asar are required.");
        }
        if (FileVersionInfo.GetVersionInfo(_exe).ProductVersion != ProductVersion)
        {
            throw new InvalidOperationException($"Only verified Yandex Music {AsarPatch.Version} is supported.");
        }
        var current = HashPair(_asar, _exe);

        if (!install)
        {
            if (receipt is null && current == Originals)
            {
                Console.WriteLine("The original files are already installed.");
                return;
            }
            if (receipt is null || current != receipt.Patched)
            {
                throw new InvalidOperationException("The installed version and checksums must match the recorded patch to uninstall it.");
            }

            VerifyPayload(_payload, receipt, allowMissing: true);
            AssertBackups();
            music.Close();
            AssertClosed();
            WriteReceipt(receipt with { Phase = Phase.Removing });
            RestoreOriginals(receipt);
            Console.WriteLine("The original Yandex Music files were restored and verified.");
            return;
        }

        if (current != Originals && (receipt is null || current != receipt.Patched))
        {
            throw new InvalidOperationException("The original Yandex Music checksums do not match the verified build.");
        }

        var agentHash = Hash(Path.Combine(_bundle, AgentName));
        var configPath = Path.Combine(_bundle, ConfigName);
        var configHash = Hash(configPath);
        ValidateConfig(configPath);
        var scripts = Path.Combine(_bundle, "electron");
        var bundleHash = GetBundleHash(agentHash, configHash, scripts);

        if (receipt is not null && current == receipt.Patched)
        {
            AssertBackups();
            VerifyPayload(_payload, receipt, allowMissing: false);
            if (bundleHash != receipt.BundleHash)
            {
                throw new InvalidOperationException("Uninstall the current MusicSync patch before installing a different package.");
            }
            Console.WriteLine("This MusicSync patch is already installed.");
            return;
        }

        if (receipt is null && (Directory.Exists(_payload) || File.Exists(_payload)))
        {
            throw new InvalidOperationException("An unrecognized MusicSync payload already exists.");
        }

        music.Close();
        AssertClosed();
        if (receipt is not null)
        {
            RemovePayload(receipt);
        }
        ClearState();
        Install(scripts, agentHash, configHash, bundleHash);
    }

    private void Install(string scripts, string agentHash, string configHash, string bundleHash)
    {
        Directory.CreateDirectory(_state);
        Receipt? receipt = null;
        try
        {
            var originalAsar = StatePath("original.asar");
            var originalExe = StatePath("original.exe");
            File.Copy(_asar, originalAsar);
            File.Copy(_exe, originalExe);
            AssertBackups();

            var patchedAsar = StatePath("patched.asar");
            var patchedExe = StatePath("patched.exe");
            var (originalHeader, patchedHeader) = AsarPatch.Build(originalAsar, patchedAsar, scripts);
            PeAsarIntegrity.Build(originalExe, patchedExe, originalHeader, patchedHeader);
            var stagedPayload = StatePath(PayloadName);
            Directory.CreateDirectory(stagedPayload);
            File.Copy(Path.Combine(_bundle, AgentName), Path.Combine(stagedPayload, AgentName));
            File.Copy(Path.Combine(_bundle, ConfigName), Path.Combine(stagedPayload, ConfigName));
            var prepared = new Receipt(AsarPatch.Version, HashPair(patchedAsar, patchedExe), agentHash, configHash,
                bundleHash, Phase.Installing);
            VerifyPayload(stagedPayload, prepared, allowMissing: false);
            if (GetBundleHash(agentHash, configHash, scripts) != bundleHash)
            {
                throw new InvalidOperationException("The installation package changed during preparation.");
            }

            AssertClosed();
            WriteReceipt(prepared);
            receipt = prepared;
            Directory.Move(stagedPayload, _payload);
            ReplaceIfMatching(patchedAsar, _asar, Originals.Asar);
            ReplaceIfMatching(patchedExe, _exe, Originals.Exe);
            if (HashPair(_asar, _exe) != receipt.Patched)
            {
                throw new IOException("Patched file verification failed.");
            }
            WriteReceipt(receipt with { Phase = Phase.Installed });
        }
        catch (Exception error)
        {
            try
            {
                if (receipt is null)
                {
                    ClearState();
                }
                else
                {
                    RestoreOriginals(receipt);
                }
            }
            catch (Exception rollback)
            {
                throw new IOException($"Installation failed ({error.Message}). Rollback requires attention ({rollback.Message}). " +
                    "Close Yandex Music and rerun the installer to recover matching files.", error);
            }
            throw new IOException($"Installation failed. The original files were preserved or restored. {error.Message}", error);
        }

        Console.WriteLine("MusicSync Together installed.");
    }

    private void RestoreOriginals(Receipt receipt)
    {
        AssertClosed();
        // A failed ReplaceFile can remove the destination. The journal still owns the recovery.
        var asarHash = HashIfPresent(_asar);
        var exeHash = HashIfPresent(_exe);
        if ((asarHash is not null && asarHash != Originals.Asar && asarHash != receipt.Patched.Asar) ||
            (exeHash is not null && exeHash != Originals.Exe && exeHash != receipt.Patched.Exe))
        {
            throw new InvalidOperationException("Recovery stopped because Yandex Music files have unrecognized checksums.");
        }
        if (asarHash != Originals.Asar || exeHash != Originals.Exe)
        {
            AssertBackups();
        }
        if (asarHash != Originals.Asar)
        {
            RestoreFile("original.asar", _asar, asarHash);
        }
        if (exeHash != Originals.Exe)
        {
            RestoreFile("original.exe", _exe, exeHash);
        }
        if (HashPair(_asar, _exe) != Originals)
        {
            throw new IOException("Original file verification failed.");
        }

        RemovePayload(receipt);
        ClearState();
    }

    private void RestoreFile(string original, string target, string? currentHash)
    {
        var temporary = StatePath("restore.tmp");
        File.Copy(StatePath(original), temporary, overwrite: true);
        if (currentHash is null)
        {
            File.Move(temporary, target);
        }
        else
        {
            ReplaceIfMatching(temporary, target, currentHash);
        }
    }

    private void AssertBackups()
    {
        if (HashPair(StatePath("original.asar"), StatePath("original.exe")) != Originals)
        {
            throw new IOException("The original backup checksums do not match the verified build.");
        }
    }

    private void AssertClosed()
    {
        if (YandexMusicProcess.IsRunning(_exe))
        {
            throw new InvalidOperationException("Yandex Music is still running in this directory. Close it and retry.");
        }
    }

    private void RestartIfSafe(YandexMusicProcess music)
    {
        if (!music.RestartPending)
        {
            return;
        }
        var current = HashPair(_asar, _exe);
        if (current != Originals && (ReadReceipt() is not { Phase: Phase.Installed } receipt || current != receipt.Patched))
        {
            throw new IOException("Yandex Music files need recovery before the application can be restarted.");
        }
        music.Restart();
    }

    private void CheckPaths()
    {
        foreach (var path in new[] { _root, _resources, _state, _asar, _exe, _payload, StatePath(PayloadName) })
        {
            CheckPath(path);
        }
        foreach (var name in StateFiles)
        {
            CheckPath(StatePath(name));
        }
        if (Directory.Exists(_state) && Directory.EnumerateFileSystemEntries(_state).Any(path =>
                Path.GetFileName(path) != PayloadName && !StateFiles.Contains(Path.GetFileName(path))))
        {
            throw new IOException("Unrecognized files in the MusicSync backup directory.");
        }
    }

    private static void CheckPath(string path)
    {
        try
        {
            if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new IOException($"The patch cannot use a redirected path: {path}");
            }
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }

    private Receipt? ReadReceipt()
    {
        var path = StatePath("receipt.json");
        if (!File.Exists(path))
        {
            return null;
        }
        var receipt = JsonSerializer.Deserialize<Receipt>(File.ReadAllBytes(path));
        if (receipt is null || receipt.Version != AsarPatch.Version || !Enum.IsDefined(receipt.Phase) ||
            !IsHash(receipt.Patched.Asar) || !IsHash(receipt.Patched.Exe) || !IsHash(receipt.AgentHash) ||
            !IsHash(receipt.ConfigHash) || !IsHash(receipt.BundleHash))
        {
            throw new InvalidDataException("Unrecognized MusicSync installation receipt.");
        }
        return receipt;
    }

    private void WriteReceipt(Receipt receipt)
    {
        var next = StatePath("receipt.next");
        using (var file = File.Create(next))
        {
            JsonSerializer.Serialize(file, receipt);
            file.Flush(flushToDisk: true);
        }
        File.Move(next, StatePath("receipt.json"), overwrite: true);
    }

    private void RemovePayload(Receipt receipt)
    {
        VerifyPayload(_payload, receipt, allowMissing: true);
        RemovePayloadFiles(_payload);
    }

    private static void VerifyPayload(string path, Receipt receipt, bool allowMissing)
    {
        CheckPath(path);
        if (!Directory.Exists(path))
        {
            if (allowMissing && !File.Exists(path))
            {
                return;
            }
            throw new IOException("The MusicSync payload directory is missing or invalid.");
        }
        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        {
            if (Path.GetFileName(entry) is not (AgentName or ConfigName))
            {
                throw new IOException($"Unrecognized file in the MusicSync payload: {entry}");
            }
        }
        CheckFile(AgentName, receipt.AgentHash);
        CheckFile(ConfigName, receipt.ConfigHash);

        void CheckFile(string name, string expected)
        {
            var file = Path.Combine(path, name);
            CheckPath(file);
            if ((!allowMissing || File.Exists(file) || Directory.Exists(file)) && Hash(file) != expected)
            {
                throw new IOException($"The installed MusicSync file was modified: {name}");
            }
        }
    }

    private void ClearState()
    {
        if (!Directory.Exists(_state))
        {
            return;
        }
        RemovePayloadFiles(StatePath(PayloadName));
        foreach (var name in StateFiles)
        {
            File.Delete(StatePath(name));
        }
        Directory.Delete(_state);
    }

    private static void RemovePayloadFiles(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }
        var agent = Path.Combine(path, AgentName);
        var config = Path.Combine(path, ConfigName);
        CheckPath(agent);
        CheckPath(config);
        File.Delete(agent);
        File.Delete(config);
        Directory.Delete(path);
    }

    private string StatePath(string name) => Path.Combine(_state, name);

    private static void ValidateConfig(string path)
    {
        using var configuration = JsonDocument.Parse(File.ReadAllBytes(path));
        if (!Uri.TryCreate(configuration.RootElement.GetProperty("endpoint").GetString(), UriKind.Absolute, out var endpoint) ||
            (endpoint.Scheme != "wss" && !(endpoint.Scheme == "ws" &&
                endpoint.IdnHost is "127.0.0.1" or "::1" or "localhost")) || endpoint.Host.Length == 0 ||
            endpoint.AbsolutePath != "/ws" || endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
        {
            throw new InvalidDataException("The package must specify a secure WebSocket endpoint or a local test endpoint.");
        }
    }

    private static string GetBundleHash(string agent, string config, string scripts)
    {
        var hashes = AsarPatch.AddedFiles.Append(AsarPatch.PreloadName).Select(name => Hash(Path.Combine(scripts, name)))
            .Prepend(config).Prepend(agent);
        return Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(string.Concat(hashes))));
    }

    private static void ReplaceIfMatching(string source, string destination, string expectedHash)
    {
        if (Hash(destination) != expectedHash)
        {
            throw new IOException($"The file changed during patching: {destination}");
        }
        File.Replace(source, destination, destinationBackupFileName: null);
    }

    private static Pair HashPair(string asar, string exe) => new(Hash(asar), Hash(exe));

    private static string? HashIfPresent(string path)
    {
        try { return Hash(path); }
        catch (FileNotFoundException) { return null; }
    }

    private static string Hash(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(file));
    }

    private static bool IsHash(string? value) => value?.Length == 64 && value.All(Uri.IsHexDigit);

    private readonly record struct Pair(string Asar, string Exe);
    private sealed record Receipt(string Version, Pair Patched, string AgentHash, string ConfigHash, string BundleHash, Phase Phase);
    private enum Phase { Installing = 1, Installed, Removing }
}
