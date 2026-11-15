import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import {
  Box, Button, Dialog, DialogActions, DialogContent, DialogTitle, LinearProgress, MenuItem, Table, TableBody, TableCell, TableHead, TableRow, TextField, Tooltip, Typography, useTheme,
} from '@mui/material'
import { alpha } from '@mui/material/styles'
import CloudUpload from '@mui/icons-material/CloudUploadRounded'
import CheckCircle from '@mui/icons-material/CheckCircleRounded'
import ErrorOutline from '@mui/icons-material/ErrorOutlineRounded'
import WarningAmber from '@mui/icons-material/WarningAmberRounded'
import GppMaybe from '@mui/icons-material/GppMaybeRounded'
import { api, currencies, type BankTemplate, type Draft, type StatementInfo } from '../api'
import { useApp } from '../AppContext'
import { plural, shortDate } from '../format'
import { DraftCard } from '../components/DraftCard'
import { FileBrowser } from '../components/FileBrowser'
import { ConfirmDialog, PageHeader, SectionCard } from '../components/common'
import { useRowOrder } from '../components/order'
import { ROW_ACTIONS_WIDTH, RowActions } from '../components/RowActions'
import { TextCell } from '../components/amounts'

/** An imported statement changed from its pencil: the bank and the name of its account, and the account's currency. */
function StatementDialog({ statement, onClose }: { statement: StatementInfo | null; onClose: () => void }) {
  const { accounts, banks, dataChanged, notify, fail, refreshAccounts } = useApp()
  const [bank, setBank] = useState(0)
  const [name, setName] = useState('')
  const [currency, setCurrency] = useState('CAD')
  const account = accounts.find((a) => a.id === statement?.accountId)
  useEffect(() => {
    if (!statement) return
    setBank(statement.bankId ?? 0)
    setName(account?.name ?? statement.account)
    setCurrency(statement.currency)
  }, [statement]) // eslint-disable-line react-hooks/exhaustive-deps
  const save = () => {
    if (!account) return
    api.accounts.update(account, { name, currency, ...(bank ? { bankId: bank } : {}) }).then(() => {
      notify('Statement saved')
      void refreshAccounts()
      dataChanged()
      onClose()
    }, fail)
  }
  return (
    <Dialog open={statement !== null} onClose={onClose} maxWidth="xs" fullWidth data-statement-dialog>
      <DialogTitle>Edit {statement?.fileName}</DialogTitle>
      <DialogContent sx={{ display: 'grid', gap: 2.25, pt: '8px !important' }}>
        <TextField select label="Bank" value={bank} onChange={(e) => setBank(Number(e.target.value))}>
          <MenuItem value={0}>No bank</MenuItem>
          {banks.map((b) => <MenuItem key={b.id} value={b.id}>{b.name}</MenuItem>)}
        </TextField>
        <TextField autoFocus label="Account name" value={name} onChange={(e) => setName(e.target.value)} slotProps={{ htmlInput: { 'data-field': 'name' } }} />
        <TextField select label="Currency" value={currency} onChange={(e) => setCurrency(e.target.value)}>
          {[...new Set([currency, ...currencies])].map((c) => <MenuItem key={c} value={c}>{c}</MenuItem>)}
        </TextField>
      </DialogContent>
      <DialogActions sx={{ px: 3, pb: 2.5 }}>
        <Button onClick={onClose}>Cancel</Button>
        <Button variant="contained" disabled={!account || name.trim() === ''} onClick={save}>Save</Button>
      </DialogActions>
    </Dialog>
  )
}

