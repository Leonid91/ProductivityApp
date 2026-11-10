import { memo, startTransition, useEffect, useLayoutEffect, useMemo, useReducer, useRef, useState, type ReactNode } from 'react'
import { Accordion, AccordionDetails, AccordionSummary, Box, Button, Link, Skeleton, Table, TableBody, TableCell, TableHead, TableRow, Typography, useTheme } from '@mui/material'
import PictureAsPdf from '@mui/icons-material/PictureAsPdfOutlined'
import ExpandMore from '@mui/icons-material/ExpandMoreRounded'
import SwapHoriz from '@mui/icons-material/SwapHorizRounded'
import EventRepeat from '@mui/icons-material/EventRepeatRounded'
import Payments from '@mui/icons-material/PaymentsRounded'
import AccountBalance from '@mui/icons-material/AccountBalanceOutlined'
import Public from '@mui/icons-material/PublicRounded'
import CurrencyExchange from '@mui/icons-material/CurrencyExchangeRounded'
import { api, globalTransactions, type Conversion, type ConversionLine, type Granularity, type PeriodReport, type PeriodSummary, type TransactionView } from '../api'
import { useApp } from '../AppContext'
import { formatDate, money, moneyBeside, plural } from '../format'
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
import { MovementsTable } from './MovementsTable'
import { TransactionTable } from './TransactionTable'
import { ROW_EDIT_WIDTH, RowActions } from './RowActions'

function Stat({ label, value, color, bg, note, ...rest }: { label: string; value: string; color?: string; bg: string; note?: string; 'data-part'?: string }) {
  return (
    <Box data-stat={label} {...rest} sx={{ flex: '1 1 180px', borderRadius: '18px', bgcolor: bg, px: 2.5, py: 2 }}>
      <Typography variant="body2" color="text.secondary" sx={{ fontWeight: 520 }}>{label}</Typography>
      <Typography className="tnum" sx={{ fontSize: 26, fontWeight: 650, letterSpacing: '-0.02em', mt: 0.25, color }}>{value}</Typography>
      {note && <Typography variant="body2" color="text.secondary" className="tnum" sx={{ mt: 0.25 }}>{note}</Typography>}
    </Box>
  )
}

/**
 * Switching between Summary, Statements and Analytics: the period at the top of the screen is drawn in its new view at
 * once, the others one per frame, the nearest to the screen first, so the screen never freezes while tens of periods
 * draw their tables or charts.
 */
const switches: { el: HTMLElement; run: () => void }[] = []
let pumping = false
function distance(el: HTMLElement) {
  const main = el.closest('main')?.getBoundingClientRect()
  const r = el.getBoundingClientRect()
  if (!main) return 0
  return r.bottom < main.top ? main.top - r.bottom : r.top > main.bottom ? r.top - main.bottom : 0
}
function pump() {
  pumping = false
  if (switches.length === 0) return
  let best = 0
  for (let i = 1; i < switches.length; i++) if (distance(switches[i].el) < distance(switches[best].el)) best = i
  switches.splice(best, 1)[0].run()
  if (switches.length > 0) { pumping = true; setTimeout(() => requestAnimationFrame(pump), 0) }
}
function later(el: HTMLElement, run: () => void) {
  const item = { el, run }
  switches.push(item)
  if (!pumping) { pumping = true; requestAnimationFrame(pump) }
  return () => { const i = switches.indexOf(item); if (i >= 0) switches.splice(i, 1) }
}
/** True for the period at the top of the screen (the first one on screen, under the bar of the page). */
function atTop(el: HTMLElement) {
  const main = el.closest('main')?.getBoundingClientRect()
  if (!main) return true
  const r = el.getBoundingClientRect()
  if (r.bottom <= main.top || r.top >= main.bottom) return false
  const before = el.previousElementSibling?.getBoundingClientRect()
  return !before || before.bottom <= main.top + 1 || before.top >= main.bottom
}

// In the Summary view every month (or year) is as tall as the others: periods not loaded yet take the height of a loaded
// one, so nothing above the screen changes size while scrolling (the page never jumps back down).
const summaryHeights = new Map<string, number>()
const summaryListeners = new Set<() => void>()

const percent = (share: number | null) => (share === null ? '—' : `${Math.round(share * 100)}%`)
const rate = (v: number) => v.toLocaleString('en-CA', { maximumFractionDigits: 6 })

