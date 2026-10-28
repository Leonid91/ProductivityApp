import { useMemo, useState } from 'react'
import { Box, Typography, useTheme } from '@mui/material'
import type { Flow } from '../api'
import { money } from '../format'

const NODE = 8
const GAP = 16
const LABEL = 190

interface Node { name: string; left: boolean; outside: boolean; total: number; y: number; h: number }
interface Band { flow: Flow; y0: number; y1: number; t: number }

/** Same layout as the PDF (src/Finance.Core/Reports/FlowLayout.cs): sources left, destinations right, bands as wide as the money. */
function layout(flows: Flow[], width: number, height: number) {
  const leftX = LABEL + NODE
  const rightX = width - LABEL - NODE
  const group = (key: 'from' | 'to', outsideKey: 'fromOutside' | 'toOutside') => {
    const m = new Map<string, { name: string; outside: boolean; total: number }>()
    for (const f of flows) {
      const n = m.get(f[key]) ?? { name: f[key], outside: f[outsideKey], total: 0 }
      n.total += f.amount
      m.set(f[key], n)
    }
    return [...m.values()].sort((a, b) => Number(a.outside) - Number(b.outside) || b.total - a.total || a.name.localeCompare(b.name))
  }
  const sources = group('from', 'fromOutside')
  const targets = group('to', 'toOutside')
  const total = flows.reduce((s, f) => s + f.amount, 0)
  const count = Math.max(sources.length, targets.length)
  const scale = total > 0 ? Math.max(0, height - 4 - GAP * (count - 1)) / total : 0
  const nodes: Node[] = []
  const leftY = new Map<string, number>()
  const rightY = new Map<string, number>()
  const place = (list: typeof sources, left: boolean) => {
    const used = list.reduce((s, n) => s + Math.max(2, n.total * scale), 0) + GAP * (list.length - 1)
    let y = Math.max(2, (height - used) / 2)
    for (const n of list) {
      const h = Math.max(2, n.total * scale)
      nodes.push({ ...n, left, y, h })
      ;(left ? leftY : rightY).set(n.name, y)
      y += h + GAP
    }
  }
  place(sources, true)
  place(targets, false)
  const si = new Map(sources.map((s, i) => [s.name, i]))
  const ti = new Map(targets.map((t, i) => [t.name, i]))
  const start = new Map<Flow, number>()
  for (const f of [...flows].sort((a, b) => si.get(a.from)! - si.get(b.from)! || ti.get(a.to)! - ti.get(b.to)!)) {
    start.set(f, leftY.get(f.from)!)
    leftY.set(f.from, leftY.get(f.from)! + Math.max(1, f.amount * scale))
  }
  const bands: Band[] = []
  for (const f of [...flows].sort((a, b) => ti.get(a.to)! - ti.get(b.to)! || si.get(a.from)! - si.get(b.from)!)) {
    const t = Math.max(1, f.amount * scale)
    bands.push({ flow: f, y0: start.get(f)!, y1: rightY.get(f.to)!, t })
    rightY.set(f.to, rightY.get(f.to)! + t)
  }
  return { nodes, bands, leftX, rightX }
}

/**
 * Where the money moved over the period: transfers, card payments and investments only (spending always goes out).
 * Blue between the person's own accounts, orange to or from the outside (accounts not imported, other people,
 * investment accounts).
 */
