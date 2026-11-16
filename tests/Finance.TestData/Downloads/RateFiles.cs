using System.Globalization;
using System.Text;

namespace ProductivityApp.Finance.Testing;

/// <summary>Files of exchange rates in the layouts of the central banks the app reads, with invented rates.</summary>
public static class RateFiles
{
    /// <summary>Three days of the European Central Bank's history (units of each currency per euro), newest first, "N/A" where a currency had none.</summary>
    public const string EuropeanCentralBank = """
        Date,USD,CAD,GBP,
        2025-10-10,1.1500,1.6200,0.8700,
        2025-10-09,1.1600,1.6300,N/A,
        2025-10-08,1.1700,1.6000,0.8650,
        """;

    /// <summary>Two days of the Bank of Canada's service: each rate is the value of one unit in Canadian dollars.</summary>
    public const string BankOfCanada = """
        {"observations":[{"d":"2025-10-09","FXEURCAD":{"v":"1.6300"},"FXUSDCAD":{"v":"1.4000"}},
                         {"d":"2025-10-10","FXEURCAD":{"v":"1.6200"},"FXUSDCAD":{"v":"1.3900"},"FXVNDCAD":{"v":""}}]}
        """;

    /// <summary>Three days of the US Federal Reserve's H.10 table (the bank's layout): "ND" where a rate is missing.</summary>
    public const string FederalReserve = """
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

    /// <summary>
    /// The European Central Bank's history (eurofxref-hist.csv layout: "Date,USD,...,CAD,...", newest first) with a rate for
    /// every day from <paramref name="from"/> to <paramref name="to"/> but Saturdays and Sundays (the bank publishes none
    /// then), each currency's rate given by <paramref name="rate"/>.
    /// </summary>
    public static string EuropeanCentralBankHistory(DateOnly from, DateOnly to, IReadOnlyList<string> currencies, Func<string, DateOnly, decimal> rate)
    {
        var sb = new StringBuilder("Date," + string.Join(',', currencies) + ",\n");
        for (var d = to; d >= from; d = d.AddDays(-1))
            if (d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
                sb.Append(d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(',')
                  .Append(string.Join(',', currencies.Select(c => rate(c, d).ToString("0.0000", CultureInfo.InvariantCulture)))).Append(",\n");
        return sb.ToString();
    }
}
