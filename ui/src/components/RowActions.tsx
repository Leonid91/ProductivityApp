import { Box, IconButton, Tooltip } from '@mui/material'
import EditOutlined from '@mui/icons-material/EditOutlined'
import DeleteOutline from '@mui/icons-material/DeleteOutlineRounded'

/** The pencil of a line (and any other action of it): a plain icon, blue under the mouse, with no shape of its own around it. */
export const quietEdit = {
  color: 'text.secondary', borderRadius: '8px', transition: 'color 120ms',
  '&:hover': { bgcolor: 'transparent', color: 'primary.main' },
  '&.Mui-focusVisible': { outline: '2px solid', outlineColor: 'primary.main', outlineOffset: 1 },
}

/** The delete button of a line: a plain icon that turns red under the mouse, with no shape of its own around it. */
export const quietDelete = {
  color: 'text.secondary', borderRadius: '8px', transition: 'color 120ms',
  '&:hover': { bgcolor: 'transparent', color: 'error.main' },
  '&.Mui-focusVisible': { outline: '2px solid', outlineColor: 'primary.main', outlineOffset: 1 },
}

/** Width of the last column of a table with a pencil and a delete button. */
export const ROW_ACTIONS_WIDTH = 84
/** Width of the last column of a table with a pencil only. */
export const ROW_EDIT_WIDTH = 48

/**
 * The actions at the far right of a line of every table: the pencil that edits the line, then (in a table that
 * deletes) the delete button, last. Both always shown. A line that cannot be deleted keeps the delete button's place
 * empty, so every pencil of the table stays in one column. Other actions of the line come before the pencil.
 */
export function RowActions({ onEdit, editLabel = 'Edit', onDelete, deleteLabel = 'Delete', canDelete = true, deleteIcon, children }: {
  onEdit: () => void
  editLabel?: string
  /** Given in a table that deletes lines. */
  onDelete?: () => void
  deleteLabel?: string
  canDelete?: boolean
  deleteIcon?: React.ReactNode
  children?: React.ReactNode
}) {
  return (
    <Box data-row-actions sx={{ display: 'inline-flex', alignItems: 'center', justifyContent: 'flex-end', gap: 0.25, verticalAlign: 'middle' }}>
      {children}
      <Tooltip title={editLabel} enterDelay={600}>
        <IconButton size="small" disableRipple aria-label={editLabel} data-row-edit onClick={onEdit} sx={quietEdit}>
          <EditOutlined sx={{ fontSize: 18 }} />
        </IconButton>
      </Tooltip>
      {onDelete && (
        <Box sx={{ width: 34, display: 'flex', justifyContent: 'center' }}>
          {canDelete && (
            <Tooltip title={deleteLabel} enterDelay={600}>
              <IconButton size="small" disableRipple aria-label={deleteLabel} data-row-delete onClick={onDelete} sx={quietDelete}>
                {deleteIcon ?? <DeleteOutline fontSize="small" />}
              </IconButton>
            </Tooltip>
          )}
        </Box>
      )}
    </Box>
  )
}
