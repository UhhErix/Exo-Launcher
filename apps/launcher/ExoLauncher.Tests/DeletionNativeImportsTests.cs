using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using ExoLauncher.Helpers;

namespace ExoLauncher.Tests;

/// <summary>
/// The native half of the delete rule (BannedSymbols.txt covers the .NET deletes). Windows' own delete calls (the Recycle Bin
/// send and empty, the Win32 file and folder deletes) may be declared in DeletionNative alone, and every host caller reaches
/// them through the deletion gate. Ways it could fail: a new P/Invoke delete added elsewhere and reached without the gate;
/// a delete hidden in a native name in a string, which the source scan catches; the scan seeing nothing and passing anyway.
/// </summary>
public sealed partial class DeletionNativeImportsTests
{
    private static readonly HashSet<string> DeleteNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "SHFileOperation", "SHFileOperationW", "SHFileOperationA", "SHEmptyRecycleBin", "SHEmptyRecycleBinW", "SHEmptyRecycleBinA",
        "DeleteFile", "DeleteFileW", "DeleteFileA", "RemoveDirectory", "RemoveDirectoryW", "RemoveDirectoryA",
    };

    [Fact]
    public void Only_DeletionNative_declares_a_native_delete()
    {
        var offenders = typeof(DeletionNative).Assembly.GetTypes()
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(method => method.GetCustomAttribute<DllImportAttribute>() is not null || method.GetCustomAttribute<LibraryImportAttribute>() is not null)
            .Where(method => method.DeclaringType != typeof(DeletionNative))
            .Where(method => DeleteNames.Contains(method.GetCustomAttribute<DllImportAttribute>()?.EntryPoint is { Length: > 0 } entry ? entry : method.Name))
            .Select(method => $"{method.DeclaringType}.{method.Name}")
            .ToArray();
        Assert.True(offenders.Length == 0, "Native delete declared outside DeletionNative: " + string.Join(", ", offenders));
        // The scan must see the native deletes it is meant to guard, or it proves nothing.
        Assert.Contains(typeof(DeletionNative).GetMethods(BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly),
            method => method.Name == "SHFileOperationW");
    }

    [Fact]
    public void No_source_names_a_native_delete_outside_DeletionNative()
    {
        var offenders = Repo.HostSources()
            .Where(path => !path.EndsWith(Path.Combine("Helpers", "DeletionNative.cs"), StringComparison.OrdinalIgnoreCase))
            .SelectMany(path => File.ReadLines(path).Select((line, index) => (path, line, index)))
            .Where(entry => DeleteName().Matches(entry.line).Count > 0)
            .Select(entry => $"{Path.GetRelativePath(Repo.Root, entry.path)}:{entry.index + 1}")
            .ToArray();
        Assert.True(offenders.Length == 0, "Native delete named outside DeletionNative: " + string.Join(", ", offenders));
    }

    [GeneratedRegex(@"\b(SHFileOperation[AW]?|SHEmptyRecycleBin[AW]?|DeleteFile[AW]|RemoveDirectory[AW])\b")]
    private static partial Regex DeleteName();
}
