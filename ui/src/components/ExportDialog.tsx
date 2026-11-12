import { useEffect, useRef, useState } from 'react'
import {
  Box, Button, Checkbox, Dialog, DialogActions, DialogContent, DialogTitle, FormControlLabel, InputAdornment, TextField, ThemeProvider, Typography,
} from '@mui/material'
import FolderOpen from '@mui/icons-material/FolderOpenRounded'
import { api, type Granularity, type TransferTimeline } from '../api'
import { useApp } from '../AppContext'
import { makeTheme } from '../theme'
import { FileBrowser } from './FileBrowser'
import { TransferTimelineChart } from './TransferTimeline'

/** The views of a period a PDF can hold; all but the summary at first. */
const PARTS = [['summary', 'Summary'], ['statements', 'Statements'], ['analytics', 'Analytics'], ['transfers', 'Transfers']] as const
type Part = (typeof PARTS)[number][0]
const DEFAULT_PARTS: Record<Part, boolean> = { summary: false, statements: true, analytics: true, transfers: true }

/** The PDF shows the drawings on white paper, whatever the theme of the window. */
const paper = makeTheme('light')

/**
 * The transfers over time as drawn on the screen, made ready for the PDF: one standalone drawing, its size and font
 * given, without what only the screen uses (hover descriptions, the wide unseen lines for pointing, styles).
 */
function drawingOf(host: HTMLElement | null): string | undefined {
  const svg = host?.querySelector('[data-transfer-timeline] svg')
  const box = svg?.getAttribute('viewBox')?.split(' ')
  if (!svg || !box || box.length !== 4) return undefined
  const copy = svg.cloneNode(true) as SVGSVGElement
  copy.querySelectorAll('title').forEach((t) => t.remove())
  copy.querySelectorAll('[stroke="transparent"]').forEach((e) => e.remove())
  for (const el of [copy, ...copy.querySelectorAll('*')]) {
    const opacity = (el as SVGElement).style?.opacity
    if (opacity) el.setAttribute('opacity', opacity)
    // A line's own transparency is given as the transparency of the whole line (read the same way by every drawing reader).
    const strokeOpacity = el.getAttribute('stroke-opacity')
    if (strokeOpacity && el.tagName === 'line') { el.setAttribute('opacity', strokeOpacity); el.removeAttribute('stroke-opacity') }
    el.removeAttribute('style')
    el.removeAttribute('class')
  }
  copy.setAttribute('xmlns', 'http://www.w3.org/2000/svg')
  copy.setAttribute('width', box[2])
  copy.setAttribute('height', box[3])
  copy.setAttribute('font-family', 'Inter')
  return new XMLSerializer().serializeToString(copy)
}

export function ExportDialog({ open, onClose, from, to, granularity, currency, title, timeline, timelineCurrency }: {
  open: boolean
  onClose: () => void
  from: string
  to: string
  granularity: Granularity
  currency: string
  title: string
  /** The transfers over time of the period (every bank together), drawn into the PDF as on the screen. */
  timeline: TransferTimeline
  timelineCurrency: string
}) {
  const { notify, fail } = useApp()
  const [folder, setFolder] = useState('')
  const [name, setName] = useState('')
  const [browsing, setBrowsing] = useState(false)
  const [busy, setBusy] = useState(false)
  const [parts, setParts] = useState(DEFAULT_PARTS)
  const drawing = useRef<HTMLDivElement>(null)

  useEffect(() => {
    if (!open) return
    setParts(DEFAULT_PARTS)
    api.report.defaults(from, to, granularity, currency).then((d) => { setFolder(d.folder); setName(d.fileName) }, fail)
  }, [open, from, to, granularity, currency, fail])

  const exportNow = async () => {
    setBusy(true)
    try {
      const timelineSvg = parts.transfers ? drawingOf(drawing.current) : undefined
      const r = await api.report.export({ from, to, granularity, currency, folder, fileName: name, parts, timelineSvg })
      onClose()
      notify('Report saved', 'success', [
        { label: 'Open', run: () => void api.shell.open(r.path).catch(fail) },
        { label: 'Show in folder', run: () => void api.shell.reveal(r.path).catch(fail) },
      ])
    } catch (e) {
      fail(e)
    } finally {
      setBusy(false)
    }
  }

  return (
    <>
      <Dialog open={open} onClose={onClose} maxWidth="sm" fullWidth>
        <DialogTitle>Export to PDF</DialogTitle>
        <DialogContent sx={{ display: 'grid', gap: 2.5, pt: '6px !important' }}>
          <Typography color="text.secondary">{title}</Typography>
          <Box data-export-parts sx={{ display: 'flex', flexWrap: 'wrap', columnGap: 1 }}>
            {PARTS.map(([key, label]) => (
              <FormControlLabel key={key} label={label}
                control={<Checkbox data-export-part={key} checked={parts[key]} onChange={(_, on) => setParts((p) => ({ ...p, [key]: on }))} />} />
            ))}
          </Box>
          <TextField
            label="Folder"
            value={folder}
            slotProps={{
              input: {
                readOnly: true,
                endAdornment: (
                  <InputAdornment position="end">
                    <Button size="small" startIcon={<FolderOpen />} onClick={() => setBrowsing(true)}>Change</Button>
                  </InputAdornment>
                ),
              },
            }}
          />
          <TextField label="File name" value={name} onChange={(e) => setName(e.target.value)} />
        </DialogContent>
        <DialogActions sx={{ px: 3, pb: 2.5 }}>
          <Box sx={{ flex: 1 }} />
          <Button onClick={onClose}>Cancel</Button>
          <Button variant="contained" disabled={busy || !folder || !name.trim() || !Object.values(parts).some(Boolean)} onClick={() => void exportNow()}>Export</Button>
        </DialogActions>
      </Dialog>
      {/* The transfers over time drawn out of sight, on white, for the PDF. */}
      {open && parts.transfers && timeline.lanes.length > 0 && (
        <ThemeProvider theme={paper}>
          <Box ref={drawing} aria-hidden sx={{ position: 'fixed', left: -20000, top: 0, width: 1000, pointerEvents: 'none' }}>
            <TransferTimelineChart timeline={timeline} currency={timelineCurrency} />
          </Box>
        </ThemeProvider>
      )}
      <FileBrowser
        open={browsing}
        mode="folder"
        title="Save reports in"
        initialPath={folder}
        onClose={() => setBrowsing(false)}
        onPick={([p]) => { setFolder(p); setBrowsing(false) }}
      />
    </>
  )
}
