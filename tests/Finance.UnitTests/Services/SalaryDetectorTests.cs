using System.Globalization;
using ProductivityApp.Finance.Model;
using ProductivityApp.Finance.Services;

namespace ProductivityApp.Finance.UnitTests.Services;

/// <summary>The salary learned from the history: one payer, again and again, on a pay cycle, at about the same amount.</summary>
public sealed class SalaryDetectorTests
{
    private static SalaryDetector.Line Pay(int index, string day, decimal amount, string payer = "ACME", string currency = "CAD") =>
        new(index, DateOnly.Parse(day, CultureInfo.InvariantCulture), amount, currency, payer);

    /// <summary>Six pays of about 2,000, every two weeks from January 2.</summary>
    private static List<SalaryDetector.Line> EveryTwoWeeks() =>
    [
        Pay(0, "2026-01-02", 2000m), Pay(1, "2026-01-16", 2001m), Pay(2, "2026-01-30", 2002m),
        Pay(3, "2026-02-13", 2000m), Pay(4, "2026-02-27", 2001m), Pay(5, "2026-03-13", 2002m),
    ];

    public static TheoryData<string, SalaryDetector.Line[]> Cycles => new()
    {
        { "weekly", [Pay(0, "2026-01-02", 500m), Pay(1, "2026-01-09", 500m), Pay(2, "2026-01-16", 510m), Pay(3, "2026-01-23", 500m), Pay(4, "2026-01-30", 505m)] },
        { "every-two-weeks", [.. EveryTwoWeeks()] },
        { "15th-and-last-day", [Pay(0, "2026-01-15", 1500m), Pay(1, "2026-01-30", 1500m), Pay(2, "2026-02-13", 1500m), Pay(3, "2026-02-27", 1500m), Pay(4, "2026-03-16", 1500m), Pay(5, "2026-03-31", 1500m)] },
        { "monthly-moved-by-weekends", [Pay(0, "2026-01-30", 2400m), Pay(1, "2026-02-27", 2400m), Pay(2, "2026-03-31", 2450m), Pay(3, "2026-04-30", 2450m)] },
    };

    [Theory]
    [MemberData(nameof(Cycles))]
    public void Find_PaysOnACycle_AreSalaries(string cycle, SalaryDetector.Line[] pays)
    {
        Assert.True(SalaryDetector.Find(pays).Count == pays.Length, cycle);
    }

    [Fact]
    public void Find_TwoPaysOnly_AreNotEnough() =>
        Assert.Empty(SalaryDetector.Find([Pay(0, "2026-01-02", 2000m), Pay(1, "2026-01-16", 2000m)]));

    [Fact]
    public void Find_ExpensesPaidBackByTheEmployerOffTheCycle_StayOtherIncome()
    {
        // Paid back between two pays, at about a pay's amount.
        List<SalaryDetector.Line> lines = [.. EveryTwoWeeks(), Pay(99, "2026-01-23", 1900m)];

        var found = SalaryDetector.Find(lines);

        Assert.Equal((6, false), (found.Count, found.Contains(99)));
    }

    [Fact]
    public void Find_PaymentFarFromTheUsualAmountOnAPayDay_StaysOtherIncome() =>
        Assert.DoesNotContain(99, SalaryDetector.Find([.. EveryTwoWeeks(), Pay(99, "2026-01-30", 6500m)]));

    [Fact]
    public void Find_MissedPay_KeepsTheOthers() =>
        Assert.Equal(5, SalaryDetector.Find([Pay(0, "2026-01-02", 2000m), Pay(1, "2026-01-16", 2000m), Pay(2, "2026-01-30", 2000m), Pay(4, "2026-02-27", 2000m), Pay(5, "2026-03-13", 2000m)]).Count);

    [Fact]
    public void Find_PaymentsOnNoCycle_AreNotSalaries() =>
        Assert.Empty(SalaryDetector.Find([Pay(0, "2026-01-03", 300m), Pay(1, "2026-01-21", 280m), Pay(2, "2026-03-02", 310m), Pay(3, "2026-03-12", 290m), Pay(4, "2026-05-29", 305m)]));

    [Fact]
    public void Find_TwoEmployers_AreBothSalaries() =>
        Assert.Equal(10, SalaryDetector.Find([.. EveryTwoWeeks(),
            Pay(10, "2026-01-28", 900m, "BETA"), Pay(11, "2026-02-27", 900m, "BETA"), Pay(12, "2026-03-30", 900m, "BETA"), Pay(13, "2026-04-28", 900m, "BETA")]).Count);

    [Fact]
    public void Find_PaysInTwoCurrencies_AreTwoSeries() =>
        Assert.Empty(SalaryDetector.Find([Pay(0, "2026-01-30", 2400m, currency: "CAD"), Pay(1, "2026-02-27", 2400m, currency: "EUR"), Pay(2, "2026-03-31", 2400m, currency: "CAD")]));

    // ------------------------------------------------------------------------------------------------ what may be a salary

    [Theory]
    [InlineData("DEPOT DIRECT EXEMPLE INC", "otherincome", "")]
    [InlineData("PAYROLL DEPOSIT EXAMPLE CORP", "salary", "")]
    [InlineData("Dépôt direct Exemple", "otherincome", "Revenus")]
    public void Candidate_IncomeTheAppFiled_MayBeASalary(string label, string category, string bankCategory)
    {
        Assert.True(SalaryDetector.Candidate(2000m, category, ValueSource.Keyword, false, label, bankCategory, BankingSystem.NorthAmerica));
    }

    [Theory]
    [InlineData("INTERETS CREDITES", "")]
    [InlineData("DEPOT DIRECT ALLOCATION CANADIENNE ENFANTS", "")]
    [InlineData("DEPOT DIRECT GOUV DU QUEBEC", "")]
    [InlineData("VIR SEPA RECU CAF DES YVELINES", "")]
    [InlineData("Dépôt", "Intérêts")]
    public void Candidate_IncomeNamedAsSomethingElse_IsNever(string label, string bankCategory)
    {
        Assert.False(SalaryDetector.Candidate(300m, "otherincome", ValueSource.Keyword, false, label, bankCategory, BankingSystem.NorthAmerica));
    }

    [Theory]
    [InlineData(ValueSource.User)]
    [InlineData(ValueSource.Learned)]
    public void Candidate_CategoryThePersonChose_IsKept(ValueSource source) =>
        Assert.False(SalaryDetector.Candidate(2000m, "otherincome", source, false, "DEPOT DIRECT EXEMPLE INC", "", BankingSystem.NorthAmerica));

    [Theory]
    [InlineData(-2000, false)]
    [InlineData(2000, true)]
    public void Candidate_MoneyOutOrBetweenOwnAccounts_IsNever(int amount, bool linked)
    {
        Assert.False(SalaryDetector.Candidate(amount, "otherincome", ValueSource.Keyword, linked, "DEPOT DIRECT EXEMPLE INC", "", BankingSystem.NorthAmerica));
    }

    [Theory]
    [InlineData("VIR SEPA RECU /DE EXEMPLE SAS /MOTIF OCTOBRE", true)]
    [InlineData("VIR SEPA RECU /DE M DUPONT JEAN", false)]
    public void Candidate_TransferReceived_MayBeASalaryOnlyFromACompany(string label, bool candidate)
    {
        Assert.Equal(candidate, SalaryDetector.Candidate(2100m, Categories.Transfers, ValueSource.Keyword, false, label, "", BankingSystem.France));
    }
}
