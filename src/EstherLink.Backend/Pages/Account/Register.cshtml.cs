using System.ComponentModel.DataAnnotations;
using EstherLink.Backend.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;

namespace EstherLink.Backend.Pages.Account;

[EnableRateLimiting("auth")]
public sealed class RegisterModel : PageModel
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly SignInManager<ApplicationUser> _signInManager;

    public RegisterModel(UserManager<ApplicationUser> userManager, SignInManager<ApplicationUser> signInManager)
    {
        _userManager = userManager;
        _signInManager = signInManager;
    }

    [BindProperty]
    public RegisterInputModel Input { get; set; } = new();

    [TempData]
    public string? ErrorMessage { get; set; }

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            Email = Input.Email.Trim(),
            UserName = Input.Email.Trim(),
            CreatedAt = DateTimeOffset.UtcNow,
            EmailConfirmed = true
        };

        var createResult = await _userManager.CreateAsync(user, Input.Password);
        if (!createResult.Succeeded)
        {
            ErrorMessage = string.Join(" ", createResult.Errors.Select(x => x.Description));
            return Page();
        }

        await _signInManager.SignInAsync(user, isPersistent: true);
        return RedirectToPage("/App/Dashboard");
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
        [Compare(nameof(Password), ErrorMessage = "Password and confirmation must match.")]
        public string ConfirmPassword { get; set; } = string.Empty;
    }
}