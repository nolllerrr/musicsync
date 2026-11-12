using System.Text.Json.Nodes;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MusicSync.Patch.Tests;

[TestClass]
public sealed class AutoRecoveryTests
{
    [TestMethod]
    public void RecoveryIsOptInAndDisabledCheckWritesNothing()
    {
        using var app = new RecoveryFixture();
        Assert.IsFalse(app.Controller.IsEnabled(app.Root));
        StringAssert.Contains(app.Controller.CheckOnce(app.Root), "выключено");
        Assert.IsFalse(Directory.Exists(app.State));
        Assert.AreEqual(0, app.Repairs);
        Assert.AreEqual(0, app.Scheduler.Registered);
    }

    [TestMethod]
    public void EnableCopiesVerifiedWorkerOutsideYandexAndRegistersCurrentUserTask()
    {
        using var app = new RecoveryFixture();
        app.Enable();
        Assert.IsTrue(app.Controller.IsEnabled(app.Root));
        Assert.AreEqual(1, app.Scheduler.Registered);
        Assert.IsTrue(File.Exists(app.Scheduler.Worker));
        Assert.IsFalse(app.Scheduler.Worker!.StartsWith(app.Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        CollectionAssert.AreEqual(File.ReadAllBytes(app.Source), File.ReadAllBytes(app.Scheduler.Worker));
    }

    [TestMethod]
    public void SupportedUpdateIsRecoveredOnceAndRepeatedTriggerDoesNotReinstall()
    {
        using var app = new RecoveryFixture();
        app.Enable();
        StringAssert.Contains(app.Controller.CheckOnce(app.Root), "восстановлен");
        app.Controller.CheckOnce(app.Root);
        Assert.AreEqual(1, app.Repairs);
        Assert.IsTrue(File.Exists(Path.Combine(app.State, "recovery.jsonl")));
    }

    [TestMethod]
    [DataRow("Unsupported")]
    [DataRow("HashMismatch")]
    [DataRow("Damaged")]
    [DataRow("Interrupted")]
    [DataRow("Unsafe")]
    public void UnsafeOrUnsupportedDiagnosisNeverAttemptsRepair(string status)
    {
        using var app = new RecoveryFixture();
        app.Observation = new RecoveryObservation(false, "new-version", status, "Ожидайте совместимого обновления MusicSync.");
        app.Enable();
        app.Controller.CheckOnce(app.Root);
        app.Controller.CheckOnce(app.Root);
        Assert.AreEqual(0, app.Repairs);
        Assert.HasCount(1, File.ReadAllLines(Path.Combine(app.State, "recovery.jsonl")));
    }

    [TestMethod]
    public void RunningPlayerIsDeferredWithoutConsumingAttemptOrClosingIt()
    {
        using var app = new RecoveryFixture { Running = true };
        app.Enable();
        StringAssert.Contains(app.Controller.CheckOnce(app.Root), "отложено");
        Assert.AreEqual(0, app.Repairs);
        Assert.AreEqual(0, JsonNode.Parse(File.ReadAllText(Path.Combine(app.State, "attempts.json")))!["Counts"]!.AsObject().Count);
        app.Running = false;
        app.Controller.CheckOnce(app.Root);
        Assert.AreEqual(1, app.Repairs);
    }

    [TestMethod]
    public void FailedRecoveryHasPersistentFiniteBudgetAcrossControllerInstances()
    {
        using var app = new RecoveryFixture { FailRepair = true };
        app.Enable();
        for (var index = 0; index < 8; index++)
            app.CreateController().CheckOnce(app.Root);
        Assert.AreEqual(RecoveryController.MaximumAttempts, app.Repairs);
        StringAssert.Contains(app.Controller.CheckOnce(app.Root), "трёх попыток");
    }

    [TestMethod]
    public void NewBuildGetsItsOwnBudgetAndReturningToOldBuildDoesNotResetBudget()
    {
        using var app = new RecoveryFixture { FailRepair = true };
        app.Enable();
        for (var index = 0; index < 3; index++) app.Controller.CheckOnce(app.Root);
        app.Observation = app.Observation with { Fingerprint = "another-verified-build" };
        app.Controller.CheckOnce(app.Root);
        Assert.AreEqual(4, app.Repairs);
        app.Observation = app.Observation with { Fingerprint = "verified-build" };
        app.Controller.CheckOnce(app.Root);
        Assert.AreEqual(4, app.Repairs);
    }

    [TestMethod]
    public void DisableRemovesTaskAndVerifiedWorkerAndStopsFutureRepairs()
    {
        using var app = new RecoveryFixture();
        app.Enable();
        var worker = app.Scheduler.Worker!;
        app.Controller.Disable(app.Root);
        Assert.AreEqual(1, app.Scheduler.Removed);
        Assert.IsFalse(app.Controller.IsEnabled(app.Root));
        Assert.IsFalse(File.Exists(worker));
        app.Controller.CheckOnce(app.Root);
        Assert.AreEqual(0, app.Repairs);
    }

    [TestMethod]
    public void TaskRemovalFailureStillDurablyRevokesRecovery()
    {
        using var app = new RecoveryFixture();
        app.Enable();
        app.Scheduler.FailRemove = true;
        Assert.ThrowsExactly<IOException>(() => app.Controller.Disable(app.Root));
        Assert.IsFalse(app.CreateController().IsEnabled(app.Root));
        app.Controller.CheckOnce(app.Root);
        Assert.AreEqual(0, app.Repairs);
    }

    [TestMethod]
    public void RegistrationFailureLeavesWorkerDisabled()
    {
        using var app = new RecoveryFixture();
        app.Scheduler.FailRegister = true;
        Assert.ThrowsExactly<IOException>(app.Enable);
        Assert.IsFalse(app.Controller.IsEnabled(app.Root));
        app.Controller.CheckOnce(app.Root);
        Assert.AreEqual(0, app.Repairs);
    }

    [TestMethod]
    public void TamperedWorkerIsNeitherExecutedNorDeleted()
    {
        using var app = new RecoveryFixture();
        app.Enable();
        var worker = app.Scheduler.Worker!;
        File.WriteAllText(worker, "unrecognized file");
        Assert.ThrowsExactly<IOException>(() => app.Controller.CheckOnce(app.Root));
        Assert.AreEqual(0, app.Repairs);
        app.Controller.Disable(app.Root);
        Assert.AreEqual("unrecognized file", File.ReadAllText(worker));
    }

    [TestMethod]
    public async Task TwoRecoveryInstancesShareOneExclusiveLock()
    {
        using var app = new RecoveryFixture();
        using var entered = new ManualResetEventSlim();
        using var proceed = new ManualResetEventSlim();
        app.BeforeRepair = () => { entered.Set(); Assert.IsTrue(proceed.Wait(TimeSpan.FromSeconds(10))); };
        app.Enable();
        var first = Task.Run(() => app.Controller.CheckOnce(app.Root));
        try
        {
            Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(10)));
            StringAssert.Contains(app.CreateController().CheckOnce(app.Root), "уже работает");
            Assert.AreEqual(1, app.Repairs);
        }
        finally { proceed.Set(); }
        await first;
        Assert.AreEqual(1, app.Repairs);
    }

