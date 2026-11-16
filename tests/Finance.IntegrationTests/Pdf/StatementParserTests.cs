using ProductivityApp.Finance.Model;
using ProductivityApp.Finance.Parsing;
using ProductivityApp.Finance.Pdf;
using ProductivityApp.Finance.Text;
using static ProductivityApp.Finance.Testing.StatementGenerator;

namespace ProductivityApp.Finance.IntegrationTests.Pdf;

/// <summary>Statements printed as PDF (made here with invented operations), read by the PDF reader and the statement parser together.</summary>
public class StatementParserTests
{
    private static async Task<(Spec Spec, StatementParse Parse)> ReadAsync(Style style, int count = 34, int seed = 7, BankTemplate? template = null)
    {
        var spec = Sample(style, count: count, seed: seed);
        var pages = await PdfReader.ReadAsync(Render(spec), TestContext.Current.CancellationToken);
        return (spec, StatementParser.Parse(pages, template, "CAD"));
    }

    private static void AssertSameOperations(Spec spec, StatementParse parse)
    {
        Assert.Equal(spec.Transactions.Select(t => (t.Date, t.Amount)), parse.Rows.Select(r => (r.Date, r.Amount)));
        Assert.All(spec.Transactions.Zip(parse.Rows), x => Assert.StartsWith(TextNorm.Fold(x.First.Label), TextNorm.Fold(x.Second.Label)));
        Assert.All(spec.Transactions.Zip(parse.Rows).Where(x => x.First.SecondLine is not null),
            x => Assert.Contains(TextNorm.Fold(x.First.SecondLine!), TextNorm.Fold(x.Second.Label)));
    }

    private static void AssertBalanced(Spec spec, StatementParse parse)
    {
        Assert.Equal((VerificationStatus.Verified, 0m), (parse.Status, parse.Difference));
        Assert.Equal((spec.Opening, spec.Closing), (parse.OpeningBalance, parse.ClosingBalance));
    }

    public static TheoryData<Style, int> PrintedStyles => new()
    {
        { Style.FrenchDebitCredit, 7 },
        { Style.FrenchDebitCredit, 21 },
        { Style.EnglishWithdrawalsDeposits, 7 },
        { Style.EnglishCard, 7 },
        { Style.FrenchSignedAmount, 7 },
    };

    [Theory]
    [MemberData(nameof(PrintedStyles))]
    public async Task Parse_StatementInAnyPrintedLayout_ReadsEveryOperationAndItsPeriod(Style style, int seed)
    {
        var (spec, parse) = await ReadAsync(style, seed: seed);

        AssertSameOperations(spec, parse);
        Assert.Equal((spec.Start, spec.End), (parse.PeriodStart, parse.PeriodEnd));
    }

    [Theory]
    [MemberData(nameof(PrintedStyles))]
    public async Task Parse_StatementInAnyPrintedLayout_BalancesToTheCent(Style style, int seed)
    {
        var (spec, parse) = await ReadAsync(style, seed: seed);

        AssertBalanced(spec, parse);
    }

    [Theory]
    [InlineData(Style.FrenchDebitCredit, "EUR", AccountKind.Bank, "8901")]
    [InlineData(Style.EnglishWithdrawalsDeposits, "CAD", AccountKind.Bank, "8901")]
    [InlineData(Style.EnglishCard, "CAD", AccountKind.CreditCard, "1234")]
    [InlineData(Style.FrenchSignedAmount, "EUR", AccountKind.Bank, "5678")]
    public async Task Parse_StatementInAnyPrintedLayout_FindsItsCurrencyKindAndAccountNumber(Style style, string currency, AccountKind kind, string last4)
    {
        var (_, parse) = await ReadAsync(style);

        Assert.Equal((currency, kind, last4), (parse.Currency, parse.Kind, parse.Last4));
    }

    [Fact]
    public async Task Parse_FrenchStatementWithARunningBalance_ChecksEveryLineAgainstIt()
    {
        var (_, parse) = await ReadAsync(Style.FrenchDebitCredit);

        Assert.True(parse.RowBalanceChecks > 0);
        Assert.Equal(0, parse.RowBalanceMismatches);
    }

    [Fact]
    public async Task Parse_CardStatement_ReadsThePaymentAsMoneyIn()
    {
        var (_, parse) = await ReadAsync(Style.EnglishCard);

        Assert.Contains(parse.Rows, r => r.Amount == 750.00m);
    }

    [Fact]
    public async Task Parse_StatementOverSeveralPages_ReadsAndBalancesEveryPage()
    {
        var (spec, parse) = await ReadAsync(Style.EnglishWithdrawalsDeposits, count: 140, seed: 3);

        Assert.True(UglyToad.PdfPig.PdfDocument.Open(Render(spec)).NumberOfPages >= 3);
        AssertSameOperations(spec, parse);
        AssertBalanced(spec, parse);
    }

    [Fact]
    public async Task Parse_LayoutNoRuleReads_IsNotVerified()
    {
        var (_, parse) = await ReadAsync(Style.Atypical);

        Assert.NotEqual(VerificationStatus.Verified, parse.Status);
    }

    private static readonly BankTemplate Mutuelle = new()
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

    [Fact]
    public async Task Parse_LayoutNoRuleReadsWithATemplateOfItsColumns_ReadsAndBalancesIt()
    {
        var (spec, parse) = await ReadAsync(Style.Atypical, template: Mutuelle);

        AssertSameOperations(spec, parse);
        AssertBalanced(spec, parse);
        Assert.StartsWith("Template", parse.Method);
    }

    [Fact]
    public async Task Parse_StatementPrintingNoCurrencyWithSepaOperations_IsInEuros()
    {
        var (_, parse) = await ReadAsync(Style.Atypical, template: Mutuelle);

        Assert.Equal("EUR", parse.Currency);
    }

    [Fact]
    public async Task Parse_ScannedStatement_IsReadByTextRecognitionAndBalances()
    {
        Assert.SkipUnless(PdfReader.OcrAvailable, "No Windows text-recognition language is installed on this machine.");
        var spec = Sample(Style.FrenchDebitCredit, count: 18, seed: 5);
        var pages = await PdfReader.ReadAsync(AsScan(Render(spec)), TestContext.Current.CancellationToken);

        var parse = StatementParser.Parse(pages, null, "CAD");

        Assert.All(pages, p => Assert.True(p.FromOcr));
        Assert.Equal((true, VerificationStatus.Verified), (parse.UsedOcr, parse.Status));
        Assert.Equal(spec.Transactions.Select(t => t.Amount), parse.Rows.Select(r => r.Amount));
    }
}
