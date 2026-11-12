import { invoke } from './bridge'

export type CategoryKind = 'expense' | 'income' | 'transfer'
export type Granularity = 'month' | 'year' | 'range'
export type ColumnRole = 'ignore' | 'date' | 'valueDate' | 'description' | 'debit' | 'credit' | 'amount' | 'balance' | 'category'
export type DateOrder = 'unknown' | 'dayMonth' | 'monthDay'
export type KindHint = 'auto' | 'bank' | 'creditCard'
export type AccountKind = 'bank' | 'creditCard' | 'savings'
export type Verification = 'verified' | 'mismatch' | 'noBalances' | 'noTransactions'

/** parentId: the category it is nested in (null at the top); color: '#rrggbb' or null; builtIn: comes with the app (cannot be deleted). */
export interface Category { id: string; name: string; kind: CategoryKind; icon: string; parentId: string | null; color: string | null; builtIn: boolean }

export type Settings = Record<'currency' | 'dateFormat' | 'theme' | 'reportsFolder' | 'periodTransfers' | 'calendarHideCardPayments' | 'calendarHideTransfers' | 'calendarHideInvestments' | 'pieShowAll' | 'chartsTopLevel', string> & Record<string, string>

/** A tag the person made; operations counts the operations that carry it themselves (not through a category). */
export interface Tag { id: number; name: string; color: string | null; operations: number }
/** Every tag, and the tags each category gives to its operations (and to those of the categories nested in it). */
export interface TagData { tags: Tag[]; categoryTags: Record<string, number[]> }

export type BankingSystem = 'northAmerica' | 'france'
export interface Bank { id: number; name: string; country: string; system: BankingSystem; currency: string }

export const currencies = ['CAD', 'USD', 'EUR', 'GBP', 'CHF', 'AUD', 'JPY', 'MXN']

/** Account types, named the way the bank's banking system names them. */
export function accountKinds(system: string): { value: AccountKind; label: string }[] {
  return system === 'france'
    ? [{ value: 'bank', label: 'Current account (debit card)' }, { value: 'savings', label: 'Savings (livret)' }, { value: 'creditCard', label: 'Deferred debit card' }]
    : [{ value: 'bank', label: 'Chequing account' }, { value: 'savings', label: 'Savings account' }, { value: 'creditCard', label: 'Credit card' }]
}

export interface InitInfo {
  version: string
  settings: Settings
  categories: Category[]
  tags: TagData
  dateFormats: string[]
  ocrAvailable: boolean
  nonClientRegions: boolean
  maximized: boolean
  dataFolder: string
}

export interface Slice { key: string; label: string; amount: number; share: number }
export interface Bar { key: string; label: string; expenses: number; earnings: number; moved: number; cardPaid: number; invested: number }

export interface TransactionView {
  id: number
  date: string
  amount: number
  currency: string
  brand: string
  categoryId: string
  categorySource: string
  brandSource: string
  rawLabel: string
  note: string
  accountId: number
  /** Missing (like the fields below that can be empty) when it has none. */
  statementId?: number | null
  /** How often it comes back ("monthly", "twice a month", "yearly"); null when it is not recurring. */
  recurring?: string | null
  linkedId?: number | null
  /** For money back from an investment: the part entered as earned (gain), counted as income. */
  earned?: number | null
  /** Tags put on the operation itself. */
  ownTags: number[]
  /** Every tag it carries: its own and its category's. */
  tags: number[]
  /** Converted operations (all banks together): the amount as the bank wrote it, its currency, the rate and the day of the rate. */
  original?: number | null
  originalCurrency?: string | null
  rate?: number | null
  rateDay?: string | null
}

/** One part of a period (a month, or a year for ranges longer than a year) and its balance. */
export interface PeriodBalance { from: string; to: string; title: string; expenses: number; earnings: number; balance: number }

