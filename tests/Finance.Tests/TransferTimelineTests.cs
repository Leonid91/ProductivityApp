using System.Globalization;
using ProductivityApp.Finance.Services;

namespace ProductivityApp.Finance.Tests;

/// <summary>
/// The transfers timeline, whatever it shows: one line per account, the accounts of a bank together, the outside last;
/// every movement counted once, in the step of its day; movements inside a bank, between two banks and with the
/// outside told apart; what each account gained or gave; steps of a day, a week, a month or a year by the length of the
/// period. Checked on written cases and on hundreds of invented ones.
/// </summary>
public class TransferTimelineTests
{
    private static readonly Dictionary<string, long> Ids = [];

    private static long Id(string key)
    {
        lock (Ids) return Ids.TryGetValue(key, out var v) ? v : Ids[key] = Ids.Count + 1;
    }

    /// <summary>A movement between two accounts (a null bank: the outside), each account its own id by its bank and name.</summary>
    private static MovementView M(string date, string from, string? fromBank, string to, string? toBank, decimal amount, decimal? lost = null) =>
        new(date, "transfers", "x", amount, from, to, fromBank is null, toBank is null, 1, null, fromBank is null ? null : "Chequing account", toBank is null ? null : "Savings account",
            fromBank is null ? null : Id(fromBank + "/" + from), toBank is null ? null : Id(toBank + "/" + to), fromBank, toBank, Lost: lost);

