using ProductivityApp.Finance.Pdf;

namespace ProductivityApp.Finance.UnitTests.Pdf;

/// <summary>The PDF reader's pure helpers (reading real documents: Finance.IntegrationTests).</summary>
public class PdfReaderTests
{
    [Fact]
    public void WithHeight_WordOfNoSize_TakesTheUsualSizeOfTextAroundItsBaseline()
    {
        var word = PdfReader.WithHeight("Balance", 10, 50, 100, 0);

        Assert.True(word.Height > 5, $"height {word.Height}");
        Assert.True(word.Top < 100, $"top {word.Top}");
        Assert.True(word.Bottom > 100, $"bottom {word.Bottom}");
    }
}