/** One movement of money, once, from an account (or the outside) to another (or the outside). */
export interface MovementView {
  date: string
  categoryId: string
  label: string
  amount: number
  from: string
  to: string
  fromOutside: boolean
  toOutside: boolean
  editId: number
  otherId: number | null
  /** Type of an own account ("Chequing account"); null for the outside. */
  fromType: string | null
  toType: string | null
  /** The bank of an own account; null for the outside. */
  fromBank: string | null
  toBank: string | null
  /** An international transfer: what was sent and received, each in its own currency, and what the banks kept (in the period's currency). */
  sent?: number
  sentCurrency?: string
  received?: number
  receivedCurrency?: string
  lost?: number
}

export interface OutsideParty { name: string; sent: number; received: number }
/** An account box (centre x, y; size w, h) or the outside blob (an ellipse) with who is behind it. */
/** title: the account's full name (inside its bank it is named without the bank's name it starts with). */
export interface DiagramNode { id: string; name: string; type: string; outside: boolean; x: number; y: number; w: number; h: number; parties: OutsideParty[]; title?: string | null }
/** lost: what the banks kept on the international transfers of the arrow (their rate and fees). */
export interface DiagramArrow { from: string; to: string; amount: number; count: number; outside: boolean; path: string; head: string; width: number; labelX: number; labelY: number; lost?: number | null }
/** A bank: a larger box holding its accounts, its name at the top (centre x, y; size w, h). */
export interface DiagramBank { name: string; x: number; y: number; w: number; h: number }
export interface FlowDiagram { width: number; height: number; nodes: DiagramNode[]; arrows: DiagramArrow[]; banks: DiagramBank[] }

/** A line of the transfers timeline: an account (title: its full name when shortened inside its bank) or the outside, with what came in and went out. */
export interface TimelineLane { id: string; name: string; type: string; bank: string | null; outside: boolean; in: number; out: number; net: number; title?: string | null
  /** The money transfers put in the line: at the start of the period, then after each step (one per slot). */
  start: number; levels: number[] }
/** A bank of the timeline: the lines of its accounts, from first. */
export interface TimelineBank { name: string; first: number; count: number }
/** Money moved from one line to another in one step of time; kind: inside a bank, between two banks, to or from the outside. */
export interface TimelineEvent {
  slot: number; date: string; from: number; to: number; amount: number; count: number; kind: 'inside' | 'between' | 'outside'
  lost?: number; sent?: number; sentCurrency?: string; received?: number; receivedCurrency?: string
}
/** The transfers of a period over time: steps of a day, a week, a month or a year (slots: the first day of each). */
export interface TransferTimeline {
  unit: 'day' | 'week' | 'month' | 'year'; slots: string[]; lanes: TimelineLane[]; banks: TimelineBank[]; events: TimelineEvent[]
  count: number; inside: number; between: number; sentOutside: number; receivedOutside: number; lost: number | null
}

export interface Flow { from: string; to: string; amount: number; fromOutside: boolean; toOutside: boolean; count: number }


export interface PeriodSummary {
  from: string
  to: string
  granularity: Granularity
  title: string
  currency: string
  expenses: number
  earnings: number
  balance: number
  transfersOut: number
  transfersIn: number
  transactions: TransactionView[]
  byCategory: Slice[]
  categoryPie: Slice[]
  /** Expenses per first-level category (nested categories counted in theirs). */
  byTopCategory: Slice[]
  byBrand: Slice[]
  brandPie: Slice[]
  /** Expenses per set of tags: each tagged expense in one slice ("Travel + Work"), never twice. */
  tagPie: Slice[]
  /** The period in months (years for ranges longer than a year), each with its balance. */
  breakdown: PeriodBalance[]
  series: Bar[]
  seriesUnit: 'day' | 'month' | 'year'
  recurringExpenses: number
  recurringExpensePie: Slice[]
  /** Every income by source. */
  incomePie: Slice[]
  movements: MovementView[]
  flows: Flow[]
  diagram: FlowDiagram
  timeline: TransferTimeline
  /** Recurring payments without housing (rent, mortgage). */
  recurringWithoutHousing: number
  /** Shares of earnings; null without earnings. */
  recurringWithoutHousingShare: number | null
  expensesShare: number | null
}

