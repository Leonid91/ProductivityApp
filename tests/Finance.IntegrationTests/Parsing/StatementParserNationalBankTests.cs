using System.Collections.Concurrent;
using ProductivityApp.Finance.Model;
using ProductivityApp.Finance.Parsing;
using ProductivityApp.Finance.Pdf;
using ProductivityApp.Finance.Text;
using static ProductivityApp.Finance.IntegrationTests.Infrastructure.NationalBankSeptember;
using static ProductivityApp.Finance.Testing.BncGenerator;

namespace ProductivityApp.Finance.IntegrationTests.Parsing;

/// <summary>
/// The National Bank of Canada's PDF histories (invented data in its layouts), read by the PDF reader and the statement
/// parser. Each history is read once; every test reads that reading.
/// </summary>
public class StatementParserNationalBankTests
{
    private static readonly ConcurrentDictionary<BncAccount, Lazy<Task<StatementParse>>> Read = new();

    private static Task<StatementParse> ReadPdfAsync(BncAccount account) => Read.GetOrAdd(account, a => new Lazy<Task<StatementParse>>(async () =>
        StatementParser.Parse(await PdfReader.ReadAsync(PdfOf(a), CancellationToken.None), null, "CAD"))).Value;

    [Theory]
    [InlineData(BncAccount.Card)]
    [InlineData(BncAccount.Chequing)]
    [InlineData(BncAccount.Savings)]
    public async Task Parse_PdfHistory_ReadsEveryLineAsWrittenNewestFirst(BncAccount account)
    {
        var result = await ReadPdfAsync(account);

        var expected = September.Of(account).Where(l => l.Amount != 0).ToList();
        Assert.Equal(expected.Select(l => (l.Date, l.Amount)), result.Rows.Select(r => (r.Date, r.Amount)));
        Assert.Equal(expected.Select(l => TextNorm.Fold(l.Description)), result.Rows.Select(r => TextNorm.Fold(r.Label)));
        Assert.Equal(expected.Select(l => TextNorm.Fold(l.Category)), result.Rows.Select(r => TextNorm.Fold(r.BankCategory)));
        Assert.Equal(("National Bank of Canada", "bnc", "CAD", true), (result.Bank, result.Profile, result.Currency, result.NewestFirst));
    }

    [Theory]
    [InlineData(BncAccount.Card, AccountKind.CreditCard, VerificationStatus.NoBalances)]
    [InlineData(BncAccount.Chequing, AccountKind.Bank, VerificationStatus.Verified)]
    [InlineData(BncAccount.Savings, AccountKind.Savings, VerificationStatus.Verified)]
    public async Task Parse_PdfHistory_ReturnsTheKindOfAccountAndTheBalanceCheck(BncAccount account, AccountKind kind, VerificationStatus status)
    {
        var result = await ReadPdfAsync(account);

        Assert.Equal((kind, status), (result.Kind, result.Status));
    }

    [Fact]
    public async Task Parse_CardPdfHistory_ReadsTheCardsLastFourDigits()
    {
        var result = await ReadPdfAsync(BncAccount.Card);

        Assert.Equal("1234", result.Last4);
    }

    [Fact]
    public async Task Parse_ChequingPdfWithWaivedFees_LeavesTheLinesOfZeroOut()
    {
        var result = await ReadPdfAsync(BncAccount.Chequing);

        Assert.Equal(2, result.ZeroRowsSkipped);
    }
}
