using System.Globalization;
using System.Text;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace ProductivityApp.Finance.Testing;

/// <summary>
/// Fictitious statements in the layouts of the Caisse d'Epargne (a French bank), for tests and for trying the app:
/// <list type="bullet">
/// <item>the monthly PDF "Relevé de vos comptes": every account of the person in one file (the current account, then the
/// Livret A), each with "Date | Débit | Crédit" and no running balance, dates without a year, a second line of detail
/// under some operations, "SOLDE PRECEDENT AU", "NOUVEAU SOLDE CREDITEUR AU ... (en francs : ...)", fee lines starting
/// with "*", the total of fees, a code printed down the margin, several pages;</item>
/// <item>the CSV export of online banking since 2020 ("Date de comptabilisation;Libelle simplifie;Libelle operation;...;
/// Debit;Credit;..."), debits written negative, dates like 3/10/2025;</item>
/// <item>the older CSV export (bank, branch and account lines above "Date;Numéro d'opération;Libellé;Débit;Crédit;Détail;",
/// the closing balance above the operations and the opening one below), the Money (OFX) and Quicken (QIF) exports.</item>
/// </list>
/// French cards are debit cards: card purchases ("CB ... FACT 031025") come straight out of the current account.
/// The person, the branch, the account numbers, the employer and the amounts are invented.
/// </summary>
public static class CaisseEpargneGenerator
{
    /// <param name="Simple">"Libelle simplifie" of the CSV export.</param>
    /// <param name="Type">"Type operation" of the CSV export.</param>
    public sealed record Line(DateOnly Date, string Label, string? Detail, decimal Amount, string Simple, string Type, string Category, string SubCategory);

    public sealed record Account(string Title, string Number, decimal Opening, List<Line> Lines)
    {
        public decimal Closing => Opening + Lines.Sum(l => l.Amount);
        public decimal Debits => -Lines.Where(l => l.Amount < 0).Sum(l => l.Amount);
        public decimal Credits => Lines.Where(l => l.Amount > 0).Sum(l => l.Amount);
    }

    public sealed record Statement(DateOnly Start, DateOnly End, int Number, List<Account> Accounts);

    private static readonly (string Label, string Simple, string Type, string Category, string Sub, decimal Min, decimal Max)[] Card =
    [
        ("CB CARREFOUR MARKET", "CARREFOUR MARKET", "Carte bancaire", "Alimentation", "Supermarché", 18.40m, 96.30m),
        ("CB BOULANGERIE DU PORT", "BOULANGERIE DU PORT", "Carte bancaire", "Alimentation", "Boulangerie", 2.10m, 9.80m),
        ("CB MONOPRIX", "MONOPRIX", "Carte bancaire", "Alimentation", "Supermarché", 7.35m, 58.90m),
        ("CB PHARMACIE CENTRALE", "PHARMACIE CENTRALE", "Carte bancaire", "Santé", "Pharmacie", 4.60m, 27.15m),
        ("CB SNCF INTERNET", "SNCF INTERNET", "Carte bancaire", "Transports", "Train", 19.00m, 112.00m),
        ("CB TOTALENERGIES ST EXEMPLE", "TOTALENERGIES", "Carte bancaire", "Auto / Moto", "Carburant", 35.00m, 72.40m),
        ("CB LE PETIT CAFE EXEMPLE", "LE PETIT CAFE EXEMPLE", "Carte bancaire", "Loisirs", "Restaurants", 3.20m, 24.50m),
        ("CB DECATHLON", "DECATHLON", "Carte bancaire", "Shopping", "Sport", 12.99m, 79.99m),
    ];

