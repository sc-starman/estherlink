namespace EstherLink.Backend.Configuration;

public sealed class WebOptions
{
    public string SupportEmail { get; set; } = "support@omnirelay.local";
    public LandingContentOptions LandingContent { get; set; } = new();
}

public sealed class LandingContentOptions
{
    public string HeroHeadline { get; set; } = string.Empty;
    public string HeroSubheadline { get; set; } = string.Empty;
    public string PrimaryCtaText { get; set; } = string.Empty;
    public string PrimaryCtaUrl { get; set; } = string.Empty;
    public string SecondaryCtaText { get; set; } = string.Empty;
    public string SecondaryCtaUrl { get; set; } = string.Empty;

    public List<string> BenefitBlocks { get; set; } = [];
    public List<LandingResultCardOptions> ResultCards { get; set; } = [];
    public List<LandingTestimonialOptions> Testimonials { get; set; } = [];
    public string TrustBarText { get; set; } = string.Empty;

    public List<string> HowItWorksSteps { get; set; } = [];

    public string OfferTitle { get; set; } = string.Empty;
    public string OfferSummary { get; set; } = string.Empty;
    public string OfferPriceAnchor { get; set; } = string.Empty;
    public string OfferRiskReducer { get; set; } = string.Empty;
    public string MidCtaText { get; set; } = string.Empty;
    public string MidCtaUrl { get; set; } = string.Empty;

    public List<LandingFaqOptions> Faqs { get; set; } = [];

    public string ClosingHeadline { get; set; } = string.Empty;
    public string ClosingBody { get; set; } = string.Empty;
    public string FinalCtaText { get; set; } = string.Empty;
    public string FinalCtaUrl { get; set; } = string.Empty;
}

public sealed class LandingResultCardOptions
{
    public string Value { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
}

public sealed class LandingTestimonialOptions
{
    public string Quote { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
}

public sealed class LandingFaqOptions
{
    public string Question { get; set; } = string.Empty;
    public string Answer { get; set; } = string.Empty;
}
