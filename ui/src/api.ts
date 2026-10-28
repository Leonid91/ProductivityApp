import { invoke } from './bridge'

export type CategoryKind = 'expense' | 'income' | 'transfer'
export type Granularity = 'month' | 'year' | 'range'
export type ColumnRole = 'ignore' | 'date' | 'valueDate' | 'description' | 'debit' | 'credit' | 'amount' | 'balance' | 'category'
export type DateOrder = 'unknown' | 'dayMonth' | 'monthDay'
export type KindHint = 'auto' | 'bank' | 'creditCard'
export type AccountKind = 'bank' | 'creditCard' | 'savings'
export type Verification = 'verified' | 'mismatch' | 'noBalances' | 'noTransactions'

export interface Category { id: string; name: string; kind: CategoryKind; icon: string; group: string | null }

export type Settings = Record<'currency' | 'dateFormat' | 'theme' | 'reportsFolder' | 'bankingSystem' | 'bankFocus' | 'periodTransfers' | 'calendarHideCardPayments' | 'calendarHideTransfers' | 'pieShowAll', string> & Record<string, string>

export interface BankProfile { id: string; name: string; proven: boolean }

/** Account types, named the way the chosen banking system names them. */
export function accountKinds(system: string): { value: AccountKind; label: string }[] {
  return system === 'france'
    ? [{ value: 'bank', label: 'Current account' }, { value: 'savings', label: 'Savings (livret)' }, { value: 'creditCard', label: 'Deferred debit card' }]
    : [{ value: 'bank', label: 'Chequing account' }, { value: 'savings', label: 'Savings account' }, { value: 'creditCard', label: 'Credit card' }]
}

export interface InitInfo {
  version: string
  settings: Settings
  categories: Category[]
  dateFormats: string[]
  ocrAvailable: boolean
  nonClientRegions: boolean
  maximized: boolean
  dataFolder: string
  bankProfiles: BankProfile[]
}

export interface Slice { key: string; label: string; amount: number; share: number }
export interface Bar { key: string; label: string; expenses: number; earnings: number; moved: number; cardPaid: number }

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
  statementId: number | null
  /** How often it comes back ("monthly", "twice a month", "yearly"); null when it is not recurring. */
  recurring: string | null
  linkedId: number | null
}

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
}

export interface OutsideParty { name: string; sent: number; received: number }
/** An account box (centre x, y; size w, h) or the outside blob (an ellipse) with who is behind it. */
export interface DiagramNode { id: string; name: string; type: string; outside: boolean; x: number; y: number; w: number; h: number; parties: OutsideParty[] }
export interface DiagramArrow { from: string; to: string; amount: number; count: number; outside: boolean; path: string; head: string; width: number; labelX: number; labelY: number }
export interface FlowDiagram { width: number; height: number; nodes: DiagramNode[]; arrows: DiagramArrow[] }

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
  byBrand: Slice[]
  brandPie: Slice[]
  series: Bar[]
  seriesUnit: 'day' | 'month' | 'year'
  recurringExpenses: number
  recurringIncome: number
  recurringExpensePie: Slice[]
  recurringIncomePie: Slice[]
  movements: MovementView[]
  flows: Flow[]
  diagram: FlowDiagram
  /** Recurring payments without housing (rent, mortgage). */
  recurringWithoutHousing: number
  /** Shares of earnings; null without earnings. */
  recurringWithoutHousingShare: number | null
  expensesShare: number | null
}

export interface MonthEntry { year: number; month: number; count: number }
export interface PeriodIndex { months: MonthEntry[]; years: number[]; currencies: string[]; first: string | null; last: string | null }

export interface Account { id: number; name: string; currency: string; kind: AccountKind; bank: string | null; last4: string | null }

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

export interface BrandInfo { brand: string; count: number; spent: number; received: number; categoryId: string; learned: boolean; currency: string; lastDate: string }
export interface BrandRule { id: number; merchantKey: string; brand: string; categoryId: string | null; updatedAt: string }

export interface TransactionInput { date: string; amount: number; brand: string; categoryId: string; accountId: number | null; note: string | null }

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
  },
  tx: {
    setCategory: (id: number, categoryId: string, allOfBrand: boolean) => invoke<{ changed: number }>('tx.setCategory', { id, categoryId, allOfBrand }),
    setBrand: (id: number, brand: string) => invoke<{ changed: number }>('tx.setBrand', { id, brand }),
    create: (input: TransactionInput) => invoke<TransactionView>('tx.create', { input }),
    update: (id: number, input: TransactionInput, allOfBrand = true) => invoke<TransactionView>('tx.update', { id, input, allOfBrand }),
    remove: (id: number) => invoke<null>('tx.delete', { id }),
  },
  brands: {
    list: () => invoke<{ brands: BrandInfo[]; rules: BrandRule[]; builtInCount: number }>('brands.list'),
    setCategory: (brand: string, categoryId: string) => invoke<{ changed: number }>('brands.setCategory', { brand, categoryId }),
    deleteRule: (id: number) => invoke<{ changed: number }>('brands.deleteRule', { id }),
  },
  accounts: {
    list: () => invoke<Account[]>('accounts.list'),
    rename: (id: number, name: string) => invoke<Account>('accounts.update', { id, name }),
    update: (id: number, name: string, kind: AccountKind) => invoke<Account>('accounts.update', { id, name, kind }),
    remove: (id: number) => invoke<null>('accounts.delete', { id }),
  },
  statements: {
    list: () => invoke<StatementInfo[]>('statements.list'),
    remove: (id: number) => invoke<null>('statements.delete', { id }),
  },
  imports: {
    parseFile: (path: string) => invoke<Draft>('import.parseFile', { path }),
    parseBytes: (name: string, base64: string) => invoke<Draft>('import.parseBytes', { name, base64 }),
    choose: (draftId: string, accountId: number | null, kind: AccountKind | null) => invoke<Draft>('import.choose', { draftId, accountId, kind }),
    commit: (p: { draftId: string; accountId: number | null; newAccountName: string | null; currency: string; kind: AccountKind | null; edits: DraftEdit[] }) =>
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
}
