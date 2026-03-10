using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using OmniRelay.Core.Policy;
using OmniRelay.UI.Services;
using System.IO;
using System.Text;
using System.Windows;

namespace OmniRelay.UI.ViewModels;

public partial class WhitelistViewModel : ObservableObject
{
    private readonly GatewayOrchestratorService _orchestrator;

    public WhitelistViewModel(GatewayOrchestratorService orchestrator)
    {
        _orchestrator = orchestrator;
    }

    [ObservableProperty]
    private string whitelistText = string.Empty;

    [ObservableProperty]
    private string blacklistText = string.Empty;

    [ObservableProperty]
    private string feedback = string.Empty;

    [ObservableProperty]
    private string validationSummary = string.Empty;

    [ObservableProperty]
    private string whitelistSummary = "Not loaded";

    [ObservableProperty]
    private string blacklistSummary = "Not loaded";

    [ObservableProperty]
    private bool isBusy;

    private bool CanRun() => !IsBusy;

    partial void OnIsBusyChanged(bool value)
    {
        RefreshCommand.NotifyCanExecuteChanged();
        ValidateCommand.NotifyCanExecuteChanged();
        ValidateBlacklistCommand.NotifyCanExecuteChanged();
        UpdateWhitelistCommand.NotifyCanExecuteChanged();
        UpdateBlacklistCommand.NotifyCanExecuteChanged();
        ImportWhitelistCommand.NotifyCanExecuteChanged();
        ImportBlacklistCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RefreshAsync()
    {
        await RunBusyAsync(async () =>
        {
            await ReloadListAsync(PolicyListTypes.Whitelist);
            await ReloadListAsync(PolicyListTypes.Blacklist);
            Feedback = "Policy lists refreshed.";
        });
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private void Validate()
    {
        var errors = _orchestrator.ValidatePolicyLines(WhitelistText);
        ValidationSummary = errors.Count == 0
            ? "Whitelist syntax looks valid."
            : string.Join(Environment.NewLine, errors);
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private void ValidateBlacklist()
    {
        var errors = _orchestrator.ValidatePolicyLines(BlacklistText);
        ValidationSummary = errors.Count == 0
            ? "Blacklist syntax looks valid."
            : string.Join(Environment.NewLine, errors);
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task UpdateWhitelistAsync()
    {
        await UpdatePolicyTextAsync(PolicyListTypes.Whitelist, PolicyUpdateModes.Replace, WhitelistText);
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task UpdateBlacklistAsync()
    {
        await UpdatePolicyTextAsync(PolicyListTypes.Blacklist, PolicyUpdateModes.Replace, BlacklistText);
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task ImportWhitelistAsync()
    {
        await ImportPolicyAsync(PolicyListTypes.Whitelist);
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task ImportBlacklistAsync()
    {
        await ImportPolicyAsync(PolicyListTypes.Blacklist);
    }

    private async Task ImportPolicyAsync(string listType)
    {
        var dialog = new OpenFileDialog
        {
            Title = $"Import {listType} entries",
            Filter = "Text/CSV files|*.txt;*.csv|Text files|*.txt|CSV files|*.csv|All files|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var mode = SelectImportMode();
        if (string.IsNullOrWhiteSpace(mode))
        {
            return;
        }

        var parsed = ParseImportFile(dialog.FileName);
        if (parsed.Entries.Count == 0)
        {
            Feedback = $"No valid {listType} entries found in import file.";
            return;
        }

        await RunBusyAsync(async () =>
        {
            var commit = await _orchestrator.UpdatePolicyListAsync(listType, mode, parsed.Entries);
            Feedback = commit.Success
                ? $"{commit.Message} Imported={parsed.Entries.Count}, invalidRows={parsed.InvalidCount}."
                : commit.Message;

            if (commit.Success)
            {
                await ReloadListAsync(listType);
                await _orchestrator.RefreshStatusAsync();
            }
        });
    }

    private async Task UpdatePolicyTextAsync(string listType, string mode, string text)
    {
        await RunBusyAsync(async () =>
        {
            var errors = _orchestrator.ValidatePolicyLines(text);
            if (errors.Count > 0)
            {
                ValidationSummary = string.Join(Environment.NewLine, errors);
                Feedback = $"{listType} contains invalid entries.";
                return;
            }

            var entries = ExtractEntriesFromText(text);
            var commit = await _orchestrator.UpdatePolicyListAsync(listType, mode, entries);
            Feedback = commit.Message;
            if (commit.Success)
            {
                await ReloadListAsync(listType);
                await _orchestrator.RefreshStatusAsync();
            }
        });
    }

    private async Task ReloadListAsync(string listType)
    {
        var result = await _orchestrator.GetPolicyListAsync(listType);
        if (!result.Success)
        {
            Feedback = result.Message;
            return;
        }

        var text = string.Join(Environment.NewLine, result.Entries);
        var summary = $"Count={result.Count}, revision={result.Revision}, updated={result.UpdatedAtUtc:yyyy-MM-dd HH:mm:ss} UTC";
        if (listType == PolicyListTypes.Blacklist)
        {
            BlacklistText = text;
            BlacklistSummary = summary;
        }
        else
        {
            WhitelistText = text;
            WhitelistSummary = summary;
        }
    }

    private static List<string> ExtractEntriesFromText(string? text)
    {
        var entries = new List<string>();
        var lines = (text ?? string.Empty).Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        foreach (var line in lines)
        {
            var value = (line ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(value) || value.StartsWith('#'))
            {
                continue;
            }

            var commentIndex = value.IndexOf('#');
            if (commentIndex >= 0)
            {
                value = value[..commentIndex].Trim();
            }

            if (!string.IsNullOrWhiteSpace(value))
            {
                entries.Add(value);
            }
        }

        return entries;
    }

    private static string SelectImportMode()
    {
        var choice = MessageBox.Show(
            "Choose import mode:\nYes = Replace existing list\nNo = Merge with existing list\nCancel = Abort import",
            "Import Mode",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);

        return choice switch
        {
            MessageBoxResult.Yes => PolicyUpdateModes.Replace,
            MessageBoxResult.No => PolicyUpdateModes.Merge,
            _ => string.Empty
        };
    }

    private static ImportParseResult ParseImportFile(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension == ".csv"
            ? ParseCsvImport(path)
            : ParseTextImport(path);
    }

    private static ImportParseResult ParseTextImport(string path)
    {
        var entries = new List<string>();
        var invalid = 0;

        foreach (var line in File.ReadLines(path))
        {
            var token = (line ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(token) || token.StartsWith('#'))
            {
                continue;
            }

            var commentIndex = token.IndexOf('#');
            if (commentIndex >= 0)
            {
                token = token[..commentIndex].Trim();
            }

            if (string.IsNullOrWhiteSpace(token))
            {
                continue;
            }

            if (NetworkRule.TryParse(token, out _, out _))
            {
                entries.Add(token);
            }
            else
            {
                invalid++;
            }
        }

        return new ImportParseResult(entries, invalid);
    }

    private static ImportParseResult ParseCsvImport(string path)
    {
        var entries = new List<string>();
        var invalid = 0;

        foreach (var rawLine in File.ReadLines(path))
        {
            var line = (rawLine ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var columns = ParseCsvLine(line);
            var found = false;
            foreach (var column in columns)
            {
                var token = (column ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(token))
                {
                    continue;
                }

                if (NetworkRule.TryParse(token, out _, out _))
                {
                    entries.Add(token);
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                invalid++;
            }
        }

        return new ImportParseResult(entries, invalid);
    }

    private static List<string> ParseCsvLine(string line)
    {
        var values = new List<string>();
        var sb = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    sb.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }

                continue;
            }

            if (ch == ',' && !inQuotes)
            {
                values.Add(sb.ToString());
                sb.Clear();
                continue;
            }

            sb.Append(ch);
        }

        values.Add(sb.ToString());
        return values;
    }

    private async Task RunBusyAsync(Func<Task> action)
    {
        if (IsBusy)
        {
            return;
        }

        try
        {
            IsBusy = true;
            await action();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private sealed record ImportParseResult(IReadOnlyList<string> Entries, int InvalidCount);
}