export function FlowChart({ flows, currency }: { flows: Flow[]; currency: string }) {
  const theme = useTheme()
  const [hover, setHover] = useState<Flow | null>(null)
  const width = 1040
  const rows = Math.max(new Set(flows.map((f) => f.from)).size, new Set(flows.map((f) => f.to)).size)
  const height = Math.max(180, rows * 64)
  const { nodes, bands, leftX, rightX } = useMemo(() => layout(flows, width, height), [flows, height])
  const internal = theme.palette.chart.series[0]
  const outside = theme.palette.outside
  const between = flows.filter((f) => !f.fromOutside && !f.toOutside).reduce((s, f) => s + f.amount, 0)
  const out = flows.filter((f) => f.toOutside).reduce((s, f) => s + f.amount, 0)
  const inn = flows.filter((f) => f.fromOutside).reduce((s, f) => s + f.amount, 0)
  const xm = (leftX + rightX) / 2

  return (
    <Box data-flowchart sx={{ border: 1, borderColor: 'divider', borderRadius: '18px', p: 2.5, bgcolor: 'background.paper' }}>
      <Typography variant="subtitle1">Money moved: transfers, card payments & investments</Typography>
      <Box sx={{ display: 'flex', gap: 2.5, flexWrap: 'wrap', mt: 0.75, mb: 1.5 }}>
        <Legend color={internal} label="Between your accounts" value={money(between, currency)} />
        <Legend color={outside} label="Sent outside" value={money(out, currency)} />
        <Legend color={outside} label="Received from outside" value={money(inn, currency)} />
      </Box>
      {flows.length === 0 ? (
        <Typography color="text.secondary" sx={{ py: 5, textAlign: 'center' }}>No transfers in this period</Typography>
      ) : (
        <Box component="svg" viewBox={`0 0 ${width} ${height}`} sx={{ width: '100%', height: 'auto', display: 'block', overflow: 'visible' }}>
          {bands.map((b, i) => {
            const color = b.flow.fromOutside || b.flow.toOutside ? outside : internal
            const on = hover === b.flow
            return (
              <path
                key={i}
                d={`M${leftX} ${b.y0} C${xm} ${b.y0} ${xm} ${b.y1} ${rightX} ${b.y1} L${rightX} ${b.y1 + b.t} C${xm} ${b.y1 + b.t} ${xm} ${b.y0 + b.t} ${leftX} ${b.y0 + b.t} Z`}
                fill={color}
                fillOpacity={hover === null ? 0.3 : on ? 0.55 : 0.12}
                onMouseEnter={() => setHover(b.flow)}
                onMouseLeave={() => setHover(null)}
                style={{ transition: 'fill-opacity 120ms', cursor: 'default' }}
              >
                <title>{`${b.flow.from} → ${b.flow.to}: ${money(b.flow.amount, currency)} (${b.flow.count} movement${b.flow.count === 1 ? '' : 's'})`}</title>
              </path>
            )
          })}
          {/* The amount sits where the band leaves its source: bands never overlap there. */}
          {bands.filter((b) => b.t >= 14).map((b, i) => (
            <text key={'t' + i} x={leftX + 8} y={b.y0 + b.t / 2 + 4} fontSize={12} textAnchor="start" fill={theme.palette.text.secondary} style={{ pointerEvents: 'none' }} className="tnum">
              {money(b.flow.amount, currency)}
            </text>
          ))}
          {nodes.map((n) => {
            const color = n.outside ? outside : theme.palette.text.primary
            const x = n.left ? leftX - NODE : rightX
            const tx = n.left ? x - 8 : x + NODE + 8
            const cy = n.y + n.h / 2
            return (
              <g key={(n.left ? 'l' : 'r') + n.name}>
                <rect x={x} y={n.y} width={NODE} height={n.h} rx={2} fill={color} />
                <text x={tx} y={cy - 2} fontSize={13} fontWeight={600} fill={color} textAnchor={n.left ? 'end' : 'start'}>{n.name}</text>
                <text x={tx} y={cy + 14} fontSize={12} fill={theme.palette.text.secondary} textAnchor={n.left ? 'end' : 'start'} className="tnum">{money(n.total, currency)}</text>
              </g>
            )
          })}
        </Box>
      )}
    </Box>
  )
}

function Legend({ color, label, value }: { color: string; label: string; value: string }) {
  return (
    <Box sx={{ display: 'flex', alignItems: 'center', gap: 0.75 }}>
      <Box sx={{ width: 10, height: 10, borderRadius: '3px', bgcolor: color, opacity: 0.8 }} />
      <Typography variant="body2" color="text.secondary">{label}</Typography>
      <Typography variant="body2" className="tnum" sx={{ fontWeight: 600 }}>{value}</Typography>
    </Box>
  )
}
