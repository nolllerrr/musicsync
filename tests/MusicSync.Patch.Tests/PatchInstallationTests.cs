using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[assembly: Parallelize(Workers = 4, Scope = ExecutionScope.MethodLevel)]

namespace MusicSync.Patch.Tests;

[TestClass]
public sealed class PatchInstallationTests
{
    [TestMethod]
    public void InstallPatchesBothFilesAndSavesVerifiedBackups()
    {
        using var app = new InstallationFixture();

        Assert.AreEqual("Патч установлен. Раздел «Вместе» доступен в Яндекс Музыке.", Install(app));

        var patched = app.TargetHashes();
        Assert.AreNotEqual(app.Originals.Asar, patched.Asar);
        Assert.AreNotEqual(app.Originals.Exe, patched.Exe);
        Assert.AreEqual(app.Originals, app.BackupHashes());
        var receipt = app.ReadReceipt();
        Assert.AreEqual(2, receipt["Phase"]!.GetValue<int>());
        Assert.AreEqual(patched.Asar, receipt["Patched"]!["Asar"]!.GetValue<string>());
        Assert.AreEqual(patched.Exe, receipt["Patched"]!["Exe"]!.GetValue<string>());
        Assert.IsTrue(File.Exists(Path.Combine(app.Payload, PatchInstallation.AgentName)));
        Assert.AreEqual(InstallationFixture.Endpoint, app.Open().ReadInstalledEndpoint());
    }

    [TestMethod]
    public void ReinstallWithSameBundleIsIdempotent()
    {
        using var app = new InstallationFixture();
        Install(app);
        var before = app.Snapshot();

        Assert.AreEqual("Патч уже установлен.", app.Run(install: true));
        CollectionAssert.AreEqual(before, app.Snapshot());
    }

