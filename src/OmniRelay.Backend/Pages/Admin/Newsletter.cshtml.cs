using OmniRelay.Backend.Data.Enums;
using OmniRelay.Backend.Localization;
using OmniRelay.Backend.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;
using System.ComponentModel.DataAnnotations;

namespace OmniRelay.Backend.Pages.Admin;

public sealed class NewsletterModel : PageModel
{
    private readonly INewsletterService _newsletterService;
    private readonly IStringLocalizer<SharedResource> _localizer;

    public NewsletterModel(INewsletterService newsletterService, IStringLocalizer<SharedResource> localizer)
    {
        _newsletterService = newsletterService;
        _localizer = localizer;
    }

    [TempData]
    public string? NoticeMessage { get; set; }

    [TempData]
    public string? ErrorMessage { get; set; }

    [BindProperty]
    public SendSingleInput Input { get; set; } = new();

    public IReadOnlyList<NewsletterCampaignView> Items { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Items = await _newsletterService.ListCampaignsAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostSendLatestAsync(CancellationToken cancellationToken)
    {
        var result = await _newsletterService.CreateLatestCampaignAsync(cancellationToken);
        if (result.Success)
        {
            NoticeMessage = result.Message;
        }
        else
        {
            ErrorMessage = result.Message;
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostSendSingleAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            ErrorMessage = _localizer["Common.Msg.ValidationFailed"];
            return RedirectToPage();
        }

        var result = await _newsletterService.CreateLatestCampaignForEmailAsync(Input.Email?.Trim() ?? string.Empty, cancellationToken);
        if (result.Success)
        {
            NoticeMessage = result.Message;
        }
        else
        {
            ErrorMessage = result.Message;
        }

        return RedirectToPage();
    }

    public string GetStateLabel(NewsletterState state)
    {
        return state switch
        {
            NewsletterState.Pending => _localizer["Admin.Newsletter.State.Pending"],
            NewsletterState.Running => _localizer["Admin.Newsletter.State.Running"],
            NewsletterState.Done => _localizer["Admin.Newsletter.State.Done"],
            _ => state.ToString()
        };
    }

    public sealed class SendSingleInput
    {
        [Required]
        [EmailAddress]
        public string Email { get; set; } = string.Empty;
    }
}
