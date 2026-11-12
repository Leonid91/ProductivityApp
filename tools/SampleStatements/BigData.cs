using System.Diagnostics;
using System.Globalization;
using System.Text;
using ProductivityApp.Finance.Model;
using ProductivityApp.Finance.Services;
using ProductivityApp.Finance.Storage;

/// <summary>Years of fictitious operations in CSV exports, to measure the app on large data.</summary>
internal static class BigData
{
    private static readonly (string Label, decimal Min, decimal Max)[] French =
    [
        ("CB CARREFOUR MARKET", 18.40m, 132.60m), ("CB MONOPRIX", 6.20m, 54.80m), ("PRLV SEPA EDF", 61.33m, 98.71m),
        ("CB SNCF INTERNET", 19.00m, 112.00m), ("CB BOULANGERIE PAUL", 3.40m, 12.90m), ("CB FNAC", 14.99m, 189.00m),
        ("PRLV SEPA FREE MOBILE", 19.99m, 19.99m), ("RETRAIT DAB", 20.00m, 110.00m), ("CB RATP NAVIGO", 86.40m, 86.40m),
        ("CB PHARMACIE CENTRALE", 7.80m, 42.15m), ("CB RESTAURANT LE ZINC", 22.50m, 76.00m), ("CB LIDL", 12.30m, 64.90m),
        ("CB PICARD SURGELES", 9.90m, 48.00m), ("CB DECATHLON", 15.00m, 120.00m), ("CB TABAC PRESSE", 2.00m, 9.50m),
    ];

    private static readonly (string Label, decimal Min, decimal Max)[] English =
    [
        ("TIM HORTONS #2345 MONTREAL QC", 2.15m, 14.80m), ("IGA EXTRA 08123 LAVAL QC", 23.40m, 168.90m),
        ("UBER *TRIP HELP.UBER.COM", 9.50m, 38.75m), ("AMZN MKTP CA*2K3J49QW1", 12.99m, 142.35m), ("SAQ SELECTION 23045", 17.50m, 64.20m),
        ("PHARMAPRIX #0123", 6.49m, 58.10m), ("STM-METRO BERRI-UQAM", 3.75m, 97.00m), ("COSTCO WHOLESALE #503", 64.00m, 289.45m),
        ("SHELL C12345 BROSSARD", 38.20m, 86.40m), ("CINEPLEX ODEON FORUM", 14.25m, 41.80m), ("DOLLARAMA #1187", 3.45m, 27.60m),
        ("STARBUCKS 01234 TORONTO ON", 4.85m, 9.70m), ("RESTAURANT LE PETIT BISTRO", 24.60m, 88.15m), ("BOULANGERIE DU COIN", 4.25m, 19.60m),
    ];

    private static IEnumerable<(DateOnly Date, string Label, decimal Amount)> Lines(int fromYear, int years, int perMonth, int seed,
        (string Label, decimal Min, decimal Max)[] pool, string salary, decimal pay, string rent, decimal rentAmount)
    {
        var rng = new Random(seed);
        for (var m = new DateOnly(fromYear, 1, 1); m.Year < fromYear + years; m = m.AddMonths(1))
        {
            var days = DateTime.DaysInMonth(m.Year, m.Month);
            var month = new List<(DateOnly, string, decimal)>
            {
                (m.AddDays(1), rent, -rentAmount), (m.AddDays(4), salary, pay), (m.AddDays(18), salary, pay),
            };
            for (var i = 0; i < perMonth; i++)
            {
                var p = pool[rng.Next(pool.Length)];
                var amount = p.Min == p.Max ? p.Min : Math.Round(p.Min + (decimal)rng.NextDouble() * (p.Max - p.Min), 2);
                month.Add((m.AddDays(rng.Next(days)), p.Label, -amount));
            }
            foreach (var l in month.OrderBy(x => x.Item1)) yield return l;
        }
    }

