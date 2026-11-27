import { memo, useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState, type CSSProperties, type ReactNode } from 'react'
import { Box, Button, Link, Table, TableBody, TableCell, TableHead, TableRow, Typography, useTheme, type Theme } from '@mui/material'
import PictureAsPdf from '@mui/icons-material/PictureAsPdfOutlined'
import ChevronRight from '@mui/icons-material/ChevronRightRounded'
import ReceiptLong from '@mui/icons-material/ReceiptLongRounded'
import SwapHoriz from '@mui/icons-material/SwapHorizRounded'
import EventRepeat from '@mui/icons-material/EventRepeatRounded'
import Payments from '@mui/icons-material/PaymentsRounded'
import AccountBalance from '@mui/icons-material/AccountBalanceOutlined'
import Public from '@mui/icons-material/PublicRounded'
import CurrencyExchange from '@mui/icons-material/CurrencyExchangeRounded'
import { api, globalTransactions, type Category, type Conversion, type ConversionLine, type Granularity, type PeriodReport, type PeriodSummary, type SalaryRate, type TransactionView } from '../api'
import { useApp } from '../AppContext'
import { formatDate, money, moneyBeside, plural } from '../format'
import { amountsHidden, MASK, useAmountsHidden } from '../mask'
// The charts (the largest library of the interface) are not needed to start: their code is read when the browser is
// idle after start-up, or when a period first shows its analytics. Once read, they are drawn at once (no waiting state).
type ChartsModule = typeof import('./Charts') & typeof import('./CategoryLines')
let charts: ChartsModule | null = null
let loading: Promise<ChartsModule> | null = null
export function loadCharts(): Promise<ChartsModule> {
  return (loading ??= Promise.all([import('./Charts'), import('./CategoryLines')]).then(([c, l]) => (charts = { ...c, ...l })))
}
function useCharts(wanted: boolean): ChartsModule | null {
  const [m, setM] = useState(charts)
  useEffect(() => { if (wanted && !m) void loadCharts().then(setM) }, [wanted, m])
  return m ?? charts
}
import { ExportDialog } from './ExportDialog'
import { FlowChart } from './FlowChart'
import { TransferTimelineChart } from './TransferTimeline'
import { MovementsTable } from './MovementsTable'
import { TransactionTable } from './TransactionTable'
import { ROW_EDIT_WIDTH, RowActions } from './RowActions'
import { Glyph } from './plain'
import { pace, paceTurn, pending, useStepper } from '../pace'
import { clickedNotSelected, isOpen, useFold } from '../folds'

/** A colour of the theme by its path ("surface.sunken"), or the colour itself. */
function paint(theme: Theme, c: string): string {
  const [a, b] = c.split('.')
  const v = b ? (theme.palette as unknown as Record<string, Record<string, string> | undefined>)[a]?.[b] : undefined
  return v ?? c
}

function Stat({ label, value, color, bg, note, className, ...rest }: { label: string; value: string; color?: string; bg: string; note?: string; className?: string; 'data-part'?: string; 'data-ratio'?: boolean }) {
  const theme = useTheme()
  return (
    <div data-stat={label} {...rest} className={className ? `p-stat ${className}` : 'p-stat'} style={{ backgroundColor: paint(theme, bg) }}>
      <p className="p-body2 p-stat-label">{label}</p>
      <p className="p-body1 tnum p-stat-value" style={color ? { color } : undefined}>{value}</p>
      {note && <p className="p-body2 tnum p-stat-note">{note}</p>}
    </div>
  )
}

const percent = (share: number | null) => (amountsHidden() ? MASK : share === null ? '—' : `${Math.round(share * 100)}%`)
const rate = (v: number) => v.toLocaleString('en-CA', { maximumFractionDigits: 6 })

/**
 * The summary of a bank, or of every bank together: what came in, what went out and the balance; under the balance,
 * the expenses as a share of the earnings (above 100% more was spent than earned).
 */
function Totals({ summary, currency }: { summary: PeriodSummary; currency: string }) {
  useAmountsHidden()
  const theme = useTheme()
  const m = theme.palette.money
  const balanceColor = summary.balance > 0 ? m.positive : summary.balance < 0 ? m.negative : m.zero
  const balanceBg = summary.balance === 0 ? 'surface.sunken' : summary.balance > 0 ? m.positiveStrong : m.negativeStrong
  const ratioBg = summary.expensesShare === null ? (summary.expenses > 0 ? m.expenseSoft : 'surface.sunken')
    : summary.expensesShare > 1 ? m.expenseSoft
    : summary.expensesShare < 1 ? m.incomeSoft
    : 'surface.sunken'
  return (
    <div data-totals className="p-totals">
      <Stat label="Earnings" value={money(summary.earnings, currency)} bg={m.incomeSoft} />
      <Stat label="Expenses" value={money(summary.expenses, currency)} bg={m.expenseSoft} />
      <Stat label="Balance" value={money(summary.balance, currency, { sign: true })} color={balanceColor} bg={balanceBg} />
      <Stat label="Expenses / earnings" value={percent(summary.expensesShare)} bg={ratioBg} className="p-ratio" data-ratio />
    </div>
  )
}

/** The button that folds or unfolds what its heading is the heading of. */
function FoldButton({ open, name, onToggle }: { open: boolean; name: string; onToggle: () => void }) {
  return (
    <button className="p-fold-button" data-fold={name} aria-expanded={open} aria-label={open ? `Fold ${name}` : `Unfold ${name}`}
      onClick={(e) => { e.stopPropagation(); onToggle() }}>
      <Glyph icon={ChevronRight} size={22} className={open ? 'p-chev p-open' : 'p-chev'} />
    </button>
  )
}

/**
 * A table under its own heading: folded or unfolded from it (its title stays selectable), how many operations it
 * holds, and the total of the period on the right.
 */
