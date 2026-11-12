using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

internal static class AutoRecovery
{
    private static RecoveryController Controller() => new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MusicSync", "Recovery"),
        new WindowsRecoveryScheduler(), WorkerSource,
        root =>
        {
            var diagnosis = new PatchInstallation(root).Diagnose();
            return new RecoveryObservation(diagnosis.CanRepair && diagnosis.Status is
                InstallationStatus.SupportedOriginal or InstallationStatus.PatchMissing,
                diagnosis.Fingerprint, diagnosis.Status.ToString(), diagnosis.Message);
        },
        (root, worker) => new PatchInstallation(root).RunAutomatic(worker), IsApplicationRunning);

    internal static bool IsEnabled(string root) => Controller().IsEnabled(root);
    internal static void Enable(string root) => Controller().Enable(root);
    internal static void Disable(string root) => Controller().Disable(root);
    internal static string CheckOnce(string root) => Controller().CheckOnce(root);

    [UnconditionalSuppressMessage("SingleFile", "IL3000", Justification = "Only a standalone published installer can be copied as a recovery worker.")]
    private static string WorkerSource() => typeof(AutoRecovery).Assembly.Location.Length == 0 && Environment.ProcessPath is string path
        ? path : throw new InvalidOperationException("Для включения восстановления запустите собранный MusicSync Together из папки dist.");

    internal static bool IsApplicationRunning(string root) => YandexMusicRestart.IsRunning(root);
}

internal sealed record RecoveryObservation(bool CanRepair, string Fingerprint, string Status, string Message);

internal interface IRecoveryScheduler
{
    void Register(string name, string worker, string root);
    void Remove(string name);
}

