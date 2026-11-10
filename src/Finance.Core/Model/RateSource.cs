namespace ProductivityApp.Finance.Model;

/// <summary>
/// A central bank whose daily exchange rates the app can read: the layout of its file (<see cref="Format"/>), its own
/// currency (every rate it publishes is against it) and the address of its file to start with.
/// </summary>
public sealed record CentralBank(string Format, string Name, string Currency, string DefaultUrl)
{
    /// <summary>About 30 currencies against the euro, every working day since 1999 (CSV in a ZIP).</summary>
    public static readonly CentralBank Ecb = new("ecb", "European Central Bank", "EUR", "https://www.ecb.europa.eu/stats/eurofxref/eurofxref-hist.zip");

    /// <summary>26 currencies against the Canadian dollar, every business day since 2017 (Valet JSON).</summary>
    public static readonly CentralBank BankOfCanada = new("boc", "Bank of Canada", "CAD",
        "https://www.bankofcanada.ca/valet/observations/group/FX_RATES_DAILY/json?start_date=2017-01-01");

    /// <summary>23 currencies against the US dollar, every business day (release H.10, CSV), the last 5,000 days (since 2007).</summary>
    public static readonly CentralBank FederalReserve = new("fed", "US Federal Reserve", "USD",
        "https://www.federalreserve.gov/datadownload/Output.aspx?rel=H10&series=60f32914ab61dfab590e0e470153e3ae&lastobs=5000&from=&to=&filetype=csv&label=include&layout=seriescolumn&type=package");

    /// <summary>Every central bank the app can read, in the order the list of choices shows them.</summary>
    public static readonly IReadOnlyList<CentralBank> All = [Ecb, BankOfCanada, FederalReserve];

    public static CentralBank? Find(string? format) => All.FirstOrDefault(b => b.Format == format);
}

/// <summary>
/// A central bank defined in Definitions > Exchange rates: which bank (<see cref="Format"/>, the layout of its file),
/// the name shown, the address of its file of rates, and the currency the rates are against.
/// </summary>
public sealed record RateSource(string Id, string Name, string Url, string? Base, string Format)
{
    public static readonly RateSource Ecb = From(CentralBank.Ecb, "ecb");
    public static readonly RateSource BankOfCanada = From(CentralBank.BankOfCanada, "boc");

    public static RateSource From(CentralBank bank, string id) => new(id, bank.Name, bank.DefaultUrl, bank.Currency, bank.Format);

    /// <summary>The sources every new database starts with.</summary>
    public static readonly IReadOnlyList<RateSource> Defaults = [Ecb, BankOfCanada];
}
