import { Typography } from '@mui/material'
import ErrorOutline from '@mui/icons-material/ErrorOutlineRounded'

/**
 * What the tables of downloaded sources share (Definitions > Exchange rates and Holidays): one line per source,
 * chosen with its round button or check box (its line tinted), its address changed in place, a plain delete icon.
 */

/** Read by screen readers only. */
export const visuallyHidden = { border: 0, clip: 'rect(0 0 0 0)', height: '1px', m: '-1px', overflow: 'hidden', p: 0, position: 'absolute', whiteSpace: 'nowrap', width: '1px' } as const

/** A text changed in place: it looks like text until the mouse is over it or it is being typed in. */
export const inlineInput = { flex: 1, fontWeight: 520, px: 1, mx: -1, borderRadius: 2, fontSize: 14, '&:hover, &.Mui-focused': { bgcolor: 'action.hover' } }

/**
 * The first line of every cell of a line of sources: the same height, its content centred on it, so the round button
 * or check box, the texts, the address and the delete icon share one middle (what shows under an address only adds
 * lines below).
 */
export const SOURCE_LINE = 32
export const sourceLine = { height: SOURCE_LINE, display: 'flex', alignItems: 'center' } as const
/** The padding of every cell of a line, the button cells' too (they lose it otherwise). */
export const sourceRow = { verticalAlign: 'top', transition: 'background-color 160ms', '& td, & td.MuiTableCell-paddingCheckbox': { py: 1.25 } } as const

/** The delete button of a line (shared with every table). */
export { quietDelete } from './RowActions'

/** Why a source gives nothing, in red under its address (or its choice in Settings). */
export function SourceError({ message, ...data }: { message: string } & Record<`data-${string}`, string | boolean>) {
  return (
    <Typography variant="body2" role="alert" {...data} sx={{ color: 'error.main', display: 'flex', alignItems: 'flex-start', gap: 0.75, maxWidth: 560 }}>
      <ErrorOutline sx={{ fontSize: 18, mt: '1px', flexShrink: 0 }} />
      {message}
    </Typography>
  )
}
