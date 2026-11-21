import { useEffect, useState } from 'react'
import { Box, Table, TableBody, TableCell, TableHead, TableRow, Typography } from '@mui/material'
import Check from '@mui/icons-material/CheckRounded'
import { api, type SupportedCountry } from '../api'
import { useApp } from '../AppContext'
import { SectionCard } from '../components/common'
import { Flag } from '../components/flags'

/** Settings > Information: the banks whose statements are read by themselves, a table per country (read only). */
export function InformationPanel() {
  const { fail } = useApp()
  const [countries, setCountries] = useState<SupportedCountry[] | null>(null)
  useEffect(() => { api.banks.supported().then(setCountries, fail) }, [fail])
  if (!countries) return null
  const mark = (on: boolean, label: string) => on
    ? <Check sx={{ fontSize: 20, color: 'success.main', display: 'block', mx: 'auto' }} aria-label={label} />
    : <Typography component="span" sx={{ color: 'text.disabled', display: 'block', textAlign: 'center' }} aria-label={`Not ${label.toLowerCase()}`}>—</Typography>
  return (
    <Box sx={{ display: 'grid', gap: 3 }} data-supported-banks>
      {countries.map((c) => (
        <SectionCard
          key={c.code}
          title={<Box component="span" sx={{ display: 'inline-flex', alignItems: 'center', gap: 1.25 }}><Flag code={c.code} />{c.name}</Box>}
        >
          <Table size="small" data-country={c.code}>
            <TableHead>
              <TableRow>
                <TableCell>Name</TableCell>
                <TableCell align="center" sx={{ width: 160 }}>Considered</TableCell>
                <TableCell align="center" sx={{ width: 160 }}>Confirmed</TableCell>
              </TableRow>
            </TableHead>
            <TableBody>
              {c.banks.map((b) => (
                <TableRow key={b.name} data-supported-bank={b.name}>
                  <TableCell>{b.name}</TableCell>
                  <TableCell align="center" data-considered={b.considered}>{mark(b.considered, 'Considered')}</TableCell>
                  <TableCell align="center" data-confirmed={b.confirmed}>{mark(b.confirmed, 'Confirmed')}</TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </SectionCard>
      ))}
    </Box>
  )
}
