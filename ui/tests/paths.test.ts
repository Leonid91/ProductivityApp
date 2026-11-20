// Run with: node --test tests/  (Node 24 reads TypeScript as it is)
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { baseName, isInside, pathCrumbs } from '../src/paths.ts'

test('a Windows path is cut at its backslashes, from its drive', () => {
  assert.deepEqual(pathCrumbs('C:\\Users\\Alex\\Downloads'), [
    { name: 'C:\\', path: 'C:\\' },
    { name: 'Users', path: 'C:\\Users' },
    { name: 'Alex', path: 'C:\\Users\\Alex' },
    { name: 'Downloads', path: 'C:\\Users\\Alex\\Downloads' },
  ])
  assert.deepEqual(pathCrumbs('D:\\'), [{ name: 'D:\\', path: 'D:\\' }])
})

test('a macOS or Linux path is cut at its slashes, from the root', () => {
  assert.deepEqual(pathCrumbs('/Users/alex/Downloads'), [
    { name: '/', path: '/' },
    { name: 'Users', path: '/Users' },
    { name: 'alex', path: '/Users/alex' },
    { name: 'Downloads', path: '/Users/alex/Downloads' },
  ])
  assert.deepEqual(pathCrumbs('/'), [{ name: '/', path: '/' }])
  assert.deepEqual(pathCrumbs('/Volumes/USB KEY/'), [{ name: '/', path: '/' }, { name: 'Volumes', path: '/Volumes' }, { name: 'USB KEY', path: '/Volumes/USB KEY' }])
})

test('the name of a file is the last part of its path on every system', () => {
  assert.equal(baseName('C:\\Users\\Alex\\Downloads\\March.pdf'), 'March.pdf')
  assert.equal(baseName('/Users/alex/Downloads/March.pdf'), 'March.pdf')
  assert.equal(baseName('March.pdf'), 'March.pdf')
})

test('a folder holds itself and what is under it, not a folder of a longer name', () => {
  assert.ok(isInside('C:\\Users\\Alex\\Desktop\\x', 'C:\\Users\\Alex\\Desktop'))
  assert.ok(isInside('c:\\users\\alex\\desktop', 'C:\\Users\\Alex\\Desktop'))
  assert.ok(isInside('C:\\Users', 'C:\\'))
  assert.ok(!isInside('C:\\Users\\Alex\\Desktop2', 'C:\\Users\\Alex\\Desktop'))
  assert.ok(isInside('/Users/alex/Desktop/x', '/Users/alex/Desktop'))
  assert.ok(isInside('/Users/alex', '/'))
  assert.ok(isInside('/', '/'))
  assert.ok(!isInside('/Users/alex/Desktop2', '/Users/alex/Desktop'))
  assert.ok(!isInside('/Volumes/USB', '/Users/alex'))
})
