// Run with: node --test tests/  (Node 24 reads TypeScript as it is)
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { baseName, isInside, pathCrumbs } from '../src/paths.ts'

// Each case is a test of its own (the theories of these tests): its name says the case.
const crumbs: [string, string, { name: string; path: string }[]][] = [
  ['pathCrumbs_WindowsPath_IsCutAtItsBackslashesFromItsDrive', 'C:\\Users\\Alex\\Downloads', [
    { name: 'C:\\', path: 'C:\\' },
    { name: 'Users', path: 'C:\\Users' },
    { name: 'Alex', path: 'C:\\Users\\Alex' },
    { name: 'Downloads', path: 'C:\\Users\\Alex\\Downloads' },
  ]],
  ['pathCrumbs_WindowsDrive_IsTheDriveAlone', 'D:\\', [{ name: 'D:\\', path: 'D:\\' }]],
  ['pathCrumbs_MacOsOrLinuxPath_IsCutAtItsSlashesFromTheRoot', '/Users/alex/Downloads', [
    { name: '/', path: '/' },
    { name: 'Users', path: '/Users' },
    { name: 'alex', path: '/Users/alex' },
    { name: 'Downloads', path: '/Users/alex/Downloads' },
  ]],
  ['pathCrumbs_Root_IsTheRootAlone', '/', [{ name: '/', path: '/' }]],
  ['pathCrumbs_PathEndingWithASlashAndASpaceInAName_KeepsTheNameWhole', '/Volumes/USB KEY/', [
    { name: '/', path: '/' }, { name: 'Volumes', path: '/Volumes' }, { name: 'USB KEY', path: '/Volumes/USB KEY' },
  ]],
]
crumbs.forEach(([name, path, expected]) => test(name, () => assert.deepEqual(pathCrumbs(path), expected)))

const names: [string, string][] = [
  ['C:\\Users\\Alex\\Downloads\\March.pdf', 'March.pdf'],
  ['/Users/alex/Downloads/March.pdf', 'March.pdf'],
  ['March.pdf', 'March.pdf'],
]
names.forEach(([path, name]) => test(`baseName_PathOfAnySystem_IsItsLastPart (${path})`, () => assert.equal(baseName(path), name)))

const inside: [string, string, boolean][] = [
  ['C:\\Users\\Alex\\Desktop\\x', 'C:\\Users\\Alex\\Desktop', true],
  ['c:\\users\\alex\\desktop', 'C:\\Users\\Alex\\Desktop', true],
  ['C:\\Users', 'C:\\', true],
  ['C:\\Users\\Alex\\Desktop2', 'C:\\Users\\Alex\\Desktop', false],
  ['/Users/alex/Desktop/x', '/Users/alex/Desktop', true],
  ['/Users/alex', '/', true],
  ['/', '/', true],
  ['/Users/alex/Desktop2', '/Users/alex/Desktop', false],
  ['/Volumes/USB', '/Users/alex', false],
]
inside.forEach(([path, folder, expected]) =>
  test(`isInside_FolderItselfOrUnderItNotALongerName_IsTrueOnlyThen (${path} in ${folder})`, () => assert.equal(isInside(path, folder), expected)))