    public static Statement Sample(int year = 2025, int month = 10, int seed = 7)
    {
        var rng = new Random(seed);
        var start = new DateOnly(year, month, 1);
        var end = start.AddMonths(1).AddDays(-1);
        var day = (int d) => start.AddDays(Math.Min(d, end.Day) - 1);
        var current = new List<Line>
        {
            new(day(2), "VIR SEPA SALAIRE EXEMPLE SARL", "SALAIRE " + start.ToString("MMMM yyyy", Fr).ToUpperInvariant(), 2450.00m, "SALAIRE EXEMPLE SARL", "Virement reçu", "Revenus", "Salaire"),
            new(day(5), "PRLV SEPA EDF CLIENTS PARTICULIERS", "-Réf. du mandat : EX0000123456", -78.00m, "EDF", "Prélèvement", "Logement", "Électricité"),
            new(day(5), "VIR SEPA LOYER SCI EXEMPLE", "LOYER " + start.ToString("MMMM", Fr).ToUpperInvariant(), -720.00m, "LOYER SCI EXEMPLE", "Virement émis", "Logement", "Loyer"),
            new(day(8), "RETRAIT DAB " + day(8).ToString("dd/MM", Fr) + " CE EXEMPLE", null, -60.00m, "RETRAIT DAB", "Retrait", "Retraits", "Distributeur"),
            new(day(10), "PRLV SEPA FREE MOBILE", "-Réf. donneur d'ordre : FM0000654321", -19.99m, "FREE MOBILE", "Prélèvement", "Télécom", "Téléphone mobile"),
            new(day(12), "VIR LIVRET A", null, -200.00m, "VIR LIVRET A", "Virement interne", "Épargne", "Livret A"),
            new(day(15), "VIR INST RECU M PAUL EXEMPLE", "REMBOURSEMENT DINER", 35.00m, "M PAUL EXEMPLE", "Virement reçu", "Revenus", "Remboursements"),
            new(day(18), "CHEQUE N 0001234", null, -45.00m, "CHEQUE", "Chèque", "Divers", "Chèques"),
            new(day(21), "PRLV SEPA MUTUELLE EXEMPLE SANTE", null, -42.50m, "MUTUELLE EXEMPLE SANTE", "Prélèvement", "Santé", "Mutuelle"),
            new(day(28), "* COTIS BOUQUET LIBERTE", null, -15.90m, "COTIS BOUQUET LIBERTE", "Frais bancaires", "Banque", "Cotisation"),
            new(day(28), "* FRAIS TENUE DE COMPTE", null, -2.00m, "FRAIS TENUE DE COMPTE", "Frais bancaires", "Banque", "Frais"),
        };
        for (var i = 0; i < 26; i++)
        {
            var c = Card[rng.Next(Card.Length)];
            var amount = Math.Round(c.Min + (decimal)rng.NextDouble() * (c.Max - c.Min), 2);
            var d = day(1 + rng.Next(end.Day));
            current.Add(new Line(d, $"{c.Label} FACT {d:ddMMyy}", null, -amount, c.Simple, c.Type, c.Category, c.Sub));
        }
        current = [.. current.OrderBy(l => l.Date)];
        var livret = new List<Line> { new(day(12), "VIR LIVRET A", null, 200.00m, "VIR LIVRET A", "Virement interne", "Épargne", "Livret A") };
        return new Statement(start, end, 10 + month, [
            new Account("COMPTE DE DEPOT", "08123 00451 04012345678", 1532.18m, current),
            new Account("LIVRET A", "08123 00451 04012345679", 3800.00m, livret),
        ]);
    }

    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");

    /// <summary>"1 234,56" (French grouping with a plain space, as the bank prints it).</summary>
    private static string Money(decimal v) => Math.Abs(v).ToString("#,##0.00", Fr).Replace(' ', ' ').Replace(' ', ' ');
    private static string Signed(decimal v) => (v < 0 ? "-" : "+") + Money(v);

