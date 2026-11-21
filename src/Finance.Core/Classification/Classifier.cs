using System.Text;
using ProductivityApp.Finance.Model;
using ProductivityApp.Finance.Text;

namespace ProductivityApp.Finance.Classification;

public sealed record Classification(string MerchantKey, string Brand, ValueSource BrandSource, string CategoryId, ValueSource CategorySource);

/// <summary>What kind of bank movement a label describes, independently of any shop.</summary>
public enum Movement { None, CardPayment, AccountTransfer, MoneyTransfer, BillPayment, Investment }

/// <summary>
/// Gives every operation a brand and a predefined category. Order of precedence: the person's own corrections,
/// the kind of bank movement (card payment, transfer, bill payment), the bank's own category, the built-in brand
/// dictionary, then wording such as "LOYER", "RETRAIT" or "PAYROLL".
/// Transfers, credit card payments and investments are kept apart from spending and income (they are shown in their
/// own section); <see cref="Services.TransferMatcher"/> then links the two halves found in two imported accounts.
/// </summary>
public sealed class Classifier
{
    private readonly Dictionary<string, BrandRule> _byKey;
    private readonly Dictionary<string, string> _brandCategory;
    private readonly List<(string Pattern, BrandEntry Entry)> _builtIn;
    private readonly BankingSystem _system;

