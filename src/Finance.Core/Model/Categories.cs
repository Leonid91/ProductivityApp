namespace ProductivityApp.Finance.Model;

public enum CategoryKind { Expense, Income, Transfer }

/// <summary>
/// A predefined category. Icon is a Material icon name used by the interface. Group "exceptional" marks the
/// exceptional expenses (shown in gold).
/// </summary>
public sealed record Category(string Id, string Name, CategoryKind Kind, string Icon, string? Group = null);

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
    public const string Exceptional = "exceptional";

    public static readonly IReadOnlyList<Category> All =
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

        new("exc-flights", "Flights", CategoryKind.Expense, "FlightTakeoff", Exceptional),
        new("exc-trips", "Trips & holidays", CategoryKind.Expense, "Luggage", Exceptional),
        new("exc-gifts", "Special gifts", CategoryKind.Expense, "Redeem", Exceptional),
        new("exc-events", "Celebrations & events", CategoryKind.Expense, "Celebration", Exceptional),
        new("exc-purchases", "Big purchases", CategoryKind.Expense, "Chair", Exceptional),
        new("exc-repairs", "Unexpected repairs", CategoryKind.Expense, "Build", Exceptional),
        new("exc-medical", "Medical emergencies", CategoryKind.Expense, "MedicalServices", Exceptional),
        new(Exceptional, "Other exceptional", CategoryKind.Expense, "AutoAwesome", Exceptional),

        new("salary", "Salary", CategoryKind.Income, "Work"),
        new("refunds", "Refunds", CategoryKind.Income, "Replay"),
        new(OtherIncome, "Other income", CategoryKind.Income, "Savings"),

        new(Transfers, "Transfers", CategoryKind.Transfer, "SwapHoriz"),
        new(CardPayments, "Credit card payments", CategoryKind.Transfer, "CreditScore"),
        new(Investments, "Savings & investments", CategoryKind.Transfer, "TrendingUp"),
    ];

    private static readonly Dictionary<string, Category> ById = All.ToDictionary(c => c.Id);

    public static bool Exists(string id) => ById.ContainsKey(id);

    /// <summary>Unknown ids (from an older version of the app) read as "Other expenses".</summary>
    public static Category Get(string id) => ById.TryGetValue(id, out var c) ? c : ById[OtherExpense];

    public static bool IsTransfer(string id) => Get(id).Kind == CategoryKind.Transfer;

    /// <summary>Default category for an amount nobody recognised.</summary>
    public static string DefaultFor(decimal amount) => amount >= 0 ? OtherIncome : OtherExpense;
}
