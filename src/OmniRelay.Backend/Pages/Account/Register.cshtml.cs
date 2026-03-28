using System.ComponentModel.DataAnnotations;
using System.Text;
using OmniRelay.Backend.Configuration;
using OmniRelay.Backend.Localization;
using OmniRelay.Backend.Models;
using OmniRelay.Backend.Services;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

namespace OmniRelay.Backend.Pages.Account;

[EnableRateLimiting("auth")]
public sealed class RegisterModel : PageModel
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IEmailDeliveryService _emailDeliveryService;
    private readonly IOptions<SpamProtectionOptions> _spamOptions;
    private readonly IRecaptchaVerifier _recaptchaVerifier;
    private readonly ILogger<RegisterModel> _logger;
    private readonly IStringLocalizer<SharedResource> _localizer;

    public RegisterModel(
        UserManager<ApplicationUser> userManager,
        IEmailDeliveryService emailDeliveryService,
        IOptions<SpamProtectionOptions> spamOptions,
        IRecaptchaVerifier recaptchaVerifier,
        IStringLocalizer<SharedResource> localizer,
        ILogger<RegisterModel> logger)
    {
        _userManager = userManager;
        _emailDeliveryService = emailDeliveryService;
        _spamOptions = spamOptions;
        _recaptchaVerifier = recaptchaVerifier;
        _localizer = localizer;
        _logger = logger;
    }

    [BindProperty]
    public RegisterInputModel Input { get; set; } = new();

    [TempData]
    public string? ErrorMessage { get; set; }

    [TempData]
    public string? SuccessMessage { get; set; }

    public bool RecaptchaEnabled { get; private set; }
    public string RecaptchaSiteKey { get; private set; } = string.Empty;

    public void OnGet()
    {
        ApplySpamOptions();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        ApplySpamOptions();

        if (!ModelState.IsValid)
        {
            _logger.LogWarning("Register form model validation failed. Errors: {Errors}", string.Join(" | ", GetModelStateErrors()));
            ErrorMessage = _localizer["Common.Msg.ValidationFailed"];
            return Page();
        }

        var recaptchaResult = await _recaptchaVerifier.VerifyAsync(
            Input.RecaptchaToken ?? string.Empty,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            HttpContext.RequestAborted,
            expectedAction: "register_form");
        if (!recaptchaResult.IsValid)
        {
            _logger.LogWarning("Registration blocked by reCAPTCHA verification: {Reason}", recaptchaResult.ErrorMessage);
            ErrorMessage = _localizer["Common.Msg.VerificationFailed"];
            return Page();
        }

        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            Email = Input.Email.Trim(),
            UserName = Input.Email.Trim(),
            CreatedAt = DateTimeOffset.UtcNow
        };

        var createResult = await _userManager.CreateAsync(user, Input.Password);
        if (!createResult.Succeeded)
        {
            ErrorMessage = string.Join(" ", createResult.Errors.Select(x => x.Description));
            return Page();
        }

        try
        {
            var rawToken = await _userManager.GenerateEmailConfirmationTokenAsync(user);
            var encodedToken = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(rawToken));
            var confirmUrl = Url.Page(
                "/Account/ConfirmEmail",
                pageHandler: null,
                values: new { userId = user.Id, code = encodedToken },
                protocol: Request.Scheme);

            if (string.IsNullOrWhiteSpace(confirmUrl))
            {
                throw new InvalidOperationException(_localizer["Register.Msg.ConfirmUrlGenerationFailed"]);
            }

            var body = _localizer["Register.Email.Body", confirmUrl].Value;

            await _emailDeliveryService.SendAsync(
                new EmailDeliveryMessage(
                    user.Email!,
                    _localizer["Register.Email.Subject"],
                    body,
                    ToName: user.UserName),
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send registration confirmation email for user {UserId}.", user.Id);
            await _userManager.DeleteAsync(user);
            ErrorMessage = _localizer["Register.Msg.EmailDeliveryFailed"];
            return Page();
        }

        SuccessMessage = _localizer["Register.Msg.AccountCreated"];
        return RedirectToPage("/Account/Login");
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

    public sealed class RegisterInputModel
    {
        [Required]
        [EmailAddress]
        public string Email { get; set; } = string.Empty;

        [Required]
        [DataType(DataType.Password)]
        [MinLength(8)]
        public string Password { get; set; } = string.Empty;

        [Required]
        [DataType(DataType.Password)]
        [Compare(nameof(Password), ErrorMessage = "Register.Validation.PasswordMismatch")]
        public string ConfirmPassword { get; set; } = string.Empty;

        [StringLength(4096)]
        public string? RecaptchaToken { get; set; }
    }
}
