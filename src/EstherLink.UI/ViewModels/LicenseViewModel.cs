using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EstherLink.UI.Services;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;

namespace EstherLink.UI.ViewModels;

public partial class LicenseViewModel : ObservableObject
{
    private const string RelayRoute = "relay";

    private readonly GatewayOrchestratorService _orchestrator;
    private readonly GatewayStateStore _state;
    private readonly INavigationService _navigationService;

    public LicenseViewModel(
        GatewayOrchestratorService orchestrator,
        GatewayStateStore state,
        INavigationService navigationService)
    {
        _orchestrator = orchestrator;
        _state = state;
        _navigationService = navigationService;
        _state.PropertyChanged += OnStateChanged;
        DeviceFingerprint = CreateFingerprint();
        RefreshCards();
    }

    public GatewayStateStore State => _state;

    [ObservableProperty]
    private string feedback = string.Empty;

    [ObservableProperty]
    private string licenseStatus = "Unknown";

    [ObservableProperty]
    private string expirationDate = "--";

    [ObservableProperty]
    private string deviceFingerprint = string.Empty;

    [ObservableProperty]
    private bool licenseActive;

    [ObservableProperty]
    private string bannerTitle = "No Active License";

    [ObservableProperty]
    private string bannerDescription = "Enter a valid license key and activate it to unlock professional features.";

    [ObservableProperty]
    private string bannerActionText = "Activation Required";

    [ObservableProperty]
    private bool bannerActionEnabled;

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private bool isServiceCompatible;

    [ObservableProperty]
    private string serviceCompatibilityMessage = "Checking relay service compatibility...";

    [ObservableProperty]
    private bool transferRequired;

    [ObservableProperty]
    private string activeDeviceHint = string.Empty;

    [ObservableProperty]
    private int transfersUsedInWindow;

    [ObservableProperty]
    private int transfersRemainingInWindow;

    [ObservableProperty]
    private int transferLimitPerRollingYear;

    private bool CanActivate() => !IsBusy && IsServiceCompatible;
    private bool CanTransfer() => !IsBusy && IsServiceCompatible && TransferRequired;
    private bool CanRefreshCompatibility() => !IsBusy;

    partial void OnIsBusyChanged(bool value)
    {
        ActivateLicenseCommand.NotifyCanExecuteChanged();
        TransferLicenseCommand.NotifyCanExecuteChanged();
        RefreshServiceCompatibilityCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsServiceCompatibleChanged(bool value)
    {
        ActivateLicenseCommand.NotifyCanExecuteChanged();
        TransferLicenseCommand.NotifyCanExecuteChanged();
    }

    partial void OnTransferRequiredChanged(bool value)
    {
        TransferLicenseCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanActivate))]
    private async Task ActivateLicenseAsync()
    {
        if (IsBusy)
        {
            return;
        }

        try
        {
            IsBusy = true;
            await RefreshServiceCompatibilityAsync();
            if (!IsServiceCompatible)
            {
                Feedback = ServiceCompatibilityMessage;
                return;
            }

            var result = await _orchestrator.VerifyLicenseAsync();
            Feedback = result.Message;
            await _orchestrator.RefreshStatusAsync();
            RefreshCards();

            if (LicenseActive)
            {
                _navigationService.Navigate(RelayRoute);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanTransfer))]
    private async Task TransferLicenseAsync()
    {
        if (IsBusy)
        {
            return;
        }

        try
        {
            IsBusy = true;
            var result = await _orchestrator.RequestLicenseTransferAndVerifyAsync();
            Feedback = result.Message;
            await _orchestrator.RefreshStatusAsync();
            RefreshCards();

            if (LicenseActive)
            {
                _navigationService.Navigate(RelayRoute);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRefreshCompatibility))]
    private async Task RefreshServiceCompatibilityAsync()
    {
        var result = await _orchestrator.CheckLicenseServiceCompatibilityAsync();
        IsServiceCompatible = result.Success;
        ServiceCompatibilityMessage = result.Message;
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(GatewayStateStore.Status) or nameof(GatewayStateStore.LicenseActivated) or nameof(GatewayStateStore.LicenseActivatedExpiresAtUtc))
        {
            RefreshCards();
        }
    }

    private void RefreshCards()
    {
        var status = _state.Status;
        LicenseActive = _state.LicenseActivated || status?.LicenseValid == true;
        var now = DateTimeOffset.UtcNow;
        var reason = status?.LicenseReason ?? string.Empty;
        var localKey = (_state.LicenseKey ?? string.Empty).Trim();
        var isTrial = localKey.StartsWith("OMNI-TRIAL", StringComparison.OrdinalIgnoreCase);
        var expiredByTime = status?.LicenseExpiresAtUtc is DateTimeOffset exp && exp <= now;

        LicenseStatus = status is null
            ? (LicenseActive ? (isTrial ? "Active (Trial)" : "Active") : "Unavailable")
            : LicenseActive
                ? (isTrial ? "Active (Trial)" : "Active")
                : (string.Equals(reason, "EXPIRED", StringComparison.OrdinalIgnoreCase) || expiredByTime)
                    ? "Expired"
                    : "Inactive";

        var expiresAt = _state.LicenseActivatedExpiresAtUtc ?? status?.LicenseExpiresAtUtc;

        ExpirationDate = expiresAt is null
            ? "--"
            : expiresAt.Value.LocalDateTime.ToString("MMM dd, yyyy");

        TransferRequired = status?.LicenseTransferRequired == true;
        ActiveDeviceHint = status?.LicenseActiveDeviceHint ?? string.Empty;
        TransfersUsedInWindow = status?.LicenseTransfersUsedInWindow ?? 0;
        TransfersRemainingInWindow = status?.LicenseTransfersRemainingInWindow ?? 0;
        TransferLimitPerRollingYear = status?.LicenseTransferLimitPerRollingYear ?? 0;

        if (LicenseActive)
        {
            BannerTitle = isTrial ? "Trial License is Active" : "License is Active";
            BannerDescription = isTrial
                ? "Trial access is active for this device until the listed expiration date."
                : "Your license is active for this device.";
            BannerActionText = "License Details";
            BannerActionEnabled = true;
        }
        else
        {
            BannerTitle = "No Active License";
            BannerDescription = (string.Equals(reason, "EXPIRED", StringComparison.OrdinalIgnoreCase) || expiredByTime)
                ? "Your license has expired. Enter a valid license key to continue."
                : TransferRequired
                ? $"License is active on {(string.IsNullOrWhiteSpace(ActiveDeviceHint) ? "another device" : ActiveDeviceHint)}. Confirm transfer to activate here."
                : "Activate a valid license to unlock professional routing, security features, and support.";
            BannerActionText = "Activation Required";
            BannerActionEnabled = false;
        }
    }

    private static string CreateFingerprint()
    {
        var raw = $"{Environment.MachineName}|{Environment.OSVersion.VersionString}|{Environment.UserDomainName}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        var hex = Convert.ToHexString(bytes);
        return $"{hex[..4]}-{hex[4..8]}-{hex[8..12]}-XXXX";
    }

    public async Task InitializeAsync()
    {
        await RefreshServiceCompatibilityAsync();
    }
}
