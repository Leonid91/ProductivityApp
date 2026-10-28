import { useCallback, useEffect, useRef, useState } from 'react'
import {
  Box, Button, IconButton, LinearProgress, Table, TableBody, TableCell, TableHead, TableRow, Tooltip, Typography, useTheme,
} from '@mui/material'
import { alpha } from '@mui/material/styles'
import CloudUpload from '@mui/icons-material/CloudUploadRounded'
import CheckCircle from '@mui/icons-material/CheckCircleRounded'
import ErrorOutline from '@mui/icons-material/ErrorOutlineRounded'
import DeleteOutline from '@mui/icons-material/DeleteOutlineRounded'
import { api, type BankTemplate, type Draft, type StatementInfo } from '../api'
import { useApp } from '../AppContext'
import { plural, shortDate } from '../format'
import { DraftCard } from '../components/DraftCard'
import { FileBrowser } from '../components/FileBrowser'
import { ConfirmDialog, PageHeader, SectionCard } from '../components/common'

function toBase64(file: File): Promise<string> {
  return new Promise((resolve, reject) => {
    const r = new FileReader()
    r.onload = () => resolve(String(r.result).split(',')[1] ?? '')
    r.onerror = () => reject(r.error ?? new Error('The file could not be read.'))
    r.readAsDataURL(file)
  })
}

type Job = { name: string; run: () => Promise<Draft> }

