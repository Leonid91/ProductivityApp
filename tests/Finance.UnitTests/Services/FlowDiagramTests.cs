using ProductivityApp.Finance.Services;
using ProductivityApp.Finance.Testing;

namespace ProductivityApp.Finance.UnitTests.Services;

/// <summary>
/// The money-moved diagram stays readable whatever it shows (see <see cref="DiagramInspector"/> for the rules): the
/// layouts seen in real use, and 150 invented ones.
/// </summary>
public class FlowDiagramTests
{
    public static TheoryData<string> RealLayouts => [.. MovementLayouts.RealNames];

    public static TheoryData<int> InventedLayouts => [.. Enumerable.Range(20261110, 150)];

    [Theory]
    [MemberData(nameof(RealLayouts))]
    public void Build_LayoutSeenInRealUse_IsReadable(string layout)
    {
        var (movements, _) = MovementLayouts.Real(layout);

        var diagram = FlowDiagram.Build(movements);

        Assert.Empty(DiagramInspector.Problems(diagram));
    }

    [Theory]
    [MemberData(nameof(RealLayouts))]
    public void Build_LayoutSeenInRealUse_CrossesNoArrowsUnlessTheBanksForceIt(string layout)
    {
        var (movements, allowed) = MovementLayouts.Real(layout);

        var diagram = FlowDiagram.Build(movements);

        Assert.InRange(DiagramInspector.Crossings(diagram), 0, allowed);
    }

    [Theory]
    [MemberData(nameof(InventedLayouts))]
    public void Build_InventedLayout_IsReadable(int seed)
    {
        var movements = MovementLayouts.Invented(seed);

        var diagram = FlowDiagram.Build(movements);

        Assert.Empty(DiagramInspector.Problems(diagram));
    }

    [Fact]
    public void Build_SameMovementsAgain_ReturnsTheDiagramAlreadyMade()
    {
        var (movements, _) = MovementLayouts.Real("wise");
        var first = FlowDiagram.Build([.. movements]);

        var second = FlowDiagram.Build([.. movements]);

        Assert.Same(first, second);
    }

    [Fact]
    public void Fit_NameThatFits_ReturnsItWhole()
    {
        var result = FlowDiagram.Fit("Sample Debit", 210);

        Assert.Equal("Sample Debit", result);
    }

    [Fact]
    public void Fit_NameTooLongForItsBox_CutsItWithAnEllipsisToFit()
    {
        const double width = 210;

        var result = FlowDiagram.Fit("Fabrikam Card Services card •••• 1234", width);

        Assert.EndsWith("…", result);
        Assert.True(result.Length * 8 <= width - 16 + 8, result);
    }
}
