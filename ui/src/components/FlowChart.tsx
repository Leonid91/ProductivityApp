import { useState } from 'react'
import { Box, Typography, useTheme } from '@mui/material'
import type { FlowDiagram, Flow } from '../api'
import { money } from '../format'
import { useAmountsHidden } from '../mask'

/**
 * Where the money moved over the period: transfers, card payments and investments only (spending always goes out).
 * The layout comes from the engine (src/Finance.Core/Services/FlowDiagram.cs), so the PDF draws the same diagram:
 * each bank as a box holding its accounts (name, then type), everything outside the app as one orange box of the size of an account,
 * and arrows pointing where the money went, blue between own accounts (inside a bank, or from one bank to another), orange to or
 * from the outside.
 */
export function FlowChart({ diagram, flows, currency }: { diagram: FlowDiagram; flows: Flow[]; currency: string }) {
  useAmountsHidden()
  const theme = useTheme()
  const [hover, setHover] = useState<number | null>(null)
  const dark = theme.palette.mode === 'dark'
  const internal = theme.palette.chart.series[0]
  const outside = theme.palette.outside
  const paper = theme.palette.background.paper
  const between = flows.filter((f) => !f.fromOutside && !f.toOutside).reduce((s, f) => s + f.amount, 0)
  const out = flows.filter((f) => f.toOutside).reduce((s, f) => s + f.amount, 0)
  const inn = flows.filter((f) => f.fromOutside).reduce((s, f) => s + f.amount, 0)
  // What the banks kept on international transfers (their rate and fees): the amount sent at the rate of its day, less the amount received.
  const lost = diagram.arrows.reduce((s, a) => s + (a.lost ?? 0), 0)
  const hasLost = diagram.arrows.some((a) => a.lost != null)
  const name = (id: string) => { const n = diagram.nodes.find((x) => x.id === id); return n ? n.title ?? n.name : id }

  return (
    <Box data-flowchart sx={{ border: 1, borderColor: 'divider', borderRadius: '18px', p: 2.5, bgcolor: 'background.paper' }}>
      <Typography variant="subtitle1">Money moved: transfers, card payments & investments</Typography>
      <Box sx={{ display: 'flex', gap: 2.5, flexWrap: 'wrap', mt: 0.75, mb: 1.5 }}>
        <Legend color={internal} label="Between your accounts" value={money(between, currency)} />
        <Legend color={outside} label="Sent outside the app" value={money(out, currency)} />
        <Legend color={outside} label="Received from outside the app" value={money(inn, currency)} />
        {hasLost && <Legend data-lost label="Lost to exchange and fees" value={money(lost, currency)} />}
      </Box>
      {diagram.nodes.length === 0 ? (
        <Typography color="text.secondary" sx={{ py: 5, textAlign: 'center' }}>No transfers in this period</Typography>
      ) : (
        <Box component="svg" viewBox={`0 0 ${diagram.width} ${diagram.height}`} sx={{ width: '100%', maxWidth: 1000, height: 'auto', display: 'block', mx: 'auto', overflow: 'visible' }}>
          {diagram.banks.map((b) => (
            // A bank behind its accounts: the shape of an account box, larger, its name at the top.
            <g key={'b' + b.name} data-bank-node={b.name}>
              <rect x={b.x - b.w / 2} y={b.y - b.h / 2} width={b.w} height={b.h} rx={18} fill={theme.palette.surface.sunken} stroke={theme.palette.text.secondary} strokeWidth={1.5} />
              <text x={b.x} y={b.y - b.h / 2 + 29} fontSize={15} fontWeight={700} fill={theme.palette.text.secondary} textAnchor="middle">{fit(b.name, b.w)}</text>
            </g>
          ))}
          {diagram.arrows.map((a, i) => {
            const color = a.outside ? outside : internal
            const fade = hover !== null && hover !== i
            return (
              <g key={'a' + i} onMouseEnter={() => setHover(i)} onMouseLeave={() => setHover(null)} style={{ opacity: fade ? 0.25 : 1, transition: 'opacity 120ms' }}>
                <title>{`${name(a.from)} → ${name(a.to)}: ${money(a.amount, currency)} (${a.count} movement${a.count === 1 ? '' : 's'})`
                  + (a.lost != null ? `. Lost to exchange and fees: ${money(a.lost, currency)}` : '')}</title>
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
          {diagram.nodes.map((n) => (
            // The outside box has the size of the accounts, in orange; who is behind it shows on hover.
            <g key={n.id} {...(n.outside ? { 'data-outside-node': '' } : { 'data-account-node': n.name })}>
              <title>{n.outside ? n.parties.map((p) => p.name).join(', ') : n.title ?? n.name}</title>
              <rect x={n.x - n.w / 2} y={n.y - n.h / 2} width={n.w} height={n.h} rx={12} fill={n.outside ? (dark ? '#3a2a20' : '#fdf1ea') : paper} stroke={n.outside ? outside : theme.palette.text.primary} strokeWidth={1.5} />
              <text x={n.x} y={n.y - 3} fontSize={15} fontWeight={700} fill={n.outside ? outside : theme.palette.text.primary} textAnchor="middle">{fit(n.name, n.w)}</text>
              <text x={n.x} y={n.y + 16} fontSize={12} fill={theme.palette.text.secondary} textAnchor="middle">{n.type}</text>
            </g>
          ))}
        </Box>
      )}
    </Box>
  )
}

/** A name cut with "…" when longer than its box, by the same rule as the engine (FlowDiagram.Fit), so the PDF matches. */
function fit(name: string, w: number) {
  const max = Math.floor((w - 16) / 8)
  return name.length <= max ? name : name.slice(0, Math.max(1, max - 1)).trimEnd() + '…'
}

/** One figure of the legend: with the arrow of its colour when it names arrows of the drawing. */
export function Legend({ color, label, value, ...rest }: { color?: string; label: string; value: string; 'data-lost'?: boolean; 'data-total'?: string }) {
  return (
    <Box {...rest} sx={{ display: 'flex', alignItems: 'center', gap: 0.75 }}>
      {color && <Box component="span" sx={{ color, fontWeight: 800, fontSize: 16, lineHeight: 1 }}>→</Box>}
      <Typography variant="body2" color="text.secondary">{label}</Typography>
      <Typography variant="body2" className="tnum" sx={{ fontWeight: 600 }}>{value}</Typography>
    </Box>
  )
}
