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
