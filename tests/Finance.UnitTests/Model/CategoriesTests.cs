using ProductivityApp.Finance.Model;

namespace ProductivityApp.Finance.UnitTests.Model;

/// <summary>The categories the engine ships with.</summary>
public class CategoriesTests
{
    [Fact]
    public void Get_CardPayments_IsNamedCardStatementPayments()
    {
        var category = Categories.Get(Categories.CardPayments);

        Assert.Equal("Card statement payments", category.Name);
    }
}
