using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OmniRelay.UI.Models;
using OmniRelay.UI.Services;

namespace OmniRelay.UI.ViewModels;

public partial class RelayGatewayOperationDialogViewModel : ObservableObject
{
    private readonly IDeploymentProgressAggregator _progressAggregator;
    private readonly Func<IProgress<DeploymentProgressSnapshot>, CancellationToken, Task<GatewayOperationResult>> _executeAsync;
    private readonly Func<IProgress<DeploymentProgressSnapshot>, GatewayOperationResult, CancellationToken, Task>? _afterOperationAsync;
    private readonly StringBuilder _operationLogBuilder = new();

    private CancellationTokenSource? _operationCts;

    public RelayGatewayOperationDialogViewModel(
        string title,
        IDeploymentProgressAggregator progressAggregator,
        Func<IProgress<DeploymentProgressSnapshot>, CancellationToken, Task<GatewayOperationResult>> executeAsync,
        Func<IProgress<DeploymentProgressSnapshot>, GatewayOperationResult, CancellationToken, Task>? afterOperationAsync = null)
    {
        GatewayOperationTitle = title;
        _progressAggregator = progressAggregator;
        _executeAsync = executeAsync;
        _afterOperationAsync = afterOperationAsync;
    }

    public event Action? RequestClose;

    [ObservableProperty]
    private string gatewayOperationTitle = string.Empty;

    [ObservableProperty]
    private string operationLog = string.Empty;

    [ObservableProperty]
    private bool isGatewayOperationRunning;

    [ObservableProperty]
    private bool isGatewayOperationInstallResultVisible;

    [ObservableProperty]
    private string gatewayOperationPanelUrl = string.Empty;

    [ObservableProperty]
    private string gatewayOperationPanelUsername = string.Empty;

    [ObservableProperty]
    private string gatewayOperationPanelPassword = string.Empty;

    [ObservableProperty]
    private bool isGatewayOperationPanelPasswordVisible;

    [ObservableProperty]
    private bool isGatewayProgressVisible;

    [ObservableProperty]
    private bool isGatewayProgressIndeterminate = true;

    [ObservableProperty]
    private double gatewayProgressPercent;

    [ObservableProperty]
    private string gatewayProgressMessage = string.Empty;

    public bool CanCancelGatewayOperation => IsGatewayOperationRunning;
    public bool CanCloseGatewayOperationDialog => !IsGatewayOperationRunning && !IsGatewayOperationInstallResultVisible;
    public bool CanAcknowledgeGatewayInstallSecrets => !IsGatewayOperationRunning && IsGatewayOperationInstallResultVisible;
    public string GatewayProgressPercentText => IsGatewayProgressIndeterminate ? string.Empty : $"{Math.Round(GatewayProgressPercent):0}%";
    public string GatewayOperationPanelPasswordDisplay =>
        IsGatewayOperationPanelPasswordVisible
            ? (GatewayOperationPanelPassword ?? string.Empty)
            : MaskSecret(GatewayOperationPanelPassword);

    public bool LastOperationSuccess { get; private set; }
    public string LastOperationMessage { get; private set; } = string.Empty;

