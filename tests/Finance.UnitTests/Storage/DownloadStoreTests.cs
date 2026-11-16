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

    public static TheoryData<string, int?, byte[], string> DownloadedFiles => new()
    {
        { "rates", null, [0x50, 0x4B, 3, 4], "rates.zip" },
        { "holidays", 2026, Encoding.UTF8.GetBytes("{}"), "holidays-2026.json" },
        { "holidays", null, Encoding.UTF8.GetBytes("BEGIN:VCALENDAR"), "holidays.ics" },
        { "rates", null, Encoding.UTF8.GetBytes("Date,USD"), "rates.csv" },
    };

    [Theory]
    [MemberData(nameof(DownloadedFiles))]
    public void FileName_DownloadedContent_IsNamedByWhatItHoldsAndItsYear(string what, int? year, byte[] content, string fileName)
    {
        var result = DownloadStore.FileName(what, year, content);

        Assert.Equal(fileName, result);
    }
}
