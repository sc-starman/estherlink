namespace OmniRelay.Backend.Services;

public sealed record NewsletterSnapshot(
    string Version,
    string Headline,
    IReadOnlyList<string> Highlights,
    string CtaText);

public interface INewsletterContentProvider
{
    NewsletterSnapshot GetLatest();
}
