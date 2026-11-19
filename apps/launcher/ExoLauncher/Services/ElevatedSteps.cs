using System.Security.Cryptography;
using System.Text;

namespace ExoLauncher.Services;

/// <summary>
/// Many elevated steps under one Windows prompt. They fit one cmd line, or else they go in a file the elevated process reads:
/// that file sits where this user can write, so the elevated command itself carries the hash of what was written and runs
/// only the bytes it read and checked, never what someone changed since.
/// </summary>
internal static class ElevatedSteps
{
    private const int CmdLimit = 7800;

    /// <summary>What to start elevated, and the file to write first when it needs one.</summary>
    internal sealed record Plan(string File, string Arguments, byte[]? Script);

    internal static Plan For(IReadOnlyList<string> steps, string scriptPath)
    {
        var line = string.Join(" & ", steps);
        if (line.Length <= CmdLimit) return new("cmd.exe", "/d /c " + line, null);
        var script = Encoding.Unicode.GetBytes(string.Join('\n', steps));
        var stub =
            $"$b=[IO.File]::ReadAllBytes('{scriptPath.Replace("'", "''")}');" +
            "if(([BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($b)) -replace '-','') -ne " +
            $"'{Convert.ToHexString(SHA256.HashData(script))}'){{exit 1}};" +
            "foreach($l in ([Text.Encoding]::Unicode.GetString($b) -split \"`n\")){if($l){" +
            "$i=New-Object Diagnostics.ProcessStartInfo;$i.FileName=[Environment]::SystemDirectory+'\\cmd.exe';$i.Arguments='/d /c '+$l;$i.UseShellExecute=$false;$i.CreateNoWindow=$true;" +
            "([Diagnostics.Process]::Start($i)).WaitForExit()}}";
        return new(LeftoverScan.PowerShell, "-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(stub)), script);
    }
}
