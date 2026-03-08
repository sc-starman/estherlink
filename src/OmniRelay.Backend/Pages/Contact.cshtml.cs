using System.ComponentModel.DataAnnotations;
using OmniRelay.Backend.Configuration;
using OmniRelay.Backend.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace OmniRelay.Backend.Pages;

[EnableRateLimiting("public")]
public sealed class ContactModel : PageModel
{
    private readonly IContactEmailSender _contactEmailSender;
    private readonly IOptions<WebOptions> _webOptions;
    private readonly IOptions<SpamProtectionOptions> _spamOptions;
    private readonly IRecaptchaVerifier _recaptchaVerifier;
    private readonly ILogger<ContactModel> _logger;

    public ContactModel(
        IContactEmailSender contactEmailSender,
        IOptions<WebOptions> webOptions,
        IOptions<SpamProtectionOptions> spamOptions,
        IRecaptchaVerifier recaptchaVerifier,
        ILogger<ContactModel> logger)
    {
        _contactEmailSender = contactEmailSender;
        _webOptions = webOptions;
        _spamOptions = spamOptions;
        _recaptchaVerifier = recaptchaVerifier;
        _logger = logger;
    }

    [BindProperty]
    public ContactInputModel Input { get; set; } = new();

    [TempData]
    public string? SuccessMessage { get; set; }

    [TempData]
    public string? ErrorMessage { get; set; }

    public bool RecaptchaEnabled { get; private set; }
    public string RecaptchaSiteKey { get; private set; } = string.Empty;

    public void OnGet()
    {
        ApplySpamOptions();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        ApplySpamOptions();

        if (!ModelState.IsValid)
        {
            _logger.LogWarning("Contact form model validation failed. Errors: {Errors}", string.Join(" | ", GetModelStateErrors()));
            ErrorMessage = "Form validation failed. Please refresh the page and try again.";
            return Page();
        }

        if (!string.IsNullOrWhiteSpace(Input.Website))
        {
            // Honeypot triggered; return generic success to avoid bot feedback.
            SuccessMessage = "Message sent successfully. Our team will contact you shortly.";
            return RedirectToPage("/Contact");
        }

        var recaptchaResult = await _recaptchaVerifier.VerifyAsync(
            Input.RecaptchaToken ?? string.Empty,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken,
            expectedAction: "contact_form");
        if (!recaptchaResult.IsValid)
        {
            ErrorMessage = "Verification failed. Please refresh and try again.";
            _logger.LogWarning("Contact submission blocked by spam checks: {Reason}", recaptchaResult.ErrorMessage);
            return Page();
        }

        var supportEmail = _webOptions.Value.SupportEmail;
        if (string.IsNullOrWhiteSpace(supportEmail))
        {
            ErrorMessage = "Support email is not configured. Please try again later.";
            return Page();
        }

        try
        {
            await _contactEmailSender.SendAsync(
                new ContactEmailMessage(
                    Input.Name.Trim(),
                    Input.Email.Trim(),
                    Input.Subject.Trim(),
                    Input.Message.Trim(),
                    supportEmail.Trim()),
                cancellationToken);

            SuccessMessage = "Message sent successfully. Our team will contact you shortly.";
            return RedirectToPage("/Contact");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send contact email.");
            ErrorMessage = "We could not send your message right now. Please try again later.";
            return Page();
        }
    }

    private void ApplySpamOptions()
    {
        var options = _spamOptions.Value;
        RecaptchaEnabled = options.EnableRecaptcha;
        RecaptchaSiteKey = options.RecaptchaSiteKey;
    }

    private IEnumerable<string> GetModelStateErrors()
    {
        foreach (var item in ModelState)
        {
            if (item.Value?.Errors is not { Count: > 0 })
            {
                continue;
            }

            foreach (var error in item.Value.Errors)
            {
                yield return $"{item.Key}: {error.ErrorMessage}";
            }
        }
    }

    public sealed class ContactInputModel
    {
        [Required]
        [StringLength(120)]
        public string Name { get; set; } = string.Empty;

        [Required]
        [EmailAddress]
        [StringLength(256)]
        public string Email { get; set; } = string.Empty;

        [Required]
        [StringLength(180)]
        public string Subject { get; set; } = string.Empty;

        [Required]
        [StringLength(5000, MinimumLength = 10)]
        public string Message { get; set; } = string.Empty;

        [StringLength(128)]
        public string? Website { get; set; }

        [StringLength(4096)]
        public string? RecaptchaToken { get; set; }
    }
}
