using System.Globalization;
using ExoLauncher.Helpers;
using Microsoft.Win32;

namespace ExoLauncher.Services;

/// <summary>
/// The registry values the Windows side keeps and puts back exactly, as <see cref="RegValue"/> (the start-up guard's record of a
/// value: a string, an expandable string, bytes or a number). A value of another kind (a QWord, a list of strings) is one
/// Exo cannot put back exactly, so it is never read as "not there" and never written over: reading it throws.
/// </summary>
internal static class RegistryValues
{
    /// <summary>The value as the registry holds it, or null when the key has none.</summary>
    internal static RegValue? From(RegistryKey key, string name)
    {
        if (key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not { } raw) return null;
        return key.GetValueKind(name) switch
        {
            RegistryValueKind.String => RegValue.Str((string)raw),
            RegistryValueKind.ExpandString => RegValue.Expand((string)raw),
            RegistryValueKind.Binary => RegValue.Bin((byte[])raw),
            RegistryValueKind.DWord => RegValue.DWord((int)raw),
            var other => throw new IOException($"A {other} value can't be put back exactly."),
        };
    }

    internal static void WriteTo(RegistryKey key, string name, RegValue value)
    {
        switch (value.Kind)
        {
            case RegistryValueKind.DWord: key.SetValue(name, value.Number, RegistryValueKind.DWord); break;
            case RegistryValueKind.Binary: key.SetValue(name, value.Bytes ?? [], RegistryValueKind.Binary); break;
            case RegistryValueKind.ExpandString: key.SetValue(name, value.Text ?? "", RegistryValueKind.ExpandString); break;
            default: key.SetValue(name, value.Text ?? "", RegistryValueKind.String); break;
        }
    }

    /// <summary>What a program reading the value gets: an int for a DWord, anything else a non-number.</summary>
    internal static object? Plain(RegValue? value) => value is null ? null : value.Kind == RegistryValueKind.DWord ? (object)value.Number : value;

    /// <summary>The value in a line for app.log: a number, a string, or its kind.</summary>
    internal static string Describe(RegValue? value) => value switch
    {
        null => "not set",
        { Kind: RegistryValueKind.DWord } => value.Number.ToString(CultureInfo.InvariantCulture),
        { Kind: RegistryValueKind.String or RegistryValueKind.ExpandString } => "\"" + value.Text + "\"",
        _ => value.Kind.ToString(),
    };
}

/// <summary>One named value of a key, for <see cref="RegTree"/>.</summary>
internal sealed record RegEntry(string Name, RegValue Value);

/// <summary>A key and everything under it, small enough to keep in the record: Game Bar's link handlers.</summary>
internal sealed class RegTree
{
    /// <summary>The most keys and values a kept tree may hold, and the most data: a handler is a few lines, never a hive.</summary>
    internal const int MaxNodes = 64, MaxBytes = 64 * 1024;

    public List<RegEntry> Values { get; set; } = [];
    public Dictionary<string, RegTree> Keys { get; set; } = [];

    /// <summary>Whether two trees hold the same keys, names and values (names are not case sensitive, as the registry's are not).</summary>
    internal bool SameAs(RegTree? other)
    {
        if (other is null || Values.Count != other.Values.Count || Keys.Count != other.Keys.Count) return false;
        foreach (var entry in Values)
            if (other.Values.FirstOrDefault(v => string.Equals(v.Name, entry.Name, StringComparison.OrdinalIgnoreCase)) is not { } mine || mine.Value != entry.Value) return false;
        foreach (var (name, child) in Keys)
            if (other.Keys.FirstOrDefault(k => string.Equals(k.Key, name, StringComparison.OrdinalIgnoreCase)) is not { Value: { } theirs } || !child.SameAs(theirs)) return false;
        return true;
    }

