using System.Text.Json;
using ExoLauncher.Services;

// tools/AppFiles: a release's file list and pack (see ExoLauncher/Services/AppFiles.cs).
//   build   --app <publish folder> [--setup <ExoSetup.exe>] --version X.Y.Z --installer <revision> --out <folder> [--level 19]
//   measure --manifest <files.json> --app <installed app folder>
var options = new Dictionary<string, string>(StringComparer.Ordinal);
for (var i = 1; i + 1 < args.Length; i += 2) options[args[i]] = args[i + 1];
string Need(string name) => options.TryGetValue(name, out var value) ? value : throw new ArgumentException("Missing " + name);

switch (args.FirstOrDefault())
{
    case "build":
    {
        var sources = AppFiles.Folder(Need("--app"));
        if (options.TryGetValue("--setup", out var setup))
            sources.Add(new AppFiles.Source(Path.GetFileName(setup), Path.GetFullPath(setup), Setup: true));
        var level = options.TryGetValue("--level", out var l) ? int.Parse(l, System.Globalization.CultureInfo.InvariantCulture) : AppFiles.Level;
        var started = System.Diagnostics.Stopwatch.StartNew();
        var manifest = AppFiles.Build(sources, Need("--version"), Need("--installer"), Need("--out"), level);
        Console.WriteLine($"files.json: {manifest.Files.Count} files, {manifest.Files.Sum(f => f.Size):N0} bytes; files.pack: {manifest.PackSize:N0} bytes, " +
                          $"SHA-256 {manifest.PackSha256} ({started.Elapsed.TotalSeconds:0} s)");
        return 0;
    }
    case "measure":
    {
        var manifest = AppFiles.Parse(File.ReadAllBytes(Need("--manifest"))) ?? throw new InvalidDataException("Not a valid files.json.");
        var installed = new Dictionary<string, (long, string)>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in AppFiles.Folder(Need("--app")))
            installed[file.Path] = (new FileInfo(file.FullPath).Length, await AppFiles.Sha256FileAsync(file.FullPath, CancellationToken.None));
        var (reuse, fetch) = AppFiles.Diff(manifest, installed);
        var spans = AppFiles.Ranges(fetch);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            files = manifest.Files.Count,
            unchanged = reuse.Count,
            changed = fetch.Count,
            changedBytes = fetch.Sum(f => f.Size),
            downloadBytes = spans.Sum(s => s.Length),
            requests = spans.Count,
            packBytes = manifest.PackSize,
            largest = fetch.OrderByDescending(f => f.Length).Take(10).Select(f => new { f.Path, f.Length }),
        }, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
    default:
        Console.Error.WriteLine("Usage: build --app <folder> [--setup <ExoSetup.exe>] --version X.Y.Z --installer <revision> --out <folder> | measure --manifest <files.json> --app <folder>");
        return 2;
}
