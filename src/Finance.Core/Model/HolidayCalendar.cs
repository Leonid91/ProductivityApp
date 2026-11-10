namespace ProductivityApp.Finance.Model;

/// <summary>
/// A calendar of public holidays defined in Definitions > Holidays: the place it is for (free text), the address of
/// its file (ICS or JSON; "{year}" in it stands for each year when the file holds one year), whether it is checked
/// (its holidays are shown on the calendar and are not business days), and the country it is part of (<see
/// cref="Parent"/>: a province's or a state's calendar sits under its country's federal one, whose holidays apply in
/// it too: checking it checks its country; unchecking the country unchecks it).
/// </summary>
public sealed record HolidayCalendar(string Id, string Location, string Url, bool Active, string? Parent = null)
{
    /// <summary>Canada's federal holidays, one file per year, from canada-holidays.ca (the day off when a holiday falls on a weekend).</summary>
    public static readonly HolidayCalendar Canada = new("ca", "Canada (Federal)", "https://canada-holidays.ca/api/v1/holidays?federal=true&year={year}", true);

    /// <summary>Quebec's holidays (the CNESST list), one file per year, from canada-holidays.ca; under Canada.</summary>
    public static readonly HolidayCalendar Quebec = new("qc", "Québec", "https://canada-holidays.ca/api/v1/provinces/QC?year={year}", true, "ca");

    /// <summary>The US federal holidays of the Office of Personnel Management (their days off), 2021 to 2030.</summary>
    public static readonly HolidayCalendar UnitedStates = new("us", "United States (Federal)",
        "https://www.opm.gov/policy-data-oversight/pay-leave/federal-holidays/holidays.ics", false);

    /// <summary>France's public holidays (metropolitan France), from the government's service, twenty years back and five ahead.</summary>
    public static readonly HolidayCalendar France = new("fr", "France", "https://calendrier.api.gouv.fr/jours-feries/metropole.json", false);

    /// <summary>The calendars every database starts with (Canada and Québec checked), also offered again for a new line.</summary>
    public static readonly IReadOnlyList<HolidayCalendar> Defaults = [Canada, Quebec, UnitedStates, France];
}

/// <summary>
/// One public holiday: its day and its name as its calendar writes it. <see cref="Observed"/>: the day off given for a
/// holiday falling on a weekend (the holiday itself is kept on its own day too).
/// </summary>
public sealed record Holiday(DateOnly Day, string Name, bool Observed = false);
