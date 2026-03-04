namespace EstherLink.Backend.Configuration;

public sealed class CommerceOptions
{
    public string PaidLicensePlan { get; set; } = "professional";
    public int PaidMaxDevices { get; set; } = 3;
    public int TrialDays { get; set; } = 2;
    public int UpdateEntitlementMonths { get; set; } = 12;
}