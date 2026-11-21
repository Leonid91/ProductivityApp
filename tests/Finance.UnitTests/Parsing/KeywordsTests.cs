using ProductivityApp.Finance.Parsing;

namespace ProductivityApp.Finance.UnitTests.Parsing;

public class KeywordsTests
{
    [Theory]
    [InlineData("SOLDEAU : 29/11/2024", "BalanceAsOf")]
    [InlineData("ANCIEN SOLDECREDITEURAU 30 NOVEMBRE 2023", "Opening")]
    [InlineData("NOUVEAUSOLDE CREDITEUR AU 31 DECEMBRE 2023", "Closing")]
    [InlineData("NOUVEAU SOLDE EN EUR :", "Closing")]
    public void ClassifySummary_BalanceWordsSetWithoutSpaces_AreStillBalances(string folded, string kind) =>
        Assert.Equal(kind, Keywords.ClassifySummary(folded).ToString());

    [Fact]
    public void ClassifySummary_AnOperation_IsNoBalance() =>
        Assert.Equal("None", Keywords.ClassifySummary("CARTE 28/02 BOUCHERIE ALPES LA TOUR DU PIN").ToString());
}
