using System.Collections.Concurrent;
using System.Text.Json;
using ProductivityApp.Finance.Services;

namespace ProductivityApp.Finance.Testing;

/// <summary>
/// Movements for the transfers timeline: written by hand (<see cref="Movement"/>, <see cref="MovementOut"/>,
/// <see cref="MovementIn"/>) or 300 invented periods (one to six banks, one to four accounts each, a period of a day to
/// eight years, up to forty movements inside banks, between them, with the outside, some losing part of their money to
/// the banks), kept as data (InventedPeriods.json) under the seed that made them.
/// </summary>
public static class TimelinePeriods
{
    private static long _ids;
    private static readonly ConcurrentDictionary<string, long> Ids = new(StringComparer.Ordinal);

    /// <summary>Each account its own id, by its bank and name.</summary>
    private static long Id(string bank, string account) => Ids.GetOrAdd(bank + "/" + account, _ => Interlocked.Increment(ref _ids));

    public const string Outside = "Outside (not imported)";

    /// <summary>A movement from a chequing account to a savings account (the banks may keep <paramref name="lost"/> of it).</summary>
    public static MovementView Movement(string date, string from, string fromBank, string to, string toBank, decimal amount, decimal? lost = null) =>
        new(date, "transfers", "x", amount, from, to, false, false, 1, null, "Chequing account", "Savings account", Id(fromBank, from), Id(toBank, to), fromBank, toBank, Lost: lost);

    /// <summary>A movement from a chequing account to the outside.</summary>
    public static MovementView MovementOut(string date, string from, string fromBank, decimal amount) =>
        new(date, "transfers", "x", amount, from, Outside, false, true, 1, null, "Chequing account", null, Id(fromBank, from), null, fromBank, null);

    /// <summary>A movement from the outside to a savings account.</summary>
    public static MovementView MovementIn(string date, string to, string toBank, decimal amount) =>
        new(date, "transfers", "x", amount, Outside, to, true, false, 1, null, null, "Savings account", null, Id(toBank, to), null, toBank);

    public sealed record Period(List<MovementView> Movements, DateOnly From, DateOnly To);

    private static readonly Lazy<Dictionary<int, Period>> InventedPeriods = new(() =>
        JsonSerializer.Deserialize<Dictionary<int, Period>>(typeof(TimelinePeriods).Assembly.GetManifestResourceStream("Diagrams.InventedPeriods.json")!)!);

    /// <summary>The seeds of the invented periods.</summary>
    public static IEnumerable<int> InventedSeeds => InventedPeriods.Value.Keys.Order();

    /// <summary>The invented period of <paramref name="seed"/>.</summary>
    public static Period Invented(int seed) => InventedPeriods.Value[seed];
}
