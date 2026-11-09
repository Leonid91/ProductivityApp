import { useState } from 'react'
import { Autocomplete, Box, Checkbox, Chip, ListItemText, Menu, MenuItem, TextField, Tooltip, useTheme } from '@mui/material'
import { alpha } from '@mui/material/styles'
import LocalOffer from '@mui/icons-material/LocalOfferOutlined'
import { api, type Tag } from '../api'
import { useApp } from '../AppContext'

/** A tag as a small pill in its colour (or neutral). Faded when it comes from the operation's category. */
export function TagChip({ tag, inherited = false }: { tag: Tag; inherited?: boolean }) {
  const theme = useTheme()
  const dark = theme.palette.mode === 'dark'
  const color = tag.color
  return (
    <Box
      component="span"
      data-tag={tag.name}
      sx={{
        display: 'inline-flex', alignItems: 'center', maxWidth: '100%', px: 0.9, py: 0.15, borderRadius: 999, fontSize: 12, fontWeight: 600,
        whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis', lineHeight: 1.6,
        color: color ?? 'text.secondary',
        bgcolor: color ? alpha(color, dark ? 0.2 : 0.12) : dark ? 'rgba(255,255,255,.07)' : 'rgba(0,0,0,.05)',
        border: inherited ? '1px dashed' : '1px solid transparent',
        borderColor: inherited ? (color ? alpha(color, 0.55) : 'divider') : 'transparent',
      }}
    >
      {tag.name}
    </Box>
  )
}

/**
 * The Tag column of an operation: its tags as pills; clicking opens the list of tags, each ticked on or off for this
 * operation. Tags that come from its category are shown ticked and greyed (they are changed on the category).
 */
export function TagCell({ operationId, own, all, categoryName }: { operationId: number; own: number[]; all: number[]; categoryName: string }) {
  const { tags, tag, setTagData, fail } = useApp()
  const [anchor, setAnchor] = useState<HTMLElement | null>(null)
  const ownSet = new Set(own)
  const shown = all.map((id) => tag(id)).filter((t): t is Tag => !!t)
  const toggle = async (id: number) => {
    const next = ownSet.has(id) ? own.filter((x) => x !== id) : [...own, id]
    try { setTagData(await api.tags.setForOperation(operationId, next)) } catch (e) { fail(e) }
  }
  return (
    <>
      <Box
        component="button"
        data-tag-cell={operationId}
        aria-label="Tags"
        onClick={(e) => setAnchor(e.currentTarget)}
        sx={{
          display: 'flex', flexWrap: 'wrap', gap: 0.5, alignItems: 'center', minHeight: 26, width: '100%', border: 0, background: 'none', p: 0.25, mx: -0.25,
          borderRadius: 1.5, cursor: 'pointer', font: 'inherit', textAlign: 'left', '&:hover': { bgcolor: 'action.hover' },
          '&:hover .tag-add, &:focus-visible .tag-add': { opacity: 1 },
        }}
      >
        {shown.map((t) => <TagChip key={t.id} tag={t} inherited={!ownSet.has(t.id)} />)}
        {shown.length === 0 && <LocalOffer className="tag-add" sx={{ fontSize: 16, color: 'text.disabled', opacity: 0, transition: 'opacity 120ms' }} />}
      </Box>
      <Menu anchorEl={anchor} open={anchor !== null} onClose={() => setAnchor(null)} slotProps={{ paper: { sx: { minWidth: 240, maxHeight: 420 } } }}>
        {tags.length === 0 && (
          <MenuItem disabled><ListItemText>No tags yet</ListItemText></MenuItem>
        )}
        {tags.map((t) => {
          const inherited = all.includes(t.id) && !ownSet.has(t.id)
          return (
            <Tooltip key={t.id} title={inherited ? `From ${categoryName}` : ''} placement="right">
              <span>
                <MenuItem data-tag-choice={t.name} disabled={inherited} onClick={() => void toggle(t.id)} sx={{ py: 0.25, gap: 1 }}>
                  <Checkbox size="small" checked={ownSet.has(t.id) || inherited} sx={{ p: 0.5 }} disableRipple tabIndex={-1} />
                  <TagChip tag={t} />
                </MenuItem>
              </span>
            </Tooltip>
          )
        })}
      </Menu>
    </>
  )
}

/** Picks any number of tags (the tags of a category). */
export function TagSelect({ value, onChange, label = 'Tags' }: { value: number[]; onChange: (ids: number[]) => void; label?: string }) {
  const { tags } = useApp()
  const chosen = tags.filter((t) => value.includes(t.id))
  return (
    <Autocomplete
      multiple
      disableCloseOnSelect
      options={tags}
      value={chosen}
      onChange={(_, list) => onChange(list.map((t) => t.id))}
      getOptionLabel={(t) => t.name}
      isOptionEqualToValue={(a, b) => a.id === b.id}
      noOptionsText="No tags yet"
      renderOption={(props, t, { selected }) => {
        const { key, ...rest } = props as typeof props & { key: string }
        return (
          <li key={key} {...rest} data-tag-option={t.name}>
            <Checkbox size="small" checked={selected} sx={{ p: 0.5, mr: 1 }} />
            <TagChip tag={t} />
          </li>
        )
      }}
      renderValue={(list, getItemProps) => list.map((t, index) => {
        const { key, ...rest } = getItemProps({ index })
        return <Chip key={key} size="small" label={t.name} {...rest} />
      })}
      renderInput={(params) => <TextField {...params} label={label} data-field="tags" />}
    />
  )
}

/** Tag names for a list of ids, for filters and text. */
export function useTagName(): (id: number) => string {
  const { tag } = useApp()
  return (id) => tag(id)?.name ?? ''
}


/** The tags a category gives to its operations: its own and those of every category it is nested in. */
export function useCategoryTags(): (categoryId: string) => number[] {
  const { categoryTags, category } = useApp()
  return (categoryId) => {
    const out = new Set<number>()
    let c = category(categoryId)
    for (let depth = 0; c && depth < 64; depth++) {
      for (const t of categoryTags[c.id] ?? []) out.add(t)
      if (!c.parentId) break
      c = category(c.parentId)
    }
    return [...out].sort((a, b) => a - b)
  }
}

/** Tags shown as pills, without editing (operations not saved yet carry their category's tags). */
export function TagList({ ids }: { ids: number[] }) {
  const { tag } = useApp()
  const list = ids.map((id) => tag(id)).filter((t): t is Tag => !!t)
  return <Box sx={{ display: 'flex', flexWrap: 'wrap', gap: 0.5 }}>{list.map((t) => <TagChip key={t.id} tag={t} inherited />)}</Box>
}
