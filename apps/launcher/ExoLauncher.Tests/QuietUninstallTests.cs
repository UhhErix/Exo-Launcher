using System.Text;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Which installer made a program, and the quiet command a confirmed run uses, against made-up Apps &amp; features entries
/// (never this PC's registry). Ways it could fail: an installer is told for another and given a switch it doesn't know (an
/// Inno switch to an NSIS uninstaller opens its window anyway, an NSIS /S to an unknown program may mean something else); an
/// MSI is run with /qn, under which a per-machine product can't ask for rights and fails; the vendor's own quiet command is
/// passed over, or one Exo wouldn't start is trusted; a switch already there is added twice; a program with no known switch
/// is run with a guessed one instead of its own window; a quiet run is taken without the confirmed pass.
/// </summary>
public sealed class QuietUninstallTests
{
    private static QuietUninstall.Kind None(string file) => QuietUninstall.Kind.Unknown;

    private static QuietUninstall.Entry Entry(string key, string? uninstall, string? quiet = null, string? product = null, bool bundle = false) =>
        new(key, uninstall, quiet, product, bundle);

    [Fact]
    public void InnoSetupIsToldByItsKeyOrItsUninstallerAndRunsVerySilent()
    {
        var byKey = Entry("Acme Paint_is1", "\"C:\\Program Files\\Acme Paint\\uninstall.exe\"");
        var byName = Entry("AcmePaint", "\"C:\\Program Files\\Acme Paint\\unins001.exe\"");

        Assert.Equal(QuietUninstall.Kind.Inno, QuietUninstall.Detect(byKey, None));
        Assert.Equal(QuietUninstall.Kind.Inno, QuietUninstall.Detect(byName, None));
        Assert.Equal("\"C:\\Program Files\\Acme Paint\\unins001.exe\" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART", QuietUninstall.Command(byName, None));
    }

    [Fact]
    public void NsisIsToldOnlyByItsOwnFileAndRunsWithS()
    {
        var entry = Entry("AcmeTool", "\"C:\\Program Files\\Acme Tool\\uninst.exe\"");

        Assert.Equal("\"C:\\Program Files\\Acme Tool\\uninst.exe\" /S", QuietUninstall.Command(entry, _ => QuietUninstall.Kind.Nsis));
        // The same file name without the NSIS marker: no switch is guessed, its own window runs.
        Assert.Null(QuietUninstall.Command(entry, None));
    }

    [Fact]
    public void WindowsInstallerRunsPassiveNeverQn()
    {
        const string Product = "{12345678-90AB-CDEF-1234-567890ABCDEF}";
        var command = QuietUninstall.Command(Entry(Product, $"MsiExec.exe /I{Product}", product: Product), None);

        Assert.Equal($"MsiExec.exe /X{Product} /passive /norestart", command);
        Assert.DoesNotContain("/qn", command, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheVendorsOwnQuietCommandComesFirstWhenExoWouldStartIt()
    {
        var vendor = Entry("Acme Paint_is1", "\"C:\\Acme\\unins000.exe\"", quiet: "\"C:\\Acme\\unins000.exe\" /SILENT");
        Assert.Equal(QuietUninstall.Kind.Vendor, QuietUninstall.Detect(vendor, None));
        Assert.Equal("\"C:\\Acme\\unins000.exe\" /SILENT", QuietUninstall.Command(vendor, None));

        // A quiet string Exo wouldn't run at all (not a full path to an .exe): passed over for what the entry's installer says.
        var odd = Entry("Acme Paint_is1", "\"C:\\Acme\\unins000.exe\"", quiet: "cmd /c del C:\\x");
        Assert.Equal(QuietUninstall.Kind.Inno, QuietUninstall.Detect(odd, None));
    }

    [Fact]
    public void SquirrelAndWixBundlesUseTheirOwnSwitches()
    {
        var squirrel = Entry("discordish", "\"C:\\Users\\a\\AppData\\Local\\Discordish\\Update.exe\" --uninstall");
        var burn = Entry("{AAAAAAAA-0000-0000-0000-000000000001}", "\"C:\\ProgramData\\Package Cache\\{AAAA}\\setup.exe\"  /uninstall", bundle: true);

        Assert.Equal("\"C:\\Users\\a\\AppData\\Local\\Discordish\\Update.exe\" --uninstall -s", QuietUninstall.Command(squirrel, None));
        Assert.Equal("\"C:\\ProgramData\\Package Cache\\{AAAA}\\setup.exe\"  /uninstall /passive /norestart", QuietUninstall.Command(burn, None));
    }

    [Fact]
    public void ASwitchAlreadyThereIsNotAddedTwice()
    {
        var entry = Entry("Acme_is1", "\"C:\\Acme\\unins000.exe\" /verysilent");
        Assert.Equal("\"C:\\Acme\\unins000.exe\" /verysilent /SUPPRESSMSGBOXES /NORESTART", QuietUninstall.Command(entry, None));
    }

    [Fact]
    public void AnUnknownUninstallerGetsNoGuessedSwitch()
    {
        Assert.Null(QuietUninstall.Command(Entry("Acme", "\"C:\\Acme\\remove.exe\" -x"), None));
        Assert.Null(QuietUninstall.Command(Entry("Acme", null), None));
        Assert.Null(QuietUninstall.Command(Entry("Acme", "remove.exe"), _ => throw new InvalidOperationException("a bare name is never read")));
    }

    [Fact]
    public void TheMarkerReadsEachInstallersOwnWords()
    {
        static byte[] With(string words) => [.. new byte[64], .. Encoding.ASCII.GetBytes(words), .. new byte[64]];

        Assert.Equal(QuietUninstall.Kind.Nsis, QuietUninstall.Marker(With("<assemblyIdentity name=\"Nullsoft.NSIS.exehead\"/>")));
        Assert.Equal(QuietUninstall.Kind.Inno, QuietUninstall.Marker(With("Inno Setup Setup Data (6.2.0)")));
        Assert.Equal(QuietUninstall.Kind.Burn, QuietUninstall.Marker(With(".wixburn")));
        Assert.Equal(QuietUninstall.Kind.Unknown, QuietUninstall.Marker(With("MZ this program cannot be run in DOS mode")));
    }

    [Fact]
    public void ARunTakesTheQuietCommandOnlyWithItsPass()
    {
        var program = new InstalledProgram("HKLM:Acme_is1", "Acme", null, null, null, null, null, null, false, true,
            "\"C:\\Acme\\unins000.exe\"", null, null, true, null, null, @"HKLM\Software\Microsoft\Windows\CurrentVersion\Uninstall\Acme_is1");

        Assert.Equal(("\"C:\\Acme\\unins000.exe\"", QuietUninstall.Kind.Unknown), WebHostBridge.UninstallCommand(program, quiet: false, None));
        Assert.Equal(("\"C:\\Acme\\unins000.exe\" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART", QuietUninstall.Kind.Inno), WebHostBridge.UninstallCommand(program, quiet: true, None));
    }
}