/** A table under its own heading, with the total of the period on the right. */
function Section({ icon, title, total, note, children, id, tint }: { icon: ReactNode; title: string; total?: string; note?: string; children: ReactNode; id: string; tint?: string }) {
  return (
    <Box data-section={id} sx={{ mt: 3 }}>
      <Box sx={{ display: 'flex', alignItems: 'baseline', gap: 1.25, mb: 1, flexWrap: 'wrap' }}>
        <Box sx={{ alignSelf: 'center', display: 'flex', color: 'text.secondary' }}>{icon}</Box>
        <Typography variant="h6" sx={{ flex: 1 }}>{title}</Typography>
        {/* The total on a faint tint: red for payments, green for income. */}
        {total && <Typography data-section-total className="tnum" sx={{ fontWeight: 650, bgcolor: tint, borderRadius: '10px', px: 1.25, py: 0.4 }}>{total}</Typography>}
        {note && <Typography variant="body2" color="text.secondary" className="tnum">{note}</Typography>}
      </Box>
      {children}
    </Box>
  )
}

/**
 * What a period shows: its summary (only the balance, all banks together: a month or a year alone, other periods month
 * by month, or year by year beyond a year), its statements (the tables) or its analytics (the charts), bank by bank,
 * each with its totals, then every bank together in the global display currency.
 */
export type PeriodView = 'summary' | 'statements' | 'analytics'

/** Height of a bank's heading: it stays at the top of the screen while the bank's tables scroll, their headers just under it. */
const PART_HEAD = 52

/**
 * One bank (or every bank together): a heading, then what the view shows, in one bordered block. The heading stays at
 * the top of the screen while the block scrolls by; native: the currency of the bank's accounts, shown with the
 * display currency when they differ ("EUR → CAD").
 */
