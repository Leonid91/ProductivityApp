import { useId, useLayoutEffect, useMemo, useRef, useState } from 'react'
import { Box, Dialog, IconButton, Typography, useTheme } from '@mui/material'
import OpenInFull from '@mui/icons-material/OpenInFullRounded'
import Close from '@mui/icons-material/CloseRounded'
import type { TimelineEvent, TimelineLane, TransferTimeline as Timeline } from '../api'
import { useApp } from '../AppContext'
import { formatDate, money, moneyBeside, monthName, parts, plural, useMoney } from '../format'
import { useAmountsHidden } from '../mask'
import { Legend } from './FlowChart'

/**
 * The transfers of a period as streams of money over time (the model comes from the engine,
 * src/Finance.Core/Services/TransferTimeline.cs): one stream per account, the accounts of a bank together in the box of
 * their bank, the outside last, in orange; time from left to right. A stream is as thick as the money transfers have
 * put in it so far: it widens when money comes in and narrows when money leaves. Every transfer is a band as thick as
 * its amount that leaves the stream of its account, in its colour, and ends with an arrow in the stream it reaches
 * (thinner on arrival when the banks kept part of it); inside a bank it stays in the bank's box, between two banks it
 * goes from one box to the other. One scale for everything, so widths compare across streams and transfers. Steps
 * without transfers are narrowed to leave room to those with transfers. Each step with transfers has room for them and
 * their labels: a long period is wider than the page and scrolls sideways, the names of the accounts and what they
 * gained or gave staying in sight on both sides; it can also be opened on the whole screen. The largest transfers are
 * written beside their arrow (as many as fit without covering one another or the streams), every one on hover. On the
 * right, what each account gained or gave over the period.
 */

const LEFT = 196, RIGHT = 128, TOP = 46
const LANE_MIN = 56, LANE_PAD = 28, BANK_HEAD = 28, BANK_PAD = 8, BANK_GAP = 16
const CHIP_H = 52, CHIP_SHORT = 36, MAX_LABELS = 16
/** The width of a step with transfers: room for its bands and for the labels of its largest ones. */
const STEP_BUSY = 150
/** The same when the drawing must fit a given width (the PDF). */
const STEP_FIT = 70

/** How the drawing is laid out: the width it has, fitted into it (no wider), drawn on the whole screen (taller). */
interface Room { width: number; fit?: boolean; big?: boolean }

interface Placed {
  e: TimelineEvent; i: number
  /** The step of the transfer, left to right; the centre of the band where it leaves and where it arrives. */
  x0: number; x1: number; xc: number; y0: number; y1: number
  /** Thickness where it leaves and where it arrives. */
  sent: number; got: number
  path: string; arrow: string
}
/** A label: two lines beside a transfer (when, how much), three when pointed at (and between which accounts). */
interface Chip { x: number; y: number; w: number; h: number; lines: [string, string, string]; cost: number }
type Rect = { x: number; y: number; w: number; h: number }

const short = (m: number) => monthName(m).slice(0, 3)
const WEEKDAYS = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat']
const weekday = (iso: string) => { const { y, m, d } = parts(iso); return WEEKDAYS[new Date(Date.UTC(y, m - 1, d)).getUTCDay()] }
const f1 = (v: number) => v.toFixed(1)

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

/** What reaches the other stream: the amount sent less what the banks kept on the way (as the engine counts it). */
const arrived = (e: TimelineEvent) => Math.max(0, e.amount - (e.lost ?? 0))

/** A band between two heights at x0 and two heights at x1, its edges curved (flat at both ends). */
const band = (x0: number, a0: number, a1: number, x1: number, b0: number, b1: number) => {
  const c = (x0 + x1) / 2
  return `M${f1(x0)},${f1(a0)} C${f1(c)},${f1(a0)} ${f1(c)},${f1(b0)} ${f1(x1)},${f1(b0)} L${f1(x1)},${f1(b1)} C${f1(c)},${f1(b1)} ${f1(c)},${f1(a1)} ${f1(x0)},${f1(a1)} Z`
}

