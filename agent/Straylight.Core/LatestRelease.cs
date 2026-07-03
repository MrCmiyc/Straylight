namespace Straylight.Agent;

/// <summary>A release announced on the retained `straylight/latest` topic.</summary>
public sealed record LatestRelease(string Version, string Sha256, string? Notes);
