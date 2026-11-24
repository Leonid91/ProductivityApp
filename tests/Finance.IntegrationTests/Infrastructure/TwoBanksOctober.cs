using System.Globalization;
using System.Text;
using ProductivityApp.Finance.Model;
using ProductivityApp.Finance.Services;
using ProductivityApp.Finance.Testing;

namespace ProductivityApp.Finance.IntegrationTests.Infrastructure;

/// <summary>
/// October 2025 at two banks (invented): the Caisse d'Epargne's PDF of two accounts in euros and the National Bank's
/// chequing history in Canadian dollars; euro rates for every day of the month (1.60 plus a thousandth for each day).
/// </summary>
public static class TwoBanksOctober
{
    public static readonly CaisseEpargneGenerator.Statement Caisse = CaisseEpargneGenerator.Sample(2025, 10, 30);
    public static readonly CaisseEpargneGenerator.Account Current = Caisse.Accounts[0];
    public static readonly (DateOnly From, DateOnly To) October = (new(2025, 10, 1), new(2025, 10, 31));

    private static readonly Lazy<byte[]> CaissePdf = new(() => CaisseEpargneGenerator.Pdf(Caisse));
    private static readonly Lazy<byte[]> NationalPdf = new(() => BncGenerator.Pdf(BncGenerator.Sample(2025, 10, 4), BncGenerator.BncAccount.Chequing));

    /// <summary>Both banks' statements imported.</summary>
    public static async Task ImportBothBanksAsync(TestStore store)
    {
        var caisse = store.AddBank("Caisse d'Epargne", BankingSystem.France, "EUR", "France");
        store.CommitAll(await store.ParseAllAsync("releve.pdf", CaissePdf.Value), "EUR", caisse);
        await store.ImportAsync("chequing.pdf", NationalPdf.Value);
    }

    public static decimal CadPerEuro(DateOnly day) => 1.60m + day.Day / 1000m;

    public static decimal InCad(decimal euros, DateOnly day) => Math.Round(euros * CadPerEuro(day), 2, MidpointRounding.AwayFromZero);

    /// <summary>The month's euro rates saved, both banks imported, October reported in Canadian dollars.</summary>
    public static async Task<PeriodReport> InCadAsync(TestStore store)
    {
        var rates = new ExchangeRates(store.Db);
        var csv = "Date,CAD,\n" + string.Join("\n", Enumerable.Range(1, 31).Reverse().Select(d => $"2025-10-{d:D2},{CadPerEuro(new DateOnly(2025, 10, d)).ToString(CultureInfo.InvariantCulture)},"));
        rates.Save(RateSource.Ecb, Encoding.UTF8.GetBytes(csv));
        await ImportBothBanksAsync(store);
        return store.Summaries.Report(October.From, October.To, Granularity.Month, "CAD", rates);
    }

    /// <summary>Both banks imported without any rate, October reported in <paramref name="currency"/>.</summary>
    public static async Task<PeriodReport> WithoutRatesAsync(TestStore store, string currency)
    {
        await ImportBothBanksAsync(store);
        return store.Summaries.Report(October.From, October.To, Granularity.Month, currency, new ExchangeRates(store.Db));
    }
}
