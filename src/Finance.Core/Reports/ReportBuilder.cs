using System.Globalization;
using System.Reflection;
using ProductivityApp.Finance.Model;
using ProductivityApp.Finance.Services;
using QuestPDF.Drawing;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace ProductivityApp.Finance.Reports;

/// <summary>
/// The period report, always in the same layout (US Letter, portrait), with the same sections as the screen:
/// operations, recurring payments, regular income, transfers (From / To), totals,
/// pies (category, brand, recurring payments, regular income), expenses per day (or month, or year) and the
/// diagram of the money moved.
/// </summary>
public static class ReportBuilder
{
    private static readonly Lock InitGate = new();
    private static bool _initialized;

    private static void Initialize()
    {
        lock (InitGate)
        {
            if (_initialized) return;
            QuestPDF.Settings.License = LicenseType.Community;
            QuestPDF.Settings.UseSystemFonts = false;
            var asm = typeof(ReportBuilder).Assembly;
            foreach (var name in asm.GetManifestResourceNames().Where(n => n.StartsWith("Fonts.", StringComparison.Ordinal) && n.EndsWith(".ttf", StringComparison.Ordinal)))
            {
                using var s = asm.GetManifestResourceStream(name)!;
                FontManager.RegisterFontFromStream(s);
            }
            _initialized = true;
        }
    }

    /// <summary>
    /// What the report shows, following the settings: every slice in the pies or the five largest; transfers in their
    /// own table ("separate"), left out ("hidden") or among the operations ("inline").
    /// </summary>
    /// <param name="TopLevelCategories">Charts by first-level category (nested categories counted in theirs), or by the categories themselves.</param>
    public sealed record Options(bool PieShowAll = true, string Transfers = "separate", bool TopLevelCategories = true);

