using CommunityToolkit.Mvvm.ComponentModel;
using OmniRelay.UI.Models;

namespace OmniRelay.UI.ViewModels;

public partial class NavigationItemViewModel : ObservableObject
{
    public NavigationItemViewModel(NavigationItemModel item)
    {
        Item = item;
    }

    public NavigationItemModel Item { get; }

    [ObservableProperty]
    private bool isActive;

    [ObservableProperty]
    private bool isEnabled = true;
}
