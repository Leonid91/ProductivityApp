import { useEffect, useMemo, useState } from 'react'
import {
  Autocomplete, Box, Button, Dialog, DialogActions, DialogContent, DialogTitle, InputAdornment, ListItemIcon, ListItemText, ListSubheader,
  MenuItem, TextField, ToggleButton, ToggleButtonGroup,
} from '@mui/material'
import { DatePicker } from '@mui/x-date-pickers/DatePicker'
import dayjs, { type Dayjs } from 'dayjs'
import { api, type TransactionView } from '../api'
import { useApp } from '../AppContext'
import { today } from '../format'
import { CategoryIcon } from './CategoryIcon'
import { ConfirmDialog } from './common'

const pickerFormats: Record<string, string> = {
  'DD month YYYY': 'DD MMMM YYYY', 'YYYY-MM-DD': 'YYYY-MM-DD', 'DD-MM-YYYY': 'DD-MM-YYYY', 'DD/MM/YYYY': 'DD/MM/YYYY', 'MM/DD/YYYY': 'MM/DD/YYYY',
}

/** Add or edit one operation by hand (cash spending, a correction...). */
export function TransactionDialog({ open, tx, brands, defaultDate, onClose }: {
  open: boolean
  tx: TransactionView | null
  brands: string[]
  defaultDate?: string
  onClose: () => void
}) {
  const { categories, accounts, settings, notify, fail, dataChanged, refreshAccounts } = useApp()
  const [date, setDate] = useState<Dayjs | null>(dayjs())
  const [direction, setDirection] = useState<'out' | 'in'>('out')
  const [amount, setAmount] = useState('')
  const [brand, setBrand] = useState('')
  const [categoryId, setCategoryId] = useState('other')
  const [accountId, setAccountId] = useState<number | 'manual'>('manual')
  const [note, setNote] = useState('')
  const [confirmDelete, setConfirmDelete] = useState(false)

  useEffect(() => {
    if (!open) return
    void refreshAccounts()
    if (tx) {
      setDate(dayjs(tx.date))
      setDirection(tx.amount < 0 ? 'out' : 'in')
      setAmount(Math.abs(tx.amount).toFixed(2))
      setBrand(tx.brand)
      setCategoryId(tx.categoryId)
      setAccountId(tx.accountId)
      setNote(tx.note)
    } else {
      setDate(dayjs(defaultDate ?? today()))
      setDirection('out')
      setAmount('')
      setBrand('')
      setCategoryId('other')
      setAccountId('manual')
      setNote('')
    }
  }, [open, tx, defaultDate, refreshAccounts])

  const value = Number(amount.replace(',', '.'))
  const valid = date?.isValid() && Number.isFinite(value) && value > 0 && brand.trim().length > 0

  const grouped = useMemo(() => [
    ['Expenses', categories.filter((c) => c.kind === 'expense' && c.group !== 'exceptional')],
    ['Exceptional', categories.filter((c) => c.group === 'exceptional')],
    ['Income', categories.filter((c) => c.kind === 'income')],
    ['Between my accounts', categories.filter((c) => c.kind === 'transfer')],
  ] as const, [categories])

  const save = async () => {
    if (!valid || !date) return
    const input = {
      date: date.format('YYYY-MM-DD'),
      amount: Math.round(value * 100) / 100 * (direction === 'out' ? -1 : 1),
      brand: brand.trim(),
      categoryId,
      accountId: accountId === 'manual' ? null : accountId,
      note: note.trim() || null,
    }
    try {
      if (tx) await api.tx.update(tx.id, input)
      else await api.tx.create(input)
      notify(tx ? 'Operation updated' : 'Operation added')
      dataChanged()
      onClose()
    } catch (e) {
      fail(e)
    }
  }

  const remove = async () => {
    if (!tx) return
    try {
      await api.tx.remove(tx.id)
      notify('Operation deleted')
      dataChanged()
      onClose()
    } catch (e) {
      fail(e)
    }
  }

  return (
    <Dialog open={open} onClose={onClose} maxWidth="sm" fullWidth>
      <DialogTitle>{tx ? 'Edit operation' : 'Add operation'}</DialogTitle>
      <DialogContent sx={{ display: 'grid', gap: 2.25, pt: '8px !important' }}>
        <ToggleButtonGroup exclusive value={direction} onChange={(_, v) => v && setDirection(v)} sx={{ justifySelf: 'start' }}>
          <ToggleButton value="out">Expense</ToggleButton>
          <ToggleButton value="in">Income</ToggleButton>
        </ToggleButtonGroup>
        <Box sx={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 2 }}>
          <DatePicker label="Date" value={date} onChange={setDate} format={pickerFormats[settings.dateFormat] ?? 'DD MMMM YYYY'} />
          <TextField
            label="Amount"
            value={amount}
            onChange={(e) => setAmount(e.target.value.replace(/[^\d.,]/g, ''))}
            slotProps={{ input: { startAdornment: <InputAdornment position="start">{direction === 'out' ? '−' : '+'}</InputAdornment>, className: 'tnum' }, htmlInput: { inputMode: 'decimal' } }}
          />
        </Box>
        <Autocomplete
          freeSolo
          options={brands}
          inputValue={brand}
          onInputChange={(_, v) => setBrand(v)}
          renderInput={(p) => <TextField {...p} label="Brand Name" />}
        />
        <TextField select label="Category" value={categoryId} onChange={(e) => setCategoryId(e.target.value)}>
          {grouped.flatMap(([label, list]) => [
            <ListSubheader key={label} sx={{ lineHeight: '30px', fontSize: 11.5, fontWeight: 650, textTransform: 'uppercase', letterSpacing: '.04em' }}>{label}</ListSubheader>,
            ...list.map((c) => (
              <MenuItem key={c.id} value={c.id}>
                <Box sx={{ display: 'flex', alignItems: 'center' }}>
                  <ListItemIcon sx={{ minWidth: 32 }}><CategoryIcon name={c.icon} fontSize="small" /></ListItemIcon>
                  <ListItemText>{c.name}</ListItemText>
                </Box>
              </MenuItem>
            )),
          ])}
        </TextField>
        <TextField select label="Account" value={accountId} onChange={(e) => setAccountId(e.target.value === 'manual' ? 'manual' : Number(e.target.value))}>
          {!tx && !accounts.some((a) => a.name === 'Cash & manual entries') && <MenuItem value="manual">Cash & manual entries</MenuItem>}
          {accounts.map((a) => <MenuItem key={a.id} value={a.id}>{a.name}</MenuItem>)}
        </TextField>
        <TextField label="Note" value={note} onChange={(e) => setNote(e.target.value)} multiline minRows={1} maxRows={4} />
      </DialogContent>
      <DialogActions sx={{ px: 3, pb: 2.5 }}>
        {tx && <Button color="error" onClick={() => setConfirmDelete(true)} sx={{ mr: 'auto' }}>Delete</Button>}
        <Button onClick={onClose}>Cancel</Button>
        <Button variant="contained" disabled={!valid} onClick={() => void save()}>{tx ? 'Save' : 'Add'}</Button>
      </DialogActions>
      <ConfirmDialog open={confirmDelete} title="Delete this operation?" confirm="Delete" danger onClose={() => setConfirmDelete(false)} onConfirm={() => void remove()} />
    </Dialog>
  )
}
