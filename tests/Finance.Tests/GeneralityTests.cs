using System.Text;
using ProductivityApp.Finance.Parsing;
using ProductivityApp.Finance.Text;

namespace ProductivityApp.Finance.Tests;

/// <summary>
/// What the app reads is not tied to the banks and currencies of its examples: amounts in any currency, exports with
/// columns never seen before, dates with their time, operations that never moved money.
/// </summary>
public class GeneralityTests
{
    [Theory]
    [InlineData("CHF 1'234.50", 1234.50)]
    [InlineData("12,50 GBP", 12.50)]
    [InlineData("SEK 1 020,00", 1020.00)]
    [InlineData("-45.10 AUD", -45.10)]
    [InlineData("PLN 7.00", 7.00)]
    [InlineData("$12.34", 12.34)]
    [InlineData("12,34 €", 12.34)]
    [InlineData("₹1,234.56", 1234.56)]
    [InlineData("¥ 980.00", 980.00)]
    [InlineData("-₩15,000.00", -15000.00)]
    [InlineData("KES 1,200.00", 1200.00)]
    [InlineData("250.00 MXN", 250.00)]
    [InlineData("₺99,90", 99.90)]
    [InlineData("JPY 1,200", 1200)]
    [InlineData("1 200 JPY", 1200)]
    [InlineData("₩15,000", 15000)]
    [InlineData("KWD 12.500", 12.500)]
    [InlineData("-1.250,750 BHD", -1250.750)]
    public void Amounts_are_read_in_any_currency(string text, double expected)
    {
        Assert.True(AmountParser.TryParse(text, out var a), text);
        Assert.Equal((decimal)Math.Abs(expected), a.Magnitude);
        Assert.Equal(expected < 0, a.Marker == SignMarker.Minus);
    }

    [Theory]
    [InlineData("ATM 12.50")]
    [InlineData("12.50 NYC")]
    [InlineData("REF 2026.01")]
    [InlineData("1,200")]
    [InlineData("$1,200")]
    [InlineData("1,234")]
    [InlineData("12.500 EUR")]
    [InlineData("¥1,200")]
    public void A_word_that_is_not_a_currency_does_not_make_an_amount(string text) => Assert.False(AmountParser.TryParse(text, out _), text);

    [Theory]
    [InlineData("¥")]
    [InlineData("₹")]
    [InlineData("₽")]
    [InlineData("NGN")]
    [InlineData("VND")]
    public void Any_currency_sign_or_code_beside_an_amount_is_a_marker(string text) => Assert.True(AmountParser.IsDetachedMarker(text), text);

    [Theory]
    [InlineData("Credit Mutuel export 2026", false, false)]
    [InlineData("Caisse d'Epargne compte courant", false, false)]
    [InlineData("Caisse d'Epargne Livret A", false, true)]
    [InlineData("Credit Agricole Visa", true, false)]
    [InlineData("American Express Gold", true, false)]
    public void The_name_of_a_bank_never_decides_the_kind_of_account(string fileName, bool card, bool savings)
    {
        // An export without any column or line that tells: only the file's name can.
        var csv = "Date,Description,Amount,Balance\n2026-02-03,Shop Example,-10.00,90.00\n2026-02-05,Shop Example,-5.00,85.00\n";
        var p = CsvStatementReader.Parse(Encoding.UTF8.GetBytes(csv), fileName + ".csv", "EUR");
        Assert.Equal(card, p.Kind == ProductivityApp.Finance.Model.AccountKind.CreditCard);
        Assert.Equal(savings, p.Kind == ProductivityApp.Finance.Model.AccountKind.Savings);
    }

    [Fact]
    public void An_export_in_a_currency_the_examples_never_use_is_read_and_balanced()
    {
        var csv = "Booking date;Text;Amount (CHF);Balance (CHF)\n03.02.2026;Migros Example;-45.10 CHF;954.90 CHF\n05.02.2026;Lohn Example AG;CHF 5'200.00;CHF 6'154.90\n09.02.2026;Miete;-1'800.00;4'354.90\n";
        var p = CsvStatementReader.Parse(Encoding.UTF8.GetBytes(csv), "export.csv", "CHF");
        Assert.Equal(VerificationStatus.Verified, p.Status);
        Assert.Equal([-45.10m, 5200.00m, -1800.00m], p.Rows.Select(r => r.Amount));
        Assert.Equal(new DateOnly(2026, 2, 5), p.Rows[1].Date);
        Assert.Equal((1000.00m, 4354.90m), (p.OpeningBalance!.Value, p.ClosingBalance!.Value));
    }