    public static byte[] Build(PeriodSummary s, string dateFormat, DateTime generatedAt, Options? options = null)
    {
        Initialize();
        var o = options ?? new Options();
        bool IsMove(TransactionView t) => Categories.Get(t.CategoryId).Kind == CategoryKind.Transfer;
        // Same split as the screen: recurring payments and income, then movements, are shown apart from the rest.
        // Same split as the screen: every income in its own table (the earned part of an investment coming back
        // included), recurring payments apart, then movements; the main table keeps the other spending.
        var income = s.Transactions.Where(t => t.Amount > 0 && !IsMove(t))
            .Concat(s.Transactions.Where(t => IsMove(t) && t.Earned is > 0).Select(t => t with { Amount = t.Earned!.Value, Brand = t.Brand + " (earned part)" }))
            .OrderBy(t => t.Date).ToList();
        var main = s.Transactions.Where(t => t.Recurring is null && !(t.Amount > 0 && !IsMove(t)) && (o.Transfers == "inline" || !IsMove(t))).ToList();
        var recurringOut = s.Transactions.Where(t => t.Recurring is not null && t.Amount < 0).ToList();
        var doc = Document.Create(container => container.Page(page =>
        {
            page.Size(PageSizes.Letter);
            page.MarginHorizontal(40);
            page.MarginVertical(36);
            page.PageColor(Colors.White);
            page.DefaultTextStyle(x => x.FontFamily("Inter").FontSize(9).FontColor(Palette.Ink));

            page.Header().PaddingBottom(14).Column(col =>
            {
                col.Item().Row(row =>
                {
                    row.RelativeItem().Text("FINANCES · PERIOD REPORT").FontSize(7.5f).LetterSpacing(0.08f).SemiBold().FontColor(Palette.InkMuted);
                    row.AutoItem().Text($"Generated {generatedAt.ToString("dd MMMM yyyy", Formatting.English)}").FontSize(7.5f).FontColor(Palette.InkMuted);
                });
                col.Item().PaddingTop(6).Text(s.Title).FontSize(17).SemiBold();
                col.Item().PaddingTop(2).Text($"{s.Transactions.Count} operation{(s.Transactions.Count == 1 ? "" : "s")} · amounts in {s.Currency}").FontSize(8.5f).FontColor(Palette.InkSecondary);
            });

            page.Content().Column(col =>
            {
                col.Spacing(16);
                col.Item().Element(c => OperationsTable(c, main, s, dateFormat, "No other operations in this period."));
                if (recurringOut.Count > 0)
                    col.Item().Element(c => Section(c, "Recurring payments & subscriptions", s.RecurringExpenses, s, Palette.NegativeSoft,
                        x => OperationsTable(x, recurringOut, s, dateFormat, "", frequency: true)));
                if (income.Count > 0)
                    col.Item().Element(c => Section(c, "Income", s.Earnings, s, Palette.PositiveSoft,
                        x => OperationsTable(x, income, s, dateFormat, "", frequency: true)));
                if (o.Transfers is "separate" or "table" && s.Movements.Count > 0)
                    col.Item().Element(c => Section(c, "Transfers, card payments & investments", s.Movements.Sum(m => m.Amount), s, Palette.Surface,
                        x => MovementsTable(x, s.Movements, s, dateFormat)));
                col.Item().ShowEntire().Column(t =>
                {
                    t.Spacing(8);
                    t.Item().Element(c => Totals(c, s));
                    t.Item().Element(c => Ratios(c, s));
                });
                var byCategory = o.TopLevelCategories ? s.ByTopCategory : s.ByCategory;
                col.Item().ShowEntire().Element(c => PieBlock(c, "Expenses per category", o.PieShowAll ? byCategory : SummaryService.FoldPie(byCategory), s.Currency));
                col.Item().ShowEntire().Element(c => PieBlock(c, "Expenses per brand", o.PieShowAll ? s.ByBrand : s.BrandPie, s.Currency));
                col.Item().ShowEntire().Element(c => PieBlock(c, "Recurring payments & subscriptions",
                    o.PieShowAll ? s.RecurringExpensePie : SummaryService.FoldPie(s.RecurringExpensePie), s.Currency, "No recurring payments in this period."));
                col.Item().ShowEntire().Element(c => PieBlock(c, "Income", o.PieShowAll ? s.IncomePie : SummaryService.FoldPie(s.IncomePie), s.Currency, "No income in this period."));
                col.Item().ShowEntire().Element(c => PieBlock(c, "Tags", o.PieShowAll ? s.TagPie : SummaryService.FoldPie(s.TagPie), s.Currency, "No tagged expenses in this period."));
                col.Item().ShowEntire().Element(c => SeriesBlock(c, s));
                if (s.Diagram.Nodes.Count > 0) col.Item().ShowEntire().Element(c => FlowBlock(c, s));
            });

            page.Footer().PaddingTop(10).Row(row =>
            {
                row.RelativeItem().Text(s.Title).FontSize(7).FontColor(Palette.InkMuted);
                row.AutoItem().Text(t =>
                {
                    t.DefaultTextStyle(x => x.FontSize(7).FontColor(Palette.InkMuted));
                    t.Span("Page ");
                    t.CurrentPageNumber();
                    t.Span(" of ");
                    t.TotalPages();
                });
            });
        }));
        return doc.GeneratePdf();
    }

    private static string Day(string iso, string dateFormat) => Formatting.FormatDate(DateOnly.ParseExact(iso, "yyyy-MM-dd"), dateFormat);

    /// <summary>A titled table with its total in the corner.</summary>
    private static void Section(IContainer container, string title, decimal total, PeriodSummary s, string tint, Action<IContainer> body)
    {
        container.Column(col =>
        {
            col.Item().PaddingBottom(4).Row(r =>
            {
                r.RelativeItem().Text(title).SemiBold().FontSize(10.5f);
                var per = s.Granularity == Granularity.Month ? "this month" : s.Granularity == Granularity.Year ? "this year" : "over the period";
                // The total sits on a faint tint: red for payments, green for income.
                r.AutoItem().Background(tint).CornerRadius(5).PaddingHorizontal(7).PaddingVertical(3)
                    .Text($"Total {per}: {Formatting.Money(total, s.Currency)}").SemiBold().FontSize(9).EnableFontFeature("tnum");
            });
            col.Item().Element(body);
        });
    }

    /// <summary>The names of the tags an operation carries (its own and its category's), joined.</summary>
    private static string TagText(long[]? ids, PeriodSummary s) =>
        ids is null or { Length: 0 } ? "" : string.Join(", ", ids.Select(id => s.Tags.FirstOrDefault(t => t.Id == id)?.Name).Where(n => n is not null));

