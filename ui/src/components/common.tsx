import { useState, type ReactNode } from 'react'
import {
  Box, Button, Dialog, DialogActions, DialogContent, DialogTitle, Divider, ListItemIcon, ListItemText, ListSubheader, Menu, MenuItem,
  Typography, useTheme, type SxProps, type Theme,
} from '@mui/material'
import Check from '@mui/icons-material/CheckRounded'
import { useApp } from '../AppContext'
import { money } from '../format'
import { CategoryIcon } from './CategoryIcon'

export function Money({ value, currency, sign, sx, muted }: { value: number; currency: string; sign?: boolean; sx?: SxProps<Theme>; muted?: boolean }) {
  const theme = useTheme()
  const color = muted ? theme.palette.text.secondary : value > 0 && sign ? theme.palette.money.positive : theme.palette.text.primary
  return (
    <Box component="span" className="tnum" sx={{ color, whiteSpace: 'nowrap', fontWeight: 520, ...sx as object }}>
      {money(value, currency, { sign })}
    </Box>
  )
}

/** Category picker grouped by kind; used in tables, drafts and dialogs. */
export function CategoryMenu({ anchor, value, onClose, onPick }: {
  anchor: HTMLElement | null
  value?: string
  onClose: () => void
  onPick: (id: string) => void
}) {
  const { categories } = useApp()
  const groups: [string, string][] = [['expense', 'Expenses'], ['income', 'Income'], ['transfer', 'Between my accounts']]
  return (
    <Menu anchorEl={anchor} open={anchor !== null} onClose={onClose} slotProps={{ paper: { sx: { maxHeight: 460, width: 250 } } }}>
      {groups.flatMap(([kind, label]) => [
        <ListSubheader key={'h' + kind} sx={{ lineHeight: '30px', fontSize: 11.5, fontWeight: 650, letterSpacing: '.04em', textTransform: 'uppercase', bgcolor: 'background.paper' }}>
          {label}
        </ListSubheader>,
        ...categories.filter((c) => c.kind === kind).map((c) => (
          <MenuItem key={c.id} selected={c.id === value} onClick={() => { onPick(c.id); onClose() }}>
            <ListItemIcon><CategoryIcon name={c.icon} fontSize="small" /></ListItemIcon>
            <ListItemText>{c.name}</ListItemText>
            {c.id === value && <Check fontSize="small" sx={{ color: 'primary.main', ml: 1 }} />}
          </MenuItem>
        )),
      ])}
    </Menu>
  )
}

/** Category shown as a soft pill; clicking it opens the picker. */
export function CategoryPill({ id, onChange, disabled }: { id: string; onChange?: (id: string) => void; disabled?: boolean }) {
  const { category } = useApp()
  const theme = useTheme()
  const [anchor, setAnchor] = useState<HTMLElement | null>(null)
  const c = category(id)
  const dark = theme.palette.mode === 'dark'
  return (
    <>
      <Box
        component="button"
        disabled={disabled || !onChange}
        onClick={(e) => { e.stopPropagation(); setAnchor(e.currentTarget) }}
        sx={{
          display: 'inline-flex',
          alignItems: 'center',
          gap: 0.75,
          border: 0,
          borderRadius: 999,
          py: 0.4,
          pl: 0.9,
          pr: 1.25,
          maxWidth: '100%',
          font: 'inherit',
          fontSize: 13,
          fontWeight: 520,
          color: c.kind === 'transfer' ? 'text.secondary' : 'text.primary',
          bgcolor: dark ? 'rgba(255,255,255,.06)' : 'rgba(0,0,0,.045)',
          cursor: onChange ? 'pointer' : 'default',
          transition: 'background-color 120ms',
          '&:hover:not(:disabled)': { bgcolor: dark ? 'rgba(255,255,255,.1)' : 'rgba(0,0,0,.075)' },
          '&:focus-visible': { outline: `2px solid ${theme.palette.primary.main}` },
        }}
      >
        <CategoryIcon name={c.icon} sx={{ fontSize: 16, color: 'text.secondary' }} />
        <Box component="span" sx={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{c.name}</Box>
      </Box>
      {onChange && <CategoryMenu anchor={anchor} value={id} onClose={() => setAnchor(null)} onPick={onChange} />}
    </>
  )
}

export function ConfirmDialog({ open, title, body, confirm, danger, onClose, onConfirm }: {
  open: boolean
  title: string
  body?: ReactNode
  confirm: string
  danger?: boolean
  onClose: () => void
  onConfirm: () => void
}) {
  return (
    <Dialog open={open} onClose={onClose} maxWidth="xs" fullWidth>
      <DialogTitle>{title}</DialogTitle>
      {body && <DialogContent><Typography color="text.secondary">{body}</Typography></DialogContent>}
      <DialogActions sx={{ px: 3, pb: 2.5 }}>
        <Button onClick={onClose}>Cancel</Button>
        <Button variant="contained" color={danger ? 'error' : 'primary'} onClick={() => { onConfirm(); onClose() }}>{confirm}</Button>
      </DialogActions>
    </Dialog>
  )
}

export function SectionCard({ title, action, children, sx }: { title?: ReactNode; action?: ReactNode; children: ReactNode; sx?: SxProps<Theme> }) {
  return (
    <Box sx={{ border: 1, borderColor: 'divider', borderRadius: '20px', bgcolor: 'background.paper', p: { xs: 2, md: 3 }, ...sx as object }}>
      {(title || action) && (
        <Box sx={{ display: 'flex', alignItems: 'center', gap: 2, mb: 2 }}>
          {title && <Typography variant="h6" sx={{ flex: 1 }}>{title}</Typography>}
          {action}
        </Box>
      )}
      {children}
    </Box>
  )
}

export function EmptyState({ icon, title, action }: { icon: ReactNode; title: string; action?: ReactNode }) {
  return (
    <Box sx={{ display: 'grid', placeItems: 'center', textAlign: 'center', py: 10, gap: 2, color: 'text.secondary' }}>
      <Box sx={{ width: 72, height: 72, borderRadius: '24px', display: 'grid', placeItems: 'center', bgcolor: 'action.hover', '& svg': { fontSize: 34 } }}>{icon}</Box>
      <Typography variant="h6" color="text.primary">{title}</Typography>
      {action}
    </Box>
  )
}

export function MenuDivider() {
  return <Divider sx={{ my: 0.5 }} />
}

export function PageHeader({ title, children }: { title: string; children?: ReactNode }) {
  return (
    <Box sx={{ display: 'flex', alignItems: 'center', gap: 2, flexWrap: 'wrap', mb: 3 }}>
      <Typography variant="h4" sx={{ flex: '1 1 auto' }}>{title}</Typography>
      {children}
    </Box>
  )
}
