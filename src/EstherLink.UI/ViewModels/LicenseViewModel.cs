using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EstherLink.UI.Services;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;

namespace EstherLink.UI.ViewModels;

public partial class LicenseViewModel : ObservableObject
{
    private readonly GatewayOrchestratorService _orchestrator;
    private readonly GatewayStateStore _state;

    public LicenseViewModel(GatewayOrchestratorService orchestrator, GatewayStateStore state)
    {
        _orchestrator = orchestrator;
        _state = state;
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

    private bool CanActivate() => !IsBusy;

    partial void OnIsBusyChanged(bool value)
    {
        ActivateLicenseCommand.NotifyCanExecuteChanged();
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
            var result = await _orchestrator.VerifyLicenseAsync();
            Feedback = result.Message;
            await _orchestrator.RefreshStatusAsync();
            RefreshCards();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(GatewayStateStore.Status))
        {
            RefreshCards();
        }
    }

    private void RefreshCards()
    {
        var status = _state.Status;
        LicenseActive = status?.LicenseValid == true;
        LicenseStatus = status is null
            ? "Unavailable"
            : status.LicenseValid
                ? "Active (Pro)"
                : "Inactive";

        ExpirationDate = status?.LicenseExpiresAtUtc is null
            ? "--"
            : status.LicenseExpiresAtUtc.Value.LocalDateTime.ToString("MMM dd, yyyy");

        if (LicenseActive)
        {
            BannerTitle = "Professional License is Active";
            BannerDescription = "You have access to 256-bit AES encryption, unlimited network configurations, and dedicated 24/7 technical support.";
            BannerActionText = "View Feature Log";
            BannerActionEnabled = true;
        }
        else
        {
            BannerTitle = "No Active License";
            BannerDescription = "Activate a valid license to unlock professional routing, security features, and support.";
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
}
