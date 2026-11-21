using ProductivityApp.Finance.Pdf;

namespace ProductivityApp.Finance.UnitTests.Parsing;

public class LineBuilderTests
{
    // Words placed as a PDF whose fonts give no glyph boxes (Scotiabank's e-statements) gives them: each word's height
    // comes from the size it is printed at, around its baseline.
    private static Word At(string text, double left, double baseline, double size = 9) => PdfReader.WithHeight(text, left, left + text.Length * size * 0.5, baseline, size);

    [Fact]
    public void Build_WordsWithHeightsFromTheirPrintedSize_GroupsEachRowIntoOneLine()
    {
        var page = new PageWords
        {
            Number = 1, Width = 612, Height = 792,
            Words = [At("Jul", 73, 426.7), At("31", 87, 426.7), At("Purchase", 113, 426.7), At("64.18", 283, 426.7), At("1,140.19", 410, 426.7),
                At("Sobeys", 113, 435.6, 8), At("Brampton", 150, 435.6, 8),
                At("Aug", 73, 506.1), At("9", 93, 506.1), At("Interest", 113, 506.1), At(".42", 359, 506.1), At("1,140.61", 410, 506.1)],
        };

        var lines = LineBuilder.Build(page);

        Assert.Equal(["Jul 31 Purchase 64.18 1,140.19", "Sobeys Brampton", "Aug 9 Interest .42 1,140.61"], lines.Select(l => string.Join(' ', l.Words.Select(w => w.Text))));
    }

    [Fact]
    public void WithHeight_WordOfNoSize_TakesTheUsualSizeOfText()
    {
        var word = PdfReader.WithHeight("Balance", 10, 50, 100, 0);

        Assert.True(word.Height > 5 && word.Top < 100 && word.Bottom > 100);
    }
}
