namespace OmniRelay.UI.Models;

public sealed class NavigationItemModel
{
    public required string Route { get; init; }
    public required string Title { get; init; }
    public required string IconGlyph { get; init; }
}
