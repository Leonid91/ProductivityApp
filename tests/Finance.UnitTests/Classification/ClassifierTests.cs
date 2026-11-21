using ProductivityApp.Finance.Classification;
using ProductivityApp.Finance.Model;

namespace ProductivityApp.Finance.UnitTests.Classification;

public class ClassifierTests
{
    private static readonly Classifier NorthAmerica = new([], BankingSystem.NorthAmerica);
    private static readonly Classifier France = new([], BankingSystem.France);

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
    public void Classify_KnownMerchantLabel_ReturnsItsBrandAndCategory(string label, double amount, string brand, string category)
    {
        var result = NorthAmerica.Classify(label, (decimal)amount);

        Assert.Equal((brand, category), (result.Brand, result.CategoryId));
    }

    [Theory]
    [InlineData("VIR SEPA RECU SALAIRE EXEMPLE SAS", 1845.20, "salary")]
    [InlineData("PAYROLL DEPOSIT EXAMPLE CORP", 2140.55, "salary")]
    [InlineData("E-TRANSFER RECEIVED A SAMPLE", 120.00, "transfers")]
    [InlineData("PAYMENT - THANK YOU", 750.00, "cardpayments")]
    [InlineData("RETRAIT DAB", -40.00, "cash")]
    [InlineData("FRAIS MENSUELS DU COMPTE", -4.95, "fees")]
    [InlineData("LOYER OCTOBRE", -925.00, "housing")]
    [InlineData("RESTAURANT LE PETIT BISTRO", -48.00, "restaurants")]
    [InlineData("QUINCAILLERIE INCONNUE", -12.00, "other")]
    [InlineData("SOMETHING UNKNOWN", 16.00, "otherincome")]
    [InlineData("Withdrawal 1234567 MB-Email Money Trf", -88.32, "transfers")]
    [InlineData("Service charge MB-Email Money Trf", -1.00, "fees")]
    [InlineData("ABM withdrawal Sample Mall 24-7 Sampletown ON", -60.00, "cash")]
    [InlineData("Credit Card/LOC payment", -95.00, "cardpayments")]
    [InlineData("PAYMENT-THANK YOU SCOTIABANK TRANSIT", 57.96, "cardpayments")]
    [InlineData("ANNUAL FEE", -39.00, "fees")]
    [InlineData("PAIEMENT MASTERCARD", -120.00, "cardpayments")]
    public void Classify_UnknownMerchantWithTellingWording_ReturnsTheWordingsCategory(string label, double amount, string category)
    {
        var result = NorthAmerica.Classify(label, (decimal)amount);

        Assert.Equal(category, result.CategoryId);
    }

    [Theory]
    [InlineData("RETRAIT GUICHET 4412", -60.0, "Cash withdrawal")]
    [InlineData("FRAIS MENSUELS DU COMPTE", -4.95, "Bank fees")]
    [InlineData("Deposit 76543210 MB-Email Money Trf", 100.0, "Interac e-Transfer")]
    public void Classify_BankMovement_ReturnsAPlainBrandName(string label, double amount, string brand)
    {
        var result = NorthAmerica.Classify(label, (decimal)amount);

        Assert.Equal(brand, result.Brand);
    }

    [Theory]
    [InlineData("Commerce Inconnu", -12.0, "Epicerie", "groceries")]
    [InlineData("Paiement recu merci!", 120.0, "Paiement carte de credit", "cardpayments")]
    [InlineData("Paiement de facture", -500.0, "Investissements", "investments")]
    [InlineData("Paiement des interets", 3.17, "Revenu d'interets", "otherincome")]
    [InlineData("Loyer Exemple", -1500.0, "Hypotheque et loyer", "housing")]
    public void Classify_CategoryGivenByTheBank_ReturnsTheMatchingCategory(string label, double amount, string bankCategory, string category)
    {
        var result = NorthAmerica.Classify(label, (decimal)amount, bankCategory);

        Assert.Equal(category, result.CategoryId);
    }

    [Fact]
    public void Classify_CategoryGivenByTheBank_SaysTheBankAsItsSource()
    {
        var result = NorthAmerica.Classify("Commerce Inconnu", -12m, "Epicerie");

        Assert.Equal(ValueSource.Bank, result.CategorySource);
    }

    [Theory]
    [InlineData("ACHAT TIM HORTONS #2345 MONTREAL QC 12/10", "TIM HORTONS")]
    [InlineData("CB BOULANGERIE LA FOURNEE PARIS 11", "BOULANGERIE FOURNEE")]
    public void MerchantKey_LabelWithNumbersAndPlaces_KeepsOnlyTheMerchantsWords(string label, string key)
    {
        var result = Classifier.MerchantKey(Classifier.Normalize(label));

        Assert.Equal(key, result);
    }

    [Fact]
    public void Classify_PointOfSaleWordingOfTwoShops_KeepsTwoMerchants()
    {
        var furniture = NorthAmerica.Classify("Point of sale purchase Example Furniture Brampton ON", -50m);
        var garden = NorthAmerica.Classify("Point of sale purchase Example Garden Centre Brampton ON", -20m);

        Assert.NotEqual(furniture.MerchantKey, garden.MerchantKey);
        Assert.Equal("Example Furniture", furniture.Brand);
    }

