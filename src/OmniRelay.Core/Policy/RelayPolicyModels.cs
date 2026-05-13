using System.Net;

namespace OmniRelay.Core.Policy;

public enum PolicyMatchAction
{
    None = 0,
    Whitelist = 1,
    Blacklist = 2
}

public sealed record RelayPolicyList(
    string ListId,
    string RelayId,
    string Label,
    string ListType,
    int Priority,
    IReadOnlyList<string> Entries,
    int EntryCount);

public sealed record RelayPolicySetSnapshot(
    string RelayId,
    IReadOnlyList<RelayPolicyList> Lists,
    long Revision,
    DateTimeOffset UpdatedAtUtc,
    int TotalListCount,
    int TotalEntryCount,
    int WhitelistListCount,
    int BlacklistListCount);

public sealed record RelayPolicyListSummary(
    string ListId,
    string RelayId,
    string Label,
    string ListType,
    int Priority,
    int EntryCount);

public sealed record RelayPolicyMatchResult(
    PolicyMatchAction Action,
    string? MatchedListId,
    string? MatchedListLabel,
    int? MatchedListPriority)
{
    public static RelayPolicyMatchResult None { get; } = new(
        PolicyMatchAction.None,
        null,
        null,
        null);
}

public sealed record RelayPolicyCompiledList(
    string ListId,
    string Label,
    string ListType,
    int Priority,
    PolicyAddressIndex Index);

public static class RelayPolicyMatcher
{
    public static RelayPolicyMatchResult Evaluate(IReadOnlyList<RelayPolicyCompiledList> lists, IPAddress? destinationAddress)
    {
        if (destinationAddress is null || lists.Count == 0)
        {
            return RelayPolicyMatchResult.None;
        }

        foreach (var list in lists)
        {
            if (!list.Index.Matches(destinationAddress))
            {
                continue;
            }

            var listType = PolicyListTypes.Normalize(list.ListType);
            var action = string.Equals(listType, PolicyListTypes.Blacklist, StringComparison.OrdinalIgnoreCase)
                ? PolicyMatchAction.Blacklist
                : PolicyMatchAction.Whitelist;
            return new RelayPolicyMatchResult(action, list.ListId, list.Label, list.Priority);
        }

        return RelayPolicyMatchResult.None;
    }
}
