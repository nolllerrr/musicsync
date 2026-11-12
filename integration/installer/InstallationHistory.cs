using System.IO;
using System.Text.Json;

internal sealed partial class PatchInstallation
{
    private string HistoryPath => Path.Combine(_root, ".musicsync-together-history");

    private Uri? ReadVerifiedEndpoint(Receipt receipt)
    {
        foreach (var directory in new[] { _payload, StatePath("retired-payload") })
        {
            VerifyPayload(directory, receipt, allowMissing: true);
            var config = Path.Combine(directory, ConfigName);
            if (File.Exists(config)) return PatchSettings.Read(config);
        }
        return null;
    }

    private void RetireState(Receipt receipt)
    {
        // Never restore an old generation over a complete, verified official update.
        // Keep the entire journal and backups. A move interrupted before the final rename
        // is retried using the same old receipt and retired-payload directory.
        CheckPaths();
        VerifyPayload(_payload, receipt, allowMissing: true);
        VerifyPayload(StatePath(PayloadName), receipt, allowMissing: true);
        var retired = StatePath("retired-payload");
        VerifyPayload(retired, receipt, allowMissing: true);
        if (Directory.Exists(_payload))
        {
            if (Directory.Exists(retired))
                throw new IOException("Both active and retired MusicSync payloads exist. Files were preserved for inspection.");
            Directory.Move(_payload, retired);
        }
        Directory.CreateDirectory(HistoryPath);
        var archive = Path.Combine(HistoryPath, DateTime.UtcNow.ToString("yyyyMMddHHmmssfffffff") + "-" + Guid.NewGuid().ToString("N"));
        Directory.Move(_state, archive);
    }

    private Uri? ReadArchivedEndpoint()
    {
        CheckPath(HistoryPath);
        if (!Directory.Exists(HistoryPath)) return null;
        // Read only our verifiable records; leave foreign files and damaged archives untouched.
        foreach (var directory in Directory.EnumerateDirectories(HistoryPath).OrderDescending(StringComparer.Ordinal))
        {
            CheckPath(directory);
            var receiptPath = Path.Combine(directory, "receipt.json");
            if (!File.Exists(receiptPath)) continue;
            Receipt receipt;
            try { receipt = ReadReceiptFile(receiptPath); }
            catch (Exception error) when (error is JsonException or InvalidDataException) { continue; }
            var payload = Path.Combine(directory, "retired-payload");
            VerifyPayload(payload, receipt, allowMissing: true);
            var config = Path.Combine(payload, ConfigName);
            if (File.Exists(config)) return PatchSettings.Read(config);
        }
        return null;
    }

    private static void FlushFile(string path)
    {
        using var file = File.Open(path, FileMode.Open, FileAccess.Write, FileShare.Read);
        file.Flush(flushToDisk: true);
    }
}
