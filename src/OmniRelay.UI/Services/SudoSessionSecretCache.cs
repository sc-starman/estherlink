namespace OmniRelay.UI.Services;

public sealed class SudoSessionSecretCache : ISudoSessionSecretCache
{
    private string? _value;

    public string? Get() => _value;

    public void Set(string value)
    {
        _value = string.IsNullOrWhiteSpace(value) ? null : value;
    }

    public void Clear()
    {
        _value = null;
    }
}