function Section({ icon, title, count, total, note, children, id, tint, foldKey, byDefault = true, boxed = false }: {
  icon: ReactNode; title: string; count: number; total?: string; note?: string; children: ReactNode; id: string; tint?: string
  foldKey: string; byDefault?: boolean; boxed?: boolean
}) {
  const [open, setOpen] = useFold(foldKey, byDefault)
  return (
    <div data-section={id} data-open={open || undefined} className={boxed ? 'p-section p-boxed' : 'p-section'}>
      <div className="p-section-head p-fold-head" onClick={() => { if (clickedNotSelected()) setOpen(!open) }}>
        <FoldButton open={open} name={title} onToggle={() => setOpen(!open)} />
        <div className="p-section-icon">{icon}</div>
        <h6 className="p-h6">{title}</h6>
        <p data-section-count className="p-body2 tnum p-section-count">{plural(count, 'operation')}</p>
        <span style={{ flex: 1 }} />
        {/* The total on a faint tint: red for payments, green for income. */}
        {total && <p data-section-total className="p-body1 tnum p-section-total" style={{ backgroundColor: tint }}>{total}</p>}
        {note && <p className="p-body2 tnum">{note}</p>}
      </div>
      {open && children}
    </div>
  )
}

/**
 * What a period shows: its statements (the tables) or its analytics (the charts), bank by bank, each with its totals,
 * after every bank together in the global display currency; or its transfers (the second part of Analytics): the money
 * moved between the accounts of every bank, over time and in total. Every period, and every bank in it, folds down to
 * its summary.
 */
export type PeriodView = 'statements' | 'analytics' | 'transfers'

/** Height of a bank's heading: it stays at the top of the screen while the bank's tables scroll, their headers just under it. */
const PART_HEAD = 52

/**
 * One bank (or every bank together): a heading, its summary, then (unfolded) what the view shows, in one bordered
 * block. The heading stays at the top of the screen while the block scrolls by; native: the currency of the bank's
 * accounts, shown with the display currency when they differ ("EUR → CAD").
 */
function Part({ kind, name, currency, native, children, partKey, summary, foldKey }: {
  kind: 'bank' | 'global'; name: string; currency: string; native?: string; children?: ReactNode; partKey: string
  /** Its Earnings, Expenses and Balance under the heading (Statements only). */
  summary?: PeriodSummary
  /** Folds to its summary (none: nothing more to show). */
  foldKey?: string
}) {
  const theme = useTheme()
  const [open, setOpen] = useFold(foldKey ?? '', false)
  const bg = kind === 'global' ? theme.palette.surface.sunken : theme.palette.background.paper
  const foldable = foldKey !== undefined
  return (
    <div data-part-block={partKey} data-open={(foldable && open) || undefined} className="p-part" style={{ backgroundColor: bg, '--part-head': `${PART_HEAD}px` } as CSSProperties}>
      <div data-part-head className={foldable ? 'p-part-head p-fold-head' : 'p-part-head'} style={{ height: PART_HEAD, backgroundColor: bg }}
        onClick={foldable ? () => { if (clickedNotSelected()) setOpen(!open) } : undefined}>
        {foldable && <FoldButton open={open} name={name} onToggle={() => setOpen(!open)} />}
        <div className="p-part-icon"><Glyph icon={kind === 'global' ? Public : AccountBalance} /></div>
        <h6 data-part-name className="p-h6 p-part-name">{name}</h6>
        <p data-part-currency className="p-body1 tnum p-part-currency" title={native && native !== currency ? `Its accounts are in ${native}, shown in ${currency}` : undefined}>
          {native && native !== currency ? `${native} → ${currency}` : currency}
        </p>
      </div>
      {summary && <Totals summary={summary} currency={currency} />}
      {(!foldable || open) && children}
    </div>
  )
}

/**
 * A period's operations come in pieces small enough to be read between two frames: the latest FIRST of each bank, then
 * the older ones PIECE at a time. A period of up to GATHER pieces shows once all of them are in (its tables never grow
 * after it appears); a longer one shows its latest operations at once, the older ones joining as they come.
 */
const FIRST = 300, PIECE = 1500, GATHER = 8

const NO_OPERATIONS: TransactionView[] = []

/** A bank's operations split into its tables (see splitOperations), with what they were split from. */
interface Split {
  all: TransactionView[]; mode: string; category: (id: string) => Category
  main: TransactionView[]; recurringOut: TransactionView[]; incomeOnly: TransactionView[]; earned: TransactionView[]; income: TransactionView[]
}

/**
 * The operations of a bank split into its tables: the other spending (with or without the transfers), the recurring
 * payments, and every income (for money back from an investment, only the part entered as earned), each in date order.
 * When the operations are the ones split before with older ones put before them (a long period coming in), only the
 * older ones are split, and put before.
 */
function splitOperations(all: TransactionView[], mode: string, category: (id: string) => Category, before: Split | null): Split {
  const isMove = (t: TransactionView) => category(t.categoryId).kind === 'transfer'
  const isIncome = (t: TransactionView) => t.amount > 0 && !isMove(t)
  const k = before ? all.length - before.all.length : -1
  const grown = before !== null && before.mode === mode && before.category === category && k > 0
    && all[k] === before.all[0] && all[all.length - 1] === before.all[before.all.length - 1]
  const part = grown ? all.slice(0, k) : all
  let main = part.filter((t) => t.recurring == null && !isIncome(t) && (mode === 'inline' || !isMove(t)))
  let recurringOut = part.filter((t) => t.recurring != null && t.amount < 0)
  let incomeOnly = part.filter(isIncome)
  let earned = part.filter((t) => isMove(t) && (t.earned ?? 0) > 0).map((t) => ({ ...t, amount: t.earned ?? 0, brand: `${t.brand} (earned part)` }))
  if (grown) {
    main = main.concat(before.main)
    recurringOut = recurringOut.concat(before.recurringOut)
    incomeOnly = incomeOnly.concat(before.incomeOnly)
    earned = earned.concat(before.earned)
  }
  // Both lists are in date order (as the bank lists its operations; put in it if not): merged by date, the income
  // itself before the earned parts of the same day.
  const byDate = (l: TransactionView[]) => { for (let n = 1; n < l.length; n++) if (l[n].date < l[n - 1].date) return [...l].sort((x, y) => (x.date < y.date ? -1 : x.date > y.date ? 1 : 0)); return l }
  incomeOnly = byDate(incomeOnly)
  earned = byDate(earned)
  const income: TransactionView[] = []
  let i = 0, j = 0
  while (i < incomeOnly.length || j < earned.length) {
    if (j >= earned.length || (i < incomeOnly.length && incomeOnly[i].date <= earned[j].date)) income.push(incomeOnly[i++])
    else income.push(earned[j++])
  }
  return { all, mode, category, main, recurringOut, incomeOnly, earned, income }
}
const folds = new WeakMap<PeriodSummary['byCategory'], PeriodSummary['byCategory']>()

