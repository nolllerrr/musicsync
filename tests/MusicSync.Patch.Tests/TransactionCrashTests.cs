using System.ComponentModel;
using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MusicSync.Patch.Tests;

[TestClass]
public sealed class TransactionCrashTests
{
    [TestMethod]
    [DataRow("original.asar")]
    [DataRow("original.exe")]
    public void InterruptedRemovalWithAlreadyRestoredTargetsDoesNotRequireDeletedBackup(string deletedBackup)
    {
        using var app = new InstallationFixture();
        app.Run(install: true, InstallationFixture.Endpoint);
        var receipt = app.ReadReceipt();
        receipt["Phase"] = 3; // Removing, with an interruption during final cleanup.
        File.WriteAllText(app.Receipt, receipt.ToJsonString());
        File.Copy(Path.Combine(app.State, "original.asar"), app.Asar, overwrite: true);
        File.Copy(Path.Combine(app.State, "original.exe"), app.Exe, overwrite: true);
        File.Delete(Path.Combine(app.State, deletedBackup));

        app.Run(install: false);

        Assert.AreEqual(app.Originals, app.TargetHashes());
        Assert.IsFalse(Directory.Exists(app.State));
        Assert.IsFalse(Directory.Exists(app.Payload));
        var archive = Directory.GetDirectories(app.History).Single();
        Assert.IsTrue(File.Exists(Path.Combine(archive, "receipt.json")));
        Assert.IsFalse(File.Exists(Path.Combine(archive, deletedBackup)));
        app.Run(install: false);
        Assert.AreEqual(app.Originals, app.TargetHashes());
    }

    [TestMethod]
    public void SupportedUpdateRecoversEndpointFromInterruptedStagedPayload()
    {
        using var app = new InstallationFixture();
        var endpoint = new Uri("wss://staged-recovery.example.test/ws");
        PrepareInterruptedStagedPayload(app, endpoint);
        var updated = app.UpdateToSupportedBuild();

        app.Run(true);

        Assert.AreEqual(InstallationStatus.Installed, app.Open().Diagnose().Status);
        Assert.AreEqual(endpoint, app.Open().ReadInstalledEndpoint());
        Assert.AreEqual(updated.Originals, app.BackupHashes());
        var archive = Directory.GetDirectories(app.History).Single();
        Assert.IsTrue(File.Exists(Path.Combine(archive, "musicsync-together", "config.json")));
        app.Run(install: false);
        Assert.AreEqual(updated.Originals, app.TargetHashes());
    }

    [TestMethod]
    public void LaterInstallRecoversEndpointFromArchivedStagedPayloadWithoutActiveReceipt()
    {
        using var app = new InstallationFixture();
        var endpoint = new Uri("wss://archived-staging.example.test/ws");
        PrepareInterruptedStagedPayload(app, endpoint);
        var updated = app.UpdateToSupportedBuild();
        app.Run(install: false); // Retire the interrupted generation without changing the new official pair.
        Assert.IsFalse(Directory.Exists(app.State));
        Assert.IsFalse(Directory.Exists(app.Payload));
        Assert.AreEqual(updated.Originals, app.TargetHashes());

        app.Run(true);

        Assert.AreEqual(endpoint, app.Open().ReadInstalledEndpoint());
        Assert.AreEqual(InstallationStatus.Installed, app.Open().Diagnose().Status);
        app.Run(install: false);
        Assert.AreEqual(updated.Originals, app.TargetHashes());
    }

