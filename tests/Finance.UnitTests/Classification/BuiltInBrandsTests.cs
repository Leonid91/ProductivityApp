using ProductivityApp.Finance.Classification;
using ProductivityApp.Finance.Model;

namespace ProductivityApp.Finance.UnitTests.Classification;

/// <summary>The brands the engine ships with.</summary>
public class BuiltInBrandsTests
{
    [Fact]
    public void All_EveryBrand_ReferencesAnExistingCategory()
    {
        var brands = BuiltInBrands.All;

        Assert.All(brands, b => Assert.True(Categories.Exists(b.CategoryId), b.Brand));
    }
}
