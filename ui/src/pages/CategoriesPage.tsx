import { useCallback, useMemo, useState } from 'react'
import {
  Box, Button, Dialog, DialogActions, DialogContent, DialogTitle, IconButton, InputBase, MenuItem, Table, TableBody, TableCell, TableRow, TextField,
  Tooltip, Typography, type Theme,
} from '@mui/material'
import Add from '@mui/icons-material/AddRounded'
import Block from '@mui/icons-material/BlockRounded'
import { api, type Category, type CategoryKind } from '../api'
import { useApp } from '../AppContext'
import { CategoryIcon } from '../components/CategoryIcon'
import { ConfirmDialog, SectionCard, useCategoryColor, useCategorySections } from '../components/common'
import { useRowOrder, type TreeEntry } from '../components/order'
import { TagSelect } from '../components/tags'
import { RowActions } from '../components/RowActions'

export const palette = ['#e53935', '#d81b60', '#8e24aa', '#5e35b1', '#3949ab', '#1e88e5', '#0288d1', '#00897b', '#43a047', '#689f38', '#afb42b', '#f9a825', '#fb8c00', '#f4511e', '#6d4c41', '#757575']

type Editing = { id: string | null; name: string; parentId: string | null; kind: CategoryKind; color: string | null }

/**
 * Create or edit a category: its name, the category it is in, its colour, and its tags (every operation of the
 * category, and of the categories nested in it, carries them).
 */
function CategoryDialog({ editing, onClose }: { editing: Editing; onClose: () => void }) {
  const { tree, setCategories, categoryTags, setTagData, notify, fail } = useApp()
  const [v, setV] = useState<Editing>(editing)
  const [tagIds, setTagIds] = useState<number[]>(editing.id ? categoryTags[editing.id] ?? [] : [])

  // A category goes in one of the same kind, never in itself or one of its own subcategories.
  const inside = (e: TreeEntry) => {
    for (let c: Category | undefined = e.c; c; c = tree.find((x) => x.c.id === c!.parentId)?.c) if (c.id === v.id) return true
    return false
  }
  const parents = tree.filter((e) => e.c.kind === v.kind && !inside(e))
  const save = async () => {
    try {
      const name = v.name.trim()
      const before = new Set((v.id && categoryTags[v.id]) || [])
      const list = v.id === null
        ? await api.categories.create({ name, parentId: v.parentId, kind: v.kind, color: v.color })
        : await api.categories.update({ id: v.id, name, parentId: v.parentId, color: v.color })
      setCategories(list)
      // A new category is the last one of its name made; its tags are set once it exists.
      const id = v.id ?? [...list].reverse().find((c) => c.name === name && !c.builtIn)?.id
      if (id && (tagIds.length !== before.size || tagIds.some((t) => !before.has(t)))) setTagData(await api.tags.setForCategory(id, tagIds))
      notify(v.id === null ? `${name} created` : `${name} saved`)
      onClose()
    } catch (e) {
      fail(e)
    }
  }

  return (
    <Dialog open onClose={onClose} maxWidth="xs" fullWidth data-category-dialog>
      <DialogTitle>{v.id === null ? 'New category' : 'Edit category'}</DialogTitle>
      <DialogContent sx={{ display: 'grid', gap: 2.5, pt: '8px !important' }}>
        <TextField label="Name" value={v.name} autoFocus onChange={(e) => setV({ ...v, name: e.target.value })} slotProps={{ htmlInput: { maxLength: 60 } }} />
        <TextField select label="Inside" value={v.parentId ?? ''} onChange={(e) => setV({ ...v, parentId: e.target.value || null })}>
          <MenuItem value="">Top level</MenuItem>
          {parents.map(({ c, depth }) => (
            <MenuItem key={c.id} value={c.id} sx={{ pl: 2 + depth * 2.25, gap: 1.25 }}>
              <CategoryIcon name={c.icon} sx={{ fontSize: 18, color: 'text.secondary' }} />
              {c.name}
            </MenuItem>
          ))}
        </TextField>
        <TagSelect value={tagIds} onChange={setTagIds} />
        <Box>
          <Typography variant="body2" color="text.secondary" sx={{ mb: 1 }}>Colour</Typography>
          <Box sx={{ display: 'flex', flexWrap: 'wrap', gap: 1 }}>
            <Tooltip title="None">
              <Box component="button" aria-label="No colour" onClick={() => setV({ ...v, color: null })} sx={swatch(v.color === null, 'transparent')}>
                <Block sx={{ fontSize: 18, color: 'text.secondary' }} />
              </Box>
            </Tooltip>
            {palette.map((c) => (
              <Box key={c} component="button" aria-label={c} data-color={c} onClick={() => setV({ ...v, color: c })} sx={swatch(v.color === c, c)} />
            ))}
          </Box>
        </Box>
      </DialogContent>
      <DialogActions sx={{ px: 3, pb: 2.5 }}>
        <Button onClick={onClose}>Cancel</Button>
        <Button variant="contained" disabled={v.name.trim() === ''} onClick={() => void save()}>{v.id === null ? 'Create' : 'Save'}</Button>
      </DialogActions>
    </Dialog>
  )
}

export const swatch = (selected: boolean, color: string) => (t: Theme) => ({
  width: 30, height: 30, borderRadius: '50%', border: 0, p: 0, cursor: 'pointer', display: 'grid', placeItems: 'center', bgcolor: color,
  boxShadow: selected ? `0 0 0 2px ${t.palette.background.paper}, 0 0 0 4px ${t.palette.primary.main}` : `inset 0 0 0 1px ${t.palette.divider}`,
})

