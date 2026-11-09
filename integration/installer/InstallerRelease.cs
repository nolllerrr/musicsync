using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

internal sealed record InstallerRelease(Version Version, Uri DownloadUrl)
{
    private const string Repository = "nolllerrr/musicsync";
    private const string LatestReleaseUrl = $"https://api.github.com/repos/{Repository}/releases/latest";
    private static readonly Version CurrentVersion = typeof(InstallerRelease).Assembly.GetName().Version!;
    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromSeconds(8),
        DefaultRequestHeaders = { UserAgent = { new("MusicSync", CurrentVersion.ToString(3)) } }
    };

    internal static Task<InstallerRelease?> CheckAsync(CancellationToken cancellationToken) =>
        CheckAsync(Client, CurrentVersion, cancellationToken);

    internal static async Task<InstallerRelease?> CheckAsync(HttpClient client, Version currentVersion,
        CancellationToken cancellationToken)
    {
        var release = await client.GetFromJsonAsync<ReleaseMetadata>(LatestReleaseUrl, cancellationToken)
            .ConfigureAwait(false);
        if (release?.Tag is not { } tag || !tag.StartsWith('v') ||
            !Version.TryParse(tag.AsSpan(1), out var version) ||
            version.Build < 0 || version.Revision >= 0 || tag != $"v{version.ToString(3)}")
            throw new InvalidDataException("Invalid MusicSync release tag.");

        // Assembly versions include a fourth component. GitHub release tags use three.
        var installed = new Version(currentVersion.Major, currentVersion.Minor, currentVersion.Build);
        if (version <= installed)
            return null;

        var assetName = $"MusicSync-Together-{version.ToString(3)}-Windows-x64.exe";
        if (release.Assets?.Any(asset => asset is { State: "uploaded", Size: > 0 } && asset.Name == assetName) != true)
            throw new InvalidDataException("The latest MusicSync release has no Windows installer.");

        return new InstallerRelease(version,
            new Uri($"https://github.com/{Repository}/releases/download/{tag}/{assetName}"));
    }

    private sealed record ReleaseMetadata(
        [property: JsonPropertyName("tag_name")] string? Tag, ReleaseAsset[]? Assets);

    private sealed record ReleaseAsset(string? Name, string? State, long Size);
}