    [Fact]
    public void Classify_LabelOfALearnedMerchantAtAnotherBranch_UsesTheLearnedBrandAndCategory()
    {
        var classifier = new Classifier([new BrandRule { MerchantKey = "BOULANGERIE FOURNEE", Brand = "La Fournée", CategoryId = "groceries", UpdatedAt = DateTime.UnixEpoch }]);

        var result = classifier.Classify("CB BOULANGERIE LA FOURNEE LYON 3", -6.40m);

        Assert.Equal(("La Fournée", "groceries", ValueSource.Learned), (result.Brand, result.CategoryId, result.CategorySource));
    }

    [Fact]
    public void Classify_BrandWithACategoryChosenForIt_UsesThatCategory()
    {
        var classifier = new Classifier([new BrandRule { MerchantKey = "", Brand = "Costco", CategoryId = "shopping", UpdatedAt = DateTime.UnixEpoch }]);

        var result = classifier.Classify("COSTCO WHOLESALE #503", -120m);

        Assert.Equal(("Costco", "shopping"), (result.Brand, result.CategoryId));
    }

    [Theory]
    [InlineData("CB CARREFOUR MARKET FACT 031025", -64.23, "groceries")]
    [InlineData("CB CARREFOUR MARKET", -31.20, "groceries")]
    [InlineData("PAIEMENT CB 0310 PARIS CARREFOUR CITY CARTE 0742XXXX5521", -23.40, "groceries")]
    [InlineData("RETRAIT DAB 0710 PARIS 11 CARTE 0742XXXX5521", -40.0, "cash")]
    [InlineData("F COTIS EUROCOMPTE DUO CONFORT", -11.50, "fees")]
    [InlineData("VIR SEPA LOYER SCI EXEMPLE", -720.0, "housing")]
    [InlineData("VERSEMENT ASSURANCE VIE", -100.0, "investments")]
    public void Classify_FrenchCardPaymentOrBankMovement_ReturnsItsCategory(string label, double amount, string category)
    {
        var result = France.Classify(label, (decimal)amount);

        Assert.Equal(category, result.CategoryId);
    }

    [Theory]
    [InlineData("VIR LIVRET A")]
    [InlineData("VIREMENT LDDS")]
    [InlineData("VERSEMENT LEP")]
    public void Classify_FrenchSavingsBookMovement_IsATransferBetweenOwnAccounts(string label)
    {
        var result = France.Classify(label, -200m);

        Assert.Equal(Categories.Transfers, result.CategoryId);
    }

    [Theory]
    [InlineData("PAIEMENT VISA CARREFOUR", -20.0)]
    [InlineData("CB CARREFOUR MARKET FACT 031025", -64.23)]
    public void Classify_FrenchCardPurchase_IsNeverACardStatementPayment(string label, double amount)
    {
        var result = France.Classify(label, (decimal)amount);

        Assert.NotEqual(Categories.CardPayments, result.CategoryId);
    }

    [Theory]
    [InlineData("FACTURE CARTE DU 30/09/25", -412.50)]
    [InlineData("PRLV FACTURE CARTE 0925", -642.10)]
    public void Classify_FrenchMonthlyDebitOfADeferredDebitCard_IsACardPayment(string label, double amount)
    {
        var result = France.Classify(label, (decimal)amount);

        Assert.Equal(Categories.CardPayments, result.CategoryId);
    }

    [Fact]
    public void Classify_FrenchDeferredDebitCardPayment_IsNamedDeferredDebitCard()
    {
        var result = France.Classify("FACTURE CARTE DU 30/09/25", -412.50m);

        Assert.Equal("Deferred debit card", result.Brand);
    }

    [Theory]
    [InlineData(BankingSystem.France, "VIR SEPA", -50.0, "Bank transfer")]
    [InlineData(BankingSystem.NorthAmerica, "PAIEMENT VISA MERCI", -300.0, "Credit card payment")]
    public void Classify_PlainTransferOrCardPayment_IsNamedAfterTheBankingSystem(BankingSystem system, string label, double amount, string brand)
    {
        var classifier = new Classifier([], system);

        var result = classifier.Classify(label, (decimal)amount);

        Assert.Equal(brand, result.Brand);
    }

    [Fact]
    public void Classify_CardPaymentAtAShopWithAFeeWordInItsName_IsSpendingAtThatShopNotBankFees()
    {
        var result = France.Classify("CARTE 06/12/24 GRAND FRAIS CB*5940", -1.20m);

        Assert.NotEqual("fees", result.CategoryId);
        Assert.NotEqual("Bank fees", result.Brand);
    }

    [Theory]
    [InlineData("CARTE 06/12/24 HETM049 4 CB*5940", -226.19)]
    [InlineData("AVOIR 02/12/24 HETM016 4 CB*5940", 62.00)]
    public void Classify_ShopNamedOnlyByItsCode_IsNamedByTheCodesLetters(string label, double amount)
    {
        var result = France.Classify(label, (decimal)amount);

        Assert.Equal("Hetm", result.Brand);
    }
}