/** The periods holding their data, by periodId. */
export const holding = new Set<string>()
/** What tells a period from another: its kind, its days, its currency. */
export const periodId = (granularity: string, from: string, to: string, currency: string) => `${granularity}:${from}:${to}:${currency}`

/** Frames a period waits after its last piece appeared before it counts as drawn. */
const SETTLE_FRAMES = 8

/**
 * The height each period had once drawn, by view, with the width of the page and the version of the data it was drawn
 * with (and how many operations it holds): a period not loaded yet takes it (or failing that the height of the period
 * of its kind drawn with the nearest number of operations), so the page has its real length before it is reached.
 * Drawn with other data or at another width, it is drawn again in the background.
 */
const drawnHeights = new Map<string, { h: number; w: number; v: number; n?: number }>()
const pageWidth = () => document.querySelector('main')?.clientWidth ?? 0
const heightOf = (key: string) => drawnHeights.get(key)?.h
const upToDate = (key: string, w: number, v: number) => { const k = drawnHeights.get(key); return !!k && k.w === w && k.v === v }
/** Periods not drawn yet, told when another of their kind is drawn (their guess may be nearer). */
const usualListeners = new Set<() => void>()
function usualHeight(kind: string, count?: number): number {
  let best: number | undefined, far = Infinity
  for (const [key, k] of drawnHeights) {
    if (!key.startsWith(kind)) continue
    const d = count !== undefined && k.n !== undefined ? Math.abs(k.n - count) : 0
    if (d < far) { far = d; best = k.h }
  }
  return best ?? 560
}

/** How far from the screen a period not drawn yet is drawn in the background to know its height (in pixels). */
const REACH = 60000
/** How near the screen a period holds its data (in pixels): further away, it lets go of it. */
const NEAR = 1500

/** The width of the page, once it stops changing (the window resized). */
const widthListeners = new Set<(w: number) => void>()
let widthWatch: ResizeObserver | null = null
let widthTimer = 0
function usePageWidth(): number {
  const [w, setW] = useState(pageWidth)
  useEffect(() => {
    widthListeners.add(setW)
    const main = document.querySelector('main')
    if (!widthWatch && main) {
      widthWatch = new ResizeObserver(() => {
        clearTimeout(widthTimer)
        widthTimer = window.setTimeout(() => { const now = pageWidth(); widthListeners.forEach((l) => l(now)) }, 300)
      })
      widthWatch.observe(main)
    }
    return () => { widthListeners.delete(setW) }
  }, [])
  return w
}

