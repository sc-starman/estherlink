using OmniRelay.Backend.Localization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;

namespace OmniRelay.Backend.Pages;

public sealed class DocsModel : PageModel
{
    private readonly IStringLocalizer<SharedResource> _localizer;

    public DocsModel(IStringLocalizer<SharedResource> localizer)
    {
        _localizer = localizer;
    }

    public string LastUpdated => _localizer["Docs.LastUpdatedDate"];

    public IReadOnlyList<string> Introduction =>
    [
        _localizer["Docs.Introduction.1"],
        _localizer["Docs.Introduction.2"],
        _localizer["Docs.Introduction.3"]
    ];

    public IReadOnlyList<string> SystemRequirements =>
    [
        _localizer["Docs.Requirements.1"],
        _localizer["Docs.Requirements.2"],
        _localizer["Docs.Requirements.3"],
        _localizer["Docs.Requirements.4"],
        _localizer["Docs.Requirements.5"],
        _localizer["Docs.Requirements.6"]
    ];

    public IReadOnlyList<string> InstallationSteps =>
    [
        _localizer["Docs.Installation.1"],
        _localizer["Docs.Installation.2"],
        _localizer["Docs.Installation.3"],
        _localizer["Docs.Installation.4"],
        _localizer["Docs.Installation.5"]
    ];

    public IReadOnlyList<string> FirstLaunchSteps =>
    [
        _localizer["Docs.FirstLaunch.1"],
        _localizer["Docs.FirstLaunch.2"],
        _localizer["Docs.FirstLaunch.3"],
        _localizer["Docs.FirstLaunch.4"]
    ];

    public IReadOnlyList<string> LicenseActivationSteps =>
    [
        _localizer["Docs.Activation.1"],
        _localizer["Docs.Activation.2"],
        _localizer["Docs.Activation.3"],
        _localizer["Docs.Activation.4"],
        _localizer["Docs.Activation.5"]
    ];

    public IReadOnlyList<ConfigRow> RelayConfigRows =>
    [
        new(
            _localizer["Docs.RelayConfig.Row1.Field"],
            _localizer["Docs.RelayConfig.Row1.Description"],
            _localizer["Docs.RelayConfig.Row1.Valid"],
            _localizer["Docs.RelayConfig.Row1.Notes"]),
        new(
            _localizer["Docs.RelayConfig.Row2.Field"],
            _localizer["Docs.RelayConfig.Row2.Description"],
            _localizer["Docs.RelayConfig.Row2.Valid"],
            _localizer["Docs.RelayConfig.Row2.Notes"]),
        new(
            _localizer["Docs.RelayConfig.Row3.Field"],
            _localizer["Docs.RelayConfig.Row3.Description"],
            _localizer["Docs.RelayConfig.Row3.Valid"],
            _localizer["Docs.RelayConfig.Row3.Notes"]),
        new(
            _localizer["Docs.RelayConfig.Row4.Field"],
            _localizer["Docs.RelayConfig.Row4.Description"],
            _localizer["Docs.RelayConfig.Row4.Valid"],
            _localizer["Docs.RelayConfig.Row4.Notes"]),
        new(
            _localizer["Docs.RelayConfig.Row5.Field"],
            _localizer["Docs.RelayConfig.Row5.Description"],
            _localizer["Docs.RelayConfig.Row5.Valid"],
            _localizer["Docs.RelayConfig.Row5.Notes"])
    ];

    public IReadOnlyList<string> StartingRelaySteps =>
    [
        _localizer["Docs.StartRelay.1"],
        _localizer["Docs.StartRelay.2"],
        _localizer["Docs.StartRelay.3"],
        _localizer["Docs.StartRelay.4"],
        _localizer["Docs.StartRelay.5"],
        _localizer["Docs.StartRelay.6"]
    ];

    public IReadOnlyList<StatusRow> MonitoringRows =>
    [
        new(
            _localizer["Docs.Monitoring.Row1.Indicator"],
            _localizer["Docs.Monitoring.Row1.Meaning"],
            _localizer["Docs.Monitoring.Row1.Expected"]),
        new(
            _localizer["Docs.Monitoring.Row2.Indicator"],
            _localizer["Docs.Monitoring.Row2.Meaning"],
            _localizer["Docs.Monitoring.Row2.Expected"]),
        new(
            _localizer["Docs.Monitoring.Row3.Indicator"],
            _localizer["Docs.Monitoring.Row3.Meaning"],
            _localizer["Docs.Monitoring.Row3.Expected"]),
        new(
            _localizer["Docs.Monitoring.Row4.Indicator"],
            _localizer["Docs.Monitoring.Row4.Meaning"],
            _localizer["Docs.Monitoring.Row4.Expected"]),
        new(
            _localizer["Docs.Monitoring.Row5.Indicator"],
            _localizer["Docs.Monitoring.Row5.Meaning"],
            _localizer["Docs.Monitoring.Row5.Expected"]),
        new(
            _localizer["Docs.Monitoring.Row6.Indicator"],
            _localizer["Docs.Monitoring.Row6.Meaning"],
            _localizer["Docs.Monitoring.Row6.Expected"]),
        new(
            _localizer["Docs.Monitoring.Row7.Indicator"],
            _localizer["Docs.Monitoring.Row7.Meaning"],
            _localizer["Docs.Monitoring.Row7.Expected"])
    ];

    public IReadOnlyList<TroubleshootRow> TroubleshootingRows =>
    [
        new(
            _localizer["Docs.Troubleshooting.Row1.Problem"],
            _localizer["Docs.Troubleshooting.Row1.Action"],
            _localizer["Docs.Troubleshooting.Row1.Confirm"]),
        new(
            _localizer["Docs.Troubleshooting.Row2.Problem"],
            _localizer["Docs.Troubleshooting.Row2.Action"],
            _localizer["Docs.Troubleshooting.Row2.Confirm"]),
        new(
            _localizer["Docs.Troubleshooting.Row3.Problem"],
            _localizer["Docs.Troubleshooting.Row3.Action"],
            _localizer["Docs.Troubleshooting.Row3.Confirm"]),
        new(
            _localizer["Docs.Troubleshooting.Row4.Problem"],
            _localizer["Docs.Troubleshooting.Row4.Action"],
            _localizer["Docs.Troubleshooting.Row4.Confirm"]),
        new(
            _localizer["Docs.Troubleshooting.Row5.Problem"],
            _localizer["Docs.Troubleshooting.Row5.Action"],
            _localizer["Docs.Troubleshooting.Row5.Confirm"]),
        new(
            _localizer["Docs.Troubleshooting.Row6.Problem"],
            _localizer["Docs.Troubleshooting.Row6.Action"],
            _localizer["Docs.Troubleshooting.Row6.Confirm"]),
        new(
            _localizer["Docs.Troubleshooting.Row7.Problem"],
            _localizer["Docs.Troubleshooting.Row7.Action"],
            _localizer["Docs.Troubleshooting.Row7.Confirm"])
    ];

    public IReadOnlyList<string> UpdateSteps =>
    [
        _localizer["Docs.Update.1"],
        _localizer["Docs.Update.2"],
        _localizer["Docs.Update.3"],
        _localizer["Docs.Update.4"],
        _localizer["Docs.Update.5"],
        _localizer["Docs.Update.6"],
        _localizer["Docs.Update.7"]
    ];

    public void OnGet()
    {
    }
}

public sealed record ConfigRow(string Field, string Description, string ValidValues, string OperatorNotes);
public sealed record StatusRow(string Indicator, string Meaning, string ExpectedValue);
public sealed record TroubleshootRow(string Problem, string Action, string SuccessCheck);
