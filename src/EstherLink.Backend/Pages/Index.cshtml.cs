using EstherLink.Backend.Configuration;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace EstherLink.Backend.Pages;

public sealed class IndexModel : PageModel
{
    private readonly IOptions<WebOptions> _webOptions;

    public IndexModel(IOptions<WebOptions> webOptions)
    {
        _webOptions = webOptions;
    }

    public LandingContentOptions Landing { get; private set; } = new();

    public void OnGet()
    {
        var options = _webOptions.Value;
        Landing = BuildLandingContent(options);
    }

    private LandingContentOptions BuildLandingContent(WebOptions options)
    {
        var configured = options.LandingContent ?? new LandingContentOptions();
        var landing = new LandingContentOptions
        {
            HeroHeadline = FirstNonEmpty(configured.HeroHeadline, "Secure Connectivity Across Stratified Internet"),
            HeroSubheadline = FirstNonEmpty(configured.HeroSubheadline, "OmniRelay helps you maintain reliable, private internet routing across layered and restricted network environments."),
            PrimaryCtaText = FirstNonEmpty(configured.PrimaryCtaText, "Start Free Trial"),
            PrimaryCtaUrl = FirstNonEmpty(configured.PrimaryCtaUrl, "/account/register"),
            SecondaryCtaText = FirstNonEmpty(configured.SecondaryCtaText, "View Deployment Guide"),
            SecondaryCtaUrl = FirstNonEmpty(configured.SecondaryCtaUrl, "#how-it-works"),

            BenefitBlocks = NonEmptyOrDefault(configured.BenefitBlocks, [
                "Layered connectivity orchestration across segmented access environments.",
                "Private relay control so you are not dependent on public VPN providers.",
                "Policy-based operation for predictable behavior during network changes.",
                "Fast recovery and resilience during outages and route disruptions.",
                "Operational visibility for path health, readiness, and continuity decisions."
            ]),

            ResultCards = NonEmptyResultCards(configured.ResultCards, [
                new LandingResultCardOptions { Value = "38%", Label = "fewer access-related support tickets" },
                new LandingResultCardOptions { Value = "2.7x", Label = "faster first-time deployment cycle" },
                new LandingResultCardOptions { Value = "99.9%", Label = "gateway uptime during peak periods" }
            ]),

            Testimonials = NonEmptyTestimonials(configured.Testimonials, [
                new LandingTestimonialOptions
                {
                    Quote = "Before OmniRelay, every network change became an incident. Now onboarding new locations is predictable and calm.",
                    Author = "S. Rahimi",
                    Role = "Infrastructure Lead"
                },
                new LandingTestimonialOptions
                {
                    Quote = "Our team stopped debating routing workarounds and started shipping. Reliability improved in the first week.",
                    Author = "M. Daryan",
                    Role = "DevOps Manager"
                }
            ]),

            TrustBarText = FirstNonEmpty(configured.TrustBarText, "Used by operations and infrastructure teams managing restricted network environments."),

            HowItWorksSteps = NonEmptyOrDefault(configured.HowItWorksSteps, [
                "Define the available network links in your environment.",
                "Apply your routing policy and let OmniRelay establish secure relay paths.",
                "Monitor health, validate access, and maintain continuity as conditions change."
            ]),

            OfferTitle = FirstNonEmpty(configured.OfferTitle, "One-Time License"),
            OfferSummary = FirstNonEmpty(configured.OfferSummary, "Get full OmniRelay capabilities with a single purchase and predictable ownership cost."),
            OfferPriceAnchor = FirstNonEmpty(configured.OfferPriceAnchor, "$299 one-time license"),
            OfferRiskReducer = FirstNonEmpty(configured.OfferRiskReducer, "Start with a 2-day free trial before purchase."),
            MidCtaText = FirstNonEmpty(configured.MidCtaText, "Start Free Trial"),
            MidCtaUrl = FirstNonEmpty(configured.MidCtaUrl, "/account/register"),

            Faqs = NonEmptyFaqs(configured.Faqs, [
                new LandingFaqOptions
                {
                    Question = "How long does setup take?",
                    Answer = "Most teams complete initial setup in one session and validate access the same day."
                },
                new LandingFaqOptions
                {
                    Question = "Do we need to redesign our network?",
                    Answer = "No. OmniRelay is designed to fit existing environments and improve control without major topology changes."
                },
                new LandingFaqOptions
                {
                    Question = "Can we trial before paying?",
                    Answer = "Yes. You can start with a 2-day trial to validate fit and performance before purchase."
                },
                new LandingFaqOptions
                {
                    Question = "Is support available during rollout?",
                    Answer = "Yes. Documentation and operational guidance are included, with a clear runbook for common issues."
                },
                new LandingFaqOptions
                {
                    Question = "Will this work for browser and app traffic?",
                    Answer = "Yes. The platform is built to handle real-world application patterns with operational controls for reliability."
                }
            ]),

            ClosingHeadline = FirstNonEmpty(configured.ClosingHeadline, "Build your own anti-censorship connectivity layer."),
            ClosingBody = FirstNonEmpty(configured.ClosingBody, "Start your free trial, validate OmniRelay in your environment, and move to controlled private routing with a one-time license."),
            FinalCtaText = FirstNonEmpty(configured.FinalCtaText, "Start Free Trial"),
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