/** Where everything goes: the rows (banks and their streams), the streams, every transfer, the labels that fit. */
export function layoutTimeline(t: Timeline, text: (e: TimelineEvent) => [string, string, string], space: Room = { width: 1000, fit: true }) {
  const n = Math.max(1, t.slots.length)
  const drawn = t.events.filter((e) => e.from !== e.to)
  // One scale for every stream and transfer: the widest moments of all streams share the room given to the drawing.
  const most = t.lanes.map((l) => Math.max(0, l.start, ...l.levels))
  const total = most.reduce((a, b) => a + b, 0)
  const lanes = t.lanes.length
  const room = space.big ? Math.min(900, Math.max(240, 120 * lanes)) : Math.min(560, Math.max(150, 84 * lanes))
  const scale = total > 0 ? room / total : 0
  const level = (lane: number, slot: number) => (slot < 0 ? t.lanes[lane].start : t.lanes[lane].levels[slot] ?? t.lanes[lane].start) * scale

  // Rows: each bank a box with its name, then one row per account, as high as its stream at its widest.
  const laneY: number[] = [], laneH = t.lanes.map((_, i) => Math.max(LANE_MIN, most[i] * scale + LANE_PAD))
  const boxes: { name: string | null; y: number; h: number }[] = []
  let y = TOP
  for (const b of t.banks) {
    let h = BANK_HEAD
    for (let i = b.first; i < b.first + b.count; i++) { laneY[i] = y + h + laneH[i] / 2; h += laneH[i] }
    boxes.push({ name: b.name, y, h: h + BANK_PAD })
    y += h + BANK_PAD + BANK_GAP
  }
  const outside = t.lanes.findIndex((l) => l.outside)
  if (outside >= 0) {
    boxes.push({ name: null, y, h: laneH[outside] + 2 * BANK_PAD })
    laneY[outside] = y + BANK_PAD + laneH[outside] / 2
    y += laneH[outside] + 2 * BANK_PAD + BANK_GAP
  }
  const height = y - BANK_GAP + 8

  // Time: steps with transfers at least STEP_MIN wide, the others share what is left; wider than the room given when
  // they need it (unless it must fit).
  const busy = new Set(drawn.map((e) => e.slot))
  const STEP_MIN = space.fit ? STEP_FIT : STEP_BUSY
  const W = LEFT + Math.max(space.width - LEFT - RIGHT, space.fit ? 0 : busy.size * STEP_MIN) + RIGHT
  const X0 = LEFT, X1 = W - RIGHT
  const even = (X1 - X0) / n
  let quiet = even, wide = even
  if (even < STEP_MIN && busy.size > 0 && busy.size < n) {
    quiet = Math.max(0, Math.min(even, (X1 - X0 - busy.size * STEP_MIN) / (n - busy.size)))
    wide = (X1 - X0 - quiet * (n - busy.size)) / busy.size
  }
  const xs: number[] = []
  for (let s = 0, x = X0; s <= n; s++) { xs.push(s === n ? X1 : x); x += busy.has(s) ? wide : quiet }

  // Each step with transfers: on its left the money of every stream it touches split into what leaves (towards each
  // stream reached, in their order from top to bottom) and what stays; on its right what stays and what arrives.
  const placed: Placed[] = drawn.map((e, i) => ({ e, i, x0: 0, x1: 0, xc: 0, y0: 0, y1: 0, sent: 0, got: 0, path: '', arrow: '' }))
  const stays: { lane: number; path: string; box: Rect }[] = []
  const touched = new Map<number, Set<number>>()
  for (const p of placed) {
    for (const lane of [p.e.from, p.e.to]) {
      const set = touched.get(p.e.slot) ?? new Set<number>()
      set.add(lane)
      touched.set(p.e.slot, set)
    }
  }
  const src = new Map<number, [number, number]>(), dst = new Map<number, [number, number]>()
  for (const [slot, lanes] of touched) {
    const xa = xs[slot], xb = xs[slot + 1]
    for (const lane of lanes) {
      const before = level(lane, slot - 1), after = level(lane, slot)
      const outs = placed.filter((p) => p.e.slot === slot && p.e.from === lane)
      const ins = placed.filter((p) => p.e.slot === slot && p.e.to === lane)
      const stay = Math.max(0, before - outs.reduce((a, p) => a + p.e.amount * scale, 0))
      const stack = (items: { key: number; h: number; p: Placed | null }[], top: number, into: Map<number, [number, number]>) => {
        const at: [number, number] = [top, top]
        for (const it of items.sort((a, b) => a.key - b.key || (a.p ? 1 : 0) - (b.p ? 1 : 0))) {
          const seg: [number, number] = [top, top + it.h]
          top += it.h
          if (it.p) into.set(it.p.i, seg); else at.splice(0, 2, ...seg)
        }
        return at
      }
      const left = stack([{ key: laneY[lane], h: stay, p: null }, ...outs.map((p) => ({ key: laneY[p.e.to], h: p.e.amount * scale, p }))], laneY[lane] - before / 2, src)
      const right = stack([{ key: laneY[lane], h: stay, p: null }, ...ins.map((p) => ({ key: laneY[p.e.from], h: arrived(p.e) * scale, p }))], laneY[lane] - after / 2, dst)
      if (stay > 0.05) stays.push({ lane, path: band(xa, left[0], left[1], xb, right[0], right[1]), box: { x: xa, y: Math.min(left[0], right[0]), w: xb - xa, h: Math.max(left[1], right[1]) - Math.min(left[0], right[0]) } })
    }
  }
  for (const p of placed) {
    const xa = xs[p.e.slot], xb = xs[p.e.slot + 1]
    const [a0, a1] = src.get(p.i)!, [b0, b1] = dst.get(p.i)!
    // Very small amounts stay visible: a band at least a pixel thick, an arrow at least 7 pixels high.
    const [sa, sb] = a1 - a0 < 1.2 ? [(a0 + a1) / 2 - 0.6, (a0 + a1) / 2 + 0.6] : [a0, a1]
    const [da, db] = b1 - b0 < 1.2 ? [(b0 + b1) / 2 - 0.6, (b0 + b1) / 2 + 0.6] : [b0, b1]
    const head = Math.min(10, (xb - xa) * 0.22), end = xb - head, mid = (b0 + b1) / 2, half = Math.max((b1 - b0) / 2, 3.5)
    Object.assign(p, {
      x0: xa, x1: xb, xc: (xa + xb) / 2, y0: (a0 + a1) / 2, y1: mid, sent: a1 - a0, got: b1 - b0,
      path: band(xa, sa, sb, end, da, db),
      arrow: `M${f1(end)},${f1(mid - half)} L${f1(xb)},${f1(mid)} L${f1(end)},${f1(mid + half)} Z`,
    })
  }
  // The streams between the steps where they change: straight, as thick as the money they hold.
  const runs = t.lanes.map((_, lane) => {
    const out: { x: number; w: number; y: number; h: number }[] = []
    let from = X0
    for (let s = 0; s < n; s++) {
      if (!touched.get(s)?.has(lane)) continue
      const h = level(lane, s - 1)
      if (xs[s] > from && h > 0.05) out.push({ x: from, w: xs[s] - from, y: laneY[lane] - h / 2, h })
      from = xs[s + 1]
    }
    const h = level(lane, n - 1)
    if (X1 > from && h > 0.05) out.push({ x: from, w: X1 - from, y: laneY[lane] - h / 2, h })
    return out
  })

  // Labels: the largest amounts first, each beside its arrow where it covers no other label, no arrow and the fewest bands.
  const points = placed.map((p) => Array.from({ length: 9 }, (_, k) => {
    const s = k / 8, u = 1 - s
    return {
      x: u * u * u * p.x0 + 3 * u * u * s * p.xc + 3 * u * s * s * p.xc + s * s * s * p.x1,
      y: u * u * u * p.y0 + 3 * u * u * s * p.y0 + 3 * u * s * s * p.y1 + s * s * s * p.y1,
    }
  }))
  const chipOf = (p: Placed, free: boolean, taken: Rect[]): Chip | null => {
    const lines = text(p.e)
    const h = free ? CHIP_H : CHIP_SHORT
    const w = Math.min(230, Math.max(lines[0].length * 6.2, lines[1].length * 8.4, free ? lines[2].length * 6.2 : 0) + 20)
    lines[2] = fit(lines[2], w - 20, 6.2)
    let best: Chip | null = null
    let bestCost = Infinity
    // Beside the arrow first, then farther from it (a dashed line ties the label to its arrow): the nearest place clear of
    // the transfers and the streams is kept.
    const places: [number, number][] = []
    for (const far of [0, 44, 96, 160]) {
      for (const dy of [-h - 10 - far / 4, 12 + far / 4, -h - 40 - far / 2, 40 + far / 2]) places.push([12 + far, dy], [-w - 12 - far, dy])
      if (far === 0) places.push([-w / 2, -h - 16], [-w / 2, 16])
    }
    for (const [k, [dx, dy]] of places.entries()) {
      const r = { x: Math.min(X1 - w, Math.max(X0, p.x1 + dx)), y: Math.min(height - h - 2, Math.max(2, p.y1 + dy)), w, h }
      if (!free) {
        if (taken.some((o) => overlaps(r, o, 4))) continue
        if (placed.some((q) => q.x1 > r.x - 7 && q.x1 < r.x + r.w + 7 && q.y1 > r.y - 7 && q.y1 < r.y + r.h + 7)) continue
      }
      let cost = k / 100
      for (const line of points) for (const pt of line) if (pt.x > r.x && pt.x < r.x + r.w && pt.y > r.y && pt.y < r.y + r.h) cost++
      // Over the streams themselves: only when nothing else is free (the largest transfer keeps its label).
      for (const lane of runs) for (const u of lane) if (overlaps(r, { x: u.x, y: u.y, w: u.w, h: u.h })) cost += 1
      for (const u of stays) if (overlaps(r, u.box)) cost += 1
      if (cost < bestCost) { bestCost = cost; best = { x: r.x, y: r.y, w, h, lines, cost } }
      if (free) break
    }
    return best
  }
  const chips = new Map<number, Chip>()
  const taken: Rect[] = []
  for (const p of [...placed].sort((a, b) => b.e.amount - a.e.amount || a.i - b.i)) {
    if (chips.size >= MAX_LABELS) break
    const c = chipOf(p, false, taken)
    // A label covering a transfer or a stream is left out (pointing at the transfer writes it), but for the largest.
    if (c && (chips.size === 0 || c.cost < 1)) { chips.set(p.i, c); taken.push({ x: c.x, y: c.y, w: c.w, h: c.h }) }
  }
  return { W, X0, X1, laneY, laneH, boxes, height, xs, busy, scale, most, runs, stays, placed, chips, hoverChip: (p: Placed) => chipOf(p, true, []) }
}

