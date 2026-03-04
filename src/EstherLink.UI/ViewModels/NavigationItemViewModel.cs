using CommunityToolkit.Mvvm.ComponentModel;
using EstherLink.UI.Models;

namespace EstherLink.UI.ViewModels;

public partial class NavigationItemViewModel : ObservableObject
{
    public NavigationItemViewModel(NavigationItemModel item)
    {
        Item = item;
    }

    public NavigationItemModel Item { get; }

    [ObservableProperty]
    private bool isActive;
}
