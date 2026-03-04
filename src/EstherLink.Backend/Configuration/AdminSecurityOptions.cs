namespace EstherLink.Backend.Configuration;

public sealed class AdminSecurityOptions
{
    public List<string> ApiKeys { get; set; } = [];
    public string? ApiKeyPepper { get; set; }
}
