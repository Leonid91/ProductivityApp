# Development log

## October 27th, 2026 - Finances foundations - Commit 1

### Added
- Windows desktop app: C# .NET 10 engine, React + TypeScript interface (MUI, MUI X Charts, FullCalendar) shown in WebView2,
  borderless window with its own title bar, resize edges, snapping, light and dark themes. No native dialog or raw HTML is
  shown: files and folders are chosen in an in-app browser, statements can also be dropped on the window.
- Single self-contained `ProductivityApp.exe` (`tools/build.ps1`). Works with no network: the interface, fonts and
  engine are inside the file, the page is blocked from loading anything else.
- Statement import for any bank, French or English, without any trained model:
  - layout reader: finds the table header (or the columns amounts line up in), rebuilds rows and multi-line labels,
    reads FR/EN dates and amounts in every common format, value dates, credit card statements;
  - checks every statement to the cent against its opening, closing and running balances; several readings are tried
    and the one that balances is kept;
  - column template set once by dragging the column borders on the page picture, checked live, applied automatically
    to later statements of the same bank;
  - scanned statements read with the text recognition built into Windows;
  - review before saving: account, brand and category of each line, duplicates from overlapping statements left out,
    same file refused twice.
- Classification: 24 predefined categories, about 200 built-in brands (Canada, France, US), wording rules; every
  correction of a brand or category is learned for that merchant and that brand and applied to past operations.
- Periods view as in the sketch: Month / Year / Several years; title "Month, from Weekday XX to Weekday XX, Year";
  table Date (format chosen in the column menu, DD month YYYY by default), Category (filter menu, editable),
  Brand Name (editable), Amount with its currency (CAD by default); Expenses, Earnings, Balance (green, red or black);
  pies of expenses per category and per brand; bars of expenses per day (per month for a year, per year for long ranges);
  Export to PDF.
- PDF reports, always the same layout, with the same charts.
- Calendar of daily spending and income; brands page with the learned corrections; settings (currency, date format,
  theme, reports folder, accounts, erase all data); manual operations.
- Local SQLite database, money stored as exact decimals.
- Tests: 86 unit and integration tests on generated fictitious statements (five layouts, multi-page, scanned), and an
  end-to-end check that drives the real app off-screen (`tools/test.ps1`).

## October 27th, 2026 - Banking systems, Banque Nationale, CSV import - Commit 2

### Added
- Banking system setting: North America (default) or France. Account types follow it: chequing, savings and
  credit card (North America); current account, savings (livret) and deferred debit card (France).
- Movements between one's own accounts are recognised and never counted as spending or income: paying the credit
  card from chequing, transfers to and from savings, investments. Both halves are linked across accounts even when
  the wording says nothing; "Savings & investments" category for money put aside.
- Bank setting with Banque Nationale (BNC) as a proven bank: its "Historique de mes transactions" PDF exports and
  its CSV exports (Mastercard, chequing, high-interest savings) are read exactly, with the bank's own categories.
- CSV import for any bank: separator, quoting, encoding, header or no header, debit/credit or signed amounts,
  day/month order and decimal comma are detected; files with a balance column are checked to the cent.
- The same month imported as PDF and as CSV is recognised: nothing is added twice.
- Account type can be changed in the settings.
- Tests: fictitious BNC-style month (three accounts, PDF and CSV) and CSV exports in other banks' styles (99 tests);
  the end-to-end check imports them through the interface.

### Changed
- The bank's category column, when a statement has one, decides the category before any guess from the label.
- Statements listing the newest operation first are put back in time order before their balances are checked.
- Lines with a zero amount (waived fees) are left out.

### Bug fixed
- A "+" amount on a card statement was read as a purchase: it is a payment or a refund.
- "EUR" was found inside French words (VALEUR, DEBITEUR) and turned Canadian statements into euros.
- A shop named in the transactions could be taken for the bank: the bank is now read above the table only.
- A chequing statement mentioning credit card payments was read as a credit card statement.

