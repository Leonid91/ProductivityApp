using ProductivityApp.Finance.IntegrationTests.Infrastructure;
using ProductivityApp.Finance.Model;
using ProductivityApp.Finance.Pdf;
using ProductivityApp.Finance.Reports;
using ProductivityApp.Finance.Services;

namespace ProductivityApp.Finance.IntegrationTests.Reports;

/// <summary>The PDF report of a period, read back with the app's own PDF reader: what it says, and only the views chosen.</summary>
public sealed class ReportBuilderTests : IDisposable
{
    private static readonly DateTime Made = new(2026, 4, 1);

    private readonly TestStore _store = new();

    public ReportBuilderTests()
    {
        var chequing = _store.AddAccount("Chequing");
        var savings = _store.AddAccount("Savings", kind: AccountKind.Savings);
        _store.Enter(chequing, "2026-03-03", -86.12m, "Sobeys", "groceries");
        _store.Enter(chequing, "2026-03-09", -131.06m, "Hydro One", "housing");
        _store.Enter(chequing, "2026-03-15", 2140.55m, "Example Corp", "salary");
        _store.Enter(chequing, "2026-03-20", -250m, "To savings", Categories.Transfers);
        _store.Enter(savings, "2026-03-20", 250m, "From chequing", Categories.Transfers);
        TransferMatcher.Link(_store.Db);
    }

    public void Dispose() => _store.Dispose();

    private PeriodReport March() => _store.Summaries.Report(new(2026, 3, 1), new(2026, 3, 31), Granularity.Month, "CAD", new ExchangeRates(_store.Db));

    private static async Task<string> TextOf(byte[] pdf) =>
        string.Join(' ', (await PdfReader.ReadAsync(pdf, TestContext.Current.CancellationToken)).SelectMany(p => p.Words).Select(w => w.Text));

    [Fact]
    public async Task Build_MonthSummary_SaysThePeriodAndItsTotals()
    {
        var summary = _store.Summarize(new(2026, 3, 1), new(2026, 3, 31));

        var text = await TextOf(ReportBuilder.Build(summary, "DD month YYYY", Made));

        Assert.Contains("March", text);
        Assert.Contains("Expenses per category", text);
        Assert.Contains(Formatting.Money(217.18m, "CAD").Replace("−", ""), text);
        Assert.Contains(Formatting.Money(2140.55m, "CAD").Replace("−", ""), text);
    }

    [Fact]
    public void FileName_MonthSummary_IsNamedAfterTheMonth()
    {
        var summary = _store.Summarize(new(2026, 3, 1), new(2026, 3, 31));

        Assert.Equal("Finances 2026-03.pdf", ReportBuilder.FileName(summary));
    }

    [Fact]
    public async Task Build_EveryViewByDefault_HoldsStatementsAnalyticsAndTransfersButNotTheSummary()
    {
        var text = await TextOf(ReportBuilder.Build(March(), "dd MMMM yyyy", Made, new ReportBuilder.Options()));

        Assert.Contains("Brand Name", text);
        Assert.Contains("Expenses per category", text);
        Assert.Contains("Transfers over time", text);
        Assert.DoesNotContain("Summary", text);
    }

    public static TheoryData<ReportBuilder.Parts, string[], string[]> ChosenViews => new()
    {
        { new(Summary: true, Statements: false, Analytics: false, Transfers: false), ["Summary", "Balance"], ["Brand Name", "Expenses per category", "Transfers over time"] },
        { new(Summary: false, Statements: true, Analytics: false, Transfers: false), ["Brand Name"], ["Expenses per category", "Transfers over time"] },
        { new(Summary: false, Statements: false, Analytics: true, Transfers: false), ["Expenses per category"], ["Brand Name", "Transfers over time"] },
        { new(Summary: false, Statements: false, Analytics: false, Transfers: true), ["Transfers over time", "Inside a bank"], ["Brand Name", "Expenses per category"] },
    };

    [Theory]
    [MemberData(nameof(ChosenViews))]
    public async Task Build_ViewsChosen_HoldsThoseViewsOnly(ReportBuilder.Parts parts, string[] present, string[] absent)
    {
        var text = await TextOf(ReportBuilder.Build(March(), "dd MMMM yyyy", Made, new ReportBuilder.Options(), parts));

        Assert.All(present, phrase => Assert.Contains(phrase, text, StringComparison.Ordinal));
        Assert.All(absent, phrase => Assert.DoesNotContain(phrase, text, StringComparison.Ordinal));
    }

    [Fact]
    public void Build_TransfersWithTheDrawingOfTheScreen_EmbedsTheDrawingAsAPicture()
    {
        var parts = new ReportBuilder.Parts(Summary: false, Statements: false, Analytics: false, Transfers: true);
        const string drawing = """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1000 437.5" font-family="Inter"><rect x="10" y="10" width="980" height="400" fill="#2468c0"/><text x="20" y="40">Lane</text></svg>""";
        var report = March();
        Assert.NotEmpty(report.Global.Timeline.Lanes);

        var plain = ReportBuilder.Build(report, "dd MMMM yyyy", Made, new ReportBuilder.Options(), parts);
        var drawn = ReportBuilder.Build(report, "dd MMMM yyyy", Made, new ReportBuilder.Options(), parts, drawing);

        Assert.True(drawn.Length > plain.Length + 200, $"{plain.Length} then {drawn.Length} bytes");
    }

    [Fact]
    public void Build_NoViewChosen_Throws() =>
        Assert.Throws<ArgumentException>(() => ReportBuilder.Build(March(), "dd MMMM yyyy", Made, new ReportBuilder.Options(), new ReportBuilder.Parts(false, false, false, false)));
}
