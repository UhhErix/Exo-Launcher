using System.Buffers.Text;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace ExoLauncher.Services.Account;

internal static class WindowsWebAuthn
{
    private const uint CurrentClientDataVersion = 1;
    private const uint CurrentRpVersion = 1;
    private const uint CurrentUserVersion = 1;
    private const uint CurrentCoseParamVersion = 1;
    private const uint CurrentMakeOptionsVersion = 7;
    private const uint CurrentGetOptionsVersion = 7;
    private const uint AttachmentAny = 0;
    private const uint UserVerificationRequired = 1;
    private const int AlgEs256 = -7;
    private const int AlgRs256 = -257;
    private const string CredentialTypePublicKey = "public-key";
    private const string HashAlgSha256 = "SHA-256";
    private const uint HrCancelled = 0x800704C7;

    internal sealed record Availability(bool Supported, uint ApiVersion, bool PlatformAvailable);

    internal sealed record RegistrationProof(string CredentialId, string ClientDataJson, string AttestationObject);

    internal sealed record AssertionProof(string CredentialId, string ClientDataJson, string AuthenticatorData, string Signature);

    internal sealed record WebAuthnCallResult<T>(bool Ok, T? Value, bool Cancelled, string? Error);

    internal static Availability Probe()
    {
        try
        {
            var version = WebAuthNGetApiVersionNumber();
            var hr = WebAuthNIsUserVerifyingPlatformAuthenticatorAvailable(out var avail);
            return new Availability(version >= 1, version, hr == 0 && avail);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return new Availability(false, 0, false);
        }
    }

    internal static WebAuthnCallResult<RegistrationProof> MakeCredential(
        nint hwnd,
        string rpId,
        string rpName,
        string origin,
        string challenge,
        string userIdBase64Url,
        string userName,
        IReadOnlyList<string> excludeCredentialIds,
        uint timeoutMs = 60_000)
    {
        var userIdBytes = Base64Url.DecodeFromChars(userIdBase64Url);
        var clientJson = JsonSerializer.Serialize(new
        {
            type = "webauthn.create",
            challenge,
            origin,
            crossOrigin = false,
        });
        var clientBytes = Encoding.UTF8.GetBytes(clientJson);

        var pins = new List<GCHandle>();
        var allocs = new List<nint>();
        nint attestationPtr = 0;
        try
        {
            var rpIdPtr = AllocWide(rpId, allocs);
            var rpNamePtr = AllocWide(rpName, allocs);
            var userNamePtr = AllocWide(userName, allocs);
            var pubKeyTypePtr = AllocWide(CredentialTypePublicKey, allocs);
            var hashAlgPtr = AllocWide(HashAlgSha256, allocs);
            var userIdPtr = PinBytes(userIdBytes, pins);
            var clientDataPtr = PinBytes(clientBytes, pins);

            var rpInfo = new WebAuthnRpEntityInfo
            {
                Version = CurrentRpVersion,
                Id = rpIdPtr,
                Name = rpNamePtr,
            };
            var userInfo = new WebAuthnUserEntityInfo
            {
                Version = CurrentUserVersion,
                IdLen = (uint)userIdBytes.Length,
                Id = userIdPtr,
                Name = userNamePtr,
                DisplayName = userNamePtr,
            };

            var coseArray = new[]
            {
                new WebAuthnCoseParam { Version = CurrentCoseParamVersion, CredentialType = pubKeyTypePtr, Alg = AlgEs256 },
                new WebAuthnCoseParam { Version = CurrentCoseParamVersion, CredentialType = pubKeyTypePtr, Alg = AlgRs256 },
            };
            var coseHandle = GCHandle.Alloc(coseArray, GCHandleType.Pinned);
            pins.Add(coseHandle);
            var coseParams = new WebAuthnCoseParams
            {
                Count = (uint)coseArray.Length,
                Params = coseHandle.AddrOfPinnedObject(),
            };

            var clientData = new WebAuthnClientData
            {
                Version = CurrentClientDataVersion,
                ClientDataLen = (uint)clientBytes.Length,
                ClientData = clientDataPtr,
                HashAlgId = hashAlgPtr,
            };

            var excludeList = BuildCredentialList(excludeCredentialIds, pubKeyTypePtr, pins);
            var options = new WebAuthnMakeOptions
            {
                Version = Math.Min(CurrentMakeOptionsVersion, Math.Max(1u, WebAuthNGetApiVersionNumber())),
                TimeoutMs = timeoutMs,
                ExcludeCredentials = excludeList,
                AuthenticatorAttachment = AttachmentAny,
                RequireResidentKey = false,
                UserVerification = UserVerificationRequired,
                PreferResidentKey = true,
            };

            var hr = WebAuthNAuthenticatorMakeCredential(
                hwnd,
                ref rpInfo,
                ref userInfo,
                ref coseParams,
                ref clientData,
                ref options,
                out attestationPtr);

            if (hr != 0 || attestationPtr == 0)
            {
                var cancelled = unchecked((uint)hr) == HrCancelled;
                return new WebAuthnCallResult<RegistrationProof>(false, null, cancelled, cancelled ? "cancelled" : $"webauthn-0x{unchecked((uint)hr):x8}");
            }

            var att = Marshal.PtrToStructure<WebAuthnCredentialAttestation>(attestationPtr);
            var credId = ReadBytes(att.CredentialId, att.CredentialIdLen);
            var attObj = ReadBytes(att.AttestationObject, att.AttestationObjectLen);
            return new WebAuthnCallResult<RegistrationProof>(
                true,
                new RegistrationProof(
                    Base64Url.EncodeToString(credId),
                    Base64Url.EncodeToString(clientBytes),
                    Base64Url.EncodeToString(attObj)),
                false,
                null);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return new WebAuthnCallResult<RegistrationProof>(false, null, false, "unsupported");
        }
        finally
        {
            if (attestationPtr != 0) WebAuthNFreeCredentialAttestation(attestationPtr);
            foreach (var h in pins) h.Free();
            foreach (var ptr in allocs) Marshal.FreeHGlobal(ptr);
        }
    }

