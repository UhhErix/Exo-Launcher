using ExoLauncher.Helpers;

namespace ExoLauncher.Tests;

/// <summary>
/// The one deletion gate (DeletionGate) every host delete goes through, in temp folders only. Ways it could fail: a cleaner
/// deleting a browser's sign-in data, a credential store, a save or Exo's own data; a folder with a protected file inside
/// taken whole; an owning program that is open being deleted under; an unknown process list read as "nothing running";
/// a refusal cached so a folder that became safe stays kept; a freed-space figure taken from the summed file sizes instead of
/// the measured change in free bytes; a shader cache touched while Exo Shaders compiles, or by a cleaner at all.
/// </summary>
public sealed class DeletionGateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "exo-gate-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _running = [];
    private bool _unknown;
    private bool _shaderBusy;

    public DeletionGateTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) DeletionGate.DeleteDirectory(_root, recursive: true);
    }

    private DeletionGate.Policy Policy() => new(
        KeptRoots: [Path.Combine(_root, "Documents", "Exo", "Backups")],
        ExoData: Path.Combine(_root, "Exo"),
        ShaderRoots: [Path.Combine(_root, "Local", "D3DSCache")],
        Running: () => _unknown ? null : _running,
        ShaderBusy: () => _shaderBusy);

    private string File(string relative, string text = "x")
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, text);
        return path;
    }

    [Theory]
    [InlineData(@"Chrome\User Data\Default\Cookies")]
    [InlineData(@"Chrome\User Data\Default\Login Data")]
    [InlineData(@"Chrome\User Data\Local State")]
    [InlineData(@"Microsoft\Credentials\abc")]
    [InlineData(@"Users\me\.ssh\id_ed25519")]
    [InlineData(@"Games\Hades\Saved\SaveGames\slot1.sav")]
    [InlineData(@"Documents\My Games\Game\profile.ini")]
    public void A_protected_segment_is_refused_for_cleaners(string relative)
    {
        var path = File(relative);
        var why = DeletionGate.Refusal(path, clean: true, owners: null, Policy());
        Assert.NotNull(why);
        Assert.True(System.IO.File.Exists(path));
        Assert.False(DeletionGate.Clean(path, null, out _, Policy()));
        Assert.True(System.IO.File.Exists(path));
    }

    [Fact]
    public void Exo_s_own_backups_in_documents_are_refused_for_every_caller()
    {
        var backups = Path.Combine(_root, "Documents", "Exo", "Backups");
        var file = File(@"Documents\Exo\Backups\2026-11-07.zip");
        Assert.NotNull(DeletionGate.Refusal(file, clean: false, owners: null, Policy()));
        Assert.NotNull(DeletionGate.Refusal(backups, clean: false, owners: null, Policy()));
        Assert.True(System.IO.File.Exists(file));
        // The live gate refuses the real backups folder before any disk access, so this throws and deletes nothing.
        var real = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Exo", "Backups", "probe.zip");
        Assert.Throws<DeletionRefusedException>(() => DeletionGate.DeleteFile(real));
    }

    [Fact]
    public void A_cleaner_is_refused_inside_exos_own_data_except_its_old_backups()
    {
        var exo = Path.Combine(_root, "Exo");
        var settings = File("Exo\\settings.json");
        Assert.NotNull(DeletionGate.Refusal(settings, clean: true, owners: null, Policy()));
        Assert.Null(DeletionGate.Refusal(Path.Combine(exo, "app.previous-1.2.3"), clean: true, owners: null, Policy()));
        Assert.Null(DeletionGate.Refusal(Path.Combine(exo, "app.incoming-1.2.4"), clean: true, owners: null, Policy()));
        // Exo's own owned writes (its settings, its sign-in files) are its own to replace.
        Assert.Null(DeletionGate.Refusal(settings, clean: false, owners: null, Policy()));
    }

    [Fact]
    public void A_folder_holding_a_protected_file_is_not_taken_whole_by_a_cleaner()
    {
        var folder = Path.Combine(_root, "Temp", "Cache");
        File(@"Temp\Cache\data.bin");
        File(@"Temp\Cache\Default\Cookies");
        Assert.False(DeletionGate.Clean(folder, null, out var why, Policy()));
        Assert.False(string.IsNullOrEmpty(why));
        Assert.True(System.IO.File.Exists(Path.Combine(folder, "Default", "Cookies")));
        Assert.True(System.IO.File.Exists(Path.Combine(folder, "data.bin")));
    }

    [Fact]
    public void A_folder_with_nothing_protected_inside_is_cleaned()
    {
        var folder = Path.Combine(_root, "Temp", "Old");
        File(@"Temp\Old\a.tmp");
        File(@"Temp\Old\sub\b.tmp");
        Assert.True(DeletionGate.Clean(folder, null, out var why, Policy()));
        Assert.Null(why);
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public void An_open_owner_is_refused_and_names_the_program()
    {
        var file = File(@"Temp\Steam\htmlcache\index");
        _running.Add("steam");
        Assert.False(DeletionGate.Clean(file, ["steam", "steamwebhelper"], out var why, Policy()));
        Assert.Contains("steam", why, StringComparison.OrdinalIgnoreCase);
        Assert.True(System.IO.File.Exists(file));
    }

    [Fact]
    public void An_unknown_process_list_means_keep()
    {
        var file = File(@"Temp\Steam\htmlcache\index");
        _unknown = true;
        Assert.False(DeletionGate.Clean(file, ["steam"], out var why, Policy()));
        Assert.NotNull(why);
        Assert.True(System.IO.File.Exists(file));
    }

    [Fact]
    public void A_refusal_is_checked_again_on_the_next_call_and_not_cached()
    {
        var file = File(@"Temp\Steam\htmlcache\index");
        _running.Add("steam");
        Assert.False(DeletionGate.Clean(file, ["steam"], out _, Policy()));
        // The program closes: the very next call deletes, with no memory of the refusal.
        _running.Clear();
        Assert.True(DeletionGate.Clean(file, ["steam"], out var why, Policy()));
        Assert.Null(why);
        Assert.False(System.IO.File.Exists(file));
    }

    [Fact]
    public void Shader_caches_are_never_a_cleaners_and_wait_for_a_compile_for_everyone_else()
    {
        var file = File(@"Local\D3DSCache\abc\cache.db");
        Assert.NotNull(DeletionGate.Refusal(file, clean: true, owners: null, Policy()));
        Assert.False(DeletionGate.Clean(file, null, out _, Policy()));
        Assert.True(System.IO.File.Exists(file));

        _shaderBusy = true;
        Assert.NotNull(DeletionGate.Refusal(file, clean: false, owners: null, Policy()));

        // Once no compile runs, the lock is free for the one caller allowed to touch a shader folder (Exo Shaders' own deletes never come through here).
        _shaderBusy = false;
        Assert.Null(DeletionGate.Refusal(file, clean: false, owners: null, Policy()));
    }

    [Fact]
    public void Measured_freed_space_is_the_change_in_free_bytes_not_the_sum_of_sizes()
    {
        var file = File(@"Temp\big.tmp", new string('x', 4096));
        var readings = new Queue<long>([900, 1_000]);
        var (result, freed) = DeletionGate.Measure(_root, () =>
        {
            DeletionGate.DeleteFile(file);
            return true;
        }, _ => readings.Dequeue());
        Assert.True(result);
        // The file was 4 KB, but the drive gained 100 bytes (other writers took the rest): report what the drive says.
        Assert.Equal(100, freed);
    }

    [Fact]
    public void Measured_freed_space_is_never_negative()
    {
        var readings = new Queue<long>([800, 500]);
        var (_, freed) = DeletionGate.Measure(_root, () => 0, _ => readings.Dequeue());
        Assert.Equal(0, freed);
    }

    [Fact]
    public void Owned_deletes_work_in_a_temp_folder_and_throw_only_on_refusal()
    {
        var file = File(@"Temp\plain.tmp");
        DeletionGate.DeleteFile(file);
        Assert.False(System.IO.File.Exists(file));
        var folder = Path.Combine(_root, "Temp", "tree");
        File(@"Temp\tree\a.txt");
        DeletionGate.DeleteDirectory(folder, recursive: true);
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public void A_folder_that_contains_the_kept_backups_folder_is_refused_too()
    {
        // An ancestor of Documents\Exo\Backups holds it: a whole-folder delete of it would take the backups with it.
        var documents = Path.Combine(_root, "Documents");
        var exo = Path.Combine(documents, "Exo");
        Assert.NotNull(DeletionGate.Refusal(exo, clean: false, owners: null, Policy()));
        Assert.NotNull(DeletionGate.Refusal(exo, clean: true, owners: null, Policy()));
        Assert.NotNull(DeletionGate.Refusal(documents, clean: true, owners: null, Policy()));
        Assert.Null(DeletionGate.Refusal(Path.Combine(documents, "Other"), clean: true, owners: null, Policy()));
    }

    [Fact]
    public void Browser_session_files_are_kept_from_junk_clean_but_allowed_for_uninstall_recycle()
    {
        var folder = Path.Combine(_root, "Temp", "UninstalledElectronApp");
        var cookies = File(@"Temp\UninstalledElectronApp\Cookies");
        File(@"Temp\UninstalledElectronApp\Local State");
        var ssh = File(@"Temp\UninstalledElectronApp\.ssh\id_ed25519");

        Assert.NotNull(DeletionGate.Check(cookies, policy: Policy(), allowAppBrowserFiles: false));
        Assert.NotNull(DeletionGate.Check(folder, policy: Policy(), allowAppBrowserFiles: false));
        Assert.Null(DeletionGate.Check(cookies, policy: Policy(), allowAppBrowserFiles: true));
        // Even with allowAppBrowserFiles: true, SSH keys / credentials / game saves inside a folder remain blocked.
        Assert.NotNull(DeletionGate.Check(ssh, policy: Policy(), allowAppBrowserFiles: true));
        Assert.NotNull(DeletionGate.Check(folder, policy: Policy(), allowAppBrowserFiles: true));
    }
}
