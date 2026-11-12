import { useMemo, useState } from 'react'
import { Box, Typography, useTheme } from '@mui/material'
import type { TimelineEvent, TimelineLane, TransferTimeline as Timeline } from '../api'
import { useApp } from '../AppContext'
import { formatDate, money, moneyBeside, monthName, parts, plural } from '../format'
import { Legend } from './FlowChart'

/**
 * The transfers of a period over time (the model comes from the engine, src/Finance.Core/Services/TransferTimeline.cs):
 * one coloured line per account, the accounts of a bank together in the box of their bank, the outside last, in orange;
 * time from left to right. Each movement leaves the line of its account at its day, in the colour of that account, as
 * thick as its amount, and ends with a dot on the line it reached: inside a bank it stays in the bank's box, between
 * two banks it goes from one box to the other. The largest movements are written beside their dot (as many as fit
 * without covering one another), every one on hover. On the right, what each account gained or gave over the period.
 */

const W = 1000, LEFT = 196, RIGHT = 128, X0 = LEFT, X1 = W - RIGHT, TOP = 46
const LANE = 46, BAND = 16, BANK_HEAD = 26, BANK_PAD = 6, BANK_GAP = 12
const CHIP_H = 52, MAX_LABELS = 16

interface Placed { e: TimelineEvent; i: number; x0: number; x1: number; xc: number; y0: number; y1: number; width: number; path: string }
interface Chip { x: number; y: number; w: number; lines: [string, string, string] }
type Rect = { x: number; y: number; w: number; h: number }

const short = (m: number) => monthName(m).slice(0, 3)
const WEEKDAYS = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat']
const weekday = (iso: string) => { const { y, m, d } = parts(iso); return WEEKDAYS[new Date(Date.UTC(y, m - 1, d)).getUTCDay()] }

/** The step of an event in words: its day, the week or month it is in, or its year. */
function when(t: Timeline, e: TimelineEvent, dateFormat: string, brief = false): string {
  const start = t.slots[e.slot]
  const { y, m, d } = parts(start)
  if (t.unit === 'year') return String(y)
  if (t.unit === 'month') return brief ? `${short(m)} ${y}` : `${monthName(m)} ${y}`
  if (t.unit === 'week') return brief ? `Week of ${short(m)} ${d}` : `Week of ${formatDate(start, dateFormat)}`
  const day = parts(e.date)
  return brief ? `${short(day.m)} ${day.d}` : formatDate(e.date, dateFormat)
}

/** A name cut with "…" when longer than the room it has. */
function fit(name: string, width: number, charW: number) {
  const max = Math.floor(width / charW)
  return name.length <= max ? name : name.slice(0, Math.max(1, max - 1)).trimEnd() + '…'
}

const overlaps = (a: Rect, b: Rect, pad = 0) => a.x < b.x + b.w + pad && b.x < a.x + a.w + pad && a.y < b.y + b.h + pad && b.y < a.y + a.h + pad