export function ImportPage() {
  const { fail, dataVersion, dataChanged, notify } = useApp()
  const theme = useTheme()
  const [drafts, setDrafts] = useState<Draft[]>([])
  const [queue, setQueue] = useState<Job[]>([])
  const [current, setCurrent] = useState<string | null>(null)
  const [over, setOver] = useState(false)
  const [browsing, setBrowsing] = useState(false)
  const [statements, setStatements] = useState<StatementInfo[]>([])
  const [templates, setTemplates] = useState<BankTemplate[]>([])
  const [confirm, setConfirm] = useState<{ title: string; run: () => Promise<void> } | null>(null)
  const working = useRef(false)

  const reloadLists = useCallback(() => {
    api.statements.list().then(setStatements, fail)
    api.templates.list().then(setTemplates, fail)
  }, [fail])

  useEffect(() => { reloadLists() }, [reloadLists, dataVersion])

  // Statements are read one after the other; each becomes a draft to review.
  useEffect(() => {
    if (working.current || queue.length === 0) return
    working.current = true
    const [job, ...rest] = queue
    setCurrent(job.name)
    job.run()
      .then((d) => setDrafts((list) => [...list, d]))
      .catch((e) => fail(new Error(`${job.name}: ${e instanceof Error ? e.message : String(e)}`)))
      .finally(() => {
        working.current = false
        setCurrent(null)
        setQueue(rest)
      })
  }, [queue, fail])

  const addFiles = (files: File[]) => {
    const pdfs = files.filter((f) => f.name.toLowerCase().endsWith('.pdf'))
    if (pdfs.length < files.length) notify('Only PDF files can be imported', 'warning')
    setQueue((q) => [...q, ...pdfs.map((f) => ({ name: f.name, run: async () => api.imports.parseBytes(f.name, await toBase64(f)) }))])
  }

  const addPaths = (paths: string[]) => {
    setBrowsing(false)
    setQueue((q) => [...q, ...paths.map((p) => ({ name: p.split('\\').pop() ?? p, run: () => api.imports.parseFile(p) }))])
  }

  const onDrop = (e: React.DragEvent) => {
    e.preventDefault()
    setOver(false)
    addFiles([...e.dataTransfer.files])
  }

  const ok = theme.palette.money.positive

  return (
    <Box sx={{ maxWidth: 1180, mx: 'auto', pt: 3, pb: 6 }}>
      <PageHeader title="Import" />
      <Box
        onDragOver={(e) => { e.preventDefault(); setOver(true) }}
        onDragLeave={() => setOver(false)}
        onDrop={onDrop}
        sx={{
          border: '2px dashed', borderColor: over ? 'primary.main' : 'divider', borderRadius: '24px', p: 5, textAlign: 'center',
          bgcolor: over ? alpha(theme.palette.primary.main, 0.06) : 'background.paper', transition: 'all 140ms',
        }}
      >
        <Box sx={{ width: 64, height: 64, mx: 'auto', borderRadius: '20px', display: 'grid', placeItems: 'center', bgcolor: alpha(theme.palette.primary.main, 0.1), color: 'primary.main', mb: 2 }}>
          <CloudUpload sx={{ fontSize: 32 }} />
        </Box>
        <Typography variant="h6">Drop PDF statements here</Typography>
        <Button variant="contained" sx={{ mt: 2 }} onClick={() => setBrowsing(true)}>Choose files</Button>
        {(current || queue.length > 0) && (
          <Box sx={{ maxWidth: 420, mx: 'auto', mt: 3 }}>
            <Typography variant="body2" color="text.secondary" noWrap sx={{ mb: 1 }}>
              Reading {current}{queue.length > 1 ? ` · ${queue.length - 1} more` : ''}
            </Typography>
            <LinearProgress />
          </Box>
        )}
      </Box>

      <Box sx={{ display: 'grid', gap: 3, mt: 3 }}>
        {drafts.map((d) => (
          <DraftCard key={d.id} draft={d} onDone={() => { setDrafts((list) => list.filter((x) => x.id !== d.id)); reloadLists() }} />
        ))}
      </Box>

      {statements.length > 0 && (
        <SectionCard title="Imported statements" sx={{ mt: 3 }}>
          <Table size="small">
            <TableHead>
              <TableRow>
                <TableCell>File</TableCell>
                <TableCell>Account</TableCell>
                <TableCell>Period</TableCell>
                <TableCell align="right">Operations</TableCell>
                <TableCell align="center">Balances</TableCell>
                <TableCell padding="checkbox" />
              </TableRow>
            </TableHead>
            <TableBody>
              {statements.map((s) => (
                <TableRow key={s.id} hover>
                  <TableCell sx={{ maxWidth: 320 }}><Typography variant="body2" noWrap>{s.fileName}</Typography></TableCell>
                  <TableCell><Typography variant="body2" noWrap>{s.account}</Typography></TableCell>
                  <TableCell className="tnum" sx={{ whiteSpace: 'nowrap', color: 'text.secondary' }}>
                    {s.periodStart && s.periodEnd ? `${shortDate(s.periodStart)} – ${shortDate(s.periodEnd)}` : '—'}
                  </TableCell>
                  <TableCell align="right" className="tnum">{s.transactionCount}</TableCell>
                  <TableCell align="center">
                    {s.verified
                      ? <Tooltip title="Balanced to the cent"><CheckCircle sx={{ fontSize: 18, color: ok }} /></Tooltip>
                      : <Tooltip title="Not checked against balances"><ErrorOutline sx={{ fontSize: 18, color: 'warning.main' }} /></Tooltip>}
                  </TableCell>
                  <TableCell padding="checkbox">
                    <IconButton size="small" aria-label="Delete" onClick={() => setConfirm({
                      title: `Delete ${s.fileName} and its ${plural(s.transactionCount, 'operation')}?`,
                      run: async () => { await api.statements.remove(s.id); notify('Statement deleted'); dataChanged() },
                    })}>
                      <DeleteOutline fontSize="small" />
                    </IconButton>
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </SectionCard>
      )}

      {templates.length > 0 && (
        <SectionCard title="Column templates" sx={{ mt: 3 }}>
          <Table size="small">
            <TableHead>
              <TableRow>
                <TableCell>Name</TableCell>
                <TableCell>Recognised by</TableCell>
                <TableCell padding="checkbox" />
              </TableRow>
            </TableHead>
            <TableBody>
              {templates.map((t) => (
                <TableRow key={t.id} hover>
                  <TableCell>{t.name}</TableCell>
                  <TableCell sx={{ color: 'text.secondary' }}>“{t.matchText}”</TableCell>
                  <TableCell padding="checkbox">
                    <IconButton size="small" aria-label="Delete" onClick={() => setConfirm({
                      title: `Delete the template ${t.name}?`,
                      run: async () => { await api.templates.remove(t.id); reloadLists() },
                    })}>
                      <DeleteOutline fontSize="small" />
                    </IconButton>
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </SectionCard>
      )}

      <FileBrowser open={browsing} mode="pdfs" title="Choose statements" onClose={() => setBrowsing(false)} onPick={addPaths} />
      <ConfirmDialog
        open={confirm !== null}
        title={confirm?.title ?? ''}
        confirm="Delete"
        danger
        onClose={() => setConfirm(null)}
        onConfirm={() => { const c = confirm; if (c) void c.run().catch(fail) }}
      />
    </Box>
  )
}
