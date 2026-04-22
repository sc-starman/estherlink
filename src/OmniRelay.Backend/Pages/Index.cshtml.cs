using OmniRelay.Backend.Configuration;
using OmniRelay.Backend.Localization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

namespace OmniRelay.Backend.Pages;

public sealed class IndexModel : PageModel
{
    private readonly IOptions<WebOptions> _webOptions;
    private readonly IOptions<PayKryptOptions> _payKryptOptions;
    private readonly IStringLocalizer<SharedResource> _localizer;

    public IndexModel(
        IOptions<WebOptions> webOptions,
        IOptions<PayKryptOptions> payKryptOptions,
        IStringLocalizer<SharedResource> localizer)
    {
        _webOptions = webOptions;
        _payKryptOptions = payKryptOptions;
        _localizer = localizer;
    }

    public LandingContentOptions Landing { get; private set; } = new();
    public decimal PriceUsd { get; private set; }
    public decimal OriginalPrice { get; private set; }

    public void OnGet()
    {
        var options = _webOptions.Value;
        var payKryptOptions = _payKryptOptions.Value;
        Landing = BuildLandingContent(options);
        PriceUsd = payKryptOptions.PriceUsd;
        OriginalPrice = payKryptOptions.OriginalPrice;
    }

    private LandingContentOptions BuildLandingContent(WebOptions options)
    {
        var configured = options.LandingContent ?? new LandingContentOptions();
        var landing = new LandingContentOptions
        {
            HeroHeadline = FirstNonEmpty(configured.HeroHeadline, _localizer["Index.Config.HeroHeadline"]),
            HeroSubheadline = FirstNonEmpty(configured.HeroSubheadline, _localizer["Index.Config.HeroSubheadline"]),
            PrimaryCtaText = FirstNonEmpty(configured.PrimaryCtaText, _localizer["Index.Config.PrimaryCtaText"]),
            PrimaryCtaUrl = FirstNonEmpty(configured.PrimaryCtaUrl, "/account/register"),
            SecondaryCtaText = FirstNonEmpty(configured.SecondaryCtaText, _localizer["Index.Config.SecondaryCtaText"]),
            SecondaryCtaUrl = FirstNonEmpty(configured.SecondaryCtaUrl, "#how-it-works"),

            BenefitBlocks = NonEmptyOrDefault(configured.BenefitBlocks, [
                _localizer["Index.Config.Benefit1"],
                _localizer["Index.Config.Benefit2"],
                _localizer["Index.Config.Benefit3"],
                _localizer["Index.Config.Benefit4"],
                _localizer["Index.Config.Benefit5"]
            ]),

            ResultCards = NonEmptyResultCards(configured.ResultCards, [
                new LandingResultCardOptions { Value = "38%", Label = _localizer["Index.Config.Result1"] },
                new LandingResultCardOptions { Value = "2.7x", Label = _localizer["Index.Config.Result2"] },
                new LandingResultCardOptions { Value = "99.9%", Label = _localizer["Index.Config.Result3"] }
            ]),

            Testimonials = NonEmptyTestimonials(configured.Testimonials, [
                new LandingTestimonialOptions
                {
                    Quote = _localizer["Index.Config.Testimonial1.Quote"],
                    Author = "S. Rahimi",
                    Role = _localizer["Index.Config.Testimonial1.Role"]
                },
                new LandingTestimonialOptions
                {
                    Quote = _localizer["Index.Config.Testimonial2.Quote"],
                    Author = "M. Daryan",
                    Role = _localizer["Index.Config.Testimonial2.Role"]
                }
            ]),

            TrustBarText = FirstNonEmpty(configured.TrustBarText, _localizer["Index.Config.TrustBarText"]),

            HowItWorksSteps = NonEmptyOrDefault(configured.HowItWorksSteps, [
                _localizer["Index.Config.How1"],
                _localizer["Index.Config.How2"],
                _localizer["Index.Config.How3"]
            ]),

            OfferTitle = FirstNonEmpty(configured.OfferTitle, _localizer["Index.Config.OfferTitle"]),
            OfferSummary = FirstNonEmpty(configured.OfferSummary, _localizer["Index.Config.OfferSummary"]),
            OfferPriceAnchor = FirstNonEmpty(configured.OfferPriceAnchor, _localizer["Index.Config.OfferPriceAnchor"]),
            OfferRiskReducer = FirstNonEmpty(configured.OfferRiskReducer, _localizer["Index.Config.OfferRiskReducer"]),
            MidCtaText = FirstNonEmpty(configured.MidCtaText, _localizer["Index.Config.MidCtaText"]),
            MidCtaUrl = FirstNonEmpty(configured.MidCtaUrl, "/account/register"),

            Faqs = NonEmptyFaqs(configured.Faqs, [
                new LandingFaqOptions
                {
                    Question = _localizer["Index.Config.Faq1.Q"],
                    Answer = _localizer["Index.Config.Faq1.A"]
                },
                new LandingFaqOptions
                {
                    Question = _localizer["Index.Config.Faq2.Q"],
                    Answer = _localizer["Index.Config.Faq2.A"]
                },
                new LandingFaqOptions
                {
                    Question = _localizer["Index.Config.Faq3.Q"],
                    Answer = _localizer["Index.Config.Faq3.A"]
                },
                new LandingFaqOptions
                {
                    Question = _localizer["Index.Config.Faq4.Q"],
                    Answer = _localizer["Index.Config.Faq4.A"]
                },
                new LandingFaqOptions
                {
                    Question = _localizer["Index.Config.Faq5.Q"],
                    Answer = _localizer["Index.Config.Faq5.A"]
                }
            ]),

            ClosingHeadline = FirstNonEmpty(configured.ClosingHeadline, _localizer["Index.Config.ClosingHeadline"]),
            ClosingBody = FirstNonEmpty(configured.ClosingBody, _localizer["Index.Config.ClosingBody"]),
            FinalCtaText = FirstNonEmpty(configured.FinalCtaText, _localizer["Index.Config.FinalCtaText"]),
            FinalCtaUrl = FirstNonEmpty(configured.FinalCtaUrl, "/account/register")
        };

        return landing;
    }

    private static string FirstNonEmpty(string value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private static List<string> NonEmptyOrDefault(List<string>? values, List<string> fallback)
    {
        var filtered = values?
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .ToList();

        return filtered is { Count: > 0 } ? filtered : fallback;
    }

    private static List<LandingResultCardOptions> NonEmptyResultCards(List<LandingResultCardOptions>? values, List<LandingResultCardOptions> fallback)
    {
        var filtered = values?
            .Where(x => !string.IsNullOrWhiteSpace(x.Value) && !string.IsNullOrWhiteSpace(x.Label))
            .ToList();

        return filtered is { Count: > 0 } ? filtered : fallback;
    }

    private static List<LandingTestimonialOptions> NonEmptyTestimonials(List<LandingTestimonialOptions>? values, List<LandingTestimonialOptions> fallback)
    {
        var filtered = values?
            .Where(x => !string.IsNullOrWhiteSpace(x.Quote) && !string.IsNullOrWhiteSpace(x.Author))
            .ToList();

        return filtered is { Count: > 0 } ? filtered : fallback;
    }

    private static List<LandingFaqOptions> NonEmptyFaqs(List<LandingFaqOptions>? values, List<LandingFaqOptions> fallback)
    {
        var filtered = values?
            .Where(x => !string.IsNullOrWhiteSpace(x.Question) && !string.IsNullOrWhiteSpace(x.Answer))
            .ToList();

        return filtered is { Count: > 0 } ? filtered : fallback;
    }
}
