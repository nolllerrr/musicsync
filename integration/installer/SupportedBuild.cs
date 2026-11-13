internal sealed record SupportedBuild(string Version, string ProductVersion, PatchInstallation.Pair Originals)
{
    // Exact identities are the compatibility boundary. Structural checks are additional guards.
    internal static readonly SupportedBuild[] Catalog =
    [
        new("5.126.1", "5.126.1.0", new(
            "5A259722D60F746EC32C8BF6E880E375E888315080FB18E67C29B54B582C1215",
            "F5FB2817E331AA0B2DD0A6C7B0A360C89B5AE96AFC535CA085ABE514F8DE4AE4")),
        new("5.127.0", "5.127.0.0", new(
            "C176FF3CECDDDFAB76B2D0D90BF527BBED226919FAB37F5723B46913084620CB",
            "DE2849A1378DAFF16500FD5C8499634AAB8D994C1FFB1B1D8A6BC264EDD9AADA"))
    ];
}

internal enum InstallationStatus
{
    Missing, SupportedOriginal, Installed, PatchMissing, Unsupported, HashMismatch,
    Damaged, Interrupted, Busy, Unsafe, Error
}

internal sealed record InstallationDiagnosis(string? ProductVersion, string? PackageVersion,
    InstallationStatus Status, string Message, bool CanRepair, string Fingerprint);