    [TestMethod]
    public void UninstallRestoresBothOriginalFilesAndCanBeRepeated()
    {
        using var app = new InstallationFixture();
        Install(app);

        Assert.AreEqual("Патч удалён. Исходные файлы Яндекс Музыки восстановлены и проверены.", app.Run(install: false));
        AssertRestored(app);
        Assert.AreEqual("Патч уже снят.", app.Run(install: false));
        AssertRestored(app);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void UninstallRecoversInterruptedInstallWithOriginalOrMissingExe(bool exeMissing)
    {
        using var app = new InstallationFixture();
        Install(app);
        app.InterruptAfterAsarReplacement(exeMissing);

        Assert.AreEqual("Патч уже снят.", app.Run(install: false));
        AssertRestored(app);
    }

    [TestMethod]
    public void InstallRecoversInterruptedInstallBeforeApplyingPatchAgain()
    {
        using var app = new InstallationFixture();
        Install(app);
        var patched = app.TargetHashes();
        app.InterruptAfterAsarReplacement();

        Assert.AreEqual("Патч установлен. Раздел «Вместе» доступен в Яндекс Музыке.", Install(app));
        Assert.AreEqual(patched, app.TargetHashes());
        Assert.AreEqual(app.Originals, app.BackupHashes());
        app.Run(install: false);
        AssertRestored(app);
    }

    [TestMethod]
    public void MalformedReceiptBlocksInstallAndUninstall()
    {
        using var app = new InstallationFixture();
        Install(app);
        File.WriteAllText(app.Receipt, """{"Phase":""");

        AssertRejected<JsonException>(app, install: true);
        AssertRejected<JsonException>(app, install: false);
    }

    [TestMethod]
    public void ReceiptWithUnknownPhaseBlocksInstallAndUninstall()
    {
        using var app = new InstallationFixture();
        Install(app);
        var receipt = app.ReadReceipt();
        receipt["Phase"] = 99;
        File.WriteAllText(app.Receipt, receipt.ToJsonString());

        AssertRejected<InvalidDataException>(app, install: true, "Unrecognized MusicSync installation receipt.");
        AssertRejected<InvalidDataException>(app, install: false, "Unrecognized MusicSync installation receipt.");
    }

    [TestMethod]
    [DataRow("original.asar", false, false)]
    [DataRow("original.exe", false, false)]
    [DataRow("original.asar", true, false)]
    [DataRow("original.exe", true, false)]
    [DataRow("original.asar", false, true)]
    [DataRow("original.exe", false, true)]
    [DataRow("original.asar", true, true)]
    [DataRow("original.exe", true, true)]
    public void MissingOrCorruptBackupBlocksBothOperations(string backupName, bool corrupt, bool interrupted)
    {
        using var app = new InstallationFixture();
        Install(app);
        if (interrupted)
            app.InterruptAfterAsarReplacement();

        var backup = Path.Combine(app.State, backupName);
        if (corrupt)
        {
            File.AppendAllText(backup, "corrupt");
            const string error = "The original backup checksums do not match the verified build.";
            AssertRejected<IOException>(app, install: true, error);
            AssertRejected<IOException>(app, install: false, error);
        }
        else
        {
            File.Delete(backup);
            AssertRejected<FileNotFoundException>(app, install: true);
            AssertRejected<FileNotFoundException>(app, install: false);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void RecoveryRejectsEitherUnknownTargetBeforeRewritingAnyFile(bool changeExe)
    {
        using var app = new InstallationFixture();
        Install(app);
        app.InterruptAfterAsarReplacement();
        File.AppendAllText(changeExe ? app.Exe : app.Asar, "external edit");

        const string error = "Recovery stopped because Yandex Music files have unrecognized checksums.";
        AssertRejected<InvalidOperationException>(app, install: true, error);
        AssertRejected<InvalidOperationException>(app, install: false, error);
    }

    [TestMethod]
    public void LostStateWithPatchedTargetsBlocksBothOperations()
    {
        using var app = new InstallationFixture();
        Install(app);
        app.LoseState();

        AssertUnrecognizedTargets(app);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ExternalUpdateWithDifferentHashesBlocksBothOperations(bool loseState)
    {
        using var app = new InstallationFixture();
        Install(app);
        var patched = app.TargetHashes();
        if (loseState)
            app.LoseState();
        app.ReplaceWithUpdatedApplication();

        var updated = app.TargetHashes();
        Assert.AreNotEqual(app.Originals.Asar, updated.Asar);
        Assert.AreNotEqual(app.Originals.Exe, updated.Exe);
        Assert.AreNotEqual(patched.Asar, updated.Asar);
        Assert.AreNotEqual(patched.Exe, updated.Exe);
        AssertUnrecognizedTargets(app);
    }

    [TestMethod]
    public void StockFilesWithoutStateLeaveOrphanedPayloadUntouched()
    {
        using var app = new InstallationFixture();
        Install(app);
        File.Copy(Path.Combine(app.State, "original.asar"), app.Asar, overwrite: true);
        File.Copy(Path.Combine(app.State, "original.exe"), app.Exe, overwrite: true);
        app.LoseState();
        var before = app.Snapshot();

        Assert.AreEqual(app.Originals, app.TargetHashes());
        Assert.IsTrue(Directory.Exists(app.Payload));
        Assert.AreEqual("Патч уже снят.", app.Run(install: false));
        CollectionAssert.AreEqual(before, app.Snapshot());
        AssertRejected<InvalidOperationException>(app, install: true, "An unrecognized MusicSync payload already exists.");
    }

    private static string Install(InstallationFixture app) => app.Run(install: true, InstallationFixture.Endpoint);

    private static void AssertRestored(InstallationFixture app)
    {
        Assert.AreEqual(app.Originals, app.TargetHashes());
        Assert.IsFalse(Directory.Exists(app.State));
        Assert.IsFalse(Directory.Exists(app.Payload));
    }

    private static void AssertUnrecognizedTargets(InstallationFixture app)
    {
        AssertRejected<InvalidOperationException>(app, install: true,
            "Контрольные суммы файлов Яндекс Музыки отличаются от проверенной сборки.");
        AssertRejected<InvalidOperationException>(app, install: false,
            "Не удалось снять патч: файлы Яндекс Музыки отличаются от записанных при установке.");
    }

    private static void AssertRejected<TException>(InstallationFixture app, bool install, string? message = null)
        where TException : Exception
    {
        var before = app.Snapshot();
        var error = Assert.Throws<TException>(() => app.Run(install, InstallationFixture.Endpoint));
        if (message is not null)
            Assert.AreEqual(message, error.Message);
        CollectionAssert.AreEqual(before, app.Snapshot());
    }
}
