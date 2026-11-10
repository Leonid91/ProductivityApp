using System.Globalization;
using System.Net;
using ProductivityApp.Finance.Model;
using ProductivityApp.Finance.Parsing;
using ProductivityApp.Finance.Reports;
using ProductivityApp.Finance.Services;
using ProductivityApp.Finance.Testing;

namespace ProductivityApp.Finance.Tests;

/// <summary>
/// A Canadian bank and a French bank together: each bank on its own in its currency, every bank together converted
/// to the global currency at the rate of each operation's day; the statements of the Caisse d'Epargne in every form
/// it offers; the French banking system (debit cards only, savings books).
/// </summary>
[Collection("categories")]
public class TwoBankTests
{
    /// <summary>Invented rates in the European Central Bank's layout: newest first, "N/A" where a currency had none.</summary>
    private const string Rates = """
        Date,USD,CAD,GBP,
        2025-10-10,1.1500,1.6200,0.8700,
        2025-10-09,1.1600,1.6300,N/A,
        2025-10-08,1.1700,1.6000,0.8650,
        """;

    private static ExchangeRates WithRates(TempStore s)
    {
        var rates = new ExchangeRates(s.Db);
        rates.Save(RateSource.Ecb, ExchangeRates.ParseCsv(Rates));
        return rates;
    }

    [Fact]
    public void Rates_of_the_day_or_the_last_day_before_and_through_the_euro()
    {
        using var s = new TempStore();
        var rates = WithRates(s);
        // The rate of the day.
        Assert.Equal(new Converted(162.00m, 1.62m, new DateOnly(2025, 10, 10)), rates.Convert(100m, "EUR", "CAD", new DateOnly(2025, 10, 10)));
        // Saturday: Friday's rate.
        Assert.Equal(new DateOnly(2025, 10, 10), rates.Convert(100m, "EUR", "CAD", new DateOnly(2025, 10, 11))!.RateDay);
        // CAD to EUR: divided, rounded to the cent.
        Assert.Equal(61.73m, rates.Convert(100m, "CAD", "EUR", new DateOnly(2025, 10, 10))!.Amount);
        // CAD to USD goes through the euro, the same day: 100 x 1.15 / 1.62.
        var cross = rates.Convert(100m, "CAD", "USD", new DateOnly(2025, 10, 10))!;
        Assert.Equal(Math.Round(100m * 1.15m / 1.62m, 2, MidpointRounding.AwayFromZero), cross.Amount);
        Assert.Equal(0.709877m, cross.Rate);
        // A day with no GBP rate ("N/A"): the day before that had one.
        Assert.Equal(new DateOnly(2025, 10, 8), rates.Convert(10m, "EUR", "GBP", new DateOnly(2025, 10, 9))!.RateDay);
        // Before the first rate, or a currency without rates: nothing is made up.
        Assert.Null(rates.Convert(10m, "EUR", "CAD", new DateOnly(2025, 10, 1)));
        Assert.Null(rates.Convert(10m, "EUR", "JPY", new DateOnly(2025, 10, 10)));
        Assert.Equal(new Converted(10m, 1m, new DateOnly(2025, 10, 1)), rates.Convert(10m, "EUR", "EUR", new DateOnly(2025, 10, 1)));
        Assert.Equal("2025-10-10", rates.Status().Latest);
    }

    /// <summary>National Bank in CAD and the Caisse d'Epargne in EUR, one month of each (the Caisse's PDF holds two accounts).</summary>
    private static async Task<(long National, long Caisse, CaisseEpargneGenerator.Statement Statement)> TwoBanks(TempStore s)
    {
        var caisse = s.Db.AddBank(new Bank { Name = "Caisse d'Epargne", Country = "France", System = BankingSystem.France, Currency = "EUR" });
        var national = s.Bank;
        var st = CaisseEpargneGenerator.Sample(2025, 10, 30);
        var first = await s.Imports.ParseAsync("releve.pdf", CaisseEpargneGenerator.Pdf(st));
        foreach (var d in new[] { first }.Concat(first.Others))
        {
            var kind = d.FileName.Contains("LIVRET", StringComparison.Ordinal) ? AccountKind.Savings : AccountKind.Bank;
            s.Imports.Choose(d.Id, null, kind, caisse);
            s.Imports.Commit(new CommitRequest(d.Id, null, null, "EUR", kind, [], BankId: caisse));
        }
        var bnc = BncGenerator.Sample(2025, 10, 4);
        var chq = await s.Imports.ParseAsync("chequing.pdf", BncGenerator.Pdf(bnc, BncGenerator.BncAccount.Chequing));
        s.Imports.Choose(chq.Id, null, AccountKind.Bank, national);
        s.Imports.Commit(new CommitRequest(chq.Id, null, null, "CAD", AccountKind.Bank, [], BankId: national));
        return (national, caisse, st);
    }

