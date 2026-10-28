import { useEffect, useMemo, useState } from 'react'
import { Box, Chip, IconButton, InputBase, Table, TableBody, TableCell, TableHead, TableRow, Tooltip, Typography } from '@mui/material'
import Search from '@mui/icons-material/SearchRounded'
import DeleteOutline from '@mui/icons-material/DeleteOutlineRounded'
import Storefront from '@mui/icons-material/StorefrontRounded'
import { api, type BrandInfo, type BrandRule } from '../api'
import { useApp } from '../AppContext'
import { formatDate, plural } from '../format'
import { CategoryPill, EmptyState, Money, PageHeader, SectionCard } from '../components/common'

/** The brand dictionary: every brand seen, its category, and the corrections the app has learned. */
export function BrandsPage() {
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

  const setCategory = async (b: BrandInfo, id: string) => {
    try {
      const r = await api.brands.setCategory(b.brand, id)
      notify(`${b.brand} is now ${category(id).name}` + (r.changed ? ` · ${plural(r.changed, 'operation')} updated` : ''))
      dataChanged()
    } catch (e) {
      fail(e)
    }
  }

  const forget = async (rule: BrandRule) => {
    try {
      const r = await api.brands.deleteRule(rule.id)
      notify('Correction removed' + (r.changed ? ` · ${plural(r.changed, 'operation')} updated` : ''))
      dataChanged()
    } catch (e) {
      fail(e)
    }
  }

  return (
    <Box sx={{ maxWidth: 1180, mx: 'auto', pt: 3, pb: 6 }}>
      <PageHeader title="Brands">
        <Box sx={{ display: 'flex', alignItems: 'center', gap: 0.75, bgcolor: 'background.paper', border: 1, borderColor: 'divider', borderRadius: 999, px: 1.5, height: 38, width: 280 }}>
          <Search sx={{ fontSize: 19, color: 'text.secondary' }} />
          <InputBase placeholder="Search" value={query} onChange={(e) => setQuery(e.target.value)} sx={{ flex: 1, fontSize: 14 }} />
        </Box>
      </PageHeader>

      {brands.length === 0 ? (
        <EmptyState icon={<Storefront />} title="No brands yet" />
      ) : (
        <SectionCard>
          <Table size="small">
            <TableHead>
              <TableRow>
                <TableCell>Brand Name</TableCell>
                <TableCell>Category</TableCell>
                <TableCell align="right">Operations</TableCell>
                <TableCell align="right">Spent</TableCell>
                <TableCell align="right">Last seen</TableCell>
              </TableRow>
            </TableHead>
            <TableBody>
              {shown.map((b) => (
                <TableRow key={b.brand} hover sx={{ '& td': { py: 0.9 } }}>
                  <TableCell>
                    <Box sx={{ display: 'flex', alignItems: 'center', gap: 1 }}>
                      <Typography variant="body2" sx={{ fontWeight: 560 }}>{b.brand}</Typography>
                      {b.learned && <Chip size="small" label="Learned" variant="outlined" sx={{ height: 20, fontSize: 11 }} />}
                    </Box>
                  </TableCell>
                  <TableCell><CategoryPill id={b.categoryId} onChange={(id) => void setCategory(b, id)} /></TableCell>
                  <TableCell align="right" className="tnum">{b.count}</TableCell>
                  <TableCell align="right"><Money value={b.spent} currency={b.currency} /></TableCell>
                  <TableCell align="right" className="tnum" sx={{ color: 'text.secondary', whiteSpace: 'nowrap' }}>{formatDate(b.lastDate, settings.dateFormat)}</TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </SectionCard>
      )}

      {rules.length > 0 && (
        <SectionCard title="Learned corrections" sx={{ mt: 3 }}>
          <Table size="small">
            <TableHead>
              <TableRow>
                <TableCell>Statement wording</TableCell>
                <TableCell>Brand Name</TableCell>
                <TableCell>Category</TableCell>
                <TableCell padding="checkbox" />
              </TableRow>
            </TableHead>
            <TableBody>
              {rules.map((r) => (
                <TableRow key={r.id} hover>
                  <TableCell sx={{ color: 'text.secondary', fontFamily: 'ui-monospace, Consolas, monospace', fontSize: 12.5 }}>{r.merchantKey || 'Every operation of this brand'}</TableCell>
                  <TableCell>{r.brand}</TableCell>
                  <TableCell>{r.categoryId ? <CategoryPill id={r.categoryId} /> : <Typography variant="body2" color="text.secondary">—</Typography>}</TableCell>
                  <TableCell padding="checkbox">
                    <Tooltip title="Forget"><IconButton size="small" onClick={() => void forget(r)}><DeleteOutline fontSize="small" /></IconButton></Tooltip>
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
