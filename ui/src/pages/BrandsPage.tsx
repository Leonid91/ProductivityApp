import { useCallback, useEffect, useMemo, useState } from 'react'
import { Autocomplete, Box, Button, Chip, Dialog, DialogActions, DialogContent, DialogTitle, InputBase, Table, TableBody, TableCell, TableHead, TableRow, TextField, Typography } from '@mui/material'
import Search from '@mui/icons-material/SearchRounded'
import Storefront from '@mui/icons-material/StorefrontRounded'
import { api, type BrandInfo, type BrandRule } from '../api'
import { useApp } from '../AppContext'
import { formatDate, plural } from '../format'
import { CategoryPill, CategorySelect, EmptyState, Money, SectionCard } from '../components/common'
import { useRowOrder } from '../components/order'
import { ROW_ACTIONS_WIDTH, ROW_EDIT_WIDTH, RowActions } from '../components/RowActions'

/**
 * A brand changed from its pencil: its name (renamed on every operation, with what was learned for it) and its
 * category (for every operation of the brand, and learned for the next imports).
 */
function BrandDialog({ brand, categoryId, brands, onClose }: { brand: string | null; categoryId: string | null; brands: string[]; onClose: () => void }) {
  const { dataChanged, notify, fail } = useApp()
  const [name, setName] = useState('')
  const [chosen, setChosen] = useState<string | null>(null)
  useEffect(() => { if (brand !== null) { setName(brand); setChosen(categoryId) } }, [brand, categoryId])
  const save = async () => {
    if (brand === null) return
    try {
      const to = name.trim()
      let changed = 0
      if (to !== brand) changed += (await api.brands.rename(brand, to)).changed
      if (chosen && chosen !== categoryId) changed += (await api.brands.setCategory(to, chosen)).changed
      notify(`${to} saved` + (changed ? ` · ${plural(changed, 'operation')} updated` : ''))
      dataChanged()
      onClose()
    } catch (e) {
      fail(e)
    }
  }
  return (
    <Dialog open={brand !== null} onClose={onClose} maxWidth="xs" fullWidth data-brand-dialog>
      <DialogTitle>Edit {brand}</DialogTitle>
      <DialogContent sx={{ display: 'grid', gap: 2.25, pt: '8px !important' }}>
        {/* The same fields as an operation's: a brand of the list (or a new name), and a category dropdown. */}
        <Autocomplete
          freeSolo
          options={brands}
          inputValue={name}
          onInputChange={(_, v) => setName(v)}
          renderInput={(p) => <TextField {...p} autoFocus label="Brand Name" slotProps={{ ...p.slotProps, htmlInput: { ...p.slotProps.htmlInput, 'data-field': 'name' } }} />}
        />
        <CategorySelect value={chosen ?? 'other'} onChange={setChosen} />
      </DialogContent>
      <DialogActions sx={{ px: 3, pb: 2.5 }}>
        <Button onClick={onClose}>Cancel</Button>
        <Button variant="contained" disabled={name.trim() === ''} onClick={() => void save()}>Save</Button>
      </DialogActions>
    </Dialog>
  )
}

/** What was learned for one brand: the statement wordings recognised as it, and its category. */
interface Learned { brand: string; wordings: string[]; categoryId: string | null }