    private static void OperationsTable(IContainer container, List<TransactionView> rows, PeriodSummary s, string dateFormat, string empty, bool frequency = false)
    {
        var currency = s.Currency;
        container.Table(table =>
        {
            table.ColumnsDefinition(c =>
            {
                c.ConstantColumn(108);
                c.RelativeColumn(1.1f);
                c.RelativeColumn(1.4f);
                c.RelativeColumn(0.8f);
                if (frequency) c.ConstantColumn(72);
                c.ConstantColumn(92);
            });
            var columns = frequency ? 6u : 5u;
            table.Header(h =>
            {
                IContainer Head(IContainer c) => c.BorderBottom(0.8f).BorderColor("#c9c8c3").PaddingVertical(5).PaddingHorizontal(4);
                h.Cell().Element(Head).Text("Date").SemiBold().FontSize(8);
                h.Cell().Element(Head).Text("Category").SemiBold().FontSize(8);
                h.Cell().Element(Head).Text("Brand Name").SemiBold().FontSize(8);
                h.Cell().Element(Head).Text("Tags").SemiBold().FontSize(8);
                if (frequency) h.Cell().Element(Head).Text("Comes back").SemiBold().FontSize(8);
                h.Cell().Element(Head).AlignRight().Text($"Amount ({currency})").SemiBold().FontSize(8);
            });
            if (rows.Count == 0)
            {
                table.Cell().ColumnSpan(columns).PaddingVertical(10).PaddingHorizontal(4).Text(empty).FontColor(Palette.InkMuted);
                return;
            }
            foreach (var t in rows)
            {
                IContainer Cell(IContainer c) => c.BorderBottom(0.5f).BorderColor(Palette.Hairline).PaddingVertical(4).PaddingHorizontal(4);
                var cat = Categories.Get(t.CategoryId);
                var transfer = cat.Kind == CategoryKind.Transfer;
                table.Cell().Element(Cell).Text(Day(t.Date, dateFormat)).EnableFontFeature("tnum");
                table.Cell().Element(Cell).Text(cat.Name).FontColor(transfer ? Palette.InkMuted : Palette.Ink);
                table.Cell().Element(Cell).Text(t.Brand).ClampLines(1);
                table.Cell().Element(Cell).Text(TagText(t.Tags, s)).FontColor(Palette.InkSecondary).ClampLines(1);
                if (frequency) table.Cell().Element(Cell).Text(t.Recurring ?? "").FontColor(Palette.InkSecondary);
                table.Cell().Element(Cell).AlignRight().Text(Formatting.Money(t.Amount, currency, sign: true)).EnableFontFeature("tnum")
                    .FontColor(transfer ? Palette.InkMuted : t.Amount > 0 ? Palette.Positive : Palette.Ink);
            }
        });
    }

