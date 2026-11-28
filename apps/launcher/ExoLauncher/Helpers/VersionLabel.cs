namespace ExoLauncher.Helpers;

public static class VersionLabel
{
    public static string Of(string? version)
    {
        var shown = (version ?? "").Trim().TrimStart('v');
        var core = shown.Split('+', '-')[0];
        var parts = core.Split('.');
        if (parts.Length < 2 || parts[0] != "4" || !uint.TryParse(parts[1], out var minor)) return shown;
        var patch = parts.Length > 2 && uint.TryParse(parts[2], out var parsed) ? parsed : 0;
        return patch == 0 ? $"0.{minor + 1} beta" : $"0.{minor + 1}.{patch} beta";
    }
}
