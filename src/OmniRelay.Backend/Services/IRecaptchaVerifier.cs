namespace OmniRelay.Backend.Services;

public interface IRecaptchaVerifier
{
    Task<RecaptchaVerificationResult> VerifyAsync(
        string token,
        string? remoteIp,
        CancellationToken cancellationToken,
        string? expectedAction = null);
}

public sealed record RecaptchaVerificationResult(bool IsValid, string? ErrorMessage = null);
