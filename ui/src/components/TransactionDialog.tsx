import { useEffect, useState } from 'react'
import {
  Alert, Autocomplete, Box, Button, Dialog, DialogActions, DialogContent, DialogTitle, InputAdornment,
  MenuItem, TextField, ToggleButton, ToggleButtonGroup,
} from '@mui/material'
import { DatePicker } from '@mui/x-date-pickers/DatePicker'
import dayjs, { type Dayjs } from 'dayjs'
import { api, type TransactionView } from '../api'
import { useApp } from '../AppContext'
import { today } from '../format'
import { CategorySelect, ConfirmDialog } from './common'

export const pickerFormats: Record<string, string> = {
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
  const { accounts, banks, settings, notify, fail, dataChanged, refreshAccounts, refreshBanks, askScope, category } = useApp()
  const [date, setDate] = useState<Dayjs | null>(dayjs())
  const [direction, setDirection] = useState<'out' | 'in'>('out')
  const [amount, setAmount] = useState('')
  const [brand, setBrand] = useState('')
  const [categoryId, setCategoryId] = useState('other')
  const [accountId, setAccountId] = useState<number | ''>('')
  const [note, setNote] = useState('')
  const [earned, setEarned] = useState('')
  const [confirmDelete, setConfirmDelete] = useState(false)

  useEffect(() => {
    if (!open) return
    void refreshAccounts()
    void refreshBanks()
    if (tx) {
      setDate(dayjs(tx.date))
      setDirection(tx.amount < 0 ? 'out' : 'in')
      setAmount(Math.abs(tx.amount).toFixed(2))
      setBrand(tx.brand)
      setCategoryId(tx.categoryId)
      setAccountId(tx.accountId)
      setNote(tx.note)
      setEarned(tx.earned ? tx.earned.toFixed(2) : '')
    } else {
      setDate(dayjs(defaultDate ?? today()))
      setDirection('out')
      setAmount('')
      setBrand('')
      setCategoryId('other')
      setAccountId('')
      setNote('')
      setEarned('')
    }
  }, [open, tx, defaultDate, refreshAccounts, refreshBanks])
  // A new operation goes in the first account unless another is chosen.
  useEffect(() => { if (open && !tx && accountId === '' && accounts.length) setAccountId(accounts[0].id) }, [open, tx, accountId, accounts])

  const value = Number(amount.replace(',', '.'))
  const valid = date?.isValid() && Number.isFinite(value) && value > 0 && brand.trim().length > 0 && accountId !== ''

  // Exactly the same operation already that day (same amount, brand and account): said while typing, never refused
  // (two identical coffees in a day happen).
  const [same, setSame] = useState(0)
  const signed = Math.round(value * 100) / 100 * (direction === 'out' ? -1 : 1)
  const day = date?.isValid() ? date.format('YYYY-MM-DD') : null
  useEffect(() => {
    if (!open || !valid || !day) { setSame(0); return }
    let live = true
    const t = setTimeout(() => api.tx.sameOnDay(day, signed, brand.trim(), accountId, tx?.id ?? null)
      .then((r) => { if (live) setSame(r.count) }, () => undefined), 250)
    return () => { live = false; clearTimeout(t) }
  }, [open, valid, day, signed, brand, accountId, tx])


  // Money coming back from an investment (or a transfer in): the person may say how much of it was gain.
  const canEarn = direction === 'in' && category(categoryId).kind === 'transfer'
  const earnedValue = earned.trim() === '' ? null : Number(earned.replace(',', '.'))
  const earnedValid = !canEarn || earnedValue === null || (Number.isFinite(earnedValue) && earnedValue >= 0 && earnedValue <= value)

  const save = async () => {
    if (!earnedValid) return
    if (!valid || !date) return
    const input = {
      date: date.format('YYYY-MM-DD'),
      amount: Math.round(value * 100) / 100 * (direction === 'out' ? -1 : 1),
      brand: brand.trim(),
      categoryId,
      accountId,
      note: note.trim() || null,
      earned: canEarn && earnedValue ? Math.round(earnedValue * 100) / 100 : null,
    }
    // A new category: for this operation only, or for the whole brand (the person decides).
    let allOfBrand = true
    if (tx && categoryId !== tx.categoryId) {
      const answer = await askScope(input.brand, category(categoryId).name)
      if (answer === null) return
      allOfBrand = answer
    }
    try {
      if (tx) await api.tx.update(tx.id, input, allOfBrand)
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
        <CategorySelect value={categoryId} onChange={setCategoryId} />
        <TextField select label="Account" value={accountId} error={accountId === ''} onChange={(e) => setAccountId(Number(e.target.value))}>
          {accounts.map((a) => <MenuItem key={a.id} value={a.id}>{a.name} · {banks.find((b) => b.id === a.bankId)?.name}</MenuItem>)}
        </TextField>
        {canEarn && (
          <TextField
            data-earned
            label="Of which earned (gain, interest)"
            value={earned}
            onChange={(e) => setEarned(e.target.value.replace(/[^\d.,]/g, ''))}
            error={!earnedValid}
            helperText={earnedValid
              ? 'Only this part counts as income. The rest is your own money coming back. Leave empty if you do not know: nothing is guessed.'
              : 'Between 0 and the amount received.'}
            slotProps={{ input: { className: 'tnum' }, htmlInput: { inputMode: 'decimal' } }}
          />
        )}
        {same > 0 && (
          <Alert data-same-operation severity="warning" variant="outlined" sx={{ py: 0 }}>
            {same === 1 ? 'The same operation is already on this day.' : `The same operation is already ${same} times on this day.`}
          </Alert>
        )}
        <TextField label="Note" value={note} onChange={(e) => setNote(e.target.value)} multiline minRows={1} maxRows={4} />
      </DialogContent>
      <DialogActions sx={{ px: 3, pb: 2.5 }}>
        {tx && <Button color="error" onClick={() => setConfirmDelete(true)} sx={{ mr: 'auto' }}>Delete</Button>}
        <Button onClick={onClose}>Cancel</Button>
        <Button variant="contained" disabled={!valid || !earnedValid} onClick={() => void save()}>{tx ? 'Save' : 'Add'}</Button>
      </DialogActions>
      <ConfirmDialog open={confirmDelete} title="Delete this operation?" confirm="Delete" danger onClose={() => setConfirmDelete(false)} onConfirm={() => void remove()} />
    </Dialog>
  )
}
