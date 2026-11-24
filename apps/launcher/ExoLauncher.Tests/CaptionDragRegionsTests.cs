using ExoLauncher.Ui;
using Windows.Graphics;

namespace ExoLauncher.Tests;

public sealed class CaptionDragRegionsTests
{
    private static readonly RectInt32[] Caption = [new(0, 0, 1600, 11)];
    private static readonly RectInt32[] Passthrough = [new(0, 11, 1600, 41)];
    private static readonly RectInt32[] Maximize = [new(1500, 0, 46, 52)];

    [Fact]
    public void Every_region_is_cleared_before_it_is_set()
    {
        var writes = CaptionDragRegions.WriteOrder(Caption, Passthrough, Maximize);

        foreach (var region in Enum.GetValues<CaptionDragRegions.CaptionRegion>())
        {
            var mine = writes.Select((w, i) => (w, i)).Where(x => x.w.Region == region).ToList();
            var clear = mine.FindIndex(x => x.w.Rects is null);
            var set = mine.FindIndex(x => x.w.Rects is not null);
            Assert.True(clear >= 0, $"{region} is never cleared");
            Assert.True(set > clear, $"{region} is set before it is cleared");
        }
    }

    [Fact]
    public void Caption_and_passthrough_are_written_with_the_rects_built_for_them()
    {
        var writes = CaptionDragRegions.WriteOrder(Caption, Passthrough, Maximize);

        Assert.Equal(Caption, writes.Single(w => w.Region == CaptionDragRegions.CaptionRegion.Caption && w.Rects is not null).Rects);
        Assert.Equal(Passthrough, writes.Single(w => w.Region == CaptionDragRegions.CaptionRegion.Passthrough && w.Rects is not null).Rects);
        Assert.Equal(Maximize, writes.Single(w => w.Region == CaptionDragRegions.CaptionRegion.Maximize && w.Rects is not null).Rects);
    }

    [Fact]
    public void An_empty_maximize_set_is_only_its_clear()
    {
        var writes = CaptionDragRegions.WriteOrder(Caption, Passthrough, []);

        Assert.Single(writes, w => w.Region == CaptionDragRegions.CaptionRegion.Maximize);
        Assert.Null(writes.Single(w => w.Region == CaptionDragRegions.CaptionRegion.Maximize).Rects);
    }

    [Fact]
    public void Writes_come_out_in_the_order_Caption_Passthrough_Maximize()
    {
        var order = CaptionDragRegions.WriteOrder(Caption, Passthrough, Maximize).Select(w => w.Region).ToArray();

        Assert.Equal(
            [
                CaptionDragRegions.CaptionRegion.Caption, CaptionDragRegions.CaptionRegion.Caption,
                CaptionDragRegions.CaptionRegion.Passthrough, CaptionDragRegions.CaptionRegion.Passthrough,
                CaptionDragRegions.CaptionRegion.Maximize, CaptionDragRegions.CaptionRegion.Maximize,
            ],
            order);
    }
}