## October 28th, 2026 - Import never guesses the account, transfers apart, exceptional expenses - Commit 3

### Added
- Each imported file waits until the person chooses its account (or a new account and its type); it cannot be saved
  before, and it is then read for that type. No account or type is ever chosen automatically.
- Month view: transfers, credit card payments and investments in their own folded table (setting: folded table,
  hidden, or with the other operations). Dates newest first by default.
- Exceptional expenses, shown in gold: Flights, Trips & holidays, Special gifts, Celebrations & events,
  Big purchases, Unexpected repairs, Medical emergencies, Other exceptional.
- Calendar: spending in red, income in green, money moved between accounts or invested in black; credit card
  payments hidden by default (setting).
- Categories "Credit card payments", "Credit card spending" (card paid while no card account is imported) and
  "Bill payments"; card payments are named "Credit card payment".

### Bug fixed
- Lines were dropped as "already imported" when another operation had the same date and amount: a duplicate must
  now also be the same operation (label).
- A card purchase could be paired with a transfer of the same amount and left out of spending.
- Transfer pairs depended on the order files were imported in: everything is linked again after each import.
- Brands keep the bank's own capitalisation ("Il ..." not "IL ...").

## October 28th, 2026 - Missing operations, recurring payments, money moved, category scope - Commit 4

### Added
- Missing operations: every account whose statements print balances is checked from one known balance to the next;
  when the saved operations do not add up (a month cut from an export, lines missing between two files), the month
  shows how much went out or came in, between which days, and the operations it most likely is (the same ones as in
  the nearby months, adding up exactly to the cent). One click adds them as "Estimated"; importing the statement
  that holds the real ones replaces them. Shown in the PDF too.
- Import: a plain "Balance check of this file" (starts with, ends with, what its lines add up to) and whether the
  file follows on from what is already saved for the chosen account.
- "Recurring payments & subscriptions" and "Regular income" tables after the operations, with their total for the
  month or the year (and the monthly average for a year), found automatically over the whole history: rent,
  insurance, phone, subscriptions, fees and salary; everyday purchases at the same price are never taken for them.
  Moved out of the main table, never shown twice. A pie for each after the two main pies.
- Transfers, card payments and investments table with From and To: each movement once, between two imported
  accounts or to and from the outside (accounts not imported, other people, investments), in orange.
- "Money moved" diagram in every month and year (screen and PDF): sources on the left, destinations on the right,
  blue between own accounts, orange to or from the outside.
- Changing a category asks: this operation only, or every operation of the brand (past and future). Also in the
  edit window and during import.
- Settings: pies show every category and brand (default) or the five largest and "Other"; the calendar hides every
  transfer, card payment and investment (default).
- Built-in subscriptions: OpenAI, Proton, GitHub, Patreon, Midjourney; a known subscription stays a
  subscription when the bank files it under "Electronics & software".
- Tests (112): missing rent found in every month it is cut from, over four generated years and any import order;
  estimated lines replaced by the real ones; recurring payments and income; From / To of every movement; category
  for one operation or the whole brand; pie colours; every day on the axis. The end-to-end check answers the new
  category question.

### Changed
- Expenses per day: every day of the month on the axis, from the 1st to the last (screen and PDF).
- Expenses box faintly red, earnings faintly green, balance more strongly in the colour of its sign (screen and PDF).
- The PDF report has the same sections as the screen.
- "Beneva" is shown as Beneva (was La Capitale).

### Bug fixed
- Exceptional categories, "Other exceptional" included, were not gold in the edit window.
- Lines of zero (waived fees) no longer shorten the period a statement covers: their dates count for the balances.
- An Amazon purchase filed by the bank under "Internet" was taken for a phone bill: a known shop must come back
  before it counts as recurring.
- Tests: the generated test statements carried the time they were made, so "the same file twice" failed whenever
  the two copies were made in different seconds; they now carry a fixed date.
