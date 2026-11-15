import { useEffect, useMemo, useState } from 'react'
import { Autocomplete, Box, Button, IconButton, TextField, Tooltip, Typography, useTheme } from '@mui/material'
import East from '@mui/icons-material/EastRounded'
import West from '@mui/icons-material/WestRounded'
import ChevronLeft from '@mui/icons-material/ChevronLeftRounded'
import ChevronRight from '@mui/icons-material/ChevronRightRounded'
import Add from '@mui/icons-material/AddRounded'
import FullCalendar from '@fullcalendar/react'
import dayGridPlugin from '@fullcalendar/react/daygrid'
import interactionPlugin from '@fullcalendar/react/interaction'
import classicThemePlugin from '@fullcalendar/react/themes/classic'
import '@fullcalendar/react/skeleton.css'
import '@fullcalendar/react/themes/classic/theme.css'
import '@fullcalendar/react/themes/classic/palette.css'
import { api, globalTransactions, type Holiday, type PeriodSummary, type RateUse, type TransactionView } from '../api'
import { useApp } from '../AppContext'
import { daysInMonth, formatDate, iso, money, moneyBeside, monthName, parts, today } from '../format'
import { CategoryIcon } from '../components/CategoryIcon'
import { Money, useCategoryColor } from '../components/common'
import { TransactionDialog } from '../components/TransactionDialog'

/** The day n days after an ISO day. */
function addDays(day: string, n: number): string {
  const d = parts(day)
  const t = new Date(Date.UTC(d.y, d.m - 1, d.d + n))
  return iso(t.getUTCFullYear(), t.getUTCMonth() + 1, t.getUTCDate())
}


/** The month's totals as small boxes, in the colours of the periods: earnings, expenses, balance. */
function MonthTotal({ label, value, color, bg }: { label: string; value: string; color?: string; bg: string }) {
  return (
    <Box data-month-total={label} sx={{ borderRadius: '12px', px: 1.25, py: 0.5, bgcolor: bg, lineHeight: 1.2, whiteSpace: 'nowrap' }}>
      <Typography sx={{ fontSize: 11.5, fontWeight: 520, color: 'text.secondary' }}>{label}</Typography>
      <Typography className="tnum" sx={{ fontSize: 15, fontWeight: 650, letterSpacing: '-0.01em', color }}>{value}</Typography>
    </Box>
  )
}

/** "CAD>EUR": what one CAD is worth in EUR; "CAD<EUR": what one EUR is worth in CAD (the arrow turned). */
function parsePair(setting: string | undefined, currency: string): { left: string; right: string; forward: boolean } {
  const m = /^([A-Z]{3})([<>])([A-Z]{3})$/.exec(setting ?? '')
  if (m) return { left: m[1], right: m[3], forward: m[2] === '>' }
  return { left: currency, right: currency === 'EUR' ? 'USD' : 'EUR', forward: true }
}

const rateText = (r: number) => r.toLocaleString('en-CA', { minimumFractionDigits: 4, maximumFractionDigits: 6 })

/**
 * The exchange rate of two currencies, chosen in lists that find a currency as it is typed; the arrow between them
 * says which way (CAD → EUR: one CAD in EUR), turned by a click. The rate of the day chosen is written here, and every
 * day's on the calendar. The rates are those of the central bank chosen in Settings.
 */