    [Fact]
    public void Operations_that_never_moved_money_are_left_out_whatever_the_bank_calls_them()
    {
        var csv = "Date,Description,Amount,Status,Balance\n2026-04-01 09:00:00,Coffee,-3.50,Completed,96.50\n2026-04-02 10:00:00,Hotel,-200.00,Declined,\n2026-04-03T11:00:00Z,Books,-20.00,Pending,\n"
            + "2026-04-04 12:00,Refund,5.00,Posted,101.50\n2026-04-05 13:00,Taxi,-11.00,Cancelled,\n";
        var p = CsvStatementReader.Parse(Encoding.UTF8.GetBytes(csv), "history.csv", "USD");
        Assert.Equal(VerificationStatus.Verified, p.Status);
        Assert.Equal([(new DateOnly(2026, 4, 1), -3.50m), (new DateOnly(2026, 4, 4), 5.00m)], p.Rows.Select(r => (r.Date, r.Amount)));
    }

    [Fact]
    public void A_fee_column_is_a_line_of_its_own_in_any_export_newest_first_included()
    {
        var csv = "Date,Details,Amount,Commission,Balance\n2026-05-03,Wire abroad,-100.00,2.00,48.00\n2026-05-02,Bakery,-50.00,0,150.00\n2026-05-01,Salary,200.00,,200.00\n";
        var p = CsvStatementReader.Parse(Encoding.UTF8.GetBytes(csv), "history.csv", "USD");
        Assert.Equal(VerificationStatus.Verified, p.Status);
        Assert.True(p.NewestFirst);
        Assert.Equal([200.00m, -50.00m, -100.00m, -2.00m], p.Rows.Select(r => r.Amount));
        Assert.Equal("Commission: Wire abroad", p.Rows[^1].Label);
        Assert.Equal((0m, 48.00m), (p.OpeningBalance!.Value, p.ClosingBalance!.Value));
    }

    [Fact]
    public void Two_accounts_named_line_by_line_are_told_apart_only_when_the_balances_prove_it()
    {
        // Two accounts whose balances follow each other account by account, not line by line.
        var two = "Date,Account,Description,Amount,Balance\n2026-06-01,Everyday,Bakery,-5.00,95.00\n2026-06-01,Rainy day,Interest,1.00,501.00\n2026-06-02,Everyday,Bus,-3.00,92.00\n2026-06-03,Rainy day,Deposit,50.00,551.00\n";
        var parts = CsvStatementReader.Accounts(CsvStatementReader.Table(Encoding.UTF8.GetBytes(two)), "USD");
        Assert.Equal(["Everyday", "Rainy day"], parts.Select(p => p.Title));
        Assert.All(parts, p => Assert.Equal(VerificationStatus.Verified, CsvStatementReader.Parse(CsvStatementReader.Write(p.Table), "x.csv", "USD").Status));
        // One account whose lines name other accounts (who was paid): the file balances as it is, it stays whole.
        var one = "Date,Account,Description,Amount,Balance\n2026-06-01,ACME Ltd,Bakery,-5.00,95.00\n2026-06-02,City Transit,Bus,-3.00,92.00\n2026-06-03,Employer Inc,Pay,50.00,142.00\n";
        Assert.Single(CsvStatementReader.Accounts(CsvStatementReader.Table(Encoding.UTF8.GetBytes(one)), "USD"));
        // No balance to prove anything: the file stays whole.
        var none = "Date,Account,Description,Amount\n2026-06-01,A,Bakery,-5.00\n2026-06-02,B,Bus,-3.00\n";
        Assert.Single(CsvStatementReader.Accounts(CsvStatementReader.Table(Encoding.UTF8.GetBytes(none)), "USD"));
    }

    [Theory]
    [InlineData(';')]
    [InlineData(',')]
    [InlineData('\t')]
    public void A_short_table_under_lines_of_title_is_read_and_proven_by_the_balance_printed_under_it(char d)
    {
        // As many lines of title (one cell each) as lines of the table: the separator and the width are those of the table.
        var csv = $"Savings account No 99887766\nA Sample\nFrom 2026-05-01 to 2026-05-31\nDate{d}Description{d}Withdrawal{d}Deposit{d}Balance\n"
            + $"2026-05-12{d}Transfer from chequing{d}{d}150.00{d}4360.00\n{d}Balance on 2026-05-31{d}{d}{d}4360.00\n";
        var p = CsvStatementReader.Parse(Encoding.UTF8.GetBytes(csv), "export.csv", "USD");
        Assert.Equal([(new DateOnly(2026, 5, 12), 150.00m)], p.Rows.Select(r => (r.Date, r.Amount)));
        Assert.Equal((VerificationStatus.Verified, 4210.00m, 4360.00m), (p.Status, p.OpeningBalance, p.ClosingBalance));
        // A balance printed under the table that disagrees with the running balance is a mismatch, never hidden.
        var wrong = csv.Replace($"{d}{d}{d}4360.00\n", $"{d}{d}{d}4300.00\n", StringComparison.Ordinal);
        Assert.Equal(VerificationStatus.Mismatch, CsvStatementReader.Parse(Encoding.UTF8.GetBytes(wrong), "export.csv", "USD").Status);
    }
}
