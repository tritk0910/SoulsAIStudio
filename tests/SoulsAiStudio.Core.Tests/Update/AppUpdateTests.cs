using System.IO.Compression;
using System.Net;
using SoulsAiStudio.Core.Tests.TestSupport;
using SoulsAiStudio.Core.Update;

namespace SoulsAiStudio.Core.Tests.Update;

public class AppUpdateTests
{
    private const string Json = """
        [
          {"tag_name":"v0.2.4","name":"Souls AI Studio v0.2.4","body":"notes 4","draft":false,"prerelease":true,
           "html_url":"https://github.com/x/y/releases/tag/v0.2.4",
           "assets":[{"name":"SoulsAIStudio-v0.2.4-win-x64.zip","size":10,"browser_download_url":"https://example.invalid/a.zip",
                      "digest":"sha256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"}]},
          {"tag_name":"v0.3.0","name":"draft","draft":true,"prerelease":false,"assets":[]},
          {"tag_name":"v0.2.3","name":"Souls AI Studio v0.2.3","body":"","draft":false,"prerelease":false,
           "assets":[{"name":"notes.txt","size":1,"browser_download_url":"https://example.invalid/n"}]},
          {"tag_name":"v.0.1.0","name":"old","draft":false,"prerelease":true,"assets":[]},
          {"tag_name":"nightly","name":"not a version","draft":false,"prerelease":true,"assets":[]}
        ]
        """;

    [Theory]
    [InlineData("v0.2.3", "0.2.3.0")]
    [InlineData("0.2", "0.2.0.0")]
    [InlineData("v.0.1.0", "0.1.0.0")]
    [InlineData("v1.2.3-beta", "1.2.3.0")]
    public void ParseVersion_Tags(string tag, string expected) => Assert.Equal(Version.Parse(expected), UpdateCheck.ParseVersion(tag));

    [Fact]
    public void ParseVersion_NotAVersion_IsNull() => Assert.Null(UpdateCheck.ParseVersion("nightly"));

    [Fact]
    public void ParseReleases_SkipsDraftsAndNonVersions_ReadsAssetAndDigest()
    {
        var r = UpdateCheck.ParseReleases(Json);
        Assert.Equal(["v0.2.4", "v0.2.3", "v.0.1.0"], r.Select(x => x.Tag));
        var v4 = r[0];
        Assert.True(v4.Prerelease);
        Assert.Equal("SoulsAIStudio-v0.2.4-win-x64.zip", v4.AssetName);
        Assert.Equal(new string('a', 64), v4.Sha256);
        Assert.True(v4.Installable);
        Assert.False(r[1].Installable); // no zip
    }

    [Fact]
    public void PickUpdate_NewestAboveCurrent_RespectsPrereleaseAndSkip()
    {
        var r = UpdateCheck.ParseReleases(Json);
        Assert.Equal("v0.2.4", UpdateCheck.PickUpdate(r, new Version(0, 2, 3), includePrereleases: true)?.Tag);
        Assert.Null(UpdateCheck.PickUpdate(r, new Version(0, 2, 3), includePrereleases: false));
        Assert.Null(UpdateCheck.PickUpdate(r, new Version(0, 2, 4), includePrereleases: true));
        Assert.Null(UpdateCheck.PickUpdate(r, new Version(0, 2, 3), includePrereleases: true, skippedTag: "v0.2.4"));
        Assert.Equal("v0.2.3", UpdateCheck.PickUpdate(r, new Version(0, 2), includePrereleases: false)?.Tag);
    }

