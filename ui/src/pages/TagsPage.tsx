import { useState } from 'react'
import {
  Box, Button, Dialog, DialogActions, DialogContent, DialogTitle, Table, TableBody, TableCell, TableHead, TableRow, TextField, Tooltip, Typography,
} from '@mui/material'
import Block from '@mui/icons-material/BlockRounded'
import LocalOffer from '@mui/icons-material/LocalOfferOutlined'
import { api, type Tag } from '../api'
import { useApp } from '../AppContext'
import { ConfirmDialog, EmptyState, SectionCard } from '../components/common'
import { TagChip } from '../components/tags'
import { palette, swatch } from './CategoriesPage'
import { RowActions } from '../components/RowActions'

type Editing = { id: number | null; name: string; color: string | null }

/** A new tag, or one renamed or coloured. */
export function TagDialog({ editing, onClose }: { editing: Editing; onClose: () => void }) {
  const { setTagData, notify, fail } = useApp()
  const [v, setV] = useState<Editing>(editing)
  const save = async () => {
    const name = v.name.trim()
    try {
      setTagData(v.id === null ? await api.tags.create({ name, color: v.color }) : await api.tags.update({ id: v.id, name, color: v.color }))
      notify(v.id === null ? `${name} created` : `${name} saved`)
      onClose()
    } catch (e) {
      fail(e)
    }
  }
  return (
    <Dialog open onClose={onClose} maxWidth="xs" fullWidth data-tag-dialog>
      <DialogTitle>{v.id === null ? 'New tag' : 'Edit tag'}</DialogTitle>
      <DialogContent sx={{ display: 'grid', gap: 2.5, pt: '8px !important' }}>
        <TextField
          label="Name"
          value={v.name}
          autoFocus
          onChange={(e) => setV({ ...v, name: e.target.value })}
          onKeyDown={(e) => { if (e.key === 'Enter' && v.name.trim()) void save() }}
          slotProps={{ htmlInput: { maxLength: 40, 'data-field': 'name' } }}
        />
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

/** Every tag: the categories that give it to their operations, how many operations carry it themselves; edit, delete. */
export function TagsPanel({ adding, onAdded }: { adding: boolean; onAdded: () => void }) {
  const { tags, categoryTags, category, setTagData, notify, fail } = useApp()
  const [editing, setEditing] = useState<Editing | null>(null)
  const [confirm, setConfirm] = useState<Tag | null>(null)
  const categoriesOf = (id: number) => Object.entries(categoryTags).filter(([, ids]) => ids.includes(id)).map(([c]) => category(c).name).sort((a, b) => a.localeCompare(b))
  const remove = async (t: Tag) => {
    try {
      setTagData(await api.tags.remove(t.id))
      notify(`${t.name} deleted`)
    } catch (e) {
      fail(e)
    }
  }
  return (
    <>
      <SectionCard>
        {tags.length === 0 ? (
          <EmptyState icon={<LocalOffer />} title="No tags yet" />
        ) : (
          <Table size="small" data-tags sx={{ '& td, & th': { px: 1.5 } }}>
            <TableHead>
              <TableRow>
                <TableCell>Tag</TableCell>
                <TableCell>Categories</TableCell>
                <TableCell align="right">Operations</TableCell>
                <TableCell sx={{ width: 90 }} />
              </TableRow>
            </TableHead>
            <TableBody>
              {tags.map((t) => (
                <TableRow key={t.id} hover data-tag-row={t.name}>
                  <TableCell><TagChip tag={t} /></TableCell>
                  <TableCell sx={{ color: 'text.secondary' }}>{categoriesOf(t.id).join(', ')}</TableCell>
                  <TableCell align="right" className="tnum">{t.operations}</TableCell>
                  <TableCell padding="none" align="right" sx={{ whiteSpace: 'nowrap', pr: 0.5 }}>
                    <RowActions onEdit={() => setEditing({ id: t.id, name: t.name, color: t.color })} onDelete={() => setConfirm(t)} />
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        )}
      </SectionCard>
      {(editing || adding) && (
        <TagDialog
          key={editing?.id ?? 'new'}
          editing={editing ?? { id: null, name: '', color: null }}
          onClose={() => { setEditing(null); onAdded() }}
        />
      )}
      <ConfirmDialog
        open={confirm !== null}
        title={`Delete ${confirm?.name ?? ''}?`}
        body="It is taken off every operation and category that carries it."
        confirm="Delete"
        danger
        onClose={() => setConfirm(null)}
        onConfirm={() => { if (confirm) void remove(confirm) }}
      />
    </>
  )
}
