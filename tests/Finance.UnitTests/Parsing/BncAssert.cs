using ProductivityApp.Finance.Parsing;
using ProductivityApp.Finance.Testing;
using ProductivityApp.Finance.Text;

namespace ProductivityApp.Finance.UnitTests.Parsing;

/// <summary>Custom assertion: a reading of a National Bank of Canada history holds every line of it, as written.</summary>
internal static class BncAssert
{
    public static void SameLines(List<BncGenerator.Line> expected, StatementParse actual)
    {
        var nonZero = expected.Where(l => l.Amount != 0).ToList();
        Assert.Equal(nonZero.Select(l => (l.Date, l.Amount, TextNorm.Fold(l.Description), TextNorm.Fold(l.Category))),
            actual.Rows.Select(r => (r.Date, r.Amount, TextNorm.Fold(r.Label), TextNorm.Fold(r.BankCategory))));
        Assert.Equal(("National Bank of Canada", "bnc", "CAD", true), (actual.Bank, actual.Profile, actual.Currency, actual.NewestFirst));
    }
}
