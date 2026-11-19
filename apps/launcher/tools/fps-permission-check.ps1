# Proves, on a PC you choose, that the FPS counter's one permission works for a process that isn't elevated, with no group
# and no sign-out, then puts everything back. It gives your own account Exo's rights (PresentTrace.AddRightsScript), starts a
# plain-user process that runs the whole trace (start, enable the three providers, read, stop), and removes those rights.
# Changing them is a machine security setting, so this is run by the PC's owner, from an elevated PowerShell:
#     pwsh -File tools\fps-permission-check.ps1
# Each line it prints is Windows' answer: 0 is yes, 5 is access denied.
$ErrorActionPreference = 'Stop'
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run this from an elevated PowerShell.' }

$session = '7E4C6A5E-93B1-4E3C-8F3A-2B1D6C0F5A91'
$providers = 'CA11C036-0102-4A2D-A6AD-F03CFED5D3C9', '783ACA0A-790E-4D7F-8451-AA850511C6B9', '802EC45A-1E99-4B83-9920-87C98277BA9D'
$key = 'HKLM:\SYSTEM\CurrentControlSet\Control\WMI\Security'
$user = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$out = Join-Path $env:TEMP "exo-fps-check-$PID.txt"
$probe = Join-Path $env:TEMP "exo-fps-check-$PID.ps1"

# The trace as a plain user runs it, under the session id whose rights are being tested.
@"
`$out = '$out'
try {
  Add-Type -TypeDefinition @'
using System; using System.Collections.Generic; using System.Runtime.InteropServices; using System.Threading;
public static class P {
  [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] static extern uint StartTraceW(out ulong session, string name, byte[] properties);
  [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] static extern uint ControlTraceW(ulong session, string name, byte[] properties, uint code);
  [DllImport("advapi32.dll")] static extern uint EnableTraceEx2(ulong session, ref Guid provider, uint control, byte level, ulong any, ulong all, uint timeout, IntPtr parameters);
  [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] static extern ulong OpenTraceW(IntPtr logfile);
  [DllImport("advapi32.dll")] static extern uint ProcessTrace(ulong[] handles, uint count, IntPtr start, IntPtr end);
  [DllImport("advapi32.dll")] static extern uint CloseTrace(ulong handle);
  delegate void Callback(IntPtr record);
  static long events; static Callback keep = r => Interlocked.Increment(ref events);
  static byte[] Props(Guid session) {
    var p = new byte[248];
    BitConverter.GetBytes(248u).CopyTo(p, 0); session.ToByteArray().CopyTo(p, 24); BitConverter.GetBytes(1u).CopyTo(p, 40); BitConverter.GetBytes(0x20000u).CopyTo(p, 44);
    BitConverter.GetBytes(64u).CopyTo(p, 48); BitConverter.GetBytes(0x100u).CopyTo(p, 64); BitConverter.GetBytes(1u).CopyTo(p, 68); BitConverter.GetBytes(120u).CopyTo(p, 116);
    return p;
  }
  public static List<string> Run(string id, string[] providers) {
    var session = new Guid(id); var name = "ExoFpsCounterCheck"; var lines = new List<string>();
    ulong h; var start = StartTraceW(out h, name, Props(session)); lines.Add("start: " + start);
    if (start != 0) return lines;
    foreach (var provider in providers) { var g = new Guid(provider); lines.Add("enable " + provider + ": " + EnableTraceEx2(h, ref g, 1, 4, 0, 0, 0, IntPtr.Zero)); }
    lines.Add("query: " + ControlTraceW(0, name, Props(session), 0));
    var log = Marshal.AllocHGlobal(448); for (var i = 0; i < 448; i++) Marshal.WriteByte(log, i, 0);
    Marshal.WriteIntPtr(log, 8, Marshal.StringToHGlobalUni(name));
    Marshal.WriteInt32(log, 28, unchecked((int)0x10000100));
    Marshal.WriteIntPtr(log, 424, Marshal.GetFunctionPointerForDelegate(keep));
    var reader = OpenTraceW(log); lines.Add("open: " + (reader == ulong.MaxValue ? "refused" : "0"));
    uint read = 999;
    if (reader != ulong.MaxValue) {
      var thread = new Thread(() => { read = ProcessTrace(new[] { reader }, 1, IntPtr.Zero, IntPtr.Zero); }); thread.Start();
      Thread.Sleep(3000);
      lines.Add("stop: " + ControlTraceW(0, name, Props(session), 1));
      thread.Join(5000);
      lines.Add("read: " + read + ", " + Interlocked.Read(ref events) + " events in 3 s");
      CloseTrace(reader);
    } else lines.Add("stop: " + ControlTraceW(0, name, Props(session), 1));
    return lines;
  }
}
'@
  `$admin = (New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
  @("administrator: `$admin") + [P]::Run('$session', [string[]]@('$($providers -join "','")')) | Set-Content `$out
} catch { "failed: `$_" | Set-Content `$out }
"@ | Set-Content $probe

function Run-Probe {
  if (Test-Path $out) { [IO.File]::Delete($out) }
  & runas.exe '/trustlevel:0x20000' "powershell.exe -NoProfile -ExecutionPolicy Bypass -File $probe" | Out-Null
  foreach ($i in 1..40) { if (Test-Path $out) { break }; Start-Sleep -Milliseconds 500 }
  if (Test-Path $out) { Get-Content $out } else { 'no answer from the plain-user process' }
}

# What is there now, to put back exactly.
$ids = @($session) + $providers
$saved = @{}
foreach ($id in $ids) { $saved[$id] = (Get-ItemProperty $key -ErrorAction SilentlyContinue).($id.ToLowerInvariant()) }

'--- before: a plain-user process'
Run-Probe
try {
  Add-Type -Name Etw -Namespace ExoCheck -MemberDefinition '[DllImport("advapi32.dll")] public static extern uint EventAccessControl(ref Guid guid, uint operation, byte[] sid, uint rights, [MarshalAs(UnmanagedType.U1)] bool allow);'
  $sid = New-Object Security.Principal.SecurityIdentifier($user)
  $bytes = New-Object byte[] $sid.BinaryLength
  $sid.GetBinaryForm($bytes, 0)
  $id = [guid]$session
  "grant session: " + [ExoCheck.Etw]::EventAccessControl([ref]$id, 2, $bytes, 0x423, $true)
  foreach ($provider in $providers) { $id = [guid]$provider; "grant ${provider}: " + [ExoCheck.Etw]::EventAccessControl([ref]$id, 2, $bytes, 0x80, $true) }
  '--- after: a plain-user process, no sign-out in between'
  Run-Probe
}
finally {
  foreach ($id in $ids) {
    if ($null -eq $saved[$id]) { Remove-ItemProperty $key -Name $id.ToLowerInvariant() -ErrorAction SilentlyContinue }
    else { Set-ItemProperty $key -Name $id.ToLowerInvariant() -Value $saved[$id] -Type Binary }
  }
  foreach ($file in $out, $probe) { if (Test-Path $file) { [IO.File]::Delete($file) } }
  '--- rights put back as they were'
}
