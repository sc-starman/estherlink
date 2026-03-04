namespace EstherLink.UI.Services;

public interface ISudoSessionSecretCache
{
    string? Get();
    void Set(string value);
    void Clear();
}
