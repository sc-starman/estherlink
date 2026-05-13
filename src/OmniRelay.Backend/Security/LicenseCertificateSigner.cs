using System.Security.Cryptography;
using System.Text;
using Chaos.NaCl;
using OmniRelay.Backend.Contracts.Licensing;

namespace OmniRelay.Backend.Security;

public sealed class LicenseCertificateSigner
{
    public const string SignatureAlgorithm = "Ed25519";
    public const string KeyId = "offline-cert-v1";

    private static readonly byte[] Seed = SHA256.HashData(Encoding.UTF8.GetBytes("OmniRelay.LicenseCertificate.Root.v1"));
    private static readonly byte[] ExpandedPrivateKey;

    static LicenseCertificateSigner()
    {
        Ed25519.KeyPairFromSeed(out _, out ExpandedPrivateKey, Seed);
    }

    public void Sign(LicenseCertificate certificate)
    {
        certificate.SignatureAlg = SignatureAlgorithm;
        certificate.KeyId = KeyId;
        var payload = BuildPayload(certificate);
        var signature = Ed25519.Sign(Encoding.UTF8.GetBytes(payload), ExpandedPrivateKey);
        certificate.Signature = Convert.ToBase64String(signature);
    }

    public static string BuildPayload(LicenseCertificate certificate)
    {
        return
            $"licenseId={certificate.LicenseId};" +
            $"plan={certificate.Plan};" +
            $"deviceFingerprintHash={certificate.DeviceFingerprintHash};" +
            $"issuedAt={certificate.IssuedAt.ToUniversalTime():O};" +
            $"isPerpetual={certificate.IsPerpetual};" +
            $"updateEntitlementUntil={Format(certificate.UpdateEntitlementUntil)};" +
            $"signatureAlg={certificate.SignatureAlg};" +
            $"keyId={certificate.KeyId}";
    }

    private static string Format(DateTimeOffset? value)
    {
        return value?.ToUniversalTime().ToString("O") ?? string.Empty;
    }
}
