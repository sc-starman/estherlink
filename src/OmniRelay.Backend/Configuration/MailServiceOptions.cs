namespace OmniRelay.Backend.Configuration;

public sealed class MailServiceOptions
{
    public string BaseUrl { get; set; } = string.Empty;
    public string SendPath { get; set; } = "/send";
    public string ApiKey { get; set; } = string.Empty;
    public string ApiKeyHeader { get; set; } = "x-api-key";
    public int TimeoutSeconds { get; set; } = 45;
    public int RetryCount { get; set; } = 1;
}