    public Classifier(IEnumerable<BrandRule> learned, BankingSystem system = BankingSystem.NorthAmerica)
    {
        _system = system;
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

    /// <summary>What a wording says by itself, worked out once per wording (statements repeat the same shops).</summary>
    private sealed record LabelFacts(string Norm, string Key, string Padded, Movement Movement, BrandEntry? BuiltIn, string? KeywordOut, string? KeywordIn)
    {
        /// <summary>The keyword category for money going out (amount 0 or less) or coming in.</summary>
        public string? Keyword(decimal amount) => amount > 0 ? KeywordIn : KeywordOut;
        public string? Display { get; set; }
    }

    private readonly Dictionary<string, BrandRule?> _learned = [];

    private readonly Dictionary<(string, string), LabelFacts> _facts = [];

    private LabelFacts Facts(string rawLabel, string bankCategory)
    {
        if (_facts.TryGetValue((rawLabel, bankCategory), out var f)) return f;
        var norm = Normalize(rawLabel);
        var padded = " " + norm + " ";
        // The brand named first in the label wins ("STM-METRO BERRI" is the transit company, not the grocer);
        // at the same position the longer pattern wins ("UBER EATS" over "UBER").
        BrandEntry? builtIn = null;
        var bestAt = int.MaxValue;
        foreach (var (pattern, entry) in _builtIn)
        {
            var at = padded.IndexOf(pattern, StringComparison.Ordinal);
            if (at >= 0 && at < bestAt) { bestAt = at; builtIn = entry; }
        }
        f = new LabelFacts(norm, MerchantKey(norm), padded, MovementOf(padded, bankCategory, _system), builtIn, KeywordCategory(padded, 0), KeywordCategory(padded, 1));
        if (_facts.Count < 100_000) _facts[(rawLabel, bankCategory)] = f;
        return f;
    }

    public Classification Classify(string rawLabel, decimal amount, string? bankCategory = "")
    {
        bankCategory ??= "";
        var facts = Facts(rawLabel, bankCategory);
        var norm = facts.Norm;
        var key = facts.Key;
        var padded = facts.Padded;
        var movement = facts.Movement;
        var salaryWords = facts.Keyword(amount) == "salary";
        if (salaryWords && movement is Movement.MoneyTransfer) movement = Movement.None;
        // A transfer that says what it pays or returns is that: rent ("VIR SEPA LOYER ..."), a refund ("VIR INST RECU ... REMBOURSEMENT").
        if (movement is Movement.MoneyTransfer && facts.Keyword(amount) is "housing" or "refunds") movement = Movement.None;

        string? brand = null;
        var brandSource = ValueSource.Default;
        string? category = null;
        var categorySource = ValueSource.Default;

        var generic = IsGeneric(key, movement);
        var rule = generic ? _byKey.GetValueOrDefault(AmountKey(norm, amount)) : FindLearned(key);
        if (rule is not null)
        {
            brand = rule.Brand;
            brandSource = ValueSource.Learned;
            if (rule.CategoryId is not null && Categories.Exists(rule.CategoryId)) { category = rule.CategoryId; categorySource = ValueSource.Learned; }
        }

        var builtIn = movement == Movement.None ? facts.BuiltIn : null;

        brand ??= movement switch
        {
            // France has no credit cards: the one monthly debit of a deferred-debit card.
            Movement.CardPayment => _system == BankingSystem.France ? "Deferred debit card" : "Credit card payment",
            Movement.AccountTransfer => "Transfer between accounts",
            Movement.BillPayment => "Bill payment",
            Movement.MoneyTransfer when key is "" or "VIREMENT" or "VIR" or "TRANSFER" or "TRANSFERT" or "E TRANSFER" or "ETRANSFER" || key.Contains("EMAIL MONEY", StringComparison.Ordinal)
                => _system == BankingSystem.France ? "Bank transfer" : "Interac e-Transfer",
            _ => null,
        };
        if (brand is null && builtIn is not null) { brand = builtIn.Brand; brandSource = ValueSource.BuiltIn; }
        if (brand is null)
        {
            brand = facts.Display ??= DisplayName(rawLabel, key);
            if (brand.Length == 0) brand = "Unknown";
        }

        if (category is null && !generic && _brandCategory.TryGetValue(brand, out var learnedCat) && Categories.Exists(learnedCat))
        {
            category = learnedCat;
            categorySource = ValueSource.Learned;
        }

        if (category is null && movement != Movement.None)
        {
            var bankSays = BankCategories.Map(bankCategory, amount);
            category = movement switch
            {
                Movement.CardPayment => Categories.CardPayments,
                // "Paiement de facture" filed by the bank under investments is money sent to an investment account.
                Movement.BillPayment => bankSays == Categories.Investments ? Categories.Investments : amount < 0 ? Categories.Bills : "refunds",
                Movement.Investment => Categories.Investments,
                _ => Categories.Transfers,
            };
            categorySource = bankSays == category ? ValueSource.Bank : ValueSource.Keyword;
        }

        // A known subscription stays a subscription even when the bank files it under a broad heading
        // ("Electroniques et logiciels" for a monthly software plan).
        if (category is null && amount < 0 && builtIn is not null && builtIn.Brand == brand && builtIn.CategoryId == "subscriptions"
            && BankCategories.Map(bankCategory, amount) is null or "shopping" or "entertainment")
        { category = builtIn.CategoryId; categorySource = ValueSource.BuiltIn; }

        // The bank's own category, when its export has one, is more reliable than any guess from the label.
        if (category is null && BankCategories.Map(bankCategory, amount) is { } fromBank) { category = fromBank; categorySource = ValueSource.Bank; }

        var keyword = facts.Keyword(amount);
        // Bank movements have no shop behind them: give them a plain name rather than leftover label words.
        if (brandSource == ValueSource.Default && rule is null && builtIn is null && movement == Movement.None && keyword is "cash" or "fees")
            brand = keyword == "cash" ? "Cash withdrawal" : "Bank fees";
        if (brandSource == ValueSource.Default && rule is null && builtIn is null && movement == Movement.None && amount > 0
            && (padded.Contains(" INTERETS ", StringComparison.Ordinal) || padded.Contains(" INTERET ", StringComparison.Ordinal) || padded.Contains(" INTEREST ", StringComparison.Ordinal)))
            brand = "Interest";
        if (category is null && keyword is "cash" or "fees" or "salary") { category = keyword; categorySource = ValueSource.Keyword; }
        if (category is null && builtIn is not null && builtIn.Brand == brand) { category = builtIn.CategoryId; categorySource = ValueSource.BuiltIn; }
        if (category is null && keyword is not null) { category = keyword; categorySource = ValueSource.Keyword; }

        if (category is not null && categorySource is not (ValueSource.Learned or ValueSource.Bank) && amount > 0
            && Categories.Get(category).Kind == CategoryKind.Expense && category is not "fees")
        {
            // Money back from a shop is a refund, not negative spending.
            category = "refunds";
            categorySource = ValueSource.Keyword;
        }
        if (category is null) { category = Categories.DefaultFor(amount); categorySource = ValueSource.Default; }

        return new Classification(key, brand, brandSource, category, categorySource);
    }

    /// <summary>
    /// Key under which a correction is remembered. Generic bank wording ("Paiement de facture", "Transfert entre
    /// comptes") says nothing about who was paid, so it is remembered together with the exact amount: correcting the
    /// rent paid by bill payment does not turn every other bill payment into rent.
    /// </summary>
    public static string LearningKey(string rawLabel, decimal amount, string bankCategory = "", BankingSystem system = BankingSystem.NorthAmerica)
    {
        var norm = Normalize(rawLabel);
        var key = MerchantKey(norm);
        return IsGeneric(key, MovementOf(" " + norm + " ", bankCategory, system)) ? AmountKey(norm, amount) : key;
    }

    public static bool IsGenericLabel(string rawLabel, string bankCategory = "", BankingSystem system = BankingSystem.NorthAmerica)
    {
        var norm = Normalize(rawLabel);
        return IsGeneric(MerchantKey(norm), MovementOf(" " + norm + " ", bankCategory, system));
    }

    private static string AmountKey(string norm, decimal amount) => "#" + norm + "|" + AmountParser.ToStorage(amount);

    private static readonly HashSet<string> GenericKeys = ["DEPOT GUICHET", "DEPOT", "RETRAIT", "RETRAIT GUICHET", "DEPOSIT", "WITHDRAWAL", "MERCI", "INTERETS", "PAIEMENT INTERETS"];

    private static bool IsGeneric(string key, Movement movement) => key.Length == 0 || movement != Movement.None || GenericKeys.Contains(key);

    private static readonly string[] CardPaymentWords =
    [
        "PAIEMENT MASTERCARD", "PAIEMENT VISA", "PAIEMENT AMEX", "PAIEMENT RECU", "PAYMENT RECEIVED", "PAYMENT THANK YOU", "PAIEMENT MERCI",
        "CREDIT CARD PAYMENT", "PAIEMENT CARTE DE CREDIT", "PAIEMENT CARTE CREDIT", "MASTERCARD PAYMENT", "VISA PAYMENT", "AMEX PAYMENT",
        "CARD LOC PAYMENT", "CREDIT CARD LOC PAYMENT", "PAYMENT FROM CHEQUING", "CC PAYMENT",
    ];
    private static readonly string[] DeferredCardWords = ["FACTURE CARTE", "RELEVE CB", "RELEVE CARTE", "DEBIT DIFFERE", "CARTE DIFFERE", "PRLV CARTE"];
    private static readonly string[] AccountTransferWords =
    [
        "TRANSFERT ENTRE COMPTES", "VIREMENT ENTRE COMPTES", "TRANSFER BETWEEN ACCOUNTS", "ONLINE TRANSFER", "TRANSFERT EN LIGNE", "VIR INTERNE",
        "VIREMENT INTERNE", "VERSEMENT LIVRET", "VIR LIVRET", "VIREMENT LIVRET", "VIR COMPTE EPARGNE", "INTERNAL TRANSFER",
        // French savings books (Livret A, LDDS, LEP, Livret Jeune): a buffer of the person's own money, not an investment.
        "LIVRET A", "LIVRET JEUNE", "LDDS", "LEP",
        // Accounts holding several currencies and pockets of savings: money changed or put aside stays the person's.
        "EXCHANGED TO", "EXCHANGED FROM", "CURRENCY EXCHANGE", "TO POCKET", "FROM POCKET", "TO VAULT", "FROM VAULT",
    ];
    private static readonly string[] MoneyTransferWords =
    [
        "TOP UP", "TOPUP", "VIREMENT INTERAC", "INTERAC E TRANSFER", "E TRANSFER", "ETRANSFER", "VIREMENT", "VIR", "VIRT", "TRANSFER", "TRANSFERT", "TFR", "VIR SEPA", "VIR INST",
        "EMAIL MONEY TRF", "EMAIL MONEY TRANSFER", "E MAIL MONEY TRANSFER", "E MAIL MONEY TRF", "INTERAC E TRF", "SEND E TFR", "E TFR", "WIRE PAYMENT", "WIRE TRANSFER",
        "MONEY TRF", "TRF",
    ];

    /// <summary>A fee charged on a transfer is a fee, not the transfer: "Service charge MB-Email Money Trf".</summary>
    private static readonly string[] FeeWords = ["SERVICE CHARGE", "SERVICE CHARGES", "FEE", "FEES", "FRAIS", "COMMISSION"];
    private static readonly string[] BillPaymentWords = ["PAIEMENT DE FACTURE", "PAIEMENT FACTURE", "BILL PAYMENT", "PAIEMENT DE FACTURES"];
    private static readonly string[] InvestmentWordsNorthAmerica =
        ["COTISATION REER", "COTISATION CELI", "TFSA CONTRIBUTION", "RRSP CONTRIBUTION", "WEALTHSIMPLE", "QUESTRADE", "DISNAT", "COURTAGE",
         // A term investment at the bank (placement, GIC / CPG, term deposit) and brokers.
         "PLACEMENT", "PLACEMENTS", "CPG", "GIC", "DEPOT A TERME", "TERM DEPOSIT", "INTERACTIVE BRO", "INTERACTIVE BROKERS", "IBKR"];
    private static readonly string[] InvestmentWordsFrance = ["VERSEMENT PEA", "ASSURANCE VIE", "VERSEMENT PEL", "PLACEMENT", "PLACEMENTS"];

    public static Movement MovementOf(string padded, string bankCategory, BankingSystem system)
    {
        bool Has(string[] words) => words.Any(w => padded.Contains(" " + w + " ", StringComparison.Ordinal));
        var bank = TextNorm.Fold(bankCategory ?? "").Trim();
        // France has no credit cards: a card is a debit card, its purchases come out of the current account; only a
        // deferred-debit card is settled by one monthly debit of its statement.
        if (system == BankingSystem.France) { if (Has(DeferredCardWords)) return Movement.CardPayment; }
        else if (Has(CardPaymentWords) || bank is "PAIEMENT CARTE DE CREDIT" or "CREDIT CARD PAYMENT" or "CREDIT CARD PAYMENTS") return Movement.CardPayment;
        if (Has(FeeWords)) return Movement.None;
        if (Has(BillPaymentWords)) return Movement.BillPayment;
        if (Has(AccountTransferWords)) return Movement.AccountTransfer;
        if (Has(system == BankingSystem.France ? InvestmentWordsFrance : InvestmentWordsNorthAmerica)) return Movement.Investment;
        if (Has(MoneyTransferWords) || bank is "TRANSFERT" or "TRANSFERTS" or "TRANSFER" or "TRANSFERS" or "VIREMENTS") return Movement.MoneyTransfer;
        return Movement.None;
    }

    private BrandRule? FindLearned(string key)
    {
        if (key.Length == 0) return null;
        if (_learned.TryGetValue(key, out var known)) return known;
        return _learned[key] = FindLearnedOnce(key);
    }

    private BrandRule? FindLearnedOnce(string key)
    {
        if (_byKey.TryGetValue(key, out var exact)) return exact;
        // Same merchant, other branch or city: share the first two words (or one long distinctive word).
        var tokens = key.Split(' ');
        BrandRule? best = null;
        var bestLen = 0;
        foreach (var (k, rule) in _byKey)
        {
            if (k.StartsWith('#')) continue;
            var other = k.Split(' ');
            var common = 0;
            while (common < tokens.Length && common < other.Length && tokens[common] == other[common]) common++;
            var enough = common >= 2 || (common == 1 && tokens[0].Length >= 5 && (other.Length == 1 || tokens.Length == 1));
            if (enough && common > bestLen) { best = rule; bestLen = common; }
        }
        return best;
    }

    /// <summary>
    /// The brand shown for a merchant the dictionary does not know: the words of the merchant key, written as the
    /// bank wrote them when it used upper and lower case ("Le Petit Bistro Exemple"), title-cased when it wrote in capitals.
    /// </summary>
    public static string DisplayName(string rawLabel, string key)
    {
        var raw = TextNorm.CollapseSpaces(rawLabel);
        if (key.Length == 0)
        {
            // Only a shop's code ("CARTE 06/12/24 HETM049 4 CB*5940"): its letters, rather than the whole line.
            var code = Normalize(rawLabel).Split(' ')
                .Where(w => w.Any(char.IsDigit))
                .Select(w => new string([.. w.Where(char.IsLetter)]))
                .FirstOrDefault(w => w.Length >= 3 && !Noise.Contains(w) && !Cities.Contains(w));
            if (code is not null) return TextNorm.TitleCase(code);
            return raw.Any(char.IsLower) ? raw : TextNorm.TitleCase(raw);
        }
        if (!raw.Any(char.IsLower)) return TextNorm.TitleCase(key);
        var wanted = key.Split(' ').ToList();
        var picked = new List<string>();
        foreach (var word in raw.Split(' '))
        {
            if (picked.Count == wanted.Count) break;
            if (Normalize(word) == wanted[picked.Count]) picked.Add(word.Trim(',', '.', ';', ':', '*', '#'));
        }
        if (picked.Count != wanted.Count) return TextNorm.TitleCase(key);
        // A brand always starts with a capital, even when the bank wrote it in lower case.
        picked[0] = char.ToUpperInvariant(picked[0][0]) + picked[0][1..];
        return string.Join(' ', picked);
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
        "CHQ", "FRAIS", "OPERATION", "OP", "TRANSACTION", "TRX", "RECU", "EMIS", "PRESENTE", "CARD", "CREDIT", "DEBITS", "ACH", "PPD", "BY",
        "POINT", "SALE", "OPOS", "APOS", "FPOS", "MISC", "AUTHORIZED", "PREAUTHORIZED", "MEMO", "ONCD", "ONC",
        // French transfers: "VIR SEPA", "VIR INST RECU" (instant transfer received).
        "VIR", "INST",
        // French card refunds: "AVOIR 02/12/24 SHOP"; the reference of a transfer ("Réf : SCT...", BoursoBank's "Rèf").
        "AVOIR", "REF",
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
        ("salary", ["SALAIRE", "PAIE", "PAYROLL", "SALARY", "DEPOT DIRECT", "DIRECT DEP", "DIRECT DEPOSIT", "REMUNERATION", "DEPOT DE PAIE"], 1),
        ("cash", ["RETRAIT", "DAB", "GAB", "ATM", "ABM", "WITHDRAWAL", "GUICHET", "RETRAIT DAB", "CASH"], -1),
        ("otherincome", ["INTERET", "INTERETS", "INTEREST", "DIVIDENDE", "DIVIDEND", "ALLOCATION", "PENSION", "CASHBACK", "REMISE", "PRESTATION", "CAF", "GST", "TPS", "DEPOT", "DEPOSIT"], 1),
        ("refunds", ["REMBOURSEMENT", "REFUND", "RETOUR", "AVOIR", "RETURN"], 1),
        ("fees", ["FRAIS", "FEE", "FEES", "SERVICE CHARGE", "INTERET", "INTERETS", "INTEREST", "COMMISSION", "COTISATION", "COTIS", "AGIOS", "ANNUAL FEE", "PENALITE", "NSF"], -1),
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

    /// <summary>The keyword rules with each word between spaces, ready to look for in a padded wording.</summary>
    private static readonly (string Category, string[] Words, int Sign)[] PaddedKeywordRules =
        KeywordRules.Select(r => (r.Category, r.Words.Select(w => " " + w + " ").ToArray(), r.Sign)).ToArray();

    /// <summary>A payment by card at a shop ("CARTE 06/12/24 GRAND FRAIS", "CB LECLERC"): the bank's own fees are lines of their own.</summary>
    private static bool CardPurchase(string padded) =>
        padded.StartsWith(" CARTE ", StringComparison.Ordinal) || padded.StartsWith(" CB ", StringComparison.Ordinal)
        || padded.StartsWith(" PAIEMENT CB ", StringComparison.Ordinal) || padded.StartsWith(" PAIEMENT PAR CARTE ", StringComparison.Ordinal)
        || padded.StartsWith(" ACHAT CB ", StringComparison.Ordinal) || padded.StartsWith(" ACHAT CARTE ", StringComparison.Ordinal);

    private static string? KeywordCategory(string padded, decimal amount)
    {
        var card = CardPurchase(padded);
        foreach (var (category, words, sign) in PaddedKeywordRules)
        {
            if (sign > 0 && amount <= 0) continue;
            if (sign < 0 && amount > 0) continue;
            // A shop's name with a fee word in it ("GRAND FRAIS") is spending at that shop.
            if (card && category == "fees") continue;
            foreach (var w in words)
                if (padded.Contains(w, StringComparison.Ordinal)) return category;
        }
        return null;
    }
}
