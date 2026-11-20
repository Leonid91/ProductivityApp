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

## October 28th, 2026 - Estimated operations and balance-gap warnings removed - Commit 5

### Removed
- "Estimated" operations: the app never adds an operation that is not in an imported file. Lines added that way by
  the previous version are deleted when the app opens, with their column.
- The "operations missing" warnings in the month view, the import screen and the PDF, and their suggestions: days
  without any operation in a statement are normal, and money moved to accounts that are not imported cannot be
  explained by the imported files.

### Kept
- The balance check of each file on its own (its opening balance, its closing balance and what its lines add up to).

### Changed (Commit 5)
- A transfer whose other side is not imported is named after who sent or received it when the bank says it
  ("A. Sample (not imported)"), instead of "Outside (not imported)"; From and To wrap instead of being cut.
- The category of a transfer can be changed from the transfers table, with the same question (this operation only or
  every one of that name): a friend paying you back set to "Refunds" then counts in earnings.

## October 28th, 2026 - Money-moved diagram with arrows, subscription burden, spending ratio - Commit 6

### Changed
- Money-moved diagram redrawn: each account is a box with its name, then its type ("Chequing — Chequing account");
  everything outside the app is one dashed blob listing who is behind it (investments, people, unnamed transfers)
  with what was sent and received; one arrow per direction between two places, pointing where the money went,
  as thick as the amount, with the amount on it. Laid out once by the engine, so the screen and the PDF match.
- Transfers table: the type of each account under its name in From and To.
- Recurring payments total on a faint red tint, regular income total on a faint green tint (screen and PDF).

### Added
- "Recurring payments, rent excluded" (the subscription burden) for the month, year or range, and its share of
  earnings.
- "Expenses / earnings": the share of what came in that was spent.

## October 28th, 2026 - All income listed, closed investments, earned part - Commit 7

### Changed
- "Regular income" becomes "Income": every income of the period whatever its source (salary, refunds, tax
  refunds, interest...), with how often it comes back when it does; its total is the period's earnings, and its pie
  shows every source. Income lines leave the main table, which keeps the other spending.

### Added
- "Of which earned (gain, interest)" on money coming back from an investment or a transfer: only that part counts as
  income (listed as "… (earned part)"); the rest is the person's own money coming back. Never guessed: empty unless
  the person enters it. Screen and PDF.

### Bug fixed
- A term investment closed at the bank ("Placement", filed by the bank under "Retrait") was counted as a refund,
  so as income: it is now money coming back from investments. Money coming back under "withdrawal" or "fees" is
  never a refund. "Placement", GIC / CPG, term deposits and Interactive Brokers are recognised as investments.

## November 3rd, 2026 - Nested categories, several banks, currencies per file, settings in tabs - Commit 8

### Added
- Categories page in the side bar: categories the person makes, nested at any depth, renamed, coloured, moved into
  another category of the same kind, reordered and deleted (built-in ones can be edited but not deleted; the
  operations of a deleted category go to the category it was in). The colour shows on every category pill (periods,
  calendar, import, brands) and in every category picker, where nested categories are indented under theirs.
- Several banks at once, each with its country, banking system (North America or France) and currency. Every account
  belongs to a bank and is read with that bank's banking system. The import dialog asks the bank of a new account
  (the one the statement names is proposed) or adds a new bank; the currency follows the bank (CAD for the National
  Bank of Canada).
- Import table: Bank and Currency columns. The currency of a file is its account's: changing it there or in the
  account table changes it everywhere. Files of one bank in different currencies show a "Currency mismatch" warning.
- Periods: "Custom" tab with any two days, picked on a calendar or typed; the month and custom views show how many
  days the period has.
- Ctrl+F: finds text anywhere in the window, highlights every match, Enter / Shift+Enter go from one to the next.
- Rows of every table outside Periods can be reordered by dragging their handle; the order is kept.
- Light / dark mode button above the settings.
- Database location can be changed to any folder (the database is moved there).

### Changed
- Settings in tabs: General (Display, Periods, Calendar, Data), Bank Configuration (one tab per bank with its
  accounts: name, type, currency), Definitions (Brands, Bank). Brands left the side bar.
- Periods setting "Transfers, card payments and investments" also offers "In a separate table" (open); the folded
  table stays the default. "Show all data in pie charts", off by default.
- Calendar: three settings, Hide transfers (on), Hide credit card payments (on), Hide investments (off).
- "Banque Nationale (BNC)" is named National Bank of Canada; the global banking system and bank settings, the display
  currency and the theme setting left the settings.

### Bug fixed
- A learned correction was listed twice (once for the statement wording, once as "Every operation of this brand"):
  each brand is now one line with the wordings learned for it.
- Changing the category of a brand in the brand list did not reach operations whose wording had a learned category:
  every operation and every learned wording of the brand now take it.
- Tests: 113 unit and integration tests (categories made, nested twelve levels, edited, moved, deleted; banks and
  currencies) and 37 end-to-end checks.

## November 3rd, 2026 - Faster and leaner on large data - Commit 9

Measured on 41,520 fictitious operations (ten years, two accounts), with `SampleStatements big` and `bench`, and the
real app with `tools/e2e/perf.mjs`.

### Changed
- Operations are kept in memory between writes, and writes update that copy instead of dropping it; texts that repeat
  (currencies, categories, brands, wordings) are stored once.
- The classifier works out what a wording says once per wording, not once per operation.
- What is recurring is found once over the history and kept until the data changes, instead of for every period.
- A brand's new category is written to all its operations at once.
- Indexes for the operations of a statement and of an account.
- Periods are drawn in the background a little at a time, periods far from the screen are not laid out or painted,
  and they let go of their data once far away (their height is kept).

### Results
- Scrolling through the months: 80 fps before, 156 fps after. Category picker: 194 fps before, 240 fps after.
- Month summary 6 per second before, about 2,470 after; year 5.7 -> 143; ten years 2.9 -> 31.
- Category change for a whole brand 0.6 per second before, 17.6 after; brand rename 0.8 -> 13; import 7,830 -> 14,225
  operations per second.
- Engine memory once loaded: 33.6 MB -> 19.8 MB managed, peak 245 MB -> 134 MB. Interface heap after scrolling the
  months: 64 MB -> 51 MB.

