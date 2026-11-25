using ExoLauncher.Helpers;
using ExoLauncher.Services;
using Xunit;

namespace ExoLauncher.Tests;

public sealed class ExoNativeApoTests
{
    private const string TestGuid = "{9709e4ab-1234-5678-abcd-0123456789ab}";

    [Fact]
    public void Disabled_eq_never_prompts_reenable()
    {
        var status = ExoNativeApo.EvaluateHealth(new NativeApoHealthSnapshot(
            HelperAvailable: true,
            SystemEqEnabled: false,
            DrmDisclosureAccepted: false,
            SigningMode: null,
            ProtectedAudioDgDisabled: false,
            EndpointRegistered: false,
            AudiodgRejected: true,
            EffectsOff: true));

        Assert.True(status.NativeAvailable);
        Assert.False(status.SystemEqEnabled);
        Assert.False(status.NeedsReenable);
        Assert.Null(status.ReenableReason);
    }

    [Fact]
    public void Unsigned_enabled_eq_is_healthy_when_policy_and_endpoint_hold()
    {
        var status = ExoNativeApo.EvaluateHealth(new NativeApoHealthSnapshot(
            HelperAvailable: true,
            SystemEqEnabled: true,
            DrmDisclosureAccepted: true,
            SigningMode: "unsignedFreeRoute",
            ProtectedAudioDgDisabled: true,
            EndpointRegistered: true,
            AudiodgRejected: false,
            EffectsOff: false));

        Assert.True(status.SystemEqEnabled);
        Assert.False(status.NeedsReenable);
        Assert.Null(status.ReenableReason);
    }

    [Fact]
    public void Windows_update_reset_of_DisableProtectedAudioDG_triggers_reenable_prompt()
    {
        var status = ExoNativeApo.EvaluateHealth(new NativeApoHealthSnapshot(
            HelperAvailable: true,
            SystemEqEnabled: true,
            DrmDisclosureAccepted: true,
            SigningMode: "unsignedFreeRoute",
            ProtectedAudioDgDisabled: false,
            EndpointRegistered: true,
            AudiodgRejected: false,
            EffectsOff: false));

        Assert.True(status.NeedsReenable);
        Assert.Equal("protected-audiodg-reset", status.ReenableReason);
    }

    [Fact]
    public void Audiodg_rejection_triggers_reenable_prompt()
    {
        var status = ExoNativeApo.EvaluateHealth(new NativeApoHealthSnapshot(
            HelperAvailable: true,
            SystemEqEnabled: true,
            DrmDisclosureAccepted: true,
            SigningMode: "unsignedFreeRoute",
            ProtectedAudioDgDisabled: true,
            EndpointRegistered: true,
            AudiodgRejected: true,
            EffectsOff: false));

        Assert.True(status.NeedsReenable);
        Assert.Equal("audiodg-rejected", status.ReenableReason);
    }

    [Fact]
    public void Endpoint_wipe_triggers_reenable_prompt()
    {
        var status = ExoNativeApo.EvaluateHealth(new NativeApoHealthSnapshot(
            HelperAvailable: true,
            SystemEqEnabled: true,
            DrmDisclosureAccepted: true,
            SigningMode: "unsignedFreeRoute",
            ProtectedAudioDgDisabled: true,
            EndpointRegistered: false,
            AudiodgRejected: false,
            EffectsOff: false));

        Assert.True(status.NeedsReenable);
        Assert.Equal("endpoint-reset", status.ReenableReason);
    }

    [Fact]
    public void Signed_mode_remains_healthy_without_DisableProtectedAudioDG()
    {
        var status = ExoNativeApo.EvaluateHealth(new NativeApoHealthSnapshot(
            HelperAvailable: true,
            SystemEqEnabled: true,
            DrmDisclosureAccepted: false,
            SigningMode: "signed",
            ProtectedAudioDgDisabled: false,
            EndpointRegistered: true,
            AudiodgRejected: false,
            EffectsOff: false));

        Assert.False(status.NeedsReenable);
        Assert.Null(status.ReenableReason);
    }

    [Fact]
    public async Task Enable_refuses_silently_without_drm_disclosure_confirmation()
    {
        var err = await ExoNativeApo.EnableSystemEqAsync(
            TestGuid,
            drmDisclosureAccepted: false,
            signedMode: false,
            ct: TestContext.Current.CancellationToken);

        Assert.NotNull(err);
        Assert.Contains("disclosure", err, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Helper_arguments_include_explicit_consent_and_mode_flags()
    {
        var unsignedArgs = ExoNativeApo.BuildEnableArguments(
            TestGuid,
            @"C:\Exo\exo_apo.dll",
            @"C:\Data\exo-apo-ledger.json",
            drmDisclosureAccepted: true,
            signedMode: false);
        Assert.Contains("--explicit-user-consent", unsignedArgs);
        Assert.Contains("--drm-disclosure-accepted", unsignedArgs);
        Assert.DoesNotContain("--signed", unsignedArgs);

        var signedArgs = ExoNativeApo.BuildEnableArguments(
            TestGuid,
            @"C:\Exo\exo_apo.dll",
            @"C:\Data\exo-apo-ledger.json",
            drmDisclosureAccepted: false,
            signedMode: true);
        Assert.Contains("--explicit-user-consent", signedArgs);
        Assert.Contains("--signed", signedArgs);

        var rollbackArgs = ExoNativeApo.BuildRollbackArguments(@"C:\Data\exo-apo-ledger.json");
        Assert.Equal("rollback --ledger \"C:\\Data\\exo-apo-ledger.json\"", rollbackArgs);
    }

    [Fact]
    public void Ledger_reader_parses_enabled_state_and_endpoints_from_temp_file()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"exo-apo-ledger-test-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(tmp, """
                {
                  "version": 1,
                  "systemEqEnabled": true,
                  "drmDisclosureAccepted": true,
                  "signingMode": "unsignedFreeRoute",
                  "endpoints": {
                    "{9709e4ab-1234-5678-abcd-0123456789ab}": {
                      "endpointGuid": "{9709e4ab-1234-5678-abcd-0123456789ab}",
                      "originalFxValues": {},
                      "registeredClsid": "{7E3A8C14-4F2B-4A91-9D5E-2B8C6F1A4E03}",
                      "usedCompositeFx": true,
                      "preservedOemClsids": []
                    }
                  }
                }
                """);
            var (enabled, disclosed, mode, endpoints) = ExoNativeApo.ReadLedgerState(tmp);
            Assert.True(enabled);
            Assert.True(disclosed);
            Assert.Equal("unsignedFreeRoute", mode);
            Assert.Single(endpoints);
            Assert.Equal(TestGuid, endpoints[0]);
        }
        finally
        {
            if (File.Exists(tmp)) DeletionGate.DeleteFile(tmp);
        }
    }
}
