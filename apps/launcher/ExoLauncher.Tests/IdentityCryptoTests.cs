using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Exo.Identity;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways Exo identity's building blocks could be wrong while every Exo app still talks to itself happily, so only
/// the Worker or another implementation would ever notice:
/// - HPKE that seals and opens its own messages but isn't RFC 9180 (a label, the suite id, the DH byte order: CNG's raw
///   secret is little-endian), so another implementation can't open what Launcher sends;
/// - a P1363 signature that verifies after a byte of the message, signature or key changed, or a key off the curve
///   that's accepted; a high-s signature made or taken (its low twin would be a second valid copy anyone can make);
/// - base32 or base64url that reads one value from two spellings, or base32 that refuses a code typed with 0/1/8,
///   spaces or hyphens;
/// - a scalar retry nobody runs: a candidate of n or more taken, or the next counter not tried;
/// - canonical JSON that isn't RFC 8785 for the subset (key order, escapes, integers), or that writes what the subset
///   refuses (null, floats, -0, a repeated key, a lone surrogate, a non-ASCII key), so two apps sign different bytes.
/// </summary>
public sealed class IdentityCryptoTests
{
    // RFC 9180, A.3.1: DHKEM(P-256, HKDF-SHA256), HKDF-SHA256, AES-128-GCM, base mode.
    private const string Info = "4f6465206f6e2061204772656369616e2055726e";
    private const string IkmE = "4270e54ffd08d79d5928020af4686d8f6b7d35dbe470265f1f5aa22816ce860e";
    private const string SkEm = "4995788ef4b9d6132b249ce59a77281493eb39af373d236a1fe415cb0c2d7beb";
    private const string PkEm = "04a92719c6195d5085104f469a8b9814d5838ff72b60501e2c4466e5e67b325ac98536d7b61a1af4b78e5b7f951c0900be863c403ce65c9bfcb9382657222d18c4";
    private const string IkmR = "668b37171f1072f3cf12ea8a236a45df23fc13b82af3609ad1e354f6ef817550";
    private const string SkRm = "f3ce7fdae57e1a310d87f1ebbde6f328be0a99cdbcadf4d6589cf29de4b8ffd2";
    private const string PkRm = "04fe8c19ce0905191ebc298a9245792531f26f0cece2460639e8bc39cb7f706a826a779b4cf969b8a0e539c7f62fb3d30ad6aa8f80e30f1d128aafd68a2ce72ea0";
    private const string Key = "868c066ef58aae6dc589b6cfdd18f97e";
    private const string BaseNonce = "4e0bc5018beba4bf004cca59";
    private const string ExporterSecret = "14ad94" + "af484a" + "7ad3ef" + "40e9f3" + "be99ec" + "c6fa90" + "36df9d" + "492054" + "8424df" + "127ee0" + "d99f";
    private const string Plaintext = "4265617574792069732074727574682c20747275746820626561757479";

    private static readonly Dictionary<int, string> Ciphertexts = new()
    {
        [0] = "5ad590bb8baa577f8619db35a36311226a896e7342a6d836d8b7bcd2f20b6c7f9076ac232e3ab2523f39513434",
        [1] = "fa6f037b47fc21826b610172ca9637e82d6e5801eb31cbd3748271affd4ecb06646e0329cbdf3c3cd655b28e82",
        [2] = "895cabfac50ce6c6eb02ffe6c048bf53b7f7be9a91fc559402cbc5b8dcaeb52b2ccc93e466c28fb55fed7a7fec",
        [4] = "8787491ee8df99bc99a246c4b3216d3d57ab5076e18fa27133f520703bc70ec999dd36ce042e44f0c3169a6a8f",
        [255] = "2ad71c85bf3f45c6eca301426289854b31448bcf8a8ccb1deef3ebd87f60848aa53c538c30a4dac71d619ee2cd",
        [256] = "10f179686aa2caec1758c8e554513f16472bd0a11e2a907dde0b212cbe87d74f367f8ffe5e41cd3e9962a6afb2",
    };

    private static readonly (string Context, string Value)[] Exports =
    [
        ("", "5e9bc3d236e1911d95e65b576a8a86d478fb827e8bdfe77b741b289890490d4d"),
        ("00", "6cff87658931bda83dc857e6353efe4987a201b849658d9b047aab4cf216e796"),
        ("54657374436f6e74657874", "d8f1ea7942adbba7412c6d431c62d01371ea476b823eb697e1f6e6cae1dab85a"),
    ];

