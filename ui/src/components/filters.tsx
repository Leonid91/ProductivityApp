import type { ReactNode } from 'react'
import { Box, Checkbox, ListItemIcon, ListSubheader, MenuItem, Typography } from '@mui/material'
import Check from '@mui/icons-material/CheckRounded'
import { MenuDivider } from './common'

/** One value of a column (a category, a brand, a day) and how many rows have it. */
export interface Choice { key: string; label: string; count: number; icon?: ReactNode }

/** The values hidden by a filter that are present in the table: the filter is on when there is at least one. */
export const hiding = (hidden: Set<string>, choices: Choice[]) => choices.some((c) => hidden.has(c.key))

/**
 * The items of a column filter, to put in its menu: every value of the column with a tick when its rows are shown,
 * and All / None above them. Any number can be ticked, from none to all.
 */
export function choiceItems({ title, choices, hidden, onChange, all }: {
  /** A heading above the items (the date menu has other items above it). */
  title?: string
  choices: Choice[]
  hidden: Set<string>
  onChange: (hidden: Set<string>) => void
  /** "All categories", "All brands"... */
  all: string
}): ReactNode[] {
  const none = choices.length > 0 && choices.every((c) => hidden.has(c.key))
  const everything = !hiding(hidden, choices)
  const toggle = (key: string) => {
    const next = new Set(hidden)
    if (next.has(key)) next.delete(key)
    else next.add(key)
    onChange(next)
  }
  const tick = (on: boolean) => (on ? <Check fontSize="small" sx={{ color: 'primary.main', ml: 2 }} /> : null)
  return [
    ...(title ? [<ListSubheader key="title" sx={{ lineHeight: '30px', fontSize: 11.5, fontWeight: 650, letterSpacing: '.04em', textTransform: 'uppercase', bgcolor: 'background.paper' }}>{title}</ListSubheader>] : []),
    <MenuItem key="all" data-choice="all" onClick={() => onChange(new Set())}>
      <Typography sx={{ flex: 1 }}>{all}</Typography>
      {tick(everything)}
    </MenuItem>,
    <MenuItem key="none" data-choice="none" onClick={() => onChange(new Set(choices.map((c) => c.key)))}>
      <Typography sx={{ flex: 1 }}>None</Typography>
      {tick(none)}
    </MenuItem>,
    <MenuDivider key="divider" />,
    ...choices.map((c) => (
      <MenuItem key={c.key} data-choice={c.key} onClick={() => toggle(c.key)} sx={{ py: 0.25, gap: 1 }}>
        <Checkbox size="small" checked={!hidden.has(c.key)} sx={{ p: 0.5 }} disableRipple tabIndex={-1} />
        {c.icon && <ListItemIcon sx={{ minWidth: 0 }}>{c.icon}</ListItemIcon>}
        <Box component="span" sx={{ flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{c.label}</Box>
        <Typography variant="body2" color="text.secondary" className="tnum" sx={{ ml: 2, minWidth: 22, textAlign: 'right' }}>{c.count}</Typography>
      </MenuItem>
    )),
  ]
}

/** The values of a column with their counts, in the given order. */
export function countChoices<T>(rows: T[], keyOf: (r: T) => string, label: (key: string) => string, icon?: (key: string) => ReactNode): Choice[] {
  const counts = new Map<string, number>()
  rows.forEach((r) => counts.set(keyOf(r), (counts.get(keyOf(r)) ?? 0) + 1))
  return [...counts.entries()].map(([key, count]) => ({ key, count, label: label(key), icon: icon?.(key) }))
}
