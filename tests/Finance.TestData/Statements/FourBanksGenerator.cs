using System.Globalization;
using System.Text;
using ProductivityApp.Finance.Model;
using static ProductivityApp.Finance.Testing.StatementGenerator;

namespace ProductivityApp.Finance.Testing;

/// <summary>
/// Four banks in three currencies with Wise between them, October 2026 (fictitious): Northwind Bank in CAD (chequing,
/// savings, card), Banque Exemple in EUR (current account, Livret A), Contoso Bank in USD (checking, savings, card) and
/// Wise (a CAD, a EUR and a USD balance). Money is put on Wise from Canada and the United States, exchanged between its
/// currencies, sent on to France; France also sends euros to Canada directly; savings go to investments outside. The
/// amounts received in another currency are what the banks gave at about the rates of October 2026 (1 EUR = 1.60 CAD,
/// 1 USD = 1.42 CAD), less their margin.
/// </summary>
public static class FourBanksGenerator
{
    /// <summary>One file of one account: the account, its bank (country, banking system, currency), its type and the file.</summary>
    public sealed record Statement(string Account, string Bank, string Country, BankingSystem System, string Currency, AccountKind Kind, string FileName, byte[] Bytes);

    private static DateOnly D(int month, int day) => new(2026, month, day);

    private static Spec Pdf(string bank, List<FakeTransaction> txs, Style style = Style.EnglishWithdrawalsDeposits, string currency = "CAD") =>
        new(style, bank, D(10, 1), D(10, 31), 5000m, txs, currency);

    /// <summary>Operations as a CSV export: "Date,Description,Amount".</summary>
    public static byte[] Csv(IEnumerable<FakeTransaction> txs)
    {
        var sb = new StringBuilder("Date,Description,Amount\n");
        foreach (var t in txs) sb.Append(CultureInfo.InvariantCulture, $"{t.Date:yyyy-MM-dd},\"{t.Label}\",{t.Amount.ToString("0.00", CultureInfo.InvariantCulture)}\n");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static readonly Lazy<List<Statement>> Drawn = new(Draw);

    /// <summary>Every statement of the four banks, drawn once per test run (a new list of the same statements each call).</summary>
    public static List<Statement> Statements() => [.. Drawn.Value];

    private static List<Statement> Draw()
    {
        const string N = "Northwind Bank", E = "Banque Exemple", C = "Contoso Bank", W = "Wise";
        Statement Canada(string account, AccountKind kind, string file, byte[] bytes) => new(account, N, "Canada", BankingSystem.NorthAmerica, "CAD", kind, file, bytes);
        Statement France(string account, AccountKind kind, string file, byte[] bytes) => new(account, E, "France", BankingSystem.France, "EUR", kind, file, bytes);
        Statement States(string account, AccountKind kind, string file, byte[] bytes) => new(account, C, "United States", BankingSystem.NorthAmerica, "USD", kind, file, bytes);
        Statement Wise(string account, string currency, string file, byte[] bytes) => new(account, W, "Belgium", BankingSystem.France, currency, AccountKind.Bank, file, bytes);
        return
        [
            Canada("Northwind chequing", AccountKind.Bank, "Northwind chequing 2026-10.pdf", Render(Pdf(N,
            [
                new(D(10, 3), "PAYROLL DEPOSIT EXAMPLE CORP", 4200m),
                new(D(10, 5), "WISE PAYMENTS LTD", -2000m),
                new(D(10, 6), "ONLINE TRANSFER TO SAVINGS", -900m),
                new(D(10, 7), "CREDIT CARD PAYMENT NORTHWIND", -1240m),
                new(D(10, 10), "WIRE TRANSFER RECEIVED BANQUE EXEMPLE", 1296.26m),
                new(D(10, 11), "METRO #123 MONTREAL QC", -80m),
            ]))),
            Canada("Northwind savings", AccountKind.Savings, "Northwind savings 2026-10.pdf", Render(Pdf(N,
            [
                new(D(10, 6), "ONLINE TRANSFER FROM CHEQUING", 900m),
                new(D(10, 8), "TFSA CONTRIBUTION WEALTHSIMPLE", -1000m),
            ]))),
            Canada("Northwind card", AccountKind.CreditCard, "Northwind card 2026-10.pdf", Render(new Spec(Style.EnglishCard, N, D(10, 1), D(10, 31), 300m,
            [
                new(D(10, 7), "PAYMENT - THANK YOU", 1240m),
                new(D(10, 12), "NETFLIX.COM", -16.99m),
            ]))),
            France("Compte courant", AccountKind.Bank, "Banque Exemple compte courant 2026-10.pdf", Render(Pdf(E,
            [
                new(D(10, 2), "VIR SEPA RECU SALAIRE EXEMPLE SAS", 2500m),
                new(D(10, 6), "VIR LIVRET A", -600m),
                new(D(10, 8), "VIR SEPA RECU WISE EUROPE", 1228.22m),
                new(D(10, 9), "VIR SEPA EMIS NORTHWIND BANK", -820m),
                new(D(10, 11), "CB BOULANGERIE PAUL", -12.40m),
            ], Style.FrenchDebitCredit, "EUR"))),
            France("Livret A", AccountKind.Savings, "Banque Exemple livret A 2026-10.pdf", Render(Pdf(E, [new(D(10, 6), "VIR LIVRET A", 600m)], Style.FrenchDebitCredit, "EUR"))),
            States("Contoso checking", AccountKind.Bank, "Contoso checking 2026-10.pdf", Render(Pdf(C,
            [
                new(D(10, 3), "PAYROLL DEPOSIT EXAMPLE INC", 3000m),
                new(D(10, 5), "WISE US INC", -1500m),
                new(D(10, 6), "ONLINE TRANSFER TO SAVINGS", -700m),
                new(D(10, 7), "CREDIT CARD PAYMENT CONTOSO", -830m),
                new(D(10, 10), "AMAZON.COM", -45m),
            ], currency: "USD"))),
            States("Contoso savings", AccountKind.Savings, "Contoso savings 2026-10.pdf", Render(Pdf(C,
            [
                new(D(10, 6), "ONLINE TRANSFER FROM CHECKING", 700m),
                new(D(10, 8), "TFSA CONTRIBUTION WEALTHSIMPLE", -400m),
            ], currency: "USD"))),
            States("Contoso card", AccountKind.CreditCard, "Contoso card 2026-10.pdf", Render(new Spec(Style.EnglishCard, C, D(10, 1), D(10, 31), 200m,
            [
                new(D(10, 7), "PAYMENT - THANK YOU", 830m),
                new(D(10, 9), "UBER TRIP", -25m),
            ], "USD"))),
            // Wise: one balance per currency, exported as CSV.
            Wise("Wise CAD", "CAD", "Wise CAD 2026-10.csv", Csv(
            [
                new(D(10, 5), "Topped up from Northwind Bank", 2000m),
                new(D(10, 6), "Converted 1,985.00 CAD to EUR", -1985m),
                new(D(10, 6), "Converted 1,490.00 USD to CAD", 2120.00m),
            ])),
            Wise("Wise EUR", "EUR", "Wise EUR 2026-10.csv", Csv(
            [
                new(D(10, 6), "Converted 1,985.00 CAD to EUR", 1228.22m),
                new(D(10, 7), "Sent money to Banque Exemple", -1228.22m),
            ])),
            Wise("Wise USD", "USD", "Wise USD 2026-10.csv", Csv(
            [
                new(D(10, 5), "Topped up from Contoso Bank", 1500m),
                new(D(10, 6), "Converted 1,490.00 USD to CAD", -1490m),
            ])),
        ];
    }
}
