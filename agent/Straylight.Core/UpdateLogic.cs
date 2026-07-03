using System.Security.Cryptography;
using System.Text.Json;

namespace Straylight.Agent;

public enum UpdateAction { Skip, Proceed }

/// <summary>Result of planning an update: whether to proceed, and if so from where + expected sha.</summary>
public sealed record UpdateDecision(UpdateAction Action, string Reason, string? Url, string? ExpectedSha)
{
    public bool ShouldProceed => Action == UpdateAction.Proceed;
}

/// <summary>
/// Pure, side-effect-free update logic — manifest parsing, the go/no-go decision, and hash
/// verification. This is the part worth unit-testing; all I/O (HTTP download, the swap) lives in
/// the agent host around these and is covered by an end-to-end round-trip instead.
/// </summary>
public static class UpdateLogic
{
    public const string ExeName = "straylight-agent.exe";

    /// <summary>
    /// Decide whether to update, given the running version, the announced release, and config.
    /// Order matters: a no-manifest / no-config / already-current state must Skip before we ever
    /// look at the sha, and "already current" is what makes a late (QoS1) update command a safe no-op.
    /// </summary>
    public static UpdateDecision Plan(string? currentVersion, LatestRelease? latest, string? updateBase, string? maxVersion = null)
    {
        if (latest is null) return Skip("no straylight/latest manifest yet");
        if (string.IsNullOrWhiteSpace(latest.Version)) return Skip("manifest has no version");
        if (string.IsNullOrWhiteSpace(updateBase)) return Skip("no update_base configured");
        if (string.Equals(latest.Version, currentVersion, StringComparison.OrdinalIgnoreCase))
            return Skip($"already at {currentVersion}");
        if (string.IsNullOrWhiteSpace(latest.Sha256)) return Skip("manifest has no sha256");
        // version ceiling: never move above a pin (staged rollout / holding a box back)
        if (!string.IsNullOrWhiteSpace(maxVersion) && CompareVersions(latest.Version, maxVersion!) > 0)
            return Skip($"pinned at max {maxVersion} (latest {latest.Version})");
        return new(UpdateAction.Proceed, "update available", DownloadUrl(updateBase!), Normalize(latest.Sha256));
    }

    /// <summary>Semver-ish compare (x.y.z via System.Version; ordinal fallback). &gt;0 means a is newer.</summary>
    public static int CompareVersions(string a, string b)
        => System.Version.TryParse(a, out var va) && System.Version.TryParse(b, out var vb)
            ? va.CompareTo(vb) : string.CompareOrdinal(a, b);

    /// <summary>
    /// The version a host should treat as its "latest available": the announced latest, but never
    /// above the pin. If latest is above the pin the host can't install it, so report the current
    /// version (HA then shows "up to date" rather than a phantom update it will refuse).
    /// </summary>
    public static string EffectiveLatest(string currentVersion, string? latestVersion, string? maxVersion)
    {
        if (string.IsNullOrWhiteSpace(latestVersion)) return currentVersion;
        if (string.IsNullOrWhiteSpace(maxVersion)) return latestVersion!;
        return CompareVersions(latestVersion!, maxVersion!) <= 0 ? latestVersion! : currentVersion;
    }

    /// <summary>Build the exe URL from a base that may or may not end in a slash.</summary>
    public static string DownloadUrl(string updateBase) => updateBase.TrimEnd('/') + "/" + ExeName;

    /// <summary>Parse a straylight/latest payload; null if invalid or missing a version.</summary>
    public static LatestRelease? ParseManifest(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var j = JsonDocument.Parse(json).RootElement;
            if (j.ValueKind != JsonValueKind.Object) return null;
            var v = j.TryGetProperty("version", out var vv) ? vv.GetString() : null;
            if (string.IsNullOrWhiteSpace(v)) return null;
            var s = j.TryGetProperty("sha256", out var ss) ? ss.GetString() : null;
            var n = j.TryGetProperty("notes", out var nn) ? nn.GetString() : null;
            return new LatestRelease(v!, (s ?? "").Trim(), n);
        }
        catch { return null; }
    }

    /// <summary>True if data's SHA-256 equals expectedHex (case-insensitive, whitespace-trimmed).</summary>
    public static bool ShaMatches(byte[] data, string? expectedHex)
    {
        if (string.IsNullOrWhiteSpace(expectedHex)) return false;
        var got = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
        return string.Equals(got, Normalize(expectedHex), StringComparison.Ordinal);
    }

    static string Normalize(string hex) => hex.Trim().ToLowerInvariant();
    static UpdateDecision Skip(string reason) => new(UpdateAction.Skip, reason, null, null);
}