/** The brand dictionary: every brand seen, its category, and the corrections the app has learned. */
export function BrandsPanel() {
  const { dataVersion, dataChanged, notify, fail, category, settings } = useApp()
  const [brands, setBrands] = useState<BrandInfo[]>([])
  const [rules, setRules] = useState<BrandRule[]>([])
  const [query, setQuery] = useState('')
  const [editing, setEditing] = useState<{ brand: string; categoryId: string | null } | null>(null)

  useEffect(() => {
    api.brands.list().then((r) => { setBrands(r.brands); setRules(r.rules) }, fail)
  }, [dataVersion, fail])

  const shown = useMemo(() => {
    const q = query.trim().toLowerCase()
    return brands.filter((b) => !q || b.brand.toLowerCase().includes(q) || category(b.categoryId).name.toLowerCase().includes(q))
  }, [brands, query, category])

  // One line per brand: a correction is learned both for its wording and for the brand, which is still one correction.
  const learned = useMemo(() => {
    const by = new Map<string, Learned>()
    for (const r of rules) {
      const key = r.brand.toLowerCase()
      const l = by.get(key) ?? { brand: r.brand, wordings: [], categoryId: null }
      if (r.merchantKey) l.wordings.push(r.merchantKey)
      if (r.categoryId && (!r.merchantKey || !l.categoryId)) l.categoryId = r.categoryId
      by.set(key, l)
    }
    // A correction made for a whole brand has no wording of its own: the brand's wordings on the statements are shown.
    const byBrand = new Map(brands.map((b) => [b.brand.toLowerCase(), b]))
    for (const [key, l] of by) {
      if (l.wordings.length) continue
      const b = byBrand.get(key)
      l.wordings = b === undefined ? ['No operation yet'] : b.wordings.length ? b.wordings : ['Entered by hand']
    }
    return [...by.values()]
  }, [rules, brands])

  const brandKey = useCallback((b: BrandInfo) => b.brand.toLowerCase(), [])
  const learnedKey = useCallback((l: Learned) => l.brand.toLowerCase(), [])
  const brandOrder = useRowOrder('brands', shown, brandKey)
  const learnedOrder = useRowOrder('learned', learned, learnedKey)

  const setCategory = async (brand: string, id: string) => {
    try {
      const r = await api.brands.setCategory(brand, id)
      notify(`${brand} is now ${category(id).name}` + (r.changed ? ` · ${plural(r.changed, 'operation')} updated` : ''))
      dataChanged()
    } catch (e) {
      fail(e)
    }
  }

  const forget = async (l: Learned) => {
    try {
      const r = await api.brands.forget(l.brand)
      notify('Correction removed' + (r.changed ? ` · ${plural(r.changed, 'operation')} updated` : ''))
      dataChanged()
    } catch (e) {
      fail(e)
    }
  }

  const search = (
    <Box sx={{ display: 'flex', alignItems: 'center', gap: 0.75, bgcolor: 'background.default', border: 1, borderColor: 'divider', borderRadius: 999, px: 1.5, height: 38, width: 280 }}>
      <Search sx={{ fontSize: 19, color: 'text.secondary' }} />
      <InputBase placeholder="Search" value={query} onChange={(e) => setQuery(e.target.value)} sx={{ flex: 1, fontSize: 14 }} />
    </Box>
  )

  return (
    <Box sx={{ display: 'grid', gap: 3 }}>
      {brands.length === 0 ? (
        <EmptyState icon={<Storefront />} title="No brands yet" />
      ) : (
        <SectionCard title="Brands" action={search}>
          <Table size="small">
            <TableHead>
              <TableRow>
                <TableCell padding="none" />
                <TableCell>Brand Name</TableCell>
                <TableCell>Category</TableCell>
                <TableCell align="right">Operations</TableCell>
                <TableCell align="right">Spent</TableCell>
                <TableCell align="right">Last seen</TableCell>
                <TableCell sx={{ width: ROW_EDIT_WIDTH, px: 0 }} />
              </TableRow>
            </TableHead>
            <TableBody>
              {brandOrder.rows.map((b) => (
                <TableRow key={b.brand} hover sx={{ '& td': { py: 0.9 } }} {...brandOrder.rowProps(b)}>
                  {brandOrder.handle(b)}
                  <TableCell>
                    <Box sx={{ display: 'flex', alignItems: 'center', gap: 1 }}>
                      <Typography variant="body2" sx={{ fontWeight: 560 }}>{b.brand}</Typography>
                      {b.learned && <Chip size="small" label="Learned" variant="outlined" sx={{ height: 20, fontSize: 11 }} />}
                    </Box>
                  </TableCell>
                  <TableCell><CategoryPill id={b.categoryId} onChange={(id) => void setCategory(b.brand, id)} /></TableCell>
                  <TableCell align="right" className="tnum">{b.count}</TableCell>
                  <TableCell align="right"><Money value={b.spent} currency={b.currency} /></TableCell>
                  <TableCell align="right" className="tnum" sx={{ color: 'text.secondary', whiteSpace: 'nowrap' }}>{formatDate(b.lastDate, settings.dateFormat)}</TableCell>
                  <TableCell padding="none" align="right" sx={{ pr: 0.5 }}>
                    <RowActions editLabel={`Edit ${b.brand}`} onEdit={() => setEditing({ brand: b.brand, categoryId: b.categoryId })} />
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </SectionCard>
      )}

      {learned.length > 0 && (
        <SectionCard title="Learned corrections">
          <Table size="small" data-learned>
            <TableHead>
              <TableRow>
                <TableCell padding="none" />
                <TableCell>Statement wording</TableCell>
                <TableCell>Brand Name</TableCell>
                <TableCell>Category</TableCell>
                <TableCell sx={{ width: ROW_ACTIONS_WIDTH, px: 0 }} />
              </TableRow>
            </TableHead>
            <TableBody>
              {learnedOrder.rows.map((l) => (
                <TableRow key={l.brand} hover {...learnedOrder.rowProps(l)}>
                  {learnedOrder.handle(l)}
                  {/* One wording after the other, comma-separated, a line breaking only between two wordings. */}
                  <TableCell data-wordings sx={{ color: 'text.secondary', fontFamily: 'ui-monospace, Consolas, monospace', fontSize: 12.5, lineHeight: 1.6, maxWidth: '62ch' }}>
                    {l.wordings.map((w, i) => (
                      <Box component="span" key={w} sx={{ whiteSpace: 'nowrap' }}>{w.replace(/^#(.*)\|.*$/, '$1')}{i < l.wordings.length - 1 ? ',' : ''}{i < l.wordings.length - 1 ? ' ' : ''}</Box>
                    ))}
                  </TableCell>
                  <TableCell sx={{ whiteSpace: 'nowrap' }}>{l.brand}</TableCell>
                  <TableCell>
                    {l.categoryId ? <CategoryPill id={l.categoryId} onChange={(id) => void setCategory(l.brand, id)} /> : <Typography variant="body2" color="text.secondary">—</Typography>}
                  </TableCell>
                  <TableCell padding="none" align="right" sx={{ pr: 0.5 }}>
                    {/* Forgetting is this table's delete: what was learned goes, the brand's operations stay. */}
                    <RowActions editLabel={`Edit ${l.brand}`} onEdit={() => setEditing({ brand: l.brand, categoryId: l.categoryId })}
                      deleteLabel="Forget" onDelete={() => void forget(l)} />
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </SectionCard>
      )}
      <BrandDialog brand={editing?.brand ?? null} categoryId={editing?.categoryId ?? null} brands={brands.map((b) => b.brand)} onClose={() => setEditing(null)} />
    </Box>
  )
}
