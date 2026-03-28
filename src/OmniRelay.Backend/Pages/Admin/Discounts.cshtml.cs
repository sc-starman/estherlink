using System.Security.Cryptography;
using OmniRelay.Backend.Data;
using OmniRelay.Backend.Utilities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace OmniRelay.Backend.Pages.Admin;

public sealed class DiscountsModel : PageModel
{
    private const int DefaultPageSize = 50;
    private readonly AppDbContext _dbContext;

    public DiscountsModel(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    [BindProperty(SupportsGet = true)]
    public int PageNumber { get; set; } = 1;

    [BindProperty]
    public CreateCouponInput Input { get; set; } = new();

    [TempData]
    public string? NoticeMessage { get; set; }

    [TempData]
    public string? ErrorMessage { get; set; }

    public int PageSize { get; } = DefaultPageSize;
    public int TotalItems { get; private set; }
    public int ActiveItems { get; private set; }
    public int TotalPages => Math.Max(1, (int)Math.Ceiling(TotalItems / (double)PageSize));
    public List<DiscountCouponItem> Items { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostCreateAsync(CancellationToken cancellationToken)
    {
        if (Input.DiscountPercent is < 1 or > 99)
        {
            ModelState.AddModelError($"{nameof(Input)}.{nameof(Input.DiscountPercent)}", "Discount percent must be between 1 and 99.");
        }

        if (Input.MaxUses <= 0)
        {
            ModelState.AddModelError($"{nameof(Input)}.{nameof(Input.MaxUses)}", "Max uses must be greater than 0.");
        }

        var normalizedCode = NormalizeCode(Input.Code, out var codeError);
        if (codeError is not null)
        {
            ModelState.AddModelError($"{nameof(Input)}.{nameof(Input.Code)}", codeError);
        }

        var userId = User.GetUserId();
        if (!userId.HasValue)
        {
            return Forbid();
        }

        if (!ModelState.IsValid)
        {
            await LoadAsync(cancellationToken);
            return Page();
        }

        string code;
        if (!string.IsNullOrWhiteSpace(normalizedCode))
        {
            var exists = await _dbContext.DiscountCoupons
                .AsNoTracking()
                .AnyAsync(x => x.NormalizedCode == normalizedCode, cancellationToken);

            if (exists)
            {
                ModelState.AddModelError($"{nameof(Input)}.{nameof(Input.Code)}", "Coupon code already exists.");
                await LoadAsync(cancellationToken);
                return Page();
            }

            code = normalizedCode;
        }
        else
        {
            var generated = await GenerateUniqueCodeAsync(cancellationToken);
            if (generated is null)
            {
                ModelState.AddModelError($"{nameof(Input)}.{nameof(Input.Code)}", "Failed to generate a unique coupon code. Please try again.");
                await LoadAsync(cancellationToken);
                return Page();
            }

            code = generated;
        }

        _dbContext.DiscountCoupons.Add(new()
        {
            Id = Guid.NewGuid(),
            Code = code,
            NormalizedCode = code,
            DiscountPercent = Input.DiscountPercent,
            MaxUses = Input.MaxUses,
            TimesRedeemed = 0,
            IsActive = true,
            DisabledAt = null,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = userId.Value
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        NoticeMessage = $"Coupon {code} created.";

        return RedirectToPage(new { pageNumber = 1 });
    }

    public async Task<IActionResult> OnPostDisableAsync(Guid id, CancellationToken cancellationToken)
    {
        var coupon = await _dbContext.DiscountCoupons.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (coupon is null)
        {
            ErrorMessage = "Coupon not found.";
            return RedirectToPage(new { pageNumber = PageNumber });
        }

        if (coupon.IsActive && coupon.DisabledAt is null)
        {
            coupon.IsActive = false;
            coupon.DisabledAt = DateTimeOffset.UtcNow;
            await _dbContext.SaveChangesAsync(cancellationToken);
            NoticeMessage = $"Coupon {coupon.Code} disabled.";
        }
        else
        {
            NoticeMessage = $"Coupon {coupon.Code} is already disabled.";
        }

        return RedirectToPage(new { pageNumber = PageNumber });
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        if (PageNumber < 1)
        {
            PageNumber = 1;
        }

        var query = _dbContext.DiscountCoupons
            .AsNoTracking()
            .OrderByDescending(x => x.CreatedAt);

        TotalItems = await query.CountAsync(cancellationToken);
        ActiveItems = await _dbContext.DiscountCoupons
            .AsNoTracking()
            .CountAsync(x => x.IsActive && x.DisabledAt == null, cancellationToken);

        if (PageNumber > TotalPages)
        {
            PageNumber = TotalPages;
        }

        var skip = (PageNumber - 1) * PageSize;
        Items = await query
            .Skip(skip)
            .Take(PageSize)
            .Select(x => new DiscountCouponItem
            {
                Id = x.Id,
                Code = x.Code,
                DiscountPercent = x.DiscountPercent,
                MaxUses = x.MaxUses,
                TimesRedeemed = x.TimesRedeemed,
                IsActive = x.IsActive && x.DisabledAt == null,
                CreatedAt = x.CreatedAt,
                CreatorEmail = x.CreatedByUser.Email ?? string.Empty
            })
            .ToListAsync(cancellationToken);
    }

    private async Task<string?> GenerateUniqueCodeAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var candidate = $"OMNI-{GenerateCodeSegment(4)}-{GenerateCodeSegment(4)}";
            var exists = await _dbContext.DiscountCoupons
                .AsNoTracking()
                .AnyAsync(x => x.NormalizedCode == candidate, cancellationToken);

            if (!exists)
            {
                return candidate;
            }
        }

        return null;
    }

    private static string GenerateCodeSegment(int length)
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        Span<char> chars = stackalloc char[length];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        }

        return new string(chars);
    }

    private static string? NormalizeCode(string? value, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim().ToUpperInvariant();
        if (trimmed.Length > 64)
        {
            error = "Coupon code must be 64 characters or fewer.";
            return null;
        }

        foreach (var ch in trimmed)
        {
            if (char.IsLetterOrDigit(ch) || ch == '-' || ch == '_')
            {
                continue;
            }

            error = "Coupon code may only contain letters, digits, '-' or '_'.";
            return null;
        }

        return trimmed;
    }

    public sealed class CreateCouponInput
    {
        public string? Code { get; set; }
        public int DiscountPercent { get; set; } = 30;
        public int MaxUses { get; set; } = 25;
    }

    public sealed class DiscountCouponItem
    {
        public Guid Id { get; set; }
        public string Code { get; set; } = string.Empty;
        public int DiscountPercent { get; set; }
        public int MaxUses { get; set; }
        public int TimesRedeemed { get; set; }
        public bool IsActive { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public string CreatorEmail { get; set; } = string.Empty;
        public int RemainingUses => Math.Max(0, MaxUses - TimesRedeemed);
    }
}