export interface MonthEntry { year: number; month: number; count: number }
/** One bank's part of a period, in the global display currency; native: the currency of its accounts ("other": cash and manual entries). */
export interface BankPart { key: string; name: string; currency: string; native: string; system: BankingSystem | null; summary: PeriodSummary }
export interface RateUse { day: string; rateDay: string; rate: number }
/** How the operations of one bank in one currency were converted to another. */
export interface ConversionLine {
  group: string; from: string; to: string; operations: number
  expenses: number; earnings: number; convertedExpenses: number; convertedEarnings: number
  lowestRate: number | null; highestRate: number | null; effectiveRate: number | null
  rates: RateUse[]; unconverted: number; unconvertedAmount: number; oldRates: number
}
export interface Conversion { to: string; source: string; base: string; url: string; updatedAt: string | null; latest: string | null; lines: ConversionLine[] }
/**
 * A period bank by bank, then every bank together in the global display currency. The operations are in the banks'
 * parts only (global holds the totals and charts): globalTransactions() gives them all in the global currency.
 */
export interface PeriodReport {
  from: string; to: string; title: string; granularity: Granularity; currency: string; banks: BankPart[]; global: PeriodSummary; conversion: Conversion
  /** Operations left out of every total (no rate known for their day): the banks' parts list them in their own currency. */
  unconverted: number[]
  /** The days that are not weekend days, less the holidays of the checked calendars. */
  businessDays: number | null
  salary: SalaryRate | null
}

/** Every operation of every bank in the global currency (converted ones keep the amount the bank wrote, and the rate). */
export function globalTransactions(r: PeriodReport): TransactionView[] {
  return r.banks.flatMap((b) => b.summary.transactions)
    .filter((t) => t.currency === r.currency)
    .sort((a, b) => a.date.localeCompare(b.date) || a.id - b.id)
}
export interface RatesStatus {
  sourceId: string; source: string; base: string; url: string; updatedAt: string | null; latest: string | null; first: string | null
  currencies: number; error: string | null
  /** The latest download could not be read: the copy before it is used. */
  fromPrevious: boolean
}
/**
 * A central bank the daily exchange rates are downloaded from (Definitions > Exchange rates): which bank (format), the
 * address of its file of rates, what its copy in use holds (number of currencies and last day: none without a copy)
 * and why its last download failed.
 */
export interface RateSource {
  id: string; name: string; format: string; url: string; base: string | null; latest: string | null; currencies: number
  updatedAt: string | null; error: string | null; inUse: boolean
}
/** A central bank whose file the app reads: its file's layout (format), its currency and the address to start with. */
export interface CentralBank { format: string; name: string; currency: string; defaultUrl: string }
/**
 * A calendar of public holidays (Definitions > Holidays): its place (free text), the address of its file ("{year}"
 * stands for each year), whether it is checked, the country's calendar it sits under (parent: a province or a state),
 * and the number of holidays of this year (year) in its copy in use (none without a copy); with why its last download
 * failed.
 */
export interface HolidayCalendar {
  id: string; location: string; url: string; active: boolean; parent: string | null; perYear: number | null; year: number; updatedAt: string | null; error: string | null
}
/** A calendar every database starts with (offered again for a new line). */
export interface HolidayPreset { id: string; location: string; url: string; active: boolean; parent: string | null }
/** A holiday of a checked calendar, with the place of its calendar; observed: the day off for one on a weekend. */
export interface Holiday { day: string; name: string; location: string; observed: boolean }
/**
 * The net salary of a period per hour worked and per year, in the display currency: the salary received over the
 * business days of hoursPerDay hours; per year, the same daily pay over the business days of a year (yearBusinessDays).
 */
export interface SalaryRate { net: number; businessDays: number; hoursPerDay: number; perHour: number; yearBusinessDays: number; perYear: number }

export interface PeriodIndex { months: MonthEntry[]; years: number[]; currencies: string[]; first: string | null; last: string | null }