/** Where everything goes: the rows (banks and their lines), the curve of every event, the labels that fit. */
export function layoutTimeline(t: Timeline, text: (e: TimelineEvent) => [string, string, string]) {
  // Rows: each bank a box with its name, then one line per account; the outside in a box of its own.
  const laneY: number[] = []
  const boxes: { name: string | null; y: number; h: number }[] = []
  let y = TOP
  for (const b of t.banks) {
    const h = BANK_HEAD + b.count * LANE + BANK_PAD
    boxes.push({ name: b.name, y, h })
    for (let i = 0; i < b.count; i++) laneY[b.first + i] = y + BANK_HEAD + i * LANE + LANE / 2
    y += h + BANK_GAP
  }
  const outside = t.lanes.findIndex((l) => l.outside)
  if (outside >= 0) {
    boxes.push({ name: null, y, h: LANE + 2 * BANK_PAD })
    laneY[outside] = y + BANK_PAD + LANE / 2
    y += LANE + 2 * BANK_PAD + BANK_GAP
  }
  const height = y - BANK_GAP + 8

  // Time: every step the same width; the events of one step side by side inside it.
  const slotW = (X1 - X0) / Math.max(1, t.slots.length)
  const inSlot = new Map<number, number>()
  const seen = new Map<number, number>()
  const drawn = t.events.filter((e) => e.from !== e.to)
  for (const e of drawn) inSlot.set(e.slot, (inSlot.get(e.slot) ?? 0) + 1)
  const largest = drawn.reduce((m, e) => Math.max(m, e.amount), 0)
  const placed: Placed[] = drawn.map((e, i) => {
    const n = seen.get(e.slot) ?? 0
    seen.set(e.slot, n + 1)
    const xc = X0 + e.slot * slotW + (slotW * (n + 1)) / ((inSlot.get(e.slot) ?? 1) + 1)
    const d = Math.min(22, Math.max(7, slotW * 0.38))
    const x0 = Math.max(X0 + 2, xc - d), x1 = Math.min(X1 - 2, xc + d)
    const y0 = laneY[e.from], y1 = laneY[e.to]
    const width = 3 + 11 * Math.sqrt(largest > 0 ? e.amount / largest : 1)
    return { e, i, x0, x1, xc, y0, y1, width, path: `M${x0.toFixed(1)},${y0} C${xc.toFixed(1)},${y0} ${xc.toFixed(1)},${y1} ${x1.toFixed(1)},${y1}` }
  })

  // Labels: the largest amounts first, each beside its dot where it covers no other label, no dot and the fewest lines.
  const points = placed.map((p) => Array.from({ length: 9 }, (_, k) => {
    const s = k / 8, u = 1 - s
    return {
      x: u * u * u * p.x0 + 3 * u * u * s * p.xc + 3 * u * s * s * p.xc + s * s * s * p.x1,
      y: u * u * u * p.y0 + 3 * u * u * s * p.y0 + 3 * u * s * s * p.y1 + s * s * s * p.y1,
    }
  }))
  const chipOf = (p: Placed, free: boolean, taken: Rect[]): Chip | null => {
    const lines = text(p.e)
    const w = Math.min(230, Math.max(lines[0].length * 6.2, lines[1].length * 8.4, lines[2].length * 6.2) + 20)
    lines[2] = fit(lines[2], w - 20, 6.2)
    let best: Chip | null = null
    let bestCost = Infinity
    // Beside the dot first, then farther from it (a line ties the label to its dot): the nearest place clear of the
    // movements is kept.
    const places: [number, number][] = []
    for (const far of [0, 44, 96, 160]) {
      for (const dy of [-CHIP_H - 10 - far / 4, 12 + far / 4]) places.push([12 + far, dy], [-w - 12 - far, dy])
      if (far === 0) places.push([-w / 2, -CHIP_H - 16], [-w / 2, 16])
    }
    for (const [k, [dx, dy]] of places.entries()) {
      const r = { x: Math.min(X1 - w, Math.max(X0, p.x1 + dx)), y: Math.min(height - CHIP_H - 2, Math.max(2, p.y1 + dy)), w, h: CHIP_H }
      if (!free) {
        if (taken.some((o) => overlaps(r, o, 4))) continue
        if (placed.some((q) => q.x1 > r.x - 7 && q.x1 < r.x + r.w + 7 && q.y1 > r.y - 7 && q.y1 < r.y + r.h + 7)) continue
      }
      let cost = k / 100
      for (const line of points) for (const pt of line) if (pt.x > r.x && pt.x < r.x + r.w && pt.y > r.y && pt.y < r.y + r.h) cost++
      if (cost < bestCost) { bestCost = cost; best = { x: r.x, y: r.y, w, lines } }
      if (free) break
    }
    return best
  }
  const chips = new Map<number, Chip>()
  const taken: Rect[] = []
  for (const p of [...placed].sort((a, b) => b.e.amount - a.e.amount || a.i - b.i)) {
    if (chips.size >= MAX_LABELS) break
    const c = chipOf(p, false, taken)
    if (c) { chips.set(p.i, c); taken.push({ x: c.x, y: c.y, w: c.w, h: CHIP_H }) }
  }
  return { laneY, boxes, height, slotW, placed, chips, hoverChip: (p: Placed) => chips.get(p.i) ?? chipOf(p, true, []) }
}

/** The labels of the time axis: as many as have room, the weekday above the day when the steps are wide enough. */
function ticks(t: Timeline, slotW: number): { x: number; top: string; bottom: string }[] {
  const every = Math.max(1, Math.ceil((t.unit === 'day' ? 30 : 52) / slotW))
  const out: { x: number; top: string; bottom: string }[] = []
  t.slots.forEach((s, i) => {
    if (i % every !== 0) return
    const { y, m, d } = parts(s)
    const x = X0 + (i + 0.5) * slotW
    const prev = i >= every ? parts(t.slots[i - every]) : null
    if (t.unit === 'year') out.push({ x, top: String(y), bottom: '' })
    else if (t.unit === 'month') out.push({ x, top: short(m), bottom: !prev || prev.y !== y ? String(y) : '' })
    else if (t.unit === 'week') out.push({ x, top: `${short(m)} ${d}`, bottom: !prev || prev.y !== y ? String(y) : '' })
    else if (slotW >= 44) out.push({ x, top: weekday(s), bottom: `${short(m)} ${d}` })
    else out.push({ x, top: String(d), bottom: !prev || prev.m !== m ? short(m) : '' })
  })
  return out
}

