using ProductivityApp.Finance.Reports;

namespace ProductivityApp.Finance.UnitTests.Reports;

public class PaletteTests
{
    [Fact]
    public void Series_FortySlices_GivesFortyDistinctColoursWrittenAsHex()
    {
        var colours = Enumerable.Range(0, 40).Select(Palette.Series).ToList();

        Assert.Equal(40, colours.Distinct().Count());
        Assert.All(colours, c => Assert.Matches("^#[0-9a-f]{6}$", c));
    }
}
