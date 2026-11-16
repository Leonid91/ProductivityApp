using System.Globalization;
using ProductivityApp.Finance.Services;

namespace ProductivityApp.Finance.Testing;

/// <summary>
/// Movements for the transfers timeline: written by hand (<see cref="Movement"/>) or invented from a seed (one to six
/// banks, one to four accounts each, a period of a day to eight years, up to forty movements inside banks, between
/// them, with the outside, some losing part of their money to the banks).
/// </summary>
public static class TimelinePeriods
{
    private static readonly Dictionary<string, long> Ids = [];

    private static long Id(string key)
    {
        lock (Ids) return Ids.TryGetValue(key, out var v) ? v : Ids[key] = Ids.Count + 1;
    }

    public const string Outside = "Outside (not imported)";

    /// <summary>A movement between two accounts (a null bank: the outside), each account its own id by its bank and name.</summary>
    public static MovementView Movement(string date, string from, string? fromBank, string to, string? toBank, decimal amount, decimal? lost = null) =>
        new(date, "transfers", "x", amount, from, to, fromBank is null, toBank is null, 1, null, fromBank is null ? null : "Chequing account", toBank is null ? null : "Savings account",
            fromBank is null ? null : Id(fromBank + "/" + from), toBank is null ? null : Id(toBank + "/" + to), fromBank, toBank, Lost: lost);

    public sealed record Period(List<MovementView> Movements, DateOnly From, DateOnly To);

    public static Period Invented(int seed)
    {
        var rng = new Random(seed);
        var banks = Enumerable.Range(0, rng.Next(1, 7)).Select(b => (Name: $"Bank {(char)('A' + b)} {seed}", Accounts: rng.Next(1, 5))).ToList();
        var accounts = banks.SelectMany(b => Enumerable.Range(0, b.Accounts).Select(a => (Bank: b.Name, Name: rng.Next(3) == 0 ? $"{b.Name} account {a + 1}" : $"Account {a + 1}"))).ToList();
        var from = new DateOnly(2020 + rng.Next(6), rng.Next(1, 13), rng.Next(1, 28));
        var to = from.AddDays(rng.Next(3) switch { 0 => rng.Next(0, 40), 1 => rng.Next(40, 400), _ => rng.Next(400, 3000) });
        var span = to.DayNumber - from.DayNumber;
        var movements = new List<MovementView>();
        for (var m = rng.Next(1, 40); m > 0; m--)
        {
            var x = accounts[rng.Next(accounts.Count)];
            var y = accounts[rng.Next(accounts.Count)];
            var day = from.AddDays(rng.Next(span + 1)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var amount = rng.Next(1, 500000) / 100m;
            var kind = rng.Next(5);
            if (kind == 0) movements.Add(Movement(day, x.Name, x.Bank, Outside, null, amount));
            else if (kind == 1) movements.Add(Movement(day, Outside, null, x.Name, x.Bank, amount));
            else if (x != y) movements.Add(Movement(day, x.Name, x.Bank, y.Name, y.Bank, amount, rng.Next(6) == 0 ? rng.Next(0, 900) / 100m : null));
        }
        if (movements.Count == 0) movements.Add(Movement(from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), accounts[0].Name, accounts[0].Bank, Outside, null, 1m));
        return new Period(movements, from, to);
    }
}
