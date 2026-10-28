using System.Reflection;
using ProductivityApp.Finance.Model;
using ProductivityApp.Finance.Services;
using QuestPDF.Drawing;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace ProductivityApp.Finance.Reports;

/// <summary>
/// The period report, always in the same layout (US Letter, portrait): title, operations table,
/// totals, expenses per category, expenses per brand, expenses per day (or month, or year).
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

    public static byte[] Build(PeriodSummary s, string dateFormat, DateTime generatedAt)
    {
        Initialize();
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
                col.Item().Element(c => OperationsTable(c, s, dateFormat));
                col.Item().ShowEntire().Element(c => Totals(c, s));
                col.Item().ShowEntire().Element(c => PieBlock(c, "Expenses per category", s.CategoryPie, s.Currency));
                col.Item().ShowEntire().Element(c => PieBlock(c, "Expenses per brand", s.BrandPie, s.Currency));
                col.Item().ShowEntire().Element(c => SeriesBlock(c, s));
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

    private static void OperationsTable(IContainer container, PeriodSummary s, string dateFormat)
    {
        container.Table(table =>
        {
            table.ColumnsDefinition(c =>
            {
                c.ConstantColumn(118);
                c.RelativeColumn(1.1f);
                c.RelativeColumn(1.4f);
                c.ConstantColumn(96);
            });
            table.Header(h =>
            {
                IContainer Head(IContainer c) => c.BorderBottom(0.8f).BorderColor("#c9c8c3").PaddingVertical(5).PaddingHorizontal(4);
                h.Cell().Element(Head).Text("Date").SemiBold().FontSize(8);
                h.Cell().Element(Head).Text("Category").SemiBold().FontSize(8);
                h.Cell().Element(Head).Text("Brand Name").SemiBold().FontSize(8);
                h.Cell().Element(Head).AlignRight().Text($"Amount ({s.Currency})").SemiBold().FontSize(8);
            });
            if (s.Transactions.Count == 0)
            {
                table.Cell().ColumnSpan(4).PaddingVertical(10).PaddingHorizontal(4).Text("No operations in this period.").FontColor(Palette.InkMuted);
                return;
            }
            foreach (var t in s.Transactions)
            {
                IContainer Cell(IContainer c) => c.BorderBottom(0.5f).BorderColor(Palette.Hairline).PaddingVertical(4).PaddingHorizontal(4);
                var date = DateOnly.ParseExact(t.Date, "yyyy-MM-dd");
                var transfer = Categories.Get(t.CategoryId).Kind == CategoryKind.Transfer;
                table.Cell().Element(Cell).Text(Formatting.FormatDate(date, dateFormat)).EnableFontFeature("tnum");
                table.Cell().Element(Cell).Text(Categories.Get(t.CategoryId).Name).FontColor(transfer ? Palette.InkMuted : Palette.Ink);
                table.Cell().Element(Cell).Text(t.Brand).ClampLines(1);
                table.Cell().Element(Cell).AlignRight().Text(Formatting.Money(t.Amount, s.Currency, sign: true)).EnableFontFeature("tnum")
                    .FontColor(t.Amount > 0 ? Palette.Positive : Palette.Ink);
            }
        });
    }

    private static void Totals(IContainer container, PeriodSummary s)
    {
        var balanceColor = s.Balance > 0 ? Palette.Positive : s.Balance < 0 ? Palette.Negative : Palette.Ink;
        container.Background(Palette.Surface).CornerRadius(8).Padding(14).Row(row =>
        {
            void Stat(string label, string value, string color)
            {
                row.RelativeItem().Column(c =>
                {
                    c.Item().Text(label).FontSize(8).FontColor(Palette.InkSecondary);
                    c.Item().PaddingTop(3).Text(value).FontSize(15).SemiBold().FontColor(color).EnableFontFeature("tnum");
                });
            }
            Stat("Expenses", Formatting.Money(s.Expenses, s.Currency), Palette.Ink);
            Stat("Earnings", Formatting.Money(s.Earnings, s.Currency), Palette.Ink);
            Stat("Balance", Formatting.Money(s.Balance, s.Currency, sign: true), balanceColor);
        });
    }

    private static void PieBlock(IContainer container, string title, List<Slice> slices, string currency)
    {
        container.Border(0.6f).BorderColor(Palette.Hairline).CornerRadius(8).Padding(12).Column(col =>
        {
            col.Item().Text(title).SemiBold().FontSize(10);
            if (slices.Count == 0)
            {
                col.Item().PaddingTop(10).Text("No expenses in this period.").FontColor(Palette.InkMuted);
                return;
            }
            col.Item().PaddingTop(8).Row(row =>
            {
                row.ConstantItem(96).Height(96).Svg(SvgCharts.Donut(slices, 96));
                row.ConstantItem(28);
                row.RelativeItem().AlignMiddle().Column(legend =>
                {
                    legend.Spacing(4);
                    for (var i = 0; i < slices.Count; i++)
                    {
                        var sl = slices[i];
                        legend.Item().Row(r =>
                        {
                            r.ConstantItem(7).Height(7).OffsetY(2).CornerRadius(1.5f).Background(SvgCharts.SliceColor(i, sl.Key));
                            r.ConstantItem(5);
                            r.RelativeItem().Text(sl.Label).FontSize(8.5f).ClampLines(1);
                            r.ConstantItem(90).AlignRight().Text(Formatting.Money(sl.Amount, currency)).FontSize(8.5f).EnableFontFeature("tnum");
                            r.ConstantItem(44).AlignRight().Text($"{Math.Round(sl.Share * 100):0}%").FontSize(8.5f).FontColor(Palette.InkSecondary).EnableFontFeature("tnum");
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

    /// <summary>File name for a report: "Finances 2025-10.pdf", "Finances 2025.pdf" or "Finances 2023-01-01 to 2025-12-31.pdf".</summary>
    public static string FileName(PeriodSummary s) => s.Granularity switch
    {
        Granularity.Month => $"Finances {s.From[..7]}.pdf",
        Granularity.Year => $"Finances {s.From[..4]}.pdf",
        _ => $"Finances {s.From} to {s.To}.pdf",
    };

    internal static Assembly Assembly => typeof(ReportBuilder).Assembly;
}
