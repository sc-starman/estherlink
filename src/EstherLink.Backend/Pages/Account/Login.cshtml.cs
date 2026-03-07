using System.ComponentModel.DataAnnotations;
using EstherLink.Backend.Configuration;
using EstherLink.Backend.Models;
using EstherLink.Backend.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace EstherLink.Backend.Pages.Account;

[EnableRateLimiting("auth")]
public sealed class LoginModel : PageModel
{
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly IOptions<SpamProtectionOptions> _spamOptions;
    private readonly IRecaptchaVerifier _recaptchaVerifier;
    private readonly ILogger<LoginModel> _logger;

    public LoginModel(
        SignInManager<ApplicationUser> signInManager,
        IOptions<SpamProtectionOptions> spamOptions,
        IRecaptchaVerifier recaptchaVerifier,
        ILogger<LoginModel> logger)
    {
        _signInManager = signInManager;
        _spamOptions = spamOptions;
        _recaptchaVerifier = recaptchaVerifier;
        _logger = logger;
    }

    [BindProperty]
    public LoginInputModel Input { get; set; } = new();

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

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
            _logger.LogWarning("Login form model validation failed. Errors: {Errors}", string.Join(" | ", GetModelStateErrors()));
            ErrorMessage = "Form validation failed. Please refresh the page and try again.";
            return Page();
        }

        var recaptchaResult = await _recaptchaVerifier.VerifyAsync(
            Input.RecaptchaToken ?? string.Empty,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            HttpContext.RequestAborted,
            expectedAction: "login_form");
        if (!recaptchaResult.IsValid)
        {
            _logger.LogWarning("Login blocked by reCAPTCHA verification: {Reason}", recaptchaResult.ErrorMessage);
            ErrorMessage = "Verification failed. Please refresh and try again.";
            return Page();
        }

        var result = await _signInManager.PasswordSignInAsync(
            Input.Email.Trim(),
            Input.Password,
            Input.RememberMe,
            lockoutOnFailure: true);

        if (!result.Succeeded)
        {
            ErrorMessage = result.IsLockedOut
                ? "Account is temporarily locked. Try again later."
                : result.IsNotAllowed
                    ? "Please confirm your email address before logging in."
                    : "Invalid credentials.";
            return Page();
        }

        if (!string.IsNullOrWhiteSpace(ReturnUrl) && Url.IsLocalUrl(ReturnUrl))
        {
            return LocalRedirect(ReturnUrl);
        }

        return RedirectToPage("/App/Dashboard");
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

    public sealed class LoginInputModel
    {
        [Required]
        [EmailAddress]
        public string Email { get; set; } = string.Empty;

        [Required]
        [DataType(DataType.Password)]
        public string Password { get; set; } = string.Empty;

        public bool RememberMe { get; set; }

        [StringLength(4096)]
        public string? RecaptchaToken { get; set; }
    }
}