/** A column template changed from its pencil: its name and the text of the statements it recognises. */
function TemplateDialog({ template, onClose, onSaved }: { template: BankTemplate | null; onClose: () => void; onSaved: () => void }) {
  const { notify } = useApp()
  const [name, setName] = useState('')
  const [match, setMatch] = useState('')
  const [error, setError] = useState<string | null>(null)
  useEffect(() => { if (template) { setName(template.name); setMatch(template.matchText); setError(null) } }, [template])
  const save = () => {
    if (!template) return
    api.templates.save({ ...template, name, matchText: match }).then(() => { notify('Template saved'); onSaved(); onClose() },
      (e: unknown) => setError(e instanceof Error ? e.message : String(e)))
  }
  return (
    <Dialog open={template !== null} onClose={onClose} maxWidth="xs" fullWidth data-template-dialog>
      <DialogTitle>Edit {template?.name}</DialogTitle>
      <DialogContent sx={{ display: 'grid', gap: 2.25, pt: '8px !important' }}>
        <TextField autoFocus label="Name" value={name} onChange={(e) => setName(e.target.value)} slotProps={{ htmlInput: { 'data-field': 'name' } }} />
        <TextField label="Recognised by" value={match} onChange={(e) => setMatch(e.target.value)} helperText={error ?? 'Text found on the statements this template reads.'} error={error !== null}
          slotProps={{ htmlInput: { 'data-field': 'match' } }} />
      </DialogContent>
      <DialogActions sx={{ px: 3, pb: 2.5 }}>
        <Button onClick={onClose}>Cancel</Button>
        <Button variant="contained" disabled={name.trim() === '' || match.trim() === ''} onClick={save}>Save</Button>
      </DialogActions>
    </Dialog>
  )
}

function toBase64(file: File): Promise<string> {
  return new Promise((resolve, reject) => {
    const r = new FileReader()
    r.onload = () => resolve(String(r.result).split(',')[1] ?? '')
    r.onerror = () => reject(r.error ?? new Error('The file could not be read.'))
    r.readAsDataURL(file)
  })
}

type Job = { name: string; run: () => Promise<Draft> }

/** Selects inside table rows: same height as the text rows around them. */
const compact = { '& .MuiSelect-select': { py: 0.6, fontSize: 13.5 } }

/** Every kind of file the import reads (the same list as the engine, ImportService.Extensions). */
export const statementFile = /\.(pdf|csv|tsv|txt|xlsx|xlsm|xls|ods|odt|docx|html?|ofx|qfx|qbo|qif|png|jpe?g|webp|bmp|gif|tiff?)$/i