    private static DateOnly D(string iso) => DateOnly.ParseExact(iso, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private const string Out = "Outside (not imported)";

    [Fact]
    public void No_movement_gives_an_empty_timeline()
    {
        var t = TransferTimeline.Build([], D("2026-01-01"), D("2026-01-31"));
        Assert.Empty(t.Lanes);
        Assert.Empty(t.Events);
        Assert.Empty(t.Banks);
        Assert.Equal(0, t.Count);
    }

    [Fact]
    public void Inside_a_bank_between_banks_and_outside_are_told_apart_and_totalled()
    {
        var t = TransferTimeline.Build([
            M("2026-01-02", "Chequing", "Alpha Bank", "Card", "Alpha Bank", 800m),
            M("2026-01-05", "Chequing", "Alpha Bank", "Savings", "Beta Bank", 1000m),
            M("2026-01-08", "Savings", "Beta Bank", "Investing", "Beta Bank", 2000m),
            M("2026-01-11", "Card", "Alpha Bank", Out, null, 600m),
            M("2026-01-13", Out, null, "Chequing", "Alpha Bank", 500m),
        ], D("2026-01-01"), D("2026-01-14"));

        Assert.Equal("day", t.Unit);
        Assert.Equal(14, t.Slots.Count);
        Assert.Equal("2026-01-01", t.Slots[0]);
        Assert.Equal(5, t.Count);
        Assert.Equal(2800m, t.Inside);
        Assert.Equal(1000m, t.Between);
        Assert.Equal(600m, t.SentOutside);
        Assert.Equal(500m, t.ReceivedOutside);
        Assert.Null(t.Lost);
        Assert.Equal(["inside", "between", "inside", "outside", "outside"], t.Events.Select(e => e.Kind));
        Assert.Equal([1, 4, 7, 10, 12], t.Events.Select(e => e.Slot));

        // Two banks, their accounts together; the outside last.
        Assert.Equal(2, t.Banks.Count);
        Assert.Equal(5, t.Lanes.Count);
        Assert.True(t.Lanes[^1].Outside);
        foreach (var b in t.Banks)
            Assert.All(t.Lanes.Skip(b.First).Take(b.Count), l => Assert.Equal(b.Name, l.Bank));

        var chequing = t.Lanes.Single(l => l.Name == "Chequing");
        Assert.Equal(500m, chequing.In);
        Assert.Equal(1800m, chequing.Out);
        Assert.Equal(-1300m, chequing.Net);
        Assert.Equal(-1000m, t.Lanes.Single(l => l.Name == "Savings").Net);
        Assert.Equal(2000m, t.Lanes.Single(l => l.Name == "Investing").Net);
    }

    [Fact]
    public void Movements_between_the_same_two_accounts_in_one_step_are_one_event_each_way_its_own()
    {
        var t = TransferTimeline.Build([
            M("2026-03-04", "Chequing", "Alpha Bank", "Savings", "Alpha Bank", 100m),
            M("2026-03-04", "Chequing", "Alpha Bank", "Savings", "Alpha Bank", 50m),
            M("2026-03-04", "Savings", "Alpha Bank", "Chequing", "Alpha Bank", 30m),
            M("2026-03-05", "Chequing", "Alpha Bank", "Savings", "Alpha Bank", 20m),
        ], D("2026-03-01"), D("2026-03-31"));

        Assert.Equal(3, t.Events.Count);
        var merged = t.Events.Single(e => e.Count == 2);
        Assert.Equal(150m, merged.Amount);
        Assert.Equal(3, merged.Slot);
        Assert.Equal(4, t.Count);
        Assert.Equal(200m, t.Inside);
        Assert.Contains(t.Events, e => e.Amount == 30m && e.From == merged.To && e.To == merged.From);
    }

    [Fact]
    public void Two_accounts_with_one_name_at_two_banks_are_two_lines()
    {
        var t = TransferTimeline.Build([M("2026-03-04", "Account", "Alpha Bank", "Account", "Beta Bank", 100m)], D("2026-03-01"), D("2026-03-31"));
        Assert.Equal(2, t.Lanes.Count);
        Assert.Equal(2, t.Banks.Count);
        Assert.Equal("between", t.Events.Single().Kind);
    }

    [Fact]
    public void An_account_named_after_its_bank_is_named_without_it_and_keeps_its_full_name()
    {
        var t = TransferTimeline.Build([M("2026-03-04", "Alpha Bank savings", "Alpha Bank", "Everyday", "Alpha Bank", 100m)], D("2026-03-01"), D("2026-03-31"));
        var lane = t.Lanes.Single(l => l.Title is not null);
        Assert.Equal("Savings", lane.Name);
        Assert.Equal("Alpha Bank savings", lane.Title);
        Assert.Null(t.Lanes.Single(l => l.Name == "Everyday").Title);
    }

    [Fact]
    public void What_the_banks_kept_is_carried_by_the_event_and_totalled()
    {
        var across = M("2026-03-04", "Compte", "Banque Exemple", "Chequing", "Alpha Bank", 800m, lost: 12.5m) with
        { Sent = 500m, SentCurrency = "EUR", Received = 787.5m, ReceivedCurrency = "CAD" };
        var t = TransferTimeline.Build([across, M("2026-03-09", "Chequing", "Alpha Bank", Out, null, 10m)], D("2026-03-01"), D("2026-03-31"));
        var e = t.Events.Single(x => x.Kind == "between");
        Assert.Equal(12.5m, e.Lost);
        Assert.Equal((500m, "EUR", 787.5m, "CAD"), (e.Sent, e.SentCurrency, e.Received, e.ReceivedCurrency));
        Assert.Equal(12.5m, t.Lost);
        Assert.Null(t.Events.Single(x => x.Kind == "outside").Lost);
    }

    [Theory]
    [InlineData("2026-02-01", "2026-02-28", "day", 28)]
    [InlineData("2026-01-01", "2026-02-14", "day", 45)]
    [InlineData("2026-01-01", "2026-03-31", "week", 13)]
    [InlineData("2026-01-01", "2026-12-31", "month", 12)]
    [InlineData("2025-11-15", "2026-11-11", "month", 13)]
    [InlineData("2019-06-10", "2026-11-11", "year", 8)]
    [InlineData("2026-05-05", "2026-05-05", "day", 1)]
    public void Steps_follow_the_length_of_the_period(string from, string to, string unit, int slots)
    {
        var t = TransferTimeline.Build([M(from, "Chequing", "Alpha Bank", Out, null, 10m), M(to, Out, null, "Chequing", "Alpha Bank", 20m)], D(from), D(to));
        Assert.Equal(unit, t.Unit);
        Assert.Equal(slots, t.Slots.Count);
        Assert.Equal(from, t.Slots[0]);
        Assert.Equal(0, t.Events.Single(e => e.Amount == 10m).Slot);
        Assert.Equal(slots - 1, t.Events.Single(e => e.Amount == 20m).Slot);
    }

    [Fact]
    public void A_movement_dated_outside_the_period_shows_at_its_edge()
    {
        var t = TransferTimeline.Build([
            M("2025-12-30", "Chequing", "Alpha Bank", "Savings", "Alpha Bank", 10m),
            M("2026-02-02", "Savings", "Alpha Bank", "Chequing", "Alpha Bank", 20m),
            M("not a date", "Chequing", "Alpha Bank", Out, null, 30m),
        ], D("2026-01-01"), D("2026-01-31"));
        Assert.Equal(0, t.Events.Single(e => e.Amount == 10m).Slot);
        Assert.Equal(30, t.Events.Single(e => e.Amount == 20m).Slot);
        Assert.Equal(0, t.Events.Single(e => e.Amount == 30m).Slot);
    }

    [Fact]
    public void Accounts_moving_money_together_are_put_side_by_side()
    {
        // Alphabetical order would put Gamma between Alpha and Zeta, which move the most together.
        var t = TransferTimeline.Build([
            M("2026-01-02", "A1", "Alpha Bank", "Z1", "Zeta Bank", 10m), M("2026-01-03", "A1", "Alpha Bank", "Z1", "Zeta Bank", 10m),
            M("2026-01-04", "Z1", "Zeta Bank", "A1", "Alpha Bank", 10m), M("2026-01-05", "G1", "Gamma Bank", "G2", "Gamma Bank", 10m),
            M("2026-01-06", "G2", "Gamma Bank", "Z1", "Zeta Bank", 10m),
        ], D("2026-01-01"), D("2026-01-31"));
        var at = t.Lanes.Select((l, i) => (l.Name, i)).ToDictionary(x => x.Name, x => x.i);
        Assert.Equal(1, Math.Abs(at["A1"] - at["Z1"]));
        Assert.Equal(1, Math.Abs(at["G1"] - at["G2"]));
        Assert.Equal(1, Math.Abs(at["G2"] - at["Z1"]));
    }

    [Fact]
    public void Each_stream_widens_with_what_comes_in_and_narrows_with_what_leaves()
    {
        // Chequing gives 1000 on the 2nd, receives 300 on the 5th, gives 500 on the 9th; savings receives the 1000,
        // gives the 300 back; the outside receives the 500.
        var t = TransferTimeline.Build([
            M("2026-03-02", "Chequing", "Bank A", "Savings", "Bank A", 1000m),
            M("2026-03-05", "Savings", "Bank A", "Chequing", "Bank A", 300m),
            M("2026-03-09", "Chequing", "Bank A", Out, null, 500m),
        ], D("2026-03-01"), D("2026-03-10"));
        var chequing = t.Lanes.Single(l => l.Name == "Chequing");
        var savings = t.Lanes.Single(l => l.Name == "Savings");
        var outside = t.Lanes.Single(l => l.Outside);
        // Chequing gives 1200 in all and receives 300 between: it starts with 1200, ends with nothing.
        Assert.Equal(1200m, chequing.Start);
        Assert.Equal([1200m, 200m, 200m, 200m, 500m, 500m, 500m, 500m, 0m, 0m], chequing.Levels);
        Assert.Equal(0m, savings.Start);
        Assert.Equal([0m, 1000m, 1000m, 1000m, 700m, 700m, 700m, 700m, 700m, 700m], savings.Levels);
        Assert.Equal(0m, outside.Start);
        Assert.Equal(500m, outside.Levels[^1]);
    }

    [Fact]
    public void A_stream_receives_what_arrives_after_the_banks_kept_their_part()
    {
        var t = TransferTimeline.Build([M("2026-03-02", "Chequing", "Bank A", "Euros", "Bank B", 1000m, lost: 30m)], D("2026-03-01"), D("2026-03-03"));
        var chequing = t.Lanes.Single(l => l.Name == "Chequing");
        Assert.Equal(1000m, chequing.Start);
        Assert.Equal([1000m, 0m, 0m], chequing.Levels);
        Assert.Equal(970m, t.Lanes.Single(l => l.Name == "Euros").Levels[^1]);
    }

    [Fact]
    public void Three_hundred_invented_periods_keep_every_rule()
    {
        var rng = new Random(20261111);
        for (var n = 0; n < 300; n++)
        {
            var banks = Enumerable.Range(0, rng.Next(1, 7)).Select(b => (Name: $"Bank {(char)('A' + b)} {n}", Accounts: rng.Next(1, 5))).ToList();
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
                if (kind == 0) movements.Add(M(day, x.Name, x.Bank, Out, null, amount));
                else if (kind == 1) movements.Add(M(day, Out, null, x.Name, x.Bank, amount));
                else if (x != y) movements.Add(M(day, x.Name, x.Bank, y.Name, y.Bank, amount, rng.Next(6) == 0 ? rng.Next(0, 900) / 100m : null));
            }
            var t = TransferTimeline.Build(movements, from, to);
            var again = TransferTimeline.Build(movements, from, to);
            var label = $"case {n}";

            if (movements.Count == 0) { Assert.Empty(t.Lanes); continue; }
            // The same movements, the same drawing.
            Assert.Equal(t.Lanes.Select(l => l.Id), again.Lanes.Select(l => l.Id));
            // Every movement counted once, in one of the four kinds.
            Assert.Equal(movements.Count, t.Count);
            Assert.Equal(movements.Count, t.Events.Sum(e => e.Count));
            Assert.Equal(movements.Sum(m => m.Amount), t.Events.Sum(e => e.Amount));
            Assert.Equal(movements.Sum(m => m.Amount), t.Inside + t.Between + t.SentOutside + t.ReceivedOutside);
            Assert.Equal(movements.Where(m => m.ToOutside).Sum(m => m.Amount), t.SentOutside);
            Assert.Equal(movements.Where(m => m.FromOutside).Sum(m => m.Amount), t.ReceivedOutside);
            Assert.Equal(movements.Where(m => !m.FromOutside && !m.ToOutside && m.FromBank == m.ToBank).Sum(m => m.Amount), t.Inside);
            Assert.Equal(movements.Any(m => m.Lost is not null) ? movements.Sum(m => m.Lost ?? 0) : (decimal?)null, t.Lost);
            // One line per account that moved money, the outside last, each bank's lines together and each bank once.
            var used = movements.SelectMany(m => new[] { (m.FromAccount, m.FromOutside), (m.ToAccount, m.ToOutside) }).Where(a => !a.Item2).Select(a => a.Item1).Distinct().Count();
            Assert.Equal(used, t.Lanes.Count(l => !l.Outside));
            Assert.True(t.Lanes.Count(l => l.Outside) <= 1, label);
            if (t.Lanes.Any(l => l.Outside)) Assert.True(t.Lanes[^1].Outside, label);
            Assert.Equal(t.Banks.Count, t.Banks.Select(b => b.Name).Distinct().Count());
            Assert.Equal(t.Lanes.Count(l => !l.Outside), t.Banks.Sum(b => b.Count));
            foreach (var b in t.Banks) Assert.All(t.Lanes.Skip(b.First).Take(b.Count), l => Assert.Equal(b.Name, l.Bank));
            // Money only moves: what the lines gained and gave adds up to nothing.
            Assert.Equal(0m, t.Lanes.Sum(l => l.Net));
            Assert.All(t.Lanes, l => Assert.Equal(l.In - l.Out, l.Net));
            // Steps: in order, the first on the first day, none after the last; every event in a step, on its day.
            Assert.Equal(TransferTimeline.UnitOf(from, to), t.Unit);
            Assert.Equal(from, D(t.Slots[0]));
            Assert.True(D(t.Slots[^1]) <= to, label);
            Assert.Equal(t.Slots.OrderBy(s => s, StringComparer.Ordinal), t.Slots);
            Assert.True(t.Slots.Count <= 200, $"{label}: {t.Slots.Count} steps");
            foreach (var e in t.Events)
            {
                Assert.InRange(e.Slot, 0, t.Slots.Count - 1);
                Assert.True(D(t.Slots[e.Slot]) <= D(e.Date) && (e.Slot == t.Slots.Count - 1 || D(e.Date) < D(t.Slots[e.Slot + 1])), label);
                Assert.NotEqual(e.From, e.To);
                var (a, b) = (t.Lanes[e.From], t.Lanes[e.To]);
                Assert.Equal(a.Outside || b.Outside ? "outside" : a.Bank == b.Bank ? "inside" : "between", e.Kind);
            }
            // No two events for the same two lines, the same way, in one step.
            Assert.Equal(t.Events.Count, t.Events.Select(e => (e.Slot, e.From, e.To)).Distinct().Count());
            // Streams: one width per step, never below nothing, touching nothing at their narrowest (they start with
            // only what they give before receiving more), changed at each step by what came in less what went out.
            for (var li = 0; li < t.Lanes.Count; li++)
            {
                var l = t.Lanes[li];
                Assert.Equal(t.Slots.Count, l.Levels.Count);
                Assert.True(l.Start >= 0 && l.Levels.All(v => v >= 0), label);
                Assert.Equal(0m, Math.Min(l.Start, l.Levels.Min()));
                for (var sl = 0; sl < t.Slots.Count; sl++)
                {
                    var before = sl == 0 ? l.Start : l.Levels[sl - 1];
                    var change = t.Events.Where(e => e.Slot == sl && e.To == li).Sum(TransferTimeline.Arrived) - t.Events.Where(e => e.Slot == sl && e.From == li).Sum(e => e.Amount);
                    Assert.Equal(before + change, l.Levels[sl]);
                }
            }
            // All the streams together hold the same money all along, less what the banks kept.
            Assert.Equal(t.Lanes.Sum(l => l.Start) - t.Events.Sum(e => e.Amount - TransferTimeline.Arrived(e)), t.Lanes.Sum(l => l.Levels[^1]));
        }
    }
}
