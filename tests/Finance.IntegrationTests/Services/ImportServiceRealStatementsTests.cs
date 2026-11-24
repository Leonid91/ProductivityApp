using ProductivityApp.Finance.IntegrationTests.Infrastructure;
using ProductivityApp.Finance.Model;
using ProductivityApp.Finance.Parsing;
using ProductivityApp.Finance.Testing;

namespace ProductivityApp.Finance.IntegrationTests.Services;

/// <summary>
/// Real BoursoBank and Fortuneo statements published on the internet (2022-2025; <see cref="RealStatements"/>), read
/// through the import of their bank, against what each one prints, written down from the recording itself, never from the
/// app (<see cref="RealStatementExpectations"/>). Each statement is read once; every test reads that reading.
/// </summary>
public sealed class ImportServiceRealStatementsTests(ReadOnce read) : IClassFixture<ReadOnce>
{
    /// <summary>A recording drawn back to a PDF and read as the person imports it: the bank of the statement chosen.</summary>
    private Task<StatementParse> ReadAsync(string name) => read.GetAsync(name, async store =>
    {
        var bank = store.AddBank(RealStatementExpectations.Of(name).Bank, BankingSystem.France, "EUR", "France");
        return (await store.ParseAsync(name + ".pdf", RealStatements.Pdf(name), bank)).Parse;
    });

    /// <summary>The operations read, in the order the expectations list them: by day, then amount.</summary>
    private static List<RealStatementExpectations.Operation> Ops(StatementParse p) => [.. p.Rows.Select(r => new RealStatementExpectations.Operation(r.Date, r.Amount)).OrderBy(o => o.Date).ThenBy(o => o.Amount)];

    public static TheoryData<string> Recordings => [.. RealStatements.Names];

    public static TheoryData<string> RecordingsWithBothBalances => [.. RealStatementExpectations.WithBothBalances];

    public static TheoryData<string> RecordingsPrintingTheirOpeningBalance => [.. RealStatementExpectations.WithOpening];

    public static TheoryData<string> RecordingsPrintingTheirClosingBalance => [.. RealStatementExpectations.WithClosing];

    [Theory]
    [MemberData(nameof(Recordings))]
    public async Task ParseAsync_RealStatement_ReadsEveryOperationPrinted(string name)
    {
        var parse = await ReadAsync(name);

        Assert.Equal(RealStatementExpectations.Of(name).Operations, Ops(parse));
    }

    [Theory]
    [MemberData(nameof(Recordings))]
    public async Task ParseAsync_RealStatement_IsInEurosOfTheKindOfItsAccount(string name)
    {
        var parse = await ReadAsync(name);

        Assert.Equal(("EUR", RealStatementExpectations.Of(name).Kind), (parse.Currency, parse.Kind));
    }

    [Theory]
    [MemberData(nameof(RecordingsPrintingTheirOpeningBalance))]
    public async Task ParseAsync_RealStatementPrintingItsOpeningBalance_ReadsIt(string name)
    {
        var parse = await ReadAsync(name);

        Assert.Equal(RealStatementExpectations.Of(name).Opening, parse.OpeningBalance);
    }

    [Theory]
    [MemberData(nameof(RecordingsPrintingTheirClosingBalance))]
    public async Task ParseAsync_RealStatementPrintingItsClosingBalance_ReadsIt(string name)
    {
        var parse = await ReadAsync(name);

        Assert.Equal(RealStatementExpectations.Of(name).Closing, parse.ClosingBalance);
    }

    /// <summary>Every page published: the operations add up from one balance to the other, to the cent.</summary>
    [Theory]
    [MemberData(nameof(RecordingsWithBothBalances))]
    public async Task ParseAsync_RealStatementWithEveryPagePublished_IsVerified(string name)
    {
        var parse = await ReadAsync(name);

        Assert.Equal(VerificationStatus.Verified, parse.Status);
    }

    [Fact]
    public async Task ParseAsync_FortuneoCardPaidInDollars_TakesTheAmountInEurosNotTheOnePaidNorTheRates()
    {
        var parse = await ReadAsync("fortuneo-2025-03");

        Assert.Contains(parse.Rows, r => r.Label.Contains("COMMON GRANVILLE", StringComparison.Ordinal) && r.Amount == -0.93m);
    }

    /// <summary>
    /// BoursoBank's export of October 2026 as an open-source budget app quotes it, checked by its author against a real
    /// export of 3 accounts (PrudhommeWTF/Foyer-App, pull request 289): one file for every account, the account's number
    /// on each line, amounts in the first "Solde" ("3 500,00", "-8,94"), the account's balance in the second ("983.2").
    /// </summary>
    [Fact]
    public async Task ParseAsync_BoursobankExportOfSeveralAccounts_ReadsEachAccountWithItsOwnOperations()
    {
        const string csv = "﻿\"Date Opération\";\"Date Valeur\";Libellé;\"Libellé Suggéré\";Catégorie;\"Catégorie Parente\";Solde;Commentaire;\"Numéro Compte\";\"Libellé Compte\";Solde;Pointage\n"
            + "2026-10-30;2026-10-30;\"CARTE 28/10/26 G20 CB*7836\";\"Supermarchés G20\";Alimentation;\"Vie quotidienne\";-8,94;;00040203046;BoursoBank;983.2;Non\n"
            + "2026-10-28;2026-10-28;\"VIR SALAIRE\";\"Vir Salaire\";\"Salaire fixe\";\"Revenus du travail\";\"3 500,00\";;00021425401;\"COMPTE COURANT M THOMAS\";77197.31;Oui\n"
            + "2026-10-29;2026-10-29;\"VIR CREATION COMPTE\";\"Vir Creation Compte\";\"Virements émis\";\"Virements émis\";\"-1 000,00\";;00040203046;BoursoBank;992.14;Non\n";
        using var store = new TestStore();
        var bourso = store.AddBank("BoursoBank", BankingSystem.France, "EUR", "France");

        var accounts = (await store.ParseAllAsync("export-operations-30-10-2026.csv", System.Text.Encoding.UTF8.GetBytes(csv), bourso)).Select(d => d.Parse).ToList();

        Assert.Equal(2, accounts.Count);
        Assert.All(accounts, a => Assert.Equal("EUR", a.Currency));
        var current = accounts.Single(a => a.Rows.Count == 2);
        Assert.Equal([new(new DateOnly(2026, 10, 29), -1000.00m), new(new DateOnly(2026, 10, 30), -8.94m)], Ops(current));
        Assert.Equal(983.20m, current.ClosingBalance);
        var other = accounts.Single(a => a.Rows.Count == 1);
        Assert.Equal([new RealStatementExpectations.Operation(new DateOnly(2026, 10, 28), 3500.00m)], Ops(other));
    }
}
