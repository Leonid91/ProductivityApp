using System.Globalization;
using ProductivityApp.Finance.Parsing;
using ProductivityApp.Finance.Pdf;
using ProductivityApp.Finance.Testing;
using static ProductivityApp.Finance.Testing.StatementGenerator;

// Fictitious statements for trying the app and for automated checks.
//   generate <folder>   writes a year of sample statements in every layout, plus a scanned one
//   parse <file.pdf>    prints what the reader finds in a statement
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
if (args.Length == 2 && args[0] == "generate")
{
    var dir = Directory.CreateDirectory(args[1]).FullName;
    var seed = 11;
    for (var m = 1; m <= 12; m++)
    {
        Write(Path.Combine(dir, $"Northwind Bank {2025}-{m:D2}.pdf"), Render(Sample(Style.EnglishWithdrawalsDeposits, 2025, m, 28 + m % 7, seed++)));
        Write(Path.Combine(dir, $"Fabrikam Card {2025}-{m:D2}.pdf"), Render(Sample(Style.EnglishCard, 2025, m, 16 + m % 5, seed++)));
    }
    Write(Path.Combine(dir, "Banque Exemple 2025-10.pdf"), Render(Sample(Style.FrenchDebitCredit, 2025, 10, 30, seed++)));
    Write(Path.Combine(dir, "Caisse Fictive 2025-10.pdf"), Render(Sample(Style.FrenchSignedAmount, 2025, 10, 26, seed++)));
    Write(Path.Combine(dir, "Mutuelle Imaginaire 2025-10.pdf"), Render(Sample(Style.Atypical, 2025, 10, 22, seed++)));
    Write(Path.Combine(dir, "Banque Exemple 2025-09 (scan).pdf"), AsScan(Render(Sample(Style.FrenchDebitCredit, 2025, 9, 20, seed++))));
    // A Caisse d'Epargne-style current account (fictitious), three months.
    for (var m = 9; m <= 11; m++)
        Write(Path.Combine(dir, $"Caisse d'Epargne 2025-{m:D2}.pdf"), Render(Sample(Style.FrenchDebitCredit, 2025, m, 30, seed++) with { BankName = "Caisse d'Epargne" }));
    // A Banque Nationale-style month (fictitious): Mastercard, chequing and savings, as PDF and as CSV.
    var bnc = BncGenerator.Sample(2025, 8, seed++);
    foreach (var a in new[] { BncGenerator.BncAccount.Card, BncGenerator.BncAccount.Chequing, BncGenerator.BncAccount.Savings })
    {
        Write(Path.Combine(dir, "BNC " + BncGenerator.FileName(a, "pdf")), BncGenerator.Pdf(bnc, a));
        Write(Path.Combine(dir, "BNC " + BncGenerator.FileName(a, "csv")), BncGenerator.Csv(bnc, a));
    }
    return 0;
}
if (args.Length == 2 && args[0] == "parse")
{
    var pages = await PdfReader.ReadAsync(await File.ReadAllBytesAsync(args[1]));
    var p = StatementParser.Parse(pages, null, "CAD");
    Console.WriteLine($"{p.Status} diff={p.Difference} open={p.OpeningBalance} close={p.ClosingBalance} computed={p.ComputedClosing} rows={p.Rows.Count} kind={p.Kind} lang={p.Language} cur={p.Currency} bank={p.Bank} last4={p.Last4} period={p.PeriodStart}..{p.PeriodEnd} ocr={p.UsedOcr} rowchecks={p.RowBalanceChecks}/{p.RowBalanceMismatches}");
    foreach (var r in p.Rows) Console.WriteLine($"{r.Date:yyyy-MM-dd} {r.Amount,10} {r.Balance,10}  {r.Label}");
    return 0;
}
if (args.Length == 2 && args[0] == "scotia")
{
    // scotia <folder>: fictitious statements in Scotiabank layouts (PDF chequing and card, both CSV exports)
    var dir = Directory.CreateDirectory(args[1]).FullName;
    var chq = ScotiaGenerator.Chequing();
    Write(Path.Combine(dir, "Scotia chequing 2026-01.pdf"), ScotiaGenerator.ChequingPdf(chq));
    Write(Path.Combine(dir, "Scotia chequing 2026-01 old.csv"), ScotiaGenerator.OldCsv(chq));
    Write(Path.Combine(dir, "Scotia chequing 2026-01 new.csv"), ScotiaGenerator.NewCsv(chq));
    Console.WriteLine($"chequing open={chq.Opening} close={chq.Closing} out={chq.Withdrawals} in={chq.Deposits} lines={chq.Lines.Count}");
    var card = ScotiaGenerator.VisaCard();
    Write(Path.Combine(dir, "Scotia visa 2026-05.pdf"), ScotiaGenerator.CardPdf(card));
    Console.WriteLine($"card previous={card.Previous} new={card.New} credits={card.Credits} debits={card.Debits} lines={card.Lines.Count}");
    return 0;
}
if (args.Length >= 2 && args[0] == "inspect")
{
    // inspect <file> [bank|card]: reads any file the import takes (PDF, picture, CSV, Excel, OFX...) as the app does, in a
    // throw-away database, and prints the reading with the brand and category of every line.
    var dir = Path.Combine(Path.GetTempPath(), "finances-inspect-" + Guid.NewGuid().ToString("N"));
    try
    {
        using var db = new ProductivityApp.Finance.Storage.Database(Path.Combine(dir, "inspect.db"));
        var txs = new ProductivityApp.Finance.Services.TransactionService(db, () => "CAD");
        var imports = new ProductivityApp.Finance.Services.ImportService(db, () => "CAD", txs);
        var draft = await imports.ParseAsync(Path.GetFileName(args[1]), await File.ReadAllBytesAsync(args[1]));
        if (args.Length == 3)
            draft = imports.Choose(draft.Id, null, args[2] == "card" ? ProductivityApp.Finance.Model.AccountKind.CreditCard : ProductivityApp.Finance.Model.AccountKind.Bank, db.Banks()[0].Id);
        var p = draft.Parse;
        Console.WriteLine($"{p.Method} {p.Status} diff={p.Difference} open={p.OpeningBalance} close={p.ClosingBalance} computed={p.ComputedClosing} rows={p.Rows.Count} kind={p.Kind} cur={p.Currency} period={p.PeriodStart}..{p.PeriodEnd} ocr={p.UsedOcr} rowchecks={p.RowBalanceChecks}/{p.RowBalanceMismatches} order={p.DateOrder}");
        Console.WriteLine($"sum={p.Rows.Sum(r => r.Amount)} out={p.Rows.Where(r => r.Amount < 0).Sum(r => r.Amount)} in={p.Rows.Where(r => r.Amount > 0).Sum(r => r.Amount)}");
        foreach (var r in draft.Rows) Console.WriteLine($"{r.Date:yyyy-MM-dd} {r.Amount,10} {r.Balance,10}  {r.Label,-58} | {r.Brand} | {r.CategoryId} ({r.CategorySource})");
    }
    finally
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }
    return 0;
}
if (args.Length == 5 && args[0] == "one")
{
    // one <style> <count> <seed> <out.pdf|out-scan.pdf>
    var spec = Sample(Enum.Parse<Style>(args[1]), count: int.Parse(args[2]), seed: int.Parse(args[3]));
    var pdf = Render(spec);
    Write(args[4], args[4].Contains("scan") ? AsScan(pdf) : pdf);
    Console.WriteLine($"expected open={spec.Opening} close={spec.Closing}");
    foreach (var t in spec.Transactions) Console.WriteLine($"{t.Date:yyyy-MM-dd} {t.Amount,10}  {t.Label}");
    return 0;
}
if (args.Length == 3 && args[0] == "render")
{
    // render <file.pdf> <out-prefix>: every page as a PNG
    var pdf = await File.ReadAllBytesAsync(args[1]);
    var n = UglyToad.PdfPig.PdfDocument.Open(pdf).NumberOfPages;
    for (var i = 0; i < n; i++) Write($"{args[2]}-{i + 1}.png", await PdfReader.RenderPagePngAsync(pdf, i, 1100));
    return 0;
}
if (args.Length == 2 && args[0] == "words")
{
    var raw = await File.ReadAllBytesAsync(args[1]);
    var pages = await PdfReader.ReadAsync(PdfReader.IsImage(raw) ? await PdfReader.ImageToPdfAsync(raw) : raw);
    foreach (var page in pages)
        foreach (var line in LineBuilder.Build(page))
            Console.WriteLine($"p{line.Page} y={line.Top,6:0.0} | " + string.Join(" | ", line.Words.Select(w => $"{w.Text}@{w.Left:0}")));
    return 0;
}
if (args.Length == 2 && args[0] == "big")
{
    // big <folder>: ten years of fictitious exports with a lot of operations, to measure the app on large data.
    var dir = Directory.CreateDirectory(args[1]).FullName;
    Write(Path.Combine(dir, "Caisse d'Epargne export 2016-2025.csv"), BigData.CaisseEpargneCsv(2016, 10, 170, 3));
    Write(Path.Combine(dir, "Northwind Bank export 2016-2025.csv"), BigData.NorthwindCsv(2016, 10, 170, 5));
    return 0;
}
if (args.Length == 2 && args[0] == "bench")
{
    // bench <folder made by "big">: imports the files into a throw-away database and measures the engine.
    await Bench.Run(args[1]);
    return 0;
}
Console.Error.WriteLine("usage: generate <folder> | big <folder> | bench <folder> | parse <file.pdf> | words <file.pdf>");
return 2;

static void Write(string path, byte[] bytes) { File.WriteAllBytes(path, bytes); Console.WriteLine(path); }