### Added
- Fictitious Caisse d'Epargne statements in the samples (three months), and `SampleStatements big` / `bench` for large
  data (Caisse d'Epargne-style and English CSV exports).

## November 3rd, 2026 - Smaller app - Commit 10

### Changed
- The Windows API projection used for text recognition is trimmed to what the app calls (every other part is kept
  whole): `ProductivityApp.exe` goes from 66.4 MB to 60.7 MB.
- `-p:E2E=true` publishes a build the end-to-end check can drive, so the shipped configuration (trimmed) is checked
  too; never shipped.

## November 3rd, 2026 - Period layout fixed, labels renameable, every account at a bank - Commit 11

### Fixed
- The month view could stretch far wider than the window: periods not drawn yet were given a guessed width as well as
  a guessed height (2400 px), and with many months that width won. Only the height is guessed now, and the column of
  periods can never grow past the window.
- Month, Year, All time and Custom share the same period card with the same settings: tables in pages of 25 and the
  number of days next to every title.
- A correction learned for a whole brand showed "—" as its statement wording: it now shows the brand's wordings on the
  statements ("Entered by hand" or "No operation yet" when there are none).

### Changed
- "Several years" is replaced by "All time": from the first operation to today, found by itself.
- Settings: Periods and Calendar are groups inside Display; Definitions has a Categories tab (same as the side bar).
- Expenses, Exceptional, Income and Transfers (was "Between my accounts") are the first-layer labels of the
  categories, shown as chips and renamed in place.
- Banking systems are named by how cards work: "Credit card system" and "Deferred debit system".
- An account is always at a bank: an import without a bank, an operation without an account and deleting a bank that
  still has accounts are refused; the "Cash & manual entries" account and the "No bank" tab are gone. Database v8
  removes empty accounts without a bank and moves the others to the bank of the first account.
- CSV, OFX, QFX and QIF files are ignored by git and refused by the pre-commit check, like PDFs and databases.

### Added
- The full path of a category on hover ("Expenses › Food › Snacks").
- Table headers stay visible while a long table scrolls, under the bar of tabs.
- A warning on the Import page while PDF statements are imported: only PDFs downloaded from the bank.

## November 3rd, 2026 - Categories only in Settings, three kinds of operation - Commit 12

### Changed
- Categories are only in Settings > Definitions, whose tabs now go Bank, Brands, Categories; the side bar item is gone.
- Expenses, Income and Transfers are kinds of operation, not categories: plain headings (renamed in place), each with
  its own button to add a category of that kind, so a transfer category is added like any other.
- "Exceptional" is gone: Flights, Trips & holidays and the others are ordinary expenses, without the gold colour (in
  the app and in the PDF reports); "Other exceptional" joins "Other expenses" (database v9 moves its operations and
  rules, and the categories nested in it move to the top).
- No line under the last row before a group in Settings (Date format above Periods).

## November 8th, 2026 - Tables and Charts, expenses per category per day, money moved redrawn - Commit 13

### Changed
- Every period (Month, Year, All time, Custom, and any period added later) has a second layer, Tables or Charts:
  the tables in one, every chart in the other; Expenses, Earnings, Balance, recurring payments and the expenses /
  earnings ratio sit at the bottom of both, above the PDF export.
- Money moved: everything outside the app is one orange box of the same size as the accounts, without amounts in
  it (they are on the arrows; who is behind it shows on hover). Every arrow has its own point on each box, never
  shared with another arrow coming or going, comes into the box square to its side, goes around the other boxes,
  and its amount sits clear of boxes, heads and other amounts. Long account names are cut with "…" (full name on
  hover). Same drawing in the PDF.
- Expenses / earnings: red like the expenses above 100% (more spent than earned), green like the earnings below; the
  "spent of earned" line is gone. The line under "Recurring payments, rent excluded" is gone.
- Settings: banks and accounts are added with buttons and a dialog, not typed into the tables. Bank Configuration
  has Add account and Add bank on the right of the tabs and shows every bank as a tab; Definitions > Bank has Add
  bank.

### Added
- Expenses per category, per day (Charts): one line per category and the total always; the top five first, then
  none, any top n or all, or each category ticked on or off.

### Fixed
- Chart tooltips (the amount of a day in Expenses per day, the pies) were placed off the screen: they now open
  next to the pointer.

## November 8th, 2026 - Statements and Analytics, categories listed beside their lines, table filters, first-level categories - Commit 14

### Changed
- The second layer of every period is named Statements | Analytics.
- Expenses per category, per day: no drop-down any more. Beside the chart, under "Categories", the total and every
  category with expenses in the period (the person's own categories included), each ticked on or off; the total and
  the five largest are ticked at first. The vertical scale follows the lines shown.
- Money moved: every arrow is as thick as its money again, with a head large enough to see. Each side of a box gives
  every arrow its own stretch, as wide as its line or its head, so heads never cover one another; boxes grow (all
  the same size) when a side needs more room.
- Pie shares are shown with two decimals (0.29%, not 0%), on screen and in the PDF.
- Bank Configuration has only Add account; banks are added in Definitions > Bank.

### Added
- Filters in every table of a period: dates, categories and brands (descriptions for transfers), each all, none,
  one or any; the dates keep their format and order choices.
- Settings > Periods: "Show only first level categories in charts", on by default. A category placed above others
  (Food over Groceries and Restaurants & cafés) shows as one slice and one line with all of their money, counted once;
  off, the charts go down to every nested category. The tables keep each operation's own category. The PDF follows it.

### Fixed
- Filters, order, page and chart choices of a period were lost when it scrolled away (or when switching views): they
  are kept.
- Category filter: the count no longer touches long names.

## November 8th, 2026 - Category filter in the transfers table - Commit 15

### Added
- The table of transfers, card payments and investments has a category filter (all, none, one or any), next to its
  date and description filters; it is kept while the period is away from the screen.

## November 9th, 2026 - Tags, Summary, every download format, Scotiabank statements - Commit 16

### Added
- Tags (none come with the app): made, renamed, coloured and deleted in Settings > Definitions > Tags. A tag goes on
  single operations (from the Tags column of every table of a period, and of the PDF) or on a category in its dialog:
  every operation of that category, and of the categories nested in it, then carries it. Several tags per operation
  and per category. Every table has a Tags filter (all, none, one or any, "No tag").
- Analytics: a Tags pie. An operation with several tags is in one slice named after all of them ("Travel + Work"),
  so no money is counted twice; the slices add up to the tagged expenses. In the PDF too.
- Summary, left of Statements: only the balance, month by month (year by year for periods longer than a year), each
  with its dates, then the period's balance. Works for months, years, all time and custom periods.
- Import of every format Canadian banks offer: Excel (.xlsx, .xls, and ".xls" web pages), OpenDocument (.ods, .odt),
  Word (.docx), web pages, OFX / QFX / QBO (Money, Quicken, QuickBooks), QIF, and pictures of statements (PNG, JPEG,
  WebP, BMP, GIF, TIFF), read like scans.

### Changed
- Scanned pages and pictures are read from the page's own picture, smoothly enlarged, instead of the page drawn
  again: small digits and decimal points come out right far more often. Decimal points lost by the recognition are put
  back on amounts aligned with columns of money; numbers split in two are joined; a misread digit is corrected when
  the running balances around it prove the right amount.
- Categories for a bank never seen before: e-mail money transfers, wires, ABM withdrawals, line-of-credit card
  payments and service charges are recognised; "Point of sale purchase", "Opos", "Fpos" no longer pass for a shop.
- A statement whose running balances disagree with its amounts is shown as off even when it prints no opening or
  closing balance.

### Fixed
- Scotiabank chequing statements: the "Amounts / withdrawn ($)" and "Amounts / deposited ($)" headings on two lines
  were read as one amount column (every deposit was lost or counted as spending).
- Scotiabank card statements: lines starting with a reference number ("001") were not read; "SUB-TOTAL CREDITS" and
  "SUB-TOTAL DEBITS" were counted as operations.
- The code printed down the margin of Scotiabank statements broke two-line descriptions; it is left out.
- A description split by a page break ("Continued on next page") keeps its second line.
- CSV exports without a header keep every text column in the label ("Point of sale purchase" and the shop).

## November 9th, 2026 - Summary scrolls back up, a year shown alone - Commit 17

### Changed
- Summary of a year: the year alone with its balance (a month shows its month, all time and long custom periods
  their years, shorter custom periods their months).

### Fixed
- Summary: after scrolling down (or switching to Summary from far down in Statements), scrolling up jumped back down
  again and again. Every month (or year) in Summary is now as tall as the others whether loaded or not, so nothing
  above the screen changes size while scrolling.

## November 9th, 2026 - Banks side by side, a global currency, Caisse d'Epargne statements - Commit 18

### Added
- Periods, bank by bank: Statements and Analytics show one block per bank (its tables or charts and its totals, in
  the bank's currency), then "All banks" (totals and charts) in the global display currency. The Summary stays
  global. The calendar shows every bank together, a converted amount with the amount the bank wrote and its rate.
- Global display currency, in the title of Settings > Display, followed by the link to the source of the exchange
  rates (European Central Bank euro reference rates) and a button to download them again.
- Exchange rates: the European Central Bank's whole history (every working day since 1999, about 30 currencies) is
  downloaded for free when some accounts are in another currency, kept in the database (works offline afterwards),
  and refreshed when it falls behind. Each operation is converted at the rate of its own day (the last rate before
  it on weekends and holidays), through the euro between two other currencies, rounded to the cent; totals add the
  rounded amounts. "All banks" shows how: the source, the method, each bank's currency before and after, the lowest
  and highest rates, the rate of every day, and any operation left out for want of a rate. The PDF report has the
  same sections and conversions.
- Caisse d'Epargne: the monthly PDF of every account (current account and Livret A in one file) gives one balanced
  statement per account; the CSV export since 2020, the older CSV export (balances above and below the table), the
  Money (OFX) and Quicken (QIF) exports, decimal commas included.
- `SampleStatements caisse <folder>` and `twobanks <folder>`: invented Caisse d'Epargne files in every format, and a
  Canadian bank (CAD) with a French bank (EUR) over the same months.

### Changed
- French banking system named "Debit card system": card purchases come out of the current account; only a
  deferred-debit card is settled by a monthly debit. No credit card wording is applied to French banks.
- Livret A, LDDS, LEP and Livret Jeune are savings moved between one's own accounts, not investments.
- "Credit card payments" and "Credit card spending" are named "Card statement payments" and "Card statement
  spending" (true of both banking systems).
- The periods are listed for every bank together (no currency to pick).
- The automated check that scrolled the Summary up and down is gone (too narrow); the app tests now wait for the
  periods on screen to be loaded before reading them, and click without scrolling when the button is already in view.

### Fixed
- "Caisse d'Epargne" in a file name made every account a savings account.
- QIF amounts with a decimal comma ("-64,23") were read a hundred times too large.
- French transfers without a payee were named "Interac e-Transfer"; a transfer paying rent ("VIR SEPA LOYER") or
  returning money ("REMBOURSEMENT") was a plain transfer.
- CSV exports repeated the simplified label and added the bank's tick ("0") to every label.
- Periods: switching view far down the page, or periods loading above the screen, moved the page: the month on
  screen went away, and scrolling up could pass over a month still drawn at its old height. The month at the top of
  the screen now stays put whatever changes height above it; periods load when they come within 1,500 pixels of the
  screen (that margin was measured against the window instead of the page's scrolling area, so it never applied),
  and each period is drawn in full once whenever its content changes, so its real height is known before it is reached.
- Tags menu of an operation: ticking two tags quickly kept only the second.
- Calendar: the amounts of a day no longer overflow the panel.

### Speed (41,000 operations over ten years, a Canadian and a French bank)
- Each period now shows both banks and all banks together; all banks send the conversion of each operation instead
  of the operations a second time (a year: 3 MB of data down to 1.7 MB). Exchange rates are read only for the
  currencies used.
- Previous version, then this one: scrolling months 110 fps, 106 fps; scrolling years 233 fps, 154 fps; switching to
  years 240 fps, 193 fps; ten years 219 fps, 207 fps; calendar, category picker and brands 240 fps, 240 fps.

## November 9th, 2026 - Old unpacked copies removed from drive C: - Commit 19

### Fixed
- The app is one file that unpacks itself (about 120 MB) into %TEMP%\.net\ProductivityApp the first time each build
  runs; .NET never deletes the copies of earlier builds, so every new version left 120 MB more on drive C: (twenty
  builds had left 2.4 GB). At start the app now deletes the copies of other builds that no program is using.

## November 9th, 2026 - Exchange rates from the bank of your choice, every day - Commit 20

### Changed
- Settings > Display: "Global display currency" and "Exchange rates" are rows like "Date format", their values in the
  same column. The exchange rates are a choice: the European Central Bank (by default, rates against the euro since
  1999) or the Bank of Canada (rates against the Canadian dollar since 2017); the bank's page and a new download sit
  beside the choice. The conversion details and the PDF name the bank and the currency its rates go through.
- The rates of the chosen bank are downloaded again every day (checked at start and every hour). Only the latest
  copy and the one before are kept; when the latest cannot be read, the one before is used.

## November 9th, 2026 - Sources of exchange rates as a table; faster and lighter periods - Commit 21

### Added
- Settings > Definitions > Exchange rates (after Bank): a table of the places the rates come from, each a name and
  the address of its file of rates, with the currency its rates are against (read from the file) and the last day
  of its rates. The European Central Bank and the Bank of Canada are there to start with; sources can be added,
  renamed, readdressed and deleted (not the one in use). Two sentences above the table say where to find the address.
- Settings > Display > Exchange rates offers the sources of that table. Choosing one downloads its rates in the
  background, which checks the address: nothing is said when it works; when it does not, the reason is written in
  red under the choice (and under the address in the table): a web page instead of a file, nothing at the address,
  no answer, a file that holds no rates, an address that is not https.
- Files read: tables in the European Central Bank's layout (CSV or ZIP) and Valet JSON files like the Bank of
  Canada's; downloads larger than 32 MB are refused.

### Changed
- The pencil beside the Exchange rates choice, and the source named in a period's conversion details, open the table
  of sources.

### Speed (41,520 operations over ten years, a Canadian and a French bank; previous version, then this one)
- Frame rate unchanged: 240 fps idle, about 165 fps scrolling months and years (within the measuring noise).
- Engine, per period: a month 4.4 ms, now 2.3 ms; a year 51.2 ms, now 24.0 ms; ten years 353.7 ms, now 198.3 ms.
  Memory allocated: a year 10.1 MB, now 5.5 MB; ten years 94.2 MB, now 50.5 MB. Settings were read from the database
  for every operation converted (now kept in memory); a setting saved (window place, row order, rates downloaded)
  made the next period work out the recurring payments of the whole history again (no longer).
- Data sent to the interface: empty fields of the operations are left out (a year 1.7 MB, now 1.2 MB; ten years
  16.4 MB, now 11.6 MB).
- In the app: a year's report 111 ms, now 82 ms; ten years 1,444 ms, now 1,130 ms; switching to All time 1,840 ms,
  now 1,430 ms; start-up after a download of rates 797 ms, now 402 ms.
- The interface starts with 867 KB of code instead of 1,519 KB: Calendar, Import and Settings load when first opened,
  the charts when the browser is idle after start-up. A message shown or a dialog opened no longer draws every period
  again.
- Memory of the app and its browser processes after scrolling the months: 1,136 MB, now 968 MB (engine 439 MB, now
  311 MB).
- `tools/e2e/perf.mjs` also times actions until drawn and measures every process of the app;
  `tools/e2e/perf-quick.mjs` compares two builds on period changes; the engine benchmark times the converted reports.

## November 9th, 2026 - Central banks to choose from, every amount in the display currency - Commit 22

### Added
- Definitions > Exchange rates is a table of central banks: a round button chooses the one whose rates are used (its
  line tinted), each line shows the address of its file, the number of currencies and the last day of rates, both
  read from its last download (nothing shown without one). A new line is chosen among the central banks the app
  reads: the European Central Bank, the Bank of Canada and the US Federal Reserve (its H.10 table, 23 currencies
  against the US dollar). One short sentence above the table says what address to paste.
- Refresh all downloads every bank of the table again; every bank is also downloaded once a day by itself. Each bank
  keeps two copies at most: the one of the day before and today's (a second download the same day replaces today's).
- Any unexpected fault while downloading is said plainly under the bank, like the known ones; an empty answer and a
  file in another bank's layout are said too.
- Every text of the app can be selected and copied (controls cannot).
- The calendar shows the month's spent, earned and balance in its header.

### Changed
- The global display currency is used everywhere: each bank's block, totals, tables and charts are in it; an
  operation in another currency shows the amount its bank wrote under the converted one, and its bank's currency
  shows beside the display one ("EUR → CAD"). An account's currency is the currency its operations come in.
  Operations without a rate for their day are listed in their own currency, outside the totals. Editing an operation
  edits it as its bank wrote it.
- Each bank's block starts with its expenses, earnings and balance; its name stays at the top of the screen while its
  tables scroll, their headers just under it.
- A month is titled "October 2026", a year "2026".
- Settings > Exchange rates: an "Edit banks" button opens the table; the delete button of a line turns red under the
  mouse, with nothing drawn around it.
- Import: the warning about PDFs is a one-line note with a shield icon.
- ExcelDataReader 3.7.0 → 3.9.0.

### Speed (41,520 operations over ten years; previous version, then this one)
- Summary to Statements: the screen froze 1.2 s (one frame, 1 fps), now 0.14 s (longest frame 7 fps); Summary to
  Analytics 1.1 s, now 0.11 s. The period at the top of the screen changes view at once, the others one per frame
  (the screen stays at about 240 fps meanwhile).

## November 10th, 2026 - The US Federal Reserve to start with; bank lines aligned - Commit 23

### Added
- The US Federal Reserve is one of the central banks every database starts with, after the European Central Bank and
  the Bank of Canada; a database made before gets it at its next opening, after its own banks. Its rates are
  downloaded by themselves like the others'.

### Changed
- Definitions > Exchange rates: the round button, the bank's name, the address, the numbers and the delete icon of a
  line share one middle (the button cells had lost the line's top padding); what shows under an address only adds
  lines below.

## November 10th, 2026 - Holidays and business days; the Bank of Canada by default - Commit 24

### Added
- Definitions > Holidays (after Tags): a table of calendars of public holidays, like the one of the exchange rates:
  Location (free text), URL, Number of Holidays per Year (this year's, read from the download; nothing without it).
  Three to start with: Canada - Quebec (checked; canada-holidays.ca, built from the CNESST list, one file per year),
  United States (the federal holidays of the Office of Personnel Management) and France (the government's service).
  Any other calendar can be added: an ICS or JSON file; "{year}" in its address stands for each year (this year, the
  ten before and the next one are downloaded). A new line offers the calendars to start with that are not in the table.
- Several calendars can be checked at once (check boxes, the lines tinted); the change applies at once.
- Refresh all downloads every calendar again; each one is also downloaded once a day by itself. Each keeps two copies
  at most: the one of the day before and today's (a second download the same day replaces today's); the one of the
  day before is used when today's cannot be read. Errors are said under the address, unexpected ones too.
- The calendar shows the holidays of the checked calendars on their day (the same holiday of two calendars once, with
  both places) and in the day's panel.
- Every period shows its business days next to its days: Monday to Friday, less the holidays of the checked calendars.

### Changed
- The Bank of Canada is the source of exchange rates by default.
- The tables of exchange rates and of holidays share their line layout, delete button and error line.

## November 10th, 2026 - Holidays by country and province, weekend days, net salary per hour and per year - Commit 25

### Added
- Definitions > Holidays is a tree: a province's or a state's calendar sits under its country's federal one, set in
  and joined to it by a line. To start with: Canada (Federal) and, under it, Québec, both checked; United States
  (Federal); France. Checking a province checks its country (the federal holidays apply in it); Canada can be checked
  alone; unchecking a country unchecks what is under it; deleting a country deletes what is under it (said first). A
  new line can be put under a country ("Part of").
- Canada's federal holidays and Québec's come from canada-holidays.ca's yearly files, with the day off given for a
  holiday falling on a weekend ("Boxing Day (observed)" on Monday 28 December 2026), which is not a business day.
- Weekend days, under the table: any days of the week (Saturday and Sunday by default). Business days are the days
  that are not weekend days, less the checked calendars' holidays, on every period (month, year, all time, custom);
  the calendar shades the weekend days.
- Analytics: the net salary per hour and per year of the period, every bank together in the display currency: the
  salary received over the business days of the period, 40 hours a week shared by its working days; per year, the
  same daily pay over the business days of a year.

### Changed
- Calendars are downloaded again about once a month (30 days after their last download), not every day; the two
  copies are the previous download and the latest.
- A database made before gets Canada (Federal) with Québec under it (Québec downloaded again from its new address);
  calendars the person changed keep their place and address.

### Space
- Exchange rates: about 8.6 MB per copy (European Central Bank 4.0, Bank of Canada 2.5, US Federal Reserve 2.2), about
  17 MB with the previous copy. Holidays: about 22 KB per copy for the four calendars, about 0.05 MB with both copies.

## November 10th, 2026 - A pencil on every line, holidays total, week start - Commit 26

### Added
- Every table has a pencil at the far right of each line, always shown, opening the line in a dialog; in a table that
  deletes, the delete button comes after it, last (a line that cannot be deleted keeps that place empty, so the pencils
  stay in one column). New dialogs: banks, accounts, brands (a brand renamed on every operation, with what was learned
  for it), imported statements (bank, account name, currency), column templates (name, recognition text), the lines of
  a statement being imported (brand, category, imported or not), central banks and holiday calendars. The pencil of a
  conversion opens Definitions > Exchange rates.
- Definitions > Holidays: under the table, before the weekend days, the holidays of this year of the checked
  calendars together, each day counted once (Canada (Federal) 12 and Québec 8 make 13, not 20).
- Settings > General > Calendar: the week starts on Sunday (North America, by default), Monday (Europe: the weekend at
  the right) or Saturday (Middle East: the weekend at the left); the weekend days of Definitions > Holidays follow.

### Changed
- A calendar and a central bank are added from their button, and changed from their pencil, in the same dialog; no
  line is typed into the table any more. A calendar's country ("Part of") can be changed there.
- Each holiday calendar is checked on its own: checking Québec no longer checks Canada, unchecking Canada no longer
  unchecks Québec.
- The pencils of operations, transfers, categories and tags no longer appear only under the mouse.

## November 10th, 2026 - The + of the categories always shown, under the one of its heading - Commit 27

### Changed
- Definitions > Categories: the + of each line (a subcategory) is always shown, like the pencil, in the column of the
  + of its heading (a new category of the kind).

## November 10th, 2026 - Downloaded rates and holidays kept as their own files - Commit 28

### Changed
- The downloaded exchange rates and holidays are no longer in the database: they are kept as downloaded, in their own
  format, in a downloads folder next to the database: downloads/<kind>/<source>/<day of the download>/<what it holds>
  .<format>, for instance downloads/exchange-rates/bank-of-canada/2026-11-10/rates.json or
  downloads/holidays/quebec/2026-11-10/holidays-2026.json (one file per year when the source gives one per year).
  Every name follows the same pattern whatever the format: lowercase words joined by hyphens, days as yyyy-MM-dd.
- Two downloads at most per source: the latest (n) and the one before (n-1), used when the latest cannot be read (a
  file changed by hand or cut short). A download is written whole in a partial folder, then renamed; a file that does
  not give rates or holidays is never kept. A copy is read once, when first used, and kept in memory.
- The folders follow the sources: a calendar renamed renames its folder, a source deleted or given a new address
  deletes its downloads, and moving the database moves its downloads with it.
- The database keeps only the person's choices (the banks and calendars, their addresses, the bank in use and the
  checked calendars). A database made before drops its copies; every source is downloaded again at the next start.

## November 10th, 2026 - Euro statements shown in euros at import - Commit 29

### Fixed
- Import: a euro statement that prints no currency (the Mutuelle sample) showed its amounts with "$" in the column
  editor. Its SEPA operations now make it a euro statement, whatever the display currency.
- Import: every amount of a statement's card (balance check, status, "add up to" warning) and of its column editor is
  shown in the currency of the account it is imported into, the same as its lines.
- The sample tool's large-data measure builds again (it still saved rates the way the database used to).

## November 10th, 2026 - Imports that overlap, transfers between banks, money moved per bank - Commit 30

### Added
- Unit tests of every way statements can overlap (ImportOverlapTests): periods one after the other, sharing a day,
  part then whole and whole then part, crossing in either order, one inside another in either order, the same
  statement in another file or twice, identical operations on one day, one day and one amount for two operations,
  the same operation dated differently in two files, two purchases on both sides of the end of a statement, two
  crossing files opened together, a statement deleted then imported again, operations corrected by hand, operations
  entered by hand; and across banks: a transfer between two banks whatever the order of the imports, crossing
  statements of both banks, a transfer across the end of a month, two transfers of one amount to two accounts,
  transfers inside one bank, a transfer between two currencies, a salary of the same value, an amount too far from the
  rate, a transfer waiting for the rates of its day, two accounts with one name at two banks.
- The sample statements hold a transfer between two banks in two currencies (500 EUR from the Caisse d'Epargne to
  Northwind Bank, received as 800.00 CAD).

### Changed
- Money moved: one card of its own per period, after the banks, for every bank together, in the display currency (it
  was inside each bank and again in All banks). Each bank is a box holding its accounts; an arrow between two accounts
  of one bank stays inside its box, an arrow between two banks goes from the account of one to the account of the
  other. "Outside the app" sits between the banks (between two columns of banks, or between the two banks of the
  last row, or beside the last one) so every account reaches it by a short arrow; an arrow that would cross another
  box bends once in a gap between banks, arrows sharing a gap run side by side. Inside its bank an account named
  after the bank is named without it ("Account •••• 8901"), its full name on hover. The PDF draws it the same way,
  once, after the banks.

### Fixed
- A transfer between accounts in two currencies (euros sent from a French bank, dollars received in Canada) was never
  paired: it counted as an expense in one bank and an income in the other. Its two halves are now one movement when
  the amount received is the amount sent at the rate of its day, within 4% (the banks' rate and fees), up to 4 days
  apart and worded as a transfer on at least one side; they are paired again when new rates arrive.
- The same operation dated differently in two files of one account (the day of the purchase, the day the bank posted
  it) was counted twice. It is now one operation up to 3 days apart, when one of the two files covers the other's day
  without holding the operation itself.
- An operation entered by hand, then imported with its statement, was counted twice: it is now recognised by its brand.
- Two accounts with one name at two banks were drawn as one box.

## November 10th, 2026 - Readable money moved, international transfers through Wise and Revolut - Commit 31

### Changed
- Money moved, laid out again from the ground up: accounts placed in their bank by where their money goes (the ones
  moving money with another bank or the outside on its side); banks ordered so that those moving money together sit
  side by side; arrows chosen knowing the others (no crossing where one can be avoided), natural curves without loops,
  never over a bank's name, never out of the drawing; an amount never over its own arrow (it goes beside a short one),
  a box, a bank's name, a head or another amount; more room between the boxes when it is too tight. Several layouts
  are drawn and the clearest kept; a diagram already drawn is reused.
- Transfers between two currencies: the way of the transfer services (Wise, Revolut: named in the wording or as the
  bank of an account, a few more days between the halves, the exchanges between the currencies of an account held
  there) is tried beside the general way, and the one pairing the most operations is kept, at every import.
- What is lost to exchange and fees on a transfer between two currencies (the amount sent at the rate of its day, less
  the amount received) is said in the money-moved card and on its arrow, in the display currency; the movements table writes
  such a transfer in both currencies ("€820.00 sent, $1,296.26 received").
- An operation entered by hand stays the person's: never taken for a line of a statement. Entering exactly an
  operation the day already has (same amount, brand and account) is said while typing, never refused.
- Brands: the dialog shows the brand and the category as the same kind of field (a list of brands, a category list).
- Learned corrections: the statement wordings one after the other, comma-separated, a line breaking only between two.
- Holidays: the year of a pasted link is found by the app (".../holidays?year=2026"), no "{year}" to write; the help
  says only what link to paste.
- Settings: the date format examples show today's date; "Expenses per category charts: main categories only" names
  the charts it changes; "Select source" beside the exchange rates.
- No phrase of the interface or the PDF joined to the next by a semicolon (the end-to-end check reads every screen).

### Added
- Unit tests: the layouts seen in real use (one bank in a month and in a year, several banks, four banks with Wise)
  and 150 invented ones are checked for readability; four banks in three currencies with Wise between them imported
  from their statements (every transfer one movement, totals, what the banks kept, the diagram).
- The sample statements hold the four banks with Wise (October 2026), imported and looked at in the end-to-end check.

## November 11th, 2026 - Transfers over time, Revolut statements, any currency - Commit 32

### Added
- Analytics in two parts, chosen beside Summary / Statements / Analytics: General (the charts of spending and income)
  and Transfers (the money moved between accounts).
- Transfers over time, first in Transfers: one coloured line per account, the accounts of a bank together in the box
  of their bank, everything outside the app last, in orange; time from left to right, in steps of a day (up to 45
  days), a week, a month or a year by the length of the period. Each movement leaves the line of its account on its
  day, in the colour of that account, as thick as its amount, and ends with a dot on the line it reached: inside a
  bank it stays in the bank's box, between two banks it goes from one box to the other. The largest amounts are
  written beside their dot (day, amount, from and to) where they cover no other label; pointing at any movement
  writes it and fades the others. On the right, what each account gained or gave; above, the totals inside a bank,
  between banks, sent outside, received from outside and lost to exchange and fees. Accounts moving money together
  are put side by side (every order is tried). Money moved follows it, as before.
- Revolut statements: the PDF of a currency (the "Money out" and "Money in" columns, the currency named in the title,
  the table of reverted operations left out, the fee printed under an operation as a line of its own) and the export
  of the app in English and in French (dates with their time, the fee column, pending, declined and reverted lines
  left out, one account per currency and per product from the one file), and the export of a business account (the
  amount paid, not the one before conversion). Exchanges, top-ups and pockets are transfers.
- Unit tests: the timeline on written cases and 300 invented periods; Revolut PDF and exports read to the cent, saved
  and categorised; amounts in currencies no example uses, exports with columns never seen, date order, fee columns,
  several accounts in one file. `SampleStatements revolut <folder>` writes the fictitious Revolut files.

### Changed
- Reading is not tied to the examples: amounts and amount columns in any currency code (CHF, GBP, SEK... they were
  read only in CAD, USD and EUR); a file whose numeric dates do not say which is the day is read in the order under
  which it covers the shorter time (it was month first unless its headings were French); any export with a fee
  column, a state column, dates with a time or several accounts is read by the same rules as Revolut's.
- A period reads the list of banks once (it was read again for each bank).

### Removed
- Code nothing used: three readers of exchange-rate files kept beside the ones in use, a month-name lookup, a brand
  rule deletion, two colours of the PDF, three window constants, one flag of the interface.

### Fixed
- A column heading of two words was read backwards when its second word sat a little higher ("in Money"), so the
  column was not recognised.

## November 12th, 2026 - Transfers as streams with arrows, a smaller and faster app, any currency's decimals - Commit 33

### Changed
- Transfers over time is drawn as streams of money: each account is a stream as thick as the money transfers put in
  it so far, widening when money comes in and narrowing when it leaves; every transfer is a band as thick as its
  amount that leaves the stream of its account and ends with an arrow in the stream it reaches (thinner on arrival by
  what the banks kept). One scale for every stream and transfer. Days, weeks or months with transfers get the room,
  the others are narrowed. Labels are tied to their arrow by a dashed line. Banks, the outside, totals, net moved and
  hover are kept.
- General and Transfers sit on a line of their own under the Analytics button, and only while Analytics is chosen.
- A smaller app: the .NET libraries are trimmed to what the app and its libraries use (the window, WebView2 and the
  packages are kept whole), and files the app never uses are left out (the debugger's, the PDF library's PDF
  merger). ProductivityApp.exe: 63.9 MB → 35.1 MB.
- Faster changes on large data (41,520 operations, engine benchmark, runs per second before → after): pairing movements between
  accounts 36 → 89, reclassifying everything 23 → 49, changing one operation's category 23 → 40, a whole brand's 11 → 19,
  renaming a brand 12 → 18; importing 13,100 → 15,800 operations per second.
  Database statements are compiled once and kept; pairing movements writes only the operations whose other half
  changed; reclassifying writes an operation once, after the pairing, and only when it ends up different; the
  transfer service named by each wording is looked up once.
- In the real app (`tools/e2e/perf.mjs`, ten years of fictitious operations): reports per second, month 95 → 141, year 12.9 → 14.1, ten
  years 1.8 → 2.0; periods index 119 → 172; All time shown in 0.81 s (0.90 s); scrolling years at 172 fps (159); memory
  of the app at the end 863 MB → 826 MB (its engine 343 MB → 310 MB); start-up unchanged (0.35 s).
- Reading is not tied to a list of examples: amounts with any of the 146 currency codes in use and with any currency
  sign (¥, ₹, ₩, ₽, ₺...); no decimals for the currencies without (yen, won...) and three for the dinars, when their code
  or own sign is beside the amount; a bare "$" is the person's own currency whenever its sign has one (34 currencies,
  it was 7); the name of a bank never decides the kind of account (it was only stripped for one bank); a file named
  "American Express" is a card; any bank of the person holding several currencies is a transfer service when a
  wording names it (it was Wise and Revolut only).

### Added
- Unit tests: the streams on written cases and on the 300 invented periods (never below nothing, changed by what came
  in less what went out, the same money all along less what the banks kept); amounts in more currencies and decimals;
  bank names in file names.
- End-to-end checks: an arrow on every transfer, streams and transfers as thick as their money on one scale, General
  and Transfers under the Analytics button and only there.

### Removed
- The database's two link writes replaced by one that writes a single operation's link.

## November 12th, 2026 - Faster years, all time and custom periods - Commit 34

### Changed
- A period on the screen is drawn as soon as it arrives; the others in the background (periods arriving together held
  back the one on screen: Month was slower since the last change).
- Periods are drawn faster: the lines of the operations and movements tables are plain cells styled once by their
  table (they were rebuilt cell by cell by the component library); the tooltips of a line (category path, wording,
  pencil) are made the moment the mouse first reaches them (a click right after still lands), and the category and tag menus the first time they open. They
  look and behave as before.
- Reports are made faster for long periods: the bars and the month or year parts go through the operations once
  (they went through all of them for every day, month or year); the operations already in order are not sorted again.
- The interface receives less: two fields of every operation it never read are left out (a period of ten years: 12 MB
  → 10 MB).
- The last reports made are kept until the operations, the settings, the rates, the holidays or the day change: going
  back to a period shows it at once (at most 8 reports and 60,000 operations kept).

### Added
- Unit test: a report is kept until something it depends on changes.

## November 12th, 2026 - One export per period, with the views to export chosen - Commit 35

### Changed
- One Export button at the top of each period, in every view (the Export to PDF button at the bottom of the
  statements, analytics and transfers is gone). Its dialog ticks the views to put in the PDF: Summary, Statements,
  Analytics and Transfers, all but the summary at first; nothing ticked, nothing to export.
- The PDF holds the views ticked, in that order: the summary (the balance by month, or by year beyond a year, then in
  all, every bank together); the statements (each bank's totals and tables); the analytics (totals and charts of each
  bank, then of every bank together, the salary per hour and per year, how amounts were converted); the transfers
  (the totals of each kind, the transfers over time drawn exactly as on the screen, on white, then money moved).

### Added
- Unit tests: a PDF holds the views chosen and only those; the drawing of the transfers over time is in it.
- End-to-end checks: one Export at the top of each period in every view, none at the bottom; everything but the
  summary ticked at first; nothing ticked, nothing to export; the PDF written.

## November 12th, 2026 - No freeze when periods come in - Commit 36

### Changed
- The screen keeps moving while periods come in. Frame rate at the worst moment of each action (the longest frame;
  ten years of fictitious operations, a 240 Hz screen, median of 3 runs, commit 32 → now): switching to Year 10 → 48
  fps, to All time 9 → 48 fps, to Custom 18 → 27 fps, to Month 16 → 60 fps; scrolling years 9 → 30 fps, months 16 → 40
  fps. The average stays 180 to 240 fps.
- How: a period is drawn in the background, a little at a time (the one on the screen first, the others when the
  screen has a moment); its banks appear one per frame, and in each bank its tables (or its rows of charts) one per
  frame; the periods of the kind left and those far from the screen are let go in the background too.
- A long period comes in pieces: its latest 2,000 operations of each bank with everything else, then the older ones
  6,000 at a time (ten years were 10 MB read by the browser in one frame). Totals and charts are the whole period's
  from the start; tables and filters hold every operation a moment later.
- The numbers given for the last change were not frame rates (one over the time a view took to show): removed.

### Added
- Unit test: a long period sent in pieces makes the whole list again, in its order.

## November 12th, 2026 - Periods drawn without dropping frames - Commit 37

### Changed
- Switching between Month, Year, All time and Custom, and scrolling the periods, keep the screen's frame rate. Lowest
  frame rate during each action (the longest frame), ten years of fictitious operations, 240 Hz screen, median of 3
  runs, commit 36 → now): switching to Year 48 → 119 fps, to All time 60 → 99 fps, to Custom 27 → 119 fps, to Month
  60 → 120 fps; scrolling years 34 → 119 fps, months 48 → 92 fps (a scroll of about ten screens a second). Scrolled with
  the mouse wheel: months 17 → 120 fps, years 120 → 213 fps. The average is now 235 to 240 fps everywhere (was 185 to
  225). This machine itself drops to about 100 fps now and then with the app doing nothing.
- What a period shows (its boxes of totals, its tables and their lines, the category and tag pills, the icons) is made
  of plain elements given their look by one style sheet made from the theme, instead of styles worked out again for
  every element each time it is drawn. Same look: compared element by element (computed style and place of every
  element of a month and a year, light and dark, statements, analytics, summary, transfers) and pixel by pixel.
- Everything that appears a piece at a time (a period coming in, its banks, each of its tables, five lines of a table
  at a time, a period let go of) waits its turn in one queue for the whole page: pieces are drawn just after a frame is
  shown, the nearest to the screen first, a little each frame.
- Each table and section of a bank is only laid out and painted when near the screen; each is measured once in the
  background, so the page keeps its real length.
- Periods of the kind just left are hidden at once and taken away a little at a time; switching back to them before
  they are gone shows them again at once. The days of a custom period are made ready while the window is idle.
- A period's operations come in pieces small enough to be read between two frames (the latest 300 of each bank, then
  1,500 at a time); a month or a year shows once all its pieces are in, a longer period shows its latest operations at
  once and the older ones join as they come. The filters' lists are made when a filter is opened or in use.
- The table headers stick to the top as one row (they looked the same; the page has fewer layers to draw).

### Added
- End-to-end checks read the text of a table away from the screen as it shows once drawn, and wait for a period to be
  drawn in full before reading it.

## November 12th, 2026 - Scrolling without jumps, readable transfers, faster start - Commit 38

### Fixed
- Scrolling the months (wheel, or dragging the scrollbar) no longer makes the scrollbar jump up and down. A month
  coming in below the screen grew and shrank a dozen times while its banks and tables were drawn (each table first at a
  guessed height); the page's length then changed under the scrollbar. Now:
  - a table or section skipped away from the screen keeps the exact height it had when last laid out; while what it
    shows changes, the browser lays it out with the rest of the page, then it is skipped again at that height;
  - a month being drawn away from the screen keeps its height until all of it is there, then takes its new height at
    once; one drawn on the screen is at least as tall as before meanwhile;
  - months near the screen are drawn once in the background, three at a time, to know their height; the others take
    the height of the drawn month with the nearest number of operations;
  - a scroll the page has not yet been told about is never undone when something above changes height, and switching
    the view keeps the period on screen in place.
  Measured (real wheel, scrollbar dragged), page length changes while scrolling the months once loaded: 26 to 34 in
  3 seconds before, none now (one change of a pixel, rounding).
- A tooltip no longer opens after the mouse has left its element, nor stays.

### Changed
- No pop-up over the category pills, the brands, the accounts and labels of the transfers.
- Transfers over time: each step with transfers is wide enough for them and their labels; a long period scrolls
  sideways, the accounts' names on the left and what each gained or gave on the right staying in sight. More room
  between accounts. Labels beside the arrows show when and how much, never over a stream or another transfer;
  pointing at a transfer shows it in full. A button opens it on the whole screen. The PDF keeps it at the page's width.
- The app starts faster: its code is compiled ahead (trimming had left it to be compiled at every start), and the latest
  three months are made while the window and its browser start. First month on screen, several launches on the same test data:
  about 3.6 s before, about 1 s now. The program is 52.5 MB (was 35.1 MB).

## November 14th, 2026 - Logs of every action - Commit 39

### Added
- Logs, in the left menu under Periods: every entry of the log file, the latest first, a hundred per page, with its
  time (to the millisecond), source (You, App, Engine, Window), action and full details. New entries come in while
  the page is open. Details show their first lines; Show all opens the whole entry (read from the file when it is long).
  Written, with full raw details:
  - the window: every click, double, right and middle click, every key pressed, every changed field, copy, cut and
    paste (with the text), drags, files dropped (name, size, type), scrolls (where each started and ended): the event's
    type and properties (buttons, coordinates, keys, modifiers, trusted or not), the element reached (its path in the
    page, every attribute, its text, value and place on the screen), its control, row, dialog and menu, the page;
    pages opened, messages shown, failures with their stack, errors and unhandled rejections of the page, console
    errors and warnings;
  - the engine: every request of the window as received and the whole answer as sent, when it started; a failed
    request with its full exception (type, message, inner exceptions, call stack); any exception of the app, and every
    exception the engine catches and handles itself (a download that fails, a copy that cannot be read, a picture the
    text recognition refuses, a file read in another encoding) with where it was caught;
  - the window: start (version, program, process, arguments, data folder, system, runtime), database opened, every
    WinForms and WebView2 event (state, bounds, activation, closing, navigations, blocked windows, downloads and
    permissions, browser process failures, missing interface files), exit.
- Clear logs, under the table of the Logs page: asks first, then empties the log file.
- Settings > Data > Log location, under Database location: the path of the log file, Open (shows it in Explorer) and
  Change (moves it to another folder, where it goes on being written; kept at the next start).

### Fixed
- Periods: the page could jump away from what was being looked at (by a screen or more) just after switching to
  General and scrolling, while the other months drew their charts. The element kept in place was the first one
  starting on screen, below what was being read: whatever grew between it and the top of the screen pushed the page.
  Now what crosses the top of the screen is kept in place (the month, then its bank, then its section). Found in the
  new log (the page's own scrolls, 8,347 to 13,469 px). In repeated runs of the end-to-end check the chart-hover step
  failed in about half of the runs before (previous version included), 1 in 6 after: not fully gone yet.
- The Logs page reloads only while it is open; it never told the window about new entries otherwise.

### Changed
- The error log is part of this log; the older error file is turned into entries the first time. One line per entry in
  `app.log`, written on a thread of its own (large answers are written as they are, never copied first): nothing waits
  for the disk. A page of the log reads at most 16 KB of each entry.
- Tests: 7 unit tests of the log file (order, pages, line breaks, a long entry cut on the page and read whole,
  clearing, moving, older log, restart); the end-to-end check reads the raw entries (a click, a whole request and
  answer, a failure's stack, a handled download failure's exception), opens a long entry, presses a key with the page open, clears the logs from under the
  table and moves the file through the interface.

## November 14th, 2026 - Periods fold to their summaries, tables edited in place - Commit 40

### Added
- Periods: every month (year, period) folds down to its summary, and every bank in it. Folded, a period shows the
  summary of every bank together: Earnings, Expenses, Balance and, under the balance, Expenses / earnings (whether
  there is one bank or several). Unfolded: that summary first ("All banks"), then each bank folded to its own summary;
  a bank unfolds to its tables or charts. "Unfold everything" / "Fold everything", in the bar that stays at the top of
  the screen. This replaces the Summary view. Periods start folded.
- Every table of the statements folds from its heading, which says how many operations it holds ("7 operations"); its
  title stays selectable. The table of other spending is named "Non-recurring expenses", with its total. Under every
  table, how many lines it shows with its filters, named ("44 non-recurring expenses", "5 recurring payments",
  "3 income operations", "7 money movements").
- Amount column of every table of operations: ordered by date (as before), smallest amount first or largest first;
  filtered greater than, less than or exactly a value (amounts compared by size: 24.14 for −24.14).
- Tables edited in place as well as with the pencil: dates (a calendar opens), amounts (typed; without a sign the sign
  stays; an amount converted from another currency is typed as its bank wrote it), descriptions of transfers, names
  of categories, tags and column templates (brands in Definitions stay as they were).
- Tags in tables: each tag its own pill with a small cross that takes it off the operation.
- Analytics: Income and expenses per day (per month beyond a month), two bars side by side, income green and
  expenses red, each ticked on or off. Under the Income chart, the net salary (the salary alone) per working day,
  per hour and per year: every day but the weekend is a working day (holidays are paid), hours from Settings.
- Expenses per category: "Selected total" (the categories ticked, added together; changes with the ticks) and "Total"
  (every expense, unticked at first, never added to the selected total); a point per month beyond a month.
- Calendar: an Exchange rate card under the day: two currencies found as they are typed, an arrow turned by a click
  (CAD → EUR: one CAD in EUR; ← one EUR in CAD); the rate of the chosen day, and every day's on the calendar, from the
  central bank chosen in Settings.
- Settings > General: an Analytics card (hours worked per week, 40 at first); a Logs card under Data opens the logs
  (they left the left menu). "Show all data in pie charts" split in two: Expenses per category pie (on at first),
  Expenses per brand pie (off at first).

### Fixed
- Categories: a new subcategory was drawn at the bottom of the list (under the last category, Medical emergencies)
  instead of under its parent, once rows had been dragged: the tree was sorted flat by the saved order. Every level
  keeps its parent now.
- Escape in the amount filter's field now closes its menu.

### Changed
- Earnings, then Expenses, then Balance everywhere (periods, calendar, PDF report).
- Recurring payments, rent excluded: under the recurring payments table. Analytics no longer repeats the totals, nor
  the two ratio boxes.
- The transfers table: its header has the colour of its box; descriptions in full, on several lines.
- Calendar: the month's totals as small boxes in the periods' colours; the day no longer repeats what it spent.
- Logs: the actions (what changes something), pages opened, messages, every failure and error; no longer every click,
  key, scroll, or request that only reads.
- Wider pages (periods and calendar); a thicker, darker scrollbar.
- Bars of a period longer than a month are months (no longer days up to 62 days, years beyond 3 years).
- Brand and category pies: checked with real data that every expense is in both once (a part of Groceries missing
  from the main shop's slice is another shop, a slice of its own). A unit test keeps it so for any data.
- Tests: 275 unit tests (salary per day and hour with holidays paid and the hours set, pair of rates day by day,
  both pies counting every expense once), `npm test` in ui (selected total and total; shown to fail when the selected
  total is counted into the total). The real app checked off-screen on a copy of real data: 40 of 40. The
  end-to-end suite (tools/e2e/run.mjs) was not updated for the folded periods and was not run.

## November 15th, 2026 - Folding in Statements only - Commit 41

### Fixed
- The folding of Commit 40 (months and banks folded to their Earnings, Expenses and Balance, the Fold everything
  button) is for Statements only. Analytics, General and Transfers, show every period and bank whole again, with no
  fold buttons and no summary boxes: the charts of each bank, then of every bank together; the transfers' charts.
- Tests: `npm test` in ui 3 of 3. The real app checked off-screen on a copy of real data: Statements folded,
  Analytics General and Transfers unfolded without summaries, Statements folded again on coming back (4 of 4).

## November 15th, 2026 - A clean log, no dead code - Commit 42

### Fixed
- Calendar: the log recorded "ResizeObserver loop completed with undelivered notifications" as an error each time the
  calendar's width changed (the window resized, the panel beside it changing). The calendar library (FullCalendar,
  same in its latest version) measured its rows and week-day headers again and again while the browser was
  reporting their sizes. The month is now a grid of the app's own, laid out by the browser without measuring, so
  there is nothing left to report. It looks and behaves as before: checked against the previous version pixel by pixel
  in six states (a month, a day picked, a six-week month, a narrow window with its smaller text and one-letter week
  days, today, the other theme): identical. Every amount is still a button (Tab, then Enter or Space picks its day,
  the same blue outline). A six-week month a few pixels taller than the window fits at once, as the old calendar ended
  up doing after showing its scrollbar for a second.
- The log recorded an error at every start: the window asked for its icon (favicon.ico) and got "404 Not Found". The
  app's own icon is served.
- Reading a CSV statement that is not in UTF-8 (Windows-1252, as many French banks export) wrote a "Caught exception"
  warning to the log for every reading of the file: the encoding is now checked instead of tried and failed.
- Exchange rates: a central bank without a base currency added an empty entry to the list of currencies.

### Removed
- Code that did nothing: parameters never read (the account kind given to the classifier, the default currency of
  the operations service, a file name, two coordinates of the flow diagram), a helper left without callers, unneeded
  imports, a misplaced description, the dev-server address in release builds (debug builds only).
- FullCalendar and its temporal-polyfill dependency: the calendar's code went from 277 KB to 14 KB, the interface
  from 1.98 MB to 1.72 MB.

### Changed
- Every project builds without a warning: the WebView2 package's WPF control (never used by the Windows Forms window,
  and the cause of a WindowsBase version conflict) is no longer referenced; images of PDF pages are read with the PDF
  library's current API; a test waits for its result instead of blocking.
- Tests: 275 unit tests, `npm test` in ui 3 of 3. The real app driven off-screen on a copy of real data (every page,
  Month, Year, All time and Custom, Statements and Analytics General and Transfers, folded and unfolded, the calendar
  resized and clicked, 111 fictitious statements of every format read, exchange rates and holidays downloaded,
  Settings, Logs, both themes): 0 warnings, 0 errors in its log. Every sample statement read by the previous and the
  new engine: the same readings, line by line, brands and categories included. End-to-end suite (tools/e2e/run.mjs,
  still not updated for the folded periods): the same 24 of 24 checks on the previous and the new build.

## November 15th, 2026 - The log in three columns; closing the window without an error - Commit 43

### Fixed
- Closing the window while the Logs page was open could write an error to the log: "COMException (0x8007139F): The
  group or resource is not in the correct state" in PostWebMessageAsJson. Writing "Form.FormClosing" (then
  "Form.Deactivate") to the log told the open Logs page of new entries; that message waited in the window's queue and
  was sent after the window had shut its browser control down. The window now stops telling the page of anything once
  it closes, and every message to the page checks that the page is still there when it is sent (not when it is asked
  for). A message that can no longer reach the page (an answer finished as the window closed) is written to the log
  as a debug entry, with the call stack of what asked for it.
- Exchange rates: a source's currency can no longer be missing (its bank's currency is the one); this replaces the
  guard of Commit 42.

