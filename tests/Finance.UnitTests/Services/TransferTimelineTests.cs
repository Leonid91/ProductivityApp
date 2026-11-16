using System.Globalization;
using ProductivityApp.Finance.Services;
using ProductivityApp.Finance.Testing;
using static ProductivityApp.Finance.Testing.TimelinePeriods;

namespace ProductivityApp.Finance.UnitTests.Services;

/// <summary>
/// The transfers timeline: one line per account, the accounts of a bank together, the outside last; every movement
/// counted once, in the step of its day; movements inside a bank, between two banks and with the outside told apart;
/// what each account gained or gave; steps of a day, a week, a month or a year by the length of the period. Written
/// cases, then 300 invented periods for every rule.
/// </summary>
public class TransferTimelineTests
{
    private static DateOnly D(string iso) => DateOnly.ParseExact(iso, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static TransferTimeline FiveKinds() => TransferTimeline.Build([
        Movement("2026-01-02", "Chequing", "Alpha Bank", "Card", "Alpha Bank", 800m),
        Movement("2026-01-05", "Chequing", "Alpha Bank", "Savings", "Beta Bank", 1000m),
        Movement("2026-01-08", "Savings", "Beta Bank", "Investing", "Beta Bank", 2000m),
        Movement("2026-01-11", "Card", "Alpha Bank", Outside, null, 600m),
        Movement("2026-01-13", Outside, null, "Chequing", "Alpha Bank", 500m),
    ], D("2026-01-01"), D("2026-01-14"));

    [Fact]
    public void Build_NoMovement_ReturnsAnEmptyTimeline()
    {
        var result = TransferTimeline.Build([], D("2026-01-01"), D("2026-01-31"));

        Assert.Empty(result.Lanes);
        Assert.Empty(result.Events);
        Assert.Empty(result.Banks);
        Assert.Equal(0, result.Count);
    }

    [Fact]
    public void Build_MovementsInsideBetweenAndOutsideBanks_TotalsEachKind()
    {
        var result = FiveKinds();

        Assert.Equal((5, 2800m, 1000m, 600m, 500m), (result.Count, result.Inside, result.Between, result.SentOutside, result.ReceivedOutside));
        Assert.Null(result.Lost);
    }

    [Fact]
    public void Build_MovementsInsideBetweenAndOutsideBanks_PutsEachInTheStepOfItsDayWithItsKind()
    {
        var result = FiveKinds();

        Assert.Equal(("day", 14, "2026-01-01"), (result.Unit, result.Slots.Count, result.Slots[0]));
        Assert.Equal(["inside", "between", "inside", "outside", "outside"], result.Events.Select(e => e.Kind));
        Assert.Equal([1, 4, 7, 10, 12], result.Events.Select(e => e.Slot));
    }

    [Fact]
    public void Build_TwoBanks_GroupsTheirAccountsAndPutsTheOutsideLast()
    {
        var result = FiveKinds();

        Assert.Equal((2, 5, true), (result.Banks.Count, result.Lanes.Count, result.Lanes[^1].Outside));
        Assert.All(result.Banks, b => Assert.All(result.Lanes.Skip(b.First).Take(b.Count), l => Assert.Equal(b.Name, l.Bank)));
    }

    [Fact]
    public void Build_AccountThatGaveAndReceived_SaysWhatItGainedAndGave()
    {
        var result = FiveKinds();

        var chequing = result.Lanes.Single(l => l.Name == "Chequing");
        Assert.Equal((500m, 1800m, -1300m), (chequing.In, chequing.Out, chequing.Net));
        Assert.Equal(-1000m, result.Lanes.Single(l => l.Name == "Savings").Net);
        Assert.Equal(2000m, result.Lanes.Single(l => l.Name == "Investing").Net);
    }

    [Fact]
    public void Build_SameTwoAccountsSameWayInOneStep_MergesThemIntoOneEvent()
    {
        var result = TransferTimeline.Build([
            Movement("2026-03-04", "Chequing", "Alpha Bank", "Savings", "Alpha Bank", 100m),
            Movement("2026-03-04", "Chequing", "Alpha Bank", "Savings", "Alpha Bank", 50m),
            Movement("2026-03-04", "Savings", "Alpha Bank", "Chequing", "Alpha Bank", 30m),
            Movement("2026-03-05", "Chequing", "Alpha Bank", "Savings", "Alpha Bank", 20m),
        ], D("2026-03-01"), D("2026-03-31"));

        var merged = result.Events.Single(e => e.Count == 2);
        Assert.Equal((3, 150m, 3), (result.Events.Count, merged.Amount, merged.Slot));
        Assert.Equal((4, 200m), (result.Count, result.Inside));
        Assert.Contains(result.Events, e => e.Amount == 30m && e.From == merged.To && e.To == merged.From);
    }

    [Fact]
    public void Build_AccountsOfOneNameAtTwoBanks_AreTwoLines()
    {
        var result = TransferTimeline.Build([Movement("2026-03-04", "Account", "Alpha Bank", "Account", "Beta Bank", 100m)], D("2026-03-01"), D("2026-03-31"));

        Assert.Equal((2, 2, "between"), (result.Lanes.Count, result.Banks.Count, result.Events.Single().Kind));
    }

    [Fact]
    public void Build_AccountNamedAfterItsBank_IsNamedWithoutItAndKeepsItsFullName()
    {
        var result = TransferTimeline.Build([Movement("2026-03-04", "Alpha Bank savings", "Alpha Bank", "Everyday", "Alpha Bank", 100m)], D("2026-03-01"), D("2026-03-31"));

        var lane = result.Lanes.Single(l => l.Title is not null);
        Assert.Equal(("Savings", "Alpha Bank savings"), (lane.Name, lane.Title));
        Assert.Null(result.Lanes.Single(l => l.Name == "Everyday").Title);
    }

    [Fact]
    public void Build_MovementBetweenCurrencies_CarriesWhatTheBanksKept()
    {
        var across = Movement("2026-03-04", "Compte", "Banque Exemple", "Chequing", "Alpha Bank", 800m, lost: 12.5m) with
        { Sent = 500m, SentCurrency = "EUR", Received = 787.5m, ReceivedCurrency = "CAD" };

        var result = TransferTimeline.Build([across, Movement("2026-03-09", "Chequing", "Alpha Bank", Outside, null, 10m)], D("2026-03-01"), D("2026-03-31"));

        var between = result.Events.Single(x => x.Kind == "between");
        Assert.Equal((12.5m, 500m, "EUR", 787.5m, "CAD"), (between.Lost, between.Sent, between.SentCurrency, between.Received, between.ReceivedCurrency));
        Assert.Equal(12.5m, result.Lost);
        Assert.Null(result.Events.Single(x => x.Kind == "outside").Lost);
    }

    [Theory]
    [InlineData("2026-02-01", "2026-02-28", "day", 28)]
    [InlineData("2026-01-01", "2026-02-14", "day", 45)]
    [InlineData("2026-01-01", "2026-03-31", "week", 13)]
    [InlineData("2026-01-01", "2026-12-31", "month", 12)]
    [InlineData("2025-11-15", "2026-11-11", "month", 13)]
    [InlineData("2019-06-10", "2026-11-11", "year", 8)]
    [InlineData("2026-05-05", "2026-05-05", "day", 1)]
    public void Build_PeriodOfAnyLength_UsesStepsThatFitIt(string from, string to, string unit, int slots)
    {
        var result = TransferTimeline.Build([Movement(from, "Chequing", "Alpha Bank", Outside, null, 10m), Movement(to, Outside, null, "Chequing", "Alpha Bank", 20m)], D(from), D(to));

        Assert.Equal((unit, slots, from), (result.Unit, result.Slots.Count, result.Slots[0]));
        Assert.Equal((0, slots - 1), (result.Events.Single(e => e.Amount == 10m).Slot, result.Events.Single(e => e.Amount == 20m).Slot));
    }

    [Fact]
    public void Build_MovementDatedOutsideThePeriodOrUnreadable_ShowsAtTheNearestEdge()
    {
        var result = TransferTimeline.Build([
            Movement("2025-12-30", "Chequing", "Alpha Bank", "Savings", "Alpha Bank", 10m),
            Movement("2026-02-02", "Savings", "Alpha Bank", "Chequing", "Alpha Bank", 20m),
            Movement("not a date", "Chequing", "Alpha Bank", Outside, null, 30m),
        ], D("2026-01-01"), D("2026-01-31"));

        Assert.Equal([0, 30, 0], new[] { 10m, 20m, 30m }.Select(a => result.Events.Single(e => e.Amount == a).Slot));
    }

    [Fact]
    public void Build_AccountsMovingMoneyTogether_PutsThemSideBySide()
    {
        // Alphabetical order would put Gamma between Alpha and Zeta, which move the most together.
        var result = TransferTimeline.Build([
            Movement("2026-01-02", "A1", "Alpha Bank", "Z1", "Zeta Bank", 10m), Movement("2026-01-03", "A1", "Alpha Bank", "Z1", "Zeta Bank", 10m),
            Movement("2026-01-04", "Z1", "Zeta Bank", "A1", "Alpha Bank", 10m), Movement("2026-01-05", "G1", "Gamma Bank", "G2", "Gamma Bank", 10m),
            Movement("2026-01-06", "G2", "Gamma Bank", "Z1", "Zeta Bank", 10m),
        ], D("2026-01-01"), D("2026-01-31"));

        var at = result.Lanes.Select((l, i) => (l.Name, i)).ToDictionary(x => x.Name, x => x.i);
        Assert.Equal((1, 1, 1), (Math.Abs(at["A1"] - at["Z1"]), Math.Abs(at["G1"] - at["G2"]), Math.Abs(at["G2"] - at["Z1"])));
    }

    [Fact]
    public void Build_AccountGivingThenReceiving_WidensAndNarrowsItsStreamStepByStep()
    {
        // Chequing gives 1000 on the 2nd, receives 300 on the 5th, gives 500 on the 9th; savings receives the 1000,
        // gives the 300 back; the outside receives the 500. Chequing starts with what it gives before receiving more.
        var result = TransferTimeline.Build([
            Movement("2026-03-02", "Chequing", "Bank A", "Savings", "Bank A", 1000m),
            Movement("2026-03-05", "Savings", "Bank A", "Chequing", "Bank A", 300m),
            Movement("2026-03-09", "Chequing", "Bank A", Outside, null, 500m),
        ], D("2026-03-01"), D("2026-03-10"));

        var chequing = result.Lanes.Single(l => l.Name == "Chequing");
        var savings = result.Lanes.Single(l => l.Name == "Savings");
        var outside = result.Lanes.Single(l => l.Outside);
        Assert.Equal(1200m, chequing.Start);
        Assert.Equal([1200m, 200m, 200m, 200m, 500m, 500m, 500m, 500m, 0m, 0m], chequing.Levels);
        Assert.Equal(0m, savings.Start);
        Assert.Equal([0m, 1000m, 1000m, 1000m, 700m, 700m, 700m, 700m, 700m, 700m], savings.Levels);
        Assert.Equal((0m, 500m), (outside.Start, outside.Levels[^1]));
    }

    [Fact]
    public void Build_MovementThatLostPartOnTheWay_GivesTheReceiverOnlyWhatArrived()
    {
        var result = TransferTimeline.Build([Movement("2026-03-02", "Chequing", "Bank A", "Euros", "Bank B", 1000m, lost: 30m)], D("2026-03-01"), D("2026-03-03"));

        var chequing = result.Lanes.Single(l => l.Name == "Chequing");
        Assert.Equal(1000m, chequing.Start);
        Assert.Equal([1000m, 0m, 0m], chequing.Levels);
        Assert.Equal(970m, result.Lanes.Single(l => l.Name == "Euros").Levels[^1]);
    }

    // ------------------------------------------------------------------------------------------- 300 invented periods

    public static TheoryData<int> InventedPeriods => [.. Enumerable.Range(20261111, 300)];

    [Theory]
    [MemberData(nameof(InventedPeriods))]
    public void Build_InventedPeriod_CountsEveryMovementOnceInOneOfTheFourKinds(int seed)
    {
        var (movements, from, to) = Invented(seed);

        var t = TransferTimeline.Build(movements, from, to);

        Assert.Equal((movements.Count, movements.Count), (t.Count, t.Events.Sum(e => e.Count)));
        Assert.Equal(movements.Sum(m => m.Amount), t.Events.Sum(e => e.Amount));
        Assert.Equal(movements.Sum(m => m.Amount), t.Inside + t.Between + t.SentOutside + t.ReceivedOutside);
        Assert.Equal(movements.Where(m => m.ToOutside).Sum(m => m.Amount), t.SentOutside);
        Assert.Equal(movements.Where(m => m.FromOutside).Sum(m => m.Amount), t.ReceivedOutside);
        Assert.Equal(movements.Where(m => !m.FromOutside && !m.ToOutside && m.FromBank == m.ToBank).Sum(m => m.Amount), t.Inside);
        Assert.Equal(movements.Any(m => m.Lost is not null) ? movements.Sum(m => m.Lost ?? 0) : null, t.Lost);
    }

    [Theory]
    [MemberData(nameof(InventedPeriods))]
    public void Build_InventedPeriod_HasOneLinePerAccountEachBankTogetherOnceAndTheOutsideLast(int seed)
    {
        var (movements, from, to) = Invented(seed);

        var t = TransferTimeline.Build(movements, from, to);

        var used = movements.SelectMany(m => new[] { (m.FromAccount, m.FromOutside), (m.ToAccount, m.ToOutside) }).Where(a => !a.Item2).Select(a => a.Item1).Distinct().Count();
        Assert.Equal(used, t.Lanes.Count(l => !l.Outside));
        Assert.InRange(t.Lanes.Count(l => l.Outside), 0, 1);
        Assert.True(!t.Lanes.Any(l => l.Outside) || t.Lanes[^1].Outside);
        Assert.Equal(t.Banks.Count, t.Banks.Select(b => b.Name).Distinct().Count());
        Assert.Equal(t.Lanes.Count(l => !l.Outside), t.Banks.Sum(b => b.Count));
        Assert.All(t.Banks, b => Assert.All(t.Lanes.Skip(b.First).Take(b.Count), l => Assert.Equal(b.Name, l.Bank)));
    }

    [Theory]
    [MemberData(nameof(InventedPeriods))]
    public void Build_InventedPeriod_MoneyOnlyMovesBetweenLines(int seed)
    {
        var (movements, from, to) = Invented(seed);

        var t = TransferTimeline.Build(movements, from, to);

        Assert.Equal(0m, t.Lanes.Sum(l => l.Net));
        Assert.All(t.Lanes, l => Assert.Equal(l.In - l.Out, l.Net));
    }

    [Theory]
    [MemberData(nameof(InventedPeriods))]
    public void Build_InventedPeriod_StepsAreInOrderFromTheFirstDayAndHoldEachEventOnItsDay(int seed)
    {
        var (movements, from, to) = Invented(seed);

        var t = TransferTimeline.Build(movements, from, to);

        Assert.Equal((TransferTimeline.UnitOf(from, to), from), (t.Unit, D(t.Slots[0])));
        Assert.True(D(t.Slots[^1]) <= to);
        Assert.Equal(t.Slots.Order(StringComparer.Ordinal), t.Slots);
        Assert.InRange(t.Slots.Count, 1, 200);
        Assert.All(t.Events, e =>
        {
            Assert.InRange(e.Slot, 0, t.Slots.Count - 1);
            Assert.True(D(t.Slots[e.Slot]) <= D(e.Date) && (e.Slot == t.Slots.Count - 1 || D(e.Date) < D(t.Slots[e.Slot + 1])), e.Date);
        });
    }

    [Theory]
    [MemberData(nameof(InventedPeriods))]
    public void Build_InventedPeriod_GivesEachEventTheKindOfItsTwoLinesOnceAStep(int seed)
    {
        var (movements, from, to) = Invented(seed);

        var t = TransferTimeline.Build(movements, from, to);

        Assert.All(t.Events, e =>
        {
            Assert.NotEqual(e.From, e.To);
            var (a, b) = (t.Lanes[e.From], t.Lanes[e.To]);
            Assert.Equal(a.Outside || b.Outside ? "outside" : a.Bank == b.Bank ? "inside" : "between", e.Kind);
        });
        Assert.Equal(t.Events.Count, t.Events.Select(e => (e.Slot, e.From, e.To)).Distinct().Count());
    }

    [Theory]
    [MemberData(nameof(InventedPeriods))]
    public void Build_InventedPeriod_EachStreamChangesByWhatCameInLessWhatWentOutAndNeverGoesBelowNothing(int seed)
    {
        var (movements, from, to) = Invented(seed);

        var t = TransferTimeline.Build(movements, from, to);

        for (var li = 0; li < t.Lanes.Count; li++)
        {
            var l = t.Lanes[li];
            Assert.Equal(t.Slots.Count, l.Levels.Count);
            Assert.True(l.Start >= 0 && l.Levels.All(v => v >= 0));
            Assert.Equal(0m, Math.Min(l.Start, l.Levels.Min()));
            var expected = new List<decimal>();
            var level = l.Start;
            for (var sl = 0; sl < t.Slots.Count; sl++)
            {
                level += t.Events.Where(e => e.Slot == sl && e.To == li).Sum(TransferTimeline.Arrived) - t.Events.Where(e => e.Slot == sl && e.From == li).Sum(e => e.Amount);
                expected.Add(level);
            }
            Assert.Equal(expected, l.Levels);
        }
        Assert.Equal(t.Lanes.Sum(l => l.Start) - t.Events.Sum(e => e.Amount - TransferTimeline.Arrived(e)), t.Lanes.Sum(l => l.Levels[^1]));
    }

    [Theory]
    [MemberData(nameof(InventedPeriods))]
    public void Build_SameMovementsTwice_DrawsTheSameLines(int seed)
    {
        var (movements, from, to) = Invented(seed);

        var again = TransferTimeline.Build(movements, from, to);

        Assert.Equal(TransferTimeline.Build(movements, from, to).Lanes.Select(l => l.Id), again.Lanes.Select(l => l.Id));
    }
}