    partial void OnIsGatewayOperationRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(CanCancelGatewayOperation));
        OnPropertyChanged(nameof(CanCloseGatewayOperationDialog));
        OnPropertyChanged(nameof(CanAcknowledgeGatewayInstallSecrets));
        CancelGatewayOperationCommand.NotifyCanExecuteChanged();
        CloseGatewayOperationDialogCommand.NotifyCanExecuteChanged();
        AcknowledgeGatewayInstallSecretsCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsGatewayOperationInstallResultVisibleChanged(bool value)
    {
        OnPropertyChanged(nameof(CanCloseGatewayOperationDialog));
        OnPropertyChanged(nameof(CanAcknowledgeGatewayInstallSecrets));
        CloseGatewayOperationDialogCommand.NotifyCanExecuteChanged();
        AcknowledgeGatewayInstallSecretsCommand.NotifyCanExecuteChanged();
    }

    partial void OnGatewayProgressPercentChanged(double value)
    {
        OnPropertyChanged(nameof(GatewayProgressPercentText));
    }

    partial void OnIsGatewayOperationPanelPasswordVisibleChanged(bool value)
    {
        OnPropertyChanged(nameof(GatewayOperationPanelPasswordDisplay));
    }

    public async Task RunAsync()
    {
        if (IsGatewayOperationRunning)
        {
            return;
        }

        var progress = CreateGatewayProgressReporter();
        BeginGatewayProgress(GatewayOperationTitle);
        IsGatewayOperationRunning = true;
        _operationCts?.Cancel();
        _operationCts?.Dispose();
        _operationCts = new CancellationTokenSource();
        var token = _operationCts.Token;

        var success = false;
        var finalMessage = $"{GatewayOperationTitle} did not complete.";

        try
        {
            var result = await _executeAsync(progress, token);
            var safeMessage = BuildSafeGatewayOperationMessage(result);
            AppendLog(safeMessage);
            success = result.Success;
            finalMessage = safeMessage;

            if (_afterOperationAsync is not null)
            {
                await _afterOperationAsync(progress, result, token);
            }

            PrepareInstallSecretsForOneTimeDisplay(result);
        }
        catch (OperationCanceledException)
        {
            finalMessage = $"{GatewayOperationTitle} canceled.";
            AppendLog(finalMessage);
            success = false;
        }
        catch (Exception ex)
        {
            finalMessage = $"{GatewayOperationTitle} failed: {ex.Message}";
            AppendLog(finalMessage);
            success = false;
        }
        finally
        {
            IsGatewayOperationRunning = false;
            _operationCts?.Dispose();
            _operationCts = null;
            EndGatewayProgress(success, finalMessage);
            LastOperationSuccess = success;
            LastOperationMessage = finalMessage;
        }
    }

    [RelayCommand(CanExecute = nameof(CanCancelGatewayOperation))]
    private void CancelGatewayOperation()
    {
        _operationCts?.Cancel();
    }

    [RelayCommand(CanExecute = nameof(CanCloseGatewayOperationDialog))]
    private void CloseGatewayOperationDialog()
    {
        ClearGatewayOperationInstallSecrets();
        RequestClose?.Invoke();
    }

    [RelayCommand(CanExecute = nameof(CanAcknowledgeGatewayInstallSecrets))]
    private void AcknowledgeGatewayInstallSecrets()
    {
        ClearGatewayOperationInstallSecrets();
    }

    [RelayCommand]
    private void CopyGatewayOperationPanelUsername()
    {
        var username = GatewayOperationPanelUsername?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(username))
        {
            return;
        }

        Clipboard.SetText(username);
    }

    [RelayCommand]
    private void CopyGatewayOperationPanelPassword()
    {
        var password = GatewayOperationPanelPassword ?? string.Empty;
        if (string.IsNullOrWhiteSpace(password))
        {
            return;
        }

        Clipboard.SetText(password);
    }

    [RelayCommand]
    private void OpenGatewayOperationPanelUrl()
    {
        var panelUrl = GatewayOperationPanelUrl?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(panelUrl))
        {
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = panelUrl,
            UseShellExecute = true
        });
    }

    private IProgress<DeploymentProgressSnapshot> CreateGatewayProgressReporter()
    {
        return new Progress<DeploymentProgressSnapshot>(snapshot =>
        {
            if (snapshot is null)
            {
                return;
            }

            UpdateGatewayProgress(snapshot);
            if (!string.IsNullOrWhiteSpace(snapshot.Message))
            {
                AppendLog(snapshot.Message);
            }
        });
    }

    private void BeginGatewayProgress(string operationName)
    {
        GatewayProgressPercent = 0;
        IsGatewayProgressIndeterminate = true;
        GatewayProgressMessage = operationName;
        IsGatewayProgressVisible = true;
    }

    private void UpdateGatewayProgress(DeploymentProgressSnapshot snapshot)
    {
        if (!string.IsNullOrWhiteSpace(snapshot.Message))
        {
            GatewayProgressMessage = snapshot.Message;
        }

        if (snapshot.Percent <= 0)
        {
            IsGatewayProgressIndeterminate = true;
            return;
        }

        IsGatewayProgressIndeterminate = false;
        GatewayProgressPercent = Math.Clamp(_progressAggregator.ToOverallPercent(snapshot), 0, 100);
    }

    private void EndGatewayProgress(bool success, string finalMessage)
    {
        GatewayProgressMessage = string.IsNullOrWhiteSpace(finalMessage)
            ? (success ? "Operation completed." : "Operation failed.")
            : finalMessage.Trim();
        IsGatewayProgressIndeterminate = false;
        GatewayProgressPercent = success ? 100 : GatewayProgressPercent;
    }

    private void AppendLog(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var line = $"{DateTimeOffset.Now:HH:mm:ss} {text.Trim()}";
        _operationLogBuilder.AppendLine(line);
        OperationLog = _operationLogBuilder.ToString();
    }

    private static string BuildSafeGatewayOperationMessage(GatewayOperationResult operation)
    {
        if (!operation.Success)
        {
            return operation.Message;
        }

        var hasPanelSecrets =
            !string.IsNullOrWhiteSpace(operation.PanelUrl) &&
            !string.IsNullOrWhiteSpace(operation.PanelUsername) &&
            !string.IsNullOrWhiteSpace(operation.InitialPanelPassword);

        if (hasPanelSecrets)
        {
            return "Gateway install completed. Panel credentials are shown one-time in this operation dialog.";
        }

        return operation.Message;
    }

    private void PrepareInstallSecretsForOneTimeDisplay(GatewayOperationResult result)
    {
        if (!result.Success)
        {
            return;
        }

        GatewayOperationPanelUrl = result.PanelUrl?.Trim() ?? string.Empty;
        GatewayOperationPanelUsername = result.PanelUsername?.Trim() ?? string.Empty;
        GatewayOperationPanelPassword = result.InitialPanelPassword ?? string.Empty;
        IsGatewayOperationPanelPasswordVisible = true;

        var hasSecrets =
            !string.IsNullOrWhiteSpace(GatewayOperationPanelUrl) &&
            !string.IsNullOrWhiteSpace(GatewayOperationPanelUsername) &&
            !string.IsNullOrWhiteSpace(GatewayOperationPanelPassword);

        IsGatewayOperationInstallResultVisible = hasSecrets;
        if (hasSecrets)
        {
            AppendLog("WARNING: Panel credentials are shown one-time only. Save them before closing this dialog.");
        }
    }

    private void ClearGatewayOperationInstallSecrets()
    {
        GatewayOperationPanelUrl = string.Empty;
        GatewayOperationPanelUsername = string.Empty;
        GatewayOperationPanelPassword = string.Empty;
        IsGatewayOperationPanelPasswordVisible = false;
        IsGatewayOperationInstallResultVisible = false;
    }

    private static string MaskSecret(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return new string('*', value.Length);
    }
}
