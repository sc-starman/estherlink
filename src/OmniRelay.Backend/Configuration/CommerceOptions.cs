namespace OmniRelay.Backend.Configuration;

public sealed class CommerceOptions
{
    public string PaidLicensePlan { get; set; } = "professional";
    public int PaidMaxDevices { get; set; } = 1;
    public int TrialDays { get; set; } = 2;
    public int UpdateEntitlementMonths { get; set; } = 12;
}