    internal static WebAuthnCallResult<AssertionProof> GetAssertion(
        nint hwnd,
        string rpId,
        string origin,
        string challenge,
        IReadOnlyList<string> allowCredentialIds,
        uint timeoutMs = 60_000)
    {
        var clientJson = JsonSerializer.Serialize(new
        {
            type = "webauthn.get",
            challenge,
            origin,
            crossOrigin = false,
        });
        var clientBytes = Encoding.UTF8.GetBytes(clientJson);

        var pins = new List<GCHandle>();
        var allocs = new List<nint>();
        nint assertionPtr = 0;
        try
        {
            var pubKeyTypePtr = AllocWide(CredentialTypePublicKey, allocs);
            var hashAlgPtr = AllocWide(HashAlgSha256, allocs);
            var clientDataPtr = PinBytes(clientBytes, pins);

            var clientData = new WebAuthnClientData
            {
                Version = CurrentClientDataVersion,
                ClientDataLen = (uint)clientBytes.Length,
                ClientData = clientDataPtr,
                HashAlgId = hashAlgPtr,
            };

            var allowList = BuildCredentialList(allowCredentialIds, pubKeyTypePtr, pins);
            var options = new WebAuthnGetOptions
            {
                Version = Math.Min(CurrentGetOptionsVersion, Math.Max(1u, WebAuthNGetApiVersionNumber())),
                TimeoutMs = timeoutMs,
                AllowCredentials = allowList,
                AuthenticatorAttachment = AttachmentAny,
                UserVerification = UserVerificationRequired,
            };

            var hr = WebAuthNAuthenticatorGetAssertion(
                hwnd,
                rpId,
                ref clientData,
                ref options,
                out assertionPtr);

            if (hr != 0 || assertionPtr == 0)
            {
                var cancelled = unchecked((uint)hr) == HrCancelled;
                return new WebAuthnCallResult<AssertionProof>(false, null, cancelled, cancelled ? "cancelled" : $"webauthn-0x{unchecked((uint)hr):x8}");
            }

            var assertion = Marshal.PtrToStructure<WebAuthnAssertion>(assertionPtr);
            var credId = ReadBytes(assertion.Credential.Id, assertion.Credential.IdLen);
            var authData = ReadBytes(assertion.AuthenticatorData, assertion.AuthenticatorDataLen);
            var sig = ReadBytes(assertion.Signature, assertion.SignatureLen);
            return new WebAuthnCallResult<AssertionProof>(
                true,
                new AssertionProof(
                    Base64Url.EncodeToString(credId),
                    Base64Url.EncodeToString(clientBytes),
                    Base64Url.EncodeToString(authData),
                    Base64Url.EncodeToString(sig)),
                false,
                null);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return new WebAuthnCallResult<AssertionProof>(false, null, false, "unsupported");
        }
        finally
        {
            if (assertionPtr != 0) WebAuthNFreeAssertion(assertionPtr);
            foreach (var h in pins) h.Free();
            foreach (var ptr in allocs) Marshal.FreeHGlobal(ptr);
        }
    }

