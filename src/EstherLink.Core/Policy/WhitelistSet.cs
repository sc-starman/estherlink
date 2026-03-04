using System.Net;

namespace EstherLink.Core.Policy;

public sealed class WhitelistSet
{
    private readonly IReadOnlyList<NetworkRule> _rules;

    private WhitelistSet(IReadOnlyList<NetworkRule> rules)
    {
        _rules = rules;
    }

    public static WhitelistSet Empty { get; } = new([]);

    public IReadOnlyList<NetworkRule> Rules => _rules;

    public static bool TryCreate(IEnumerable<string> rawEntries, out WhitelistSet set, out IReadOnlyList<string> errors)
    {
        var parsed = new List<NetworkRule>();
        var issues = new List<string>();

        foreach (var item in rawEntries)
        {
            var value = (item ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(value) || value.StartsWith('#'))
            {
                continue;
            }

            var commentMarker = value.IndexOf('#');
            if (commentMarker > 0)
            {
                value = value[..commentMarker].Trim();
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (NetworkRule.TryParse(value, out var rule, out var error) && rule is not null)
            {
                parsed.Add(rule);
            }
            else
            {
                issues.Add(error ?? $"Invalid entry '{value}'.");
            }
        }

        if (issues.Count > 0)
        {
            set = Empty;
            errors = issues;
            return false;
        }

        set = new WhitelistSet(parsed);
        errors = Array.Empty<string>();
        return true;
    }

    public bool Matches(IPAddress? sourceAddress, IPAddress? destinationAddress, RoutingPolicyMode mode)
    {
        return mode switch
        {
            RoutingPolicyMode.SourceOnly => Matches(sourceAddress),
            RoutingPolicyMode.DestinationOnly => Matches(destinationAddress),
            RoutingPolicyMode.SourceOrDestination => Matches(sourceAddress) || Matches(destinationAddress),
            _ => false
        };
    }

    private bool Matches(IPAddress? address)
    {
        if (address is null)
        {
            return false;
        }

        foreach (var rule in _rules)
        {
            if (rule.Contains(address))
            {
                return true;
            }
        }

        return false;
    }
}
