import { startTransition, useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { Accordion, AccordionDetails, AccordionSummary, Box, Button, Skeleton, Typography, useTheme } from '@mui/material'
import PictureAsPdf from '@mui/icons-material/PictureAsPdfOutlined'
import ExpandMore from '@mui/icons-material/ExpandMoreRounded'
import SwapHoriz from '@mui/icons-material/SwapHorizRounded'
import EventRepeat from '@mui/icons-material/EventRepeatRounded'
import Payments from '@mui/icons-material/PaymentsRounded'
import { api, type Granularity, type PeriodSummary, type TransactionView } from '../api'
import { useApp } from '../AppContext'
import { money, plural } from '../format'
import { ExpenseBars, ExpensePie } from './Charts'
import { ExportDialog } from './ExportDialog'
import { FlowChart } from './FlowChart'
import { MovementsTable } from './MovementsTable'
import { TransactionTable } from './TransactionTable'

function Stat({ label, value, color, bg, note }: { label: string; value: string; color?: string; bg: string; note?: string }) {
  return (
    <Box data-stat={label} sx={{ flex: '1 1 180px', borderRadius: '18px', bgcolor: bg, px: 2.5, py: 2 }}>
      <Typography variant="body2" color="text.secondary" sx={{ fontWeight: 520 }}>{label}</Typography>
      <Typography className="tnum" sx={{ fontSize: 26, fontWeight: 650, letterSpacing: '-0.02em', mt: 0.25, color }}>{value}</Typography>
      {note && <Typography variant="body2" color="text.secondary" className="tnum" sx={{ mt: 0.25 }}>{note}</Typography>}
    </Box>
  )
}

const percent = (share: number | null) => (share === null ? '—' : `${Math.round(share * 100)}%`)

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
 * One period: title, operations, recurring payments, regular income, transfers (From / To),
 * Expenses / Earnings / Balance, the pies, expenses per day, the money moved, and the PDF export.
 */
export function PeriodCard({ from, to, granularity, currency, onEdit, onLoaded }: {
  from: string
  to: string
  granularity: Granularity
  currency: string
  onEdit: (tx: TransactionView) => void
  onLoaded?: (s: PeriodSummary) => void
}) {
  const { dataVersion, fail, category, settings } = useApp()
  const theme = useTheme()
  const ref = useRef<HTMLDivElement>(null)
  const [visible, setVisible] = useState(false)
  const [height, setHeight] = useState(560)
  const [summary, setSummary] = useState<PeriodSummary | null>(null)
  const [exporting, setExporting] = useState(false)

  // Load a period only when it comes near the screen: years of history stay fast.
  useEffect(() => {
    const el = ref.current
    if (!el) return
    // Near the screen it loads; once far away again it lets go of its data (it keeps its height, so nothing moves).
    const io = new IntersectionObserver((entries) => {
      const near = entries.some((e) => e.isIntersecting)
      if (!near && el.offsetHeight > 0) setHeight(el.offsetHeight)
      setVisible(near)
    }, { rootMargin: '900px 0px' })
    io.observe(el)
    return () => io.disconnect()
  }, [])

  useEffect(() => {
    if (!visible) { setSummary(null); return }
    let live = true
    // Drawn in the background, a little at a time, so scrolling stays smooth while periods come in.
    api.periods.summary(from, to, granularity, currency).then((s) => { if (live) startTransition(() => { setSummary(s); onLoaded?.(s) }) }, fail)
    return () => { live = false }
  }, [visible, from, to, granularity, currency, dataVersion, fail, onLoaded])

  // Transfers, card payments and investments: in their own folded table, hidden, or with the rest (Settings).
  // Recurring payments and income are shown in their own tables, never twice.
  const mode = settings.periodTransfers || 'separate'
  const showAll = settings.pieShowAll !== 'false'
  const { main, recurringOut, income } = useMemo(() => {
    const all = summary?.transactions ?? []
    const isMove = (t: TransactionView) => category(t.categoryId).kind === 'transfer'
    const isIncome = (t: TransactionView) => t.amount > 0 && !isMove(t)
    return {
      main: all.filter((t) => t.recurring === null && !isIncome(t) && (mode === 'inline' || !isMove(t))),
      recurringOut: all.filter((t) => t.recurring !== null && t.amount < 0),
      // Every income, whatever its source; for money back from an investment, only the part entered as earned.
      income: [
        ...all.filter(isIncome),
        ...all.filter((t) => isMove(t) && (t.earned ?? 0) > 0).map((t) => ({ ...t, amount: t.earned ?? 0, brand: `${t.brand} (earned part)` })),
      ].sort((a, b) => a.date.localeCompare(b.date)),
    }
  }, [summary, mode, category])
  // Editing a row of the income table opens the operation itself (full amount), not the earned part shown.
  const editOriginal = (t: TransactionView) => onEdit(summary?.transactions.find((x) => x.id === t.id) ?? t)

  const balanceColor = !summary ? undefined
    : summary.balance > 0 ? theme.palette.money.positive
    : summary.balance < 0 ? theme.palette.money.negative
    : theme.palette.money.zero
  const balanceBg = !summary || summary.balance === 0 ? 'surface.sunken'
    : summary.balance > 0 ? theme.palette.money.positiveStrong : theme.palette.money.negativeStrong
  const per = granularity === 'month' ? 'this month' : granularity === 'year' ? 'this year' : 'over the period'
  const months = summary ? Math.max(1, Math.round((Date.parse(summary.to) - Date.parse(summary.from)) / (30.44 * 86400000))) : 1
  const monthly = (v: number) => (granularity === 'month' ? undefined : `about ${money(v / months, currency)} a month`)
  const fold = (slices: PeriodSummary['byCategory']) => {
    if (showAll || slices.length <= 6) return slices
    const head = slices.slice(0, 5)
    const rest = slices.slice(5)
    return [...head, { key: '__other', label: 'Other', amount: rest.reduce((s, x) => s + x.amount, 0), share: rest.reduce((s, x) => s + x.share, 0) }]
  }

  return (
    <Box
      ref={ref}
      component="section"
      data-period={from}
      // Periods far from the screen are not laid out or painted (the browser skips them until they come near).
      // Only their height is guessed meanwhile: a guessed width would stretch every period wider than the window.
      sx={{ border: 1, borderColor: 'divider', borderRadius: '24px', bgcolor: 'background.paper', p: { xs: 2.5, md: 3.5 }, minHeight: summary ? undefined : height, contentVisibility: 'auto', minWidth: 0, containIntrinsicHeight: 'auto 2400px' }}
    >
      {!summary ? (
        <>
          <Skeleton variant="text" width="55%" height={40} />
          <Skeleton variant="rounded" height={220} sx={{ mt: 2, borderRadius: 3 }} />
          <Skeleton variant="rounded" height={90} sx={{ mt: 3, borderRadius: 3 }} />
        </>
      ) : (
        <>
          <Box sx={{ display: 'flex', alignItems: 'baseline', gap: 2, mb: 2.5 }}>
            <Typography variant="h5" component="h2" sx={{ flex: 1 }}>{summary.title}</Typography>
            <Typography data-days color="text.secondary" className="tnum" sx={{ fontWeight: 600, whiteSpace: 'nowrap' }}>
              {plural(Math.round((Date.parse(to) - Date.parse(from)) / 86400000) + 1, 'day')}
            </Typography>
          </Box>
          <TransactionTable transactions={main} currency={currency} paged onEdit={onEdit} empty="No other spending in this period" />

          {recurringOut.length > 0 && (
            <Section id="recurring-payments" tint={theme.palette.money.expenseSoft} icon={<EventRepeat />} title="Recurring payments & subscriptions" total={`${money(summary.recurringExpenses, currency)} ${per}`} note={monthly(summary.recurringExpenses)}>
              <TransactionTable transactions={recurringOut} currency={currency} paged onEdit={onEdit} frequency />
            </Section>
          )}
          {income.length > 0 && (
            <Section id="income" tint={theme.palette.money.incomeSoft} icon={<Payments />} title="Income" total={`${money(summary.earnings, currency)} ${per}`} note={monthly(summary.earnings)}>
              <TransactionTable transactions={income} currency={currency} paged onEdit={editOriginal} frequency />
            </Section>
          )}

          {mode === 'table' && summary.movements.length > 0 && (
            <Section id="movements" icon={<SwapHoriz />} title="Transfers, card payments & investments" note={String(summary.movements.length)}>
              <MovementsTable movements={summary.movements} transactions={summary.transactions} currency={currency} paged onEdit={onEdit} />
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
                <MovementsTable movements={summary.movements} transactions={summary.transactions} currency={currency} paged onEdit={onEdit} />
              </AccordionDetails>
            </Accordion>
          )}

          <Box sx={{ display: 'flex', flexWrap: 'wrap', gap: 1.5, mt: 3.5 }}>
            <Stat label="Expenses" value={money(summary.expenses, currency)} bg={theme.palette.money.expenseSoft} />
            <Stat label="Earnings" value={money(summary.earnings, currency)} bg={theme.palette.money.incomeSoft} />
            <Stat label="Balance" value={money(summary.balance, currency, { sign: true })} color={balanceColor} bg={balanceBg} />
          </Box>
          <Box sx={{ display: 'flex', flexWrap: 'wrap', gap: 1.5, mt: 1.5 }}>
            <Stat
              label="Recurring payments, rent excluded"
              value={money(summary.recurringWithoutHousing, currency)}
              note={`${percent(summary.recurringWithoutHousingShare)} of earnings ${per}`}
              bg="surface.sunken"
            />
            <Stat
              label="Expenses / earnings"
              value={percent(summary.expensesShare)}
              note={`${money(summary.expenses, currency)} spent of ${money(summary.earnings, currency)} earned`}
              bg="surface.sunken"
            />
          </Box>

          <Box sx={{ display: 'flex', flexWrap: 'wrap', gap: 1.5, mt: 1.5, alignItems: 'flex-start' }}>
            <ExpensePie title="Expenses per category" slices={fold(summary.byCategory)} currency={currency} />
            <ExpensePie title="Expenses per brand" slices={fold(summary.byBrand)} currency={currency} />
          </Box>
          <Box sx={{ display: 'flex', flexWrap: 'wrap', gap: 1.5, mt: 1.5 }}>
            <ExpensePie title="Recurring payments & subscriptions" slices={fold(summary.recurringExpensePie)} currency={currency} empty="No recurring payments" />
            <ExpensePie title="Income" slices={fold(summary.incomePie)} currency={currency} empty="No income" />
          </Box>
          <Box sx={{ mt: 1.5 }}>
            <ExpenseBars bars={summary.series} unit={summary.seriesUnit} currency={currency} />
          </Box>
          <Box sx={{ mt: 1.5 }}>
            <FlowChart diagram={summary.diagram} flows={summary.flows} currency={currency} />
          </Box>

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
            title={summary.title}
          />
        </>
      )}
    </Box>
  )
}