    private static nint AllocWide(string value, List<nint> allocs)
    {
        var ptr = Marshal.StringToHGlobalUni(value);
        allocs.Add(ptr);
        return ptr;
    }

    private static nint PinBytes(byte[] bytes, List<GCHandle> pins)
    {
        if (bytes.Length == 0) return 0;
        var h = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        pins.Add(h);
        return h.AddrOfPinnedObject();
    }

    private static WebAuthnCredentials BuildCredentialList(IReadOnlyList<string> ids, nint pubKeyTypePtr, List<GCHandle> pins)
    {
        if (ids.Count == 0) return default;
        var items = new WebAuthnCredential[ids.Count];
        for (var i = 0; i < ids.Count; i++)
        {
            var raw = Base64Url.DecodeFromChars(ids[i]);
            items[i] = new WebAuthnCredential
            {
                Version = 1,
                IdLen = (uint)raw.Length,
                Id = PinBytes(raw, pins),
                CredentialType = pubKeyTypePtr,
            };
        }
        var handle = GCHandle.Alloc(items, GCHandleType.Pinned);
        pins.Add(handle);
        return new WebAuthnCredentials { Count = (uint)items.Length, Credentials = handle.AddrOfPinnedObject() };
    }

    private static byte[] ReadBytes(nint ptr, uint len)
    {
        if (ptr == 0 || len == 0) return [];
        var buf = new byte[len];
        Marshal.Copy(ptr, buf, 0, (int)len);
        return buf;
    }

    [DllImport("webauthn.dll", ExactSpelling = true)]
    private static extern uint WebAuthNGetApiVersionNumber();

    [DllImport("webauthn.dll", ExactSpelling = true)]
    private static extern int WebAuthNIsUserVerifyingPlatformAuthenticatorAvailable([MarshalAs(UnmanagedType.Bool)] out bool isAvailable);

    [DllImport("webauthn.dll", ExactSpelling = true)]
    private static extern int WebAuthNAuthenticatorMakeCredential(
        nint hWnd,
        ref WebAuthnRpEntityInfo pRpInformation,
        ref WebAuthnUserEntityInfo pUserInformation,
        ref WebAuthnCoseParams pPubKeyCredParams,
        ref WebAuthnClientData pWebAuthNClientData,
        ref WebAuthnMakeOptions pWebAuthNMakeCredentialOptions,
        out nint ppWebAuthNCredentialAttestation);

