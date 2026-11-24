using System.Text;
using ProductivityApp.Finance.Storage;

namespace ProductivityApp.Finance.UnitTests.Storage;

public class DownloadStoreTests
{
    [Theory]
    [InlineData("Canada (Federal)", "canada-federal")]
    [InlineData("Québec", "quebec")]
    [InlineData("United States (Federal)", "united-states-federal")]
    [InlineData("US Federal Reserve", "us-federal-reserve")]
    [InlineData("  Île-de-France! ", "ile-de-france")]
    [InlineData("???", "source")]
    public void Slug_PlaceOrBankName_ReturnsAPlainFolderName(string name, string slug)
    {
        var result = DownloadStore.Slug(name);

        Assert.Equal(slug, result);
    }

    [Theory]
    [InlineData("rates", null, "PK\u0003\u0004", "rates.zip")]   // the first bytes of a zip file
    [InlineData("holidays", 2026, "{}", "holidays-2026.json")]
    [InlineData("holidays", null, "BEGIN:VCALENDAR", "holidays.ics")]
    [InlineData("rates", null, "Date,USD", "rates.csv")]
    public void FileName_DownloadedContent_IsNamedByWhatItHoldsAndItsYear(string what, int? year, string content, string fileName)
    {
        var result = DownloadStore.FileName(what, year, Encoding.Latin1.GetBytes(content));

        Assert.Equal(fileName, result);
    }
}
