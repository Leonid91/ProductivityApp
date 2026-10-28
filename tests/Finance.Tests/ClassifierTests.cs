using ProductivityApp.Finance.Classification;
using ProductivityApp.Finance.Model;

namespace ProductivityApp.Finance.Tests;

public class ClassifierTests
{
    private static readonly Classifier Plain = new([]);

    [Theory]
    [InlineData("TIM HORTONS #2345 MONTREAL QC", -3.25, "Tim Hortons", "restaurants")]
    [InlineData("IGA EXTRA 08123 LAVAL QC", -54.10, "IGA", "groceries")]
    [InlineData("STM-METRO BERRI-UQAM", -3.75, "STM", "transport")]
    [InlineData("UBER *EATS PENDING", -24.00, "Uber Eats", "restaurants")]
    [InlineData("UBER *TRIP HELP.UBER.COM", -14.00, "Uber", "transport")]
    [InlineData("AMZN MKTP CA*2K3J49QW1", -42.00, "Amazon", "shopping")]
    [InlineData("AMZN MKTP CA REFUND", 23.99, "Amazon", "refunds")]
    [InlineData("CB CARREFOUR MARKET 12/10", -31.20, "Carrefour", "groceries")]
    [InlineData("PRLV SEPA EDF", -61.33, "EDF", "utilities")]
    [InlineData("NETFLIX.COM 866-579-7172", -18.99, "Netflix", "subscriptions")]
    [InlineData("HYDRO-QUEBEC", -80.00, "Hydro-Québec", "utilities")]
    public void Known_brands(string label, double amount, string brand, string category)
    {
        var c = Plain.Classify(label, (decimal)amount);
        Assert.Equal(brand, c.Brand);
        Assert.Equal(category, c.CategoryId);
    }

    [Theory]
    [InlineData("VIR SEPA RECU SALAIRE EXEMPLE SAS", 1845.20, "salary")]
    [InlineData("PAYROLL DEPOSIT EXAMPLE CORP", 2140.55, "salary")]
    [InlineData("E-TRANSFER RECEIVED A SAMPLE", 120.00, "transfers")]
    [InlineData("PAYMENT - THANK YOU", 750.00, "transfers")]
    [InlineData("RETRAIT DAB", -40.00, "cash")]
    [InlineData("FRAIS MENSUELS DU COMPTE", -4.95, "fees")]
    [InlineData("LOYER OCTOBRE", -925.00, "housing")]
    [InlineData("RESTAURANT LE PETIT BISTRO", -48.00, "restaurants")]
    [InlineData("QUINCAILLERIE INCONNUE", -12.00, "other")]
    [InlineData("SOMETHING UNKNOWN", 15.00, "otherincome")]
    public void Wording_gives_the_category(string label, double amount, string category) =>
        Assert.Equal(category, Plain.Classify(label, (decimal)amount).CategoryId);

    [Fact]
    public void Bank_movements_get_plain_names()
    {
        Assert.Equal("Cash withdrawal", Plain.Classify("RETRAIT GUICHET 4412", -60m).Brand);
        Assert.Equal("Bank fees", Plain.Classify("FRAIS MENSUELS DU COMPTE", -4.95m).Brand);
    }

    [Fact]
    public void Merchant_key_drops_numbers_and_places()
    {
        Assert.Equal("TIM HORTONS", Classifier.MerchantKey(Classifier.Normalize("ACHAT TIM HORTONS #2345 MONTREAL QC 12/10")));
        Assert.Equal("BOULANGERIE FOURNEE", Classifier.MerchantKey(Classifier.Normalize("CB BOULANGERIE LA FOURNEE PARIS 11")));
    }

    [Fact]
    public void Corrections_win_and_follow_other_branches()
    {
        var rules = new List<BrandRule>
        {
            new() { MerchantKey = "BOULANGERIE FOURNEE", Brand = "La Fournée", CategoryId = "groceries", UpdatedAt = DateTime.UtcNow },
            new() { MerchantKey = "", Brand = "Costco", CategoryId = "shopping", UpdatedAt = DateTime.UtcNow },
        };
        var c = new Classifier(rules);
        var other = c.Classify("CB BOULANGERIE LA FOURNEE LYON 3", -6.40m);
        Assert.Equal("La Fournée", other.Brand);
        Assert.Equal("groceries", other.CategoryId);
        Assert.Equal(ValueSource.Learned, other.CategorySource);
        var costco = c.Classify("COSTCO WHOLESALE #503", -120m);
        Assert.Equal("Costco", costco.Brand);
        Assert.Equal("shopping", costco.CategoryId);
    }

    [Fact]
    public void Every_built_in_brand_has_a_known_category() =>
        Assert.All(BuiltInBrands.All, b => Assert.True(Categories.Exists(b.CategoryId), b.Brand));
}