function RatesCard({ pair, currencies, onPair, rates, day, source }: {
  pair: { left: string; right: string; forward: boolean }
  currencies: string[]
  onPair: (p: { left: string; right: string; forward: boolean }) => void
  rates: Map<string, RateUse>
  day: string
  source: string
}) {
  const { settings } = useApp()
  const [from, to] = pair.forward ? [pair.left, pair.right] : [pair.right, pair.left]
  const r = rates.get(day)
  const pick = (side: 'left' | 'right') => (
    <Autocomplete
      data-rate-currency={side}
      size="small"
      disableClearable
      autoHighlight
      options={currencies}
      value={pair[side]}
      onChange={(_, v) => { if (v) onPair({ ...pair, [side]: v }) }}
      sx={{ flex: 1, minWidth: 0 }}
      slotProps={{ paper: { sx: { borderRadius: 3 } } }}
      renderInput={(params) => <TextField {...params} slotProps={{ ...params.slotProps, htmlInput: { ...params.slotProps.htmlInput, 'aria-label': side === 'left' ? 'First currency' : 'Second currency' } }} />}
    />
  )
  return (
    <Box data-rates-card sx={{ border: 1, borderColor: 'divider', borderRadius: '24px', bgcolor: 'background.paper', p: 3 }}>
      <Typography variant="h6" sx={{ mb: 1.5 }}>Exchange rate</Typography>
      <Box sx={{ display: 'flex', alignItems: 'center', gap: 1 }}>
        {pick('left')}
        <Tooltip title={`Show what one ${pair.forward ? pair.right : pair.left} is worth in ${pair.forward ? pair.left : pair.right}`}>
          <IconButton data-rate-direction={pair.forward ? 'forward' : 'back'} aria-label="Turn the rate around" onClick={() => onPair({ ...pair, forward: !pair.forward })}>
            {pair.forward ? <East /> : <West />}
          </IconButton>
        </Tooltip>
        {pick('right')}
      </Box>
      <Typography data-rate-of-day className="tnum" sx={{ mt: 2, fontSize: 20, fontWeight: 650, letterSpacing: '-0.01em' }}>
        {from === to ? `1 ${from} = 1 ${to}` : r ? `1 ${from} = ${rateText(r.rate)} ${to}` : 'No rate for this day'}
      </Typography>
      <Typography variant="body2" color="text.secondary" sx={{ mt: 0.5 }}>
        {r && r.rateDay !== r.day ? `Rate published on ${formatDate(r.rateDay, settings.dateFormat)}. ` : ''}{source}
      </Typography>
    </Box>
  )
}

/**
 * Month calendar of every bank together, in the global display currency: what went out and came in each day; a day
 * opens its operations (an amount converted from another currency shows the amount the bank wrote). The holidays of
 * the checked calendars (Definitions > Holidays) are written on their day.
 */