    [DllImport("webauthn.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int WebAuthNAuthenticatorGetAssertion(
        nint hWnd,
        string pwszRpId,
        ref WebAuthnClientData pWebAuthNClientData,
        ref WebAuthnGetOptions pWebAuthNGetAssertionOptions,
        out nint ppWebAuthNAssertion);

    [DllImport("webauthn.dll", ExactSpelling = true)]
    private static extern void WebAuthNFreeCredentialAttestation(nint pWebAuthNCredentialAttestation);

    [DllImport("webauthn.dll", ExactSpelling = true)]
    private static extern void WebAuthNFreeAssertion(nint pWebAuthNAssertion);

    [StructLayout(LayoutKind.Sequential)]
    private struct WebAuthnRpEntityInfo
    {
        public uint Version;
        public nint Id;
        public nint Name;
        public nint Icon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WebAuthnUserEntityInfo
    {
        public uint Version;
        public uint IdLen;
        public nint Id;
        public nint Name;
        public nint Icon;
        public nint DisplayName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WebAuthnCoseParam
    {
        public uint Version;
        public nint CredentialType;
        public int Alg;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WebAuthnCoseParams
    {
        public uint Count;
        public nint Params;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WebAuthnClientData
    {
        public uint Version;
        public uint ClientDataLen;
        public nint ClientData;
        public nint HashAlgId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WebAuthnCredential
    {
        public uint Version;
        public uint IdLen;
        public nint Id;
        public nint CredentialType;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WebAuthnCredentials
    {
        public uint Count;
        public nint Credentials;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WebAuthnExtensions
    {
        public uint Count;
        public nint Extensions;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WebAuthnMakeOptions
    {
        public uint Version;
        public uint TimeoutMs;
        public WebAuthnCredentials ExcludeCredentials;
        public WebAuthnExtensions Extensions;
        public uint AuthenticatorAttachment;
        [MarshalAs(UnmanagedType.Bool)]
        public bool RequireResidentKey;
        public uint UserVerification;
        public uint AttestationConveyancePreference;
        public uint Flags;
        public nint CancellationId;
        public nint ExcludeCredentialList;
        public uint EnterpriseAttestation;
        public uint LargeBlobSupport;
        [MarshalAs(UnmanagedType.Bool)]
        public bool PreferResidentKey;
        [MarshalAs(UnmanagedType.Bool)]
        public bool BrowserInPrivateMode;
        [MarshalAs(UnmanagedType.Bool)]
        public bool EnablePrf;
        public nint LinkedDevice;
        public uint JsonExtLen;
        public nint JsonExt;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WebAuthnGetOptions
    {
        public uint Version;
        public uint TimeoutMs;
        public WebAuthnCredentials AllowCredentials;
        public WebAuthnExtensions Extensions;
        public uint AuthenticatorAttachment;
        public uint UserVerification;
        public uint Flags;
        public nint U2fAppId;
        public nint IsU2fAppIdUsed;
        public nint CancellationId;
        public nint AllowCredentialList;
        public uint CredLargeBlobOperation;
        public uint CredLargeBlobLen;
        public nint CredLargeBlob;
        public nint HmacSecretSaltValues;
        [MarshalAs(UnmanagedType.Bool)]
        public bool BrowserInPrivateMode;
        public nint LinkedDevice;
        [MarshalAs(UnmanagedType.Bool)]
        public bool AutoFill;
        public uint JsonExtLen;
        public nint JsonExt;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WebAuthnCredentialAttestation
    {
        public uint Version;
        public nint FormatType;
        public uint AuthenticatorDataLen;
        public nint AuthenticatorData;
        public uint AttestationLen;
        public nint Attestation;
        public uint AttestationDecodeType;
        public nint AttestationDecode;
        public uint AttestationObjectLen;
        public nint AttestationObject;
        public uint CredentialIdLen;
        public nint CredentialId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WebAuthnAssertion
    {
        public uint Version;
        public uint AuthenticatorDataLen;
        public nint AuthenticatorData;
        public uint SignatureLen;
        public nint Signature;
        public WebAuthnCredential Credential;
        public uint UserIdLen;
        public nint UserId;
    }
}
