using ProductivityApp.Finance.Services;

namespace ProductivityApp.Finance.UnitTests.Services;

public class FormattingTests
{
    [Theory]
    [InlineData("2025-10-01", "2025-10-31", Granularity.Month, "October 2025")]
    [InlineData("2025-10-01", "2025-10-15", Granularity.Month, "October, from Wednesday 1 to Wednesday 15, 2025")]
    [InlineData("2025-01-01", "2025-12-31", Granularity.Year, "2025")]
    [InlineData("2025-03-01", "2025-12-31", Granularity.Year, "2025, from Saturday, March 1 to Wednesday, December 31")]
    [InlineData("2023-01-01", "2025-12-31", Granularity.Range, "From Sunday, January 1, 2023 to Wednesday, December 31, 2025")]
    public void PeriodTitle_WholeOrPartOfAPeriod_SaysWhatItCovers(string from, string to, Granularity granularity, string title)
    {
        var result = Formatting.PeriodTitle(DateOnly.Parse(from, System.Globalization.CultureInfo.InvariantCulture), DateOnly.Parse(to, System.Globalization.CultureInfo.InvariantCulture), granularity);

        Assert.Equal(title, result);
    }

    [Theory]
    [InlineData("DD month YYYY", "03 October 2025")]
    [InlineData("MM/DD/YYYY", "10/03/2025")]
    public void FormatDate_ChosenDateFormat_WritesTheDateThatWay(string format, string written)
    {
        var result = Formatting.FormatDate(new DateOnly(2025, 10, 3), format);

        Assert.Equal(written, result);
    }
}
