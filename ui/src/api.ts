import { invoke } from './bridge'

export type CategoryKind = 'expense' | 'income' | 'transfer'
export type Granularity = 'month' | 'year' | 'range'
export type ColumnRole = 'ignore' | 'date' | 'valueDate' | 'description' | 'debit' | 'credit' | 'amount' | 'balance'
export type DateOrder = 'unknown' | 'dayMonth' | 'monthDay'
export type KindHint = 'auto' | 'bank' | 'creditCard'
export type AccountKind = 'bank' | 'creditCard'
export type Verification = 'verified' | 'mismatch' | 'noBalances' | 'noTransactions'

export interface Category { id: string; name: string; kind: CategoryKind; icon: string }

export type Settings = Record<'currency' | 'dateFormat' | 'theme' | 'reportsFolder', string> & Record<string, string>

export interface InitInfo {
  version: string
  settings: Settings
  categories: Category[]
  dateFormats: string[]
  ocrAvailable: boolean
  nonClientRegions: boolean
  maximized: boolean
  dataFolder: string
}

export interface Slice { key: string; label: string; amount: number; share: number }
export interface Bar { key: string; label: string; expenses: number; earnings: number }

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
}

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
  kind: AccountKind
  bank: string | null
  last4: string | null
  method: string
  usedOcr: boolean
  language: string
  dateOrder: DateOrder
  rowChecks: number
  rowMismatches: number
  suggestedAccountId: number | null
  defaultAccountName: string
  template: BankTemplate | null
  columns: TemplateColumn[]
  pageCount: number
  suggestedMatch: string
  rows: DraftRow[]
}

export interface DraftEdit { index: number; brand: string | null; categoryId: string | null; include: boolean }
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
    setCategory: (id: number, categoryId: string) => invoke<{ changed: number }>('tx.setCategory', { id, categoryId }),
    setBrand: (id: number, brand: string) => invoke<{ changed: number }>('tx.setBrand', { id, brand }),
    create: (input: TransactionInput) => invoke<TransactionView>('tx.create', { input }),
    update: (id: number, input: TransactionInput) => invoke<TransactionView>('tx.update', { id, input }),
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
    remove: (id: number) => invoke<null>('accounts.delete', { id }),
  },
  statements: {
    list: () => invoke<StatementInfo[]>('statements.list'),
    remove: (id: number) => invoke<null>('statements.delete', { id }),
  },
  imports: {
    parseFile: (path: string) => invoke<Draft>('import.parseFile', { path }),
    parseBytes: (name: string, base64: string) => invoke<Draft>('import.parseBytes', { name, base64 }),
    setAccount: (draftId: string, accountId: number | null) => invoke<Draft>('import.setAccount', { draftId, accountId }),
    commit: (p: { draftId: string; accountId: number | null; newAccountName: string | null; currency: string; kind: AccountKind; edits: DraftEdit[] }) =>
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
