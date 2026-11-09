namespace ProductivityApp.Finance.Model;

/// <summary>
/// A place publishing daily exchange rates, defined in Definitions > Exchange rates: its name, the address of its file of
/// rates, and the currency the rates are quoted against (read from the file when the address is checked).
/// </summary>
public sealed record RateSource(string Id, string Name, string Url, string? Base)
{
    /// <summary>About 30 currencies against the euro, every working day since 1999.</summary>
    public static readonly RateSource Ecb = new("ecb", "European Central Bank", "https://www.ecb.europa.eu/stats/eurofxref/eurofxref-hist.zip", "EUR");

    /// <summary>26 currencies against the Canadian dollar, every business day since 2017.</summary>
    public static readonly RateSource BankOfCanada = new("boc", "Bank of Canada",
        "https://www.bankofcanada.ca/valet/observations/group/FX_RATES_DAILY/json?start_date=2017-01-01", "CAD");

    /// <summary>The sources every new database starts with.</summary>
    public static readonly IReadOnlyList<RateSource> Defaults = [Ecb, BankOfCanada];
}
