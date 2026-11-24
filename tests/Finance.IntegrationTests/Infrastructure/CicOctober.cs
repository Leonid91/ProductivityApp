using System.Text;
using ProductivityApp.Finance.Model;
using ProductivityApp.Finance.Services;
using ProductivityApp.Finance.Testing;

namespace ProductivityApp.Finance.IntegrationTests.Infrastructure;

/// <summary>
/// The CIC's statement of October 2025 (a current account and a Livret A, invented), imported by a person whose own
/// currency is the Canadian dollar, with the European Central Bank's rates of those days (invented: 1.5 Canadian
/// dollars plus a thousandth for each day of the month).
/// </summary>
public static class CicOctober
{
    public static readonly CicGenerator.Statement October = CicGenerator.Sample(2025, 10, 5);
    public static readonly CicGenerator.Account Current = October.Accounts[0];
    public static readonly CicGenerator.Account LivretA = October.Accounts[1];

    /// <summary>The monthly PDF, drawn once.</summary>
    public static byte[] MonthlyPdf => PdfOnce.Value;

    private static readonly Lazy<byte[]> PdfOnce = new(() => CicGenerator.Pdf(October));

    public static long AddBank(TestStore store) => store.AddBank("CIC", BankingSystem.France, "EUR", "France");

    /// <summary>The European Central Bank's euro rate in Canadian dollars.</summary>
    public static decimal CadPerEuro(DateOnly day) => 1.5m + day.Day / 1000m;

    /// <summary>The day whose rate applies: the bank publishes none on Saturdays and Sundays, so the Friday before.</summary>
    public static DateOnly RateDay(DateOnly day) => day.AddDays(-DaysBackToARateDay[day.DayOfWeek]);

    private static readonly Dictionary<DayOfWeek, int> DaysBackToARateDay = new()
    {
        [DayOfWeek.Monday] = 0, [DayOfWeek.Tuesday] = 0, [DayOfWeek.Wednesday] = 0, [DayOfWeek.Thursday] = 0, [DayOfWeek.Friday] = 0,
        [DayOfWeek.Saturday] = 1, [DayOfWeek.Sunday] = 2,
    };

    /// <summary>Units of each currency per euro, by day: Canadian dollars as <see cref="CadPerEuro"/>, US dollars 1.1.</summary>
    private static readonly Dictionary<string, Func<DateOnly, decimal>> PerEuro = new() { ["CAD"] = CadPerEuro, ["USD"] = _ => 1.1m };

    public static decimal InCad(decimal euros, DateOnly day) => Math.Round(euros * CadPerEuro(RateDay(day)), 2, MidpointRounding.AwayFromZero);

    /// <summary>The rates saved in the store's database, from the European Central Bank's file.</summary>
    public static ExchangeRates SaveRates(TestStore store)
    {
        var rates = new ExchangeRates(store.Db, () => "ecb");
        var history = RateFiles.EuropeanCentralBankHistory(new(2025, 9, 20), new(2025, 11, 5), ["USD", "CAD"], (c, d) => PerEuro[c](d));
        rates.Save(RateSource.Ecb, Encoding.UTF8.GetBytes(history));
        return rates;
    }

    /// <summary>The monthly PDF imported at the CIC, both accounts in the currency the statement says.</summary>
    public static async Task<long> ImportAsync(TestStore store)
    {
        var cic = AddBank(store);
        store.CommitAll(await store.ParseAllAsync("Releve CIC.pdf", MonthlyPdf), bank: cic);
        return cic;
    }

    /// <summary>The rates saved, the monthly PDF imported, operations converted with those rates: October reported in <paramref name="currency"/>.</summary>
    public static async Task<PeriodReport> ReportedAsync(TestStore store, string currency)
    {
        var rates = SaveRates(store);
        await ImportAsync(store);
        store.ConvertWith(rates);
        return store.Summaries.Report(October.Start, October.End, Granularity.Month, currency, rates);
    }
}
