using System.Net;
using System.Net.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MusicSync.Patch.Tests;

[TestClass]
public sealed class InstallerReleaseTests
{
    [TestMethod]
    public async Task OffersNewerReleaseWithPublishedInstaller()
    {
        using var client = new HttpClient(new ReleaseResponse("""
            {
              "tag_name": "v0.1.10",
              "assets": [{
                "name": "MusicSync-Together-0.1.10-Windows-x64.exe",
                "state": "uploaded",
                "size": 71438130,
                "browser_download_url": "https://example.com/other.exe"
              }]
            }
            """));

        var release = await InstallerRelease.CheckAsync(client, new Version(0, 1, 9, 0), default);

        Assert.IsNotNull(release);
        Assert.AreEqual(new Version(0, 1, 10), release.Version);
        Assert.AreEqual(
            "https://github.com/nolllerrr/musicsync/releases/download/v0.1.10/MusicSync-Together-0.1.10-Windows-x64.exe",
            release.DownloadUrl.AbsoluteUri);
    }

    [TestMethod]
    [DataRow("v0.1.9")]
    [DataRow("v0.1.10")]
    public async Task DoesNotOfferSameOrOlderVersion(string tag)
    {
        using var client = new HttpClient(new ReleaseResponse($$"""{"tag_name":"{{tag}}"}"""));

        Assert.IsNull(await InstallerRelease.CheckAsync(client, new Version(0, 1, 10, 0), default));
    }

    [TestMethod]
    [DataRow("""{"tag_name":"v0.1.11","assets":[]}""")]
    [DataRow("""{"tag_name":"v0.1.11-beta","assets":[]}""")]
    public async Task RejectsReleaseWithoutInstallableStableVersion(string json)
    {
        using var client = new HttpClient(new ReleaseResponse(json));

        await Assert.ThrowsExactlyAsync<InvalidDataException>(
            () => InstallerRelease.CheckAsync(client, new Version(0, 1, 10, 0), default));
    }

    private sealed class ReleaseResponse(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.AreEqual("https://api.github.com/repos/nolllerrr/musicsync/releases/latest",
                request.RequestUri?.AbsoluteUri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }
}