    [TestMethod]
    public void AttemptIsDurableBeforeRepairStarts()
    {
        using var app = new RecoveryFixture();
        app.BeforeRepair = () =>
        {
            var counts = JsonNode.Parse(File.ReadAllText(Path.Combine(app.State, "attempts.json")))!["Counts"]!.AsObject();
            Assert.AreEqual(1, counts.Single().Value!.GetValue<int>());
        };
        app.Enable();
        app.Controller.CheckOnce(app.Root);
        Assert.AreEqual(1, app.Repairs);
    }

    [TestMethod]
    public void ExistingUnknownFilesAndEndpointArePreserved()
    {
        using var app = new RecoveryFixture();
        app.Enable();
        var unknown = Path.Combine(app.State, "notes.txt");
        var endpoint = Path.Combine(Path.GetDirectoryName(app.State)!, "together.json");
        File.WriteAllText(unknown, "user data");
        File.WriteAllText(endpoint, "endpoint");
        app.Controller.CheckOnce(app.Root);
        app.Controller.Disable(app.Root);
        Assert.AreEqual("user data", File.ReadAllText(unknown));
        Assert.AreEqual("endpoint", File.ReadAllText(endpoint));
    }

    [TestMethod]
    public void SchedulerXmlUsesInteractiveLeastPrivilegeAndFiniteWorkerTimeout()
    {
        var xml = XDocument.Parse(WindowsRecoveryScheduler.BuildXml(@"C:\Users\Me\MusicSync & tools\worker.exe",
            @"C:\Users\Me\Yandex Music", "S-1-5-21-123", DateTimeOffset.Parse("2026-11-12T12:00:00+03:00")));
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        Assert.AreEqual("LeastPrivilege", xml.Descendants(ns + "RunLevel").Single().Value);
        Assert.AreEqual("InteractiveToken", xml.Descendants(ns + "LogonType").Single().Value);
        Assert.AreEqual("IgnoreNew", xml.Descendants(ns + "MultipleInstancesPolicy").Single().Value);
        Assert.AreEqual("PT15M", xml.Descendants(ns + "Interval").Single().Value);
        Assert.AreEqual("PT5M", xml.Descendants(ns + "ExecutionTimeLimit").Single().Value);
        Assert.AreEqual("recover \"C:\\Users\\Me\\Yandex Music\"", xml.Descendants(ns + "Arguments").Single().Value);
        Assert.IsFalse(xml.Descendants(ns + "RestartOnFailure").Any());
    }