    public static byte[] Pdf(Statement s)
    {
        QuestPDF.Settings.License = LicenseType.Community;
        return Document.Create(c => c.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.MarginVertical(36);
            page.MarginLeft(40);
            page.MarginRight(22);
            page.DefaultTextStyle(x => x.FontFamily("Lato").FontSize(8.5f));
            page.Header().Row(r =>
            {
                r.RelativeItem().Column(h =>
                {
                    h.Item().Text("Caisse d'Epargne").FontSize(15).Bold();
                    h.Item().Text("ILE-DE-FRANCE EXEMPLE");
                    h.Item().Text("Votre Agence : AGENCE EXEMPLE");
                });
                r.AutoItem().AlignRight().Column(h =>
                {
                    h.Item().AlignRight().Text("Relevé").FontSize(13).Bold();
                    h.Item().AlignRight().Text("de vos comptes").FontSize(11);
                    h.Item().AlignRight().Text(t =>
                    {
                        t.Span($"au {s.End.AddDays(1):dd/MM/yyyy} - N° {s.Number} Page ");
                        t.CurrentPageNumber();
                        t.Span(" / ");
                        t.TotalPages();
                    });
                });
            });
            page.Content().Row(row =>
            {
                row.RelativeItem().Column(col =>
                {
                    col.Item().PaddingTop(10).Text("M ALEX SAMPLE");
                    col.Item().Text("12 RUE DE L'EXEMPLE");
                    col.Item().Text("75000 VILLE EXEMPLE");
                    col.Item().PaddingTop(10).Text("Identifiant client 0123456789012");
                    col.Item().PaddingTop(8).Text("SYNTHESE de vos comptes en euros").Bold();
                    foreach (var a in s.Accounts)
                        col.Item().Row(r =>
                        {
                            r.RelativeItem().Text($"{a.Title} N° {a.Number}");
                            r.AutoItem().Text($"Solde au {s.End.AddDays(1):dd/MM/yy} {Signed(a.Closing)}");
                        });
                    foreach (var a in s.Accounts)
                    {
                        col.Item().PaddingTop(16).Text($"M ALEX SAMPLE - {a.Title} - N° {a.Number}").Bold();
                        col.Item().Text("Détail des opérations en euros").FontSize(8);
                        col.Item().PaddingTop(4).Table(t =>
                        {
                            t.ColumnsDefinition(cd => { cd.ConstantColumn(40); cd.RelativeColumn(); cd.ConstantColumn(70); cd.ConstantColumn(70); });
                            static IContainer Head(IContainer x) => x.BorderBottom(0.6f).PaddingVertical(2);
                            t.Header(hd =>
                            {
                                hd.Cell().Element(Head).Text("Date").Bold();
                                hd.Cell().Element(Head).Text("");
                                hd.Cell().Element(Head).AlignRight().Text("Débit").Bold();
                                hd.Cell().Element(Head).AlignRight().Text("Crédit").Bold();
                            });
                            t.Cell().ColumnSpan(3).PaddingVertical(2).Text($"SOLDE PRECEDENT AU {s.Start.AddDays(-1):dd/MM/yy}").Bold();
                            t.Cell().PaddingVertical(2).AlignRight().Text(Money(a.Opening)).Bold();
                            foreach (var l in a.Lines)
                            {
                                t.Cell().PaddingTop(2).Text(l.Date.ToString("dd/MM", Fr));
                                t.Cell().PaddingTop(2).Column(d =>
                                {
                                    d.Item().Text(l.Label);
                                    if (l.Detail is not null) d.Item().PaddingLeft(10).Text(l.Detail).FontSize(7.5f);
                                });
                                t.Cell().PaddingTop(2).AlignRight().Text(l.Amount < 0 ? Money(l.Amount) : "");
                                t.Cell().PaddingTop(2).AlignRight().Text(l.Amount > 0 ? Money(l.Amount) : "");
                            }
                            t.Cell().ColumnSpan(2).PaddingTop(4).Text("TOTAL DES OPERATIONS").Bold();
                            t.Cell().PaddingTop(4).AlignRight().Text(Money(a.Debits)).Bold();
                            t.Cell().PaddingTop(4).AlignRight().Text(Money(a.Credits)).Bold();
                        });
                        var fees = -a.Lines.Where(l => l.Label.StartsWith('*')).Sum(l => l.Amount);
                        if (fees > 0) col.Item().PaddingTop(4).Text($"frais bancaires et cotisations pour un total de -{Money(fees)}€").FontSize(8);
                        col.Item().PaddingTop(4).Row(r =>
                        {
                            r.RelativeItem().Text($"NOUVEAU SOLDE CREDITEUR AU {s.End.AddDays(1):dd/MM/yy} (en francs : {Money(a.Closing * 6.55957m)})").Bold();
                            r.AutoItem().Text(Money(a.Closing)).Bold();
                        });
                        col.Item().Text("*Les lignes d'opérations correspondant à des frais et cotisations commencent par une étoile").FontSize(6.5f);
                    }
                });
                // The code the bank prints down the right margin, one character per line.
                row.ConstantItem(10).PaddingLeft(4).Column(m =>
                {
                    foreach (var ch in "0000000123456789AB43667C0543DR") m.Item().Text(ch.ToString()).FontSize(6);
                });
            });
            page.Footer().Text("Caisse d'Epargne et de Prévoyance Exemple, Banque Coopérative régie par les articles L.512-85 et suivants du Code Monétaire et Financier.").FontSize(6);
        })).WithMetadata(new DocumentMetadata { CreationDate = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero), ModifiedDate = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero) })
        .GeneratePdf();
    }

    /// <summary>The CSV export since 2020: ";"-separated, dates like 3/10/2025, debits negative, decimal point (or comma).</summary>
    public static byte[] Csv(Account a, bool decimalComma = false)
    {
        string N(decimal v) => decimalComma ? v.ToString("0.00", Fr) : v.ToString("0.##", CultureInfo.InvariantCulture);
        string D(DateOnly d) => $"{d.Day}/{d.Month}/{d.Year}";
        var sb = new StringBuilder("Date de comptabilisation;Libelle simplifie;Libelle operation;Reference;Informations complementaires;Type operation;Categorie;Sous categorie;Debit;Credit;Date operation;Date de valeur;Pointage operation\r\n");
        // Newest first, as the bank lists them.
        foreach (var l in a.Lines.OrderByDescending(l => l.Date))
            sb.Append($"{D(l.Date)};{l.Simple};{l.Label};;{l.Detail ?? ""};{l.Type};{l.Category};{l.SubCategory};{(l.Amount < 0 ? N(l.Amount) : "")};{(l.Amount > 0 ? N(l.Amount) : "")};{D(l.Date)};{D(l.Date)};0\r\n");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    /// <summary>The older CSV export: account lines, the closing balance, the operations, then the opening balance (Windows-1252).</summary>
    public static byte[] OldCsv(Statement s, Account a)
    {
        var sb = new StringBuilder();
        sb.Append($"Code de la banque : 08123;Code de l'agence : 00451;Date de début de téléchargement : {s.Start:dd/MM/yyyy};Date de fin de téléchargement : {s.End:dd/MM/yyyy};;\r\n");
        sb.Append($"Numéro de compte : {a.Number.Split(' ')[^1]};Nom du compte : {a.Title};Devise : EUR;;;\r\n");
        sb.Append(";;;;;\r\n");
        sb.Append($"Solde en fin de période;;;;{Signed(a.Closing).Replace(" ", "")};\r\n");
        sb.Append("Date;Numéro d'opération;Libellé;Débit;Crédit;Détail;\r\n");
        var n = 8150000;
        foreach (var l in a.Lines.OrderByDescending(l => l.Date))
            sb.Append($"{l.Date:dd/MM/yy};{n++};{l.Label};{(l.Amount < 0 ? "-" + Money(l.Amount).Replace(" ", "") : "")};{(l.Amount > 0 ? "+" + Money(l.Amount).Replace(" ", "") : "")};{l.Label}{(l.Detail is null ? "" : " " + l.Detail)};\r\n");
        sb.Append($"Solde en début de période;;;;{Signed(a.Opening).Replace(" ", "")};\r\n");
        return CodePagesEncoding().GetBytes(sb.ToString());
    }

    /// <summary>The Money export (OFX 1.0.2, SGML), amounts with a decimal comma as French software writes them.</summary>
    public static byte[] Ofx(Statement s, Account a, bool decimalComma = true)
    {
        string N(decimal v) => decimalComma ? v.ToString("0.00", Fr) : v.ToString("0.00", CultureInfo.InvariantCulture);
        var sb = new StringBuilder("OFXHEADER:100\r\nDATA:OFXSGML\r\nVERSION:102\r\nSECURITY:NONE\r\nENCODING:USASCII\r\nCHARSET:1252\r\nCOMPRESSION:NONE\r\nOLDFILEUID:NONE\r\nNEWFILEUID:NONE\r\n\r\n");
        sb.Append("<OFX><SIGNONMSGSRSV1><SONRS><STATUS><CODE>0<SEVERITY>INFO</STATUS><DTSERVER>20251101<LANGUAGE>FRA</SONRS></SIGNONMSGSRSV1>\r\n");
        sb.Append($"<BANKMSGSRSV1><STMTTRNRS><TRNUID>00000000<STATUS><CODE>0<SEVERITY>INFO</STATUS><STMTRS><CURDEF>EUR<BANKACCTFROM><BANKID>08123<BRANCHID>00451<ACCTID>{a.Number.Split(' ')[^1]}<ACCTTYPE>CHECKING</BANKACCTFROM>\r\n");
        sb.Append($"<BANKTRANLIST><DTSTART>{s.Start:yyyyMMdd}<DTEND>{s.End:yyyyMMdd}\r\n");
        var n = 1;
        foreach (var l in a.Lines)
            sb.Append($"<STMTTRN><TRNTYPE>{(l.Amount < 0 ? "DEBIT" : "CREDIT")}<DTPOSTED>{l.Date:yyyyMMdd}<TRNAMT>{N(l.Amount)}<FITID>{s.Start:yyyyMM}{n++:0000}<NAME>{l.Label}{(l.Detail is null ? "" : "<MEMO>" + l.Detail)}</STMTTRN>\r\n");
        sb.Append($"</BANKTRANLIST><LEDGERBAL><BALAMT>{N(a.Closing)}<DTASOF>{s.End:yyyyMMdd}</LEDGERBAL></STMTRS></STMTTRNRS></BANKMSGSRSV1></OFX>\r\n");
        return Encoding.ASCII.GetBytes(TextNormAscii(sb.ToString()));
    }

    /// <summary>The Quicken export (QIF): dates day/month/year, amounts with a decimal comma.</summary>
    public static byte[] Qif(Account a)
    {
        var sb = new StringBuilder("!Type:Bank\r\n");
        foreach (var l in a.Lines)
            sb.Append($"D{l.Date:dd/MM/yyyy}\r\nT{l.Amount.ToString("0.00", Fr)}\r\nP{l.Label}\r\n{(l.Detail is null ? "" : "M" + l.Detail + "\r\n")}^\r\n");
        return CodePagesEncoding().GetBytes(sb.ToString());
    }

    private static Encoding CodePagesEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1252);
    }

    private static string TextNormAscii(string s) => new(s.Normalize(NormalizationForm.FormD).Where(ch => ch < 128).ToArray());
}
