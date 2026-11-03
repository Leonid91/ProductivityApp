import { useCallback, useEffect, useMemo, useState } from 'react'
import { Box, Chip, IconButton, InputBase, Table, TableBody, TableCell, TableHead, TableRow, Tooltip, Typography } from '@mui/material'
import Search from '@mui/icons-material/SearchRounded'
import DeleteOutline from '@mui/icons-material/DeleteOutlineRounded'
import Storefront from '@mui/icons-material/StorefrontRounded'
import { api, type BrandInfo, type BrandRule } from '../api'
import { useApp } from '../AppContext'
import { formatDate, plural } from '../format'
import { CategoryPill, EmptyState, Money, SectionCard } from '../components/common'
import { useRowOrder } from '../components/order'

/** What was learned for one brand: the statement wordings recognised as it, and its category. */
interface Learned { brand: string; wordings: string[]; categoryId: string | null }

/** The brand dictionary: every brand seen, its category, and the corrections the app has learned. */
export function BrandsPanel() {
  const { dataVersion, dataChanged, notify, fail, category, settings } = useApp()
  const [brands, setBrands] = useState<BrandInfo[]>([])
  const [rules, setRules] = useState<BrandRule[]>([])
  const [query, setQuery] = useState('')

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
    return [...by.values()]
  }, [rules])

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
                <TableCell padding="checkbox" />
              </TableRow>
            </TableHead>
            <TableBody>
              {learnedOrder.rows.map((l) => (
                <TableRow key={l.brand} hover {...learnedOrder.rowProps(l)}>
                  {learnedOrder.handle(l)}
                  <TableCell sx={{ color: 'text.secondary', fontFamily: 'ui-monospace, Consolas, monospace', fontSize: 12.5 }}>
                    {l.wordings.length ? l.wordings.map((w) => <div key={w}>{w.replace(/^#(.*)\|.*$/, '$1')}</div>) : '—'}
                  </TableCell>
                  <TableCell>{l.brand}</TableCell>
                  <TableCell>
                    {l.categoryId ? <CategoryPill id={l.categoryId} onChange={(id) => void setCategory(l.brand, id)} /> : <Typography variant="body2" color="text.secondary">—</Typography>}
                  </TableCell>
                  <TableCell padding="checkbox">
                    <Tooltip title="Forget"><IconButton size="small" aria-label="Forget" onClick={() => void forget(l)}><DeleteOutline fontSize="small" /></IconButton></Tooltip>
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </SectionCard>
      )}
    </Box>
  )
}
