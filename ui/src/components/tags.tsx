import { useEffect, useState, type CSSProperties } from 'react'
import { Autocomplete, Box, Checkbox, Chip, ListItemText, Menu, MenuItem, TextField, Tooltip, useTheme } from '@mui/material'
import LocalOffer from '@mui/icons-material/LocalOfferOutlined'
import { api, type Tag } from '../api'
import { useApp } from '../AppContext'
import { soft } from './common'
import { Mark } from './plain'

/** A tag as a small pill in its colour (or neutral). Faded when it comes from the operation's category. */
export function TagChip({ tag, inherited = false }: { tag: Tag; inherited?: boolean }) {
  const theme = useTheme()
  const dark = theme.palette.mode === 'dark'
  const color = tag.color
  const vars: Record<string, string> = { '--tag-bg': color ? soft(color, dark, [0.12, 0.12], [0.2, 0.2])[0] : dark ? 'rgba(255,255,255,.07)' : 'rgba(0,0,0,.05)' }
  if (color) vars['--tag-color'] = color
  if (color && inherited) vars['--tag-border'] = soft(color, dark, [0.55, 0.55], [0.55, 0.55])[0]
  return (
    <span className={inherited ? 'p-tag p-inherited' : 'p-tag'} data-tag={tag.name} style={vars as CSSProperties}>
      {tag.name}
    </span>
  )
}

/**
 * The Tag column of an operation: its tags as pills; clicking opens the list of tags, each ticked on or off for this
 * operation. Tags that come from its category are shown ticked and greyed (they are changed on the category).
 */
export function TagCell({ operationId, own, all, categoryName }: { operationId: number; own: number[]; all: number[]; categoryName: string }) {
  const { tags, tag, setTagData, fail } = useApp()
  const theme = useTheme()
  const [anchor, setAnchor] = useState<HTMLElement | null>(null)
  // The tags ticked so far, until the operation comes back with them: a second tick builds on the first even when
  // the period has not been read again yet.
  const [ticked, setTicked] = useState<number[] | null>(null)
  const [menuMade, setMenuMade] = useState(false)
  const ownKey = [...own].sort().join()
  useEffect(() => {
    setTicked((t) => (t && [...t].sort().join() === ownKey ? null : t))
  }, [ownKey])
  const current = ticked ?? own
  const ownSet = new Set(current)
  const inheritedIds = all.filter((id) => !own.includes(id))
  const shown = [...new Set([...current, ...inheritedIds])].map((id) => tag(id)).filter((t): t is Tag => !!t)
  const toggle = async (id: number) => {
    const next = ownSet.has(id) ? current.filter((x) => x !== id) : [...current, id]
    setTicked(next)
    try { setTagData(await api.tags.setForOperation(operationId, next)) } catch (e) { setTicked(null); fail(e) }
  }
  return (
    <>
      <button className="p-tags" data-tag-cell={operationId} aria-label="Tags" onClick={(e) => { setAnchor(e.currentTarget); setMenuMade(true) }}>
        {shown.map((t) => <TagChip key={t.id} tag={t} inherited={!ownSet.has(t.id)} />)}
        {shown.length === 0 && <Mark icon={LocalOffer} color={theme.palette.text.disabled} className="tag-add" />}
      </button>
      {/* Made the first time it opens (a table holds hundreds of these), kept after so it closes as it always did. */}
      {menuMade && <Menu anchorEl={anchor} open={anchor !== null} onClose={() => setAnchor(null)} slotProps={{ paper: { sx: { minWidth: 240, maxHeight: 420 } } }}>
        {tags.length === 0 && (
          <MenuItem disabled><ListItemText>No tags yet</ListItemText></MenuItem>
        )}
        {tags.map((t) => {
          const inherited = inheritedIds.includes(t.id) && !ownSet.has(t.id)
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
      </Menu>}
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