    private static byte[] MakeZip(bool topFolder)
    {
        using var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            string p = topFolder ? "SoulsAIStudio-v9.9.9/" : "";
            void Add(string name, string text) { using var w = new StreamWriter(z.CreateEntry(p + name).Open()); w.Write(text); }
            Add(UpdateInstaller.ExeName, "new exe");
            Add("lib.dll", "new lib");
            Add("Tools/tool.txt", "new tool");
        }
        return ms.ToArray();
    }

    private sealed class FakeHandler(byte[] body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
    }

    private static ReleaseInfo Release(byte[] zip, string? sha) =>
        new("v9.9.9", new Version(9, 9, 9), "x", "", true, "https://example.invalid", "SoulsAIStudio-v9.9.9-win-x64.zip",
            "https://example.invalid/a.zip", zip.Length, sha);

    [Fact]
    public async Task DownloadAndStage_ChecksSha_FindsTopFolder()
    {
        using var work = TempWorkDir.Create("upd");
        var zip = MakeZip(topFolder: true);
        string sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(zip)).ToLowerInvariant();
        using var http = new HttpClient(new FakeHandler(zip));
        var seen = new List<double>();
        string root = await UpdateInstaller.DownloadAndStageAsync(http, Release(zip, sha), work.Combine("stage"), new SyncProgress(seen));
        Assert.True(File.Exists(Path.Combine(root, UpdateInstaller.ExeName)));
        Assert.EndsWith("SoulsAIStudio-v9.9.9", root);
        Assert.Equal(1.0, seen.Last());

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            UpdateInstaller.DownloadAndStageAsync(http, Release(zip, new string('0', 64)), work.Combine("stage2")));
    }

    private sealed class SyncProgress(List<double> list) : IProgress<double>
    {
        public void Report(double value) => list.Add(value);
    }

    private static (string Staged, string Install) Prepare(TempWorkDir work)
    {
        string staged = work.Combine("staged");
        ZipFile.ExtractToDirectory(new MemoryStream(MakeZip(topFolder: false)), staged);
        string install = work.Combine("install");
        Directory.CreateDirectory(Path.Combine(install, "Tools"));
        File.WriteAllText(Path.Combine(install, UpdateInstaller.ExeName), "old exe");
        File.WriteAllText(Path.Combine(install, "lib.dll"), "old lib");
        File.WriteAllText(Path.Combine(install, "imgui.ini"), "my layout");
        File.WriteAllText(Path.Combine(install, "old-only.dll"), "stays");
        return (staged, install);
    }

    [Fact]
    public void Apply_ReplacesPackageFiles_KeepsUserFiles_ThenMarkerIsTakenOnce()
    {
        using var work = TempWorkDir.Create("upd");
        var (staged, install) = Prepare(work);
        UpdateInstaller.Apply(staged, install, "0.2.3", "9.9.9");
        Assert.Equal("new exe", File.ReadAllText(Path.Combine(install, UpdateInstaller.ExeName)));
        Assert.Equal("new lib", File.ReadAllText(Path.Combine(install, "lib.dll")));
        Assert.Equal("new tool", File.ReadAllText(Path.Combine(install, "Tools", "tool.txt")));
        Assert.Equal("my layout", File.ReadAllText(Path.Combine(install, "imgui.ini")));
        Assert.Equal("stays", File.ReadAllText(Path.Combine(install, "old-only.dll")));
        Assert.Equal("old exe", File.ReadAllText(Path.Combine(install, UpdateInstaller.BackupDirName, UpdateInstaller.ExeName)));

        Assert.Equal("0.2.3 -> 9.9.9", UpdateInstaller.TakeAppliedMarker(install));
        Assert.False(Directory.Exists(Path.Combine(install, UpdateInstaller.BackupDirName)));
        Assert.Null(UpdateInstaller.TakeAppliedMarker(install));
    }

    [Fact]
    public void Apply_FailureHalfway_RestoresOldVersion()
    {
        using var work = TempWorkDir.Create("upd");
        var (staged, install) = Prepare(work);
        // "Tools" exists as a FILE in the install: creating Tools/tool.txt fails after the exe and lib were replaced.
        Directory.Delete(Path.Combine(install, "Tools"));
        File.WriteAllText(Path.Combine(install, "Tools"), "blocker");
        Assert.ThrowsAny<IOException>(() => UpdateInstaller.Apply(staged, install, "0.2.3", "9.9.9"));
        Assert.Equal("old exe", File.ReadAllText(Path.Combine(install, UpdateInstaller.ExeName)));
        Assert.Equal("old lib", File.ReadAllText(Path.Combine(install, "lib.dll")));
        Assert.Equal("my layout", File.ReadAllText(Path.Combine(install, "imgui.ini")));
        Assert.Null(UpdateInstaller.TakeAppliedMarker(install)); // no marker: not reported as updated
    }

    [Fact]
    public void CanWriteTo_TempIsWritable()
    {
        using var work = TempWorkDir.Create("upd");
        Assert.True(UpdateInstaller.CanWriteTo(work.Path));
        Assert.False(UpdateInstaller.CanWriteTo(work.Combine("does/not/exist")));
    }
}
