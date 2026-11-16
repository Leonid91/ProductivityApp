using ProductivityApp.Finance.Model;
using ProductivityApp.Finance.Parsing;
using ProductivityApp.Finance.Pdf;
using ProductivityApp.Finance.Text;
using static ProductivityApp.Finance.Testing.StatementGenerator;

namespace ProductivityApp.Finance.Tests;

public class StatementParserTests
{
    private static async Task<(Spec Spec, StatementParse Parse)> Read(Style style, int count = 34, int seed = 7, BankTemplate? template = null)
    {
        var spec = Sample(style, count: count, seed: seed);
        var pdf = Render(spec);
        var pages = await PdfReader.ReadAsync(pdf);
        return (spec, StatementParser.Parse(pages, template, "CAD"));
    }

    private static void AssertMatches(Spec spec, StatementParse p, bool periodPrinted = true)
    {
        Assert.Equal(VerificationStatus.Verified, p.Status);
        Assert.Equal(0m, p.Difference);
        Assert.Equal(spec.Opening, p.OpeningBalance);
        Assert.Equal(spec.Closing, p.ClosingBalance);
        Assert.Equal(spec.Transactions.Count, p.Rows.Count);
        for (var i = 0; i < spec.Transactions.Count; i++)
        {
            var expected = spec.Transactions[i];
            var row = p.Rows[i];
            Assert.Equal(expected.Date, row.Date);
            Assert.Equal(expected.Amount, row.Amount);
            Assert.StartsWith(TextNorm.Fold(expected.Label), TextNorm.Fold(row.Label));
            if (expected.SecondLine is not null) Assert.Contains(TextNorm.Fold(expected.SecondLine), TextNorm.Fold(row.Label));
        }
        if (!periodPrinted) return;
        Assert.Equal(spec.Start, p.PeriodStart);
        Assert.Equal(spec.End, p.PeriodEnd);
    }

    [Theory]
    [InlineData(7)]
    [InlineData(21)]
    public async Task French_debit_credit_statement_balances_to_the_cent(int seed)
    {
        var (spec, p) = await Read(Style.FrenchDebitCredit, seed: seed);
        AssertMatches(spec, p);
        Assert.Equal("fr", p.Language);
        Assert.Equal("EUR", p.Currency);
        Assert.Equal(AccountKind.Bank, p.Kind);
        Assert.Equal("8901", p.Last4);
        Assert.True(p.RowBalanceChecks > 0);
        Assert.Equal(0, p.RowBalanceMismatches);
    }

    [Fact]
    public async Task English_bank_statement_with_daily_balances()
    {
        var (spec, p) = await Read(Style.EnglishWithdrawalsDeposits);
        AssertMatches(spec, p);
        Assert.Equal("en", p.Language);
        Assert.Equal("CAD", p.Currency);
        Assert.Equal("8901", p.Last4);
    }

    [Fact]
    public async Task Credit_card_statement_reads_purchases_and_payments()
    {
        var (spec, p) = await Read(Style.EnglishCard);
        AssertMatches(spec, p);
        Assert.Equal(AccountKind.CreditCard, p.Kind);
        Assert.Equal("1234", p.Last4);
        Assert.Contains(p.Rows, r => r.Amount == 750.00m);
    }

    [Fact]
    public async Task Signed_amount_statement_with_value_dates()
    {
        var (spec, p) = await Read(Style.FrenchSignedAmount);
        AssertMatches(spec, p);
        Assert.Equal("5678", p.Last4);
    }

    [Fact]
    public async Task Long_statement_over_several_pages()
    {
        var (spec, p) = await Read(Style.EnglishWithdrawalsDeposits, count: 140, seed: 3);
        Assert.True(UglyToad.PdfPig.PdfDocument.Open(Render(spec)).NumberOfPages >= 3);
        AssertMatches(spec, p);
    }

    [Fact]
    public async Task Atypical_layout_needs_a_template_then_balances()
    {
        var (_, auto) = await Read(Style.Atypical);
        Assert.NotEqual(VerificationStatus.Verified, auto.Status);

        var template = new BankTemplate
        {
            Name = "Mutuelle Imaginaire",
            MatchText = "Mutuelle Imaginaire",
            Columns =
            [
                new TemplateColumn { Role = ColumnRole.Description, Left = 0.05, Right = 0.55 },
                new TemplateColumn { Role = ColumnRole.Debit, Left = 0.55, Right = 0.73 },
                new TemplateColumn { Role = ColumnRole.Credit, Left = 0.73, Right = 0.86 },
                new TemplateColumn { Role = ColumnRole.Date, Left = 0.86, Right = 0.96 },
            ],
            DateOrder = DateOrder.DayMonth,
        };
        var (spec, p) = await Read(Style.Atypical, template: template);
        AssertMatches(spec, p, periodPrinted: false);
        Assert.StartsWith("Template", p.Method);
        // It prints no currency; its SEPA operations make it a euro statement, whatever the display currency.
        Assert.Equal("EUR", p.Currency);
        Assert.Equal("EUR", auto.Currency);
    }

    [Fact]
    public async Task Scanned_statement_is_read_by_windows_text_recognition()
    {
        Assert.True(PdfReader.OcrAvailable, "No Windows OCR language is installed on this machine.");
        var spec = Sample(Style.FrenchDebitCredit, count: 18, seed: 5);
        var scan = AsScan(Render(spec));
        var pages = await PdfReader.ReadAsync(scan);
        Assert.All(pages, pg => Assert.True(pg.FromOcr));
        var p = StatementParser.Parse(pages, null, "CAD");
        Assert.True(p.UsedOcr);
        Assert.Equal(VerificationStatus.Verified, p.Status);
        Assert.Equal(spec.Transactions.Count, p.Rows.Count);
        Assert.Equal(spec.Transactions.Select(t => t.Amount), p.Rows.Select(r => r.Amount));
    }
}