function Part({ kind, name, currency, native, children, partKey }: { kind: 'bank' | 'global'; name: string; currency: string; native?: string; children: ReactNode; partKey: string }) {
  const bg = kind === 'global' ? 'surface.sunken' : 'background.paper'
  return (
    <Box
      data-part-block={partKey}
      sx={{ mt: 2.5, border: 1, borderColor: 'divider', borderRadius: '20px', px: { xs: 2, md: 2.5 }, pb: { xs: 2, md: 2.5 }, pt: 1, bgcolor: bg, minWidth: 0, '--part-head': `${PART_HEAD}px` }}
    >
      <Box data-part-head sx={{ position: 'sticky', top: 'var(--sticky-top, 0px)', zIndex: 3, height: PART_HEAD, display: 'flex', alignItems: 'center', gap: 1.25, bgcolor: bg, mb: 1 }}>
        <Box sx={{ display: 'flex', color: 'text.secondary' }}>{kind === 'global' ? <Public /> : <AccountBalance />}</Box>
        <Typography data-part-name variant="h6" sx={{ flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{name}</Typography>
        <Typography data-part-currency className="tnum" title={native && native !== currency ? `Its accounts are in ${native}, shown in ${currency}` : undefined}
          sx={{ fontWeight: 650, border: 1, borderColor: 'divider', borderRadius: '10px', px: 1, py: 0.25, fontSize: 13, whiteSpace: 'nowrap' }}>
          {native && native !== currency ? `${native} → ${currency}` : currency}
        </Typography>
      </Box>
      {children}
    </Box>
  )
}

/** Statements (the tables) or analytics (the charts) of one bank or of all of them, then their totals (drawn again only when they change). */
const Body = memo(function Body({ summary, view, keep, currency, granularity, onEdit, tables }: {
  summary: PeriodSummary
  view: 'statements' | 'analytics'
  keep: string
  currency: string
  granularity: Granularity
  onEdit: (tx: TransactionView) => void
  /** False for all banks together: their operations are in each bank's tables. */
  tables: boolean
}) {
  const { category, settings } = useApp()
  const theme = useTheme()
  const c = useCharts(view === 'analytics')
  // Transfers, card payments and investments: in their own folded table, hidden, or with the rest (Settings).
  // Recurring payments and income are shown in their own tables, never twice.
  const mode = settings.periodTransfers || 'separate'
  const showAll = settings.pieShowAll !== 'false'
  // Charts by first-level category (nested categories counted in theirs), or down to every nested category.
  const topLevel = settings.chartsTopLevel !== 'false'
  const { main, recurringOut, income } = useMemo(() => {
    const all = summary.transactions
    const isMove = (t: TransactionView) => category(t.categoryId).kind === 'transfer'
    const isIncome = (t: TransactionView) => t.amount > 0 && !isMove(t)
    return {
      main: all.filter((t) => t.recurring == null && !isIncome(t) && (mode === 'inline' || !isMove(t))),
      recurringOut: all.filter((t) => t.recurring != null && t.amount < 0),
      // Every income, whatever its source; for money back from an investment, only the part entered as earned.
      income: [
        ...all.filter(isIncome),
        ...all.filter((t) => isMove(t) && (t.earned ?? 0) > 0).map((t) => ({ ...t, amount: t.earned ?? 0, brand: `${t.brand} (earned part)` })),
      ].sort((a, b) => a.date.localeCompare(b.date)),
    }
  }, [summary, mode, category])
  // Editing a row of the income table opens the operation itself (full amount), not the earned part shown.
  const editOriginal = (t: TransactionView) => onEdit(summary.transactions.find((x) => x.id === t.id) ?? t)

  const balanceColor = summary.balance > 0 ? theme.palette.money.positive : summary.balance < 0 ? theme.palette.money.negative : theme.palette.money.zero
  const balanceBg = summary.balance === 0 ? 'surface.sunken' : summary.balance > 0 ? theme.palette.money.positiveStrong : theme.palette.money.negativeStrong
  const ratioBg = summary.expensesShare === null ? (summary.expenses > 0 ? theme.palette.money.expenseSoft : 'surface.sunken')
    : summary.expensesShare > 1 ? theme.palette.money.expenseSoft
    : summary.expensesShare < 1 ? theme.palette.money.incomeSoft
    : 'surface.sunken'
  const per = granularity === 'month' ? 'this month' : granularity === 'year' ? 'this year' : 'over the period'
  const months = Math.max(1, Math.round((Date.parse(summary.to) - Date.parse(summary.from)) / (30.44 * 86400000)))
  const monthly = (v: number) => (granularity === 'month' ? undefined : `about ${money(v / months, currency)} a month`)
  const fold = (slices: PeriodSummary['byCategory']) => {
    if (showAll || slices.length <= 6) return slices
    const head = slices.slice(0, 5)
    const rest = slices.slice(5)
    return [...head, { key: '__other', label: 'Other', amount: rest.reduce((s, x) => s + x.amount, 0), share: rest.reduce((s, x) => s + x.share, 0) }]
  }

  return (
    <>
      {/* Each bank starts with what it spent, earned and its balance. */}
      <Box data-totals sx={{ display: 'flex', flexWrap: 'wrap', gap: 1.5, mb: tables || view === 'analytics' ? 3 : 0 }}>
        <Stat label="Expenses" value={money(summary.expenses, currency)} bg={theme.palette.money.expenseSoft} />
        <Stat label="Earnings" value={money(summary.earnings, currency)} bg={theme.palette.money.incomeSoft} />
        <Stat label="Balance" value={money(summary.balance, currency, { sign: true })} color={balanceColor} bg={balanceBg} />
      </Box>
      {view === 'statements' ? (tables && (
        <>
          <TransactionTable keep={keep + ':main'} transactions={main} currency={currency} paged onEdit={onEdit} empty="No other spending in this period" />

          {recurringOut.length > 0 && (
            <Section id="recurring-payments" tint={theme.palette.money.expenseSoft} icon={<EventRepeat />} title="Recurring payments & subscriptions" total={`${money(summary.recurringExpenses, currency)} ${per}`} note={monthly(summary.recurringExpenses)}>
              <TransactionTable keep={keep + ':recurring'} transactions={recurringOut} currency={currency} paged onEdit={onEdit} frequency />
            </Section>
          )}
          {income.length > 0 && (
            <Section id="income" tint={theme.palette.money.incomeSoft} icon={<Payments />} title="Income" total={`${money(summary.earnings, currency)} ${per}`} note={monthly(summary.earnings)}>
              <TransactionTable keep={keep + ':income'} transactions={income} currency={currency} paged onEdit={editOriginal} frequency />
            </Section>
          )}

          {mode === 'table' && summary.movements.length > 0 && (
            <Section id="movements" icon={<SwapHoriz />} title="Transfers, card payments & investments" note={String(summary.movements.length)}>
              <MovementsTable keep={keep + ':movements'} movements={summary.movements} transactions={summary.transactions} currency={currency} paged onEdit={onEdit} />
            </Section>
          )}
          {mode === 'separate' && summary.movements.length > 0 && (
            <Accordion
              data-section="movements"
              disableGutters
              elevation={0}
              slotProps={{ transition: { unmountOnExit: true } }}
              sx={{ mt: 3, border: 1, borderColor: 'divider', borderRadius: '16px !important', bgcolor: 'surface.sunken', '& th': { bgcolor: 'surface.sunken' }, '&::before': { display: 'none' } }}
            >
              <AccordionSummary expandIcon={<ExpandMore />} sx={{ px: 2.5, minHeight: 52 }}>
                <Box sx={{ display: 'flex', alignItems: 'center', gap: 1.25 }}>
                  <SwapHoriz sx={{ color: 'text.secondary', fontSize: 20 }} />
                  <Typography sx={{ fontWeight: 600 }}>Transfers, card payments & investments</Typography>
                  <Typography color="text.secondary" className="tnum">{summary.movements.length}</Typography>
                </Box>
              </AccordionSummary>
              <AccordionDetails sx={{ px: 2.5, pt: 0, pb: 2 }}>
                <MovementsTable keep={keep + ':movements'} movements={summary.movements} transactions={summary.transactions} currency={currency} paged onEdit={onEdit} />
              </AccordionDetails>
            </Accordion>
          )}
        </>
      )) : (
        !c ? <Box data-loading-charts sx={{ minHeight: 1400 }} /> : <>
          <Box sx={{ display: 'flex', flexWrap: 'wrap', gap: 1.5, alignItems: 'flex-start' }}>
            <c.ExpensePie title="Expenses per category" slices={fold(topLevel ? summary.byTopCategory : summary.byCategory)} currency={currency} />
            <c.ExpensePie title="Expenses per brand" slices={fold(summary.byBrand)} currency={currency} />
          </Box>
          <Box sx={{ display: 'flex', flexWrap: 'wrap', gap: 1.5, mt: 1.5 }}>
            <c.ExpensePie title="Recurring payments & subscriptions" slices={fold(summary.recurringExpensePie)} currency={currency} empty="No recurring payments" />
            <c.ExpensePie title="Income" slices={fold(summary.incomePie)} currency={currency} empty="No income" />
          </Box>
          <Box sx={{ display: 'flex', flexWrap: 'wrap', gap: 1.5, mt: 1.5 }}>
            {/* Each tagged expense in the slice of its whole set of tags: no money is counted twice. */}
            <c.ExpensePie title="Tags" slices={fold(summary.tagPie)} currency={currency} empty="No tagged expenses" />
            <Box sx={{ flex: '1 1 340px', minWidth: 0 }} />
          </Box>
          <Box sx={{ mt: 1.5 }}>
            <c.ExpenseBars bars={summary.series} unit={summary.seriesUnit} currency={currency} />
          </Box>
          <Box sx={{ mt: 1.5 }}>
            <c.CategoryLines keep={`${keep}:lines:${topLevel}`} from={summary.from} to={summary.to} transactions={summary.transactions} categories={topLevel ? summary.byTopCategory : summary.byCategory} topLevel={topLevel} currency={currency} />
          </Box>
          <Box sx={{ mt: 1.5 }}>
            <FlowChart diagram={summary.diagram} flows={summary.flows} currency={currency} />
          </Box>
        </>
      )}

      <Box data-ratios sx={{ display: 'flex', flexWrap: 'wrap', gap: 1.5, mt: tables || view === 'analytics' ? 3.5 : 1.5 }}>
        <Stat label="Recurring payments, rent excluded" value={money(summary.recurringWithoutHousing, currency)} bg="surface.sunken" />
        {/* Above 100% more was spent than earned (red, like the expenses); below, a surplus (green, like the earnings). */}
        <Stat label="Expenses / earnings" value={percent(summary.expensesShare)} bg={ratioBg} />
      </Box>
    </>
  )
})

/** One bank's currency converted: totals before and after, the rates, and every rate by day on demand. */
function ConversionRow({ line }: { line: ConversionLine }) {
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
        Between two currencies other than {conversion.base} the rate goes through {conversion.base}, the same day. Each converted amount is rounded to the cent; totals add the rounded amounts.
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
export function PeriodCard({ from, to, granularity, currency, view, onEdit, onLoaded }: {
  from: string
  to: string
  granularity: Granularity
  /** The global display currency. */
  currency: string
  view: PeriodView
  onEdit: (tx: TransactionView) => void
  onLoaded?: (r: PeriodReport) => void
}) {
  const { dataVersion, fail } = useApp()
  const theme = useTheme()
  const ref = useRef<HTMLDivElement>(null)
  const [visible, setVisible] = useState(false)
  const [height, setHeight] = useState(560)
  const [report, setReport] = useState<PeriodReport | null>(null)
  const [exporting, setExporting] = useState(false)
  const [, redraw] = useReducer((n: number) => n + 1, 0)
  // The view drawn: the one chosen, at once for the period at the top of the screen, a frame later for each other one.
  const [shown, setShown] = useState(view)
  useLayoutEffect(() => {
    const el = ref.current
    if (shown === view) return
    if (!report || !el || atTop(el)) { setShown(view); return }
    return later(el, () => setShown(view))
  }, [view, shown, report])
  // All banks together: the totals and charts of the report, the operations of every bank in the global currency.
  const global = useMemo(() => (report ? { ...report.global, transactions: globalTransactions(report) } : null), [report])
  useEffect(() => { summaryListeners.add(redraw); return () => { summaryListeners.delete(redraw) } }, [])
  useLayoutEffect(() => {
    const el = ref.current
    if (shown !== 'summary' || !global || global.breakdown.length > 1 || !el) return
    if (summaryHeights.get(granularity) !== el.offsetHeight) {
      summaryHeights.set(granularity, el.offsetHeight)
      summaryListeners.forEach((l) => l())
    }
  }, [shown, global, granularity])
  const sameHeight = shown === 'summary' ? summaryHeights.get(granularity) : undefined
  // Whenever its content changes (loaded, or another view), the period is drawn in full once, even off screen, so the
  // browser knows its real height before it is reached (scrolling never skips part of it); then it may skip it again.
  const [drawn, setDrawn] = useState(false)
  useLayoutEffect(() => { setDrawn(false) }, [report, shown])
  useEffect(() => {
    if (drawn || !report) return
    let second = 0
    const first = requestAnimationFrame(() => { second = requestAnimationFrame(() => setDrawn(true)) })
    return () => { cancelAnimationFrame(first); cancelAnimationFrame(second) }
  }, [drawn, report])

  // Load a period only when it comes near the screen: years of history stay fast.
  useEffect(() => {
    const el = ref.current
    if (!el) return
    // Near the screen it loads; once far away again it lets go of its data (it keeps its height, so nothing moves).
    // "Near" is measured in the page's scrolling area (measured against the window, the margin would not count).
    const io = new IntersectionObserver((entries) => {
      const near = entries.some((e) => e.isIntersecting)
      if (!near && el.offsetHeight > 0) setHeight(el.offsetHeight)
      setVisible(near)
    }, { root: el.closest('main'), rootMargin: '1500px 0px' })
    io.observe(el)
    return () => io.disconnect()
  }, [])

  useEffect(() => {
    if (!visible) { setReport(null); return }
    let live = true
    // Drawn in the background, a little at a time, so scrolling stays smooth while periods come in.
    api.periods.report(from, to, granularity).then((r) => { if (live) startTransition(() => { setReport(r); onLoaded?.(r) }) }, fail)
    return () => { live = false }
  }, [visible, from, to, granularity, currency, dataVersion, fail, onLoaded])

  // A balance in green above zero, red below, plain at zero.
  const tone = (v: number) => ({
    color: v > 0 ? theme.palette.money.positive : v < 0 ? theme.palette.money.negative : theme.palette.money.zero,
    bg: v === 0 ? 'surface.sunken' : v > 0 ? theme.palette.money.positiveStrong : theme.palette.money.negativeStrong,
  })

  return (
    <Box
      ref={ref}
      component="section"
      data-period={from}
      // Periods away from the screen are not laid out or painted (the browser skips them until they come near), at the
      // height they had when last drawn.
      // Only their height is guessed meanwhile: a guessed width would stretch every period wider than the window.
      sx={{ border: 1, borderColor: 'divider', borderRadius: '24px', bgcolor: 'background.paper', p: { xs: 2.5, md: 3.5 }, minHeight: report ? undefined : height, ...(!report && sameHeight ? { height: sameHeight, minHeight: 0, overflow: 'hidden' } : {}), contentVisibility: shown === 'summary' || (report && !drawn) ? 'visible' : 'auto', minWidth: 0, containIntrinsicHeight: 'auto 2400px' }}
    >
      {!report || !global ? (
        <>
          <Skeleton variant="text" width="55%" height={40} />
          <Skeleton variant="rounded" height={220} sx={{ mt: 2, borderRadius: 3 }} />
          <Skeleton variant="rounded" height={90} sx={{ mt: 3, borderRadius: 3 }} />
        </>
      ) : (
        <>
          <Box sx={{ display: 'flex', alignItems: 'baseline', gap: 2, mb: shown === 'summary' ? 2.5 : 0 }}>
            <Typography variant="h5" component="h2" sx={{ flex: 1 }}>{report.title}</Typography>
            <Typography data-days color="text.secondary" className="tnum" sx={{ fontWeight: 600, whiteSpace: 'nowrap' }}>
              {plural(Math.round((Date.parse(to) - Date.parse(from)) / 86400000) + 1, 'day')}
              {report.businessDays != null && <> · <span data-business-days>{plural(report.businessDays, 'business day')}</span></>}
            </Typography>
          </Box>
          {shown === 'analytics' && report.salary && (
            // The net salary of the period per hour worked and per year, every bank together, in the display currency.
            <Typography data-salary className="tnum" sx={{ mt: 1, mb: 2.5, fontSize: 15.5 }}
              title={`${money(report.salary.net, currency)} of salary over ${plural(report.salary.businessDays, 'business day')} of ${report.salary.hoursPerDay} hours; a year: ${report.salary.yearBusinessDays} business days`}>
              Your net salary per hour is <Box component="b" data-salary-hour sx={{ fontWeight: 680 }}>{money(report.salary.perHour, currency)}</Box> and
              per year is <Box component="b" data-salary-year sx={{ fontWeight: 680 }}>{money(report.salary.perYear, currency)}</Box>.
            </Typography>
          )}
          {shown === 'summary' ? (
            // The summary stays global: every bank together, in the global display currency.
            <Box data-summary sx={{ display: 'grid', gap: 1.5 }}>
              {global.breakdown.length > 1 && (
                <Box sx={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(260px, 1fr))', gap: 1.5 }}>
                  {global.breakdown.map((b) => (
                    <Stat key={b.from} label={b.title} value={money(b.balance, currency, { sign: true })} color={tone(b.balance).color} bg={tone(b.balance).bg} data-part={b.from} />
                  ))}
                </Box>
              )}
              <Stat label="Balance" value={money(global.balance, currency, { sign: true })} color={tone(global.balance).color} bg={tone(global.balance).bg} />
            </Box>
          ) : (
            <>
              {report.banks.map((b) => (
                <Part key={b.key} kind="bank" partKey={b.key} name={b.name} currency={b.currency} native={b.native}>
                  <Body summary={b.summary} view={shown} keep={`${from}_${to}_${b.key}`} currency={b.currency} granularity={granularity} onEdit={onEdit} tables />
                </Part>
              ))}
              {showsGlobal(report) && (
                <Part kind="global" partKey="all" name="All banks" currency={report.currency}>
                  <Body summary={global} view={shown} keep={`${from}_${to}_all`} currency={report.currency} granularity={granularity} onEdit={onEdit} tables={false} />
                  <ConversionPanel conversion={report.conversion} />
                </Part>
              )}

              <Box sx={{ display: 'flex', justifyContent: 'flex-end', mt: 2.5 }}>
                <Button variant="outlined" startIcon={<PictureAsPdf />} onClick={() => setExporting(true)}>Export to PDF</Button>
              </Box>
              <ExportDialog
                open={exporting}
                onClose={() => setExporting(false)}
                from={from}
                to={to}
                granularity={granularity}
                currency={currency}
                title={report.title}
              />
            </>
          )}
        </>
      )}
    </Box>
  )
}
