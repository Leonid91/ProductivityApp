using System.Text.Json;
using ProductivityApp.Finance.Model;

namespace ProductivityApp.Finance.Testing;

/// <summary>
/// What each real statement (<see cref="RealStatements"/>) prints, written down as data (Statements/Real/expected.json):
/// its bank, its kind of account, every operation (day and signed amount, debits negative) and the opening and closing
/// balances it prints (absent: the page holding it was not published). Read from the recordings themselves, never
/// through the app; on a statement printing both balances its operations add up from one to the other, to the cent.
/// </summary>
public static class RealStatementExpectations
{
    public sealed record Operation(DateOnly Date, decimal Amount);

    public sealed record Printed(string Bank, AccountKind Kind, List<Operation> Operations, decimal? Opening, decimal? Closing);

    private static readonly Lazy<Dictionary<string, Printed>> All = new(() =>
        JsonSerializer.Deserialize<Dictionary<string, Printed>>(typeof(RealStatementExpectations).Assembly.GetManifestResourceStream("Expected.real-statements.json")!,
            new JsonSerializerOptions { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } })!);

    /// <summary>What <paramref name="name"/> prints.</summary>
    public static Printed Of(string name) => All.Value[name];

    /// <summary>The recordings printing their opening balance.</summary>
    public static IEnumerable<string> WithOpening => All.Value.Where(x => x.Value.Opening.HasValue).Select(x => x.Key).Order(StringComparer.Ordinal);

    /// <summary>The recordings printing their closing balance.</summary>
    public static IEnumerable<string> WithClosing => All.Value.Where(x => x.Value.Closing.HasValue).Select(x => x.Key).Order(StringComparer.Ordinal);

    /// <summary>The recordings whose every page was published: both balances printed.</summary>
    public static IEnumerable<string> WithBothBalances => WithOpening.Intersect(WithClosing);
}