    [Fact]
    public void HpkeIsRfc9180ForTheP256Suite()
    {
        var skE = Hpke.DeriveKeyPair(Convert.FromHexString(IkmE));
        var skR = Hpke.DeriveKeyPair(Convert.FromHexString(IkmR));
        Assert.Equal(SkEm, Convert.ToHexStringLower(skE));
        Assert.Equal(SkRm, Convert.ToHexStringLower(skR));
        using var ephemeral = P256.Agreement(skE);
        using var recipient = new EcdhDecrypter(P256.Agreement(skR));
        Assert.Equal(PkEm, Convert.ToHexStringLower(P256.PublicKey(ephemeral)));
        Assert.Equal(PkRm, Convert.ToHexStringLower(recipient.PublicKey));

        var (enc, sender) = Hpke.SetupSender(recipient.PublicKey, Convert.FromHexString(Info), ephemeral)!.Value;
        var receiver = Hpke.SetupRecipient(recipient, enc, Convert.FromHexString(Info))!;
        Assert.Equal(PkEm, Convert.ToHexStringLower(enc));
        foreach (var context in new[] { sender, receiver })
        {
            Assert.Equal(Key, Convert.ToHexStringLower(context.Key));
            Assert.Equal(BaseNonce, Convert.ToHexStringLower(context.BaseNonce));
            Assert.Equal(ExporterSecret, Convert.ToHexStringLower(context.ExporterSecret));
            foreach (var (exporterContext, value) in Exports)
                Assert.Equal(value, Convert.ToHexStringLower(context.Export(Convert.FromHexString(exporterContext), 32)));
        }

        var plain = Convert.FromHexString(Plaintext);
        for (var seq = 0; seq <= 256; seq++)
        {
            var aad = Encoding.ASCII.GetBytes("Count-" + seq);
            var ct = sender.Seal(aad, plain);
            if (Ciphertexts.TryGetValue(seq, out var expected)) Assert.Equal(expected, Convert.ToHexStringLower(ct));
            Assert.Equal(plain, receiver.Open(aad, ct));
        }
    }

    [Fact]
    public void HpkeRefusesAChangedMessageAndABadKey()
    {
        using var recipient = new EcdhDecrypter(ECDiffieHellman.Create(P256.Curve));
        var (enc, sender) = Hpke.SetupSender(recipient.PublicKey, "info"u8)!.Value;
        var ct = sender.Seal("aad"u8, "hello"u8);
        Assert.Null(Hpke.SetupRecipient(recipient, enc, "info"u8)!.Open("aax"u8, ct));
        ct[0] ^= 1;
        Assert.Null(Hpke.SetupRecipient(recipient, enc, "info"u8)!.Open("aad"u8, ct));
        ct[0] ^= 1;
        Assert.Null(Hpke.SetupRecipient(recipient, enc, "other"u8)!.Open("aad"u8, ct));
        Assert.Equal("hello"u8.ToArray(), Hpke.SetupRecipient(recipient, enc, "info"u8)!.Open("aad"u8, ct));

        var offCurve = (byte[])recipient.PublicKey.Clone();
        offCurve[^1] ^= 1;
        Assert.Null(Hpke.SetupSender(offCurve, "info"u8));
        Assert.Null(Hpke.SetupRecipient(recipient, offCurve, "info"u8));
        Assert.Null(Hpke.SetupRecipient(recipient, enc[..64], "info"u8));
    }

