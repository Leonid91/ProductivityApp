using ProductivityApp.Finance.Model;

namespace ProductivityApp.Finance.UnitTests.Model;

/// <summary>The kinds of account and their names in each banking system (AccountTypes, in Entities.cs).</summary>
public class EntitiesTests
{
    [Theory]
    [InlineData(AccountKind.Bank, BankingSystem.France, "Current account (debit card)")]
    [InlineData(AccountKind.CreditCard, BankingSystem.France, "Deferred debit card")]
    public void Label_FrenchAccountKind_ReturnsTheFrenchSystemsName(AccountKind kind, BankingSystem system, string label)
    {
        var result = AccountTypes.Label(kind, system);

        Assert.Equal(label, result);
    }
}
