import { mkdir, readFile, writeFile } from 'node:fs/promises'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { Buffer } from 'node:buffer'
import process from 'node:process'
import sharp from 'sharp'

const root = join(dirname(fileURLToPath(import.meta.url)), '..')
const publicDir = join(root, 'public')
const iconsDir = join(publicDir, 'icons')
const source = join(publicDir, 'themeforge-icon.svg')
const background = '#101828'

await mkdir(iconsDir, { recursive: true })

const render = async (size, output, { solid = false, padding = 0 } = {}) => {
  const canvas = solid
    ? sharp({ create: { width: size, height: size, channels: 4, background } })
    : sharp({ create: { width: size, height: size, channels: 4, background: { r: 0, g: 0, b: 0, alpha: 0 } } })
  const emblemSize = size - padding * 2
  const emblem = await sharp(source).resize(emblemSize, emblemSize).png().toBuffer()
  await canvas.composite([{ input: emblem, left: padding, top: padding }]).png({ compressionLevel: 9 }).toFile(output)
}

const standard = [16, 32, 48, 192, 512]
for (const size of standard) {
  await render(size, join(iconsDir, `themeforge-${size}.png`), { padding: Math.max(0, Math.round(size * 0.04)) })
}

await render(180, join(publicDir, 'apple-touch-icon.png'), { solid: true, padding: 18 })
await render(192, join(iconsDir, 'themeforge-maskable-192.png'), { solid: true, padding: 28 })
await render(512, join(iconsDir, 'themeforge-maskable-512.png'), { solid: true, padding: 76 })
await render(1024, join(iconsDir, 'themeforge-1024.png'), { padding: 40 })

// ICO supports embedded PNG images. One multi-resolution file covers current and
// older browsers without maintaining a separate conversion dependency.
const icoSizes = [16, 32, 48]
const pngs = await Promise.all(icoSizes.map(size => readFile(join(iconsDir, `themeforge-${size}.png`))))
const directoryBytes = 6 + icoSizes.length * 16
const header = Buffer.alloc(directoryBytes)
header.writeUInt16LE(0, 0)
header.writeUInt16LE(1, 2)
header.writeUInt16LE(icoSizes.length, 4)
let offset = directoryBytes
for (let index = 0; index < icoSizes.length; index += 1) {
  const entry = 6 + index * 16
  const size = icoSizes[index]
  header.writeUInt8(size, entry)
  header.writeUInt8(size, entry + 1)
  header.writeUInt8(0, entry + 2)
  header.writeUInt8(0, entry + 3)
  header.writeUInt16LE(1, entry + 4)
  header.writeUInt16LE(32, entry + 6)
  header.writeUInt32LE(pngs[index].length, entry + 8)
  header.writeUInt32LE(offset, entry + 12)
  offset += pngs[index].length
}
await writeFile(join(publicDir, 'favicon.ico'), Buffer.concat([header, ...pngs]))

process.stdout.write('Generated ThemeForge favicon, Apple touch icon, PWA, and maskable assets.\n')
