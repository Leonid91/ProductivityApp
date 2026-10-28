import { useEffect, useMemo, useState } from 'react'
import { Box, Button, IconButton, Typography, useTheme } from '@mui/material'
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
import { api, type PeriodSummary, type TransactionView } from '../api'
import { useApp } from '../AppContext'
import { daysInMonth, iso, money, monthName, parts, today } from '../format'
import { CategoryIcon } from '../components/CategoryIcon'
import { Money } from '../components/common'
import { TransactionDialog } from '../components/TransactionDialog'

/** Month calendar: what went out and came in each day; a day opens its operations. */
export function CalendarPage() {
  const { settings, dataVersion, fail, category } = useApp()
  const theme = useTheme()
  const dark = theme.palette.mode === 'dark'
  const [month, setMonth] = useState(() => { const t = parts(today()); return { y: t.y, m: t.m } })
  const [summary, setSummary] = useState<PeriodSummary | null>(null)
  const [day, setDay] = useState<string>(today())
  const [editing, setEditing] = useState<TransactionView | null>(null)
  const [adding, setAdding] = useState(false)
  const currency = settings.currency

  // Start on the latest month that has data.
  useEffect(() => {
    api.periods.index(currency).then((ix) => {
      if (ix.last) {
        const [y, m] = ix.last.split('-').map(Number)
        const t = parts(today())
        if (y * 12 + m < t.y * 12 + t.m) { setMonth({ y, m }); setDay(iso(y, m, 1)) }
      }
    }, fail)
  }, [currency, fail])

  useEffect(() => {
    const from = iso(month.y, month.m, 1)
    const to = iso(month.y, month.m, daysInMonth(month.y, month.m))
    api.periods.summary(from, to, 'month', currency).then(setSummary, fail)
  }, [month, currency, dataVersion, fail])

  const events = useMemo(() => {
    if (!summary) return []
    return summary.series.flatMap((b) => [
      ...(b.expenses > 0 ? [{ id: 'e' + b.key, start: b.key, allDay: true, title: money(-b.expenses, currency), extendedProps: { kind: 'out' } }] : []),
      ...(b.earnings > 0 ? [{ id: 'i' + b.key, start: b.key, allDay: true, title: money(b.earnings, currency, { sign: true }), extendedProps: { kind: 'in' } }] : []),
    ])
  }, [summary, currency])

  const dayTx = useMemo(() => summary?.transactions.filter((t) => t.date === day) ?? [], [summary, day])
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
  const spent = dayTx.filter((t) => t.amount < 0 && category(t.categoryId).kind !== 'transfer').reduce((s, t) => s - t.amount, 0)

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
    <Box sx={{ maxWidth: 1280, mx: 'auto', pt: 3, pb: 6, display: 'flex', gap: 3, alignItems: 'flex-start' }}>
      <Box sx={{ flex: 1, minWidth: 0, border: 1, borderColor: 'divider', borderRadius: '24px', bgcolor: 'background.paper', p: 3 }}>
        <Box sx={{ display: 'flex', alignItems: 'center', gap: 1, mb: 2 }}>
          <Typography variant="h5" sx={{ flex: 1 }}>{monthName(month.m)} {month.y}</Typography>
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
            firstDay={1}
            fixedWeekCount={false}
            height="auto"
            events={events}
            eventDisplay="block"
            dayMaxEvents={false}
            dateClick={(info) => setDay(info.dateStr.slice(0, 10))}
            eventClick={(info) => { const s = info.event.startStr; if (s) setDay(s.slice(0, 10)) }}
            dayCellClass={(arg) => (arg.date && iso(arg.date.getFullYear(), arg.date.getMonth() + 1, arg.date.getDate()) === day ? 'selected-day' : '')}
            eventContent={(arg) => (
              <Box
                className="tnum"
                sx={{
                  width: '100%', px: 0.75, py: 0.2, borderRadius: 1.25, fontSize: 12, fontWeight: 600, textAlign: 'right',
                  color: arg.event.extendedProps.kind === 'in' ? theme.palette.money.positive : theme.palette.text.primary,
                  bgcolor: arg.event.extendedProps.kind === 'in'
                    ? (dark ? 'rgba(76,194,106,.12)' : 'rgba(0,99,0,.07)')
                    : (dark ? 'rgba(255,255,255,.06)' : 'rgba(0,0,0,.045)'),
                }}
              >
                {arg.event.title}
              </Box>
            )}
          />
        </Box>
      </Box>

      <Box sx={{ width: 360, flex: '0 0 360px', border: 1, borderColor: 'divider', borderRadius: '24px', bgcolor: 'background.paper', p: 3, position: 'sticky', top: 24 }}>
        <Box sx={{ display: 'flex', alignItems: 'center', mb: 0.5 }}>
          <Typography variant="h6" sx={{ flex: 1 }}>{dayTitle}</Typography>
          <IconButton aria-label="Add operation" onClick={() => setAdding(true)}><Add /></IconButton>
        </Box>
        <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }} className="tnum">
          {dayTx.length === 0 ? 'No operations' : `${money(spent, currency)} spent`}
        </Typography>
        <Box sx={{ display: 'grid', gap: 0.5 }}>
          {dayTx.map((t) => {
            const c = category(t.categoryId)
            return (
              <Box
                key={t.id}
                onClick={() => setEditing(t)}
                sx={{ display: 'flex', alignItems: 'center', gap: 1.5, p: 1, mx: -1, borderRadius: 2.5, cursor: 'pointer', '&:hover': { bgcolor: 'action.hover' } }}
              >
                <Box sx={{ width: 36, height: 36, borderRadius: '12px', bgcolor: 'action.hover', display: 'grid', placeItems: 'center', flex: '0 0 36px' }}>
                  <CategoryIcon name={c.icon} sx={{ fontSize: 19, color: 'text.secondary' }} />
                </Box>
                <Box sx={{ flex: 1, minWidth: 0 }}>
                  <Typography variant="body2" noWrap sx={{ fontWeight: 560 }}>{t.brand}</Typography>
                  <Typography variant="caption" color="text.secondary" noWrap component="div">{c.name}</Typography>
                </Box>
                <Money value={t.amount} currency={currency} sign muted={c.kind === 'transfer'} />
              </Box>
            )
          })}
        </Box>
      </Box>

      <TransactionDialog open={editing !== null || adding} tx={editing} brands={brands} defaultDate={day} onClose={() => { setEditing(null); setAdding(false) }} />
      <style>{`.selected-day { box-shadow: inset 0 0 0 2px ${theme.palette.primary.main}; }`}</style>
    </Box>
  )
}
