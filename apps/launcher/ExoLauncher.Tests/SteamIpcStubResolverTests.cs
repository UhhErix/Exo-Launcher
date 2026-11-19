using System.Buffers.Binary;
using System.Text;
using ExoLauncher.Adapters;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways Steam IPC vtable stub resolution could fail when Valve rebuilds steamclient64.dll:
/// - low bytes of hardcoded 32-bit IPC hashes shifting across Steam builds (e.g. SetDlcEnabled 0x0ec3c8a6 -> 0x0ec3c8a2);
/// - failing to decode x64 RIP-relative LEA [rip+disp32] instructions using RAX..R15 (REX 0x48/0x4C) Past 0xC0 stub bytes;
/// - matching the wrong overload of GetCurrentLanguage when two stubs carry "GetCurrentLanguage" + "IClientAppManager";
/// - crossing 0xCC padding into an adjacent function or matching out-of-module addresses;
/// - ignoring 24-bit hash prefix fallback when strings are absent.
/// </summary>
public sealed class SteamIpcStubResolverTests
{
    private const long ModuleBase = 0x1_0000_0000L;

    private sealed class FakeModule
    {
        private readonly byte[] _bytes = new byte[0x4000];
        private int _stringCursor = 0x3000;

        public long Base => ModuleBase;
        public long End => ModuleBase + _bytes.Length;

        public long AddString(string text)
        {
            var raw = Encoding.ASCII.GetBytes(text);
            var offset = _stringCursor;
            Array.Copy(raw, 0, _bytes, offset, raw.Length);
            _bytes[offset + raw.Length] = 0;
            _stringCursor += raw.Length + 1;
            return Base + offset;
        }

        public void WriteStub(int stubOffset, uint hashImm32, long? methodStrVa, long? ifaceStrVa, byte rex = 0x48, int leaOffset = 0xE0)
        {
            // Fill prologue padding before leaOffset, include mov dword ptr [rbp+0x20], hashImm32 (C7 45 20 <imm32>).
            Array.Fill(_bytes, (byte)0x90, stubOffset, 0x160);
            _bytes[stubOffset + 0x70] = 0xC7;
            _bytes[stubOffset + 0x71] = 0x45;
            _bytes[stubOffset + 0x72] = 0x20;
            BinaryPrimitives.WriteUInt32LittleEndian(_bytes.AsSpan(stubOffset + 0x73, 4), hashImm32);

            if (methodStrVa is { } mVa)
                WriteLeaRip(stubOffset + leaOffset, rex, modrm: 0x05, targetVa: mVa);
            if (ifaceStrVa is { } iVa)
                WriteLeaRip(stubOffset + leaOffset + 11, (byte)(rex | 0x04), modrm: 0x0D, targetVa: iVa);

            // End stub with ret + CC padding.
            _bytes[stubOffset + 0x160] = 0xC3;
            Array.Fill(_bytes, (byte)0xCC, stubOffset + 0x161, 15);
        }

        public void Fill(int offset, byte value, int count) => Array.Fill(_bytes, value, offset, count);

        public void WriteLeaRip(int offset, byte rex, byte modrm, long targetVa)
        {
            var instrVa = Base + offset;
            var disp = checked((int)(targetVa - (instrVa + 7)));
            _bytes[offset] = rex;
            _bytes[offset + 1] = 0x8D;
            _bytes[offset + 2] = modrm;
            BinaryPrimitives.WriteInt32LittleEndian(_bytes.AsSpan(offset + 3, 4), disp);
        }

        public bool Read(long address, Span<byte> dest)
        {
            var off = address - Base;
            if (off < 0 || off + dest.Length > _bytes.Length) return false;
            _bytes.AsSpan((int)off, dest.Length).CopyTo(dest);
            return true;
        }
    }

    [Fact]
    public void Resolves_methods_when_low_hash_byte_shifts_and_lea_sits_past_0xC0()
    {
        var mod = new FakeModule();
        var sIface = mod.AddString("IClientAppManager");
        var sBIsDlc = mod.AddString("BIsDlcEnabled");
        var sSetDlc = mod.AddString("SetDlcEnabled");
        var sAutoUp = mod.AddString("SetAppAutoUpdateBehavior");

        // All hashes have shifted low byte vs original 32-bit values (e.g. 0x0ec3c8a6 -> 0x0ec3c8a2).
        mod.WriteStub(0x0200, 0x143d4559u, sBIsDlc, sIface, rex: 0x48, leaOffset: 0xC2);
        mod.WriteStub(0x0400, 0x0ec3c8a2u, sSetDlc, sIface, rex: 0x4C, leaOffset: 0xE3);
        mod.WriteStub(0x0600, 0xc5d6e599u, sAutoUp, sIface, rex: 0x48, leaOffset: 0xC0);

        long[] vtable = [mod.Base + 0x0200, mod.Base + 0x0400, mod.Base + 0x0600, 0];

        Assert.Equal(mod.Base + 0x0200, SteamIpcStubResolver.FindSlot(vtable, mod.Base, mod.End, mod.Read, SteamIpcStubResolver.BIsDlcEnabled));
        Assert.Equal(mod.Base + 0x0400, SteamIpcStubResolver.FindSlot(vtable, mod.Base, mod.End, mod.Read, SteamIpcStubResolver.SetDlcEnabled));
        Assert.Equal(mod.Base + 0x0600, SteamIpcStubResolver.FindSlot(vtable, mod.Base, mod.End, mod.Read, SteamIpcStubResolver.SetAutoUpdate));
    }