/** The labels of the time axis: the steps with transfers first, then others where there is room; the weekday above the day when wide enough. */
function ticks(t: Timeline, xs: number[], busy: Set<number>): { x: number; top: string; bottom: string }[] {
  const gap = t.unit === 'day' ? 30 : 52
  const centre = (i: number) => (xs[i] + xs[i + 1]) / 2
  const chosen: number[] = []
  const room = (i: number) => chosen.every((j) => Math.abs(centre(i) - centre(j)) >= gap)
  const order = t.slots.map((_, i) => i)
  for (const i of order.filter((i) => busy.has(i))) if (room(i)) chosen.push(i)
  for (const i of order.filter((i) => !busy.has(i))) if (xs[i + 1] - xs[i] > 0.5 && room(i)) chosen.push(i)
  chosen.sort((a, b) => a - b)
  return chosen.map((i, k) => {
    const s = t.slots[i]
    const { y, m, d } = parts(s)
    const prev = k > 0 ? parts(t.slots[chosen[k - 1]]) : null
    const x = centre(i)
    if (t.unit === 'year') return { x, top: String(y), bottom: '' }
    if (t.unit === 'month') return { x, top: short(m), bottom: !prev || prev.y !== y ? String(y) : '' }
    if (t.unit === 'week') return { x, top: `${short(m)} ${d}`, bottom: !prev || prev.y !== y ? String(y) : '' }
    if (xs[i + 1] - xs[i] >= 44) return { x, top: weekday(s), bottom: `${short(m)} ${d}` }
    return { x, top: String(d), bottom: !prev || prev.m !== m ? short(m) : '' }
  })
}

