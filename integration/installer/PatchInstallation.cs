using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal sealed partial class PatchInstallation
{
    internal const string AgentName = "MusicSync.Agent.exe";
    private const string ConfigName = "config.json";
    private const string PayloadName = "musicsync-together";
    private static readonly string[] StateFiles =
        ["original.asar", "original.exe", "patched.asar", "patched.exe", "restore.tmp", "config.next", "receipt.next", "receipt.json"];

    private readonly string _root;
    private readonly string _resources;
    private readonly string _state;
    private readonly string _asar;
    private readonly string _exe;
    private readonly string _payload;
    private readonly IReadOnlyList<SupportedBuild> _builds;
    private readonly Action<InstallCheckpoint>? _checkpoint;
    private SupportedBuild _activeBuild;
    private Pair _originals => _activeBuild.Originals;
    internal string Root => _root;

    internal PatchInstallation(string root) : this(root, SupportedBuild.Catalog) { }

    internal PatchInstallation(string root, Pair originals) :
        this(root,
        [new SupportedBuild("5.126.1", "5.126.1.0", originals)])
    { }

    internal PatchInstallation(string root, IReadOnlyList<SupportedBuild> builds, Action<InstallCheckpoint>? checkpoint = null)
    {
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        root = _root;
        _builds = builds;
        _checkpoint = checkpoint;
        _activeBuild = builds.First();
        _resources = Path.Combine(root, "resources");
        _state = Path.Combine(root, ".musicsync-together");
        _asar = Path.Combine(_resources, "app.asar");
        _exe = Path.Combine(root, "Яндекс Музыка.exe");
        _payload = Path.Combine(_resources, PayloadName);
    }

    internal string Run(bool install, Uri? endpoint)
    {
        var agent = install ? GetAgentSource() : null;
        var music = new YandexMusicRestart(_exe, Path.Combine(_payload, AgentName));
        return Run(install, endpoint, agent, music);
    }

    internal string Run(bool install, Uri? endpoint, string? agent, YandexMusicRestart? music)
    {
        if (!Directory.Exists(_root))
        {
            throw new DirectoryNotFoundException("Не найден каталог Яндекс Музыки. Сначала установите Яндекс Музыку для Windows.");
        }

        using var installLock = AcquireLock();
        CheckPaths();
        string result;
        try
        {
            result = Apply(install, endpoint, agent, music);
        }
        catch (Exception error)
        {
            if (music is { ShouldRestart: true } && CanRestart())
                throw new InvalidOperationException(error.Message + music.RestartNotice(), error);
            throw;
        }

        return result + music?.RestartNotice();
    }

    private string Apply(bool install, Uri? endpoint, string? agent, YandexMusicRestart? music)
    {
        var receipt = ReadReceipt();

        // An official update owns its new files, even when an old journal says Installing.
        // Recognize the complete new pair before considering rollback of the old transaction.
        var stock = FindOriginalBuild();
        if (receipt is null && stock is not null && Directory.Exists(_state) &&
            !Directory.EnumerateFileSystemEntries(_state).Any())
            Directory.Delete(_state); // Last cleanup step was interrupted; there are no files to discard.
        if (receipt is not null && stock is not null)
        {
            ValidateOriginalStructure(stock);
            endpoint ??= ReadVerifiedEndpoint(receipt);
            RetireState(receipt);
            receipt = null;
        }
        _activeBuild = stock ?? (receipt is not null ? ReceiptBuild(receipt) : _builds[0]);

        if (receipt is { Phase: Phase.Installing or Phase.Removing })
        {
            RestoreOriginals(receipt, music);
            receipt = null;
        }

        if (!File.Exists(_exe) || !File.Exists(_asar))
        {
            throw new InvalidOperationException("В каталоге Яндекс Музыки не найдены файлы приложения.");
        }
        if (FileVersionInfo.GetVersionInfo(_exe).ProductVersion != _activeBuild.ProductVersion)
        {
            throw new InvalidOperationException("Эта версия Яндекс Музыки пока не поддерживается. Дождитесь совместимого обновления MusicSync.");
        }
        var current = HashPair(_asar, _exe);

        if (!install)
        {
            if (receipt is null && current == _originals)
            {
                return "Патч уже снят.";
            }
            if (receipt is null || current != receipt.Patched)
            {
                throw new InvalidOperationException("Не удалось снять патч: файлы Яндекс Музыки отличаются от записанных при установке.");
            }

            VerifyPayload(_payload, receipt, allowMissing: true);
            AssertBackups();
            music?.Close();
            WriteReceipt(receipt with { Phase = Phase.Removing });
            RestoreOriginals(receipt, music);
            return "Патч удалён. Исходные файлы Яндекс Музыки восстановлены и проверены.";
        }

        if (current != _originals && (receipt is null || current != receipt.Patched))
        {
            throw new InvalidOperationException("Контрольные суммы файлов Яндекс Музыки отличаются от проверенной сборки.");
        }

        var agentHash = Hash(agent!);

        if (receipt is not null && current == receipt.Patched)
        {
            AssertBackups();
            VerifyPayload(_payload, receipt, allowMissing: false);
            if (GetBundleHash(agentHash, receipt.ConfigHash) != receipt.BundleHash)
            {
                throw new InvalidOperationException("Установлен патч из другой сборки. Сначала нажмите «Удалить патч».");
            }
            if (endpoint is not null)
            {
                WriteEndpoint(receipt, endpoint);
                return "Патч уже установлен. Адрес сервера сохранён.";
            }
            return "Патч уже установлен.";
        }

        if (receipt is null && Directory.Exists(_state))
            throw new InvalidOperationException("Найдена незавершённая подготовка установки без журнала. Файлы сохранены; требуется проверка вручную.");
        var config = PatchSettings.Encode(endpoint ?? ReadArchivedEndpoint() ?? PatchSettings.Load());
        var configHash = Hash(config);
        if (receipt is not null)
        {
            music?.Close();
            RemovePayload(receipt);
        }
        else if (Directory.Exists(_payload) || File.Exists(_payload))
        {
            throw new InvalidOperationException("An unrecognized MusicSync payload already exists.");
        }

        ClearState();
        Install(agent!, config, agentHash, configHash, GetBundleHash(agentHash, configHash), music);
        return "Патч установлен. Раздел «Вместе» доступен в Яндекс Музыке.";
    }

    private bool CanRestart()
    {
        try
        {
            var receipt = ReadReceipt();
            if (receipt is { Phase: Phase.Installing or Phase.Removing })
                return false;

            var current = HashPair(_asar, _exe);
            return current == _originals || current == receipt?.Patched;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    [UnconditionalSuppressMessage("SingleFile", "IL3000", Justification = "An empty location identifies the bundled assembly.")]
    private static string GetAgentSource() =>
        typeof(PatchInstallation).Assembly.Location.Length == 0 && Environment.ProcessPath is string path ? path :
            throw new InvalidOperationException("Для установки запустите собранный MusicSync Together из папки dist.");

    internal Uri? ReadInstalledEndpoint()
    {
        var path = Path.Combine(_payload, ConfigName);
        if (!File.Exists(StatePath("receipt.json")) || !File.Exists(path))
        {
            return null;
        }
        foreach (var parent in new[] { _root, _resources, _state, _payload, path })
        {
            CheckPath(parent);
        }
        return PatchSettings.Read(path);
    }

    internal bool SaveEndpoint(Uri endpoint)
    {
        if (!Directory.Exists(_root))
        {
            return false;
        }

        using var installLock = AcquireLock();
        CheckPaths();
        var receipt = ReadReceipt();
        if (receipt is null)
        {
            return false;
        }
        if (receipt.Phase != Phase.Installed || HashPair(_asar, _exe) != receipt.Patched)
        {
            throw new InvalidOperationException("Не удалось обновить адрес. Восстановите патч через это окно.");
        }
        VerifyPayload(_payload, receipt, allowMissing: false);
        WriteEndpoint(receipt, endpoint);
        return true;
    }

    private void WriteEndpoint(Receipt receipt, Uri endpoint)
    {
        var path = Path.Combine(_payload, ConfigName);
        var currentHash = Hash(path);
        if (!MatchesConfig(receipt, currentHash))
        {
            throw new IOException("Файл настроек MusicSync был изменён вне установщика.");
        }

        var config = PatchSettings.Encode(endpoint);
        var nextHash = Hash(config);
        if (currentHash == nextHash)
        {
            if (receipt.PendingSettingsHash is not null)
            {
                WriteReceipt(receipt with { SettingsHash = currentHash, PendingSettingsHash = null });
            }
            return;
        }

        // Record both valid hashes before replacing the file so an interrupted save can be retried or uninstalled.
        var next = StatePath("config.next");
        using (var file = File.Create(next))
        {
            file.Write(config);
            file.Flush(flushToDisk: true);
        }
        WriteReceipt(receipt with { SettingsHash = currentHash, PendingSettingsHash = nextHash });
        if (Hash(path) != currentHash)
        {
            throw new IOException("Файл настроек MusicSync изменился во время сохранения.");
        }
        File.Move(next, path, overwrite: true);
        WriteReceipt(receipt with { SettingsHash = nextHash, PendingSettingsHash = null });
    }

    private FileStream AcquireLock()
    {
        CheckPath(_root);
        var path = Path.Combine(_root, ".musicsync-together.lock");
        CheckPath(path);
        return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
            bufferSize: 4096, FileOptions.DeleteOnClose);
    }

    private void Install(string agent, byte[] config, string agentHash, string configHash, string bundleHash, YandexMusicRestart? music)
    {
        Directory.CreateDirectory(_state);
        Receipt? receipt = null;
        FileStream? applicationGuard = null;
        try
        {
            var originalAsar = StatePath("original.asar");
            var originalExe = StatePath("original.exe");
            File.Copy(_asar, originalAsar);
            File.Copy(_exe, originalExe);
            FlushFile(originalAsar);
            FlushFile(originalExe);
            AssertBackups();

            var patchedAsar = StatePath("patched.asar");
            var patchedExe = StatePath("patched.exe");
            var (originalHeader, patchedHeader) = AsarPatch.Build(originalAsar, patchedAsar, _activeBuild.Version);
            PeAsarIntegrity.Build(originalExe, patchedExe, originalHeader, patchedHeader);
            var stagedPayload = StatePath(PayloadName);
            Directory.CreateDirectory(stagedPayload);
            File.Copy(agent, Path.Combine(stagedPayload, AgentName));
            File.WriteAllBytes(Path.Combine(stagedPayload, ConfigName), config);
            FlushFile(Path.Combine(stagedPayload, AgentName));
            FlushFile(Path.Combine(stagedPayload, ConfigName));
            var prepared = new Receipt(_activeBuild.Version, HashPair(patchedAsar, patchedExe), agentHash, configHash,
                bundleHash, Phase.Installing, Originals: _originals, ProductVersion: _activeBuild.ProductVersion);
            VerifyPayload(stagedPayload, prepared, allowMissing: false);
            music?.Close();
            applicationGuard = GuardExecutable(_exe);
            EnsureApplicationIdle();
            if (HashPair(_asar, _exe) != _originals)
                throw new IOException("The application changed while preparing the patch.");
            WriteReceipt(prepared);
            receipt = prepared;
            Directory.Move(stagedPayload, _payload);
            EnsureApplicationIdle();
            ReplaceIfMatching(patchedAsar, _asar, _originals.Asar, receipt.Patched.Asar);
            _checkpoint?.Invoke(InstallCheckpoint.AsarReplaced);
            ReplaceIfMatching(patchedExe, _exe, _originals.Exe, receipt.Patched.Exe);
            _checkpoint?.Invoke(InstallCheckpoint.PairReplaced);
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
                    // After the final rename the complete patched pair may already be running.
                    // Acquire a writable image handle before rolling it back; if that fails,
                    // leave the owned pair and journal for explicit recovery, without closing it.
                    using var rollbackGuard = HashIfPresent(_exe) == receipt.Patched.Exe
                        ? GuardExecutable(_exe) : null;
                    RestoreOriginals(receipt, music);
                }
            }
            catch (Exception rollback)
            {
                throw new IOException($"Installation failed ({error.Message}). Rollback requires attention ({rollback.Message}). " +
                    "Rerun the installer to recover matching files.", error);
            }
            throw new IOException($"Installation failed. The original files were preserved or restored. {error.Message}", error);
        }
        finally { applicationGuard?.Dispose(); }
    }

    private void RestoreOriginals(Receipt receipt, YandexMusicRestart? music)
    {
        // A failed ReplaceFile can remove the destination. The journal still owns the recovery.
        var asarHash = HashIfPresent(_asar);
        var exeHash = HashIfPresent(_exe);
        if ((asarHash is not null && asarHash != _originals.Asar && asarHash != receipt.Patched.Asar) ||
            (exeHash is not null && exeHash != _originals.Exe && exeHash != receipt.Patched.Exe))
        {
            throw new InvalidOperationException("Recovery stopped because Yandex Music files have unrecognized checksums.");
        }
        AssertBackups();
        music?.Close();
        if (asarHash != _originals.Asar)
        {
            RestoreFile("original.asar", _asar, asarHash);
        }
        if (exeHash != _originals.Exe)
        {
            RestoreFile("original.exe", _exe, exeHash);
        }
        if (HashPair(_asar, _exe) != _originals)
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
        var expected = original == "original.asar" ? _originals.Asar : _originals.Exe;
        FlushFile(temporary);
        if (Hash(temporary) != expected)
            throw new IOException("The original backup changed during recovery.");
        if (currentHash is null)
        {
            File.Move(temporary, target);
        }
        else
        {
            ReplaceIfMatching(temporary, target, currentHash, expected);
        }
    }

    private void AssertBackups() => AssertBackups(_activeBuild);

    private void AssertBackups(SupportedBuild build)
    {
        if (HashPair(StatePath("original.asar"), StatePath("original.exe")) != build.Originals)
        {
            throw new IOException("The original backup checksums do not match the verified build.");
        }
    }

    private void CheckPaths()
    {
        foreach (var path in new[] { _root, _resources, _state, _asar, _exe, _payload, StatePath(PayloadName),
                     StatePath("retired-payload"), HistoryPath })
        {
            CheckPath(path);
        }
        foreach (var name in StateFiles)
        {
            CheckPath(StatePath(name));
        }
        if (Directory.Exists(_state) && Directory.EnumerateFileSystemEntries(_state).Any(path =>
                Path.GetFileName(path) is not (PayloadName or "retired-payload") && !StateFiles.Contains(Path.GetFileName(path))))
        {
            throw new IOException("Unrecognized files in the MusicSync backup directory.");
        }
    }

    private static void CheckPath(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                    throw new IOException($"The patch cannot use a redirected path: {current}");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private Receipt? ReadReceipt()
    {
        var path = StatePath("receipt.json");
        if (!File.Exists(path))
        {
            return null;
        }
        return ReadReceiptFile(path);
    }

    private Receipt ReadReceiptFile(string path)
    {
        CheckPath(path);
        var receipt = JsonSerializer.Deserialize<Receipt>(File.ReadAllBytes(path));
        if (receipt is null || !_builds.Any(build => build.Version == receipt.Version &&
                (receipt.Originals is null || receipt.Originals == build.Originals) &&
                (receipt.ProductVersion is null || receipt.ProductVersion == build.ProductVersion)) || !Enum.IsDefined(receipt.Phase) ||
            !IsHash(receipt.Patched.Asar) || !IsHash(receipt.Patched.Exe) || !IsHash(receipt.AgentHash) ||
            !IsHash(receipt.ConfigHash) || !IsHash(receipt.BundleHash) ||
            (receipt.SettingsHash is not null && !IsHash(receipt.SettingsHash)) ||
            (receipt.PendingSettingsHash is not null && !IsHash(receipt.PendingSettingsHash)))
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
        CheckFile(AgentName, hash => hash == receipt.AgentHash);
        CheckFile(ConfigName, hash => MatchesConfig(receipt, hash));

        void CheckFile(string name, Func<string, bool> matches)
        {
            var file = Path.Combine(path, name);
            CheckPath(file);
            if ((!allowMissing || File.Exists(file) || Directory.Exists(file)) && !matches(Hash(file)))
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

    private static bool MatchesConfig(Receipt receipt, string hash) =>
        hash == (receipt.SettingsHash ?? receipt.ConfigHash) || hash == receipt.PendingSettingsHash;

    private static string GetBundleHash(string agent, string config)
    {
        var hashes = AsarPatch.AddedFiles.Append(AsarPatch.PreloadName).Select(name => Hash(AsarPatch.ReadScript(name)))
            .Prepend(config).Prepend(agent);
        return Hash(Encoding.ASCII.GetBytes(string.Concat(hashes)));
    }

    private static void ReplaceIfMatching(string source, string destination, string expectedHash, string? sourceHash = null)
    {
        CheckPath(source);
        CheckPath(destination);
        if (Hash(destination) != expectedHash || (sourceHash is not null && Hash(source) != sourceHash))
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
        // A read-compatible writable image guard prevents CreateProcess during the two-file commit.
        using var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(file));
    }

    internal static FileStream GuardExecutable(string path)
    {
        CheckPath(path);
        return File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete);
    }

    private static string Hash(ReadOnlySpan<byte> content) => Convert.ToHexString(SHA256.HashData(content));

    private static bool IsHash(string? value) => value?.Length == 64 && value.All(Uri.IsHexDigit);

    internal readonly record struct Pair(string Asar, string Exe);
    internal enum InstallCheckpoint { AsarReplaced, PairReplaced }
    private sealed record Receipt(string Version, Pair Patched, string AgentHash, string ConfigHash, string BundleHash,
        Phase Phase, string? SettingsHash = null, string? PendingSettingsHash = null,
        Pair? Originals = null, string? ProductVersion = null);
    private enum Phase { Installing = 1, Installed, Removing }
}
