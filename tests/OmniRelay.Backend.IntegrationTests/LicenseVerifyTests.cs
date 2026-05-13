using System.Net.Http.Json;
using OmniRelay.Backend.Contracts.Licensing;
using OmniRelay.Backend.Data.Entities;
using OmniRelay.Backend.Data.Enums;

namespace OmniRelay.Backend.IntegrationTests;

public sealed class LicenseVerifyTests : IClassFixture<IntegrationTestWebApplicationFactory>
{
    private readonly IntegrationTestWebApplicationFactory _factory;

    public LicenseVerifyTests(IntegrationTestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Verify_ShouldRequireTransfer_AndAllowConfirmedDeviceMove()
    {
        await _factory.ResetDatabaseAsync();
        await _factory.ExecuteDbContextAsync(async dbContext =>
        {
            dbContext.Licenses.Add(new LicenseEntity
            {
                Id = Guid.NewGuid(),
                LicenseKey = "TEST-LIC-001",
                Status = LicenseStatus.Active,
                Plan = "professional",
                ExpiresAt = null,
                MaxDevices = 1,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            await dbContext.SaveChangesAsync();
        });

        var client = _factory.CreateClient();
        var firstRequest = new LicenseVerifyRequest
        {
            LicenseKey = "TEST-LIC-001",
            AppVersion = "1.2.3",
            Nonce = "nonce-a",
            Fingerprint = ParseJson("""{"machineGuid":"m1","nicMac":"aa-bb","osVersion":"win11"}""")
        };

        var firstResponse = await client.PostAsJsonAsync("/api/license/verify", firstRequest);
        firstResponse.EnsureSuccessStatusCode();
        var firstBody = await firstResponse.Content.ReadFromJsonAsync<LicenseVerifyResponse>();

        Assert.NotNull(firstBody);
        Assert.True(firstBody.Valid);
        Assert.Equal("OK", firstBody.Reason);
        Assert.Equal("Ed25519", firstBody.SignatureAlg);
        Assert.False(string.IsNullOrWhiteSpace(firstBody.KeyId));
        Assert.False(string.IsNullOrWhiteSpace(firstBody.RequestId));
        Assert.False(string.IsNullOrWhiteSpace(firstBody.Signature));
        Assert.NotNull(firstBody.LicenseCertificate);
        Assert.Equal("professional", firstBody.LicenseCertificate!.Plan);
        Assert.Equal("offline-cert-v1", firstBody.LicenseCertificate.KeyId);
        Assert.True(firstBody.LicenseCertificate.IsPerpetual);

        var secondRequest = new LicenseVerifyRequest
        {
            LicenseKey = "TEST-LIC-001",
            AppVersion = "1.2.4",
            Nonce = "nonce-b",
            Fingerprint = ParseJson("""{"machineGuid":"m2","nicMac":"cc-dd","osVersion":"win11"}""")
        };

        var secondResponse = await client.PostAsJsonAsync("/api/license/verify", secondRequest);
        secondResponse.EnsureSuccessStatusCode();
        var secondBody = await secondResponse.Content.ReadFromJsonAsync<LicenseVerifyResponse>();

        Assert.NotNull(secondBody);
        Assert.False(secondBody.Valid);
        Assert.Equal("TRANSFER_REQUIRED", secondBody.Reason);
        Assert.True(secondBody.TransferRequired);
        Assert.Equal(3, secondBody.TransferLimitPerRollingYear);
        Assert.Equal(3, secondBody.TransfersRemainingInWindow);
        Assert.Equal("Ed25519", secondBody.SignatureAlg);
        Assert.False(string.IsNullOrWhiteSpace(secondBody.KeyId));
        Assert.False(string.IsNullOrWhiteSpace(secondBody.Signature));

        var transferRequest = new LicenseVerifyRequest
        {
            LicenseKey = "TEST-LIC-001",
            AppVersion = "1.2.4",
            Nonce = "nonce-c",
            TransferRequested = true,
            Fingerprint = ParseJson("""{"machineGuid":"m2","nicMac":"cc-dd","osVersion":"win11"}""")
        };

        var transferResponse = await client.PostAsJsonAsync("/api/license/verify", transferRequest);
        transferResponse.EnsureSuccessStatusCode();
        var transferBody = await transferResponse.Content.ReadFromJsonAsync<LicenseVerifyResponse>();

        Assert.NotNull(transferBody);
        Assert.True(transferBody.Valid);
        Assert.Equal("OK", transferBody.Reason);
        Assert.Equal(1, transferBody.TransfersUsedInWindow);
        Assert.Equal(2, transferBody.TransfersRemainingInWindow);
        Assert.NotNull(transferBody.LicenseCertificate);
        Assert.NotEqual(firstBody.LicenseCertificate!.DeviceFingerprintHash, transferBody.LicenseCertificate!.DeviceFingerprintHash);

        var keys = await client.GetFromJsonAsync<LicensePublicKeysResponse>("/api/license/public-keys");
        Assert.NotNull(keys);
        Assert.NotEmpty(keys.Keys);
        Assert.Contains(keys.Keys, x => x.KeyId == firstBody.KeyId);
    }

    [Fact]
    public async Task Verify_ShouldNotReturnOfflineCertificate_ForTrialOrRevoked()
    {
        await _factory.ResetDatabaseAsync();
        await _factory.ExecuteDbContextAsync(async dbContext =>
        {
            dbContext.Licenses.AddRange(
                new LicenseEntity
                {
                    Id = Guid.NewGuid(),
                    LicenseKey = "TEST-TRIAL-001",
                    Status = LicenseStatus.Active,
                    Plan = "trial",
                    ExpiresAt = DateTimeOffset.UtcNow.AddDays(2),
                    MaxDevices = 1,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                },
                new LicenseEntity
                {
                    Id = Guid.NewGuid(),
                    LicenseKey = "TEST-REV-001",
                    Status = LicenseStatus.Revoked,
                    Plan = "professional",
                    ExpiresAt = null,
                    MaxDevices = 1,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                });
            await dbContext.SaveChangesAsync();
        });

        var client = _factory.CreateClient();
        var trialResponse = await client.PostAsJsonAsync("/api/license/verify", new LicenseVerifyRequest
        {
            LicenseKey = "TEST-TRIAL-001",
            AppVersion = "1.2.3",
            Nonce = "nonce-trial",
            Fingerprint = ParseJson("""{"machineGuid":"trial","osVersion":"win11"}""")
        });
        trialResponse.EnsureSuccessStatusCode();
        var trialBody = await trialResponse.Content.ReadFromJsonAsync<LicenseVerifyResponse>();
        Assert.NotNull(trialBody);
        Assert.True(trialBody.Valid);
        Assert.Null(trialBody.LicenseCertificate);

        var revokedResponse = await client.PostAsJsonAsync("/api/license/verify", new LicenseVerifyRequest
        {
            LicenseKey = "TEST-REV-001",
            AppVersion = "1.2.3",
            Nonce = "nonce-rev",
            Fingerprint = ParseJson("""{"machineGuid":"rev","osVersion":"win11"}""")
        });
        revokedResponse.EnsureSuccessStatusCode();
        var revokedBody = await revokedResponse.Content.ReadFromJsonAsync<LicenseVerifyResponse>();
        Assert.NotNull(revokedBody);
        Assert.False(revokedBody.Valid);
        Assert.Equal("REVOKED", revokedBody.Reason);
        Assert.Null(revokedBody.LicenseCertificate);
    }

    private static System.Text.Json.JsonElement ParseJson(string json)
    {
        return System.Text.Json.JsonDocument.Parse(json).RootElement.Clone();
    }
}