export function TransferTimelineChart({ timeline: t, currency }: { timeline: Timeline; currency: string }) {
  const theme = useTheme()
  const { settings } = useApp()
  const [hover, setHover] = useState<number | null>(null)
  const dark = theme.palette.mode === 'dark'
  const paper = theme.palette.background.paper
  // The colours of the accounts: those of the charts, less the orange (kept for the outside) and the red next to it last.
  const all = theme.palette.chart.series
  const series = [...all.filter((_, i) => i !== 1 && i !== 7), all[7]]
  const color = (lane: number) => (t.lanes[lane].outside ? theme.palette.outside : series[lane % series.length])
  const full = (l: TimelineLane) => (l.outside ? l.name : l.title ?? (l.bank && !l.name.includes(l.bank) ? `${l.name}, ${l.bank}` : l.name))

  const lay = useMemo(() => layoutTimeline(t, (e) => [
    when(t, e, settings.dateFormat, true) + (e.count > 1 ? ` · ${e.count}` : ''),
    `→ ${money(e.amount, currency)}`,
    `${t.lanes[e.from].name} → ${t.lanes[e.to].name}`,
  ]), [t, currency, settings.dateFormat])

  const title = (e: TimelineEvent) => `${when(t, e, settings.dateFormat)}: ${full(t.lanes[e.from])} → ${full(t.lanes[e.to])}: ${money(e.amount, currency)} (${plural(e.count, 'movement')})`
    + (e.sent != null && e.sentCurrency && e.received != null && e.receivedCurrency
      ? `. ${moneyBeside(e.sent, e.sentCurrency, e.receivedCurrency)} sent, ${moneyBeside(e.received, e.receivedCurrency, e.sentCurrency)} received` : '')
    + (e.lost != null ? `. Lost to exchange and fees: ${money(e.lost, currency)}` : '')

  const chip = (p: Placed, c: Chip, hovered: boolean) => (
    <g key={'c' + p.i} data-timeline-label={p.i} style={{ pointerEvents: 'none', opacity: hover !== null && !hovered ? 0.2 : 1 }}>
      <line x1={p.x1} y1={p.y1} x2={Math.min(c.x + c.w - 8, Math.max(c.x + 8, p.x1))} y2={c.y > p.y1 ? c.y : c.y + CHIP_H} stroke={color(p.e.from)} strokeWidth={1.2} />
      <rect x={c.x} y={c.y} width={c.w} height={CHIP_H} rx={9} fill={paper} stroke={theme.palette.divider} strokeWidth={1.2} />
      <text x={c.x + 10} y={c.y + 15} fontSize={11} fill={theme.palette.text.secondary} className="tnum">{c.lines[0]}</text>
      <text x={c.x + 10} y={c.y + 31} fontSize={13.5} fontWeight={700} fill={color(p.e.from)} className="tnum">{c.lines[1]}</text>
      <text x={c.x + 10} y={c.y + 45} fontSize={11} fill={theme.palette.text.primary}>{c.lines[2]}</text>
    </g>
  )
  const hovered = hover === null ? null : lay.placed.find((p) => p.i === hover) ?? null
  const hoveredChip = hovered ? lay.hoverChip(hovered) : null

  return (
    <Box data-transfer-timeline sx={{ border: 1, borderColor: 'divider', borderRadius: '18px', p: 2.5, bgcolor: 'background.paper' }}>
      <Typography variant="subtitle1">Transfers over time</Typography>
      <Box sx={{ display: 'flex', gap: 2.5, flexWrap: 'wrap', mt: 0.75, mb: 1.5 }}>
        <Legend data-total="inside" label="Inside a bank" value={money(t.inside, currency)} />
        <Legend data-total="between" label="Between banks" value={money(t.between, currency)} />
        <Legend data-total="sent" label="Sent outside the app" value={money(t.sentOutside, currency)} />
        <Legend data-total="received" label="Received from outside the app" value={money(t.receivedOutside, currency)} />
        {t.lost != null && <Legend data-total="lost" label="Lost to exchange and fees" value={money(t.lost, currency)} />}
      </Box>
      {t.lanes.length === 0 ? (
        <Typography color="text.secondary" sx={{ py: 5, textAlign: 'center' }}>No transfers in this period</Typography>
      ) : (
        <Box component="svg" viewBox={`0 0 ${W} ${lay.height}`} sx={{ width: '100%', maxWidth: 1000, height: 'auto', display: 'block', mx: 'auto', overflow: 'visible' }}>
          {/* The boxes of the banks behind their lines. */}
          {lay.boxes.map((b, i) => (
            <g key={'b' + i} {...(b.name === null ? { 'data-timeline-outside': '' } : { 'data-timeline-bank': b.name })}>
              <rect x={1} y={b.y} width={W - 2} height={b.h} rx={14} fill={b.name === null ? (dark ? '#2a211b' : '#fdf6f1') : theme.palette.surface.sunken} stroke={theme.palette.divider} />
              {b.name !== null && <text x={14} y={b.y + 18} fontSize={12.5} fontWeight={700} fill={theme.palette.text.secondary}>{fit(b.name, W - 40, 7)}</text>}
            </g>
          ))}
          {/* Time: a faint line at every step (when wide enough to tell them apart), the labels above. */}
          {lay.slotW >= 9 && t.slots.map((_, i) => i > 0 && (
            <line key={'g' + i} x1={X0 + i * lay.slotW} x2={X0 + i * lay.slotW} y1={TOP - 4} y2={lay.height - 8} stroke={theme.palette.chart.axis} strokeOpacity={0.28} strokeDasharray="2 4" />
          ))}
          {ticks(t, lay.slotW).map((k) => (
            <g key={'t' + k.x} data-timeline-tick>
              <text x={k.x} y={16} fontSize={11.5} fill={theme.palette.text.secondary} textAnchor="middle">{k.top}</text>
              <text x={k.x} y={31} fontSize={11.5} fill={theme.palette.text.secondary} textAnchor="middle">{k.bottom}</text>
            </g>
          ))}
          <text x={W - RIGHT / 2} y={24} fontSize={11.5} fontWeight={650} fill={theme.palette.text.secondary} textAnchor="middle">Net moved</text>
          {/* One line per account: its name and type, its coloured band across the period, what it gained or gave. */}
          {t.lanes.map((l, i) => {
            const y = lay.laneY[i]
            const net = l.net > 0 ? theme.palette.money.positive : l.net < 0 ? theme.palette.money.negative : theme.palette.text.primary
            return (
              <g key={l.id} data-timeline-lane={l.outside ? 'outside' : l.name}>
                <title>{`${full(l)}: ${money(l.in, currency)} received, ${money(l.out, currency)} sent`}</title>
                <text x={14} y={y - 2} fontSize={13} fontWeight={700} fill={l.outside ? theme.palette.outside : theme.palette.text.primary}>{fit(l.name, LEFT - 26, 7.3)}</text>
                <text x={14} y={y + 14} fontSize={11.5} fill={theme.palette.text.secondary}>{fit(l.type, LEFT - 26, 6.3)}</text>
                <rect x={X0} y={y - BAND / 2} width={X1 - X0} height={BAND} rx={BAND / 2} fill={color(i)} fillOpacity={dark ? 0.3 : 0.24} />
                {!l.outside && <rect x={X1 + 12} y={y - 15} width={RIGHT - 24} height={30} rx={9} fill={color(i)} fillOpacity={dark ? 0.2 : 0.14} />}
                {/* What an account gained or gave; the outside is not the person's (its totals are above the drawing). */}
                {!l.outside && <text data-timeline-net x={W - RIGHT / 2} y={y + 4.5} fontSize={12.5} fontWeight={700} fill={net} textAnchor="middle" className="tnum">{money(l.net, currency, { sign: true })}</text>}
              </g>
            )
          })}
          {/* The movements: from the line left to the line reached, in the colour of the account the money left. */}
          {lay.placed.map((p) => (
            <g key={'e' + p.i} data-timeline-event={p.e.kind} onMouseEnter={() => setHover(p.i)} onMouseLeave={() => setHover(null)} style={{ opacity: hover !== null && hover !== p.i ? 0.2 : 1, transition: 'opacity 120ms' }}>
              <title>{title(p.e)}</title>
              <path d={p.path} fill="none" stroke={color(p.e.from)} strokeOpacity={0.8} strokeWidth={p.width} strokeLinecap="round" />
              {/* A wider unseen line: thin movements stay easy to point at. */}
              <path d={p.path} fill="none" stroke="transparent" strokeWidth={Math.max(14, p.width)} />
              <circle cx={p.x0} cy={p.y0} r={2.5} fill={paper} stroke={color(p.e.from)} strokeWidth={1.5} />
              <circle cx={p.x1} cy={p.y1} r={5.5} fill={color(p.e.from)} stroke={paper} strokeWidth={2} />
            </g>
          ))}
          {lay.placed.map((p) => { const c = lay.chips.get(p.i); return c && p.i !== hover ? chip(p, c, false) : null })}
          {hovered && hoveredChip && chip(hovered, hoveredChip, true)}
        </Box>
      )}
    </Box>
  )
}
