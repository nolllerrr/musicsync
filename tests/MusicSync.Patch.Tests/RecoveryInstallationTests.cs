using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MusicSync.Patch.Tests;

[TestClass]
public sealed class RecoveryInstallationTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void DiagnosisVerifiesStockAndInstalledFilesWithoutChangingAnything(bool installed)
    {
        using var app = new InstallationFixture();
        if (installed) Install(app);
        var before = app.Snapshot();

        var diagnosis = app.Open().Diagnose();

        Assert.AreEqual(installed ? InstallationStatus.Installed : InstallationStatus.SupportedOriginal, diagnosis.Status);
        Assert.AreEqual("5.126.1.0", diagnosis.ProductVersion);
        Assert.AreEqual("5.126.1", diagnosis.PackageVersion);
        Assert.AreEqual(!installed, diagnosis.CanRepair);
        Assert.AreEqual(64, diagnosis.Fingerprint.Length);
        CollectionAssert.AreEqual(before, app.Snapshot());
    }

    [TestMethod]
    public void MissingInstallationDiagnosisDoesNotCreateItsDirectory()
    {
        using var app = new InstallationFixture();
        var missing = Path.Combine(app.Root, "not-installed");

        var diagnosis = new PatchInstallation(missing, app.Builds).Diagnose();

        Assert.AreEqual(InstallationStatus.Missing, diagnosis.Status);
        Assert.IsFalse(diagnosis.CanRepair);
        Assert.IsFalse(Directory.Exists(missing));
    }

    [TestMethod]
    public void UnknownProductVersionIsUnsupportedEvenWhenArchiveIsReadable()
    {
        using var app = new InstallationFixture();
        var before = app.Snapshot();
        var installation = new PatchInstallation(app.Root,
            [new SupportedBuild("5.126.1", "0.0.0.0", app.Originals)]);

        Assert.AreEqual(InstallationStatus.Unsupported, installation.Diagnose().Status);
        Assert.Throws<InvalidOperationException>(() => installation.RunAutomatic(app.AgentPath));

        CollectionAssert.AreEqual(before, app.Snapshot());
    }

    [TestMethod]
    public void UnknownHashPairIsRejectedEvenWithMatchingVersionAndValidIntegrity()
    {
        using var app = new InstallationFixture();
        app.WriteApplication("// valid archive, unverified build");
        var before = app.Snapshot();

        Assert.AreEqual(InstallationStatus.HashMismatch, app.Open().Diagnose().Status);
        Assert.Throws<InvalidOperationException>(() => app.Open().RunAutomatic(app.AgentPath));
        Assert.Throws<InvalidOperationException>(() => app.Run(true, InstallationFixture.Endpoint));

        CollectionAssert.AreEqual(before, app.Snapshot());
    }

    [TestMethod]
    public void SecondSyntheticBuildInstallsAndUninstallsToItsOwnOriginalHashes()
    {
        using var app = new InstallationFixture();
        var updated = app.UpdateToSupportedBuild();

        Install(app);

        Assert.AreEqual(InstallationStatus.Installed, app.Open().Diagnose().Status);
        Assert.AreEqual(updated.Originals, app.BackupHashes());
        Assert.AreEqual(updated.Version, app.ReadReceipt()["Version"]!.GetValue<string>());
        app.Run(false);
        Assert.AreEqual(updated.Originals, app.TargetHashes());
        Assert.IsFalse(Directory.Exists(app.State));
        Assert.IsFalse(Directory.Exists(app.Payload));
    }

    [TestMethod]
    public void OfficialUpdateIsDetectedWithoutRestoringStaleBackups()
    {
        using var app = new InstallationFixture();
        Install(app);
        var updated = app.UpdateToSupportedBuild();
        var before = app.Snapshot();

        var diagnosis = app.Open().Diagnose();

        Assert.AreEqual(InstallationStatus.PatchMissing, diagnosis.Status);
        Assert.IsTrue(diagnosis.CanRepair);
        Assert.AreEqual(updated.Version, diagnosis.PackageVersion);
        Assert.AreEqual(app.Originals, app.BackupHashes());
        Assert.AreEqual(updated.Originals, app.TargetHashes());
        CollectionAssert.AreEqual(before, app.Snapshot());
    }

    [TestMethod]
    public void AutomaticRepairArchivesOldGenerationAndPreservesEndpoint()
    {
        using var app = new InstallationFixture();
        var endpoint = new Uri("wss://rooms.example.test/ws");
        app.Run(true, endpoint);
        var updated = app.UpdateToSupportedBuild();

        app.Open().RunAutomatic(app.AgentPath);

        Assert.AreEqual(InstallationStatus.Installed, app.Open().Diagnose().Status);
        Assert.AreEqual(endpoint, app.Open().ReadInstalledEndpoint());
        Assert.AreEqual(updated.Originals, app.BackupHashes());
        var archive = Directory.GetDirectories(app.History).Single();
        Assert.AreEqual(app.Originals.Asar, Hash(Path.Combine(archive, "original.asar")));
        Assert.AreEqual(app.Originals.Exe, Hash(Path.Combine(archive, "original.exe")));
        Assert.IsTrue(File.Exists(Path.Combine(archive, "receipt.json")));
        app.Run(false);
        Assert.AreEqual(updated.Originals, app.TargetHashes());
        Assert.IsTrue(Directory.Exists(archive), "Old backups remain archived rather than being silently deleted.");
    }

    [TestMethod]
    public void RepeatedRecoveryTriggerDoesNotModifyAnAlreadyInstalledPatch()
    {
        using var app = new InstallationFixture();
        Install(app);
        app.UpdateToSupportedBuild();
        app.Open().RunAutomatic(app.AgentPath);
        var before = app.Snapshot();

        Assert.AreEqual(InstallationStatus.Installed, app.Open().Diagnose().Status);
        Assert.Throws<InvalidOperationException>(() => app.Open().RunAutomatic(app.AgentPath));

        CollectionAssert.AreEqual(before, app.Snapshot());
    }

    [TestMethod]
    public void RepairAfterInterruptedStateRetirementReadsEndpointFromVerifiedArchive()
    {
        using var app = new InstallationFixture();
        var endpoint = new Uri("wss://archived.example.test/ws");
        app.Run(true, endpoint);
        var updated = app.UpdateToSupportedBuild();
        // Model a crash after retirement completed but before the next install started.
        Directory.Move(app.Payload, Path.Combine(app.State, "retired-payload"));
        Directory.CreateDirectory(app.History);
        Directory.Move(app.State, Path.Combine(app.History, "interrupted-retirement"));

        Assert.AreEqual(InstallationStatus.SupportedOriginal, app.Open().Diagnose().Status);
        app.Open().RunAutomatic(app.AgentPath);

        Assert.AreEqual(endpoint, app.Open().ReadInstalledEndpoint());
        Assert.AreEqual(updated.Originals, app.BackupHashes());
        app.Run(false);
        Assert.AreEqual(updated.Originals, app.TargetHashes());
    }

    [TestMethod]
    public void UpdatedOfficialPairTakesPrecedenceOverOldInterruptedJournal()
    {
        using var app = new InstallationFixture();
        Install(app);
        app.InterruptAfterAsarReplacement();
        var updated = app.UpdateToSupportedBuild();

        Assert.AreEqual(InstallationStatus.PatchMissing, app.Open().Diagnose().Status);
        app.Open().RunAutomatic(app.AgentPath);

        Assert.AreEqual(updated.Originals, app.BackupHashes());
        app.Run(false);
        Assert.AreEqual(updated.Originals, app.TargetHashes());
    }

    [TestMethod]
    public void UnknownUpdateWithOldJournalIsPreservedWithoutAutomaticRollback()
    {
        using var app = new InstallationFixture();
        Install(app);
        app.InterruptAfterAsarReplacement();
        app.WriteApplication("// unknown future release", "9.999.0");
        var before = app.Snapshot();

        var diagnosis = app.Open().Diagnose();
        Assert.IsFalse(diagnosis.CanRepair);
        Assert.AreEqual(InstallationStatus.Damaged, diagnosis.Status);
        Assert.Throws<InvalidOperationException>(() => app.Open().RunAutomatic(app.AgentPath));
        Assert.Throws<InvalidOperationException>(() => app.Run(false));

        CollectionAssert.AreEqual(before, app.Snapshot());
    }

    [TestMethod]
    public void UninstallAfterKnownOfficialUpdatePreservesNewFilesAndArchivesOldBackups()
    {
        using var app = new InstallationFixture();
        Install(app);
        var updated = app.UpdateToSupportedBuild();

        app.Run(false);

        Assert.AreEqual(updated.Originals, app.TargetHashes());
        Assert.IsFalse(Directory.Exists(app.State));
        Assert.IsFalse(Directory.Exists(app.Payload));
        Assert.HasCount(1, Directory.GetDirectories(app.History));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void DamagedOrMissingPayloadIsDiagnosedWithoutMutation(bool missing)
    {
        using var app = new InstallationFixture();
        Install(app);
        var agent = Path.Combine(app.Payload, PatchInstallation.AgentName);
        if (missing) File.Delete(agent);
        else File.AppendAllText(agent, "changed outside installer");
        var before = app.Snapshot();

        var diagnosis = app.Open().Diagnose();

        Assert.AreEqual(InstallationStatus.Damaged, diagnosis.Status);
        Assert.IsFalse(diagnosis.CanRepair);
        CollectionAssert.AreEqual(before, app.Snapshot());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void UnknownFilesAreNeverDeletedDuringRecovery(bool inPayload)
    {
        using var app = new InstallationFixture();
        Install(app);
        app.UpdateToSupportedBuild();
        var unknown = Path.Combine(inPayload ? app.Payload : app.State, "user-owned.txt");
        File.WriteAllText(unknown, "preserve me");
        var before = app.Snapshot();

        var diagnosis = app.Open().Diagnose();
        Assert.IsFalse(diagnosis.CanRepair);
        Assert.Throws<IOException>(() => app.Open().RunAutomatic(app.AgentPath));

        Assert.AreEqual("preserve me", File.ReadAllText(unknown));
        CollectionAssert.AreEqual(before, app.Snapshot());
    }

    [TestMethod]
    public void PreparationWithoutReceiptIsDetectedAndRetainedForInspection()
    {
        using var app = new InstallationFixture();
        Directory.CreateDirectory(app.State);
        File.Copy(app.Asar, Path.Combine(app.State, "original.asar"));
        var before = app.Snapshot();

        var diagnosis = app.Open().Diagnose();

        Assert.AreEqual(InstallationStatus.Interrupted, diagnosis.Status);
        Assert.IsFalse(diagnosis.CanRepair);
        Assert.Throws<InvalidOperationException>(() => app.Open().RunAutomatic(app.AgentPath));
        CollectionAssert.AreEqual(before, app.Snapshot());
    }

    [TestMethod]
    public void ConcurrentInstallerLockBlocksDiagnosisAndAutomaticRepairWithoutChanges()
    {
        using var app = new InstallationFixture();
        var lockPath = Path.Combine(app.Root, ".musicsync-together.lock");
        using var held = new FileStream(lockPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
            4096, FileOptions.DeleteOnClose);
        var before = app.TargetHashes();

        Assert.AreEqual(InstallationStatus.Busy, app.Open().Diagnose().Status);
        Assert.Throws<IOException>(() => app.Open().RunAutomatic(app.AgentPath));

        Assert.AreEqual(before, app.TargetHashes());
        Assert.IsFalse(Directory.Exists(app.State));
    }

    [TestMethod]
    public void SharingViolationDuringRepairRollsBackBothFilesToNewBuild()
    {
        using var app = new InstallationFixture();
        Install(app);
        var updated = app.UpdateToSupportedBuild();
        using (var held = File.Open(app.Exe, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.Throws<IOException>(() => app.Open().RunAutomatic(app.AgentPath));
            Assert.AreEqual(updated.Originals, app.TargetHashes());
        }

        app.Open().RunAutomatic(app.AgentPath);
        Assert.AreEqual(InstallationStatus.Installed, app.Open().Diagnose().Status);
        app.Run(false);
        Assert.AreEqual(updated.Originals, app.TargetHashes());
    }

    [TestMethod]
    public void ExclusiveReadFailureDoesNotAlterInstallationOrLoseEndpoint()
    {
        using var app = new InstallationFixture();
        Install(app);
        app.UpdateToSupportedBuild();
        var before = app.Snapshot();
        using (var held = File.Open(app.Asar, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.IsFalse(app.Open().Diagnose().CanRepair);
            Assert.Throws<IOException>(() => app.Open().RunAutomatic(app.AgentPath));
        }
        CollectionAssert.AreEqual(before, app.Snapshot());
        Assert.AreEqual(InstallationFixture.Endpoint, app.Open().ReadInstalledEndpoint());
    }

    [TestMethod]
    public void LegacyReceiptWithoutBuildIdentityRemainsReadableAndUninstallable()
    {
        using var app = new InstallationFixture();
        Install(app);
        var receipt = app.ReadReceipt();
        receipt.Remove("Originals");
        receipt.Remove("ProductVersion");
        File.WriteAllText(app.Receipt, receipt.ToJsonString());

        Assert.AreEqual(InstallationStatus.Installed, app.Open().Diagnose().Status);
        app.Run(false);

        Assert.AreEqual(app.Originals, app.TargetHashes());
        Assert.IsFalse(Directory.Exists(app.State));
        Assert.IsFalse(Directory.Exists(app.Payload));
    }

    [TestMethod]
    public void InvalidReceiptIsDiagnosedReadOnly()
    {
        using var app = new InstallationFixture();
        Install(app);
        File.WriteAllText(app.Receipt, "{truncated");
        var before = app.Snapshot();

        Assert.AreEqual(InstallationStatus.Damaged, app.Open().Diagnose().Status);

        CollectionAssert.AreEqual(before, app.Snapshot());
    }

    [TestMethod]
    public void RedirectedRootIsRejectedWithoutTouchingTheRealTarget()
    {
        using var app = new InstallationFixture();
        var parent = Path.GetDirectoryName(app.Root)!;
        var alias = Path.Combine(parent, "redirected-installation");
        CreateDirectoryLink(alias, app.Root);
        try
        {
            var before = app.Snapshot();
            var installation = new PatchInstallation(alias, app.Builds);

            Assert.AreEqual(InstallationStatus.Unsafe, installation.Diagnose().Status);
            Assert.Throws<IOException>(() => installation.RunAutomatic(app.AgentPath));

            CollectionAssert.AreEqual(before, app.Snapshot());
        }
        finally { Directory.Delete(alias); }
    }

    [TestMethod]
    public void RedirectedParentIsRejectedEvenWhenInstallationItselfIsNotALink()
    {
        using var app = new InstallationFixture();
        var parent = Path.GetDirectoryName(app.Root)!;
        var alias = Path.Combine(parent, "redirected-parent");
        CreateDirectoryLink(alias, parent);
        try
        {
            var before = app.Snapshot();
            var installation = new PatchInstallation(Path.Combine(alias, "YandexMusic"), app.Builds);

            Assert.AreEqual(InstallationStatus.Unsafe, installation.Diagnose().Status);
            Assert.Throws<IOException>(() => installation.RunAutomatic(app.AgentPath));

            CollectionAssert.AreEqual(before, app.Snapshot());
        }
        finally { Directory.Delete(alias); }
    }

    private static void CreateDirectoryLink(string alias, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(alias, target);
            return;
        }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException) { }

        // Junctions work on Windows without the symbolic-link privilege. Both paths are fixture-owned.
        var start = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("/d");
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add("mklink");
        start.ArgumentList.Add("/J");
        start.ArgumentList.Add(alias);
        start.ArgumentList.Add(target);
        using var process = Process.Start(start) ?? throw new IOException("Could not start fixture junction creation.");
        var output = process.StandardOutput.ReadToEnd();
        var errorOutput = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new IOException($"Could not create fixture junction: {output} {errorOutput}");
    }

    private static void Install(InstallationFixture app) => app.Run(true, InstallationFixture.Endpoint);

    private static string Hash(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(file));
    }
}
