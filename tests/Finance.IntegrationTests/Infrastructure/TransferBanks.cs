using System.Globalization;
using System.Text;
using ProductivityApp.Finance.Model;
using ProductivityApp.Finance.Services;
using ProductivityApp.Finance.Testing;
using static ProductivityApp.Finance.Testing.StatementGenerator;

namespace ProductivityApp.Finance.IntegrationTests.Infrastructure;

/// <summary>
/// Money moved between the person's accounts at several banks, in one currency or two (invented): Northwind Bank and
/// the National Bank of Canada in Canadian dollars, Banque Exemple in euros, all in the database of
/// <paramref name="store"/>; the statements each bank prints of it, and the reports of a month of 2026.
/// </summary>
public sealed class TransferBanks(TestStore store)
{
    public TestStore Store => store;
    public long Northwind { get; } = store.AddBank("Northwind Bank", country: "Canada");
    public long National { get; } = store.DefaultBank;
    public long Exemple { get; } = store.AddBank("Banque Exemple", BankingSystem.France, "EUR", "France");

    public static DateOnly D(int month, int day) => new(2026, month, day);

    public static Spec Statement(string bank, List<FakeTransaction> txs, int month = 10, Style style = Style.EnglishWithdrawalsDeposits, string currency = "CAD") =>
        new(style, bank, D(month, 1), D(month, DateTime.DaysInMonth(2026, month)), 1000m, txs, currency);

    public static byte[] Csv(IEnumerable<FakeTransaction> txs) =>
        Encoding.UTF8.GetBytes("Date,Description,Amount\n" + string.Concat(txs.Select(t => $"{t.Date:yyyy-MM-dd},\"{t.Label}\",{t.Amount.ToString("0.00", CultureInfo.InvariantCulture)}\n")));

    /// <summary>A statement saved into a new account <paramref name="accountName"/> at <paramref name="bank"/>.</summary>
    public async Task<long> ImportAsync(Spec spec, long bank, string accountName, AccountKind kind = AccountKind.Bank) =>
        (await store.ImportAtAsync(bank, FileName(spec), Render(spec), spec.Currency, kind, accountName)).AccountId;

    /// <summary>A statement saved into the account <paramref name="account"/>.</summary>
    public async Task<long> ImportIntoAsync(Spec spec, long account) =>
        (await store.ImportIntoAsync(account, FileName(spec), Render(spec), spec.Currency)).AccountId;

    private static string FileName(Spec spec) => $"{spec.BankName} {spec.Start:yyyy-MM}.pdf";

    /// <summary>Invented rates in the European Central Bank's layout (units per euro), newest first; also used to pair halves in two currencies.</summary>
    public ExchangeRates Rates(string csv = "Date,CAD,\n2026-10-12,1.6100,\n2026-10-09,1.6000,\n2026-10-08,1.6000,\n2026-10-07,1.5900,\n")
    {
        var rates = new ExchangeRates(store.Db);
        rates.Save(RateSource.Ecb, Encoding.UTF8.GetBytes(csv));
        store.ConvertWith(rates);
        return rates;
    }

    /// <summary>A month of 2026 reported in <paramref name="currency"/>, the invented rates saved first.</summary>
    public PeriodReport Month(int month, string currency = "CAD") => Month(month, Rates(), currency);

    /// <summary>A month of 2026 reported in <paramref name="currency"/> with <paramref name="rates"/>.</summary>
    public PeriodReport Month(int month, ExchangeRates rates, string currency = "CAD") =>
        store.Summaries.Report(D(month, 1), D(month, DateTime.DaysInMonth(2026, month)), Granularity.Month, currency, rates);

    // ------------------------------------------------------------------------------------------------ one currency, two banks

    public static readonly Spec NorthwindOctober = Statement("Northwind Bank",
    [
        new(D(10, 3), "METRO #123 MONTREAL QC", -52.10m),
        new(D(10, 15), "PAYROLL DEPOSIT EXAMPLE CORP", 2100m),
        new(D(10, 20), "ONLINE TRANSFER TO NATIONAL BANK", -500m),
    ]);

