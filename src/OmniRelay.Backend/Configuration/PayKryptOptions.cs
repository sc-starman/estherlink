namespace OmniRelay.Backend.Configuration;

public sealed class PayKryptOptions
{
    public string BaseUrl { get; set; } = "https://api-sandbox.paykrypt.io";
    public string SecretApiKey { get; set; } = string.Empty;
    public string? WebhookSecret { get; set; }
    public decimal PriceUsd { get; set; } = 149m;
    public List<string> AllowedChains { get; set; } = [];
    public List<string> AllowedAssets { get; set; } = [];
    public int ExpiresInMinutes { get; set; } = 45;
}