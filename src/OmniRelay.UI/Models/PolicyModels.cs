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
