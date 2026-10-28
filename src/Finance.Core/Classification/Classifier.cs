using System.Text;
using ProductivityApp.Finance.Model;
using ProductivityApp.Finance.Text;

namespace ProductivityApp.Finance.Classification;

public sealed record Classification(string MerchantKey, string Brand, ValueSource BrandSource, string CategoryId, ValueSource CategorySource);

/// <summary>
/// Gives every operation a brand and a predefined category. Order of precedence: the user's own corrections
/// (learned dictionary), the built-in brand dictionary, then wording such as "LOYER", "RETRAIT" or "PAYROLL".
/// </summary>
public sealed class Classifier
{
    private readonly Dictionary<string, BrandRule> _byKey;
    private readonly Dictionary<string, string> _brandCategory;
    private readonly List<(string Pattern, BrandEntry Entry)> _builtIn;

    public Classifier(IEnumerable<BrandRule> learned)
    {
        var rules = learned.ToList();
        _byKey = rules.Where(r => r.MerchantKey.Length > 0).GroupBy(r => r.MerchantKey).ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.UpdatedAt).First());
        _brandCategory = rules.Where(r => r.MerchantKey.Length == 0 && r.CategoryId is not null)
            .GroupBy(r => r.Brand, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.UpdatedAt).First().CategoryId!, StringComparer.OrdinalIgnoreCase);
        _builtIn = BuiltInBrands.All
            .SelectMany(e => e.Patterns.Select(p => (Pattern: " " + Normalize(p) + " ", Entry: e)))
            .OrderByDescending(x => x.Pattern.Length)
            .ToList();
    }

    private readonly BankingSystem _system;

    public Classifier(IEnumerable<BrandRule> learned, BankingSystem system) : this(learned) => _system = system;

    public Classification Classify(string rawLabel, decimal amount, string bankCategory = "")
    {
        var norm = Normalize(rawLabel);
        var key = MerchantKey(norm);
        var padded = " " + norm + " ";

        string? brand = null;
        var brandSource = ValueSource.Default;
        string? category = null;
        var categorySource = ValueSource.Default;

        var rule = FindLearned(key);
        if (rule is not null)
        {
            brand = rule.Brand;
            brandSource = ValueSource.Learned;
            if (rule.CategoryId is not null && Categories.Exists(rule.CategoryId)) { category = rule.CategoryId; categorySource = ValueSource.Learned; }
        }

        // The brand named first in the label wins ("STM-METRO BERRI" is the transit company, not the grocer);
        // at the same position the longer pattern wins ("UBER EATS" over "UBER").
        BrandEntry? builtIn = null;
        var bestAt = int.MaxValue;
        foreach (var (pattern, entry) in _builtIn)
        {
            var at = padded.IndexOf(pattern, StringComparison.Ordinal);
            if (at >= 0 && at < bestAt) { bestAt = at; builtIn = entry; }
        }
        if (brand is null && builtIn is not null) { brand = builtIn.Brand; brandSource = ValueSource.BuiltIn; }
        if (brand is null)
        {
            brand = key.Length > 0 ? TextNorm.TitleCase(key) : TextNorm.TitleCase(TextNorm.CollapseSpaces(rawLabel));
            if (brand.Length == 0) brand = "Unknown";
        }

        if (category is null && _brandCategory.TryGetValue(brand, out var learnedCat) && Categories.Exists(learnedCat))
        {
            category = learnedCat;
            categorySource = ValueSource.Learned;
        }

        // The bank's own category, when its export has one, is more reliable than any guess from the label.
        if (category is null && BankCategories.Map(bankCategory, amount) is { } fromBank) { category = fromBank; categorySource = ValueSource.Bank; }

        var keyword = KeywordCategory(padded, amount, _system);
        // Bank movements have no shop behind them: give them a plain name rather than leftover label words.
        if (brandSource == ValueSource.Default && rule is null && builtIn is null && keyword is "cash" or "fees")
            brand = keyword == "cash" ? "Cash withdrawal" : "Bank fees";
        // Movement wording (transfers, cash, fees) is more telling than the brand: "VIREMENT ... COSTCO" is a transfer.
        if (category is null && keyword is "transfers" or "cash" or "fees" or "salary") { category = keyword; categorySource = ValueSource.Keyword; }
        if (category is null && builtIn is not null && builtIn.Brand == brand) { category = builtIn.CategoryId; categorySource = ValueSource.BuiltIn; }
        if (category is null && keyword is not null) { category = keyword; categorySource = ValueSource.Keyword; }

        if (category is not null && categorySource is not (ValueSource.Learned or ValueSource.Bank) && amount > 0 && Categories.Get(category).Kind == CategoryKind.Expense && category != "fees")
        {
            // Money back from a shop is a refund, not negative spending.
            category = "refunds";
            categorySource = ValueSource.Keyword;
        }
        if (category is null) { category = Categories.DefaultFor(amount); categorySource = ValueSource.Default; }

        return new Classification(key, brand, brandSource, category, categorySource);
    }

    private BrandRule? FindLearned(string key)
    {
        if (key.Length == 0) return null;
        if (_byKey.TryGetValue(key, out var exact)) return exact;
        // Same merchant, other branch or city: share the first two words (or one long distinctive word).
        var tokens = key.Split(' ');
        BrandRule? best = null;
        var bestLen = 0;
        foreach (var (k, rule) in _byKey)
        {
            var other = k.Split(' ');
            var common = 0;
            while (common < tokens.Length && common < other.Length && tokens[common] == other[common]) common++;
            var enough = common >= 2 || (common == 1 && tokens[0].Length >= 5 && (other.Length == 1 || tokens.Length == 1));
            if (enough && common > bestLen) { best = rule; bestLen = common; }
        }
        return best;
    }

    /// <summary>Folded, with punctuation turned into spaces (keeping &amp; and +).</summary>
    public static string Normalize(string label)
    {
        var f = TextNorm.Fold(label);
        var sb = new StringBuilder(f.Length);
        foreach (var ch in f) sb.Append(char.IsLetterOrDigit(ch) || ch is '&' or '+' or '\'' ? ch : ' ');
        return TextNorm.CollapseSpaces(sb.ToString().Replace("'", ""));
    }

    private static readonly HashSet<string> Noise =
    [
        "ACHAT", "ACHATS", "CB", "CARTE", "PAIEMENT", "PMT", "POS", "PDV", "TPV", "PURCHASE", "INTERAC", "DEBIT", "VISA", "MC",
        "MASTERCARD", "PREAUTH", "PRE", "AUTH", "AUTORISATION", "PRELEVEMENT", "PRLV", "SEPA", "FACTURE", "FACT", "RETAIL", "CONTACTLESS",
        "SANS", "CONTACT", "APPLE", "PAY", "GOOGLE", "WWW", "COM", "INC", "LTD", "LTEE", "CORP", "SAS", "SARL", "SA", "CA", "CAN", "CANADA",
        "QC", "ON", "BC", "AB", "MB", "SK", "NS", "NB", "NL", "PE", "QUE", "ONT", "FR", "FRA", "FRANCE", "USA", "US", "PAYPAL", "SQ", "TST",
        "DE", "DU", "DES", "LE", "LA", "LES", "THE", "OF", "AND", "ET", "A", "AU", "AUX", "EN", "PAR", "CHEZ", "MAG", "MAGASIN", "STORE",
        "CHQ", "FRAIS", "OPERATION", "OP", "TRANSACTION", "TRX", "RECU", "EMIS", "PRESENTE", "CARD", "CREDIT", "DEBITS", "ACH", "PPD",
    ];

    private static readonly HashSet<string> Cities =
    [
        "MONTREAL", "MTL", "LAVAL", "LONGUEUIL", "QUEBEC", "TORONTO", "OTTAWA", "GATINEAU", "SHERBROOKE", "BROSSARD", "TERREBONNE",
        "VANCOUVER", "CALGARY", "EDMONTON", "WINNIPEG", "HALIFAX", "MISSISSAUGA", "BRAMPTON", "HAMILTON", "LEVIS", "TROIS", "RIVIERES",
        "SAGUENAY", "VERDUN", "BOUCHERVILLE", "BLAINVILLE", "BOISBRIAND", "REPENTIGNY", "MIRABEL", "GRANBY", "DRUMMONDVILLE", "MASCOUCHE",
        "PARIS", "LYON", "MARSEILLE", "TOULOUSE", "NICE", "NANTES", "BORDEAUX", "LILLE", "RENNES", "STRASBOURG", "MONTPELLIER", "GRENOBLE",
        "NEW", "YORK", "BOSTON", "CHICAGO", "SEATTLE", "ONLINE", "INTERNET",
    ];

    /// <summary>Stable merchant identity: the first meaningful words, without card numbers, store numbers, dates or places.</summary>
    public static string MerchantKey(string normalized)
    {
        var words = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => !w.Any(char.IsDigit) && !Noise.Contains(w) && !Cities.Contains(w) && (w.Length > 1 || w is "&" or "+"))
            .Take(3)
            .ToList();
        return string.Join(' ', words);
    }

    private static readonly (string Category, string[] Words, int Sign)[] KeywordRules =
    [
        ("salary", ["SALAIRE", "PAIE", "PAYROLL", "SALARY", "DEPOT DIRECT", "DIRECT DEP", "DIRECT DEPOSIT", "REMUNERATION"], 1),
        ("transfers", ["VIREMENT", "VIR", "VIRT", "TRANSFER", "TRANSFERT", "TFR", "E TRANSFER", "ETRANSFER", "VIREMENT INTERAC", "PAIEMENT CARTE DE CREDIT",
            "PAYMENT THANK YOU", "PAIEMENT MERCI", "CREDIT CARD PAYMENT", "PAIEMENT VISA", "PAIEMENT MASTERCARD", "PAIEMENT CARTE CREDIT", "EPARGNE", "SAVINGS"], 0),
        ("cash", ["RETRAIT", "DAB", "GAB", "ATM", "WITHDRAWAL", "GUICHET", "RETRAIT DAB", "CASH"], -1),
        ("otherincome", ["INTERET", "INTERETS", "INTEREST", "DIVIDENDE", "DIVIDEND", "ALLOCATION", "PENSION", "CASHBACK", "REMISE", "PRESTATION", "CAF", "GST", "TPS", "DEPOT", "DEPOSIT"], 1),
        ("refunds", ["REMBOURSEMENT", "REFUND", "RETOUR", "AVOIR", "RETURN"], 1),
        ("fees", ["FRAIS", "FEE", "FEES", "SERVICE CHARGE", "INTERET", "INTERETS", "INTEREST", "COMMISSION", "COTISATION", "AGIOS", "ANNUAL FEE", "PENALITE", "NSF"], -1),
        ("housing", ["LOYER", "RENT", "HYPOTHEQUE", "MORTGAGE", "CONDO", "SYNDIC", "COPROPRIETE"], 0),
        ("insurance", ["ASSURANCE", "ASSURANCES", "INSURANCE", "MUTUELLE"], 0),
        ("utilities", ["ELECTRICITE", "GAZ NATUREL", "EAU", "WATER", "HYDRO", "ENERGIE", "ENERGY"], 0),
        ("telecom", ["INTERNET", "MOBILE", "TELEPHONE", "CELLULAIRE", "WIRELESS", "TELECOM"], 0),
        ("restaurants", ["RESTAURANT", "RESTO", "CAFE", "BISTRO", "BRASSERIE", "PIZZA", "PIZZERIA", "SUSHI", "BURGER", "BAR", "PUB", "TAVERNE",
            "BOULANGERIE", "PATISSERIE", "BAKERY", "DINER", "GRILL", "TRAITEUR", "SNACK", "KEBAB", "POKE", "RAMEN", "PHO", "TACO", "TACOS", "CREMERIE", "GELATO", "COFFEE", "BAGEL"], -1),
        ("groceries", ["EPICERIE", "MARCHE", "SUPERMARCHE", "GROCERY", "GROCERIES", "MARKET", "FRUITERIE", "BOUCHERIE", "FROMAGERIE", "POISSONNERIE", "ALIMENTATION", "DEPANNEUR"], -1),
        ("health", ["PHARMA", "PHARMACIE", "PHARMACY", "CLINIQUE", "CLINIC", "DENTISTE", "DENTAL", "DENTAIRE", "MEDICAL", "OPTOMETRISTE", "PHYSIO", "HOPITAL", "HOSPITAL", "GYM", "FITNESS", "YOGA"], -1),
        ("transport", ["TAXI", "BUS", "TRAIN", "TRANSIT", "PEAGE", "TOLL", "TRANSPORT"], -1),
        ("car", ["ESSENCE", "GAS", "FUEL", "CARBURANT", "STATION SERVICE", "GARAGE", "PNEU", "PNEUS", "CARWASH", "LAVE AUTO", "MECANIQUE"], -1),
        ("travel", ["HOTEL", "MOTEL", "AUBERGE", "AIRLINE", "AIRLINES", "VOYAGE", "VOYAGES", "TRAVEL", "AEROPORT", "AIRPORT"], -1),
        ("entertainment", ["CINEMA", "THEATRE", "CONCERT", "MUSEE", "MUSEUM", "SPECTACLE", "BILLET", "BILLETS", "TICKET", "TICKETS", "BOWLING", "JEUX", "GAMES"], -1),
        ("personal", ["COIFFURE", "SALON", "ESTHETIQUE", "SPA", "BARBIER", "BARBER", "ONGLES", "NAIL", "NAILS"], -1),
        ("education", ["ECOLE", "SCHOOL", "UNIVERSITE", "UNIVERSITY", "COURS", "TUITION", "FORMATION", "SCOLARITE"], -1),
        ("gifts", ["DON", "DONATION", "FONDATION", "FOUNDATION", "CHARITY", "CADEAU", "GIFT"], -1),
        ("taxes", ["IMPOT", "IMPOTS", "TAX", "TAXES", "TAXE"], -1),
        ("subscriptions", ["ABONNEMENT", "SUBSCRIPTION", "MEMBERSHIP"], -1),
    ];

    /// <summary>Wording of movements between one's own accounts, which depends on how the banking system works.</summary>
    private static readonly (string Category, string[] Words, int Sign)[] NorthAmericaRules =
    [
        ("transfers", ["PAIEMENT MASTERCARD", "PAIEMENT VISA", "PAIEMENT AMEX", "PAIEMENT RECU", "PAIEMENT RECU MERCI", "PAYMENT RECEIVED",
            "TRANSFERT ENTRE COMPTES", "VIREMENT ENTRE COMPTES", "TRANSFER BETWEEN ACCOUNTS", "ONLINE TRANSFER", "TRANSFERT EN LIGNE",
            "PAYMENT THANK YOU", "PAIEMENT MERCI", "CREDIT CARD PAYMENT", "PAIEMENT CARTE DE CREDIT", "MASTERCARD PAYMENT", "VISA PAYMENT"], 0),
        ("investments", ["COTISATION REER", "COTISATION CELI", "TFSA CONTRIBUTION", "RRSP CONTRIBUTION", "PLACEMENT", "WEALTHSIMPLE", "QUESTRADE",
            "DISNAT", "COURTAGE", "INVESTISSEMENT"], -1),
    ];

    private static readonly (string Category, string[] Words, int Sign)[] FranceRules =
    [
        ("transfers", ["FACTURE CARTE", "RELEVE CB", "RELEVE CARTE", "DEBIT DIFFERE", "CARTE DIFFERE", "PRLV CARTE", "VIR INTERNE", "VIREMENT INTERNE",
            "VERSEMENT LIVRET", "VIR LIVRET", "VIREMENT LIVRET", "VIR COMPTE EPARGNE", "VIR PERMANENT"], 0),
        ("investments", ["LIVRET A", "LDDS", "PEL", "PEA", "ASSURANCE VIE", "VERSEMENT PEA"], -1),
    ];

    private static string? KeywordCategory(string padded, decimal amount, BankingSystem system)
    {
        foreach (var (category, words, sign) in (system == BankingSystem.France ? FranceRules : NorthAmericaRules).Concat(KeywordRules))
        {
            if (sign > 0 && amount <= 0) continue;
            if (sign < 0 && amount > 0) continue;
            if (words.Any(w => padded.Contains(" " + w + " ", StringComparison.Ordinal))) return category;
        }
        return null;
    }
}
