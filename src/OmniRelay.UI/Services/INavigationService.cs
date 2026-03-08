using OmniRelay.UI.Models;
using System.Windows.Controls;

namespace OmniRelay.UI.Services;

public interface INavigationService
{
    IReadOnlyList<NavigationItemModel> Items { get; }
    string CurrentRoute { get; }
    event EventHandler<string>? RouteChanged;

    void Initialize(Frame frame, IServiceProvider serviceProvider);
    void Navigate(string route);
}