    /// <summary>Two days of the Bank of Canada's service: each rate is the value of one unit in Canadian dollars.</summary>
    private const string BankOfCanada = """
        {"observations":[{"d":"2025-10-09","FXEURCAD":{"v":"1.6300"},"FXUSDCAD":{"v":"1.4000"}},
                         {"d":"2025-10-10","FXEURCAD":{"v":"1.6200"},"FXUSDCAD":{"v":"1.3900"},"FXVNDCAD":{"v":""}}]}
        """;

    [Fact]
    public void Another_central_bank_can_give_the_rates_against_its_own_currency()
    {
        using var s = new TempStore();
        var source = "boc";
        var rates = new ExchangeRates(s.Db, () => source);
        Assert.Equal(("Bank of Canada", "CAD"), (rates.Source.Name, rates.Source.Base));
        rates.Save(RateSource.BankOfCanada, ExchangeRates.ParseBankOfCanada(System.Text.Encoding.UTF8.GetBytes(BankOfCanada)));
        // 100 EUR on 10 October: 162.00 CAD (the bank's own figure, no detour).
        Assert.Equal(new Converted(162.00m, 1.62m, new DateOnly(2025, 10, 10)), rates.Convert(100m, "EUR", "CAD", new DateOnly(2025, 10, 10)));
        // EUR to USD goes through the Canadian dollar: 100 x 1.62 / 1.39.
        Assert.Equal(Math.Round(100m * 1.62m / 1.39m, 2, MidpointRounding.AwayFromZero), rates.Convert(100m, "EUR", "USD", new DateOnly(2025, 10, 10))!.Amount);
        // Sunday: Friday's rate. An empty value is not a rate.
        Assert.Equal(new DateOnly(2025, 10, 10), rates.Convert(10m, "USD", "CAD", new DateOnly(2025, 10, 12))!.RateDay);
        Assert.False(rates.Knows("VND"));
        // Back to the European Central Bank: its own copy (none here yet), nothing mixed up.
        source = "ecb";
        Assert.Null(rates.Convert(100m, "EUR", "CAD", new DateOnly(2025, 10, 10)));
        Assert.Equal(("European Central Bank", "EUR"), (rates.Status().Source, rates.Status().Base));
    }

