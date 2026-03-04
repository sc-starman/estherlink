namespace EstherLink.UI.Services;

public interface ILogReaderService
{
    Task<IReadOnlyList<string>> ReadLatestAsync(int maxLines, string? search = null, CancellationToken cancellationToken = default);
}
