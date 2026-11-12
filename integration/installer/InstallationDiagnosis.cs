using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

internal sealed partial class PatchInstallation
{
    // Deliberately does not create a lock, receipt, settings, or staging file.
    internal InstallationDiagnosis Diagnose()
    {
        string? product = null, package = null;
        var fingerprint = "missing";
        InstallationDiagnosis Result(InstallationStatus status, string message, bool repair = false) =>
            new(product, package, status, message, repair, fingerprint);
        try
        {
            CheckPaths();
            if (!Directory.Exists(_root))
                return Result(InstallationStatus.Missing, "Яндекс Музыка не найдена.");
            var lockPath = Path.Combine(_root, ".musicsync-together.lock");
            CheckPath(lockPath);
            if (File.Exists(lockPath))
            {
                try { using var probe = File.Open(lockPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); }
                catch (IOException) { return Result(InstallationStatus.Busy, "Другая операция MusicSync ещё выполняется."); }
            }

            var asar = HashIfPresent(_asar);
            var exe = HashIfPresent(_exe);
            product = exe is not null ? FileVersionInfo.GetVersionInfo(_exe).ProductVersion : null;
            fingerprint = Hash(Encoding.UTF8.GetBytes($"{product}|{asar}|{exe}"));
            if (asar is not null)
            {
                try { package = AsarPatch.Inspect(_asar).Version; }
                catch (Exception error) when (error is IOException or JsonException or InvalidOperationException or ArgumentException) { }
            }
            var receipt = ReadReceipt();
            var current = new Pair(asar ?? "", exe ?? "");
            var stock = _builds.SingleOrDefault(build => build.Originals == current && build.ProductVersion == product);

            if (stock is not null)
            {
                package = AsarPatch.Inspect(_asar).Version;
                ValidateOriginalStructure(stock);
                if (receipt is not null)
                {
                    VerifyPayload(_payload, receipt, allowMissing: true);
                    VerifyPayload(StatePath("retired-payload"), receipt, allowMissing: true);
                    VerifyPayload(StatePath(PayloadName), receipt, allowMissing: true);
                    if (stock == ReceiptBuild(receipt) && receipt.Phase is Phase.Installing or Phase.Removing)
                        return Result(InstallationStatus.Interrupted,
                            "Операция прервана; точные исходные файлы целы. Можно безопасно завершить восстановление.", true);
                    return Result(InstallationStatus.PatchMissing,
                        "Официальные файлы восстановлены или обновлены. Патч отсутствует; сборка поддерживается.", true);
                }
                if (Directory.Exists(_state) && Directory.EnumerateFileSystemEntries(_state).Any())
                    return Result(InstallationStatus.Interrupted, "Найдена подготовка установки без журнала. Требуется проверка вручную.");
                if (Directory.Exists(_payload) || File.Exists(_payload))
                    return Result(InstallationStatus.Damaged, "Найден компонент MusicSync без журнала. Файлы сохранены; требуется проверка вручную.");
                return Result(InstallationStatus.SupportedOriginal, "Проверенная сборка. Патч можно установить.", true);
            }

            if (receipt is not null)
            {
                var build = ReceiptBuild(receipt);
                var owned = (asar is null || asar == build.Originals.Asar || asar == receipt.Patched.Asar) &&
                            (exe is null || exe == build.Originals.Exe || exe == receipt.Patched.Exe);
                if (owned && receipt.Phase is Phase.Installing or Phase.Removing)
                {
                    AssertBackups(build);
                    VerifyPayload(_payload, receipt, allowMissing: true);
                    return Result(InstallationStatus.Interrupted, "Предыдущая операция прервана. Можно восстановить проверенные файлы.", true);
                }
                if (current == receipt.Patched && product == build.ProductVersion)
                {
                    package = AsarPatch.Inspect(_asar).Version;
                    AssertBackups(build);
                    VerifyPayload(_payload, receipt, allowMissing: false);
                    return Result(InstallationStatus.Installed, "Патч установлен; файлы и резервные копии проверены.");
                }
                // A new official version is not corruption of the previous patch.
                if (product is not null && !_builds.Any(item => item.ProductVersion == product))
                    return Result(InstallationStatus.Unsupported, "Яндекс Музыка обновилась. Эта сборка пока не поддерживается; дождитесь обновления MusicSync.");
                if (asar is not null && exe is not null &&
                    asar != build.Originals.Asar && asar != receipt.Patched.Asar &&
                    exe != build.Originals.Exe && exe != receipt.Patched.Exe)
                    return Result(InstallationStatus.HashMismatch,
                        "Файлы заменены, но новая пара SHA-256 пока не проверена. Дождитесь совместимого обновления MusicSync.");
                return Result(InstallationStatus.Damaged, "Файлы отличаются от журнала и проверенных сборок. Автоматическое восстановление остановлено.");
            }
            if (asar is null || exe is null)
                return Result(InstallationStatus.Missing, "Не найдены файлы Яндекс Музыки.");
            if (!_builds.Any(item => item.ProductVersion == product))
                return Result(InstallationStatus.Unsupported, "Эта версия пока не поддерживается. Дождитесь совместимого обновления MusicSync.");
            return Result(InstallationStatus.HashMismatch, "Версия известна, но SHA-256 файлов не совпадают с проверенной сборкой.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or
                                      InvalidOperationException or ArgumentException or BadImageFormatException or System.ComponentModel.Win32Exception)
        {
            return Result(error.Message.Contains("redirected path", StringComparison.Ordinal) ||
                          error.Message.Contains("Unrecognized file", StringComparison.Ordinal)
                ? InstallationStatus.Unsafe : InstallationStatus.Damaged, error.Message);
        }
    }

    private SupportedBuild? FindOriginalBuild()
    {
        if (!File.Exists(_exe) || !File.Exists(_asar)) return null;
        var product = FileVersionInfo.GetVersionInfo(_exe).ProductVersion;
        if (!_builds.Any(build => build.ProductVersion == product)) return null;
        var pair = HashPair(_asar, _exe);
        return _builds.SingleOrDefault(build => build.ProductVersion == product && build.Originals == pair);
    }

    private void ValidateOriginalStructure(SupportedBuild build)
    {
        var info = AsarPatch.Inspect(_asar);
        if (info.Version != build.Version || info.Main != "./index.js")
            throw new InvalidDataException("Unexpected Yandex Music package version or entry point.");
        PeAsarIntegrity.Validate(_exe, info.HeaderHash);
    }

    private SupportedBuild ReceiptBuild(Receipt receipt) => _builds.Single(build =>
        build.Version == receipt.Version && (receipt.Originals is null || build.Originals == receipt.Originals) &&
        (receipt.ProductVersion is null || build.ProductVersion == receipt.ProductVersion));

    internal string RunAutomatic(string agentPath)
    {
        using var installLock = AcquireLock();
        CheckPaths();
        // Diagnose cannot probe our own exclusive lock. All state is checked again by Apply.
        var stock = FindOriginalBuild();
        if (stock is null)
            throw new InvalidOperationException("Автовосстановление разрешено только для точной проверенной исходной пары файлов.");
        EnsureApplicationIdle();
        _automatic = true;
        try { return Apply(install: true, endpoint: null, agent: agentPath, music: null); }
        finally { _automatic = false; }
    }

    private void EnsureApplicationIdle()
    {
        if (YandexMusicRestart.IsRunning(_root))
            throw new IOException("Яндекс Музыка работает. Восстановление отложено до её закрытия.");
    }
}
