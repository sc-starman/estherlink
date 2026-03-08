using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OmniRelay.UI.Services;
using System.Collections.ObjectModel;
using System.Windows;

namespace OmniRelay.UI.ViewModels;

public partial class LogsViewModel : ObservableObject
{
    private readonly ILogReaderService _logReaderService;

    public LogsViewModel(ILogReaderService logReaderService)
    {
        _logReaderService = logReaderService;
    }

    public ObservableCollection<string> LogLines { get; } = [];

    [ObservableProperty]
    private string searchText = string.Empty;

    [ObservableProperty]
    private bool tailMode = true;

    [ObservableProperty]
    private string feedback = string.Empty;

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private string? selectedLine;

    private bool CanRefresh() => !IsBusy;
    private bool CanCopySelected() => !string.IsNullOrWhiteSpace(SelectedLine);

    partial void OnIsBusyChanged(bool value)
    {
        RefreshCommand.NotifyCanExecuteChanged();
        ClearFilterCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedLineChanged(string? value)
    {
        CopySelectedCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        if (IsBusy)
        {
            return;
        }

        try
        {
            IsBusy = true;
            var lines = await _logReaderService.ReadLatestAsync(300, SearchText);
            LogLines.Clear();
            foreach (var line in lines)
            {
                LogLines.Add(line);
            }

            Feedback = $"Loaded {LogLines.Count} log lines.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task ClearFilterAsync()
    {
        SearchText = string.Empty;
        await RefreshAsync();
    }

    [RelayCommand(CanExecute = nameof(CanCopySelected))]
    private void CopySelected()
    {
        if (!string.IsNullOrWhiteSpace(SelectedLine))
        {
            Clipboard.SetText(SelectedLine);
            Feedback = "Selected log line copied to clipboard.";
        }
    }
}