### Changed
- The log has three columns: the date to the second, the type (debug, info, warning or error) and the details: who
  wrote it and what ("Engine: holidays.save"), everything about it, then always the full call stack of where it was
  written (the interface's own for what the interface writes; an exception keeps its own as well). The log of an
  earlier version is turned into these three columns when the app opens it (its entries say their call stack was not
  recorded).
- Logs page: Date, Type and Details; "Clear logs" at the top of the page, beside the pages.
- A statement file read as Windows-1252 (not UTF-8) is written to the log as a debug entry, once per file.
- Tests: 279 unit tests (three columns, date to the second, a call stack on every entry, debug entries, a log of five
  columns turned into three, Windows-1252 read as written and written once to the log).

## November 16th, 2026 - The end-to-end suite brought up to date - Commit 44

### Changed
- The end-to-end suite (tools/e2e/run.mjs) follows the app as it is now:
  - Periods start folded in Statements: the suite opens a period with the button of its heading, then each bank, as
    a person does; a new check that a period starts folded to the summary of every bank together. The transfers
    table is folded apart and opened with its own button.
  - The Summary view is gone: its checks became "Folded periods" (a month, a year, All time and Custom folded to
    Earnings, Expenses, Balance and Expenses / earnings; Unfold everything, all banks first, then Fold everything).
  - Analytics shows every period whole, without summaries; the category lines (Selected total, Total, any number of
    categories, from none to all); the salary per working day, per hour and per year; the calendar's header
    (Earnings, Expenses, Balance); Export checked in Analytics.
  - Logs: three columns (the date to the second, the type, the details ending with the call stack), in the file as
    on the page; requests that only read and clicks or keys are not written; an entry of the interface has the
    interface's call stack; a new entry (a console warning) comes in at the top; Clear logs at the top of the page;
    the page opened from Settings > General.
  - Charts drawn again while the view changes, or a period drawn in pieces, are waited for instead of read at once.
- Tests: the suite on a copy of the sample statements, off-screen: 232 of 232 on the debug build and 232 of 232 on a
  published release build (`-p:E2E=true`). No change to the app.

## November 16th, 2026 - The interface comes back when its browser ends; CIC statements; the month kept on view switches - Commit 45

### Fixed
- The COMException 0x8007139F on closing (fixed in Commit 43) was reproduced in a visible window with real mouse and
  keys, on a copy of real data, with the version before Commit 43 (10 of 20 closes, every one with a click on the
  window's X) and checked with this one: 0 of 24 closes in 12 ways (X clicked, X then another window in front, Alt+F4,
  the system's close, minimised, maximised, while writing to the log, while an answer was on its way, ...).
- The same error had another cause, not covered by Commit 43: the browser of the window ending (a crash, or ended from
  outside). Every message to the page then failed with that COMException; each error, written to the log, told the
  page of a new entry, which failed again: about 23,500 errors in 6 seconds, and the window stayed blank. Now the
  bridge to the page knows when its page is gone (the window closed, its browser ended, a message refused): nothing
  more is sent to it (a debug entry says what was not sent and why) and the log stops telling it. The window then
  starts a new browser and loads the interface again ("Browser started again"); the page's own process ending loads
  the page again ("Page loaded again"). Three times within five minutes at most: beyond, the window says the interface
  stopped and where the log is. That message was dark grey on the dark theme and the window had no title bar to close
  it with: it is readable in both themes and the window gets Windows' own title bar.
- Periods: switching the view far down the page (Statements, Analytics General and Transfers) could land on another
  month, up to 2,647 px away (2 switches of 4 in the check): the new view is first drawn at guessed heights, so the page
  was for a moment shorter than the place being read; the browser pulled it up, and that pull was taken for a scroll
  of the person, never undone. A pull of the browser is no longer the person's, and a correction the page is still too
  short for is made once it is long enough again: the month stays in place (0 to 1 px).
- Reading statements, for any bank:
  - a CSV or table with as many lines of title (one cell each) as lines of operations is read: its separator and width
    are those of its lines of cells (a month of one operation was read as nothing);
  - a balance line under or above the table is found in any column ("Solde au 31/10/2025" under the label column), a
    dated balance under the operations is the closing one; printed balances are checked against the running balance
    (a month of one operation is proven by them);
  - a spreadsheet (or document) with a table per account, each titled with its own account number, gives one statement
    per account (sheets of the same account still make one);
  - account titles "C/C ..." and names ending in COMPTE ("EUROCOMPTE"), opening balances "SOLDE CREDITEUR AU" and
    "SOLDE DEBITEUR AU" in statements of several accounts.

### Added
- CIC statements (a French bank, on the online banking of the Crédit Mutuel): the CSV and Excel exports in the layouts
  public projects reading them describe, the PDF from the bank's own description of its statement (no real CIC
  statement was available to compare with): the monthly PDF "Relevé et informations bancaires" with every account (current
  account and Livret A, "Date | Date valeur | Opération | Débit EUROS | Crédit EUROS", balances "SOLDE CREDITEUR AU",
  "Total des mouvements", card payments on three lines), the CSV export (ISO-8859-15, debits negative), the Excel
  export (a sheet of the accounts, then a sheet per account), OFX and QIF. Fictitious statements: `SampleStatements cic
  <folder>`. The CSV and QIF exports name no currency: they are read in the global currency until the account's is
  chosen (the PDF says EUROS).

### Changed
- Tests: 288 unit tests (CIC in every format, a short table under lines of title, a printed balance that disagrees is a
  mismatch, sheets of one account stay one statement), `npm test` in ui 3 of 3. Every sample statement of every bank
  read by the previous and this version: the same readings, but CIC's (now balanced, both accounts). The CIC files
  imported through the Import page of the real app on an empty database: 9 of 9 checks. The scroll checked with the
  real mouse wheel in a visible window on a copy of real data: no jump in 18 phases (folded and unfolded months, Analytics, years, at a person's pace and fast, the scrollbar dragged), the month kept on all 4 view switches (2 of 4 before). End-to-end suite: 232 of 232 on the debug build and on a published release build.

## November 16th, 2026 - Each bank's date format; tests split by purpose; CIC read in euros against the ECB's rates - Commit 46

### Added
- Each bank has a date format, set in Definitions > Bank (a column of the banks table, and in the bank's dialog):
  DD/MM/YYYY, MM/DD/YYYY or YYYY-MM-DD. A new bank starts with the one of its country and banking system (DD/MM/YYYY
  for a bank on the French system or in France, YYYY-MM-DD in Canada, MM/DD/YYYY in the United States, DD/MM/YYYY
  elsewhere); the banks already there get theirs the same way. A file is read once its bank is known (the bank of the
  account chosen, or the one chosen for a new account): its operations, balance check and status are shown then, read with that bank's banking
  system and date format, and the Import page shows the format beside the bank, to change for that file. Dates written
  in another format are said ("The dates of this file are written DD/MM/YYYY, not YYYY-MM-DD as set for its bank")
  and the file cannot be imported until the right format is chosen; the format chosen becomes the bank's.
- A workbook says the currency of its amounts in their number format ("#,##0.00 [$€-40C]"), an OpenDocument sheet in
  its money cells: the statement is in that currency (the CIC's Excel export, Livret A sheet included, is in euros). A
  CSV whose money columns name their currency ("Amount (CHF)", "-45.10 CHF", "12,34 €") is in it.

### Fixed
- A statement of several accounts (CIC, Caisse d'Epargne) read again when its account was chosen lost each account's
  number and the savings type of the Livret A.

### Removed
- The guess of a statement's language from its words, used only to order day and month when its dates could not
  tell: the bank's date format does it.
- A record of which way of pairing transfers between currencies was used last, kept for one test only.

### Changed
- Tests, one project per kind: `tests/Finance.UnitTests` (the engine's code alone: 2,604 tests), `tests/Finance.
  IntegrationTests` (with a real database, files, PDFs and a stand-in for the Internet: 411), `ui/tests` (3), the
  end-to-end suite of the real app (`tests/Finance.EndToEndTests`, 236 checks: nothing is read before a file's bank
  is chosen, a file is read with its bank's date format, every bank shows its format), the timings
  (`tests/Finance.PerformanceTests`, run on their own) and the driver of the app both use (`tests/Finance.AppDriver`);
  the fictitious statements, rates and calendars the .NET tests share in `tests/Finance.TestData`. Each test is named after what it calls, the case and what must happen, does one thing and checks it;
  tests that only touched code or compared a PDF's size were replaced by checks of what it says. The CIC: every form
  of its statements read in euros by a person whose currency is the Canadian dollar, each operation converted at the
  European Central Bank's rate of its day (Friday's on a weekend), the Livret A transfer one movement inside the bank.
  `tools/test.ps1` runs them all.

## November 18th, 2026 - macOS and Linux; release files for every system - Commit 47

### Added
- macOS and Linux. The engine is built twice: for Windows as before (its own PDF drawing and text recognition), and for
  every other system (`net10.0`): pages drawn by PDFium, pictures read by Skia (TIFF by LibTiff), scanned pages and
  pictures read by the text recognition built into macOS (Vision, called through the Objective-C runtime) or by
  Tesseract on Linux (`src/Finance.Core/Pdf/PdfReader.Portable.cs`, `TextRecognizer.cs`). French recognition is
  preferred, English with it; a scanned page with no recognition on the machine says what to install.
- The macOS and Linux window (`src/Finance.App/Photino`): a Photino window with the system's web view (WKWebView,
  WebKitGTK) and the system's own title bar and edges (the interface draws its title bar and resize edges only on
  Windows: `nativeFrame` in `app.init`). The interface is served from the program under `app://localhost/` with the
  same content policy as on Windows; each of its messages starts with a key written into the page when it is served,
  so no other page can talk to the engine. Nothing is sent to the page before it first speaks (the web view cannot take
  it). One window per data folder through a local socket in that folder (a second launch brings the first forward).
  The window comes back where it was, when that place is still on a screen. The file browser shows Home, the whole disk
  and the disks plugged in (/Volumes on macOS; /media, /run/media and /mnt on Linux), not the system's own mounts;
  "Show in folder" opens Finder with the file selected, or the file's folder on Linux.
- `tools/Package`: the release files in `release/` - `ProductivityApp-<version>-windows-x64.exe`; a
  `ProductivityApp.app` per Mac processor (Apple, Intel), zipped with its permissions, its icon and Info.plist (macOS 14
  or later: the oldest the window library runs on; every program and library of the Apple build carries its signature,
  which Apple processors need); a `.deb` per Linux processor (x64, ARM) that brings WebKitGTK, Tesseract with its English
  and French data, ICU and OpenSSL, with a menu entry and an icon, and the same files as a `.tar.gz` for other
  distributions.

### Changed
- The bridge no longer knows WebView2: it sends through `IPage` and asks the window through `IAppWindow`, so both windows
  share it and the whole API.
- The tests run on both targets of the engine (Windows' and the portable one): 2,604 unit and 411 integration tests
  each; the two scanned-statement tests skip where the machine has no text recognition. The end-to-end suite also drives
  the Photino window (built for Windows, served there under `http://app.localhost/` as its WebView2 cannot open a
  scheme of its own), and skips its picture check where there is no text recognition.
- The release builds of macOS and Linux leave out the debugger's files and the PDF merger, as the Windows one does.
- Source paths written into the programs (the PDF library records the file names of its callers) start at the
  repository: no folder of the machine that built them is in any release file.
- The macOS and Linux window writes its own events to the log (created, page loaded, maximised, restored, closing); an
  error inside the web view's calls is written to the log instead of ending the program.
- README: how to install on Windows, macOS and Linux.

## November 18th, 2026 - The release files in the repository - Commit 48

### Added
- The release files of version 0.1.0 in `release/` (no longer ignored), each linked from the README.

## November 19th, 2026 - Large imports without freezing; card payments counted where no card covers them; macOS app signed as a bundle - Commit 49

### Fixed
- The import card froze on large files (a 20,000-line export left the page frozen for more than 20 minutes, its memory
  at 6.9 GB): every line of the file was drawn at once, and drawn again at each key typed in the account's name or bank
  name. Only the lines in view (and a few around) are drawn now, each line again only when it changes itself: typing the
  account's name runs at 239 fps (lowest 119 fps) on that file. Finding the lines already imported looks the stored
  operations up by amount instead of going through all of them for each line.
- Card payments counted nowhere: paying a card was "Card statement spending" only while no credit card account existed
  at all. Once one card was imported, every payment became a movement to a card, also the payments of the years before
  that card's statements (an older card, a card replaced): the purchases they paid were in no file, so they were counted
  nowhere and the balance of All time was far too high. A card payment is now "Card statement spending" on every day no
  imported card covers (its statements' periods, or its first to its last operation), and a card payment paired with the
  card's own line on the days a card covers (`TransferMatcher`). Importing an older card's statements turns the payments
  of their days back into movements, and their purchases count instead.
- macOS: "The application "ProductivityApp" can't be opened". The .NET build signs the program on its own
  (identifier "apphost"), and inside an .app macOS rejects that signature: "code has no resources but signature
  indicates they must be present" (codesign on macOS 14.6). `tools/Package` now signs each .app as a bundle, ad hoc as
  codesign does (`tools/Package/MacSign.cs`): every file of Contents/ sealed in _CodeSignature/CodeResources, the program
  signed again with the bundle's identifier, binding Info.plist and that seal; `codesign --verify --deep --strict` on
  macOS says both zips (Apple processor, Intel) are valid and satisfy their requirement. The zip entries are also marked
  made by Unix (they said MS-DOS, and unzip tools other than Finder's then drop the permissions).
- `tools/Package` checks every macOS zip before finishing (`tools/Package/MacCheck.cs`): entries made by Unix, the program
  executable and named by Info.plist, the seal binding this Info.plist and these files, every Apple-processor program and
  library signed with page hashes matching its code.

## November 19th, 2026 - The file browser on macOS and Linux - Commit 50

### Fixed
- The file browser's path bar cut paths at backslashes only: on macOS and Linux ("/Users/alex/Downloads") it showed
  one crumb ending in a backslash, and clicking it asked for a folder that does not exist. Paths are now cut at
  slashes from the root "/" on macOS and Linux, at backslashes from the drive on Windows (`ui/src/paths.ts`).
- The import queue named a file by its whole path on macOS and Linux: it shows the file's name on every system.
- The places of the file browser: only the closest place holding the folder shown is highlighted (Desktop, not also
  the disk it is on; on macOS "Computer (/)" was highlighted everywhere).

### Added
- `ui/tests/paths.test.ts`: paths of Windows, macOS and Linux cut, named and compared.

### Changed
- The end-to-end suite on a machine without text recognition (the macOS and Linux window tested on Windows): the
  scanned statement must say what to install, every other file is read; it stopped at the first import before. Both
  windows pass all 236 checks.