/** A colour mixed with the page's, as a solid colour (bands drawn over one another show no seams). */
function mix(color: string, paper: string, share: number) {
  const rgb = (c: string) => {
    const h = c.replace('#', '')
    const full = h.length === 3 ? h.split('').map((x) => x + x).join('') : h
    return /^[0-9a-f]{6}$/i.test(full) ? [0, 2, 4].map((i) => parseInt(full.slice(i, i + 2), 16)) : null
  }
  const a = rgb(color), b = rgb(paper)
  if (!a || !b) return color
  return '#' + a.map((v, i) => Math.round(v * share + b[i] * (1 - share)).toString(16).padStart(2, '0')).join('')
}

/** The drawing of the transfers over time (see layoutTimeline): as wide as it needs, scrolled sideways when wider than its room. */
function Drawing({ t, currency, fit: fitted = false, big = false }: { t: Timeline; currency: string; fit?: boolean; big?: boolean }) {
  const moneyOf = useMoney()
  const theme = useTheme()
  const { settings } = useApp()
  const [hover, setHover] = useState<number | null>(null)
  // The width it has (followed when the window is resized).
  const box = useRef<HTMLDivElement>(null)
  const [width, setWidth] = useState(1000)
  useLayoutEffect(() => {
    const el = box.current
    if (!el || fitted) return
    setWidth(el.clientWidth)
    const ro = new ResizeObserver(() => setWidth(el.clientWidth))
    ro.observe(el)
    return () => ro.disconnect()
  }, [fitted])
  const dark = theme.palette.mode === 'dark'
  const paper = theme.palette.background.paper
  // The colours of the accounts: those of the charts, less the orange (kept for the outside) and the red next to it last.
  const all = theme.palette.chart.series
  const series = [...all.filter((_, i) => i !== 1 && i !== 7), all[7]]
  const color = (lane: number) => (t.lanes[lane].outside ? theme.palette.outside : series[lane % series.length])
  const stream = (lane: number) => mix(color(lane), paper, dark ? 0.55 : 0.5)
  const full = (l: TimelineLane) => (l.outside ? l.name : l.title ?? (l.bank && !l.name.includes(l.bank) ? `${l.name}, ${l.bank}` : l.name))

  const lay = useMemo(() => layoutTimeline(t, (e) => [
    when(t, e, settings.dateFormat, true) + (e.count > 1 ? ` · ${e.count}` : ''),
    `→ ${moneyOf(e.amount, currency)}`,
    `${t.lanes[e.from].name} → ${t.lanes[e.to].name}`,
  ], { width: fitted ? 1000 : width, fit: fitted, big }), [t, currency, settings.dateFormat, width, fitted, big, moneyOf])
  const { W, X0, X1 } = lay
  // Wider than its room: the names of the accounts on the left and what they gained or gave on the right stay in sight.
  const scrolls = !fitted && W > width + 1

  const title = (e: TimelineEvent) => `${when(t, e, settings.dateFormat)}: ${full(t.lanes[e.from])} → ${full(t.lanes[e.to])}: ${money(e.amount, currency)} (${plural(e.count, 'movement')})`
    + (e.sent != null && e.sentCurrency && e.received != null && e.receivedCurrency
      ? `. ${moneyBeside(e.sent, e.sentCurrency, e.receivedCurrency)} sent, ${moneyBeside(e.received, e.receivedCurrency, e.sentCurrency)} received` : '')
    + (e.lost != null ? `. Lost to exchange and fees: ${money(e.lost, currency)}` : '')

  const chip = (p: Placed, c: Chip, hovered: boolean) => (
    <g key={'c' + p.i} data-timeline-label={p.i} style={{ pointerEvents: 'none', opacity: hover !== null && !hovered ? 0.2 : 1 }}>
      <line x1={p.x1} y1={p.y1} x2={Math.min(c.x + c.w - 8, Math.max(c.x + 8, p.x1))} y2={c.y > p.y1 ? c.y : c.y + c.h} stroke={theme.palette.text.secondary} strokeOpacity={0.7} strokeWidth={1} strokeDasharray="3 3" />
      <rect x={c.x} y={c.y} width={c.w} height={c.h} rx={9} fill={paper} stroke={theme.palette.divider} strokeWidth={1.2} />
      <text x={c.x + 10} y={c.y + 15} fontSize={11} fill={theme.palette.text.secondary} className="tnum">{c.lines[0]}</text>
      <text x={c.x + 10} y={c.y + 31} fontSize={13.5} fontWeight={700} fill={color(p.e.from)} className="tnum">{c.lines[1]}</text>
      {c.h === CHIP_H && <text x={c.x + 10} y={c.y + 45} fontSize={11} fill={theme.palette.text.primary}>{c.lines[2]}</text>}
    </g>
  )
  const hovered = hover === null ? null : lay.placed.find((p) => p.i === hover) ?? null
  const hoveredChip = hovered ? lay.hoverChip(hovered) : null
  const faded = hover !== null
  const boxFill = (name: string | null) => (name === null ? (dark ? '#2a211b' : '#fdf6f1') : theme.palette.surface.sunken)

  // The names of the banks and of their accounts, on the left (a bank's name may run along the top of its box).
  const names = (
    <>
      {lay.boxes.map((b, i) => b.name !== null && <text key={'n' + i} x={14} y={b.y + 18} fontSize={12.5} fontWeight={700} fill={theme.palette.text.secondary}>{fit(b.name, W - 40, 7)}</text>)}
      {t.lanes.map((l, i) => (
        <g key={'l' + l.id}>
          <text x={14} y={lay.laneY[i] - 2} fontSize={13} fontWeight={700} fill={l.outside ? theme.palette.outside : theme.palette.text.primary}>{fit(l.name, LEFT - 26, 7.3)}</text>
          <text x={14} y={lay.laneY[i] + 14} fontSize={11.5} fill={theme.palette.text.secondary}>{fit(l.type, LEFT - 26, 6.3)}</text>
        </g>
      ))}
    </>
  )
  // What each account gained or gave, on the right; the outside is not the person's (its totals are above the drawing).
  const nets = (x: number) => (
    <>
      <text x={x + RIGHT / 2} y={24} fontSize={11.5} fontWeight={650} fill={theme.palette.text.secondary} textAnchor="middle">Net moved</text>
      {t.lanes.map((l, i) => {
        if (l.outside) return null
        const y = lay.laneY[i]
        const net = l.net > 0 ? theme.palette.money.positive : l.net < 0 ? theme.palette.money.negative : theme.palette.text.primary
        return (
          <g key={'v' + l.id}>
            <rect x={x + 12} y={y - 15} width={RIGHT - 24} height={30} rx={9} fill={color(i)} fillOpacity={dark ? 0.2 : 0.14} />
            <text data-timeline-net x={x + RIGHT / 2} y={y + 4.5} fontSize={12.5} fontWeight={700} fill={net} textAnchor="middle" className="tnum">{money(l.net, currency, { sign: true })}</text>
          </g>
        )
      })}
    </>
  )
  // The edge of the boxes of the banks on one side, the drawing scrolling under it.
  const clip = useId()
  const edge = (left: boolean, w: number) => (
    <>
      <clipPath id={clip + (left ? 'l' : 'r')}><rect x={0} y={0} width={w} height={lay.height} /></clipPath>
      <g clipPath={`url(#${clip}${left ? 'l' : 'r'})`}>
        <rect x={0} y={0} width={w} height={lay.height} fill={paper} />
        {lay.boxes.map((b, i) => <rect key={'e' + i} x={left ? 1 : -40} y={b.y} width={w + 39} height={b.h} rx={14} fill={boxFill(b.name)} stroke={theme.palette.divider} />)}
      </g>
    </>
  )

  return (
    <Box ref={box} sx={{ position: 'relative', width: '100%' }}>
      <Box data-timeline-scroll sx={{ overflowX: scrolls ? 'auto' : 'hidden', overflowY: 'hidden' }}>
        <svg data-timeline-scale={lay.scale} viewBox={`0 0 ${W} ${lay.height}`} width={fitted ? undefined : W} height={fitted ? undefined : lay.height}
          style={fitted ? { width: '100%', maxWidth: 1000, height: 'auto', display: 'block', margin: '0 auto', overflow: 'visible' } : { display: 'block', overflow: 'visible' }}>
          {/* The boxes of the banks behind their streams. */}
          {lay.boxes.map((b, i) => (
            <g key={'b' + i} {...(b.name === null ? { 'data-timeline-outside': '' } : { 'data-timeline-bank': b.name })}>
              <rect x={1} y={b.y} width={W - 2} height={b.h} rx={14} fill={boxFill(b.name)} stroke={theme.palette.divider} />
            </g>
          ))}
          {/* Time: a faint line in the middle of every labelled step, the labels above. */}
          {ticks(t, lay.xs, lay.busy).map((k) => (
            <g key={'t' + k.x} data-timeline-tick>
              <line x1={k.x} x2={k.x} y1={TOP - 4} y2={lay.height - 8} stroke={theme.palette.chart.axis} strokeOpacity={0.28} strokeDasharray="2 4" />
              <text x={k.x} y={16} fontSize={11.5} fill={theme.palette.text.secondary} textAnchor="middle">{k.top}</text>
              <text x={k.x} y={31} fontSize={11.5} fill={theme.palette.text.secondary} textAnchor="middle">{k.bottom}</text>
            </g>
          ))}
          {!scrolls && names}
          {!scrolls && nets(X1)}
          {/* One stream per account: its money over the period. */}
          {t.lanes.map((l, i) => (
            <g key={l.id} data-timeline-lane={l.outside ? 'outside' : l.name} data-timeline-most={lay.most[i]} data-timeline-thick={lay.most[i] * lay.scale}>
              <title>{`${full(l)}: ${money(l.in, currency)} received, ${money(l.out, currency)} sent`}</title>
              {/* A thread along the row: the stream stays visible while it holds nothing. */}
              <line x1={X0} x2={X1} y1={lay.laneY[i]} y2={lay.laneY[i]} stroke={color(i)} strokeOpacity={0.55} strokeWidth={1.5} />
              <g data-timeline-stream style={{ opacity: faded ? 0.45 : 1, transition: 'opacity 120ms' }}>
                {lay.runs[i].map((r, k) => <rect key={k} x={r.x} y={r.y} width={r.w} height={r.h} fill={stream(i)} />)}
                {lay.stays.filter((s) => s.lane === i).map((s, k) => <path key={'s' + k} d={s.path} fill={stream(i)} />)}
              </g>
            </g>
          ))}
          {/* The transfers: from the stream left to the stream reached, in the colour of the account the money left. */}
          {lay.placed.map((p) => (
            <g key={'e' + p.i} data-timeline-event={p.e.kind} data-timeline-sent={p.sent} data-timeline-amount={p.e.amount}
              onMouseEnter={() => setHover(p.i)} onMouseLeave={() => setHover(null)}
              style={{ opacity: hover !== null && hover !== p.i ? 0.2 : 1, transition: 'opacity 120ms' }}>
              <title>{title(p.e)}</title>
              <path d={p.path} fill={color(p.e.from)} fillOpacity={0.82} />
              <path data-timeline-arrow d={p.arrow} fill={color(p.e.from)} stroke={paper} strokeWidth={0.8} strokeLinejoin="round" />
              {/* A wider unseen line: thin transfers stay easy to point at. */}
              <path d={`M${f1(p.x0)},${f1(p.y0)} C${f1(p.xc)},${f1(p.y0)} ${f1(p.xc)},${f1(p.y1)} ${f1(p.x1)},${f1(p.y1)}`} fill="none" stroke="transparent" strokeWidth={Math.max(14, p.sent)} />
            </g>
          ))}
          {lay.placed.map((p) => { const c = lay.chips.get(p.i); return c && p.i !== hover ? chip(p, c, false) : null })}
          {hovered && hoveredChip && chip(hovered, hoveredChip, true)}
        </svg>
      </Box>
      {scrolls && (
        <>
          <svg data-timeline-names width={LEFT - 8} height={lay.height} style={{ position: 'absolute', left: 0, top: 0, pointerEvents: 'none', display: 'block', overflow: 'visible' }}>
            {edge(true, LEFT - 8)}
            {names}
          </svg>
          <svg data-timeline-nets width={RIGHT} height={lay.height} style={{ position: 'absolute', right: 0, top: 0, pointerEvents: 'none', display: 'block' }}>
            {edge(false, RIGHT)}
            {nets(0)}
          </svg>
        </>
      )}
    </Box>
  )
}

