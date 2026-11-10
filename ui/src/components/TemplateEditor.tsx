import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import {
  Box, Button, CircularProgress, Dialog, Divider, IconButton, MenuItem, Pagination, Select, Table, TableBody, TableCell, TableRow,
  TextField, Tooltip, Typography, useTheme,
} from '@mui/material'
import { alpha } from '@mui/material/styles'
import Close from '@mui/icons-material/CloseRounded'
import AddColumn from '@mui/icons-material/ViewWeekRounded'
import { api, type BankTemplate, type ColumnRole, type DateOrder, type Draft, type KindHint, type PageImage, type TemplateColumn } from '../api'
import { useApp } from '../AppContext'
import { formatDate, plural } from '../format'
import { StatusChip } from './DraftCard'
import { Money } from './common'

const roleLabels: Record<ColumnRole, string> = {
  date: 'Date', valueDate: 'Value date', description: 'Label', category: 'Bank category', debit: 'Money out', credit: 'Money in', amount: 'Amount', balance: 'Balance', ignore: 'Ignore',
}
const roles = Object.keys(roleLabels) as ColumnRole[]

function initialColumns(draft: Draft): TemplateColumn[] {
  const src = draft.template?.columns?.length ? draft.template.columns : draft.columns
  if (src.length >= 2) return [...src].sort((a, b) => a.left - b.left)
  const b = [0.06, 0.17, 0.6, 0.71, 0.82, 0.94]
  const r: ColumnRole[] = ['date', 'description', 'debit', 'credit', 'balance']
  return r.map((role, i) => ({ role, left: b[i], right: b[i + 1] }))
}

/**
 * Column template editor for banks the automatic reader cannot follow: the statement page is shown with its
 * columns; the person drags the borders and says what each column holds. The result is checked live against
 * the statement's balances, and saved for every later statement from the same bank.
 */
