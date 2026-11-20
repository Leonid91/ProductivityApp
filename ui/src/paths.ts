// Paths of every system the app runs on: Windows ("C:\Users\Alex") and macOS / Linux ("/Users/alex").

/** A Windows path: a drive letter or a network share. */
const isWindows = (path: string) => /^[A-Za-z]:/.test(path) || path.startsWith('\\\\')

/** The folders of a path from its root, each with its own path ("C:\", "Users"... or "/", "Users"...). */
export function pathCrumbs(path: string): { name: string; path: string }[] {
  if (isWindows(path)) {
    const parts = path.replace(/\\+$/, '').split('\\')
    return parts.map((name, i) => ({ name: i === 0 ? name + '\\' : name, path: parts.slice(0, i + 1).join('\\') + (i === 0 ? '\\' : '') }))
  }
  const parts = path.split('/').filter(Boolean)
  return [{ name: '/', path: '/' }, ...parts.map((name, i) => ({ name, path: '/' + parts.slice(0, i + 1).join('/') }))]
}

/** The last part of a path: a file's or a folder's name. */
export const baseName = (path: string) => path.split(/[\\/]/).filter(Boolean).pop() ?? path

/** Whether a path is a folder or inside it (Windows ignores case, macOS and Linux paths are compared as written). */
export function isInside(path: string, folder: string): boolean {
  const windows = isWindows(folder)
  const sep = windows ? '\\' : '/'
  const f = (windows ? folder.toLowerCase() : folder).replace(windows ? /\\+$/ : /\/+$/, '')
  const p = windows ? path.toLowerCase() : path
  return p === f || p.startsWith(f + sep)
}
