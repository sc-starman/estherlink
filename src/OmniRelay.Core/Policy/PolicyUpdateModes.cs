namespace OmniRelay.Core.Policy;

public static class PolicyUpdateModes
{
    public const string Replace = "replace";
    public const string Merge = "merge";

    public static string Normalize(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Equals(Merge, StringComparison.OrdinalIgnoreCase))
        {
            return Merge;
        }

        return Replace;
    }
}
