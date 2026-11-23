import { useEffect, useState } from 'react'
import { Box, Button, Checkbox, Dialog, DialogActions, DialogContent, DialogTitle, FormControlLabel, Table, TableBody, TableCell, TableHead, TableRow, TextField, Typography } from '@mui/material'
import Check from '@mui/icons-material/CheckRounded'
import { api, type SupportedBank, type SupportedCountry } from '../api'
import { useApp } from '../AppContext'
import { SectionCard } from '../components/common'
import { Flag } from '../components/flags'
import { ROW_EDIT_WIDTH, RowActions } from '../components/RowActions'

/** The bank whose pencil was clicked (kept while its dialog closes); n: the dialog is made again at each opening. */
interface Editing { country: SupportedCountry; bank: SupportedBank; open: boolean; n: number }

/**
 * Settings > Information: the banks whose statements are read by themselves, a table per country. The list is a JSON
 * file of the data folder; each bank is changed with its pencil.
 */
export function InformationPanel() {
  const { fail } = useApp()
  const [countries, setCountries] = useState<SupportedCountry[] | null>(null)
  const [editing, setEditing] = useState<Editing | null>(null)
  useEffect(() => { api.banks.supported().then(setCountries, fail) }, [fail])
  if (!countries) return null
  const mark = (on: boolean, label: string) => on
    ? <Check sx={{ fontSize: 20, color: 'success.main', display: 'block', mx: 'auto' }} aria-label={label} />
    : <Typography component="span" sx={{ color: 'text.disabled', display: 'block', textAlign: 'center' }} aria-label={`Not ${label.toLowerCase()}`}>—</Typography>
  return (
    <Box sx={{ display: 'grid', gap: 3 }} data-supported-banks>
      {countries.map((c) => (
        <SectionCard
          key={c.code}
          title={<Box component="span" sx={{ display: 'inline-flex', alignItems: 'center', gap: 1.25 }}><Flag code={c.code} />{c.name}</Box>}
        >
          <Table size="small" data-country={c.code}>
            <TableHead>
              <TableRow>
                <TableCell>Name</TableCell>
                <TableCell align="center" sx={{ width: 160 }}>Considered</TableCell>
                <TableCell align="center" sx={{ width: 160 }}>Confirmed</TableCell>
                <TableCell sx={{ width: ROW_EDIT_WIDTH, px: 0 }} />
              </TableRow>
            </TableHead>
            <TableBody>
              {c.banks.map((b) => (
                <TableRow key={b.name} hover data-supported-bank={b.name}>
                  <TableCell>{b.name}</TableCell>
                  <TableCell align="center" data-considered={b.considered}>{mark(b.considered, 'Considered')}</TableCell>
                  <TableCell align="center" data-confirmed={b.confirmed}>{mark(b.confirmed, 'Confirmed')}</TableCell>
                  <TableCell padding="none" align="right" sx={{ pr: 0.5 }}>
                    <RowActions editLabel={`Edit ${b.name}`} onEdit={() => setEditing((e) => ({ country: c, bank: b, open: true, n: (e?.n ?? 0) + 1 }))} />
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </SectionCard>
      ))}
      {editing && <SupportedBankDialog key={editing.n} editing={editing} onClose={() => setEditing({ ...editing, open: false })} onSaved={setCountries} />}
    </Box>
  )
}

/** A bank of Settings > Information changed from its pencil: its name, and whether it is considered and confirmed. */
function SupportedBankDialog({ editing, onClose, onSaved }: { editing: Editing; onClose: () => void; onSaved: (list: SupportedCountry[]) => void }) {
  const { notify, fail } = useApp()
  const [draft, setDraft] = useState<SupportedBank>(editing.bank)
  const save = () => {
    api.banks.supportedSave(editing.country.code, editing.bank.name, draft).then((list) => { onSaved(list); notify('Bank saved'); onClose() }, fail)
  }
  return (
    <Dialog open={editing.open} onClose={onClose} maxWidth="xs" fullWidth data-supported-bank-dialog>
      <DialogTitle>{`Edit ${editing.bank.name}`}</DialogTitle>
      <DialogContent sx={{ display: 'grid', gap: 1, pt: '8px !important' }}>
        <TextField autoFocus label="Name" value={draft.name} onChange={(e) => setDraft({ ...draft, name: e.target.value })} slotProps={{ htmlInput: { 'data-field': 'name' } }} sx={{ mb: 1 }} />
        <FormControlLabel label="Considered" control={<Checkbox checked={draft.considered} onChange={(e) => setDraft({ ...draft, considered: e.target.checked })} slotProps={{ input: { 'data-field': 'considered' } as object }} />} />
        <FormControlLabel label="Confirmed" control={<Checkbox checked={draft.confirmed} onChange={(e) => setDraft({ ...draft, confirmed: e.target.checked })} slotProps={{ input: { 'data-field': 'confirmed' } as object }} />} />
      </DialogContent>
      <DialogActions sx={{ px: 3, pb: 2.5 }}>
        <Button onClick={onClose}>Cancel</Button>
        <Button variant="contained" disabled={draft.name.trim() === ''} onClick={save}>Save</Button>
      </DialogActions>
    </Dialog>
  )
}
