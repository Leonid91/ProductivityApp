import { cloneElement, useEffect, useRef, useState, type CSSProperties, type ReactElement, type ReactNode } from 'react'
import { flushSync } from 'react-dom'
import {
  Box, Button, Dialog, DialogActions, DialogContent, DialogTitle, Divider, ListItemIcon, ListItemText, ListSubheader, Menu, MenuItem,
  TextField, Tooltip, Typography, useTheme, type SxProps, type Theme, type TooltipProps,
} from '@mui/material'
import { alpha } from '@mui/material/styles'
import Check from '@mui/icons-material/CheckRounded'
import type { Category, CategoryKind } from '../api'
import { useApp } from '../AppContext'
import { money } from '../format'
import { useAmountsHidden } from '../mask'
import { CategoryIcon, categoryIconOf } from './CategoryIcon'
import { Mark } from './plain'

/**
 * A tooltip made only when the mouse (or the keyboard) first comes to its element: a table of hundreds of lines draws
 * much faster, and the tooltip shows as before (after its delay, gone when the mouse leaves).
 */
export function LazyTooltip({ children, enterDelay = 100, ...props }: Omit<TooltipProps, 'open' | 'onOpen' | 'onClose' | 'children'> & { children: ReactElement<{ onMouseEnter?: () => void; onFocus?: () => void }> }) {
  const [active, setActive] = useState(false)
  const [open, setOpen] = useState(false)
  const timer = useRef<number | undefined>(undefined)
  useEffect(() => () => window.clearTimeout(timer.current), [])
  if (!active) {
    // Made at once, before anything else reaches the element: a click right after the mouse arrives lands on the
    // element as it will stay (made later, it would be replaced between the press and the release, and the click lost).
    const wake = () => { flushSync(() => setActive(true)); timer.current = window.setTimeout(() => setOpen(true), enterDelay) }
    return cloneElement(children, { onMouseEnter: wake, onFocus: wake })
  }
  // Gone as soon as the mouse leaves (or the focus), even before it showed: it never opens after the mouse is away.
  const leave = () => { window.clearTimeout(timer.current); setOpen(false) }
  return (
    <Tooltip {...props} enterDelay={enterDelay} open={open} onOpen={() => setOpen(true)} onClose={leave}>
      {cloneElement(children, { onMouseLeave: leave, onBlur: leave } as object)}
    </Tooltip>
  )
}

export function Money({ value, currency, sign, style, muted }: { value: number; currency: string; sign?: boolean; style?: CSSProperties; muted?: boolean }) {
  useAmountsHidden()
  const theme = useTheme()
  const color = muted ? theme.palette.text.secondary : value > 0 && sign ? theme.palette.money.positive : theme.palette.text.primary
  return (
    <span className="tnum p-money" style={{ color, ...style }}>
      {money(value, currency, { sign })}
    </span>
  )
}

/** A soft colour behind a coloured element (its pill), and under the mouse: worked out once per colour and theme. */
const softs = new Map<string, [string, string]>()
export function soft(color: string, dark: boolean, light: [number, number], darkOn: [number, number]): [string, string] {
  const key = `${color}|${dark}|${light}|${darkOn}`
  let v = softs.get(key)
  if (!v) { const [a, b] = dark ? darkOn : light; softs.set(key, (v = [alpha(color, a), alpha(color, b)])) }
  return v
}

/** The colour a category is shown in: its own, or none. */
export function useCategoryColor(): (c: Category) => string | null {
  return (c) => c.color
}

const kinds: CategoryKind[] = ['expense', 'income', 'transfer']

/**
 * The three kinds of operation the categories sit under, with the names the person gave them (Categories);
 * a nested category shows under the kind of its top category.
 */
export function useCategorySections(): [CategoryKind, string, (root: Category) => boolean][] {
  const { settings } = useApp()
  return kinds.map((kind) => [kind, settings[`section.${kind}`] ?? kind, (c: Category) => c.kind === kind])
}

/**
 * The category field of a dialog: an outlined dropdown like the other fields, grouped by kind, nested categories
 * indented under theirs, each in its colour with its icon.
 */
