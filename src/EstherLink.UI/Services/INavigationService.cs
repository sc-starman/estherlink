using EstherLink.UI.Models;
using System.Windows.Controls;

namespace EstherLink.UI.Services;

public interface INavigationService
{
    IReadOnlyList<NavigationItemModel> Items { get; }
    string CurrentRoute { get; }
    event EventHandler<string>? RouteChanged;

    void Initialize(Frame frame, IServiceProvider serviceProvider);
    void Navigate(string route);
}