/** Statements (the tables) or analytics (the charts) of one bank or of all of them (drawn again only when they change). */
const Body = memo(function Body({ summary, view, keep, currency, granularity, onEdit, tables, salary }: {
  summary: PeriodSummary
  view: 'statements' | 'analytics'
  keep: string
  currency: string
  granularity: Granularity
  onEdit: (tx: TransactionView) => void
  /** False for all banks together: their operations are in each bank's tables. */
  tables: boolean
  /** The net salary of the period, written under the Income chart. */
  salary?: SalaryRate | null
}) {
  useAmountsHidden()
  const { category, settings } = useApp()
  const theme = useTheme()
  const c = useCharts(view === 'analytics')
  // Transfers, card payments and investments: in their own folded table, hidden, or with the rest (Settings).
  // Recurring payments and income are shown in their own tables, never twice.
  const mode = settings.periodTransfers || 'separate'
  // Pies: every category (and the recurring, income and tag pies) unless set otherwise; the brands, the largest five.
  const showAll = settings.pieShowAllCategories !== 'false'
  const showAllBrands = settings.pieShowAllBrands === 'true'
  // Charts by first-level category (nested categories counted in theirs), or down to every nested category.
  const topLevel = settings.chartsTopLevel !== 'false'
  // The tables (statements) or the rows of charts (analytics) appear one at a time, paced to the screen (see pace),
  // after the lines of the one above them.
  const end = useRef<HTMLDivElement>(null)
  const step = useStepper(view === 'statements' ? 5 : 6, `${view}:${summary.from}:${summary.to}`, end)
  // (Only the tables of a bank need its operations split, once the first of them appears: every bank together has none.)
  const last = useRef<Split | null>(null)
  const splitting = tables && view === 'statements' && step > 1
  const { main, recurringOut, income } = useMemo(() => {
    if (!splitting) return { main: NO_OPERATIONS, recurringOut: NO_OPERATIONS, income: NO_OPERATIONS }
    const split = splitOperations(summary.transactions, mode, category, last.current)
    last.current = split
    return split
  }, [summary, mode, category, splitting])
  // What the non-recurring table spends (transfers listed with it are not spending).
  const mainSpent = useMemo(() => main.reduce((s, t) => (t.amount < 0 && category(t.categoryId).kind !== 'transfer' ? s - t.amount : s), 0), [main, category])
  // Editing a row of the income table opens the operation itself (full amount), not the earned part shown.
  const editOriginal = useCallback((t: TransactionView) => onEdit(summary.transactions.find((x) => x.id === t.id) ?? t), [onEdit, summary.transactions])

  const per = granularity === 'month' ? 'this month' : granularity === 'year' ? 'this year' : 'over the period'
  const months = Math.max(1, Math.round((Date.parse(summary.to) - Date.parse(summary.from)) / (30.44 * 86400000)))
  const monthly = (v: number) => (granularity === 'month' ? undefined : `about ${money(v / months, currency)} a month`)
  const fold = (slices: PeriodSummary['byCategory'], all = showAll) => {
    if (all || slices.length <= 6) return slices
    // The same slices folded once: a chart is drawn again only when what it shows changes.
    let folded = folds.get(slices)
    if (!folded) {
      const head = slices.slice(0, 5)
      const rest = slices.slice(5)
      folds.set(slices, (folded = [...head, { key: '__other', label: 'Other', amount: rest.reduce((s, x) => s + x.amount, 0), share: rest.reduce((s, x) => s + x.share, 0) }]))
    }
    return folded
  }

  return (
    <div ref={end} className="p-body">
      {view === 'statements' ? (tables && (
        <>
          {step > 1 && (
            <Section id="main" foldKey={`t:${keep}:main`} tint={theme.palette.money.expenseSoft} icon={<Glyph icon={ReceiptLong} />} title="Non-recurring expenses" count={main.length}
              total={`${money(mainSpent, currency)} ${per}`}>
              <TransactionTable keep={keep + ':main'} transactions={main} currency={currency} paged onEdit={onEdit} noun="non-recurring expense" empty="No other spending in this period" />
            </Section>
          )}

          {step > 2 && recurringOut.length > 0 && (
            <Section id="recurring-payments" foldKey={`t:${keep}:recurring`} tint={theme.palette.money.expenseSoft} icon={<Glyph icon={EventRepeat} />} title="Recurring payments & subscriptions" count={recurringOut.length}
              total={`${money(summary.recurringExpenses, currency)} ${per}`} note={monthly(summary.recurringExpenses)}>
              <TransactionTable keep={keep + ':recurring'} transactions={recurringOut} currency={currency} paged onEdit={onEdit} frequency noun="recurring payment" />
              <div data-recurring-burden className="p-row" style={{ marginTop: theme.spacing(1.5) }}>
                <Stat label="Recurring payments, rent excluded" value={money(summary.recurringWithoutHousing, currency)} bg="surface.sunken" />
              </div>
            </Section>
          )}
          {step > 3 && income.length > 0 && (
            <Section id="income" foldKey={`t:${keep}:income`} tint={theme.palette.money.incomeSoft} icon={<Glyph icon={Payments} />} title="Income" count={income.length}
              total={`${money(summary.earnings, currency)} ${per}`} note={monthly(summary.earnings)}>
              <TransactionTable keep={keep + ':income'} transactions={income} currency={currency} paged onEdit={editOriginal} frequency noun="income operation" />
            </Section>
          )}

          {step > 4 && (mode === 'table' || mode === 'separate') && summary.movements.length > 0 && (
            // In its own table, unfolded or (the setting's "folded table") folded at first, on a sunken box.
            <Section id="movements" foldKey={`t:${keep}:movements`} byDefault={mode === 'table'} boxed={mode === 'separate'} icon={<Glyph icon={SwapHoriz} />}
              title="Transfers, card payments & investments" count={summary.movements.length}>
              <MovementsTable keep={keep + ':movements'} movements={summary.movements} transactions={summary.transactions} currency={currency} paged onEdit={onEdit} />
            </Section>
          )}
        </>
      )) : (
        !c ? <Box data-loading-charts sx={{ minHeight: 1400 }} /> : <Box sx={{ mt: 2.5 }}>
          {step > 1 && <Box sx={{ display: 'flex', flexWrap: 'wrap', gap: 1.5, alignItems: 'flex-start' }}>
            <c.ExpensePie title="Expenses per category" slices={fold(topLevel ? summary.byTopCategory : summary.byCategory)} currency={currency} />
            <c.ExpensePie title="Expenses per brand" slices={fold(summary.byBrand, showAllBrands)} currency={currency} />
          </Box>}
          {step > 2 && <Box sx={{ display: 'flex', flexWrap: 'wrap', gap: 1.5, mt: 1.5, alignItems: 'flex-start' }}>
            <c.ExpensePie title="Recurring payments & subscriptions" slices={fold(summary.recurringExpensePie)} currency={currency} empty="No recurring payments" />
            <c.ExpensePie title="Income" slices={fold(summary.incomePie)} currency={currency} empty="No income" footer={salary ? <SalaryLine salary={salary} currency={currency} /> : undefined} />
          </Box>}
          {step > 3 && <Box sx={{ display: 'flex', flexWrap: 'wrap', gap: 1.5, mt: 1.5 }}>
            {/* Each tagged expense in the slice of its whole set of tags: no money is counted twice. */}
            <c.ExpensePie title="Tags" slices={fold(summary.tagPie)} currency={currency} empty="No tagged expenses" />
            <Box sx={{ flex: '1 1 340px', minWidth: 0 }} />
          </Box>}
          {step > 4 && <Box sx={{ mt: 1.5 }}>
            <c.IncomeExpenseBars keep={`${keep}:bars`} bars={summary.series} unit={summary.seriesUnit} currency={currency} />
          </Box>}
          {step > 5 && <Box sx={{ mt: 1.5 }}>
            <c.CategoryLines keep={`${keep}:lines:${topLevel}`} from={summary.from} to={summary.to} transactions={summary.transactions} categories={topLevel ? summary.byTopCategory : summary.byCategory} topLevel={topLevel} currency={currency} />
          </Box>}
        </Box>
      )}
    </div>
  )
})

/**
 * The net salary of the period (the salary alone) per working day and per hour, and per year: every day but the
 * weekend is a working day (holidays are paid), each of the hours of a week entered in Settings shared by its days.
 */
function SalaryLine({ salary, currency }: { salary: SalaryRate; currency: string }) {
  useAmountsHidden()
  const tip = `${money(salary.net, currency)} of salary over ${plural(salary.workingDays, 'working day')} (weekends left out, holidays paid), `
    + `${salary.hoursPerDay} hours a day (${salary.hoursPerWeek} a week). A year: ${salary.yearWorkingDays} working days.`
  return (
    <Box data-salary title={tip} sx={{ mt: 2, pt: 1.75, borderTop: 1, borderColor: 'divider' }}>
      <Typography variant="body2" sx={{ fontWeight: 650, mb: 0.75 }}>Net salary</Typography>
      <Box data-salary-rates sx={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(96px, 1fr))', gap: 1 }}>
        {([['Per hour', salary.perHour, 'data-salary-hour'], ['Per day', salary.perDay, 'data-salary-day'], ['Per two weeks', salary.perTwoWeeks, 'data-salary-two-weeks'],
          ['Per month', salary.perMonth, 'data-salary-month'], ['Per year', salary.perYear, 'data-salary-year']] as const).map(([label, v, attr]) => (
          <Box key={label} {...{ [attr]: '' }}>
            <Typography variant="body2" color="text.secondary" noWrap>{label}</Typography>
            <Typography className="tnum" sx={{ fontWeight: 650, fontSize: 17 }}>{money(v, currency)}</Typography>
          </Box>
        ))}
      </Box>
    </Box>
  )
}

