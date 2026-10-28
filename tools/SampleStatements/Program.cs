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
    var pages = await PdfReader.ReadAsync(await File.ReadAllBytesAsync(args[1]));
    foreach (var page in pages)
        foreach (var line in LineBuilder.Build(page))
            Console.WriteLine($"p{line.Page} y={line.Top,6:0.0} | " + string.Join(" | ", line.Words.Select(w => $"{w.Text}@{w.Left:0}")));
    return 0;
}
Console.Error.WriteLine("usage: generate <folder> | parse <file.pdf> | words <file.pdf>");
return 2;

static void Write(string path, byte[] bytes) { File.WriteAllBytes(path, bytes); Console.WriteLine(path); }
