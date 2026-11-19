using ExoLauncher.Helpers;

namespace ExoLauncher.Tests;

/// <summary>
/// Which notification-area icons belong to a program, from what Windows records. Ways it could fail in silence, since an
/// icon that isn't matched simply stays on the taskbar: a path under a known folder is written as that folder's id (this
/// PC's Steam is {7C5A40EF-...}\Steam\steam.exe) and never equals the running program's; another program in the same
/// folder is taken for it; case differs (Steam reports c:\program files (x86)\...); an id Windows no longer knows, or
/// braces that are no id, reads as a match or throws.
/// </summary>
public sealed class TrayIconsTests
{
    private static readonly Guid ProgramFilesX86 = new("7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E");
    private const string Steam = @"c:\program files (x86)\steam\steam.exe";

    private static string? KnownFolder(Guid id) => id == ProgramFilesX86 ? @"C:\Program Files (x86)" : null;

    private static IReadOnlyList<TrayIconRegistration> Select(string recorded, int? uid = 1, string? guid = null) =>
        TrayIcons.Select([(recorded, guid, uid)], Steam, KnownFolder);

    [Fact]
    public void A_path_written_as_its_known_folders_id_is_the_running_program() =>
        Assert.Equal([new TrayIconRegistration(null, 1)], Select(@"{7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E}\Steam\steam.exe"));

    [Fact]
    public void The_program_may_be_asked_about_by_its_recorded_path()
    {
        // NVIDIA's container: each driver's has its own folder, so Exo asks about what the entries record.
        const string recorded = @"{7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E}\Steam\steam.exe";
        Assert.Equal([new TrayIconRegistration(null, 1)], TrayIcons.Select([(recorded, null, 1)], recorded, KnownFolder));
    }

    [Fact]
    public void A_plain_path_still_matches_whatever_its_case() =>
        Assert.Single(Select(@"C:\Program Files (x86)\Steam\steam.exe"));

    [Fact]
    public void Another_program_in_the_folder_is_not_it() =>
        Assert.Empty(Select(@"{7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E}\Steam\steamapps\common\Call of Duty HQ\bootstrapper.exe"));

    [Theory]
    [InlineData(@"{00000000-0000-0000-0000-000000000001}\Steam\steam.exe")]
    [InlineData(@"{not an id}\Steam\steam.exe")]
    public void A_folder_id_nobody_knows_matches_nothing(string recorded) =>
        Assert.Empty(Select(recorded));

    [Fact]
    public void An_icon_known_by_its_guid_keeps_it()
    {
        var id = Guid.NewGuid();
        Assert.Equal([new TrayIconRegistration(id, null)], Select(@"{7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E}\Steam\steam.exe", uid: null, guid: id.ToString()));
    }
}
