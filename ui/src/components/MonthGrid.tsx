import { memo, useLayoutEffect, useRef, useState, type CSSProperties, type ReactNode } from 'react'
import { Box, useTheme } from '@mui/material'
import { iso, parts } from '../format'

/** One line written in a day: ordered by `order`, then by its text. */
export interface DayEntry { key: string; day: string; order: number; text: string; content: ReactNode }

const DAY_MS = 86_400_000
const dayOf = (t: number) => { const d = new Date(t); return iso(d.getUTCFullYear(), d.getUTCMonth() + 1, d.getUTCDate()) }
// The formats made once: a date written with options makes its format anew at each call, and a month draws 42 of them.
const longFormat = new Intl.DateTimeFormat('en-US', { month: 'long', day: 'numeric', year: 'numeric', timeZone: 'UTC' })
const longDate = (day: string) => { const d = parts(day); return longFormat.format(Date.UTC(d.y, d.m - 1, d.d)) }
// Week day names from a Sunday (4 January 1970).
const weekdays = Object.fromEntries((['short', 'narrow'] as const).map((form) => {
  const f = new Intl.DateTimeFormat('en-US', { weekday: form, timeZone: 'UTC' })
  return [form, Array.from({ length: 7 }, (_, dow) => f.format(Date.UTC(1970, 0, 4 + dow)))]
})) as Record<'short' | 'narrow', string[]>
const weekday = (dow: number, form: 'short' | 'narrow') => weekdays[form][dow]
const byText = new Intl.Collator()

/**
 * A month as weeks of seven days, from the week day chosen as first: only the weeks the month needs, the days of the
 * months before and after in grey. A row is at least as tall as a sixth of the grid's width at 1.35:1 and grows with
 * what its days hold. Laid out by the browser (flex rows and container queries); the one thing read back is whether the
 * page would fit without its scrollbar (see below), after the grid is drawn: never while the browser reports sizes.
 */
