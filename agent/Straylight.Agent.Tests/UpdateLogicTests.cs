using Straylight.Agent;
using Xunit;

namespace Straylight.Agent.Tests;

public class UpdateLogicTests
{
    const string Base = "http://mqtt-host/straylight/";

    // ---------- Plan: the go / no-go decision matrix ----------

    [Fact]
    public void Plan_NoManifest_Skips()
        => Assert.False(UpdateLogic.Plan("0.8.6", null, Base).ShouldProceed);

    [Fact]
    public void Plan_ManifestMissingVersion_Skips()
        => Assert.False(UpdateLogic.Plan("0.8.6", new LatestRelease("", "abc", null), Base).ShouldProceed);

    [Fact]
    public void Plan_NoUpdateBase_Skips()
        => Assert.False(UpdateLogic.Plan("0.8.6", new LatestRelease("0.8.7", "abc", null), "").ShouldProceed);

    [Fact]
    public void Plan_AlreadyCurrent_Skips()
    {
        var d = UpdateLogic.Plan("0.8.6", new LatestRelease("0.8.6", "abc", null), Base);
        Assert.False(d.ShouldProceed);
        Assert.Contains("already", d.Reason);
    }

    [Fact]
    public void Plan_AlreadyCurrent_IsCaseInsensitive_Skips()
        => Assert.False(UpdateLogic.Plan("0.8.6-RC", new LatestRelease("0.8.6-rc", "abc", null), Base).ShouldProceed);

    [Fact]
    public void Plan_MissingSha_Skips()          // no integrity anchor -> refuse
        => Assert.False(UpdateLogic.Plan("0.8.6", new LatestRelease("0.8.7", "  ", null), Base).ShouldProceed);

    [Fact]
    public void Plan_ValidUpdate_Proceeds_WithUrlAndNormalizedSha()
    {
        var d = UpdateLogic.Plan("0.8.6", new LatestRelease("0.8.7", "  ABCDEF  ", "n"), Base);
        Assert.True(d.ShouldProceed);
        Assert.Equal("http://mqtt-host/straylight/straylight-agent.exe", d.Url);
        Assert.Equal("abcdef", d.ExpectedSha);   // trimmed + lowercased for the byte-compare
    }

    [Fact]
    public void Plan_UnknownCurrentVersion_Proceeds()
        => Assert.True(UpdateLogic.Plan(null, new LatestRelease("0.8.7", "abc", null), Base).ShouldProceed);

    // ---------- DownloadUrl: trailing-slash handling ----------

    [Theory]
    [InlineData("http://mqtt-host/straylight/", "http://mqtt-host/straylight/straylight-agent.exe")]
    [InlineData("http://mqtt-host/straylight", "http://mqtt-host/straylight/straylight-agent.exe")]
    [InlineData("http://mqtt-host/straylight///", "http://mqtt-host/straylight/straylight-agent.exe")]
    public void DownloadUrl_NormalizesTrailingSlash(string baseUrl, string expected)
        => Assert.Equal(expected, UpdateLogic.DownloadUrl(baseUrl));

    // ---------- ParseManifest ----------

    [Fact]
    public void ParseManifest_Valid_ReturnsRelease()
    {
        var r = UpdateLogic.ParseManifest("""{"version":"0.8.7","sha256":"DEADBEEF","notes":"hi"}""");
        Assert.NotNull(r);
        Assert.Equal("0.8.7", r!.Version);
        Assert.Equal("DEADBEEF", r.Sha256);
        Assert.Equal("hi", r.Notes);
    }

    [Fact]
    public void ParseManifest_MissingSha_YieldsEmptyShaAndNullNotes()
    {
        var r = UpdateLogic.ParseManifest("""{"version":"0.8.7"}""");
        Assert.NotNull(r);
        Assert.Equal("", r!.Sha256);
        Assert.Null(r.Notes);
    }

    [Fact]
    public void ParseManifest_TrimsSha()
        => Assert.Equal("abc", UpdateLogic.ParseManifest("""{"version":"1","sha256":"  abc  "}""")!.Sha256);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("[1,2,3]")]              // array, not an object
    [InlineData("\"a string\"")]
    [InlineData("null")]
    [InlineData("{\"sha256\":\"abc\"}")] // missing version
    public void ParseManifest_Invalid_ReturnsNull(string? json)
        => Assert.Null(UpdateLogic.ParseManifest(json));

    // ---------- ShaMatches (SHA-256 NIST vectors) ----------

    static readonly byte[] Abc = "abc"u8.ToArray();
    const string AbcSha = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";

    [Fact]
    public void ShaMatches_Correct_True() => Assert.True(UpdateLogic.ShaMatches(Abc, AbcSha));

    [Fact]
    public void ShaMatches_Uppercase_True() => Assert.True(UpdateLogic.ShaMatches(Abc, AbcSha.ToUpperInvariant()));

    [Fact]
    public void ShaMatches_Whitespace_True() => Assert.True(UpdateLogic.ShaMatches(Abc, $"  {AbcSha}  "));

    [Fact]
    public void ShaMatches_Wrong_False() => Assert.False(UpdateLogic.ShaMatches(Abc, new string('0', 64)));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ShaMatches_EmptyExpected_False(string? sha) => Assert.False(UpdateLogic.ShaMatches(Abc, sha));

    [Fact]
    public void ShaMatches_EmptyData_MatchesEmptyHash()   // SHA-256("") vector
        => Assert.True(UpdateLogic.ShaMatches(
            System.Array.Empty<byte>(),
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"));

    // ---------- Version pin / ceiling ----------

    [Fact]
    public void Plan_LatestWithinPin_Proceeds()
        => Assert.True(UpdateLogic.Plan("0.8.6", new LatestRelease("0.8.8", "abc", null), Base, "0.8.8").ShouldProceed);

    [Fact]
    public void Plan_LatestAbovePin_Skips()
    {
        var d = UpdateLogic.Plan("0.8.8", new LatestRelease("0.9.0", "abc", null), Base, "0.8.8");
        Assert.False(d.ShouldProceed);
        Assert.Contains("pinned", d.Reason);
    }

    [Fact]
    public void Plan_NoPin_ProceedsToLatest()
        => Assert.True(UpdateLogic.Plan("0.8.6", new LatestRelease("0.9.0", "abc", null), Base, null).ShouldProceed);

    [Theory]
    [InlineData("0.8.8", "0.9.0", -1)]
    [InlineData("0.9.0", "0.8.8", 1)]
    [InlineData("0.8.8", "0.8.8", 0)]
    [InlineData("0.10.0", "0.9.0", 1)]     // semver, not string ordinal ("0.10" < "0.9" as strings)
    public void CompareVersions_IsSemver(string a, string b, int sign)
        => Assert.Equal(sign, System.Math.Sign(UpdateLogic.CompareVersions(a, b)));

    [Theory]
    [InlineData("0.8.6", "0.9.0", null, "0.9.0")]     // no pin -> latest
    [InlineData("0.8.6", "0.8.8", "0.8.8", "0.8.8")]  // latest == pin -> latest
    [InlineData("0.8.6", "0.9.0", "0.8.8", "0.8.6")]  // latest > pin -> current (can't install past pin)
    [InlineData("0.8.6", null, "0.8.8", "0.8.6")]     // no latest -> current
    public void EffectiveLatest_RespectsPin(string cur, string? latest, string? pin, string expected)
        => Assert.Equal(expected, UpdateLogic.EffectiveLatest(cur, latest, pin));
}