export function CategorySelect({ value, onChange, label = 'Category' }: { value: string; onChange: (id: string) => void; label?: string }) {
  const { tree, category } = useApp()
  const colorOf = useCategoryColor()
  const categorySections = useCategorySections()
  const chosen = colorOf(category(value))
  return (
    <TextField
      select
      label={label}
      value={value}
      onChange={(e) => onChange(e.target.value)}
      slotProps={{ select: { sx: chosen ? { color: chosen, fontWeight: 600, '& .MuiListItemIcon-root': { color: chosen } } : undefined } }}
    >
      {categorySections.flatMap(([, title, test]) => [
        <ListSubheader key={title} sx={{ lineHeight: '30px', fontSize: 11.5, fontWeight: 650, textTransform: 'uppercase', letterSpacing: '.04em' }}>{title}</ListSubheader>,
        ...tree.filter((e) => test(e.root)).map(({ c, depth }) => {
          const color = colorOf(c)
          return (
            <MenuItem key={c.id} value={c.id} sx={{ pl: 2 + depth * 2.25, ...(color ? { color, fontWeight: 600 } : {}) }}>
              <Box sx={{ display: 'flex', alignItems: 'center' }}>
                <ListItemIcon sx={{ minWidth: 32, color: color ?? undefined }}><CategoryIcon name={c.icon} fontSize="small" /></ListItemIcon>
                <ListItemText slotProps={{ primary: { sx: color ? { color, fontWeight: 600 } : undefined } }}>{c.name}</ListItemText>
              </Box>
            </MenuItem>
          )
        }),
      ])}
    </TextField>
  )
}

/** Category picker grouped by kind, nested categories indented under theirs; used in tables, drafts and dialogs. */
export function CategoryMenu({ anchor, value, onClose, onPick }: {
  anchor: HTMLElement | null
  value?: string
  onClose: () => void
  onPick: (id: string) => void
}) {
  const { tree } = useApp()
  const colorOf = useCategoryColor()
  const categorySections = useCategorySections()
  return (
    <Menu anchorEl={anchor} open={anchor !== null} onClose={onClose} slotProps={{ paper: { sx: { maxHeight: 480, minWidth: 260 } } }}>
      {categorySections.flatMap(([kind, label, test]) => [
        <ListSubheader key={'h' + kind} sx={{ lineHeight: '30px', fontSize: 11.5, fontWeight: 650, letterSpacing: '.04em', textTransform: 'uppercase', bgcolor: 'background.paper' }}>
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
  const [menuMade, setMenuMade] = useState(false)
  const c = category(id)
  const dark = theme.palette.mode === 'dark'
  const color = useCategoryColor()(c)
  const path = useCategoryPath()
  const [bg, hover] = color ? soft(color, dark, [0.13, 0.2], [0.2, 0.28]) : dark ? ['rgba(255,255,255,.06)', 'rgba(255,255,255,.1)'] : ['rgba(0,0,0,.045)', 'rgba(0,0,0,.075)']
  const shownPath = path(c)
  return (
    <>
      <span className="p-pill-box" data-path={shownPath}>
        <button
          className={`p-pill${color ? ' p-colored' : c.kind === 'transfer' ? ' p-quiet' : ''}${onChange ? ' p-active' : ''}`}
          disabled={disabled || !onChange}
          onClick={(e) => { e.stopPropagation(); setAnchor(e.currentTarget); setMenuMade(true) }}
          style={{ '--pill-bg': bg, '--pill-hover': hover, ...(color ? { color, '--pill-icon': color } : {}) } as CSSProperties}
        >
          <Mark icon={categoryIconOf(c.icon)} color={color ?? theme.palette.text.secondary} />
          <span className="p-nowrap">{c.name}</span>
        </button>
      </span>
      {/* Made the first time it opens (a table holds hundreds of these), kept after so it closes as it always did. */}
      {onChange && menuMade && <CategoryMenu anchor={anchor} value={id} onClose={() => setAnchor(null)} onPick={onChange} />}
    </>
  )
}

export function ConfirmDialog({ open, title, body, confirm, cancel = 'Cancel', danger, onClose, onConfirm }: {
  open: boolean
  title: string
  body?: ReactNode
  confirm: string
  cancel?: string
  danger?: boolean
  onClose: () => void
  onConfirm: () => void
}) {
  return (
    <Dialog open={open} onClose={onClose} maxWidth="xs" fullWidth>
      <DialogTitle>{title}</DialogTitle>
      {body && <DialogContent><Typography color="text.secondary">{body}</Typography></DialogContent>}
      <DialogActions sx={{ px: 3, pb: 2.5 }}>
        <Button onClick={onClose}>{cancel}</Button>
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
