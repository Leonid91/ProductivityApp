using ProductivityApp.Finance.Text;

namespace ProductivityApp.Finance.Classification;

/// <summary>
/// Category names banks put in their exports and statements (French and English, Banque Nationale wording included),
/// mapped to the app's predefined categories.
/// </summary>
public static class BankCategories
{
    private static readonly (string Category, int Sign, string[] Names)[] Table =
    [
        ("transfers", 0, ["PAIEMENT CARTE DE CREDIT", "CREDIT CARD PAYMENT", "TRANSFERT", "TRANSFERTS", "TRANSFER", "TRANSFERS", "VIREMENT ENTRE COMPTES", "VIREMENTS"]),
        ("investments", 0, ["INVESTISSEMENTS", "INVESTISSEMENT", "INVESTMENTS", "INVESTMENT", "PLACEMENTS", "PLACEMENT", "EPARGNE", "SAVINGS", "REER", "CELI", "RRSP", "TFSA"]),
        ("salary", 1, ["SALAIRE", "PAIE", "PAYROLL", "SALARY", "PAYCHECK"]),
        ("otherincome", 1, ["REVENUS", "REVENU", "INCOME", "REVENU D'INTERETS", "REVENU D INTERETS", "INTEREST INCOME", "INTERETS", "INTEREST", "DIVIDENDES", "DIVIDENDS", "REMBOURSEMENT D'IMPOT", "TAX REFUND"]),
        ("refunds", 1, ["REMBOURSEMENT", "REMBOURSEMENTS", "REFUND", "REFUNDS"]),
        ("groceries", -1, ["EPICERIE", "EPICERIES", "GROCERIES", "GROCERY", "ALIMENTATION", "SUPERMARCHE", "DEPANNEUR"]),
        ("restaurants", -1, ["RESTAURANTS", "RESTAURANT", "RESTAURATION RAPIDE", "FAST FOOD", "CAFES", "CAFE", "NOURRITURE ET BOISSON", "NOURRITURE ET BOISSONS",
            "FOOD & DRINK", "FOOD AND DRINK", "DINING", "BARS", "BARS ET RESTAURANTS"]),
        ("transport", -1, ["TRANSPORT EN COMMUN", "PUBLIC TRANSIT", "TRANSIT", "LOCATION D'AUTO ET TAXI", "LOCATION D AUTO ET TAXI", "TAXI", "TAXIS", "RIDESHARE",
            "TRANSPORT", "TRANSPORTS", "TRAIN"]),
        ("car", -1, ["ESSENCE", "CARBURANT", "GAS", "FUEL", "GAS & FUEL", "AUTOMOBILE", "AUTO", "STATIONNEMENT", "PARKING", "ENTRETIEN AUTO", "AUTO & TRANSPORT"]),
        ("health", -1, ["PHARMACIE", "SANTE", "HEALTH", "MEDICAL", "SOINS DE SANTE", "DENTISTE", "PHARMACY", "HEALTH & FITNESS", "SPORT ET CONDITIONNEMENT"]),
        ("personal", -1, ["SALON DE COIFFURE", "COIFFURE", "SOINS PERSONNELS", "PERSONAL CARE", "BEAUTE", "BEAUTY", "ESTHETIQUE"]),
        ("telecom", -1, ["INTERNET", "TELEPHONE", "CELLULAIRE", "TELECOMMUNICATIONS", "TELECOM", "MOBILE", "PHONE", "TELEVISION ET INTERNET"]),
        ("utilities", -1, ["SERVICES PUBLICS", "UTILITIES", "ELECTRICITE", "HYDRO", "CHAUFFAGE", "EAU"]),
        ("housing", -1, ["LOGEMENT", "LOYER", "HYPOTHEQUE", "HOUSING", "RENT", "MORTGAGE", "MAISON", "HOME"]),
        ("insurance", -1, ["ASSURANCE", "ASSURANCES", "INSURANCE"]),
        ("entertainment", -1, ["DIVERTISSEMENT", "DIVERTISSEMENTS", "ENTERTAINMENT", "LOISIRS", "CINEMA", "SPECTACLES", "SORTIES", "JEUX"]),
        ("subscriptions", -1, ["ABONNEMENTS", "ABONNEMENT", "SUBSCRIPTIONS", "SUBSCRIPTION"]),
        ("shopping", -1, ["MAGASINAGE", "SHOPPING", "VETEMENTS", "CLOTHING", "ELECTRONIQUES ET LOGICIELS", "ELECTRONICS & SOFTWARE", "ELECTRONIQUE",
            "ARTICLES MENAGERS", "QUINCAILLERIE", "MARCHANDISES", "MERCHANDISE", "ACHATS"]),
        ("travel", -1, ["VOYAGE", "VOYAGES", "TRAVEL", "HEBERGEMENT", "HOTEL", "HOTELS", "AVIATION", "COMPAGNIES AERIENNES", "AIRLINES"]),
        ("education", -1, ["EDUCATION", "ETUDES", "FRAIS DE SCOLARITE", "TUITION", "LIVRES"]),
        ("gifts", -1, ["DONS", "DONS ET CHARITE", "CHARITY", "GIFTS", "CADEAUX", "GIFTS & DONATIONS"]),
        ("fees", -1, ["FRAIS", "FRAIS BANCAIRES", "BANK FEES", "FEES", "FEES & CHARGES", "PROTECTION ANTIFRAUDE", "INTERETS PAYES", "INTEREST CHARGES", "SERVICE CHARGES"]),
        ("taxes", -1, ["IMPOTS", "IMPOT", "TAXES", "TAX"]),
        ("cash", -1, ["RETRAIT", "RETRAITS", "RETRAIT D'ARGENT", "ATM", "CASH", "ATM WITHDRAWAL", "ESPECES"]),
    ];

    private static readonly Dictionary<string, (string Category, int Sign)> ByName = Build();

    private static Dictionary<string, (string, int)> Build()
    {
        var d = new Dictionary<string, (string, int)>(StringComparer.Ordinal);
        foreach (var (category, sign, names) in Table)
            foreach (var n in names) d.TryAdd(TextNorm.Fold(n), (category, sign));
        return d;
    }

    /// <summary>The app category for a bank's category name, or null when unknown or contradicted by the sign.</summary>
    public static string? Map(string bankCategory, decimal amount)
    {
        if (string.IsNullOrWhiteSpace(bankCategory)) return null;
        var f = TextNorm.Fold(bankCategory).Trim();
        if (!ByName.TryGetValue(f, out var hit)) return null;
        if (hit.Sign > 0 && amount < 0) return null;
        if (hit.Sign < 0 && amount > 0) return hit.Category == "fees" ? null : "refunds";
        return hit.Category;
    }
}