export function CalendarPage() {
  const { settings, setSetting, dataVersion, fail, category } = useApp()
  const colorOf = useCategoryColor()
  // Settings: transfers and card statement payments hidden by default, investments shown.
  const hideMoves = settings.calendarHideTransfers === 'true'
  const hideCard = settings.calendarHideCardPayments === 'true'
  const hideInvest = settings.calendarHideInvestments === 'true'
  const hidden = (id: string) => category(id).kind === 'transfer'
    && (id === 'cardpayments' ? hideCard : id === 'investments' ? hideInvest : hideMoves)
  const theme = useTheme()
  const dark = theme.palette.mode === 'dark'
  const [month, setMonth] = useState(() => { const t = parts(today()); return { y: t.y, m: t.m } })
  const [summary, setSummary] = useState<PeriodSummary | null>(null)
  const [holidays, setHolidays] = useState<Holiday[]>([])
  const [day, setDay] = useState<string>(today())
  const [editing, setEditing] = useState<TransactionView | null>(null)
  const [adding, setAdding] = useState(false)
  const currency = settings.currency
  // The exchange rate of two currencies, day by day, with the rates of the central bank chosen in Settings.
  const pair = parsePair(settings.calendarRates, currency)
  const [currencies, setCurrencies] = useState<string[]>([])
  const [rates, setRates] = useState<Map<string, RateUse>>(new Map())
  const [source, setSource] = useState('')
  const [rateFrom, rateTo] = pair.forward ? [pair.left, pair.right] : [pair.right, pair.left]
  useEffect(() => {
    api.rates.currencies().then(setCurrencies, fail)
    api.rates.status().then((s) => setSource(s.source ? `Rates of ${s.source}.` : ''), fail)
  }, [dataVersion, fail])
  useEffect(() => {
    const from = iso(month.y, month.m, 1)
    const to = iso(month.y, month.m, daysInMonth(month.y, month.m))
    if (rateFrom === rateTo) { setRates(new Map()); return }
    api.rates.pair(rateFrom, rateTo, addDays(from, -7), addDays(to, 7)).then((list) => setRates(new Map(list.map((r) => [r.day, r]))), () => setRates(new Map()))
  }, [month, rateFrom, rateTo, dataVersion])
  const setPair = (p: { left: string; right: string; forward: boolean }) => void setSetting('calendarRates', `${p.left}${p.forward ? '>' : '<'}${p.right}`)

  // Start on the latest month that has data, in any bank.
  useEffect(() => {
    api.periods.index().then((ix) => {
      if (ix.last) {
        const [y, m] = ix.last.split('-').map(Number)
        const t = parts(today())
        if (y * 12 + m < t.y * 12 + t.m) { setMonth({ y, m }); setDay(iso(y, m, 1)) }
      }
    }, fail)
  }, [fail])

  useEffect(() => {
    const from = iso(month.y, month.m, 1)
    const to = iso(month.y, month.m, daysInMonth(month.y, month.m))
    api.periods.report(from, to, 'month').then((r) => {
      setSummary({ ...r.global, transactions: globalTransactions(r) })
    }, fail)
    // The weeks shown run into the months before and after.
    api.holidays.list(addDays(from, -7), addDays(to, 7)).then(setHolidays, fail)
  }, [month, currency, dataVersion, fail])

  // The same holiday in several checked calendars (Christmas Day in Canada and in Québec): written once, with its
  // places. The day off given for a holiday on a weekend is written "(observed)".
  const merged = useMemo(() => {
    const byKey = new Map<string, { day: string; name: string; locations: string[] }>()
    for (const h of holidays) {
      const name = h.observed ? `${h.name} (observed)` : h.name
      const key = `${h.day}|${name.toLowerCase()}`
      const known = byKey.get(key)
      if (known) known.locations.push(h.location)
      else byKey.set(key, { day: h.day, name, locations: [h.location] })
    }
    return [...byKey.values()]
  }, [holidays])
  // The weekend days chosen in Definitions > Holidays (ISO numbers, Monday 1 to Sunday 7), shaded.
  const weekend = useMemo(() => new Set((settings.weekendDays ?? '6,7').split(',').filter(Boolean).map(Number)), [settings.weekendDays])
  const isWeekend = (dow: number) => weekend.has(dow === 0 ? 7 : dow)
  const events = useMemo(() => {
    // Holidays first in their day, then what came in, went out and moved.
    const off = merged.map((h, i) => ({ id: `h${i}`, start: h.day, allDay: true, title: h.name, order: 0, extendedProps: { kind: 'holiday', location: h.locations.join(', ') } }))
    // The exchange rate of the day, last.
    const rated = [...rates.values()].map((r) => ({ id: 'r' + r.day, start: r.day, allDay: true, title: `${r.rate.toLocaleString('en-CA', { minimumFractionDigits: 4, maximumFractionDigits: 4 })} ${rateTo}`, order: 2, extendedProps: { kind: 'rate' } }))
    if (!summary) return [...off, ...rated]
    return [...off, ...rated, ...summary.series.flatMap((b) => {
      const moved = (hideMoves ? 0 : b.moved) + (hideCard ? 0 : b.cardPaid) + (hideInvest ? 0 : b.invested)
      return [
        ...(b.earnings > 0 ? [{ id: 'i' + b.key, start: b.key, allDay: true, title: money(b.earnings, currency, { sign: true }), extendedProps: { kind: 'in' } }] : []),
        ...(b.expenses > 0 ? [{ id: 'e' + b.key, start: b.key, allDay: true, title: money(-b.expenses, currency), extendedProps: { kind: 'out' } }] : []),
        ...(moved > 0 ? [{ id: 'm' + b.key, start: b.key, allDay: true, title: '⇄ ' + money(moved, currency), extendedProps: { kind: 'move' } }] : []),
      ].map((e) => ({ ...e, order: 1 }))
    })]
  }, [summary, merged, currency, hideCard, hideMoves, hideInvest, rates, rateTo])
  const holidayDays = useMemo(() => new Set(holidays.map((h) => h.day)), [holidays])
  const dayHolidays = merged.filter((h) => h.day === day)

  const dayTx = summary?.transactions.filter((t) => t.date === day && !hidden(t.categoryId)) ?? []
  const brands = useMemo(() => [...new Set(summary?.transactions.map((t) => t.brand) ?? [])], [summary])

  const shift = (delta: number) => {
    const n = month.y * 12 + (month.m - 1) + delta
    const y = Math.floor(n / 12)
    const m = (n % 12) + 1
    setMonth({ y, m })
    setDay(iso(y, m, 1))
  }

  const d = parts(day)
  const dayTitle = new Date(Date.UTC(d.y, d.m - 1, d.d)).toLocaleDateString('en-US', { weekday: 'long', month: 'long', day: 'numeric', timeZone: 'UTC' })

  const vars = {
    '--fc-classic-background': theme.palette.background.paper,
    '--fc-classic-border': theme.palette.divider,
    '--fc-classic-strong-border': theme.palette.divider,
    '--fc-classic-foreground': theme.palette.text.primary,
    '--fc-classic-muted-foreground': theme.palette.text.secondary,
    '--fc-classic-faint-foreground': theme.palette.text.disabled,
    '--fc-classic-today': dark ? 'rgba(106,165,236,.10)' : 'rgba(36,104,192,.06)',
    '--fc-classic-highlight': dark ? 'rgba(106,165,236,.16)' : 'rgba(36,104,192,.08)',
    '--fc-classic-faint': dark ? 'rgba(255,255,255,.03)' : 'rgba(0,0,0,.02)',
    '--fc-classic-muted': dark ? 'rgba(255,255,255,.05)' : 'rgba(0,0,0,.04)',
    '--fc-classic-event': 'transparent',
    '--fc-classic-event-contrast': theme.palette.text.primary,
  } as React.CSSProperties

  return (
    <Box sx={{ maxWidth: 1480, mx: 'auto', pt: 3, pb: 6, display: 'flex', gap: 3, alignItems: 'flex-start' }}>
      <Box sx={{ flex: 1, minWidth: 0, border: 1, borderColor: 'divider', borderRadius: '24px', bgcolor: 'background.paper', p: 3 }}>
        <Box sx={{ display: 'flex', alignItems: 'center', gap: 1, mb: 2 }}>
          <Typography variant="h5" sx={{ flex: 1 }}>{monthName(month.m)} {month.y}</Typography>
          {/* The month's totals, every bank together in the display currency. */}
          {summary && (
            <Box data-month-totals sx={{ display: 'flex', alignItems: 'center', gap: 1, mr: 1.5 }}>
              <MonthTotal label="Earnings" value={money(summary.earnings, currency)} bg={theme.palette.money.incomeSoft} />
              <MonthTotal label="Expenses" value={money(summary.expenses, currency)} bg={theme.palette.money.expenseSoft} />
              <MonthTotal label="Balance" value={money(summary.balance, currency, { sign: true })}
                color={summary.balance > 0 ? theme.palette.money.positive : summary.balance < 0 ? theme.palette.money.negative : theme.palette.money.zero}
                bg={summary.balance === 0 ? theme.palette.surface.sunken : summary.balance > 0 ? theme.palette.money.positiveStrong : theme.palette.money.negativeStrong} />
            </Box>
          )}
          <Button size="small" variant="outlined" onClick={() => { const t = parts(today()); setMonth({ y: t.y, m: t.m }); setDay(today()) }}>Today</Button>
          <IconButton aria-label="Previous month" onClick={() => shift(-1)}><ChevronLeft /></IconButton>
          <IconButton aria-label="Next month" onClick={() => shift(1)}><ChevronRight /></IconButton>
        </Box>
        <Box
          data-color-scheme={dark ? 'dark' : 'light'}
          style={vars}
          sx={{
            '& .fc': { fontFamily: 'inherit', fontSize: 13 },
            '& .fc *': { borderColor: `${theme.palette.divider} !important` },
          }}
        >
          <FullCalendar
            key={`${month.y}-${month.m}`}
            plugins={[dayGridPlugin, interactionPlugin, classicThemePlugin]}
            initialView="dayGridMonth"
            initialDate={iso(month.y, month.m, 1)}
            headerToolbar={false}
            firstDay={Number(settings.calendarFirstDay ?? 0)}
            fixedWeekCount={false}
            height="auto"
            events={events}
            eventDisplay="block"
            eventOrder="order,title"
            dayMaxEvents={false}
            dateClick={(info) => setDay(info.dateStr.slice(0, 10))}
            eventClick={(info) => { const s = info.event.startStr; if (s) setDay(s.slice(0, 10)) }}
            dayCellClass={(arg) => {
              const d = arg.date && iso(arg.date.getFullYear(), arg.date.getMonth() + 1, arg.date.getDate())
              return [d === day ? 'selected-day' : '', d && holidayDays.has(d) ? 'holiday-day' : '', isWeekend(arg.dow) ? 'weekend-day' : ''].filter(Boolean).join(' ')
            }}
            dayHeaderClass={(arg) => (isWeekend(arg.dow) ? 'weekend-head' : '')}
            eventContent={(arg) => arg.event.extendedProps.kind === 'holiday' ? (
              <Box
                data-holiday={arg.event.startStr.slice(0, 10)}
                title={`${arg.event.title} · ${arg.event.extendedProps.location}`}
                sx={{ width: '100%', px: 0.75, py: 0.2, borderRadius: 1.25, fontSize: 11.5, fontWeight: 600, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', color: 'primary.main', bgcolor: dark ? 'rgba(106,165,236,.14)' : 'rgba(36,104,192,.08)' }}
              >
                {arg.event.title}
              </Box>
            ) : arg.event.extendedProps.kind === 'rate' ? (
              <Box data-day-rate={arg.event.startStr.slice(0, 10)} className="tnum" sx={{ width: '100%', px: 0.75, fontSize: 11, fontWeight: 520, textAlign: 'right', color: 'text.secondary' }}>
                {arg.event.title}
              </Box>
            ) : (
              <Box
                className="tnum"
                sx={{
                  width: '100%', px: 0.75, py: 0.2, borderRadius: 1.25, fontSize: 12, fontWeight: 600, textAlign: 'right',
                  // Income in green, spending in red, money moved between own accounts or invested in black.
                  color: arg.event.extendedProps.kind === 'in' ? theme.palette.money.positive
                    : arg.event.extendedProps.kind === 'out' ? theme.palette.money.negative
                    : theme.palette.money.zero,
                  bgcolor: arg.event.extendedProps.kind === 'in' ? (dark ? 'rgba(76,194,106,.12)' : 'rgba(0,99,0,.07)')
                    : arg.event.extendedProps.kind === 'out' ? (dark ? 'rgba(239,107,107,.12)' : 'rgba(208,59,59,.07)')
                    : (dark ? 'rgba(255,255,255,.06)' : 'rgba(0,0,0,.045)'),
                }}
              >
                {arg.event.title}
              </Box>
            )}
          />
        </Box>
      </Box>

      <Box sx={{ width: 360, flex: '0 0 360px', position: 'sticky', top: 24, display: 'grid', gap: 3 }}>
      <Box sx={{ border: 1, borderColor: 'divider', borderRadius: '24px', bgcolor: 'background.paper', p: 3 }}>
        <Box sx={{ display: 'flex', alignItems: 'center', mb: 0.5 }}>
          <Typography variant="h6" sx={{ flex: 1 }}>{dayTitle}</Typography>
          <IconButton aria-label="Add operation" onClick={() => setAdding(true)}><Add /></IconButton>
        </Box>
        {dayHolidays.map((h) => (
          <Typography key={h.name} data-day-holiday variant="body2" sx={{ color: 'primary.main', fontWeight: 600 }}>
            {h.name} <Box component="span" sx={{ color: 'text.secondary', fontWeight: 450 }}>· {h.locations.join(', ')}</Box>
          </Typography>
        ))}
        {dayTx.length === 0 ? <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>No operations</Typography> : <Box sx={{ mb: 1.5 }} />}
        <Box sx={{ display: 'grid', gap: 0.5 }}>
          {dayTx.map((t) => {
            const c = category(t.categoryId)
            const color = colorOf(c)
            return (
              <Box
                key={t.id}
                onClick={() => { if (t.originalCurrency) api.tx.get(t.id).then(setEditing, fail); else setEditing(t) }}
                sx={{ display: 'flex', alignItems: 'center', gap: 1.5, p: 1, mx: -1, borderRadius: 2.5, cursor: 'pointer', '&:hover': { bgcolor: 'action.hover' } }}
              >
                <Box sx={{ width: 36, height: 36, borderRadius: '12px', bgcolor: color ? `${color}22` : 'action.hover', display: 'grid', placeItems: 'center', flex: '0 0 36px' }}>
                  <CategoryIcon name={c.icon} sx={{ fontSize: 19, color: color ?? 'text.secondary' }} />
                </Box>
                <Box sx={{ flex: 1, minWidth: 0 }}>
                  <Typography variant="body2" noWrap sx={{ fontWeight: 560 }}>{t.brand}</Typography>
                  <Typography variant="caption" color="text.secondary" noWrap component="div">{c.name}</Typography>
                  {t.original != null && t.originalCurrency && (
                    <Typography data-original variant="caption" color="text.secondary" noWrap component="div" className="tnum">
                      {moneyBeside(t.original, t.originalCurrency, currency, { sign: true })} at {t.rate}
                    </Typography>
                  )}
                </Box>
                <Money
                  value={t.amount}
                  currency={currency}
                  sign
                  style={{ flexShrink: 0, color: c.kind === 'transfer' ? theme.palette.money.zero : t.amount < 0 ? theme.palette.money.negative : theme.palette.money.positive }}
                />
              </Box>
            )
          })}
        </Box>
      </Box>
      <RatesCard pair={pair} currencies={currencies} onPair={setPair} rates={rates} day={day} source={source} />
      </Box>

      <TransactionDialog open={editing !== null || adding} tx={editing} brands={brands} defaultDate={day} onClose={() => { setEditing(null); setAdding(false) }} />
      <style>{`.selected-day { box-shadow: inset 0 0 0 2px ${theme.palette.primary.main}; }
        .weekend-day { background: ${dark ? 'rgba(255,255,255,.035)' : 'rgba(0,0,0,.028)'}; }
        .weekend-head { color: ${theme.palette.text.secondary}; }
        .holiday-day { background: ${dark ? 'rgba(106,165,236,.07)' : 'rgba(36,104,192,.045)'}; }`}</style>
    </Box>
  )
}
