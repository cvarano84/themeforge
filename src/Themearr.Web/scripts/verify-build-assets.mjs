import { access, readFile } from 'node:fs/promises'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'
import process from 'node:process'

const root = join(dirname(fileURLToPath(import.meta.url)), '..')
const out = join(root, 'out')
const required = [
  'favicon.ico',
  'themeforge-icon.svg',
  'themeforge-logo.svg',
  'themeforge-monochrome.svg',
  'apple-touch-icon.png',
  'site.webmanifest',
  'icons/themeforge-16.png',
  'icons/themeforge-32.png',
  'icons/themeforge-48.png',
  'icons/themeforge-192.png',
  'icons/themeforge-512.png',
  'icons/themeforge-maskable-192.png',
  'icons/themeforge-maskable-512.png',
]

await Promise.all(required.map(file => access(join(out, file))))
const html = await readFile(join(out, 'index.html'), 'utf8')
const manifest = JSON.parse(await readFile(join(out, 'site.webmanifest'), 'utf8'))
if (!html.includes('<title>ThemeForge</title>') || html.includes('logo-icon.svg')) {
  throw new Error('Production HTML does not contain the final ThemeForge metadata.')
}
if (manifest.name !== 'ThemeForge' || manifest.short_name !== 'ThemeForge') {
  throw new Error('Production manifest does not contain the ThemeForge app identity.')
}

process.stdout.write('Verified ThemeForge assets in the production bundle.\n')