    [Fact]
    public void ASignatureVerifiesOnlyForItsKeyAndMessage()
    {
        using var signer = new EcdsaSigner(ECDsa.Create(P256.Curve));
        var message = "exo-box-auth/1 something"u8.ToArray();
        var signature = signer.Sign(message);
        Assert.Equal(P256.SignatureBytes, signature.Length);
        Assert.True(P256.Verify(signer.PublicKey, message, signature));

        // The P1363 form WebCrypto checks: r || s, each 32 bytes, as .NET makes and checks it outright.
        using var check = ECDsa.Create(P256.Curve);
        check.ImportParameters(new ECParameters { Curve = P256.Curve, Q = new ECPoint { X = signer.PublicKey[1..33], Y = signer.PublicKey[33..] } });
        Assert.True(check.VerifyData(message, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

        for (var i = 0; i < message.Length; i += 7)
        {
            var changed = (byte[])message.Clone();
            changed[i] ^= 0x20;
            Assert.False(P256.Verify(signer.PublicKey, changed, signature));
        }
        for (var i = 0; i < signature.Length; i += 5)
        {
            var changed = (byte[])signature.Clone();
            changed[i] ^= 1;
            Assert.False(P256.Verify(signer.PublicKey, message, changed));
        }
        using var other = new EcdsaSigner(ECDsa.Create(P256.Curve));
        Assert.False(P256.Verify(other.PublicKey, message, signature));
        var offCurve = (byte[])signer.PublicKey.Clone();
        offCurve[40] ^= 1;
        Assert.False(P256.Verify(offCurve, message, signature));
        Assert.False(P256.Verify(signer.PublicKey[1..], message, signature));
        Assert.False(P256.Verify(signer.PublicKey, message, signature[..63]));
        Assert.False(P256.Verify(signer.PublicKey, message, new byte[64]));
    }

    [Fact]
    public void EverySignatureIsLowSAndTheHighTwinIsRefused()
    {
        using var signer = new EcdsaSigner(ECDsa.Create(P256.Curve));
        var message = "exo-change/1 {}"u8.ToArray();
        using var check = ECDsa.Create(new ECParameters { Curve = P256.Curve, Q = new ECPoint { X = signer.PublicKey[1..33], Y = signer.PublicKey[33..] } });
        for (var i = 0; i < 64; i++)
        {
            // CNG picks s or n - s at random; about half of 64 would be high without LowS.
            var signature = signer.Sign(message);
            Assert.True(P256.IsLowS(signature));
            var twin = IdentityVectorsTests.Twin(signature);
            Assert.False(P256.IsLowS(twin));
            // The twin is a real signature (anyone can make it from the first), which is why only one of the two is taken.
            Assert.True(check.VerifyData(message, twin, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
            Assert.False(P256.Verify(signer.PublicKey, message, twin));
            Assert.Equal(signature, P256.LowS(twin));
            Assert.Same(signature, P256.LowS(signature));
        }
    }

    [Fact]
    public void Base64UrlHasOneSpelling()
    {
        Assert.Equal("f"u8.ToArray(), CanonicalJson.Bytes("Zg"));
        Assert.Equal(Array.Empty<byte>(), CanonicalJson.Bytes(""));
        foreach (var other in new[] { "Zg==", "Zg=", "Zh", " Zg", "Z g", "Zg\n", "Z+", "Z/", "Z" })
            Assert.Null(CanonicalJson.Bytes(other));
    }

    [Fact]
    public void AScalarFromKeyMaterialIsInRangeAndTheSameEveryTime()
    {
        var prk = SHA256.HashData("exo"u8);
        var a = P256.DeriveScalar(prk, "box"u8);
        Assert.Equal(a, P256.DeriveScalar(prk, "box"u8));
        Assert.NotEqual(a, P256.DeriveScalar(prk, "sign"u8));
        Assert.True(P256.InRange(a));
        // .NET (and CNG under it) makes the public point from the scalar alone, as BCryptImportKeyPair does.
        using var key = P256.Signer(a);
        Assert.Equal(a, key.ExportParameters(true).D);
        Assert.False(P256.InRange(new byte[32]));
        // A key whose first candidate is n or more: the next counter's is taken.
        var retry = Convert.FromHexString(IdentityVectorsTests.RetrySyncKey);
        var first = new byte[32];
        HKDF.Expand(HashAlgorithmName.SHA256, retry, first, [.. "exo sync box v1"u8, 0]);
        Assert.False(P256.InRange(first));
        var second = new byte[32];
        HKDF.Expand(HashAlgorithmName.SHA256, retry, second, [.. "exo sync box v1"u8, 1]);
        Assert.Equal(second, Mailbox.BoxScalar(retry));
        Assert.False(P256.InRange(Convert.FromHexString("FFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551")));
        Assert.True(P256.InRange(Convert.FromHexString("FFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632550")));
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("66", "MY")]
    [InlineData("666f6f626172", "MZXW6YTBOI")]
    [InlineData("00112233445566778899aabbccddeeff", "AAISEM2EKVTHPCEZVK54ZXPO74")]
    public void Base32IsRfc4648WithoutPadding(string hex, string text)
    {
        Assert.Equal(text, Base32.Encode(Convert.FromHexString(hex)));
        Assert.Equal(Convert.FromHexString(hex), Base32.Decode(text));
        Assert.Equal(Convert.FromHexString(hex), Base32.Decode(text.ToLowerInvariant()));
    }

    [Fact]
    public void Base32ReadsAHandCopiedCodeAndNothingAmbiguous()
    {
        var code = Convert.FromHexString("00112233445566778899aabbccddeeff");
        Assert.Equal(code, Base32.Decode("aaise-m2ekv thpce-zvk54-zxpo74"));
        // 0, 1 and 8 aren't base32: they're read as the O, I and B they look like.
        Assert.Equal(Base32.Decode("MZXW6YTBOI"), Base32.Decode("MZXW6YTB0I"));
        Assert.Equal(Base32.Decode("ABIQ"), Base32.Decode("A81Q"));
        Assert.Null(Base32.Decode("MZXW6YTBO9"));
        Assert.Null(Base32.Decode("M"));
        // The spare bits must be zero, so one value has one spelling.
        Assert.Null(Base32.Decode("MZ"));
        Assert.Null(Base32.Decode("AAISEM2EKVTHPCEZVK54ZXPO75"));
    }

    [Theory]
    [InlineData("{\"b\":1,\"a\":[true,false,\"x\"]}", "{\"a\":[true,false,\"x\"],\"b\":1}")]
    [InlineData("{ \"z\" : { \"y\" : 2 , \"x\" : -3 } }", "{\"z\":{\"x\":-3,\"y\":2}}")]
    [InlineData("{\"s\":\"\\u00e9 \\u20ac \\ud83d\\ude00 / \\u0041\"}", "{\"s\":\"é € 😀 / A\"}")]
    [InlineData("{\"s\":\"q\\\" b\\\\ \\b\\f\\n\\r\\t \\u0001 \\u001f \\u007f\"}", "{\"s\":\"q\\\" b\\\\ \\b\\f\\n\\r\\t \\u0001 \\u001f \u007f\"}")]
    [InlineData("{\"n\":9007199254740991,\"m\":-9007199254740991,\"o\":0}", "{\"m\":-9007199254740991,\"n\":9007199254740991,\"o\":0}")]
    [InlineData("{\"A\":1,\"_\":2,\"a\":3,\"B\":4}", "{\"A\":1,\"B\":4,\"_\":2,\"a\":3}")]
    public void CanonicalJsonIsRfc8785ForTheSubset(string input, string canonical)
    {
        Assert.Equal(canonical, Encoding.UTF8.GetString(CanonicalJson.Serialize(CanonicalJson.ParseObject(Encoding.UTF8.GetBytes(input))!)));
    }

    [Theory]
    [InlineData("{\"a\":null}")]
    [InlineData("{\"a\":1.5}")]
    [InlineData("{\"a\":1.0}")]
    [InlineData("{\"a\":1e2}")]
    [InlineData("{\"a\":-0}")]
    [InlineData("{\"a\":9007199254740992}")]
    [InlineData("{\"é\":1}")]
    [InlineData("{\"a\":\"\\ud800\"}")]
    [InlineData("{\"a\":\"\\udc00x\"}")]
    public void CanonicalJsonRefusesWhatTheSubsetLeavesOut(string input)
    {
        var parsed = CanonicalJson.ParseObject(Encoding.UTF8.GetBytes(input));
        Assert.NotNull(parsed);
        Assert.Throws<FormatException>(() => CanonicalJson.Serialize(parsed));
    }

    [Fact]
    public void CanonicalJsonRefusesARepeatedKeyAndWhatIsntAnObject()
    {
        Assert.Null(CanonicalJson.ParseObject("{\"a\":1,\"a\":2}"u8));
        Assert.Null(CanonicalJson.ParseObject("[1]"u8));
        Assert.Null(CanonicalJson.ParseObject("{\"a\":"u8));
        // Built in code: an int and a long are the same integer.
        Assert.Equal("{\"a\":1,\"b\":2}", Encoding.UTF8.GetString(CanonicalJson.Serialize(new JsonObject { ["b"] = 2L, ["a"] = 1 })));
    }

    [Fact]
    public void ASignedRecordVerifiesUnderItsLabelOnly()
    {
        using var signer = new EcdsaSigner(ECDsa.Create(P256.Curve));
        var record = CanonicalJson.Sign(new JsonObject { ["v"] = 1, ["name"] = "Desk PC" }, "exo-device/1", signer);
        Assert.True(CanonicalJson.Verify(record, "exo-device/1", signer.PublicKey));
        Assert.False(CanonicalJson.Verify(record, "exo-change/1", signer.PublicKey));
        // The same record re-read from its text, keys in another order: still the same bytes signed.
        var reread = CanonicalJson.ParseObject(Encoding.UTF8.GetBytes("{\"sig\":\"" + record["sig"] + "\",\"name\":\"Desk PC\",\"v\":1}"))!;
        Assert.True(CanonicalJson.Verify(reread, "exo-device/1", signer.PublicKey));
        record["name"] = "Desk PC 2";
        Assert.False(CanonicalJson.Verify(record, "exo-device/1", signer.PublicKey));
        record["name"] = "Desk PC";
        record["sig"] = "not base64url!";
        Assert.False(CanonicalJson.Verify(record, "exo-device/1", signer.PublicKey));
        record.Remove("sig");
        Assert.False(CanonicalJson.Verify(record, "exo-device/1", signer.PublicKey));
    }
}