// Kept separate from Windows scheduling so all state transitions are tested in temporary directories.
internal sealed class RecoveryController(string directory, IRecoveryScheduler scheduler, Func<string> workerSource,
    Func<string, RecoveryObservation> diagnose, Action<string, string> repair, Func<string, bool> isRunning)
{
    internal const int MaximumAttempts = 3;
    private const string RegistrationFile = "registration.json";
    private const string AttemptsFile = "attempts.json";
    private const long MaximumLogBytes = 1024 * 1024;

    private sealed record Registration(string Root, string Worker, string WorkerHash, bool Enabled);
    private sealed record Attempts(Dictionary<string, int> Counts, string? LastOutcome = null);

    internal bool IsEnabled(string root)
    {
        var normalized = NormalizeRoot(root);
        var state = StateDirectory(normalized);
        SafePath(state);
        var registration = ReadRegistration(state, normalized);
        return registration is { Enabled: true };
    }

    internal void Enable(string root)
    {
        var normalized = NormalizeRoot(root);
        if (!Directory.Exists(normalized))
            throw new DirectoryNotFoundException("Не найден каталог Яндекс Музыки.");
        SafePath(normalized);
        var state = StateDirectory(normalized);
        SafePath(state);
        Directory.CreateDirectory(state);
        using var recoveryLock = AcquireLock(state);
        var source = workerSource();
        SafePath(source);
        var hash = Hash(source);
        var name = "MusicSync.Recovery-" + hash + ".exe";
        var destination = Path.Combine(state, name);
        SafePath(destination);
        if (File.Exists(destination))
        {
            if (Hash(destination) != hash)
                throw new IOException("Внешний восстановитель MusicSync был изменён. Файл сохранён для проверки.");
        }
        else
        {
            using var sourceFile = File.OpenRead(source);
            using var destinationFile = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            sourceFile.CopyTo(destinationFile);
            destinationFile.Flush(flushToDisk: true);
        }
        if (Hash(destination) != hash)
            throw new IOException("Не удалось проверить копию восстановителя MusicSync.");

        // Register only an inert worker first. A crash before the final save leaves recovery disabled.
        var previous = ReadRegistration(state, normalized);
        Save(state, RegistrationFile, new Registration(normalized, name, hash, false));
        scheduler.Register(TaskName(normalized), destination, normalized);
        Save(state, RegistrationFile, new Registration(normalized, name, hash, true));
        if (previous is not null && previous.Worker != name)
            RemoveVerifiedWorker(state, previous);
    }

    internal void Disable(string root)
    {
        var normalized = NormalizeRoot(root);
        var state = StateDirectory(normalized);
        SafePath(state);
        if (!Directory.Exists(state))
            return;
        using var recoveryLock = AcquireLock(state);
        var registration = ReadRegistration(state, normalized);
        if (registration is not null)
            Save(state, RegistrationFile, registration with { Enabled = false });
        // Revocation is durable before removing the task, including when Task Scheduler is unavailable.
        scheduler.Remove(TaskName(normalized));
        if (registration is not null)
            RemoveVerifiedWorker(state, registration);
    }

    internal string CheckOnce(string root)
    {
        var normalized = NormalizeRoot(root);
        var state = StateDirectory(normalized);
        SafePath(state);
        if (!Directory.Exists(state))
            return "Автовосстановление выключено.";
        FileStream recoveryLock;
        try { recoveryLock = AcquireLock(state); }
        catch (IOException) { return "Восстановитель уже работает."; }
        using (recoveryLock)
        {
            var registration = ReadRegistration(state, normalized);
            if (registration is not { Enabled: true })
                return "Автовосстановление выключено.";
            var worker = Path.Combine(state, registration.Worker);
            SafePath(worker);
            if (!File.Exists(worker) || Hash(worker) != registration.WorkerHash)
                throw new IOException("Проверка внешнего восстановителя MusicSync не пройдена.");
            var attempts = Read<Attempts>(state, AttemptsFile) ?? new Attempts([]);
            if (attempts.Counts is null || attempts.Counts.Any(pair => pair.Value < 0 || pair.Value > MaximumAttempts))
                throw new InvalidDataException("Повреждён журнал попыток автовосстановления.");
            var observation = diagnose(normalized);
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(observation.Fingerprint + ":" + registration.WorkerHash)));
            if (!observation.CanRepair)
                return Outcome(state, attempts, key, observation.Status, observation.Message);
            if (isRunning(normalized))
                return Outcome(state, attempts, key, "Deferred", "Восстановление отложено: закройте Яндекс Музыку обычным способом.");
            var count = attempts.Counts.GetValueOrDefault(key);
            if (count >= MaximumAttempts)
                return Outcome(state, attempts, key, "AttemptLimit", "Автовосстановление приостановлено после трёх попыток. Откройте установщик для диагностики.");

            // Count before modifying Yandex files: a power loss or killed worker consumes an attempt too.
            attempts.Counts[key] = count + 1;
            Save(state, AttemptsFile, attempts);
            try
            {
                repair(normalized, worker);
                var after = diagnose(normalized);
                if (after.Status != nameof(InstallationStatus.Installed))
                    throw new IOException("Проверка установленного патча не пройдена: " + after.Message);
                return Outcome(state, attempts, key, "Recovered", "Патч MusicSync восстановлен. Яндекс Музыку можно открыть.");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or JsonException)
            {
                return Outcome(state, attempts, key, "Failed", "Автовосстановление не выполнено: " + error.Message);
            }
        }
    }

    private static string Outcome(string state, Attempts attempts, string fingerprint, string status, string message)
    {
        var outcome = fingerprint + ":" + status + ":" + message;
        if (attempts.LastOutcome == outcome)
            return message;
        Save(state, AttemptsFile, attempts with { LastOutcome = outcome });
        var log = Path.Combine(state, "recovery.jsonl");
        SafePath(log);
        if (!File.Exists(log) || new FileInfo(log).Length < MaximumLogBytes)
        {
            using var stream = new FileStream(log, FileMode.Append, FileAccess.Write, FileShare.Read);
            stream.Write(JsonSerializer.SerializeToUtf8Bytes(new { TimeUtc = DateTimeOffset.UtcNow, Status = status, Fingerprint = fingerprint, Message = message }));
            stream.WriteByte((byte)'\n');
            stream.Flush(flushToDisk: true);
        }
        return message;
    }

    private static Registration? ReadRegistration(string state, string root)
    {
        var registration = Read<Registration>(state, RegistrationFile);
        if (registration is not null && (registration.Root != root || registration.WorkerHash is not { Length: 64 } ||
            !registration.WorkerHash.All(Uri.IsHexDigit) || registration.Worker != "MusicSync.Recovery-" + registration.WorkerHash + ".exe"))
            throw new InvalidDataException("Повреждена регистрация автовосстановления MusicSync.");
        return registration;
    }

    private static T? Read<T>(string state, string name)
    {
        var path = Path.Combine(state, name);
        SafePath(path);
        if (!File.Exists(path))
            return default;
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<T>(stream) ?? throw new InvalidDataException("Пустой файл состояния автовосстановления.");
    }

    private static void Save<T>(string state, string name, T value)
    {
        var destination = Path.Combine(state, name);
        var next = destination + "." + Guid.NewGuid().ToString("N") + ".next";
        SafePath(destination);
        SafePath(next);
        try
        {
            using (var stream = new FileStream(next, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, value);
                stream.Flush(flushToDisk: true);
            }
            SafePath(destination);
            File.Move(next, destination, overwrite: true);
        }
        finally { File.Delete(next); }
    }

    private static void RemoveVerifiedWorker(string state, Registration registration)
    {
        var worker = Path.Combine(state, registration.Worker);
        SafePath(worker);
        if (File.Exists(worker) && Hash(worker) == registration.WorkerHash)
        {
            try { File.Delete(worker); }
            catch (IOException) { } // The executing copy may remain locked until it exits; it is already disabled.
        }
    }

    private static FileStream AcquireLock(string state)
    {
        var path = Path.Combine(state, "recovery.lock");
        SafePath(path);
        return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.DeleteOnClose);
    }

    private string StateDirectory(string root) => Path.Combine(Path.GetFullPath(directory), RootKey(root));
    internal static string NormalizeRoot(string root) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)).ToUpperInvariant();
    internal static string RootKey(string root) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(NormalizeRoot(root))))[..20];
    internal static string TaskName(string root) => "MusicSync-Recovery-" + WindowsIdentity.GetCurrent().User!.Value + "-" + RootKey(root);
    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    internal static void SafePath(string path)
    {
        for (var cursor = Path.GetFullPath(path); cursor is not null; cursor = Path.GetDirectoryName(cursor))
        {
            try
            {
                if ((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Ссылки и перенаправленные пути не поддерживаются для автовосстановления.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}

internal sealed class WindowsRecoveryScheduler : IRecoveryScheduler
{
    public void Register(string name, string worker, string root) => WithFolder(folder =>
    {
        var task = folder.RegisterTask(name, BuildXml(worker, root, WindowsIdentity.GetCurrent().User!.Value, DateTimeOffset.Now),
            6 /* CREATE_OR_UPDATE */, null, null, 3 /* INTERACTIVE_TOKEN */, null);
        Marshal.FinalReleaseComObject(task);
    });

    public void Remove(string name) => WithFolder(folder =>
    {
        try { folder.DeleteTask(name, 0); }
        catch (COMException error) when (error.ErrorCode == unchecked((int)0x80070002)) { }
    });

    private static void WithFolder(Action<dynamic> action)
    {
        var type = Type.GetTypeFromProgID("Schedule.Service") ?? throw new InvalidOperationException("Планировщик заданий Windows недоступен.");
        dynamic service = Activator.CreateInstance(type)!;
        object? folder = null;
        try
        {
            service.Connect();
            folder = service.GetFolder("\\");
            action(folder);
        }
        finally
        {
            if (folder is not null) Marshal.FinalReleaseComObject(folder);
            Marshal.FinalReleaseComObject(service);
        }
    }

    internal static string BuildXml(string worker, string root, string userSid, DateTimeOffset now)
    {
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        XElement Element(string name, object? value) => new(ns + name, value);
        return new XDocument(new XElement(ns + "Task", new XAttribute("version", "1.2"),
            Element("RegistrationInfo", Element("Description", "Добровольное восстановление MusicSync после обновлений Яндекс Музыки.")),
            Element("Triggers", new object[]
            {
                Element("LogonTrigger", new object[] { Element("Enabled", true), Element("UserId", userSid), Element("Delay", "PT1M") }),
                Element("TimeTrigger", new object[]
                {
                    Element("Repetition", new object[] { Element("Interval", "PT15M"), Element("StopAtDurationEnd", false) }),
                    Element("StartBoundary", now.AddMinutes(1).ToString("yyyy-MM-dd'T'HH:mm:sszzz")), Element("Enabled", true)
                })
            }),
            Element("Principals", new XElement(ns + "Principal", new XAttribute("id", "CurrentUser"),
                Element("UserId", userSid), Element("LogonType", "InteractiveToken"), Element("RunLevel", "LeastPrivilege"))),
            Element("Settings", new object[]
            {
                Element("MultipleInstancesPolicy", "IgnoreNew"), Element("DisallowStartIfOnBatteries", false),
                Element("StopIfGoingOnBatteries", false), Element("StartWhenAvailable", true), Element("ExecutionTimeLimit", "PT5M"),
                Element("Enabled", true)
            }),
            new XElement(ns + "Actions", new XAttribute("Context", "CurrentUser"), Element("Exec", new object[]
            {
                Element("Command", worker), Element("Arguments", "recover \"" + root + "\""),
                Element("WorkingDirectory", Path.GetDirectoryName(worker))
            })))).ToString();
    }
}
