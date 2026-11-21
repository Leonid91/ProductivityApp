namespace ProductivityApp.Finance.Model;

/// <summary>A bank whose statements are read by themselves.</summary>
/// <param name="Considered">The reader was made and tested for its statements and exports, in their layouts.</param>
/// <param name="Confirmed">A real statement of the bank was read.</param>
public sealed record SupportedBank(string Name, bool Considered, bool Confirmed);

/// <summary>The banks of a country (its ISO 3166 code: CA, FR, GB).</summary>
public sealed record SupportedCountry(string Code, string Name, IReadOnlyList<SupportedBank> Banks);

/// <summary>
/// The banks whose statements and exports are read by themselves, by country, as shown in Settings > Information. Any
/// other bank is read too (columns found from the statement itself, or set once by hand): these are the ones made and
/// tested for (Considered) and the ones read from real statements (Confirmed).
/// </summary>
public static class SupportedBanks
{
    public static readonly IReadOnlyList<SupportedCountry> All =
    [
        new("CA", "Canada", [new(KnownBanks.National, true, true), new("Scotiabank", true, true)]),
        new("FR", "France", [new("BoursoBank", true, false), new("Caisse d'Epargne", true, false), new("CIC", true, false), new("Fortuneo", true, false)]),
        new("GB", "United Kingdom", [new("Revolut", true, false)]),
    ];
}
