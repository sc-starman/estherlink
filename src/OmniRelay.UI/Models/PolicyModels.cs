namespace OmniRelay.UI.Models;

public sealed record PolicyListResult(
    bool Success,
    string Message,
    string ListType,
    IReadOnlyList<string> Entries,
    int Count,
    long Revision,
    DateTimeOffset UpdatedAtUtc);

public sealed record PolicyCommitSummary(
    bool Success,
    string Message,
    string ListType,
    string Mode,
    int AppliedCount,
    int DuplicateDroppedCount,
    int InvalidCount,
    int Count,
    long Revision,
    DateTimeOffset UpdatedAtUtc);

public sealed record RelayPolicyListsResult(
    bool Success,
    string Message,
    string RelayId,
    IReadOnlyList<RelayPolicyListItemResult> Lists,
    long Revision,
    DateTimeOffset UpdatedAtUtc,
    int TotalListCount,
    int TotalEntryCount,
    int WhitelistListCount,
    int BlacklistListCount);

public sealed record RelayPolicyListItemResult(
    string ListId,
    string RelayId,
    string Label,
    string ListType,
    int Priority,
    int EntryCount);

public sealed record RelayPolicyListDetailsResult(
    bool Success,
    string Message,
    string ListId,
    string RelayId,
    string Label,
    string ListType,
    int Priority,
    IReadOnlyList<string> Entries,
    int Count,
    long Revision,
    DateTimeOffset UpdatedAtUtc);

public sealed record RelayPolicyMutationResult(
    bool Success,
    string Message,
    string ListId,
    string RelayId,
    string Label,
    string ListType,
    int Priority,
    int EntryCount,
    long Revision,
    DateTimeOffset UpdatedAtUtc);
