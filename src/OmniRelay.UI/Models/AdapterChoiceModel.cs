namespace OmniRelay.UI.Models;

public sealed class AdapterChoiceModel
{
    public int IfIndex { get; init; }
    public string Display { get; init; } = string.Empty;

    public override string ToString()
    {
        return Display;
    }
}