/** A kind of operation (Expenses, Income, Transfers): a heading over its categories, renamed in place. */
function KindHeading({ kind, label }: { kind: string; label: string }) {
  const { setSetting } = useApp()
  return (
    <InputBase
      key={label}
      defaultValue={label}
      inputProps={{ 'aria-label': 'Label name', maxLength: 40, size: Math.max(label.length, 4) }}
      onKeyDown={(e) => { if (e.key === 'Enter' || e.key === 'Escape') (e.target as HTMLInputElement).blur() }}
      onBlur={(e) => {
        const v = e.target.value.trim()
        if (!v || v === label) { e.target.value = label; return }
        void setSetting(`section.${kind}`, v)
      }}
      sx={{
        fontSize: 11.5, fontWeight: 650, letterSpacing: '.04em', textTransform: 'uppercase', color: 'text.secondary', borderRadius: 1.5,
        px: 0.75, mx: -0.75, '&:hover, &.Mui-focused': { bgcolor: 'action.hover' }, '& input': { p: '2px 0', cursor: 'text' },
      }}
    />
  )
}

/** Every category, nested at any depth, under its kind of operation: create, rename, colour, move, reorder and delete. */
export function CategoriesPanel() {
  const { tree, setCategories, notify, fail } = useApp()
  const colorOf = useCategoryColor()
  const categorySections = useCategorySections()
  const [editing, setEditing] = useState<Editing | null>(null)
  const [confirm, setConfirm] = useState<Category | null>(null)

  const keyOf = useCallback((e: TreeEntry) => e.c.id, [])
  const parentOf = useMemo(() => new Map(tree.map((e) => [e.c.id, e.c.parentId])), [tree])
  // Rows are moved among the categories of the same parent; "Inside" moves one to another parent.
  const order = useRowOrder('categories', tree, keyOf, (a, b) => parentOf.get(a) === parentOf.get(b))

  const remove = async (c: Category) => {
    try {
      setCategories(await api.categories.remove(c.id))
      notify(`${c.name} deleted`)
    } catch (e) {
      fail(e)
    }
  }

  return (
    <>
      <SectionCard>
        <Table size="small" data-categories>
          <TableBody>
            {categorySections.flatMap(([kind, label, test]) => [
              <TableRow key={'h' + kind}>
                <TableCell colSpan={2} data-section-label={kind} sx={{ pt: 2.5 }}>
                  <KindHeading kind={kind} label={label} />
                </TableCell>
                <TableCell padding="none" align="right" sx={{ pt: 2.5, pr: 1 }}>
                  <Tooltip title={`New category in ${label}`}>
                    <IconButton size="small" aria-label={`New category in ${label}`} data-add-kind={kind} onClick={() => setEditing({ id: null, name: '', parentId: null, kind, color: null })}>
                      <Add fontSize="small" />
                    </IconButton>
                  </Tooltip>
                </TableCell>
              </TableRow>,
              ...order.rows.filter((e) => test(e.root)).map((e) => {
                const { c, depth } = e
                const color = colorOf(c)
                return (
                  <TableRow key={c.id} hover data-category={c.name} {...order.rowProps(e)} sx={{ '&:hover .row-action': { opacity: 1 } }}>
                    {order.handle(e)}
                    <TableCell>
                      <Box sx={{ display: 'flex', alignItems: 'center', gap: 1.25, pl: depth * 3 }}>
                        <Box sx={{ width: 30, height: 30, borderRadius: '10px', display: 'grid', placeItems: 'center', flex: '0 0 30px', bgcolor: color ? `${color}22` : 'action.hover' }}>
                          <CategoryIcon name={c.icon} sx={{ fontSize: 17, color: color ?? 'text.secondary' }} />
                        </Box>
                        <Typography variant="body2" sx={{ fontWeight: depth === 0 ? 600 : 520, color: color ?? 'text.primary' }}>{c.name}</Typography>
                      </Box>
                    </TableCell>
                    <TableCell padding="none" align="right" sx={{ whiteSpace: 'nowrap', pr: 0.5 }}>
                      {/* Built-in categories cannot be deleted: their delete button's place stays empty. */}
                      <RowActions
                        onEdit={() => setEditing({ id: c.id, name: c.name, parentId: c.parentId, kind: c.kind, color: c.color })}
                        onDelete={() => setConfirm(c)}
                        canDelete={!c.builtIn}
                      >
                        <Tooltip title="Add a subcategory">
                          <IconButton className="row-action" size="small" aria-label="Add a subcategory" onClick={() => setEditing({ id: null, name: '', parentId: c.id, kind: c.kind, color: null })} sx={{ opacity: 0 }}>
                            <Add fontSize="small" />
                          </IconButton>
                        </Tooltip>
                      </RowActions>
                    </TableCell>
                  </TableRow>
                )
              }),
            ])}
          </TableBody>
        </Table>
      </SectionCard>
      {editing && <CategoryDialog key={`${editing.id}:${editing.parentId}`} editing={editing} onClose={() => setEditing(null)} />}
      <ConfirmDialog
        open={confirm !== null}
        title={`Delete ${confirm?.name ?? ''}?`}
        confirm="Delete"
        danger
        onClose={() => setConfirm(null)}
        onConfirm={() => { if (confirm) void remove(confirm) }}
      />
    </>
  )
}