    [TestMethod]
    [DataRow("AsarReplaced")]
    [DataRow("PairReplaced")]
    public void InstallFailureRestoresUpdatedOriginalsAndCanRetryWithoutLosingEndpoint(string checkpointName)
    {
        using var app = new InstallationFixture();
        var endpoint = new Uri("wss://rollback-preservation.example.test/ws");
        app.Run(install: true, endpoint);
        var updated = app.UpdateToSupportedBuild();
        var interruptedAt = Enum.Parse<PatchInstallation.InstallCheckpoint>(checkpointName);
        var reached = false;
        var installation = new PatchInstallation(app.Root, app.Builds, checkpoint =>
        {
            if (checkpoint != interruptedAt) return;
            reached = true;
            throw new IOException("Injected interruption after " + checkpointName);
        });

        Assert.ThrowsExactly<IOException>(() => installation.Run(true, endpoint: null, agent: app.AgentPath, music: null));

        Assert.IsTrue(reached);
        Assert.AreEqual(updated.Originals, app.TargetHashes());
        Assert.IsFalse(Directory.Exists(app.State));
        Assert.IsFalse(Directory.Exists(app.Payload));
        Assert.AreEqual(InstallationStatus.SupportedOriginal, app.Open().Diagnose().Status);
        var archive = Directory.GetDirectories(app.History).Single();
        Assert.AreEqual(endpoint, PatchSettings.Read(Path.Combine(archive, "retired-payload", "config.json")));

        app.Run(true);

        Assert.AreEqual(InstallationStatus.Installed, app.Open().Diagnose().Status);
        Assert.AreEqual(endpoint, app.Open().ReadInstalledEndpoint());
        Assert.AreEqual(updated.Originals, app.BackupHashes());
        app.Run(install: false);
        Assert.AreEqual(updated.Originals, app.TargetHashes());
    }

    [TestMethod]
    public void EmptyDirectoryAfterCompletedCleanupIsDiagnosedReadOnlyAndCanBeReused()
    {
        using var app = new InstallationFixture();
        Directory.CreateDirectory(app.State);
        var before = app.Snapshot();

        var diagnosis = app.Open().Diagnose();

        Assert.AreEqual(InstallationStatus.SupportedOriginal, diagnosis.Status);
        Assert.IsTrue(diagnosis.CanRepair);
        CollectionAssert.AreEqual(before, app.Snapshot());
        Assert.IsTrue(Directory.Exists(app.State));

        app.Run(true);

        Assert.AreEqual(InstallationStatus.Installed, app.Open().Diagnose().Status);
        Assert.AreEqual(app.Originals, app.BackupHashes());
        app.Run(install: false);
        Assert.AreEqual(app.Originals, app.TargetHashes());
    }

    [TestMethod]
    public async Task ExecutableGuardPreventsLaunchUntilAtomicReplacementMakesPairComplete()
    {
        var temporary = Directory.CreateTempSubdirectory("musicsync-executable-guard-").FullName;
        try
        {
            var executable = Path.Combine(temporary, "fixture.exe");
            var replacement = Path.Combine(temporary, "replacement.exe");
            var source = Path.Combine(Environment.SystemDirectory, "where.exe");
            File.Copy(source, executable);
            File.Copy(source, replacement);
            using var guard = PatchInstallation.GuardExecutable(executable);

            Assert.ThrowsExactly<Win32Exception>(() =>
            {
                using var unexpected = Process.Start(StartInfo(executable));
                unexpected?.WaitForExit(5_000);
            });

            // ReplaceFile may read/delete the guarded old file, but a process cannot map it as an image.
            File.Replace(replacement, executable, destinationBackupFileName: null);
            using var started = Process.Start(StartInfo(executable));
            Assert.IsNotNull(started);
            var stdout = started.StandardOutput.ReadToEndAsync();
            var stderr = started.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await started.WaitForExitAsync(timeout.Token);
            await Task.WhenAll(stdout, stderr);
            Assert.AreEqual(0, started.ExitCode);
        }
        finally { Directory.Delete(temporary, recursive: true); }
    }

    private static void PrepareInterruptedStagedPayload(InstallationFixture app, Uri endpoint)
    {
        app.Run(install: true, endpoint);
        var receipt = app.ReadReceipt();
        receipt["Phase"] = 1; // Installing: receipt flushed, staged payload not yet moved into resources.
        File.WriteAllText(app.Receipt, receipt.ToJsonString());
        Directory.Move(app.Payload, Path.Combine(app.State, "musicsync-together"));
    }

    private static ProcessStartInfo StartInfo(string executable)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("/?");
        return start;
    }
}