    private static void MovementsTable(IContainer container, List<MovementView> rows, PeriodSummary s, string dateFormat)
    {
        var currency = s.Currency;
        var byId = s.Transactions.ToDictionary(t => t.Id);
        container.Table(table =>
        {
            table.ColumnsDefinition(c =>
            {
                c.ConstantColumn(80);
                c.ConstantColumn(96);
                c.RelativeColumn(0.9f);
                c.RelativeColumn(0.8f);
                c.RelativeColumn(1.45f);
                c.RelativeColumn(0.6f);
                c.ConstantColumn(66);
            });
            table.Header(h =>
            {
                IContainer Head(IContainer c) => c.BorderBottom(0.8f).BorderColor("#c9c8c3").PaddingVertical(5).PaddingHorizontal(4);
                h.Cell().Element(Head).Text("Date").SemiBold().FontSize(8);
                h.Cell().Element(Head).Text("Category").SemiBold().FontSize(8);
                h.Cell().Element(Head).Text("Description").SemiBold().FontSize(8);
                h.Cell().Element(Head).Text("From").SemiBold().FontSize(8);
                h.Cell().Element(Head).Text("To").SemiBold().FontSize(8);
                h.Cell().Element(Head).Text("Tags").SemiBold().FontSize(8);
                h.Cell().Element(Head).AlignRight().Text($"Amount ({currency})").SemiBold().FontSize(8);
            });
            foreach (var m in rows)
            {
                IContainer Cell(IContainer c) => c.BorderBottom(0.5f).BorderColor(Palette.Hairline).PaddingVertical(4).PaddingHorizontal(4);
                table.Cell().Element(Cell).Text(Day(m.Date, dateFormat)).EnableFontFeature("tnum").FontSize(8f);
                table.Cell().Element(Cell).Text(Categories.Get(m.CategoryId).Name).FontSize(8f).FontColor(Palette.InkSecondary).ClampLines(1);
                table.Cell().Element(Cell).Text(m.Label).FontSize(8f).ClampLines(1);
                table.Cell().Element(Cell).Text(m.From).FontSize(8f).SemiBold().FontColor(m.FromOutside ? Palette.OutsideInk : Palette.Ink).ClampLines(1);
                table.Cell().Element(Cell).Text("→ " + m.To).FontSize(8f).SemiBold().FontColor(m.ToOutside ? Palette.OutsideInk : Palette.Ink).ClampLines(1);
                table.Cell().Element(Cell).Text(TagText(byId.GetValueOrDefault(m.EditId)?.Tags, s)).FontSize(8f).FontColor(Palette.InkSecondary).ClampLines(1);
                table.Cell().Element(Cell).AlignRight().Text(Formatting.Money(m.Amount, currency)).EnableFontFeature("tnum").FontSize(8f);
            }
        });
    }

    private static void Totals(IContainer container, PeriodSummary s)
    {
        var balanceColor = s.Balance > 0 ? Palette.Positive : s.Balance < 0 ? Palette.Negative : Palette.Ink;
        var balanceBack = s.Balance > 0 ? Palette.PositiveStrong : s.Balance < 0 ? Palette.NegativeStrong : Palette.Surface;
        container.Row(row =>
        {
            row.Spacing(8);
            void Stat(string label, string value, string color, string back)
            {
                row.RelativeItem().Background(back).CornerRadius(8).Padding(12).Column(c =>
                {
                    c.Item().Text(label).FontSize(8).FontColor(Palette.InkSecondary);
                    c.Item().PaddingTop(3).Text(value).FontSize(15).SemiBold().FontColor(color).EnableFontFeature("tnum");
                });
            }
            // Expenses faintly red, earnings faintly green, the balance more strongly in the colour of its sign.
            Stat("Expenses", Formatting.Money(s.Expenses, s.Currency), Palette.Ink, Palette.NegativeSoft);
            Stat("Earnings", Formatting.Money(s.Earnings, s.Currency), Palette.Ink, Palette.PositiveSoft);
            Stat("Balance", Formatting.Money(s.Balance, s.Currency, sign: true), balanceColor, balanceBack);
        });
    }

    private static string Percent(double? share) => share is { } v ? $"{Math.Round(v * 100):0}%" : "-";

    /// <summary>The subscription burden (recurring payments, rent excluded) and the share of earnings spent.</summary>
    private static void Ratios(IContainer container, PeriodSummary s)
    {
        container.Row(row =>
        {
            row.Spacing(8);
            void Stat(string label, string value, string note)
            {
                row.RelativeItem().Background(Palette.Surface).CornerRadius(8).Padding(12).Column(c =>
                {
                    c.Item().Text(label).FontSize(8).FontColor(Palette.InkSecondary);
                    c.Item().PaddingTop(3).Text(value).FontSize(15).SemiBold().EnableFontFeature("tnum");
                    c.Item().PaddingTop(2).Text(note).FontSize(7.5f).FontColor(Palette.InkMuted);
                });
            }
            Stat("Recurring payments, rent excluded", Formatting.Money(s.RecurringWithoutHousing, s.Currency),
                $"{Percent(s.RecurringWithoutHousingShare)} of earnings");
            Stat("Expenses / earnings", Percent(s.ExpensesShare),
                $"{Formatting.Money(s.Expenses, s.Currency)} spent of {Formatting.Money(s.Earnings, s.Currency)} earned");
        });
    }

