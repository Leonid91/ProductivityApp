using ProductivityApp.Finance.Classification;
using ProductivityApp.Finance.Model;

namespace ProductivityApp.Finance.UnitTests.Classification;

/// <summary>The data the engine ships with (built-in brands, categories, account types) is consistent.</summary>
public class CatalogTests
{
    [Fact]
    public void BuiltInBrands_All_ReferenceOnlyExistingCategories()
    {
        var brands = BuiltInBrands.All;

        Assert.All(brands, b => Assert.True(Categories.Exists(b.CategoryId), b.Brand));
    }

    [Theory]
    [InlineData(AccountKind.Bank, BankingSystem.France, "Current account (debit card)")]
    [InlineData(AccountKind.CreditCard, BankingSystem.France, "Deferred debit card")]
    public void AccountTypesLabel_FrenchAccountKind_ReturnsTheFrenchSystemsName(AccountKind kind, BankingSystem system, string label)
    {
        var result = AccountTypes.Label(kind, system);

        Assert.Equal(label, result);
    }

    [Fact]
    public void CategoriesGet_CardPayments_IsNamedCardStatementPayments()
    {
        var category = Categories.Get(Categories.CardPayments);

        Assert.Equal("Card statement payments", category.Name);
    }
}
