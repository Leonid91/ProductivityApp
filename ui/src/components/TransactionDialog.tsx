import { useEffect, useState } from 'react'
import {
  Autocomplete, Box, Button, Dialog, DialogActions, DialogContent, DialogTitle, InputAdornment, ListItemIcon, ListItemText, ListSubheader,
  MenuItem, TextField, ToggleButton, ToggleButtonGroup, useTheme,
} from '@mui/material'
import { DatePicker } from '@mui/x-date-pickers/DatePicker'
import dayjs, { type Dayjs } from 'dayjs'
import { api, type TransactionView } from '../api'
import { useApp } from '../AppContext'
import { today } from '../format'
import { CategoryIcon } from './CategoryIcon'
import { ConfirmDialog, categorySections, useCategoryColor } from './common'

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
  const { tree, accounts, settings, notify, fail, dataChanged, refreshAccounts, askScope, category } = useApp()
  const colorOf = useCategoryColor()
  const theme = useTheme()
  const gold = theme.palette.gold.text
  const [date, setDate] = useState<Dayjs | null>(dayjs())
  const [direction, setDirection] = useState<'out' | 'in'>('out')
  const [amount, setAmount] = useState('')
  const [brand, setBrand] = useState('')
  const [categoryId, setCategoryId] = useState('other')
  const [accountId, setAccountId] = useState<number | 'manual'>('manual')
  const [note, setNote] = useState('')
  const [earned, setEarned] = useState('')
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
      setEarned(tx.earned ? tx.earned.toFixed(2) : '')
    } else {
      setDate(dayjs(defaultDate ?? today()))
      setDirection('out')
      setAmount('')
      setBrand('')
      setCategoryId('other')
      setAccountId('manual')
      setNote('')
      setEarned('')
    }
  }, [open, tx, defaultDate, refreshAccounts])

  const value = Number(amount.replace(',', '.'))
  const valid = date?.isValid() && Number.isFinite(value) && value > 0 && brand.trim().length > 0


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
      accountId: accountId === 'manual' ? null : accountId,
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
        <TextField
          select
          label="Category"
          value={categoryId}
          onChange={(e) => setCategoryId(e.target.value)}
          // A category's colour (gold for exceptional expenses) shows everywhere, here too: the chosen value and every item.
          slotProps={{ select: { sx: colorOf(category(categoryId)) ? { color: colorOf(category(categoryId)), fontWeight: 600, '& .MuiListItemIcon-root': { color: colorOf(category(categoryId)) } } : undefined } }}
        >
          {categorySections.flatMap(([kind, label, test]) => [
            <ListSubheader key={label} sx={{ lineHeight: '30px', fontSize: 11.5, fontWeight: 650, textTransform: 'uppercase', letterSpacing: '.04em', color: kind === 'exceptional' ? gold : undefined }}>{label}</ListSubheader>,
            ...tree.filter((e) => test(e.root)).map(({ c, depth }) => {
              const color = colorOf(c)
              return (
                <MenuItem key={c.id} value={c.id} data-exceptional={c.group === 'exceptional' || undefined} sx={{ pl: 2 + depth * 2.25, ...(color ? { color, fontWeight: 600 } : {}) }}>
                  <Box sx={{ display: 'flex', alignItems: 'center' }}>
                    <ListItemIcon sx={{ minWidth: 32, color: color ?? undefined }}><CategoryIcon name={c.icon} fontSize="small" /></ListItemIcon>
                    <ListItemText slotProps={{ primary: { sx: color ? { color, fontWeight: 600 } : undefined } }}>{c.name}</ListItemText>
                  </Box>
                </MenuItem>
              )
            }),
          ])}
        </TextField>
        <TextField select label="Account" value={accountId} onChange={(e) => setAccountId(e.target.value === 'manual' ? 'manual' : Number(e.target.value))}>
          {!tx && !accounts.some((a) => a.name === 'Cash & manual entries') && <MenuItem value="manual">Cash & manual entries</MenuItem>}
          {accounts.map((a) => <MenuItem key={a.id} value={a.id}>{a.name}</MenuItem>)}
        </TextField>
        {canEarn && (
          <TextField
            data-earned
            label="Of which earned (gain, interest)"
            value={earned}
            onChange={(e) => setEarned(e.target.value.replace(/[^\d.,]/g, ''))}
            error={!earnedValid}
            helperText={earnedValid
              ? 'Only this part counts as income; the rest is your own money coming back. Leave empty if you do not know: nothing is guessed.'
              : 'Between 0 and the amount received.'}
            slotProps={{ input: { className: 'tnum' }, htmlInput: { inputMode: 'decimal' } }}
          />
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