/**
 * The transfers of a period over time, with their totals; opened on the whole screen at will. `fit`: drawn no wider
 * than its room (the PDF).
 */
export function TransferTimelineChart({ timeline: t, currency, fit = false }: { timeline: Timeline; currency: string; fit?: boolean }) {
  useAmountsHidden()
  const [whole, setWhole] = useState(false)
  const totals = (
    <Box sx={{ display: 'flex', gap: 2.5, flexWrap: 'wrap', mt: 0.75, mb: 1.5 }}>
      <Legend data-total="inside" label="Inside a bank" value={money(t.inside, currency)} />
      <Legend data-total="between" label="Between banks" value={money(t.between, currency)} />
      <Legend data-total="sent" label="Sent outside the app" value={money(t.sentOutside, currency)} />
      <Legend data-total="received" label="Received from outside the app" value={money(t.receivedOutside, currency)} />
      {t.lost != null && <Legend data-total="lost" label="Lost to exchange and fees" value={money(t.lost, currency)} />}
    </Box>
  )
  return (
    <Box data-transfer-timeline sx={{ position: 'relative', border: 1, borderColor: 'divider', borderRadius: '18px', p: 2.5, bgcolor: 'background.paper' }}>
      <Typography variant="subtitle1">Transfers over time</Typography>
      {totals}
      {t.lanes.length === 0
        ? <Typography color="text.secondary" sx={{ py: 5, textAlign: 'center' }}>No transfers in this period</Typography>
        : <Drawing t={t} currency={currency} fit={fit} />}
      {/* After the drawing (the drawing is the first picture of the card), shown at its top right. */}
      {!fit && t.lanes.length > 0 && (
        <IconButton data-timeline-whole aria-label="Full screen" size="small" onClick={() => setWhole(true)} sx={{ position: 'absolute', top: 14, right: 14 }}><OpenInFull fontSize="small" /></IconButton>
      )}
      {whole && (
        <Dialog fullScreen open onClose={() => setWhole(false)}>
          <Box data-timeline-dialog sx={{ p: 3, minHeight: '100%', bgcolor: 'background.paper' }}>
            <Box sx={{ display: 'flex', alignItems: 'center' }}>
              <Typography variant="h6" sx={{ flex: 1 }}>Transfers over time</Typography>
              <IconButton aria-label="Close" onClick={() => setWhole(false)}><Close /></IconButton>
            </Box>
            {totals}
            <Drawing t={t} currency={currency} big />
          </Box>
        </Dialog>
      )}
    </Box>
  )
}
