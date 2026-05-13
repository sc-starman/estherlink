namespace OmniRelay.UI.Models;

public sealed class AdapterChoiceModel
{
    public string AdapterId { get; init; } = string.Empty;
    public int IfIndex { get; init; }
    public string MacAddress { get; init; } = string.Empty;
    public string Display { get; init; } = string.Empty;

    public override string ToString()
    {
        return Display;
    }
}
