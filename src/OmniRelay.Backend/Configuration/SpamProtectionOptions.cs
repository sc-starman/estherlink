namespace OmniRelay.Backend.Configuration;

public sealed class SpamProtectionOptions
{
    public bool EnableRecaptcha { get; set; }
    public string RecaptchaSiteKey { get; set; } = string.Empty;
    public string RecaptchaSecretKey { get; set; } = string.Empty;
    public string RecaptchaVerifyUrl { get; set; } = "https://www.google.com/recaptcha/api/siteverify";
    public string RecaptchaExpectedAction { get; set; } = "contact_form";
    public double RecaptchaMinimumScore { get; set; } = 0.5;
}
