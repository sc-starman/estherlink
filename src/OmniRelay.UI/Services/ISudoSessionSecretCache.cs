namespace OmniRelay.UI.Services;

public interface ISudoSessionSecretCache
{
    string? Get();
    void Set(string value);
    void Clear();
}