    /// <summary>The tree under <paramref name="key"/>; <see cref="IOException"/> when it is too big to keep.</summary>
    internal static RegTree Read(RegistryKey key, ref int nodes, ref int bytes)
    {
        var tree = new RegTree();
        foreach (var name in key.GetValueNames())
        {
            if (++nodes > MaxNodes) throw new IOException("The key holds more than Exo keeps.");
            var value = RegistryValues.From(key, name) ?? throw new IOException("A value under it can't be read.");
            bytes += (value.Text?.Length ?? 0) + (value.Bytes?.Length ?? 0) + name.Length;
            if (bytes > MaxBytes) throw new IOException("The key holds more than Exo keeps.");
            tree.Values.Add(new RegEntry(name, value));
        }
        foreach (var child in key.GetSubKeyNames())
        {
            if (++nodes > MaxNodes) throw new IOException("The key holds more than Exo keeps.");
            using var sub = key.OpenSubKey(child) ?? throw new IOException("A key under it can't be read.");
            tree.Keys[child] = Read(sub, ref nodes, ref bytes);
        }
        return tree;
    }

    internal void Write(RegistryKey key)
    {
        foreach (var entry in Values) RegistryValues.WriteTo(key, entry.Name, entry.Value);
        foreach (var (name, child) in Keys)
        {
            using var sub = key.CreateSubKey(name, writable: true);
            child.Write(sub);
        }
    }
}

/// <summary>
/// Where Game Mode and Game Bar reach the user's own registry (HKCU), so what they do is tested with no registry at all. A
/// call Windows refuses throws as the registry's own calls do (<see cref="IOException"/>,
/// <see cref="UnauthorizedAccessException"/>, <see cref="System.Security.SecurityException"/>); a value or key that is not there
/// reads as null.
/// </summary>
internal interface IUserRegistry
{
    RegValue? Get(string key, string name);
    /// <summary>Sets the value, making the key if it is not there.</summary>
    void Set(string key, string name, RegValue value);
    /// <summary>Removes the value; nothing to remove is fine.</summary>
    void Delete(string key, string name);
    /// <summary>The key and everything under it, or null when the key is not there.</summary>
    RegTree? ReadTree(string key);
    /// <summary>Makes the key hold exactly this tree.</summary>
    void WriteTree(string key, RegTree tree);
    /// <summary>Removes the key and everything under it; nothing to remove is fine.</summary>
    void DeleteTree(string key);
}

/// <summary>The real thing: HKCU. Every write checks <see cref="HostMode.IsDisposable"/> itself, so no caller can bypass it.</summary>
internal sealed class UserRegistry : IUserRegistry
{
    internal static UserRegistry Shared { get; } = new();

    public RegValue? Get(string key, string name)
    {
        using var open = Registry.CurrentUser.OpenSubKey(key);
        return open is null ? null : RegistryValues.From(open, name);
    }

    public void Set(string key, string name, RegValue value)
    {
        if (HostMode.IsDisposable) return;
        using var open = Registry.CurrentUser.CreateSubKey(key, writable: true);
        RegistryValues.WriteTo(open, name, value);
    }

    public void Delete(string key, string name)
    {
        if (HostMode.IsDisposable) return;
        using var open = Registry.CurrentUser.OpenSubKey(key, writable: true);
        open?.DeleteValue(name, throwOnMissingValue: false);
    }

    public RegTree? ReadTree(string key)
    {
        using var open = Registry.CurrentUser.OpenSubKey(key);
        if (open is null) return null;
        int nodes = 0, bytes = 0;
        return RegTree.Read(open, ref nodes, ref bytes);
    }

    public void WriteTree(string key, RegTree tree)
    {
        if (HostMode.IsDisposable) return;
        Registry.CurrentUser.DeleteSubKeyTree(key, throwOnMissingSubKey: false);
        using var open = Registry.CurrentUser.CreateSubKey(key, writable: true);
        tree.Write(open);
    }

    public void DeleteTree(string key)
    {
        if (HostMode.IsDisposable) return;
        Registry.CurrentUser.DeleteSubKeyTree(key, throwOnMissingSubKey: false);
    }
}