export const MonthGrid = memo(function MonthGrid({ year, month, firstDay, entries, dayClass, headClass, onDay }: {
  year: number
  month: number
  /** 0 Sunday … 6 Saturday. */
  firstDay: number
  entries: DayEntry[]
  dayClass: (day: string, dow: number) => string
  headClass: (dow: number) => string
  onDay: (day: string) => void
}) {
  const theme = useTheme()
  const line = `1px solid ${theme.palette.divider}`
  const first = Date.UTC(year, month - 1, 1)
  const start = first - ((new Date(first).getUTCDay() - firstDay + 7) % 7) * DAY_MS
  const last = Date.UTC(year, month, 0)
  const weeks = Math.ceil(((last - start) / DAY_MS + 1) / 7)
  const byDay = new Map<string, DayEntry[]>()
  for (const e of entries) {
    const list = byDay.get(e.day)
    if (list) list.push(e)
    else byDay.set(e.day, [e])
  }
  for (const list of byDay.values()) list.sort((a, b) => a.order - b.order || byText.compare(a.text, b.text))
  const dows = Array.from({ length: 7 }, (_, i) => (firstDay + i) % 7)

  // A page taller than the window by less than the scrollbar's room gives back: the rows keep the size they have with
  // the scrollbar (a little shorter), so the page fits and the scrollbar goes, instead of staying for a sliver. Read
  // again for another month and when the window is resized.
  const root = useRef<HTMLDivElement>(null)
  const shape = `${year}-${month}-${firstDay}`
  const [kept, setKept] = useState<{ shape: string; width: number } | null>(null)
  const [resized, setResized] = useState(0)
  useLayoutEffect(() => {
    const grid = root.current
    const main = grid?.closest('main')
    if (!grid || !main || kept?.shape === shape) return
    if (main.offsetWidth > main.clientWidth && main.scrollHeight <= main.clientHeight) setKept({ shape, width: grid.clientWidth })
  }, [shape, kept, resized, entries])
  useLayoutEffect(() => {
    const again = () => { setKept(null); setResized((n) => n + 1) }
    window.addEventListener('resize', again)
    return () => window.removeEventListener('resize', again)
  }, [])
  const basis = kept?.shape === shape ? { '--mg-width': `${kept.width}px` } as CSSProperties : undefined

  return (
    <Box
      ref={root}
      role="grid"
      data-month-grid
      style={basis}
      sx={{
        containerType: 'inline-size', border: line, bgcolor: 'background.paper', fontSize: 16, lineHeight: 1.5,
        // Seven columns sharing the width left by their borders.
        '& .mg-head, & .mg-week': { display: 'flex' },
        '& .mg-head > div, & .mg-week > div': { flex: '1 1 0', minWidth: 0 },
        '& .mg-head': { borderBottom: line },
        '& .mg-head > div': { display: 'flex', alignItems: 'center', justifyContent: 'center' },
        '& .mg-label': { m: '2px 4px', fontSize: 14, lineHeight: 1.42857 },
        '& .mg-head .mg-narrow': { display: 'none' },
        '& .mg-head > div + div, & .mg-week > div + div': { borderLeft: line },
        '& .mg-week': { boxSizing: 'border-box', minHeight: 'calc(var(--mg-width, 100cqw) / 8.1)' },
        '& .mg-week:not(:last-child)': { borderBottom: line },
        '& .mg-day': { display: 'flex', flexDirection: 'column', minWidth: 0 },
        '& .mg-num': { flex: 'none', display: 'flex', justifyContent: 'flex-end' },
        '& .mg-num > span': { m: '4px', fontSize: 14, lineHeight: 1.42857 },
        '& .mg-other .mg-num > span': { color: 'text.disabled' },
        '& .mg-lines': { pb: '1px' },
        // A line is a button (Tab reaches it, Enter or Space picks its day), drawn over the line between two days when it
        // is wider than its day.
        '& .mg-line': { position: 'relative', m: '0 2px 1px', border: '1px solid transparent', borderRadius: '4px', p: '1px 0', display: 'flex', alignItems: 'center', minWidth: 0, fontSize: 12, lineHeight: 1.33333, userSelect: 'none', outlineColor: '#3788d8' },
        '& .mg-line:focus-visible': { outlineStyle: 'solid', outlineWidth: 2 },
        // Columns of 80 pixels or less: smaller text and margins; 60 or less: the week days in one letter.
        '@container (max-width: 560px)': {
          '& .mg-label': { fontSize: 11, lineHeight: 1.09091 },
          '& .mg-num > span': { m: '2px 4px', fontSize: 11, lineHeight: 1.09091 },
          '& .mg-line': { m: '0 1px 1px', fontSize: 11, lineHeight: 1.09091 },
        },
        '@container (max-width: 420px)': { '& .mg-head .mg-short': { display: 'none' }, '& .mg-head .mg-narrow': { display: 'inline' } },
      }}
    >
      <div className="mg-head" role="row">
        {dows.map((dow) => (
          <div key={dow} role="columnheader" className={headClass(dow)}>
            <span className="mg-label"><span className="mg-short">{weekday(dow, 'short')}</span><span className="mg-narrow">{weekday(dow, 'narrow')}</span></span>
          </div>
        ))}
      </div>
      {Array.from({ length: weeks }, (_, w) => (
        <div key={w} className="mg-week" role="row">
          {dows.map((dow, i) => {
            const day = dayOf(start + (w * 7 + i) * DAY_MS)
            const d = parts(day)
            const other = d.m !== month
            return (
              <div
                key={day}
                role="gridcell"
                aria-label={longDate(day)}
                data-date={day}
                className={['mg-day', other ? 'mg-other' : '', dayClass(day, dow)].filter(Boolean).join(' ')}
                onClick={() => onDay(day)}
              >
                <div className="mg-num"><span>{d.d}</span></div>
                <div className="mg-lines">
                  {(byDay.get(day) ?? []).map((e) => (
                    <div key={e.key} className="mg-line" role="button" tabIndex={0} onKeyDown={(k) => { if (k.key === 'Enter' || k.key === ' ') { k.preventDefault(); onDay(day) } }}>{e.content}</div>
                  ))}
                </div>
              </div>
            )
          })}
        </div>
      ))}
    </Box>
  )
})