export function TemplateEditor({ draft, currency, onClose }: { draft: Draft; currency: string; onClose: (next: Draft | null) => void }) {
  const { notify, fail, settings } = useApp()
  const theme = useTheme()
  const start = useMemo(() => initialColumns(draft), [draft])
  const [edges, setEdges] = useState<number[]>(() => [start[0].left, ...start.map((c, i) => (i + 1 < start.length ? (c.right + start[i + 1].left) / 2 : c.right))])
  const [colRoles, setColRoles] = useState<ColumnRole[]>(() => start.map((c) => c.role))
  const [name, setName] = useState(draft.template?.name ?? (draft.bank ?? draft.suggestedMatch ?? ''))
  const [match, setMatch] = useState(draft.template?.matchText ?? draft.suggestedMatch ?? '')
  const [dateOrder, setDateOrder] = useState<DateOrder>(draft.template?.dateOrder ?? (draft.dateOrder === 'unknown' ? 'dayMonth' : draft.dateOrder))
  const [kind, setKind] = useState<KindHint>(draft.template?.kind ?? 'auto')
  const [page, setPage] = useState(0)
  const [image, setImage] = useState<PageImage | null>(null)
  const [preview, setPreview] = useState<Draft | null>(null)
  const [previewing, setPreviewing] = useState(false)
  const [saving, setSaving] = useState(false)
  const box = useRef<HTMLDivElement>(null)
  const [width, setWidth] = useState(800)
  const drag = useRef<{ index: number } | null>(null)

  const colors: Record<ColumnRole, string> = {
    date: theme.palette.chart.series[0], valueDate: theme.palette.chart.other, description: theme.palette.chart.series[6],
    debit: theme.palette.chart.series[7], credit: theme.palette.chart.series[5], amount: theme.palette.chart.series[1],
    balance: theme.palette.chart.series[2], ignore: theme.palette.chart.other, category: theme.palette.chart.series[3],
  }

  useEffect(() => {
    let live = true
    setImage(null)
    api.imports.page(draft.id, page, 1600).then((img) => { if (live) setImage(img) }, fail)
    return () => { live = false }
  }, [draft.id, page, fail])

  useEffect(() => {
    const el = box.current
    if (!el) return
    const ro = new ResizeObserver(() => setWidth(el.clientWidth))
    ro.observe(el)
    return () => ro.disconnect()
  }, [])

  const template = useCallback((): BankTemplate => ({
    id: draft.template?.id ?? 0,
    name: name.trim(),
    matchText: match.trim(),
    dateOrder,
    kind,
    columns: colRoles.map((role, i) => ({ role, left: edges[i], right: edges[i + 1] })),
  }), [draft.template, name, match, dateOrder, kind, colRoles, edges])

  // Live check of the columns against the statement.
  useEffect(() => {
    setPreviewing(true)
    const t = setTimeout(() => {
      api.imports.preview(draft.id, template()).then(setPreview, fail).finally(() => setPreviewing(false))
    }, 350)
    return () => clearTimeout(t)
  }, [draft.id, template, fail])

  const onPointerMove = (e: React.PointerEvent) => {
    if (!drag.current || !box.current) return
    const rect = box.current.getBoundingClientRect()
    const x = Math.min(1, Math.max(0, (e.clientX - rect.left) / rect.width))
    const i = drag.current.index
    setEdges((prev) => {
      const next = [...prev]
      const lo = i === 0 ? 0 : prev[i - 1] + 0.012
      const hi = i === prev.length - 1 ? 1 : prev[i + 1] - 0.012
      next[i] = Math.min(hi, Math.max(lo, x))
      return next
    })
  }

  const addColumn = () => {
    let widest = 0
    for (let i = 1; i < colRoles.length; i++) if (edges[i + 1] - edges[i] > edges[widest + 1] - edges[widest]) widest = i
    const mid = (edges[widest] + edges[widest + 1]) / 2
    setEdges([...edges.slice(0, widest + 1), mid, ...edges.slice(widest + 1)])
    setColRoles([...colRoles.slice(0, widest + 1), 'ignore', ...colRoles.slice(widest + 1)])
  }

  const removeColumn = (i: number) => {
    if (colRoles.length <= 2) return
    const nextEdges = [...edges]
    nextEdges.splice(i === colRoles.length - 1 ? i : i + 1, 1)
    setEdges(nextEdges)
    setColRoles(colRoles.filter((_, k) => k !== i))
  }

  const cancel = async () => {
    try {
      onClose(await api.imports.preview(draft.id, draft.template))
    } catch {
      onClose(null)
    }
  }

  const save = async () => {
    setSaving(true)
    try {
      const saved = await api.templates.save(template())
      const next = await api.imports.preview(draft.id, saved)
      notify(`Columns saved for ${saved.name}`)
      onClose(next)
    } catch (e) {
      fail(e)
    } finally {
      setSaving(false)
    }
  }

  const height = image ? (width * image.heightPt) / image.widthPt : width * 1.29
  const dark = theme.palette.mode === 'dark'

  return (
    <Dialog open fullScreen onClose={() => void cancel()} slotProps={{ paper: { sx: { borderRadius: 0, border: 0, bgcolor: 'background.default' } } }}>
      <Box sx={{ display: 'flex', alignItems: 'center', gap: 1.5, px: 2.5, height: 60, borderBottom: 1, borderColor: 'divider', bgcolor: 'background.paper' }}>
        <IconButton onClick={() => void cancel()} aria-label="Close"><Close /></IconButton>
        <Typography variant="h6" noWrap sx={{ flex: 1 }}>Columns · {draft.fileName}</Typography>
        <Button startIcon={<AddColumn />} onClick={addColumn}>Add column</Button>
        <Button variant="contained" disabled={saving || !name.trim() || match.trim().length < 3} onClick={() => void save()}>Save</Button>
      </Box>
      <Box sx={{ display: 'flex', flex: 1, minHeight: 0 }}>
        <Box sx={{ flex: 1, overflow: 'auto', p: 3 }} onPointerMove={onPointerMove} onPointerUp={() => (drag.current = null)}>
          <Box ref={box} sx={{ position: 'relative', maxWidth: 980, mx: 'auto', userSelect: 'none' }}>
            {/* column roles above the page */}
            <Box sx={{ position: 'relative', height: 46, mb: 1 }}>
              {colRoles.map((role, i) => (
                <Box key={i} sx={{ position: 'absolute', left: `${edges[i] * 100}%`, width: `${(edges[i + 1] - edges[i]) * 100}%`, px: 0.4, top: 0 }}>
                  <Select
                    size="small"
                    value={role}
                    onChange={(e) => {
                      const v = e.target.value as ColumnRole | '__remove'
                      if (v === '__remove') removeColumn(i)
                      else setColRoles(colRoles.map((r, k) => (k === i ? v : r)))
                    }}
                    renderValue={(v) => roleLabels[v as ColumnRole]}
                    sx={{
                      width: '100%', fontSize: 12.5, fontWeight: 600, height: 36, bgcolor: alpha(colors[role], dark ? 0.22 : 0.13),
                      '& .MuiOutlinedInput-notchedOutline': { borderColor: alpha(colors[role], 0.5) },
                      '& .MuiSelect-select': { py: 0.75, pl: 1.25, pr: '26px !important' },
                    }}
                  >
                    {roles.map((r) => <MenuItem key={r} value={r}>{roleLabels[r]}</MenuItem>)}
                    <Divider />
                    <MenuItem value="__remove" disabled={colRoles.length <= 2} sx={{ color: 'error.main' }}>Remove column</MenuItem>
                  </Select>
                </Box>
              ))}
            </Box>
            <Box sx={{ position: 'relative', width: '100%', height, borderRadius: 2, overflow: 'hidden', bgcolor: '#fff', boxShadow: '0 1px 3px rgba(0,0,0,.12), 0 10px 30px rgba(0,0,0,.08)' }}>
              {image ? (
                <img src={`data:image/png;base64,${image.png}`} alt="" draggable={false} style={{ width: '100%', height: '100%', display: 'block' }} />
              ) : (
                <Box sx={{ position: 'absolute', inset: 0, display: 'grid', placeItems: 'center' }}><CircularProgress size={28} /></Box>
              )}
              {colRoles.map((role, i) => (
                <Box key={i} sx={{
                  position: 'absolute', top: 0, bottom: 0, left: `${edges[i] * 100}%`, width: `${(edges[i + 1] - edges[i]) * 100}%`,
                  bgcolor: role === 'ignore' ? 'transparent' : alpha(colors[role], 0.1),
                  backgroundImage: role === 'ignore' ? `repeating-linear-gradient(135deg, ${alpha('#000', 0.05)} 0 6px, transparent 6px 12px)` : undefined,
                  pointerEvents: 'none',
                }} />
              ))}
            </Box>
            {/* draggable borders across both strips */}
            {edges.map((x, i) => (
              <Tooltip key={i} title="Drag" placement="top">
                <Box
                  onPointerDown={(e) => { (e.target as HTMLElement).setPointerCapture(e.pointerId); drag.current = { index: i } }}
                  onPointerMove={onPointerMove}
                  onPointerUp={() => (drag.current = null)}
                  sx={{
                    position: 'absolute', top: 0, bottom: 0, left: `calc(${x * 100}% - 7px)`, width: 14, cursor: 'col-resize', zIndex: 2,
                    '&::after': { content: '""', position: 'absolute', left: 6, top: 0, bottom: 0, width: 2, borderRadius: 1, bgcolor: 'primary.main', opacity: 0.85 },
                    '&:hover::after': { opacity: 1, width: 3, left: 5.5 },
                  }}
                />
              </Tooltip>
            ))}
          </Box>
          {draft.pageCount > 1 && (
            <Box sx={{ display: 'flex', justifyContent: 'center', mt: 2 }}>
              <Pagination count={draft.pageCount} page={page + 1} onChange={(_, p) => setPage(p - 1)} shape="rounded" />
            </Box>
          )}
        </Box>

        <Box sx={{ width: 380, flex: '0 0 380px', borderLeft: 1, borderColor: 'divider', bgcolor: 'background.paper', overflow: 'auto', p: 3, display: 'grid', gap: 2, alignContent: 'start' }}>
          <TextField label="Template name" value={name} onChange={(e) => setName(e.target.value)} size="small" />
          <TextField
            label="Recognise statements containing"
            value={match}
            onChange={(e) => setMatch(e.target.value)}
            size="small"
            error={match.trim().length > 0 && match.trim().length < 3}
          />
          <Box sx={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 1.5 }}>
            <TextField select size="small" label="Dates" value={dateOrder} onChange={(e) => setDateOrder(e.target.value as DateOrder)}>
              <MenuItem value="dayMonth">Day / month</MenuItem>
              <MenuItem value="monthDay">Month / day</MenuItem>
            </TextField>
            <TextField select size="small" label="Statement" value={kind} onChange={(e) => setKind(e.target.value as KindHint)}>
              <MenuItem value="auto">Detect</MenuItem>
              <MenuItem value="bank">Bank account</MenuItem>
              <MenuItem value="creditCard">Credit card</MenuItem>
            </TextField>
          </Box>
          <Divider />
          <Box sx={{ display: 'flex', alignItems: 'center', gap: 1 }}>
            <Typography variant="subtitle1" sx={{ flex: 1 }}>Result</Typography>
            {previewing && <CircularProgress size={16} />}
          </Box>
          {preview && (
            <>
              <Box><StatusChip draft={preview} currency={currency} /></Box>
              <Typography variant="body2" color="text.secondary">
                {plural(preview.rows.length, 'operation')}
                {preview.opening !== null && preview.closing !== null ? ' · balances found' : ''}
              </Typography>
              <Table size="small" sx={{ tableLayout: 'fixed', '& td': { px: 0.75, py: 0.6, fontSize: 12.5 } }}>
                <colgroup><col style={{ width: 108 }} /><col /><col style={{ width: 96 }} /></colgroup>
                <TableBody>
                  {preview.rows.slice(0, 40).map((r) => (
                    <TableRow key={r.index}>
                      <TableCell className="tnum" sx={{ whiteSpace: 'nowrap', color: 'text.secondary' }}>{formatDate(r.date, settings.dateFormat)}</TableCell>
                      <TableCell sx={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{r.label}</TableCell>
                      <TableCell align="right"><Money value={r.amount} currency={currency} sign sx={{ fontSize: 12.5 }} /></TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            </>
          )}
        </Box>
      </Box>
    </Dialog>
  )
}