export interface Account { id: number; name: string; currency: string; kind: AccountKind; bank: string | null; last4: string | null; bankId: number | null; system: BankingSystem }

export interface TemplateColumn { role: ColumnRole; left: number; right: number }
export interface BankTemplate { id: number; name: string; matchText: string; columns: TemplateColumn[]; dateOrder: DateOrder; kind: KindHint; updatedAt?: string }

export interface DraftRow {
  index: number
  date: string
  label: string
  amount: number
  balance: number | null
  brand: string
  categoryId: string
  brandSource: string
  categorySource: string
  duplicate: boolean
  page: number
  top: number
}

export interface Draft {
  id: string
  /** The other accounts of the same file, each its own draft. */
  others?: Draft[]
  fileName: string
  alreadyImported: boolean
  status: Verification
  difference: number | null
  computedClosing: number | null
  opening: number | null
  closing: number | null
  periodStart: string | null
  periodEnd: string | null
  currency: string
  kind: AccountKind | null
  bank: string | null
  last4: string | null
  method: string
  usedOcr: boolean
  profile: string | null
  newestFirst: boolean
  zeroRowsSkipped: number
  language: string
  dateOrder: DateOrder
  rowChecks: number
  rowMismatches: number
  /** Account and type chosen by the person; null until they choose. */
  accountId: number | null
  defaultAccountName: string
  /** The bank of the list the statement names, if any. */
  suggestedBankId: number | null
  template: BankTemplate | null
  columns: TemplateColumn[]
  pageCount: number
  suggestedMatch: string
  rows: DraftRow[]
}

/** allOfBrand false: the new category is for this line only. */
export interface DraftEdit { index: number; brand: string | null; categoryId: string | null; include: boolean; allOfBrand?: boolean }
export interface CommitResult { statementId: number; accountId: number; added: number; skippedDuplicates: number; firstDate: string | null; lastDate: string | null }

export interface StatementInfo {
  id: number
  bankId: number | null
  bank: string | null
  accountId: number
  account: string
  fileName: string
  periodStart: string | null
  periodEnd: string | null
  openingBalance: number | null
  closingBalance: number | null
  verified: boolean
  method: string
  importedAt: string
  transactionCount: number
  currency: string
}

export interface PageImage { png: string; widthPt: number; heightPt: number; pageCount: number; fromOcr: boolean }

export interface Place { name: string; path: string; icon: string }
export interface FsEntry { name: string; path: string; isFolder: boolean; size: number; modified: string }
export interface FolderListing { path: string; parent: string | null; entries: FsEntry[] }

export interface BrandInfo { brand: string; count: number; spent: number; received: number; categoryId: string; learned: boolean; currency: string; lastDate: string; wordings: string[] }
export interface BrandRule { id: number; merchantKey: string; brand: string; categoryId: string | null; updatedAt: string }

export interface TransactionInput { date: string; amount: number; brand: string; categoryId: string; accountId: number | null; note: string | null; earned?: number | null }

