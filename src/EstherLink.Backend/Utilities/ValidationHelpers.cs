using EstherLink.Backend.Contracts.App;
using EstherLink.Backend.Contracts.Licensing;
using EstherLink.Backend.Contracts.Whitelist;

namespace EstherLink.Backend.Utilities;

public static class ValidationHelpers
{
    public static Dictionary<string, string[]> ValidateLicenseVerify(LicenseVerifyRequest request)
    {
        var errors = new Dictionary<string, string[]>();

        if (string.IsNullOrWhiteSpace(request.LicenseKey))
        {
            errors[nameof(request.LicenseKey)] = ["LicenseKey is required."];
        }

        if (string.IsNullOrWhiteSpace(request.AppVersion))
        {
            errors[nameof(request.AppVersion)] = ["AppVersion is required."];
        }

        if (string.IsNullOrWhiteSpace(request.Nonce))
        {
            errors[nameof(request.Nonce)] = ["Nonce is required."];
        }

        if (request.Fingerprint.ValueKind is System.Text.Json.JsonValueKind.Undefined or System.Text.Json.JsonValueKind.Null)
        {
            errors[nameof(request.Fingerprint)] = ["Fingerprint is required."];
        }

        return errors;
    }

    public static Dictionary<string, string[]> ValidateCreateLicense(AdminCreateLicenseRequest request)
    {
        var errors = new Dictionary<string, string[]>();

        if (string.IsNullOrWhiteSpace(request.LicenseKey))
        {
            errors[nameof(request.LicenseKey)] = ["LicenseKey is required."];
        }

        if (string.IsNullOrWhiteSpace(request.Plan))
        {
            errors[nameof(request.Plan)] = ["Plan is required."];
        }

        if (request.MaxDevices < 1)
        {
            errors[nameof(request.MaxDevices)] = ["MaxDevices must be at least 1."];
        }

        return errors;
    }

    public static Dictionary<string, string[]> ValidateWhitelistCreate(AdminCreateWhitelistSetRequest request)
    {
        var errors = new Dictionary<string, string[]>();

        if (string.IsNullOrWhiteSpace(request.CountryCode) || request.CountryCode.Trim().Length != 2)
        {
            errors[nameof(request.CountryCode)] = ["CountryCode must be ISO alpha-2 (2 chars)."];
        }

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            errors[nameof(request.Name)] = ["Name is required."];
        }

        return errors;
    }

    public static Dictionary<string, string[]> ValidateWhitelistPublish(AdminPublishWhitelistRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        if (request.Entries is null || request.Entries.Count == 0)
        {
            errors[nameof(request.Entries)] = ["At least one CIDR/IP entry is required."];
        }

        return errors;
    }

    public static Dictionary<string, string[]> ValidateCreateRelease(AdminCreateReleaseRequest request)
    {
        var errors = new Dictionary<string, string[]>();

        if (string.IsNullOrWhiteSpace(request.Channel))
        {
            errors[nameof(request.Channel)] = ["Channel is required."];
        }

        if (string.IsNullOrWhiteSpace(request.Version))
        {
            errors[nameof(request.Version)] = ["Version is required."];
        }

        if (string.IsNullOrWhiteSpace(request.DownloadUrl))
        {
            errors[nameof(request.DownloadUrl)] = ["DownloadUrl is required."];
        }

        if (string.IsNullOrWhiteSpace(request.Sha256))
        {
            errors[nameof(request.Sha256)] = ["Sha256 is required."];
        }

        if (string.IsNullOrWhiteSpace(request.MinSupportedVersion))
        {
            errors[nameof(request.MinSupportedVersion)] = ["MinSupportedVersion is required."];
        }

        return errors;
    }
}