    [Fact]
    public void Disambiguates_overloaded_GetCurrentLanguage_using_24_bit_hash_prefix()
    {
        var mod = new FakeModule();
        var sIface = mod.AddString("IClientAppManager");
        var sLang = mod.AddString("GetCurrentLanguage");

        // Second overload carries a completely different IPC hash, while our target carries shifted 0x600aa908 (prefix 0x600aa900).
        mod.WriteStub(0x0200, 0x600aa908u, sLang, sIface, leaOffset: 0xC7);
        mod.WriteStub(0x0400, 0x379d0a5au, sLang, sIface, leaOffset: 0xC7);

        long[] vtable = [mod.Base + 0x0400, mod.Base + 0x0200, 0];

        var slot = SteamIpcStubResolver.FindSlot(vtable, mod.Base, mod.End, mod.Read, SteamIpcStubResolver.GetCurrentLanguage);
        Assert.Equal(mod.Base + 0x0200, slot);
    }

    [Fact]
    public void Falls_back_to_24_bit_hash_prefix_when_debug_strings_are_absent_and_rejects_duplicates()
    {
        var mod = new FakeModule();
        // Stripped stub with no LEA strings, only shifted 0x0ec3c8ff (shares 0x0ec3c800 prefix).
        mod.WriteStub(0x0200, 0x0ec3c8ffu, methodStrVa: null, ifaceStrVa: null);
        long[] singleVtable = [mod.Base + 0x0200, 0];
        Assert.Equal(mod.Base + 0x0200, SteamIpcStubResolver.FindSlot(singleVtable, mod.Base, mod.End, mod.Read, SteamIpcStubResolver.SetDlcEnabled));

        // Ambiguous duplicate matching stubs must return 0 rather than guessing a wrong vtable slot.
        mod.WriteStub(0x0400, 0x0ec3c8a2u, methodStrVa: null, ifaceStrVa: null);
        long[] dupVtable = [mod.Base + 0x0200, mod.Base + 0x0400, 0];
        Assert.Equal(0, SteamIpcStubResolver.FindSlot(dupVtable, mod.Base, mod.End, mod.Read, SteamIpcStubResolver.SetDlcEnabled));
    }

    [Fact]
    public void Does_not_bleed_across_int3_padding_into_neighbor_stub()
    {
        var mod = new FakeModule();
        var sIface = mod.AddString("IClientAppManager");
        var sSetDlc = mod.AddString("SetDlcEnabled");
        var sMove = mod.AddString("MoveApp");

        // Short unrelated stub ends at +0x60 followed by CC padding; neighbor stub sits at +0x90.
        mod.WriteStub(0x0200, 0x11111111u, sMove, sIface, leaOffset: 0x30);
        mod.Fill(0x0260, 0xC3, 1);
        mod.Fill(0x0261, 0xCC, 16);
        mod.WriteLeaRip(0x0290, 0x48, 0x05, sSetDlc);
        mod.WriteLeaRip(0x029B, 0x4C, 0x0D, sIface);

        long[] vtable = [mod.Base + 0x0200, 0];
        Assert.Equal(mod.Base + 0x0200, SteamIpcStubResolver.FindByName(vtable, mod.Base, mod.End, mod.Read, SteamIpcStubResolver.MoveApp));
        Assert.Equal(0, SteamIpcStubResolver.FindByName(vtable, mod.Base, mod.End, mod.Read, SteamIpcStubResolver.SetDlcEnabled));
    }

    [Fact]
    public void Specs_cover_all_20_methods_without_duplicate_identities()
    {
        Assert.Equal(20, SteamIpcStubResolver.AllSpecs.Length);
        var distinct = SteamIpcStubResolver.AllSpecs.Select(s => (s.InterfaceName, s.MethodName, s.HashPrefix24)).Distinct().Count();
        Assert.Equal(20, distinct);
        Assert.True(SteamIpcStubResolver.GetCurrentLanguage.RequireHashPrefix);
    }
}
