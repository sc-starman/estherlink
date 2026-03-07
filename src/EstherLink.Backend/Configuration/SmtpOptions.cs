namespace EstherLink.Backend.Configuration;

public sealed class SmtpOptions
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 587;
    public bool UseSsl { get; set; } = true;
    public bool RequireAuthentication { get; set; } = true;
    public int SendTimeoutSeconds { get; set; } = 45;
    public int RetryCount { get; set; } = 1;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string FromEmail { get; set; } = string.Empty;
    public string FromName { get; set; } = "OmniRelay Contact";
}
