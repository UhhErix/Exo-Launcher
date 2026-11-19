using ExoLauncher.Helpers;
using ExoLauncher.Models;
using ExoLauncher.Services;
using Microsoft.Win32;

namespace ExoLauncher.Tests;

/// <summary>
/// Open Exo when Windows starts. Ways it could fail: it is on before the player turns it on, the Run value names a path
/// Windows splits at its spaces, a startup turned off in Task Manager stays off after the switch is turned on, or a moved
/// install leaves a stale path. Runs on a throwaway key under HKCU\Software\ExoLauncherTests.
/// </summary>
public sealed class StartWithWindowsTests : IDisposable
{
    private const string Exe = @"C:\Users\A B\AppData\Local\ExoLauncher\app\ExoLauncher.exe";
    private readonly string _test = @"Software\ExoLauncherTests\" + Guid.NewGuid().ToString("N");
    private readonly RegistryKey _run;
    private readonly RegistryKey _approved;

    public StartWithWindowsTests()
    {
        _run = Registry.CurrentUser.CreateSubKey(_test + @"\Run");
        _approved = Registry.CurrentUser.CreateSubKey(_test + @"\Approved");
    }

    public void Dispose()
    {
        _run.Dispose();
        _approved.Dispose();
        Registry.CurrentUser.DeleteSubKeyTree(_test, throwOnMissingSubKey: false);
    }

    [Fact]
    public void ItIsOffUntilThePlayerTurnsItOn() => Assert.False(new AppSettings().StartWithWindows);

    [Fact]
    public void TheRunValueQuotesExosPath() =>
        Assert.Equal(@"""C:\Users\A B\AppData\Local\ExoLauncher\app\ExoLauncher.exe"" --startup", StartWithWindows.CommandFor(Exe));

    [Fact]
    public void OnWritesTheRunValueAndOffTakesItOut()
    {
        StartWithWindows.ApplyTo(_run, _approved, on: true, Exe);
        Assert.Equal(StartWithWindows.CommandFor(Exe), _run.GetValue(StartWithWindows.ValueName));
        StartWithWindows.ApplyTo(_run, _approved, on: false, Exe);
        Assert.Null(_run.GetValue(StartWithWindows.ValueName));
    }

    [Fact]
    public void OnUndoesTaskManagersOff()
    {
        _approved.SetValue(StartWithWindows.ValueName, StartupApps.Approved(enabled: false, DateTime.UtcNow), RegistryValueKind.Binary);
        StartWithWindows.ApplyTo(_run, _approved, on: true, Exe);
        Assert.True(StartupApps.IsEnabled(_approved.GetValue(StartWithWindows.ValueName) as byte[]));
    }

    [Fact]
    public void OffClearsWindowsApprovalRecord()
    {
        _approved.SetValue(StartWithWindows.ValueName, StartupApps.Approved(enabled: false, DateTime.UtcNow), RegistryValueKind.Binary);
        StartWithWindows.ApplyTo(_run, _approved, on: false, Exe);
        Assert.Null(_approved.GetValue(StartWithWindows.ValueName));
    }

    [Fact]
    public void AMovedInstallIsRewritten()
    {
        _run.SetValue(StartWithWindows.ValueName, StartWithWindows.CommandFor(@"D:\Old\ExoLauncher.exe"));
        StartWithWindows.ApplyTo(_run, _approved, on: true, Exe);
        Assert.Equal(StartWithWindows.CommandFor(Exe), _run.GetValue(StartWithWindows.ValueName));
    }
}
