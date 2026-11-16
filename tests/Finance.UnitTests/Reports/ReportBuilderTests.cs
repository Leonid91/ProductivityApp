using ProductivityApp.Finance.Reports;

namespace ProductivityApp.Finance.UnitTests.Reports;

public class ReportBuilderTests
{
    [Fact]
    public void ViewBox_SvgDrawing_ReturnsItsWidthAndHeight()
    {
        const string drawing = """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1000 437.5" font-family="Inter"><rect x="10" y="10" width="980" height="400"/></svg>""";

        var result = ReportBuilder.ViewBox(drawing);

        Assert.Equal((1000d, 437.5d), result);
    }
}