/** One bank's currency converted: totals before and after, the rates, and every rate by day on demand. */
function ConversionRow({ line }: { line: ConversionLine }) {
  useAmountsHidden()
  const { settings, go } = useApp()
  const [open, setOpen] = useState(false)
  const range = line.lowestRate === null ? '—'
    : line.lowestRate === line.highestRate ? `1 ${line.from} = ${rate(line.lowestRate)} ${line.to}`
    : `1 ${line.from} = ${rate(line.lowestRate)} to ${rate(line.highestRate ?? line.lowestRate)} ${line.to}`
  return (
    <>
      <TableRow data-conversion={`${line.group}:${line.from}:${line.to}`}>
        <TableCell>{line.group}</TableCell>
        <TableCell>{line.from} → {line.to}</TableCell>
        <TableCell align="right" className="tnum">{line.operations}</TableCell>
        <TableCell align="right" className="tnum">{moneyBeside(line.expenses, line.from, line.to)} → {money(line.convertedExpenses, line.to)}</TableCell>
        <TableCell align="right" className="tnum">{moneyBeside(line.earnings, line.from, line.to)} → {money(line.convertedEarnings, line.to)}</TableCell>
        <TableCell className="tnum">
          {range}
          {line.rates.length > 0 && <Link component="button" data-rates-toggle sx={{ ml: 1, verticalAlign: 'baseline' }} onClick={() => setOpen(!open)}>{open ? 'Hide rates' : 'Rates by day'}</Link>}
        </TableCell>
        {/* A conversion is made with the rates of the bank in use: its pencil opens Definitions > Exchange rates. */}
        <TableCell padding="none" align="right" sx={{ pr: 0.5 }}>
          <RowActions editLabel="Edit the exchange rates" onEdit={() => go('settings', 'rates')} />
        </TableCell>
      </TableRow>
      {open && (
        <TableRow>
          <TableCell colSpan={7} sx={{ py: 1 }}>
            <Box data-rates sx={{ maxHeight: 260, overflow: 'auto', display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(250px, 1fr))', columnGap: 3 }}>
              {line.rates.map((r) => (
                <Typography key={r.day} variant="body2" className="tnum" sx={{ py: 0.25 }}>
                  {formatDate(r.day, settings.dateFormat)}: 1 {line.from} = {rate(r.rate)} {line.to}
                  {r.rateDay !== r.day && <Box component="span" sx={{ color: 'text.secondary' }}> (rate of {formatDate(r.rateDay, settings.dateFormat)})</Box>}
                </Typography>
              ))}
            </Box>
          </TableCell>
        </TableRow>
      )}
    </>
  )
}

/** How the amounts of every bank together were converted: where the rates come from, the method, each currency before and after. */
const ConversionPanel = memo(function ConversionPanel({ conversion }: { conversion: Conversion }) {
  useAmountsHidden()
  const { settings, fail, dataChanged, notify, go } = useApp()
  const [updating, setUpdating] = useState(false)
  const missing = conversion.lines.filter((l) => l.unconverted > 0)
  const old = conversion.lines.filter((l) => l.oldRates > 0)
  const update = () => {
    setUpdating(true)
    api.rates.update().then((s) => { if (s.error) fail(new Error(s.error)); else { notify('Exchange rates updated'); dataChanged() } }, fail).finally(() => setUpdating(false))
  }
  return (
    <Box data-conversion-panel sx={{ mt: 2.5, border: 1, borderColor: 'divider', borderRadius: '16px', p: 2, bgcolor: 'background.paper' }}>
      <Box sx={{ display: 'flex', alignItems: 'center', gap: 1, mb: 1 }}>
        <CurrencyExchange sx={{ color: 'text.secondary', fontSize: 20 }} />
        <Typography sx={{ fontWeight: 650, flex: 1 }}>How amounts are converted to {conversion.to}</Typography>
        {(missing.length > 0 || old.length > 0) && <Button size="small" variant="outlined" disabled={updating} onClick={update}>Update rates</Button>}
      </Box>
      <Typography variant="body2" sx={{ mb: 0.5 }}>
        Rates: <Link component="button" data-rates-source sx={{ verticalAlign: 'baseline' }} title={conversion.url} onClick={() => go('settings', 'rates')}>{conversion.source}</Link>, against {conversion.base}
        {conversion.latest && <>, published up to {formatDate(conversion.latest, settings.dateFormat)}</>}.
      </Typography>
      <Typography variant="body2" color="text.secondary">
        Each operation is converted at the rate published for its own day (on a weekend or holiday, the last rate before it).
        Between two currencies other than {conversion.base} the rate goes through {conversion.base}, the same day. Each converted amount is rounded to the cent. Totals add the rounded amounts.
      </Typography>
      {conversion.lines.length === 0 ? (
        <Typography variant="body2" sx={{ mt: 1 }}>Nothing to convert: every amount is in {conversion.to}.</Typography>
      ) : (
        <Box sx={{ overflowX: 'auto', mt: 1 }}>
          <Table size="small" data-conversions sx={{ '& th': { position: 'static', bgcolor: 'transparent' } }}>
            <TableHead>
              <TableRow>
                <TableCell>Bank</TableCell>
                <TableCell>Currency</TableCell>
                <TableCell align="right">Operations</TableCell>
                <TableCell align="right">Expenses</TableCell>
                <TableCell align="right">Earnings</TableCell>
                <TableCell>Rate</TableCell>
                <TableCell sx={{ width: ROW_EDIT_WIDTH, px: 0 }} />
              </TableRow>
            </TableHead>
            <TableBody>{conversion.lines.map((l) => <ConversionRow key={`${l.group}:${l.from}:${l.to}`} line={l} />)}</TableBody>
          </Table>
        </Box>
      )}
      {missing.map((l) => (
        <Typography key={'m' + l.group + l.from} data-unconverted variant="body2" color="error" sx={{ mt: 1 }}>
          {l.group}: {plural(l.unconverted, 'operation')} in {l.from} ({money(l.unconvertedAmount, l.from)}) left out of the {conversion.to} totals, no rate known for their day.
        </Typography>
      ))}
      {old.map((l) => (
        <Typography key={'o' + l.group + l.from} variant="body2" color="warning.main" sx={{ mt: 1 }}>
          {l.group}: {plural(l.oldRates, 'operation')} converted with a rate more than a week older than the operation.
        </Typography>
      ))}
    </Box>
  )
})

/** The global part is shown when there is more than one bank, or anything converted (the same rule as the PDF). */
const showsGlobal = (r: PeriodReport) => r.banks.length > 1 || r.conversion.lines.length > 0

/**
 * One period: its summary (the global balance), or its statements or analytics bank by bank, each with its totals in
 * the bank's currency, then every bank together in the global display currency with how it was converted; and the
 * PDF export.
 */
export const PeriodCard = memo(function PeriodCard({ from, to, granularity, currency, view, onEdit, onLoaded, retiring = false, count }: {
  from: string
  to: string
  /** How many operations it holds, when known (a month or a year): its height is guessed from it until drawn. */
  count?: number
  granularity: Granularity
  /** The global display currency. */
  currency: string
  view: PeriodView
  onEdit: (tx: TransactionView) => void
  onLoaded?: (r: PeriodReport) => void
  /** Of the kind of period just left: hidden, until it is taken away. */
  retiring?: boolean
}) {
  const { dataVersion, fail } = useApp()
  const ref = useRef<HTMLDivElement>(null)
  const [visible, setVisible] = useState(false)
  // On the screen itself (not only near it); within reach of it (see below).
  const [onScreen, setOnScreen] = useState(false)
  const [inReach, setInReach] = useState(false)
  const id = periodId(granularity, from, to, currency)
  // Folded to its summary until opened (see folds); in Statements only, Analytics shows every period whole. What is
  // open is the period's, whatever the display currency (another one picked keeps the period and its banks open).
  const foldId = `${granularity}:${from}:${to}`
  const [monthFolds, setMonthOpen] = useFold(`p:${foldId}`, false)
  const [height, setHeight] = useState(() => { const f = view === 'statements' ? 'f' : 'a'; return heightOf(`${granularity}:${view}:${f}:${id}`) ?? usualHeight(`${granularity}:${view}:${f}`, count) })
  // Loaded in the background (away from the screen) once, to know its height, then let go of again.
  const [measuring, setMeasuring] = useState(false)
  const [report, setReport] = useState<PeriodReport | null>(null)
  const [exporting, setExporting] = useState(false)
  const [exportMade, setExportMade] = useState(false)
  // The view drawn: the one chosen, each period in its turn, the nearest to the screen first (see pace).
  const [shown, setShown] = useState(view)
  useLayoutEffect(() => {
    if (shown === view) return
    if (!report) { setShown(view); return }
    return pace(ref.current, () => setShown(view))
  }, [view, shown, report])
  const folding = shown === 'statements'
  const monthOpen = !folding || monthFolds
  // All banks together: the totals and charts of the report, the operations of every bank in the global currency.
  const charts = shown === 'analytics'
  const global = useMemo(() => (report ? { ...report.global, transactions: charts ? globalTransactions(report) : NO_OPERATIONS } : null), [report, charts])
  // Whenever its content changes (loaded, or another view): drawn once no piece of it is waiting any more.
  const [drawn, setDrawn] = useState(false)
  // The parts of a period (each bank, then every bank together) appear one per frame: no frame has to lay out a whole
  // period at once, so the screen never freezes while a long period comes in.
  // (Paced to the screen with every other piece of every period: see pace.) The older operations of a long period
  // coming in later do not draw its banks again.
  // (Every bank together first, then each bank once the period is unfolded.)
  // Analytics: each bank, then every bank together when there is more than one (the transfers: one part).
  const parts = !report ? 0 : folding ? 1 + (monthOpen ? report.banks.length : 0) : shown === 'transfers' ? 1 : report.banks.length + (showsGlobal(report) ? 1 : 0)
  const partsShown = useStepper(parts, `${report ? 'r' : ''}:${shown}:${monthOpen}`, ref)
  // What is unfolded in it: its height is kept for each way it was drawn.
  const folded = !folding ? 'a' : !monthOpen ? 'f' : 'o' + (report ? [...report.banks.map((b) => b.key), 'all'].map((k) => (isOpen(`p:${foldId}:${k}`, false) ? 1 : 0)).join('') : '')
  const hasData = useRef(false)
  hasData.current = report !== null
  // While a period lets go of its data: how many of its banks are still drawn.
  const [kept, setKept] = useState<number | null>(null)
  // Holding its data (see PeriodList: a period of the kind just left holding none goes at once).
  useEffect(() => {
    if (!report) return
    holding.add(id)
    return () => { holding.delete(id) }
  }, [report, id])
  const loaded = report !== null
  useLayoutEffect(() => { setDrawn(false) }, [loaded, shown, partsShown, folded])
  useEffect(() => {
    if (drawn || !report) return
    // Drawn in full once no piece of it is waiting any more, for a few frames.
    let id = 0, left = SETTLE_FRAMES
    const wait = () => { left = pending(ref.current) ? SETTLE_FRAMES : left - 1; if (left > 0) id = requestAnimationFrame(wait); else setDrawn(true) }
    id = requestAnimationFrame(wait)
    return () => cancelAnimationFrame(id)
  }, [drawn, report])

  // Its height once drawn, for this view: kept for when it is let go of, and for the next time it is made.
  const heightKey = `${granularity}:${shown}:${folded}:${id}`
  // The end of its turn at being drawn in the background (see below).
  const turnOver = useRef<(() => void) | null>(null)
  const endTurn = useCallback(() => { turnOver.current?.(); turnOver.current = null; setMeasuring(false) }, [])
  useEffect(() => {
    const el = ref.current
    if (!drawn || !el || retiring || kept !== null) return
    const h = el.offsetHeight
    drawnHeights.set(heightKey, { h, w: pageWidth(), v: dataVersion, n: count })
    usualListeners.forEach((l) => l())
    setHeight(h)
    endTurn()
  }, [drawn, heightKey, retiring, kept, endTurn]) // eslint-disable-line react-hooks/exhaustive-deps
  // Another view, not loaded: the height it has in that view (or the usual one, once known).
  const guess = useCallback(() => {
    if (hasData.current) return
    // Moved only when the new guess is notably nearer (the page's length changes as little as it can).
    const h = heightOf(heightKey) ?? usualHeight(`${granularity}:${shown}:${!folding ? 'a' : monthOpen ? 'o' : 'f'}`, count)
    setHeight((now) => (heightOf(heightKey) !== undefined || Math.abs(h - now) > 0.08 * now ? h : now))
  }, [heightKey, granularity, shown, count, monthOpen, folding])
  useLayoutEffect(guess, [guess])
  useEffect(() => { usualListeners.add(guess); return () => { usualListeners.delete(guess) } }, [guess])
  // Never drawn in this view, at this width, with this data: drawn once in the background, a few periods at a time,
  // the nearest first, while within reach of the screen (further ones meanwhile take the usual height).
  const width = usePageWidth()
  const known = upToDate(heightKey, width, dataVersion)
  useEffect(() => {
    if (known || visible || !inReach || report || measuring || retiring) return
    return paceTurn(ref.current, 'period-height', 3, (over) => { turnOver.current = over; setMeasuring(true) })
  }, [known, visible, inReach, report, measuring, retiring])
  // Let go of before it was drawn: the next one's turn.
  useEffect(() => { if (retiring) endTurn() }, [retiring, endTurn])
  useEffect(() => endTurn, [endTurn])

  // Load a period only when it comes near the screen: years of history stay fast.
  useEffect(() => {
    const el = ref.current
    if (!el) return
    // Near the screen it loads; once far away again it lets go of its data (it keeps its height, so nothing moves).
    // "Near" is measured in the page's scrolling area (measured against the window, the margin would not count).
    const io = new IntersectionObserver((entries) => {
      const near = entries.some((e) => e.isIntersecting)
      // Its height as drawn last (not while it is still the outline of one not loaded yet).
      if (!near && hasData.current && el.offsetHeight > 0) setHeight(el.offsetHeight)
      setVisible(near)
    }, { root: el.closest('main'), rootMargin: `${NEAR}px 0px` })
    io.observe(el)
    const on = new IntersectionObserver((entries) => setOnScreen(entries.some((e) => e.isIntersecting)), { root: el.closest('main') })
    on.observe(el)
    const reach = new IntersectionObserver((entries) => setInReach(entries.some((e) => e.isIntersecting)), { root: el.closest('main'), rootMargin: `${REACH}px 0px` })
    reach.observe(el)
    return () => { io.disconnect(); on.disconnect(); reach.disconnect() }
  }, [])
  const wanted = visible || measuring

  // How many banks are drawn (none: no data), for letting go of them.
  const drawnParts = useRef(-1)
  const drawnNow = report ? Math.min(partsShown, parts) : -1
  useLayoutEffect(() => { drawnParts.current = drawnNow }, [drawnNow])
  useEffect(() => {
    // Far from the screen again: its data let go in the background, a bank at a time (the last first), then the rest.
    if (!wanted) {
      if (drawnParts.current < 0) return
      let stop = () => {}
      // Brought back near the screen before the browser told it (it tells after the next frame): nothing more is let go
      // of meanwhile (a piece taken away there would be drawn again at once).
      const near = () => {
        const el = ref.current
        const main = el?.closest('main')
        if (!el || !main) return false
        const r = el.getBoundingClientRect(), v = main.getBoundingClientRect()
        return r.bottom > v.top - NEAR && r.top < v.bottom + NEAR
      }
      const next = (n: number) => { stop = pace(ref.current, () => { if (near()) next(n); else if (n > 0) { setKept(n - 1); next(n - 1) } else { setReport(null); setKept(null) } }) }
      next(drawnParts.current)
      return () => { stop(); setKept(null) }
    }
    let live = true
    // Drawn a little at a time, paced to the screen with every other period (the nearest first, see pace); what is
    // still waiting is called off when the period goes (or is read again).
    const stops: (() => void)[] = []
    const later = (run: () => void) => stops.push(pace(ref.current, () => { if (live) run() }))
    // A long period comes in pieces: its latest operations with everything else, at once; then its older operations,
    // a few thousand at a time, so the browser never has to read megabytes in one frame.
    const rest = async (r: PeriodReport) => {
      for (const b of r.banks) {
        let end = (b.summary.transactionCount ?? 0) - b.summary.transactions.length
        while (live && end > 0) {
          const start = Math.max(0, end - PIECE)
          const older = await api.periods.operations(from, to, granularity, b.key, start, end)
          if (!live) return
          later(() => setReport((cur) => cur && {
            ...cur,
            banks: cur.banks.map((x) => x.key !== b.key ? x : {
              ...x,
              summary: { ...x.summary, transactions: [...older, ...x.summary.transactions], transactionCount: start > 0 ? x.summary.transactionCount : undefined },
            }),
          }))
          end = start
        }
      }
    }
    // Every older operation of a bank, the pieces asked for together, put before the latest ones in their order.
    const whole = async (b: PeriodReport['banks'][number]) => {
      const end = (b.summary.transactionCount ?? 0) - b.summary.transactions.length
      if (end <= 0) return b
      const starts: number[] = []
      for (let at = 0; at < end; at += PIECE) starts.push(at)
      const pieces = await Promise.all(starts.map((at) => api.periods.operations(from, to, granularity, b.key, at, Math.min(end, at + PIECE))))
      return { ...b, summary: { ...b.summary, transactions: [...pieces.flat(), ...b.summary.transactions], transactionCount: undefined } }
    }
    api.periods.report(from, to, granularity, FIRST).then(async (r) => {
      if (!live) return
      const pieces = r.banks.reduce((n, b) => n + Math.ceil(Math.max(0, (b.summary.transactionCount ?? 0) - b.summary.transactions.length) / PIECE), 0)
      if (pieces > 0 && pieces <= GATHER) {
        const all = { ...r, banks: await Promise.all(r.banks.map(whole)) }
        if (live) later(() => { setReport(all); onLoaded?.(all) })
      } else later(() => { setReport(r); onLoaded?.(r); void rest(r).catch(fail) })
    }, fail)
    return () => { live = false; stops.forEach((s) => s()) }
  }, [wanted, from, to, granularity, currency, dataVersion, fail, onLoaded])

  // The salary is written under the Income chart of every bank together, or of the only bank when it is alone.
  const salaryWith = report && (showsGlobal(report) ? 'all' : report.banks[0]?.key)
  const banksDrawn = report ? Math.max(0, Math.min(partsShown, kept ?? Infinity) - (folding ? 1 : 0)) : 0

  return (
    <section
      ref={ref}
      data-period={retiring ? undefined : from}
      data-retiring={retiring || undefined}
      data-open={(folding && monthOpen) || undefined}
      className="p-period"
      // The tables and sections of a period away from the screen are not laid out or painted (see useRealHeights). One
      // being drawn away from the screen keeps its height until all of it is there, then takes its new height at once:
      // the page's length changes once, not with every piece. One being drawn on the screen is at least as tall as it
      // was before meanwhile; one letting go of its data keeps its height.
      // Only their height is guessed meanwhile: a guessed width would stretch every period wider than the window.
      style={{
        minHeight: report && kept === null && drawn ? undefined : height,
        ...(report && !drawn && !onScreen ? { height, overflow: 'clip' } : {}),
        // Not loaded: its outline is not drawn (nor its pulse) away from the screen, at its height.
        ...(!report ? { contentVisibility: 'auto', containIntrinsicHeight: '0px' } : {}),
        ...(retiring ? { display: 'none' } : {}),
      }}
    >
      {!report || !global ? (
        <>
          <span className="p-skeleton p-skeleton-text" style={{ width: '55%', height: 40 }} />
          <span className="p-skeleton" style={{ height: 160, marginTop: 16, borderRadius: 36 }} />
        </>
      ) : (
        <>
          {/* The period folds down to the summary of every bank together; a click on its heading folds or unfolds it. */}
          <div className={folding ? 'p-period-head p-fold-head' : 'p-period-head'} onClick={folding ? () => { if (clickedNotSelected()) setMonthOpen(!monthOpen) } : undefined}>
            {folding && <FoldButton open={monthOpen} name={report.title} onToggle={() => setMonthOpen(!monthOpen)} />}
            <h2 className="p-h5" style={{ flex: 1 }}>{report.title}</h2>
            <p data-days className="p-body1 tnum" style={{ fontWeight: 600, whiteSpace: 'nowrap' }}>
              {plural(Math.round((Date.parse(to) - Date.parse(from)) / 86400000) + 1, 'day')}
              {report.businessDays != null && <> · <span data-business-days>{plural(report.businessDays, 'business day')}</span></>}
            </p>
            <Button data-export variant="outlined" size="small" startIcon={<PictureAsPdf />} onClick={(e) => { e.stopPropagation(); setExporting(true); setExportMade(true) }} sx={{ alignSelf: 'center' }}>Export</Button>
          </div>
          {/* Made the first time it opens, kept after (so it closes as it always did). */}
          {exportMade && <ExportDialog
            open={exporting}
            onClose={() => setExporting(false)}
            from={from}
            to={to}
            granularity={granularity}
            currency={currency}
            title={report.title}
            timeline={report.global.timeline}
            timelineCurrency={report.currency}
          />}
          {folding ? (
            <>
              {partsShown > 0 && (kept === null || kept > 0) && (
                // Every bank together first, in the display currency: its summary, then (unfolded) how the amounts were
                // converted.
                <Part kind="global" partKey="all" name="All banks" currency={report.currency} summary={report.global}
                  foldKey={monthOpen && showsGlobal(report) ? `p:${foldId}:all` : undefined}>
                  {monthOpen && showsGlobal(report) && <ConversionPanel conversion={report.conversion} />}
                </Part>
              )}
              {monthOpen && report.banks.slice(0, banksDrawn).map((b) => (
                <Part key={b.key} kind="bank" partKey={b.key} name={b.name} currency={b.currency} native={b.native} summary={b.summary} foldKey={`p:${foldId}:${b.key}`}>
                  <Body summary={b.summary} view="statements" keep={`${from}_${to}_${b.key}`} currency={b.currency} granularity={granularity} onEdit={onEdit} tables salary={null} />
                </Part>
              ))}
            </>
          ) : shown === 'transfers' ? (
            // Money moved between the accounts of every bank, in the display currency: over time, then in total.
            partsShown > 0 && (kept === null || kept > 0) && (
              <Box data-transfers sx={{ display: 'grid', gridTemplateColumns: 'minmax(0, 1fr)', gap: 2.5, mt: 2.5 }}>
                <TransferTimelineChart timeline={report.global.timeline} currency={report.currency} />
                <Box data-flow-card>
                  <FlowChart diagram={report.global.diagram} flows={report.global.flows} currency={report.currency} />
                </Box>
              </Box>
            )
          ) : (
            // Analytics: the charts of each bank, then of every bank together; nothing folds, no summaries.
            <>
              {report.banks.slice(0, banksDrawn).map((b) => (
                <Part key={b.key} kind="bank" partKey={b.key} name={b.name} currency={b.currency} native={b.native}>
                  <Body summary={b.summary} view="analytics" keep={`${from}_${to}_${b.key}`} currency={b.currency} granularity={granularity} onEdit={onEdit} tables salary={salaryWith === b.key ? report.salary : null} />
                </Part>
              ))}
              {showsGlobal(report) && partsShown > report.banks.length && kept === null && (
                <Part kind="global" partKey="all" name="All banks" currency={report.currency}>
                  <Body summary={global} view="analytics" keep={`${from}_${to}_all`} currency={report.currency} granularity={granularity} onEdit={onEdit} tables={false} salary={salaryWith === 'all' ? report.salary : null} />
                  <ConversionPanel conversion={report.conversion} />
                </Part>
              )}
            </>
          )}
        </>
      )}
    </section>
  )
})