    [TestMethod]
    public void SchedulerXmlIsAcceptedByWindowsTaskDefinitionWithoutRegisteringTask()
    {
        var type = Type.GetTypeFromProgID("Schedule.Service")!;
        dynamic service = Activator.CreateInstance(type)!;
        dynamic? definition = null;
        try
        {
            service.Connect();
            definition = service.NewTask(0);
            definition.XmlText = WindowsRecoveryScheduler.BuildXml(@"C:\MusicSync\worker.exe", @"C:\YandexMusic",
                System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value, DateTimeOffset.Now);
            Assert.IsTrue(((string)definition.XmlText).Contains("LeastPrivilege", StringComparison.Ordinal));
            // Deliberately never calls RegisterTask/RegisterTaskDefinition: this is an in-memory schema check.
        }
        finally
        {
            if (definition is not null) Marshal.FinalReleaseComObject(definition);
            Marshal.FinalReleaseComObject(service);
        }
    }

    [TestMethod]
    public void RecoveryUsesRealInstallerTransactionAndPreservesInstalledEndpoint()
    {
        using var app = new InstallationFixture();
        using var external = new RecoveryFixture();
        app.Run(install: true, InstallationFixture.Endpoint);
        File.Copy(Path.Combine(app.State, "original.asar"), app.Asar, overwrite: true);
        File.Copy(Path.Combine(app.State, "original.exe"), app.Exe, overwrite: true);
        var root = Path.GetDirectoryName(app.Exe)!;
        var repairs = 0;
        var controller = new RecoveryController(Path.GetDirectoryName(external.State)!, external.Scheduler, () => external.Source,
            _ =>
            {
                var diagnosis = app.Open().Diagnose();
                return new RecoveryObservation(diagnosis.CanRepair, diagnosis.Fingerprint, diagnosis.Status.ToString(), diagnosis.Message);
            }, (_, worker) => { repairs++; app.Open().RunAutomatic(worker); }, _ => false);
        controller.Enable(root);
        StringAssert.Contains(controller.CheckOnce(root), "восстановлен");
        controller.CheckOnce(root);
        Assert.AreEqual(1, repairs);
        Assert.AreEqual(InstallationFixture.Endpoint, app.Open().ReadInstalledEndpoint());
        app.Run(install: false);
        Assert.AreEqual(app.Originals, app.TargetHashes());
        controller.Disable(root);
    }

    private sealed class RecoveryFixture : IDisposable
    {
        private readonly string _temporary = Path.Combine(Path.GetTempPath(), "musicsync-recovery-test-" + Guid.NewGuid().ToString("N"));
        internal string Root { get; }
        internal string Source { get; }
        internal string State => Path.Combine(_temporary, "external", RecoveryController.RootKey(Root));
        internal FakeScheduler Scheduler { get; } = new();
        internal RecoveryObservation Observation = new(true, "verified-build", "PatchMissing", "Можно восстановить.");
        internal bool Running;
        internal bool FailRepair;
        internal int Repairs;
        internal Action? BeforeRepair;
        internal RecoveryController Controller { get; }

        internal RecoveryFixture()
        {
            Root = Path.Combine(_temporary, "YandexMusic");
            Source = Path.Combine(_temporary, "installer.exe");
            Directory.CreateDirectory(Root);
            File.WriteAllText(Source, "test-only worker fixture");
            Controller = CreateController();
        }

        internal RecoveryController CreateController() => new(Path.Combine(_temporary, "external"), Scheduler, () => Source,
            _ => Observation, (_, worker) =>
            {
                Interlocked.Increment(ref Repairs);
                BeforeRepair?.Invoke();
                if (FailRepair) throw new IOException("simulated file access failure");
                Observation = new RecoveryObservation(false, "installed", "Installed", "Патч установлен.");
            }, _ => Running);

        internal void Enable() => Controller.Enable(Root);
        public void Dispose() => Directory.Delete(_temporary, recursive: true);
    }

    private sealed class FakeScheduler : IRecoveryScheduler
    {
        internal int Registered;
        internal int Removed;
        internal string? Worker;
        internal bool FailRegister;
        internal bool FailRemove;
        public void Register(string name, string worker, string root)
        {
            if (FailRegister) throw new IOException("simulated task registration failure");
            Registered++;
            Worker = worker;
        }
        public void Remove(string name)
        {
            if (FailRemove) throw new IOException("simulated task removal failure");
            Removed++;
        }
    }
}