    private static void PieBlock(IContainer container, string title, List<Slice> slices, string currency, string empty = "No expenses in this period.")
    {
        container.Border(0.6f).BorderColor(Palette.Hairline).CornerRadius(8).Padding(12).Column(col =>
        {
            col.Item().Text(title).SemiBold().FontSize(10);
            if (slices.Count == 0)
            {
                col.Item().PaddingTop(10).Text(empty).FontColor(Palette.InkMuted);
                return;
            }
            var dense = slices.Count > 16;
            col.Item().PaddingTop(8).Row(row =>
            {
                row.ConstantItem(96).Height(96).Svg(SvgCharts.Donut(slices, 96));
                row.ConstantItem(28);
                row.RelativeItem().AlignMiddle().Column(legend =>
                {
                    legend.Spacing(dense ? 2 : 4);
                    for (var i = 0; i < slices.Count; i++)
                    {
                        var sl = slices[i];
                        var size = dense ? 7.5f : 8.5f;
                        var color = SvgCharts.SliceColor(i, sl.Key);
                        legend.Item().Row(r =>
                        {
                            r.ConstantItem(7).Height(7).OffsetY(2).CornerRadius(1.5f).Background(color);
                            r.ConstantItem(5);
                            r.RelativeItem().Text(sl.Label).FontSize(size).ClampLines(1);
                            r.ConstantItem(90).AlignRight().Text(Formatting.Money(sl.Amount, currency)).FontSize(size).EnableFontFeature("tnum");
                            r.ConstantItem(50).AlignRight().Text((sl.Share * 100).ToString("0.00", CultureInfo.InvariantCulture) + "%").FontSize(size).FontColor(Palette.InkSecondary).EnableFontFeature("tnum");
                        });
                    }
                });
            });
            col.Item().PaddingTop(6).AlignRight().Text($"Total {Formatting.Money(slices.Sum(x => x.Amount), currency)}").FontSize(7.5f).FontColor(Palette.InkSecondary);
        });
    }

    private static void SeriesBlock(IContainer container, PeriodSummary s)
    {
        var title = s.SeriesUnit switch { "month" => "Expenses per month", "year" => "Expenses per year", _ => "Expenses per day" };
        container.Border(0.6f).BorderColor(Palette.Hairline).CornerRadius(8).Padding(12).Column(col =>
        {
            col.Item().Text(title).SemiBold().FontSize(10);
            col.Item().PaddingTop(8).Height(150).Svg(size => SvgCharts.Bars(s.Series, size.Width, size.Height, s.Currency));
        });
    }

    private static void FlowBlock(IContainer container, PeriodSummary s)
    {
        // The diagram keeps its proportions: drawn 508 points wide, as tall as its layout asks.
        var height = s.Diagram.Height * 508 / s.Diagram.Width;
        container.Border(0.6f).BorderColor(Palette.Hairline).CornerRadius(8).Padding(12).Column(col =>
        {
            col.Item().Text("Money moved: transfers, card payments & investments").SemiBold().FontSize(10);
            col.Item().PaddingTop(3).Text(t =>
            {
                t.DefaultTextStyle(x => x.FontSize(7.5f).FontColor(Palette.InkSecondary));
                t.Span("→ ").FontColor(Palette.Accent).SemiBold();
                t.Span("between your accounts      ");
                t.Span("→ ").FontColor(Palette.OutsideInk).SemiBold();
                t.Span("to or from outside the app (accounts not imported, other people, investments)");
            });
            col.Item().PaddingTop(8).Height((float)height).Svg(size => SvgCharts.FlowDiagram(s.Diagram, size.Width, size.Height, s.Currency));
        });
    }

    /// <summary>File name for a report: "Finances 2025-10.pdf", "Finances 2025.pdf" or "Finances 2023-01-01 to 2025-12-31.pdf".</summary>
    public static string FileName(PeriodSummary s) => s.Granularity switch
    {
        Granularity.Month => $"Finances {s.From[..7]}.pdf",
        Granularity.Year => $"Finances {s.From[..4]}.pdf",
        _ => $"Finances {s.From} to {s.To}.pdf",
    };

    internal static Assembly Assembly => typeof(ReportBuilder).Assembly;
}
