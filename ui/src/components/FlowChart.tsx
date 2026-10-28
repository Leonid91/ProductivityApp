import { useState } from 'react'
import { Box, Typography, useTheme } from '@mui/material'
import type { FlowDiagram, Flow } from '../api'
import { money } from '../format'

/**
 * Where the money moved over the period: transfers, card payments and investments only (spending always goes out).
 * The layout comes from the engine (src/Finance.Core/Services/FlowDiagram.cs), so the PDF draws the same diagram:
 * each account as a box (name, then type), everything outside the app as one blob, and arrows pointing where the
 * money went, blue between own accounts, orange to or from the outside.
 */
export function FlowChart({ diagram, flows, currency }: { diagram: FlowDiagram; flows: Flow[]; currency: string }) {
  const theme = useTheme()
  const [hover, setHover] = useState<number | null>(null)
  const dark = theme.palette.mode === 'dark'
  const internal = theme.palette.chart.series[0]
  const outside = theme.palette.outside
  const paper = theme.palette.background.paper
  const between = flows.filter((f) => !f.fromOutside && !f.toOutside).reduce((s, f) => s + f.amount, 0)
  const out = flows.filter((f) => f.toOutside).reduce((s, f) => s + f.amount, 0)
  const inn = flows.filter((f) => f.fromOutside).reduce((s, f) => s + f.amount, 0)
  const name = (id: string) => diagram.nodes.find((n) => n.id === id)?.name ?? id

  return (
    <Box data-flowchart sx={{ border: 1, borderColor: 'divider', borderRadius: '18px', p: 2.5, bgcolor: 'background.paper' }}>
      <Typography variant="subtitle1">Money moved: transfers, card payments & investments</Typography>
      <Box sx={{ display: 'flex', gap: 2.5, flexWrap: 'wrap', mt: 0.75, mb: 1.5 }}>
        <Legend color={internal} label="Between your accounts" value={money(between, currency)} />
        <Legend color={outside} label="Sent outside the app" value={money(out, currency)} />
        <Legend color={outside} label="Received from outside the app" value={money(inn, currency)} />
      </Box>
      {diagram.nodes.length === 0 ? (
        <Typography color="text.secondary" sx={{ py: 5, textAlign: 'center' }}>No transfers in this period</Typography>
      ) : (
        <Box component="svg" viewBox={`0 0 ${diagram.width} ${diagram.height}`} sx={{ width: '100%', maxWidth: 1000, height: 'auto', display: 'block', mx: 'auto', overflow: 'visible' }}>
          {diagram.nodes.filter((n) => n.outside).map((n) => {
            let y = n.y - (n.parties.length * 20 + 34) / 2 + 12
            return (
              <g key={n.id} data-outside-blob>
                <ellipse cx={n.x} cy={n.y} rx={n.w / 2} ry={n.h / 2} fill={dark ? 'rgba(232,131,79,.10)' : '#fdf1ea'} stroke={outside} strokeWidth={1.5} strokeDasharray="6 4" />
                <text x={n.x} y={y} fontSize={15} fontWeight={700} fill={outside} textAnchor="middle">{n.name}</text>
                <text x={n.x} y={y + 17} fontSize={11.5} fill={theme.palette.text.secondary} textAnchor="middle">{n.type}</text>
                {n.parties.map((p) => {
                  y += 20
                  const parts = [p.sent > 0 ? `sent ${money(p.sent, currency)}` : '', p.received > 0 ? `received ${money(p.received, currency)}` : ''].filter(Boolean).join(', ')
                  return (
                    <text key={p.name} x={n.x} y={y + 17} fontSize={12.5} fill={theme.palette.text.secondary} textAnchor="middle" className="tnum">
                      <tspan fontWeight={600} fill={theme.palette.text.primary}>{p.name}</tspan>: {parts}
                    </text>
                  )
                })}
              </g>
            )
          })}
          {diagram.arrows.map((a, i) => {
            const color = a.outside ? outside : internal
            const fade = hover !== null && hover !== i
            return (
              <g key={'a' + i} onMouseEnter={() => setHover(i)} onMouseLeave={() => setHover(null)} style={{ opacity: fade ? 0.25 : 1, transition: 'opacity 120ms' }}>
                <title>{`${name(a.from)} → ${name(a.to)}: ${money(a.amount, currency)} (${a.count} movement${a.count === 1 ? '' : 's'})`}</title>
                <path d={a.path} fill="none" stroke={color} strokeOpacity={0.85} strokeWidth={a.width} strokeLinecap="round" />
              </g>
            )
          })}
          {/* Arrowheads on top of every line, so none is hidden. */}
          {diagram.arrows.map((a, i) => (
            <path key={'h' + i} d={a.head} fill={a.outside ? outside : internal} stroke={paper} strokeWidth={1}
              style={{ pointerEvents: 'none', opacity: hover !== null && hover !== i ? 0.25 : 1 }} />
          ))}
          {diagram.arrows.map((a, i) => {
            const text = money(a.amount, currency)
            const w = text.length * 7.4 + 14
            return (
              <g key={'l' + i} style={{ pointerEvents: 'none', opacity: hover !== null && hover !== i ? 0.25 : 1 }}>
                <rect x={a.labelX - w / 2} y={a.labelY - 11} width={w} height={22} rx={11} fill={paper} stroke={a.outside ? outside : internal} strokeWidth={1} />
                <text x={a.labelX} y={a.labelY + 4.5} fontSize={12.5} fontWeight={600} fill={theme.palette.text.primary} textAnchor="middle" className="tnum">{text}</text>
              </g>
            )
          })}
          {diagram.nodes.filter((n) => !n.outside).map((n) => (
            <g key={n.id} data-account-node={n.name}>
              <rect x={n.x - n.w / 2} y={n.y - n.h / 2} width={n.w} height={n.h} rx={12} fill={paper} stroke={theme.palette.text.primary} strokeWidth={1.5} />
              <text x={n.x} y={n.y - 3} fontSize={15} fontWeight={700} fill={theme.palette.text.primary} textAnchor="middle">{n.name}</text>
              <text x={n.x} y={n.y + 16} fontSize={12} fill={theme.palette.text.secondary} textAnchor="middle">{n.type}</text>
            </g>
          ))}
        </Box>
      )}
    </Box>
  )
}

function Legend({ color, label, value }: { color: string; label: string; value: string }) {
  return (
    <Box sx={{ display: 'flex', alignItems: 'center', gap: 0.75 }}>
      <Box component="span" sx={{ color, fontWeight: 800, fontSize: 16, lineHeight: 1 }}>→</Box>
      <Typography variant="body2" color="text.secondary">{label}</Typography>
      <Typography variant="body2" className="tnum" sx={{ fontWeight: 600 }}>{value}</Typography>
    </Box>
  )
}
