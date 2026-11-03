namespace ProductivityApp.Finance.Model;

public enum CategoryKind { Expense, Income, Transfer }

/// <summary>
/// A category. Icon is a Material icon name used by the interface. Parent is the category it is nested in (null at the
/// top); Color is "#rrggbb" or null.
/// Built-in categories have fixed ids the engine relies on; the ones the person makes have ids "u1", "u2"...
/// </summary>
public sealed record Category(string Id, string Name, CategoryKind Kind, string Icon, string? Parent = null, string? Color = null, bool BuiltIn = true);

public static class Categories
{
    public const string OtherExpense = "other";
    public const string OtherIncome = "otherincome";
    /// <summary>Money moved between the person's own accounts, or sent and received by transfer.</summary>
    public const string Transfers = "transfers";
    /// <summary>A credit card being paid, seen from the card and from the account paying it.</summary>
    public const string CardPayments = "cardpayments";
    /// <summary>Paying a credit card when no credit card account is imported at all: that money is the card's spending.</summary>
    public const string CardSpending = "cardspending";
    public const string Investments = "investments";
    public const string Bills = "bills";

    /// <summary>The categories the app comes with, as they are before the person renames, nests or colours them.</summary>
    public static readonly IReadOnlyList<Category> BuiltIn =
    [
        new("groceries", "Groceries", CategoryKind.Expense, "ShoppingBasket"),
        new("restaurants", "Restaurants & cafés", CategoryKind.Expense, "Restaurant"),
        new("transport", "Transport", CategoryKind.Expense, "DirectionsBus"),
        new("car", "Car & fuel", CategoryKind.Expense, "LocalGasStation"),
        new("housing", "Housing", CategoryKind.Expense, "Home"),
        new("utilities", "Utilities", CategoryKind.Expense, "Bolt"),
        new("telecom", "Phone & internet", CategoryKind.Expense, "Wifi"),
        new("subscriptions", "Subscriptions", CategoryKind.Expense, "Subscriptions"),
        new("shopping", "Shopping", CategoryKind.Expense, "ShoppingBag"),
        new("health", "Health", CategoryKind.Expense, "LocalPharmacy"),
        new("insurance", "Insurance", CategoryKind.Expense, "Shield"),
        new("entertainment", "Entertainment", CategoryKind.Expense, "Theaters"),
        new("travel", "Travel", CategoryKind.Expense, "Flight"),
        new("education", "Education", CategoryKind.Expense, "School"),
        new("personal", "Personal care", CategoryKind.Expense, "Spa"),
        new("gifts", "Gifts & donations", CategoryKind.Expense, "CardGiftcard"),
        new("fees", "Fees & interest", CategoryKind.Expense, "Percent"),
        new("taxes", "Taxes", CategoryKind.Expense, "AccountBalance"),
        new("cash", "Cash withdrawals", CategoryKind.Expense, "LocalAtm"),
        new(Bills, "Bill payments", CategoryKind.Expense, "Receipt"),
        new(CardSpending, "Credit card spending", CategoryKind.Expense, "CreditCard"),
        new(OtherExpense, "Other expenses", CategoryKind.Expense, "MoreHoriz"),

        new("exc-flights", "Flights", CategoryKind.Expense, "FlightTakeoff"),
        new("exc-trips", "Trips & holidays", CategoryKind.Expense, "Luggage"),
        new("exc-gifts", "Special gifts", CategoryKind.Expense, "Redeem"),
        new("exc-events", "Celebrations & events", CategoryKind.Expense, "Celebration"),
        new("exc-purchases", "Big purchases", CategoryKind.Expense, "Chair"),
        new("exc-repairs", "Unexpected repairs", CategoryKind.Expense, "Build"),
        new("exc-medical", "Medical emergencies", CategoryKind.Expense, "MedicalServices"),

        new("salary", "Salary", CategoryKind.Income, "Work"),
        new("refunds", "Refunds", CategoryKind.Income, "Replay"),
        new(OtherIncome, "Other income", CategoryKind.Income, "Savings"),

        new(Transfers, "Transfers", CategoryKind.Transfer, "SwapHoriz"),
        new(CardPayments, "Credit card payments", CategoryKind.Transfer, "CreditScore"),
        new(Investments, "Savings & investments", CategoryKind.Transfer, "TrendingUp"),
    ];

    private static Dictionary<string, Category> ById = BuiltIn.ToDictionary(c => c.Id);

    /// <summary>Every category in use: the built-in ones as the person edited them, then the ones they made.</summary>
    public static IReadOnlyList<Category> All { get; private set; } = BuiltIn;

    /// <summary>Replaces the categories in use (the database calls this when it opens and after every change).</summary>
    public static void Use(IReadOnlyList<Category> all)
    {
        All = all;
        ById = all.ToDictionary(c => c.Id);
    }

    public static bool Exists(string id) => ById.ContainsKey(id);

    /// <summary>Unknown ids (from an older version of the app) read as "Other expenses".</summary>
    public static Category Get(string id) => ById.TryGetValue(id, out var c) ? c : ById[OtherExpense];

    public static bool IsTransfer(string id) => Get(id).Kind == CategoryKind.Transfer;

    /// <summary>Default category for an amount nobody recognised.</summary>
    public static string DefaultFor(decimal amount) => amount >= 0 ? OtherIncome : OtherExpense;
}
