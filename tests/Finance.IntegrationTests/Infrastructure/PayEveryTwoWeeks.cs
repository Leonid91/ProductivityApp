using System.Text;

namespace ProductivityApp.Finance.IntegrationTests.Infrastructure;

/// <summary>
/// Five months of a chequing account (invented): a direct deposit from an employer every two weeks (11 pays), expenses
/// the employer paid back once, a monthly child benefit and interest, and groceries.
/// </summary>
public static class PayEveryTwoWeeks
{
    public const string Employer = "DEPOT DIRECT EXEMPLE INC";
    public const string Benefit = "DEPOT DIRECT ALLOCATION CANADIENNE ENFANTS";
    public const string Interest = "INTERETS CREDITES";
    public const int Pays = 11;

    public static readonly byte[] Csv = Encoding.UTF8.GetBytes("""
        Date,Description,Amount
        2026-01-03,EPICERIE EXEMPLE,-84.10
        2026-01-09,DEPOT DIRECT EXEMPLE INC,2140.55
        2026-01-20,DEPOT DIRECT ALLOCATION CANADIENNE ENFANTS,620.00
        2026-01-23,DEPOT DIRECT EXEMPLE INC,2142.55
        2026-01-28,INTERETS CREDITES,1.25
        2026-02-03,EPICERIE EXEMPLE,-84.10
        2026-02-06,DEPOT DIRECT EXEMPLE INC,2140.55
        2026-02-11,DEPOT DIRECT EXEMPLE INC,385.20
        2026-02-20,DEPOT DIRECT EXEMPLE INC,2142.55
        2026-02-20,DEPOT DIRECT ALLOCATION CANADIENNE ENFANTS,620.00
        2026-02-28,INTERETS CREDITES,1.25
        2026-03-03,EPICERIE EXEMPLE,-84.10
        2026-03-06,DEPOT DIRECT EXEMPLE INC,2140.55
        2026-03-20,DEPOT DIRECT EXEMPLE INC,2142.55
        2026-03-20,DEPOT DIRECT ALLOCATION CANADIENNE ENFANTS,620.00
        2026-03-28,INTERETS CREDITES,1.25
        2026-04-03,EPICERIE EXEMPLE,-84.10
        2026-04-03,DEPOT DIRECT EXEMPLE INC,2141.55
        2026-04-17,DEPOT DIRECT EXEMPLE INC,2141.55
        2026-04-20,DEPOT DIRECT ALLOCATION CANADIENNE ENFANTS,620.00
        2026-04-28,INTERETS CREDITES,1.25
        2026-05-01,DEPOT DIRECT EXEMPLE INC,2141.55
        2026-05-03,EPICERIE EXEMPLE,-84.10
        2026-05-15,DEPOT DIRECT EXEMPLE INC,2140.55
        2026-05-20,DEPOT DIRECT ALLOCATION CANADIENNE ENFANTS,620.00
        2026-05-28,INTERETS CREDITES,1.25
        2026-05-29,DEPOT DIRECT EXEMPLE INC,2142.55

        """);

    /// <summary>The pay of June 12: two weeks after the last pay of <see cref="Csv"/>.</summary>
    public static readonly byte[] June = Encoding.UTF8.GetBytes("Date,Description,Amount\n2026-06-12,DEPOT DIRECT EXEMPLE INC,2140.55\n");
}
