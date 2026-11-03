import { useMemo, useState } from 'react'
import { Box, TableCell, useTheme } from '@mui/material'
import DragIndicator from '@mui/icons-material/DragIndicatorRounded'
import type { Category } from '../api'
import { useApp } from '../AppContext'

/** Row keys in the order the person gave them (setting "order.<name>"). */
export function savedOrder(settings: Record<string, string>, name: string): string[] {
  try {
    const v = JSON.parse(settings['order.' + name] ?? '[]')
    return Array.isArray(v) ? v : []
  } catch {
    return []
  }
}

/** Items in the saved order; items never placed keep their own order, after the placed ones. */
export function ordered<T>(items: T[], saved: string[], keyOf: (t: T) => string): T[] {
  const pos = new Map(saved.map((k, i) => [k, i]))
  return items
    .map((t, i) => ({ t, rank: pos.get(keyOf(t)) ?? saved.length + i }))
    .sort((a, b) => a.rank - b.rank)
    .map((x) => x.t)
}

export interface TreeEntry { c: Category; depth: number; root: Category }

/** Categories as a tree, flattened parents first, each level in the saved order. */
export function categoryTree(categories: Category[], saved: string[]): TreeEntry[] {
  const kids = new Map<string | null, Category[]>()
  const ids = new Set(categories.map((c) => c.id))
  for (const c of ordered(categories, saved, (x) => x.id)) {
    const p = c.parentId && ids.has(c.parentId) ? c.parentId : null
    kids.set(p, [...(kids.get(p) ?? []), c])
  }
  const out: TreeEntry[] = []
  const walk = (parent: string | null, depth: number, root: Category | null) => {
    for (const c of kids.get(parent) ?? []) {
      out.push({ c, depth, root: root ?? c })
      walk(c.id, depth + 1, root ?? c)
    }
  }
  walk(null, 0, null)
  return out
}

/**
 * Rows the person can reorder by dragging their handle; the order is saved. canDrop limits where a row may go
 * (categories: only among the categories of the same parent).
 */
export function useRowOrder<T>(name: string, items: T[], keyOf: (t: T) => string, canDrop?: (from: string, to: string) => boolean) {
  const { settings, setSetting } = useApp()
  const theme = useTheme()
  const saved = useMemo(() => savedOrder(settings, name), [settings, name])
  const rows = useMemo(() => ordered(items, saved, keyOf), [items, saved, keyOf])
  const [drag, setDrag] = useState<string | null>(null)
  const [over, setOver] = useState<string | null>(null)

  // The whole saved list is kept, so rows hidden by a search or filter keep their place.
  const move = (from: string, to: string) => {
    const keys = ordered([...new Set([...saved, ...items.map(keyOf)])], saved, (k) => k)
    const b = keys.indexOf(to)
    keys.splice(keys.indexOf(from), 1)
    keys.splice(b, 0, from)
    void setSetting('order.' + name, JSON.stringify(keys))
  }

  const keys = rows.map(keyOf)
  const allowed = (key: string) => drag !== null && drag !== key && (canDrop?.(drag, key) ?? true)
  const rowProps = (t: T) => {
    const key = keyOf(t)
    const down = drag !== null && keys.indexOf(drag) < keys.indexOf(key)
    return {
      onDragOver: (e: React.DragEvent) => { if (allowed(key)) { e.preventDefault(); setOver(key) } },
      onDrop: (e: React.DragEvent) => { e.preventDefault(); if (allowed(key)) move(drag!, key); setDrag(null); setOver(null) },
      style: over === key && allowed(key) ? { boxShadow: `inset 0 ${down ? -2 : 2}px 0 ${theme.palette.primary.main}` } : undefined,
    }
  }

  const handle = (t: T) => (
    <TableCell padding="none" sx={{ width: 28 }}>
      <Box
        draggable
        aria-label="Move"
        onDragStart={(e) => {
          setDrag(keyOf(t))
          e.dataTransfer.effectAllowed = 'move'
          const row = (e.currentTarget as HTMLElement).closest('tr')
          if (row) e.dataTransfer.setDragImage(row, 16, 16)
        }}
        onDragEnd={() => { setDrag(null); setOver(null) }}
        sx={{ display: 'grid', placeItems: 'center', cursor: 'grab', color: 'text.disabled', '&:hover': { color: 'text.secondary' } }}
      >
        <DragIndicator sx={{ fontSize: 18 }} />
      </Box>
    </TableCell>
  )

  return { rows, rowProps, handle }
}
