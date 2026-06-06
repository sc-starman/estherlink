namespace OmniRelay.Backend.Services.Installers;

public sealed record OmniGatewayReleaseManifest(
    int SchemaVersion,
    string Channel,
    string Artifact,
    string Sha256,
    long SizeBytes);