    /// <summary>Answers every request with one status and body (or the body given for the address's host), and counts the requests.</summary>
    private sealed class Answer(HttpStatusCode code, byte[] body, Dictionary<string, byte[]>? byHost = null, Exception? fault = null) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            if (fault is not null) throw fault;
            var content = byHost is not null && byHost.TryGetValue(request.RequestUri!.Host, out var b) ? b : body;
            return Task.FromResult(new HttpResponseMessage(code) { Content = new ByteArrayContent(content) });
        }
    }

    private static byte[] Utf8(string text) => System.Text.Encoding.UTF8.GetBytes(text);

    /// <summary>Three days of the US Federal Reserve's H.10 table (invented rates, the bank's layout): "ND" where a rate is missing.</summary>
    private const string FederalReserve = """
        "Series Description","Euro-Area Euro","Canadian Dollar","Japanese Yen"
        "Unit:","Currency","Currency","Currency"
        "Multiplier:","1","1","1"
        "Currency:","EUR","CAD","JPY"
        "Unique Identifier: ","H10/H10/RXI$US_N.B.EU","H10/H10/RXI_N.B.CA","H10/H10/RXI_N.B.JA"
        "Time Period","RXI$US_N.B.EU","RXI_N.B.CA","RXI_N.B.JA"
        2025-10-08,1.2500,1.4000,150.00
        2025-10-09,ND,1.4100,151.00
        2025-10-10,1.2000,1.3900,ND
        """;

    [Fact]
    public void The_Federal_Reserve_table_gives_every_rate_against_the_US_dollar()
    {
        using var s = new TempStore();
        var (series, currency) = ExchangeRates.Parse(Utf8(FederalReserve), CentralBank.FederalReserve);
        Assert.Equal("USD", currency);
        Assert.Equal(["CAD", "EUR", "JPY"], series.Keys.Order());
        // The euro is quoted in US dollars per euro: kept as euros per dollar (1 / 1.20).
        Assert.Equal("2025-10-10=0.8333333333;2025-10-08=0.8;", series["EUR"]);
        Assert.Equal("2025-10-10=1.3900;2025-10-09=1.4100;2025-10-08=1.4000;", series["CAD"]);
        var rates = new ExchangeRates(s.Db, () => "fed");
        var fed = rates.SaveSource(null, "fed", CentralBank.FederalReserve.DefaultUrl);
        rates = new ExchangeRates(s.Db, () => fed.Id);
        rates.Save(fed, series);
        // 100 EUR on 10 October: 120 USD, then 166.80 CAD through the dollar (100 x 1.39 / 0.8333...).
        Assert.Equal(120.00m, rates.Convert(100m, "EUR", "USD", new DateOnly(2025, 10, 10))!.Amount);
        Assert.Equal(166.80m, rates.Convert(100m, "EUR", "CAD", new DateOnly(2025, 10, 10))!.Amount);
        // No euro rate on the 9th ("ND"): the 8th's.
        Assert.Equal(new DateOnly(2025, 10, 8), rates.Convert(1m, "EUR", "USD", new DateOnly(2025, 10, 9))!.RateDay);
    }

    [Fact]
    public void The_sources_are_a_table_of_the_central_banks_the_app_reads_starting_with_two()
    {
        using var s = new TempStore();
        var rates = new ExchangeRates(s.Db);
        Assert.Equal(["European Central Bank", "Bank of Canada"], rates.Sources().Select(x => x.Name));
        Assert.Equal(["ecb", "boc"], rates.Sources().Select(x => x.Format));
        // A new line is one of the banks of the list, once: the US Federal Reserve, with its own address to start with.
        Assert.Equal(["European Central Bank", "Bank of Canada", "US Federal Reserve"], CentralBank.All.Select(b => b.Name));
        var fed = rates.SaveSource(null, "fed", " " + CentralBank.FederalReserve.DefaultUrl + " ");
        Assert.Equal(("US Federal Reserve", "USD", CentralBank.FederalReserve.DefaultUrl), (fed.Name, fed.Base, fed.Url));
        Assert.Equal(3, new ExchangeRates(s.Db).Sources().Count);
        Assert.Throws<ArgumentException>(() => rates.SaveSource(null, "fed", "https://example.test/x"));
        Assert.Throws<ArgumentException>(() => rates.SaveSource(null, "nowhere", "https://example.test/x"));
        // Only full https addresses.
        Assert.Throws<ArgumentException>(() => rates.SaveSource(fed.Id, null, "http://example.test/rates.csv"));
        Assert.Throws<ArgumentException>(() => rates.SaveSource(fed.Id, null, "rates.csv"));
        // A new address drops what the old one gave; the bank stays the same.
        rates.Save(fed, ExchangeRates.ParseFed(FederalReserve).Series);
        Assert.Equal((4, "2025-10-10"), (rates.Views().Single(v => v.Id == fed.Id).Currencies, rates.Views().Single(v => v.Id == fed.Id).Latest));
        Assert.Equal("fed", rates.SaveSource(fed.Id, "ecb", "https://example.test/other.csv").Format);
        Assert.Empty(s.Db.LoadRateSet(fed.Id, 0));
        // Without a copy, nothing is said about its rates.
        Assert.Equal((0, null), (rates.Views().Single(v => v.Id == fed.Id).Currencies, rates.Views().Single(v => v.Id == fed.Id).Latest));
        // The source in use cannot be removed; another one can, with its rates.
        Assert.Throws<InvalidOperationException>(() => rates.RemoveSource("ecb"));
        rates.RemoveSource(fed.Id);
        Assert.Equal(2, rates.Sources().Count);
    }

    [Fact]
    public async Task Choosing_a_source_checks_its_address_and_says_cleanly_what_is_wrong()
    {
        using var s = new TempStore();
        var rates = new ExchangeRates(s.Db, () => "boc");
        async Task<string?> Check(string id, HttpStatusCode code, string body, Exception? fault = null) =>
            (await rates.UpdateAsync(id, new HttpClient(new Answer(code, Utf8(body), fault: fault)))).Error;

        Assert.Equal("Nothing at this address (404): check it.", await Check("boc", HttpStatusCode.NotFound, ""));
        Assert.StartsWith("This address gives a web page, not a file of rates", await Check("boc", HttpStatusCode.OK, "<!doctype html><html></html>"));
        Assert.Equal("The address gave an empty answer: try again later.", await Check("boc", HttpStatusCode.OK, " \r\n"));
        Assert.Equal("The file at this address is not a file of exchange rates the app can read.", await Check("boc", HttpStatusCode.OK, "{ broken"));
        Assert.Equal("The file at this address holds no exchange rates.", await Check("boc", HttpStatusCode.OK, """{"observations":[]}"""));
        // Another bank's file at the address: said so.
        Assert.Equal("This file is not in the layout of the Bank of Canada: check the address.", await Check("boc", HttpStatusCode.OK, Rates));
        Assert.StartsWith("The file at this address is not a table of exchange rates", await Check("ecb", HttpStatusCode.OK, "hello"));
        Assert.StartsWith("The table does not say which currency", await Check("ecb", HttpStatusCode.OK, "Date,EUR,USD\n2025-10-10,1,1.15"));
        // Anything unexpected is said plainly too, and the copies kept so far stay.
        Assert.Equal("The rates could not be downloaded: disk full", await Check("boc", HttpStatusCode.OK, "", new InvalidOperationException("disk full")));
        Assert.Null(rates.Views().Single(v => v.Id == "boc").Latest);

        // The bank's own file: the error is gone, the rates are there.
        Assert.Null(await Check("boc", HttpStatusCode.OK, BankOfCanada));
        Assert.Equal("CAD", rates.Source.Base);
        Assert.Equal("2025-10-10", rates.Views().Single(v => v.Id == "boc").Latest);
        Assert.Equal(162.00m, rates.Convert(100m, "EUR", "CAD", new DateOnly(2025, 10, 10))!.Amount);
        // The address itself must be https.
        s.Db.SaveRateSource(RateSource.BankOfCanada with { Url = "http://example.test/rates" });
        rates.Reload();
        Assert.Equal("The address must be a full web address starting with https://.", await Check("boc", HttpStatusCode.OK, BankOfCanada));
    }

    [Fact]
    public void Two_copies_at_most_the_day_before_and_today()
    {
        using var s = new TempStore();
        var rates = new ExchangeRates(s.Db);
        var day1 = new DateOnly(2025, 10, 8);
        string? Copy(int slot) => s.Db.LoadRateSet("ecb", slot).GetValueOrDefault("CAD");
        List<string?> Days() => [.. s.Db.RateCopies("ecb").Select(c => c.Day)];

        // Day n: one copy.
        rates.Save(RateSource.Ecb, ExchangeRates.ParseCsv("Date,CAD,\n2025-10-08,1.5000,"), day1);
        Assert.Equal(["2025-10-08"], Days());
        // Downloaded again the same day: today's copy is replaced, still one.
        rates.Save(RateSource.Ecb, ExchangeRates.ParseCsv("Date,CAD,\n2025-10-08,1.5100,"), day1);
        Assert.Equal(["2025-10-08"], Days());
        Assert.Equal("2025-10-08=1.5100;", Copy(0));
        // The next day: the day before (n-1) and today (n).
        rates.Save(RateSource.Ecb, ExchangeRates.ParseCsv("Date,CAD,\n2025-10-09,1.6000,"), day1.AddDays(1));
        Assert.Equal(["2025-10-09", "2025-10-08"], Days());
        // Again the same day: n is replaced, n-1 stays.
        rates.Save(RateSource.Ecb, ExchangeRates.ParseCsv("Date,CAD,\n2025-10-09,1.6100,"), day1.AddDays(1));
        Assert.Equal(["2025-10-09", "2025-10-08"], Days());
        Assert.Equal(("2025-10-09=1.6100;", "2025-10-08=1.5100;"), (Copy(0), Copy(1)));
        // A new day: n becomes n-1, the older copy is deleted. Never more than two.
        rates.Save(RateSource.Ecb, ExchangeRates.ParseCsv("Date,CAD,\n2025-10-10,1.7000,"), day1.AddDays(2));
        Assert.Equal(["2025-10-10", "2025-10-09"], Days());
        Assert.Empty(s.Db.LoadRateSet("ecb", 2));
        // Today's copy is the one used.
        Assert.Equal(1.7m, rates.Convert(1m, "EUR", "CAD", new DateOnly(2025, 10, 10))!.Amount);
        // A copy of today that cannot be read: the day before's is used, and the status says so.
        s.Db.SaveRateSet("ecb", new Dictionary<string, string> { ["CAD"] = "garbage" }, day1.AddDays(2));
        rates.Reload();
        Assert.Equal(1.61m, rates.Convert(1m, "EUR", "CAD", new DateOnly(2025, 10, 10))!.Amount);
        Assert.True(rates.Status().FromPrevious);
    }

    [Fact]
    public async Task Refreshing_downloads_every_bank_of_the_table_once_a_day_or_on_demand()
    {
        using var s = new TempStore();
        var rates = new ExchangeRates(s.Db);
        rates.SaveSource(null, "fed", CentralBank.FederalReserve.DefaultUrl);
        var answer = new Answer(HttpStatusCode.OK, [], new()
        {
            ["www.ecb.europa.eu"] = Utf8(Rates), ["www.bankofcanada.ca"] = Utf8(BankOfCanada), ["www.federalreserve.gov"] = Utf8(FederalReserve),
        });
        using var client = new HttpClient(answer);
        Assert.True(await rates.UpdateAllAsync(force: false, client));
        Assert.Equal(3, answer.Calls);
        // Every bank has its rates, read from what was downloaded.
        Assert.All(rates.Views(), v => Assert.Equal(("2025-10-10", null), (v.Latest, v.Error)));
        Assert.Equal([4, 3, 4], rates.Views().Select(v => v.Currencies));
        // The same day, nothing is due; the Refresh button downloads them all again anyway.
        Assert.False(await rates.UpdateAllAsync(force: false, client));
        Assert.Equal(3, answer.Calls);
        Assert.True(await rates.UpdateAllAsync(force: true, client));
        Assert.Equal(6, answer.Calls);
        Assert.All(rates.Sources(), x => Assert.Single(s.Db.RateCopies(x.Id)));
    }

    [Fact]
    public void The_rates_are_downloaded_again_every_day()
    {
        using var s = new TempStore();
        var rates = new ExchangeRates(s.Db);
        Assert.True(rates.DueToday());
        s.Db.SetSetting("rates.ecb.updatedAt", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        Assert.False(rates.DueToday());
        s.Db.SetSetting("rates.ecb.updatedAt", DateTime.Today.AddDays(-1).ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        Assert.True(rates.DueToday());
    }

    [Fact]
    public async Task Every_bank_and_all_banks_together_in_the_display_currency_converted_operation_by_operation()
    {
        using var s = new TempStore();
        var rates = new ExchangeRates(s.Db);
        // A rate for every day of October 2025 (invented): 1.60 + day / 1000 CAD per euro.
        var csv = "Date,CAD,\n" + string.Join("\n", Enumerable.Range(1, 31).Reverse().Select(d => $"2025-10-{d:D2},{(1.60m + d / 1000m).ToString(CultureInfo.InvariantCulture)},"));
        rates.Save(RateSource.Ecb, ExchangeRates.ParseCsv(csv));
        var (_, _, st) = await TwoBanks(s);

        var r = s.Summaries.Report(new DateOnly(2025, 10, 1), new DateOnly(2025, 10, 31), Granularity.Month, "CAD", rates);
        Assert.Equal(["Caisse d'Epargne", KnownBanks.National], r.Banks.Select(b => b.Name));
        var caisse = r.Banks[0].Summary;
        var national = r.Banks[1].Summary;
        // Every bank in the display currency; the Caisse's own currency is said.
        Assert.Equal(("CAD", "CAD"), (caisse.Currency, national.Currency));
        Assert.Equal(("EUR", "CAD"), (r.Banks[0].Native, r.Banks[1].Native));
        var current = st.Accounts[0];

        // Every euro operation converted at the rate of its own day, rounded to the cent, then added up (the Livret A
        // transfer is money moved, not spent).
        decimal Cad(decimal eur, DateOnly d) => Math.Round(eur * (1.60m + d.Day / 1000m), 2, MidpointRounding.AwayFromZero);
        var spentCad = current.Lines.Where(l => l.Amount < 0 && !l.Label.Contains("LIVRET", StringComparison.Ordinal)).Sum(l => -Cad(l.Amount, l.Date));
        var earnedCad = current.Lines.Where(l => l.Amount > 0).Sum(l => Cad(l.Amount, l.Date));
        Assert.Equal(spentCad, caisse.Expenses);
        Assert.Equal(earnedCad, caisse.Earnings);
        Assert.Equal(national.Expenses + spentCad, r.Global.Expenses);
        Assert.Equal(national.Earnings + earnedCad, r.Global.Earnings);
        Assert.Equal(r.Global.Earnings - r.Global.Expenses, r.Global.Balance);
        // The operations are sent once (in the banks' parts), each euro one with the amount the bank wrote and its rate.
        Assert.Empty(r.Global.Transactions);
        Assert.All(caisse.Transactions, t => Assert.Equal(("CAD", "EUR"), (t.Currency, t.OriginalCurrency)));
        Assert.Empty(r.Unconverted);

        // How it was done, shown: one line for the Caisse's euros, before and after, and the rate of every day.
        var line = Assert.Single(r.Conversion.Lines);
        Assert.Equal(("Caisse d'Epargne", "EUR", "CAD"), (line.Group, line.From, line.To));
        Assert.Equal(current.Debits - 200m, line.Expenses);
        Assert.Equal(current.Credits, line.Earnings);
        Assert.Equal(spentCad, line.ConvertedExpenses);
        Assert.Equal(0, line.Unconverted);
        Assert.All(line.Rates, u => Assert.Equal(1.60m + int.Parse(u.Day[8..], CultureInfo.InvariantCulture) / 1000m, u.Rate));
        Assert.Equal((RateSource.Ecb.Url, "EUR"), (r.Conversion.Url, r.Conversion.Base));
        // The salary: its amount in CAD at the rate of its day.
        var salary = caisse.Transactions.First(t => t.Original == 2450.00m);
        Assert.Equal(Cad(2450.00m, DateOnly.ParseExact(salary.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture)), salary.Amount);

        // The Summary view is global: its balance is the one of all banks together.
        Assert.Equal(r.Global.Balance, Assert.Single(r.Global.Breakdown).Balance);
        // The report: each bank, then all banks with the conversions.
        var pdf = ReportBuilder.Build(r, "dd MMMM yyyy", new DateTime(2025, 11, 1), new ReportBuilder.Options());
        var text = string.Join(' ', (await ProductivityApp.Finance.Pdf.PdfReader.ReadAsync(pdf)).SelectMany(p => p.Words).Select(w => w.Text));
        Assert.Contains("All banks", text, StringComparison.Ordinal);
        Assert.Contains("converted", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_currency_of_an_account_is_the_currency_its_operations_come_in()
    {
        using var s = new TempStore();
        var rates = WithRates(s);
        var (national, _, _) = await TwoBanks(s);
        var from = new DateOnly(2025, 10, 8);
        var to = new DateOnly(2025, 10, 10);
        var account = s.Db.Accounts().First(a => a.BankId == national);
        var before = s.Summaries.Report(from, to, Granularity.Range, "CAD", rates).Banks.Single(b => b.Name == KnownBanks.National).Summary;
        // The account says its money comes in US dollars: its operations are in USD, converted to the display currency.
        account.Currency = "USD";
        s.Db.UpdateAccount(account);
        var after = s.Summaries.Report(from, to, Granularity.Range, "CAD", rates).Banks.Single(b => b.Name == KnownBanks.National).Summary;
        Assert.All(after.Transactions.Where(t => t.AccountId == account.Id), t => Assert.Equal(("CAD", "USD"), (t.Currency, t.OriginalCurrency)));
        var tx = after.Transactions.First(t => t.AccountId == account.Id);
        var rate = tx.Date == "2025-10-10" ? 1.62m / 1.15m : tx.Date == "2025-10-09" ? 1.63m / 1.16m : 1.60m / 1.17m;
        Assert.Equal(Math.Round(tx.Original!.Value * rate, 2, MidpointRounding.AwayFromZero), tx.Amount);
        Assert.NotEqual(before.Expenses, after.Expenses);
    }

    [Fact]
    public async Task Without_a_rate_for_their_day_operations_are_left_out_and_said_so()
    {
        using var s = new TempStore();
        await TwoBanks(s);
        // No rates downloaded at all.
        var r = s.Summaries.Report(new DateOnly(2025, 10, 1), new DateOnly(2025, 10, 31), Granularity.Month, "CAD", new ExchangeRates(s.Db));
        var line = Assert.Single(r.Conversion.Lines);
        Assert.Equal(0, line.Operations);
        Assert.Equal(38, line.Unconverted);
        Assert.Equal(38, r.Unconverted.Count);
        Assert.Equal(r.Banks[1].Summary.Balance, r.Global.Balance);
        // The Caisse's operations are still listed, in euros, outside every total.
        Assert.Equal(38, r.Banks[0].Summary.Transactions.Count(t => t.Currency == "EUR"));
        Assert.Equal(0m, r.Banks[0].Summary.Balance);
        // In euros as the global currency, the Canadian bank is the one left out.
        r = s.Summaries.Report(new DateOnly(2025, 10, 1), new DateOnly(2025, 10, 31), Granularity.Month, "EUR", new ExchangeRates(s.Db));
        Assert.Equal(("CAD", "EUR"), (Assert.Single(r.Conversion.Lines).From, r.Conversion.Lines[0].To));
        Assert.Equal(r.Banks[0].Summary.Balance, r.Global.Balance);
    }

    [Fact]
    public async Task Caisse_d_Epargne_statement_of_every_account_gives_one_balanced_statement_each()
    {
        using var s = new TempStore();
        var st = CaisseEpargneGenerator.Sample(2025, 9, 12);
        var first = await s.Imports.ParseAsync("releve.pdf", CaisseEpargneGenerator.Pdf(st));
        var drafts = new[] { first }.Concat(first.Others).ToList();
        Assert.Equal(2, drafts.Count);
        for (var i = 0; i < 2; i++)
        {
            var a = st.Accounts[i];
            var p = drafts[i].Parse;
            Assert.Equal(VerificationStatus.Verified, p.Status);
            Assert.Equal((a.Opening, a.Closing, a.Lines.Count, "EUR"), (p.OpeningBalance, p.ClosingBalance, p.Rows.Count, p.Currency));
            Assert.Equal(a.Lines.Select(l => (l.Date, l.Amount)), p.Rows.Select(r => (r.Date, r.Amount)));
            Assert.Equal(a.Number[^4..], p.Last4);
        }
        Assert.Equal((AccountKind.Bank, AccountKind.Savings), (drafts[0].Parse.Kind, drafts[1].Parse.Kind));
        Assert.EndsWith("· COMPTE DE DEPOT", drafts[0].FileName, StringComparison.Ordinal);
        Assert.EndsWith("· LIVRET A", drafts[1].FileName, StringComparison.Ordinal);
        // The second line under an operation stays in its label; fee lines and totals are not operations.
        Assert.Contains(drafts[0].Parse.Rows, r => r.Label.Contains("SALAIRE EXEMPLE SARL", StringComparison.Ordinal) && r.Label.Contains("SALAIRE SEPTEMBRE", StringComparison.Ordinal));
        Assert.DoesNotContain(drafts[0].Parse.Rows, r => r.Label.Contains("TOTAL DES", StringComparison.Ordinal) || r.Label.Contains("frais bancaires", StringComparison.Ordinal));
        // Both accounts can be saved, each once.
        foreach (var d in drafts) s.Imports.Commit(new CommitRequest(d.Id, null, null, "EUR", d.Parse.Kind, [], BankId: s.Bank));
        var again = await s.Imports.ParseAsync("releve.pdf", CaisseEpargneGenerator.Pdf(st));
        Assert.True(again.AlreadyImported && again.Others.All(o => o.AlreadyImported));
    }

    [Fact]
    public void Caisse_d_Epargne_exports_CSV_older_CSV_OFX_and_QIF_read_the_same_operations()
    {
        var st = CaisseEpargneGenerator.Sample(2025, 10, 30);
        var a = st.Accounts[0];
        var expected = a.Lines.Select(l => (l.Date, l.Amount)).OrderBy(x => x.Date).ThenBy(x => x.Amount).ToList();
        void Same(StatementParse p) => Assert.Equal(expected, p.Rows.Select(r => (r.Date, r.Amount)).OrderBy(x => x.Date).ThenBy(x => x.Amount).ToList());

        foreach (var comma in new[] { false, true })
        {
            var csv = CsvStatementReader.Parse(CaisseEpargneGenerator.Csv(a, comma), "export.csv", "EUR");
            Same(csv);
            Assert.Equal(AccountKind.Bank, csv.Kind);
            // The full label of the operation, without the simplified one repeated or the bank's tick ("0").
            Assert.All(csv.Rows.Where(r => r.Label.StartsWith("CB ", StringComparison.Ordinal)), r => Assert.Matches(@"^CB [A-Z ]+ FACT \d{6}$", r.Label));
            Assert.DoesNotContain(csv.Rows, r => r.Label.EndsWith(" 0", StringComparison.Ordinal) || r.Label.StartsWith("MONOPRIX CB", StringComparison.Ordinal));
            // The bank's sub-category when it says more: "Salaire" rather than "Revenus".
            Assert.Equal("Salaire", csv.Rows.Single(r => r.Amount == 2450m).BankCategory);
        }

        // The older export: account lines above the table, the closing balance above it and the opening one below.
        var old = CsvStatementReader.Parse(CaisseEpargneGenerator.OldCsv(st, a), "Caisse d'Epargne export.csv", "CAD");
        Same(old);
        Assert.Equal((VerificationStatus.Verified, a.Opening, a.Closing, "EUR", "5678"), (old.Status, old.OpeningBalance, old.ClosingBalance, old.Currency, old.Last4));
        // "Caisse d'Epargne" names the bank, not a savings account; the Livret A's own export is a savings account.
        Assert.Equal(AccountKind.Bank, old.Kind);
        Assert.Equal(AccountKind.Savings, CsvStatementReader.Parse(CaisseEpargneGenerator.OldCsv(st, st.Accounts[1]), "Caisse d'Epargne export.csv", "CAD").Kind);

        var ofx = MoneyFiles.ParseOfx(CaisseEpargneGenerator.Ofx(st, a), "CAD", null);
        Same(ofx);
        Assert.Equal(("EUR", a.Closing), (ofx.Currency, ofx.ClosingBalance));
        // Quicken: day/month dates and decimal commas ("-64,23" is minus sixty-four euros twenty-three).
        Same(MoneyFiles.ParseQif(CaisseEpargneGenerator.Qif(a), "export.qif", "EUR", null));
    }

    [Fact]
    public void French_banks_have_debit_cards_and_savings_books_not_credit_cards()
    {
        using var s = new TempStore();
        var fr = new ProductivityApp.Finance.Classification.Classifier([], BankingSystem.France);
        var na = new ProductivityApp.Finance.Classification.Classifier([], BankingSystem.NorthAmerica);
        // A card purchase comes out of the current account: an expense, never a card payment.
        Assert.Equal("groceries", fr.Classify("CB CARREFOUR MARKET FACT 031025", -64.23m).CategoryId);
        Assert.NotEqual(Categories.CardPayments, fr.Classify("PAIEMENT VISA CARREFOUR", -20m).CategoryId);
        // The monthly debit of a deferred-debit card is the only card movement.
        var deferred = fr.Classify("FACTURE CARTE DU 30/09/25", -412.50m);
        Assert.Equal((Categories.CardPayments, "Deferred debit card"), (deferred.CategoryId, deferred.Brand));
        Assert.Equal("Credit card payment", na.Classify("PAIEMENT VISA MERCI", -300m).Brand);
        // Livret A, LDDS, LEP: savings of one's own (moved between accounts), not investments.
        foreach (var label in new[] { "VIR LIVRET A", "VIREMENT LDDS", "VERSEMENT LEP" })
            Assert.Equal(Categories.Transfers, fr.Classify(label, -200m).CategoryId);
        Assert.Equal(Categories.Investments, fr.Classify("VERSEMENT ASSURANCE VIE", -100m).CategoryId);
        // A transfer that pays rent is rent; a plain French transfer is a bank transfer, not Interac.
        Assert.Equal("housing", fr.Classify("VIR SEPA LOYER SCI EXEMPLE", -720m).CategoryId);
        Assert.Equal("Bank transfer", fr.Classify("VIR SEPA", -50m).Brand);
        Assert.Equal("Current account (debit card)", AccountTypes.Label(AccountKind.Bank, BankingSystem.France));
        Assert.Equal("Deferred debit card", AccountTypes.Label(AccountKind.CreditCard, BankingSystem.France));
        Assert.Equal("Card statement payments", Categories.Get(Categories.CardPayments).Name);
    }
}