    public static readonly Spec NationalOctober = Statement("National Bank of Canada",
    [
        new(D(10, 21), "ONLINE TRANSFER FROM NORTHWIND BANK", 500m),
        new(D(10, 25), "LIBRAIRIE EXEMPLE", -38.20m),
    ]);

    /// <summary>In November National sends 300 back to Northwind.</summary>
    public static readonly Spec NationalNovember = Statement("National Bank of Canada",
    [
        new(D(11, 4), "ONLINE TRANSFER TO NORTHWIND BANK", -300m),
        new(D(11, 6), "CAFE EXEMPLE MONTREAL", -4.75m),
    ], 11);

    public async Task<(long Northwind, long National)> NorthwindThenNationalAsync()
    {
        var northwind = await ImportAsync(NorthwindOctober, Northwind, "Northwind chequing");
        var national = await ImportAsync(NationalOctober, National, "National chequing");
        await ImportIntoAsync(NationalNovember, national);
        return (northwind, national);
    }

    public async Task<(long Northwind, long National)> NationalThenNorthwindAsync()
    {
        var national = await ImportAsync(NationalOctober, National, "National chequing");
        await ImportIntoAsync(NationalNovember, national);
        var northwind = await ImportAsync(NorthwindOctober, Northwind, "Northwind chequing");
        return (northwind, national);
    }

    // ------------------------------------------------------------------------------------------------ two currencies

    /// <summary>500 EUR sent from the French bank, 790.40 CAD received in Canada the day after (1.6000 that day, 1.2% kept by the banks).</summary>
    public async Task<(long Euro, long Cad)> TwoCurrenciesAsync(decimal received = 790.40m, int sentDay = 8)
    {
        var euro = await ImportAsync(Statement("Banque Exemple", [new(D(10, 9), "CB BOULANGERIE PAUL", -12.40m), new(D(10, sentDay), "VIR SEPA EMIS NORTHWIND BANK", -500m)],
            style: Style.FrenchDebitCredit, currency: "EUR"), Exemple, "Compte courant");
        var cad = await ImportAsync(Statement("Northwind Bank", [new(D(10, sentDay + 1), "WIRE TRANSFER RECEIVED BANQUE EXEMPLE", received), new(D(10, 15), "PAYROLL DEPOSIT EXAMPLE CORP", 2100m)]),
            Northwind, "Northwind chequing");
        return (euro, cad);
    }

    // ------------------------------------------------------------------------------------------------ four banks, Wise between them

    /// <summary>The statements of <see cref="FourBanksGenerator"/>, each into a new account at its bank, with 1 EUR = 1.60 CAD = 1.10 USD every day.</summary>
    public async Task<(ExchangeRates Rates, Dictionary<string, long> Accounts)> FourBanksAsync()
    {
        // Every day from 31 October back to 25 September (37 days), newest first.
        var rates = Rates("Date,CAD,USD,\n" + string.Concat(Enumerable.Range(0, 37).Select(i => $"{D(10, 31).AddDays(-i):yyyy-MM-dd},1.6000,1.1000,\n")));
        var statements = FourBanksGenerator.Statements();
        // Each bank added once, in the order its first statement comes.
        var banks = statements.DistinctBy(f => f.Bank).ToDictionary(f => f.Bank, f => store.AddBank(f.Bank, f.System, f.Currency, f.Country));
        // Each statement saved after the one before it, into a new account at its bank.
        var accounts = await statements.Aggregate(Task.FromResult(new Dictionary<string, long>()), async (saved, f) =>
        {
            var done = await saved;
            done[f.Account] = (await store.ImportAtAsync(banks[f.Bank], f.FileName, f.Bytes, f.Currency, f.Kind, f.Account)).AccountId;
            return done;
        });
        return (rates, accounts);
    }
}