export function ImportPage() {
  const { fail, dataVersion, dataChanged, notify, accounts, banks, refreshAccounts, refreshBanks } = useApp()
  const theme = useTheme()
  const [drafts, setDrafts] = useState<Draft[]>([])
  const [queue, setQueue] = useState<Job[]>([])
  const [current, setCurrent] = useState<string | null>(null)
  const [over, setOver] = useState(false)
  const [browsing, setBrowsing] = useState(false)
  const [statements, setStatements] = useState<StatementInfo[]>([])
  const [templates, setTemplates] = useState<BankTemplate[]>([])
  const [confirm, setConfirm] = useState<{ title: string; run: () => Promise<void> } | null>(null)
  const [editingStatement, setEditingStatement] = useState<StatementInfo | null>(null)
  const [editingTemplate, setEditingTemplate] = useState<BankTemplate | null>(null)
  const working = useRef(false)

  const reloadLists = useCallback(() => {
    api.statements.list().then(setStatements, fail)
    api.templates.list().then(setTemplates, fail)
  }, [fail])

  useEffect(() => { reloadLists() }, [reloadLists, dataVersion])
  useEffect(() => { void refreshAccounts(); void refreshBanks() }, [refreshAccounts, refreshBanks])

  // Files of one bank in different currencies are flagged.
  const mismatched = useMemo(() => {
    const by = new Map<number, Set<string>>()
    for (const s of statements) if (s.bankId !== null) by.set(s.bankId, (by.get(s.bankId) ?? new Set()).add(s.currency))
    return new Set([...by].filter(([, c]) => c.size > 1).map(([id]) => id))
  }, [statements])
  // PDFs are read by the app: one made to harm (not from the bank) could slow it down, so the person is warned.
  const isPdf = (name: string) => name.toLowerCase().endsWith('.pdf')
  const readingPdf = queue.some((j) => isPdf(j.name)) || drafts.some((d) => isPdf(d.fileName))
  const statementKey = useCallback((s: StatementInfo) => String(s.id), [])
  const templateKey = useCallback((t: BankTemplate) => String(t.id), [])
  const statementOrder = useRowOrder('statements', statements, statementKey)
  const templateOrder = useRowOrder('templates', templates, templateKey)

  // Bank and currency belong to the file's account: changing them here changes them everywhere.
  const updateAccount = (s: StatementInfo, change: { currency?: string; bankId?: number }, message: string) => {
    const a = accounts.find((x) => x.id === s.accountId)
    if (a) api.accounts.update(a, change).then(() => { notify(message); dataChanged() }, fail)
  }

  // Statements are read one after the other; each becomes a draft to review.
  useEffect(() => {
    if (working.current || queue.length === 0) return
    working.current = true
    const [job, ...rest] = queue
    setCurrent(job.name)
    job.run()
      .then((d) => setDrafts((list) => [...list, d, ...(d.others ?? [])]))
      .catch((e) => fail(new Error(`${job.name}: ${e instanceof Error ? e.message : String(e)}`)))
      .finally(() => {
        working.current = false
        setCurrent(null)
        setQueue(rest)
      })
  }, [queue, fail])

  const addFiles = (files: File[]) => {
    const pdfs = files.filter((f) => statementFile.test(f.name))
    if (pdfs.length < files.length) notify('Statements are read from PDF, pictures, CSV, Excel, OpenDocument, Word, OFX, QFX, QBO or QIF files', 'warning')
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
    <Box sx={{ maxWidth: 1320, mx: 'auto', pt: 3, pb: 6 }}>
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
        <Typography variant="h6">Drop statements or exports here</Typography>
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

      {readingPdf && (
        // A PDF from anywhere else can be made to slow the app down or stop it: a short note, not a banner.
        <Typography data-pdf-warning variant="body2" color="text.secondary" title="A PDF from anywhere else can be made to slow the app down or stop it."
          sx={{ mt: 2, display: 'flex', alignItems: 'center', justifyContent: 'center', gap: 0.75 }}>
          <GppMaybe sx={{ fontSize: 18, color: 'warning.main' }} />
          Import only PDFs downloaded from your bank.
        </Typography>
      )}
      <Box sx={{ display: 'grid', gap: 3, mt: 3 }}>
        {drafts.map((d) => (
          <DraftCard key={d.id} draft={d} onDone={() => { setDrafts((list) => list.filter((x) => x.id !== d.id)); reloadLists() }} />
        ))}
      </Box>

      {statements.length > 0 && (
        <SectionCard title="Imported statements" sx={{ mt: 3 }}>
          <Table size="small" sx={{ '& td, & th': { px: 1 } }}>
            <TableHead>
              <TableRow>
                <TableCell padding="none" />
                <TableCell>File</TableCell>
                <TableCell>Bank</TableCell>
                <TableCell>Account</TableCell>
                <TableCell>Currency</TableCell>
                <TableCell>Period</TableCell>
                <TableCell align="right">Operations</TableCell>
                <TableCell align="center">Balances</TableCell>
                <TableCell sx={{ width: ROW_ACTIONS_WIDTH, px: '0 !important' }} />
              </TableRow>
            </TableHead>
            <TableBody>
              {statementOrder.rows.map((s) => (
                <TableRow key={s.id} hover {...statementOrder.rowProps(s)}>
                  {statementOrder.handle(s)}
                  <TableCell sx={{ maxWidth: 240 }}><Typography variant="body2" noWrap title={s.fileName}>{s.fileName}</Typography></TableCell>
                  <TableCell>
                    <TextField
                      select
                      size="small"
                      value={s.bankId ?? 0}
                      onChange={(e) => updateAccount(s, { bankId: Number(e.target.value) }, 'Bank changed')}
                      sx={{ width: 160, ...compact }}
                      slotProps={{ htmlInput: { 'aria-label': 'Bank' } }}
                    >
                      <MenuItem value={0}>No bank</MenuItem>
                      {banks.map((b) => <MenuItem key={b.id} value={b.id}>{b.name}</MenuItem>)}
                    </TextField>
                  </TableCell>
                  <TableCell sx={{ maxWidth: 200 }}><Typography variant="body2" noWrap title={s.account}>{s.account}</Typography></TableCell>
                  <TableCell>
                    <Box sx={{ display: 'flex', alignItems: 'center', gap: 0.75 }}>
                      <TextField select size="small" value={s.currency} onChange={(e) => updateAccount(s, { currency: e.target.value }, 'Currency changed')} sx={{ width: 84, ...compact }} slotProps={{ htmlInput: { 'aria-label': 'Currency' } }}>
                        {[...new Set([s.currency, ...currencies])].map((c) => <MenuItem key={c} value={c}>{c}</MenuItem>)}
                      </TextField>
                      {s.bankId !== null && mismatched.has(s.bankId) && (
                        <Tooltip title="Currency mismatch"><WarningAmber data-currency-mismatch sx={{ fontSize: 19, color: 'warning.main' }} /></Tooltip>
                      )}
                    </Box>
                  </TableCell>
                  <TableCell className="tnum" sx={{ whiteSpace: 'nowrap', color: 'text.secondary' }}>
                    {s.periodStart && s.periodEnd ? `${shortDate(s.periodStart)} – ${shortDate(s.periodEnd)}` : '—'}
                  </TableCell>
                  <TableCell align="right" className="tnum">{s.transactionCount}</TableCell>
                  <TableCell align="center">
                    {s.verified
                      ? <Tooltip title="Balanced to the cent"><CheckCircle sx={{ fontSize: 18, color: ok }} /></Tooltip>
                      : <Tooltip title="Not checked against balances"><ErrorOutline sx={{ fontSize: 18, color: 'warning.main' }} /></Tooltip>}
                  </TableCell>
                  <TableCell padding="none" align="right" sx={{ pr: '4px !important' }}>
                    <RowActions editLabel={`Edit ${s.fileName}`} onEdit={() => setEditingStatement(s)} onDelete={() => setConfirm({
                      title: `Delete ${s.fileName} and its ${plural(s.transactionCount, 'operation')}?`,
                      run: async () => { await api.statements.remove(s.id); notify('Statement deleted'); dataChanged() },
                    })} />
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
                <TableCell padding="none" />
                <TableCell>Name</TableCell>
                <TableCell>Recognised by</TableCell>
                <TableCell sx={{ width: ROW_ACTIONS_WIDTH, px: 0 }} />
              </TableRow>
            </TableHead>
            <TableBody>
              {templateOrder.rows.map((t) => (
                <TableRow key={t.id} hover {...templateOrder.rowProps(t)}>
                  {templateOrder.handle(t)}
                  <TableCell><TextCell value={t.name} label="Template name" onSave={(name) => void api.templates.save({ ...t, name }).then(() => { notify('Template saved'); reloadLists() }, fail)} /></TableCell>
                  <TableCell sx={{ color: 'text.secondary' }}>
                    <TextCell value={t.matchText} label="Recognised by" maxLength={200} onSave={(matchText) => void api.templates.save({ ...t, matchText }).then(() => { notify('Template saved'); reloadLists() }, fail)}>“{t.matchText}”</TextCell>
                  </TableCell>
                  <TableCell padding="none" align="right" sx={{ pr: 0.5 }}>
                    <RowActions editLabel={`Edit the template ${t.name}`} onEdit={() => setEditingTemplate(t)} onDelete={() => setConfirm({
                      title: `Delete the template ${t.name}?`,
                      run: async () => { await api.templates.remove(t.id); reloadLists() },
                    })} />
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </SectionCard>
      )}

      <StatementDialog statement={editingStatement} onClose={() => setEditingStatement(null)} />
      <TemplateDialog template={editingTemplate} onClose={() => setEditingTemplate(null)} onSaved={reloadLists} />
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
