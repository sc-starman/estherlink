namespace OmniRelay.Core.Policy;

public static class PolicyListTypes
{
    public const string Whitelist = "whitelist";
    public const string Blacklist = "blacklist";

    public static string Normalize(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Equals(Blacklist, StringComparison.OrdinalIgnoreCase))
        {
            return Blacklist;
        }

        return Whitelist;
    }
}
