import { useState, type ReactNode } from 'react'
import {
  Box, Button, Dialog, DialogActions, DialogContent, DialogTitle, Divider, ListItemIcon, ListItemText, ListSubheader, Menu, MenuItem,
  Tooltip, Typography, useTheme, type SxProps, type Theme,
} from '@mui/material'
import { alpha } from '@mui/material/styles'
import Check from '@mui/icons-material/CheckRounded'
import type { Category } from '../api'
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

/** The colour a category is shown in: its own, gold for exceptional expenses, or none. */
export function useCategoryColor(): (c: Category) => string | null {
  const theme = useTheme()
  return (c) => c.color ?? (c.group === 'exceptional' ? theme.palette.gold.text : null)
}

const sections: [string, (root: Category) => boolean][] = [
  ['expense', (c) => c.kind === 'expense' && c.group !== 'exceptional'],
  ['exceptional', (c) => c.group === 'exceptional'],
  ['income', (c) => c.kind === 'income'],
  ['transfer', (c) => c.kind === 'transfer'],
]

/**
 * The four first-layer labels the categories sit under, with the names the person gave them (Categories);
 * a nested category shows under the label of its top category.
 */
export function useCategorySections(): [string, string, (root: Category) => boolean][] {
  const { settings } = useApp()
  return sections.map(([kind, test]) => [kind, settings[`section.${kind}`] ?? kind, test])
}

/** Category picker grouped by kind, nested categories indented under theirs; used in tables, drafts and dialogs. */
export function CategoryMenu({ anchor, value, onClose, onPick }: {
  anchor: HTMLElement | null
  value?: string
  onClose: () => void
  onPick: (id: string) => void
}) {
  const { tree } = useApp()
  const theme = useTheme()
  const colorOf = useCategoryColor()
  const categorySections = useCategorySections()
  return (
    <Menu anchorEl={anchor} open={anchor !== null} onClose={onClose} slotProps={{ paper: { sx: { maxHeight: 480, minWidth: 260 } } }}>
      {categorySections.flatMap(([kind, label, test]) => [
        <ListSubheader key={'h' + kind} sx={{ lineHeight: '30px', fontSize: 11.5, fontWeight: 650, letterSpacing: '.04em', textTransform: 'uppercase', bgcolor: 'background.paper', color: kind === 'exceptional' ? theme.palette.gold.text : undefined }}>
          {label}
        </ListSubheader>,
        ...tree.filter((e) => test(e.root)).map(({ c, depth }) => {
          const color = colorOf(c)
          return (
            <MenuItem key={c.id} selected={c.id === value} onClick={() => { onPick(c.id); onClose() }} sx={{ pl: 2 + depth * 2.25, ...(color ? { color, fontWeight: 600 } : {}) }}>
              <ListItemIcon sx={color ? { color } : undefined}><CategoryIcon name={c.icon} fontSize="small" /></ListItemIcon>
              <ListItemText>{c.name}</ListItemText>
              {c.id === value && <Check fontSize="small" sx={{ color: 'primary.main', ml: 1 }} />}
            </MenuItem>
          )
        }),
      ])}
    </Menu>
  )
}

/** Category shown as a soft pill; clicking it opens the picker. */
/** "Expenses › Food › Snacks": the label and every category a category is in. */
export function useCategoryPath(): (c: Category) => string {
  const { category } = useApp()
  const sections = useCategorySections()
  return (c) => {
    const names = [c.name]
    let root = c
    while (root.parentId) { root = category(root.parentId); names.unshift(root.name) }
    const label = sections.find(([, , test]) => test(root))?.[1]
    return (label ? [label, ...names] : names).join(' › ')
  }
}

export function CategoryPill({ id, onChange, disabled }: { id: string; onChange?: (id: string) => void; disabled?: boolean }) {
  const { category } = useApp()
  const theme = useTheme()
  const [anchor, setAnchor] = useState<HTMLElement | null>(null)
  const c = category(id)
  const dark = theme.palette.mode === 'dark'
  const color = useCategoryColor()(c)
  const path = useCategoryPath()
  return (
    <>
      <Tooltip title={path(c)} placement="top-start" enterDelay={400}>
        <Box component="span" data-path={path(c)} sx={{ display: 'inline-flex', maxWidth: '100%' }}>
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
              fontWeight: color ? 620 : 520,
              color: color ?? (c.kind === 'transfer' ? 'text.secondary' : 'text.primary'),
              bgcolor: color ? alpha(color, dark ? 0.2 : 0.13) : dark ? 'rgba(255,255,255,.06)' : 'rgba(0,0,0,.045)',
              cursor: onChange ? 'pointer' : 'default',
              transition: 'background-color 120ms',
              '&:hover:not(:disabled)': { bgcolor: color ? alpha(color, dark ? 0.28 : 0.2) : dark ? 'rgba(255,255,255,.1)' : 'rgba(0,0,0,.075)' },
              '&:focus-visible': { outline: `2px solid ${theme.palette.primary.main}` },
            }}
          >
            <CategoryIcon name={c.icon} sx={{ fontSize: 16, color: color ?? 'text.secondary' }} />
            <Box component="span" sx={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{c.name}</Box>
          </Box>
        </Box>
      </Tooltip>
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
          {title ? <Typography variant="h6" sx={{ flex: 1 }}>{title}</Typography> : <Box sx={{ flex: 1 }} />}
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