    /// <summary>Caisse d'Epargne-style export: "Date;Libellé;Débit euros;Crédit euros", day/month dates, decimal commas.</summary>
    public static byte[] CaisseEpargneCsv(int fromYear, int years, int perMonth, int seed)
    {
        var fr = CultureInfo.GetCultureInfo("fr-FR");
        var sb = new StringBuilder("Date;Libellé;Débit euros;Crédit euros\r\n");
        foreach (var (d, label, amount) in Lines(fromYear, years, perMonth, seed, French, "VIR SEPA RECU SALAIRE EXEMPLE SAS", 1845.20m, "PRLV SEPA LOYER SCI EXEMPLE", 925.00m))
            sb.Append(d.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)).Append(';').Append(label).Append(';')
              .Append(amount < 0 ? (-amount).ToString("0.00", fr) : "").Append(';').Append(amount > 0 ? amount.ToString("0.00", fr) : "").Append("\r\n");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    /// <summary>English chequing export with a running balance: "Date,Description,Withdrawals,Deposits,Balance".</summary>
    public static byte[] NorthwindCsv(int fromYear, int years, int perMonth, int seed)
    {
        var sb = new StringBuilder("Date,Description,Withdrawals,Deposits,Balance\r\n");
        var balance = 5000m;
        foreach (var (d, label, amount) in Lines(fromYear, years, perMonth, seed, English, "PAYROLL DEPOSIT EXAMPLE CORP", 2140.55m, "RENT PAYMENT SAMPLE PROPERTIES", 1350.00m))
        {
            balance += amount;
            sb.Append(d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(',').Append(label).Append(',')
              .Append(amount < 0 ? (-amount).ToString("0.00", CultureInfo.InvariantCulture) : "").Append(',')
              .Append(amount > 0 ? amount.ToString("0.00", CultureInfo.InvariantCulture) : "").Append(',')
              .Append(balance.ToString("0.00", CultureInfo.InvariantCulture)).Append("\r\n");
        }
        return Encoding.UTF8.GetBytes(sb.ToString());
    }
}

/// <summary>Measures the engine on the big files: how many times per second each common action can run, and memory.</summary>
internal static class Bench
{
    public static async Task Run(string folder)
    {
        var dir = Path.Combine(Path.GetTempPath(), "finances-bench-" + Guid.NewGuid().ToString("N"));
        using (var db = new Database(Path.Combine(dir, "bench.db")))
        {
            var tx = new TransactionService(db, () => "CAD");
            var imports = new ImportService(db, () => "CAD", tx);
            var summaries = new SummaryService(db);
            var rows = 0;
            var importRate = Once(() =>
            {
                foreach (var f in Directory.GetFiles(folder, "*.csv"))
                {
                    var draft = imports.ParseAsync(Path.GetFileName(f), File.ReadAllBytes(f)).GetAwaiter().GetResult();
                    rows += imports.Commit(new CommitRequest(draft.Id, null, null, f.Contains("Caisse") ? "EUR" : "CAD", AccountKind.Bank, [],
                        NewBankName: Path.GetFileName(f).Split(' ')[0])).Added;
                }
            });
            Console.WriteLine($"operations stored: {rows}");
            Console.WriteLine($"import of all files: {importRate * rows:0} operations per second");
            var last = db.AllTransactions().Max(t => t.Date);
            var month = new DateOnly(last.Year, last.Month, 1);
            Report("month summary", Rate(20, () => summaries.Summarize(month, month.AddMonths(1).AddDays(-1), Granularity.Month, "CAD")));
            Report("year summary", Rate(5, () => summaries.Summarize(new DateOnly(last.Year, 1, 1), new DateOnly(last.Year, 12, 31), Granularity.Year, "CAD")));
            Report("ten years summary", Rate(3, () => summaries.Summarize(new DateOnly(last.Year - 9, 1, 1), new DateOnly(last.Year, 12, 31), Granularity.Range, "CAD")));
            Report("periods index", Rate(20, () => summaries.Index("CAD")));
            // The periods as the app shows them: each bank in its currency, then all banks in CAD at the rate of each
            // day (invented daily rates against the euro, the source of exchange rates read from the settings as the
            // app does).
            db.SetSetting("ratesSource", "ecb");
            var rates = new ExchangeRates(db, () => db.GetSetting("ratesSource"));
            var csv = new StringBuilder("Date,CAD,USD,\n");
            for (var d = new DateOnly(last.Year, 12, 31); d >= new DateOnly(last.Year - 10, 1, 1); d = d.AddDays(-1))
                if (d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
                    csv.Append(CultureInfo.InvariantCulture, $"{d:yyyy-MM-dd},{1.45m + d.DayNumber % 97 / 1000m},{1.10m + d.DayNumber % 53 / 1000m},\n");
            rates.Save(RateSource.Ecb, Encoding.UTF8.GetBytes(csv.ToString()));
            Cost("month report (2 banks, converted)", 30, () => { summaries.ForgetReports(); summaries.Report(month, month.AddMonths(1).AddDays(-1), Granularity.Month, "CAD", rates); });
            Cost("year report (2 banks, converted)", 10, () => { summaries.ForgetReports(); summaries.Report(new DateOnly(last.Year, 1, 1), new DateOnly(last.Year, 12, 31), Granularity.Year, "CAD", rates); });
            Cost("ten years report (2 banks, converted)", 3, () => { summaries.ForgetReports(); summaries.Report(new DateOnly(last.Year - 9, 1, 1), new DateOnly(last.Year, 12, 31), Granularity.Range, "CAD", rates); });
            // The answer as the app sends it to the interface (the same JSON options as the app's bridge).
            var web = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)
            {
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase) },
            };
            foreach (var (name, from, to, g, times) in new[]
            {
                ("year", new DateOnly(last.Year, 1, 1), new DateOnly(last.Year, 12, 31), Granularity.Year, 10),
                ("ten years", new DateOnly(last.Year - 9, 1, 1), new DateOnly(last.Year, 12, 31), Granularity.Range, 3),
            })
            {
                var report = summaries.Report(from, to, g, "CAD", rates);
                Cost($"{name} report to JSON", times, () => System.Text.Json.JsonSerializer.Serialize(report, web));
                Console.WriteLine($"{name} report JSON size: {System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(report, web).Length / 1048576.0:0.0} MB");
            }
            Cost("periods index (every bank)", 20, () => summaries.Index(null));
            Cost("rates status", 50, () => rates.Status());
            var some = db.AllTransactions().Where(t => t.Currency == "CAD" && t.Amount < 0).Take(10).ToList();
            var i = 0;
            Report("category change (whole brand)", Rate(5, () => tx.SetCategory(some[i++ % some.Count].Id, i % 2 == 0 ? "shopping" : "entertainment")));
            Report("category change (one operation)", Rate(5, () => tx.SetCategory(some[i++ % some.Count].Id, "health", allOfBrand: false)));
            Report("brand rename", Rate(5, () => tx.SetBrand(some[i++ % some.Count].Id, "Brand " + i)));
            Report("reclassify everything", Rate(3, () => tx.ReapplyRules()));
            Report("pair movements between accounts", Rate(3, () => TransferMatcher.Link(db)));
            var classifier = tx.NewClassifier();
            var all = db.AllTransactions();
            Report("classify every operation", Rate(3, () => { foreach (var t in all) classifier.Classify(t.RawLabel, t.Amount, t.BankCategory); }));
            Report("statements list", Rate(20, () => db.Statements()));
            Report("all operations read", Rate(10, () => db.AllTransactions()));
            GC.Collect();
            Console.WriteLine($"managed memory after: {GC.GetTotalMemory(true) / 1048576.0:0.0} MB, peak working set: {Process.GetCurrentProcess().PeakWorkingSet64 / 1048576.0:0} MB");
            Console.WriteLine($"database file: {new FileInfo(db.Path).Length / 1048576.0:0.0} MB");
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(dir, true); } catch (IOException) { }
        await Task.CompletedTask;
    }

    private static void Report(string what, double perSecond) => Console.WriteLine($"{what}: {perSecond:0.0} per second");

    /// <summary>Time of one run (average of several, after a warm-up run) and the memory it allocates.</summary>
    private static void Cost(string what, int times, Action action)
    {
        action();
        GC.Collect();
        var before = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        for (var n = 0; n < times; n++) action();
        var ms = sw.Elapsed.TotalMilliseconds / times;
        var mb = (GC.GetAllocatedBytesForCurrentThread() - before) / (double)times / 1048576;
        Console.WriteLine($"{what}: {ms:0.0} ms per run, {mb:0.0} MB allocated per run");
    }

    private static double Once(Action action)
    {
        var sw = Stopwatch.StartNew();
        action();
        return 1 / sw.Elapsed.TotalSeconds;
    }

    /// <summary>How many times per second the action runs (one warm-up run first).</summary>
    private static double Rate(int times, Action action)
    {
        action();
        var sw = Stopwatch.StartNew();
        for (var n = 0; n < times; n++) action();
        return times / sw.Elapsed.TotalSeconds;
    }
}