export const api = {
  init: () => invoke<InitInfo>('app.init'),
  ready: () => invoke<null>('app.ready'),
  window: {
    minimize: () => invoke<null>('window.minimize'),
    toggleMaximize: () => invoke<{ maximized: boolean }>('window.toggleMaximize'),
    close: () => invoke<null>('window.close'),
    drag: () => invoke<null>('window.drag'),
    resize: (edge: string) => invoke<null>('window.resize', { edge }),
  },
  setSetting: (key: string, value: string) => invoke<Settings>('settings.set', { key, value }),
  periods: {
    index: (currency?: string) => invoke<PeriodIndex>('periods.index', { currency }),
    summary: (from: string, to: string, granularity: Granularity, currency?: string) =>
      invoke<PeriodSummary>('periods.summary', { from, to, granularity, currency }),
    /** Every bank on its own, then all of them in the global display currency. */
    report: (from: string, to: string, granularity: Granularity) => invoke<PeriodReport>('periods.report', { from, to, granularity }),
  },
  rates: {
    sources: () => invoke<RateSource[]>('rates.sources'),
    status: () => invoke<RatesStatus>('rates.status'),
    /** Downloads a source's rates again (the chosen one by default), which checks its address too. */
    update: (id?: string) => invoke<RatesStatus>('rates.update', { id }),
    /** The central banks the app reads. */
    banks: () => invoke<CentralBank[]>('rates.banks'),
    /** A new bank of the table (format), or a new address for one (id). */
    saveSource: (source: { id?: string; format?: string; url: string }) => invoke<{ id: string; sources: RateSource[] }>('rates.saveSource', source),
    removeSource: (id: string) => invoke<RateSource[]>('rates.removeSource', { id }),
    /** Downloads every bank of the table again now; updated: the rates in use changed. */
    refresh: () => invoke<{ updated: boolean; sources: RateSource[] }>('rates.refresh'),
    /** Downloads every bank without today's copy; says whether the rates in use changed. */
    ensure: () => invoke<{ updated: boolean; status: RatesStatus }>('rates.ensure'),
  },
  holidays: {
    calendars: () => invoke<HolidayCalendar[]>('holidays.calendars'),
    presets: () => invoke<HolidayPreset[]>('holidays.presets'),
    /** A new calendar (no id; under a country's calendar: parent), or a new place or address for one. */
    save: (c: { id?: string; location: string; url: string; parent?: string | null }) => invoke<{ id: string; calendars: HolidayCalendar[] }>('holidays.save', c),
    setActive: (id: string, active: boolean) => invoke<HolidayCalendar[]>('holidays.setActive', { id, active }),
    /** The holidays of a year (this one by default) in the checked calendars together, each day once. */
    total: (year?: number) => invoke<{ year: number; total: number }>('holidays.total', { year }),
    remove: (id: string) => invoke<HolidayCalendar[]>('holidays.remove', { id }),
    /** Downloads a calendar again, which checks its address too. */
    update: (id: string) => invoke<HolidayCalendar>('holidays.update', { id }),
    /** Downloads every calendar again now; updated: a checked one changed. */
    refresh: () => invoke<{ updated: boolean; calendars: HolidayCalendar[] }>('holidays.refresh'),
    /** Downloads every calendar without today's copy; says whether a checked one changed. */
    ensure: () => invoke<{ updated: boolean }>('holidays.ensure'),
    /** The holidays of the checked calendars over a period. */
    list: (from: string, to: string) => invoke<Holiday[]>('holidays.list', { from, to }),
  },
  tx: {
    setCategory: (id: number, categoryId: string, allOfBrand: boolean) => invoke<{ changed: number }>('tx.setCategory', { id, categoryId, allOfBrand }),
    setBrand: (id: number, brand: string) => invoke<{ changed: number }>('tx.setBrand', { id, brand }),
    /** The operation as its bank wrote it (not converted). */
    get: (id: number) => invoke<TransactionView>('tx.get', { id }),
    create: (input: TransactionInput) => invoke<TransactionView>('tx.create', { input }),
    /** How many operations of that day are exactly this one (same amount, brand and account). */
    sameOnDay: (date: string, amount: number, brand: string, accountId: number | null, except: number | null) =>
      invoke<{ count: number }>('tx.sameOnDay', { date, amount, brand, accountId, except }),
    update: (id: number, input: TransactionInput, allOfBrand = true) => invoke<TransactionView>('tx.update', { id, input, allOfBrand }),
    remove: (id: number) => invoke<null>('tx.delete', { id }),
  },
  brands: {
    list: () => invoke<{ brands: BrandInfo[]; rules: BrandRule[]; builtInCount: number }>('brands.list'),
    setCategory: (brand: string, categoryId: string) => invoke<{ changed: number }>('brands.setCategory', { brand, categoryId }),
    /** A brand renamed everywhere: its operations and what was learned for it. */
    rename: (from: string, to: string) => invoke<{ changed: number }>('brands.rename', { from, to }),
    forget: (brand: string) => invoke<{ changed: number }>('brands.forget', { brand }),
  },
  categories: {
    create: (c: { name: string; parentId: string | null; kind: CategoryKind; color: string | null }) => invoke<Category[]>('categories.create', c),
    update: (c: { id: string; name: string; parentId: string | null; color: string | null }) => invoke<Category[]>('categories.update', c),
    remove: (id: string) => invoke<Category[]>('categories.delete', { id }),
  },
  tags: {
    list: () => invoke<TagData>('tags.list'),
    create: (t: { name: string; color: string | null }) => invoke<TagData>('tags.create', t),
    update: (t: { id: number; name: string; color: string | null }) => invoke<TagData>('tags.update', t),
    remove: (id: number) => invoke<TagData>('tags.delete', { id }),
    setForOperation: (id: number, tagIds: number[]) => invoke<TagData>('tags.setForOperation', { id, tagIds }),
    setForCategory: (categoryId: string, tagIds: number[]) => invoke<TagData>('tags.setForCategory', { categoryId, tagIds }),
  },
  banks: {
    list: () => invoke<Bank[]>('banks.list'),
    save: (bank: Bank) => invoke<Bank[]>('banks.save', { bank }),
    remove: (id: number) => invoke<Bank[]>('banks.delete', { id }),
  },
  accounts: {
    list: () => invoke<Account[]>('accounts.list'),
    add: (a: { bankId: number; name: string; kind: AccountKind; currency: string }) => invoke<Account>('accounts.add', a),
    /** bankId 0: no bank. */
    update: (a: Account, change: { name?: string; kind?: AccountKind; currency?: string; bankId?: number }) =>
      invoke<Account>('accounts.update', { id: a.id, name: a.name, ...change }),
    remove: (id: number) => invoke<null>('accounts.delete', { id }),
  },
  statements: {
    list: () => invoke<StatementInfo[]>('statements.list'),
    remove: (id: number) => invoke<null>('statements.delete', { id }),
  },
  imports: {
    /** A file of several accounts (a French statement of every account) gives one draft per account: the others in "others". */
    parseFile: (path: string) => invoke<Draft>('import.parseFile', { path }),
    parseBytes: (name: string, base64: string) => invoke<Draft>('import.parseBytes', { name, base64 }),
    choose: (draftId: string, accountId: number | null, kind: AccountKind | null, bankId: number | null) => invoke<Draft>('import.choose', { draftId, accountId, kind, bankId }),
    commit: (p: { draftId: string; accountId: number | null; newAccountName: string | null; currency: string; kind: AccountKind | null; edits: DraftEdit[]; bankId: number | null; newBankName: string | null }) =>
      invoke<CommitResult>('import.commit', p),
    discard: (draftId: string) => invoke<null>('import.discard', { draftId }),
    page: (draftId: string, page: number, width: number) => invoke<PageImage>('import.page', { draftId, page, width }),
    preview: (draftId: string, template: BankTemplate | null) => invoke<Draft>('import.preview', { draftId, template }),
  },
  templates: {
    list: () => invoke<BankTemplate[]>('templates.list'),
    save: (template: BankTemplate) => invoke<BankTemplate>('templates.save', { template }),
    remove: (id: number) => invoke<null>('templates.delete', { id }),
  },
  fs: {
    places: () => invoke<Place[]>('fs.places'),
    list: (path: string, foldersOnly = false) => invoke<FolderListing>('fs.list', { path, foldersOnly }),
  },
  shell: {
    open: (path: string) => invoke<null>('shell.open', { path }),
    reveal: (path: string) => invoke<null>('shell.reveal', { path }),
  },
  report: {
    defaults: (from: string, to: string, granularity: Granularity, currency: string) =>
      invoke<{ folder: string; fileName: string }>('report.defaults', { from, to, granularity, currency }),
    export: (p: { from: string; to: string; granularity: Granularity; currency: string; folder: string; fileName: string }) =>
      invoke<{ path: string }>('report.export', p),
  },
  eraseAll: () => invoke<null>('data.eraseAll'),
  moveDatabase: (folder: string) => invoke<{ dataFolder: string }>('data.moveDatabase', { folder }),
}
