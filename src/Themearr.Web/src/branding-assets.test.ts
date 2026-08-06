import { readFileSync, readdirSync } from 'node:fs'
import { resolve } from 'node:path'
import { describe, expect, it } from 'vitest'
import sharp from 'sharp'

const root = process.cwd()
const publicDir = resolve(root, 'public')

function pngDimensions(file: string): [number, number] {
  const png = readFileSync(resolve(publicDir, file))
  expect(png.subarray(1, 4).toString()).toBe('PNG')
  return [png.readUInt32BE(16), png.readUInt32BE(20)]
}

function filesUnder(path: string): string[] {
  return readdirSync(path, { withFileTypes: true }).flatMap(entry => {
    const full = resolve(path, entry.name)
    return entry.isDirectory() ? filesUnder(full) : [full]
  })
}

describe('ThemeForge metadata and application assets', () => {
  it('uses ThemeForge in browser, Open Graph, Apple, manifest, and favicon metadata', () => {
    const html = readFileSync(resolve(root, 'index.html'), 'utf8')
    expect(html).toContain('<title>ThemeForge</title>')
    expect(html).toContain('name="apple-mobile-web-app-title" content="ThemeForge"')
    expect(html).toContain('property="og:title" content="ThemeForge"')
    expect(html).toContain('%BASE_URL%favicon.ico')
    expect(html).toContain('%BASE_URL%apple-touch-icon.png')
    expect(html).toContain('%BASE_URL%site.webmanifest')
    expect(html).not.toMatch(/logo-icon|>Themearr</)
  })

  it('ships a valid standalone manifest with any and maskable icons', () => {
    const manifest = JSON.parse(readFileSync(resolve(publicDir, 'site.webmanifest'), 'utf8'))
    expect(manifest).toMatchObject({
      name: 'ThemeForge', short_name: 'ThemeForge', display: 'standalone',
      start_url: './', scope: './',
    })
    expect(manifest.icons.filter((icon: { purpose: string }) => icon.purpose === 'any')).toHaveLength(2)
    expect(manifest.icons.filter((icon: { purpose: string }) => icon.purpose === 'maskable')).toHaveLength(2)
  })

  it.each([
    ['icons/themeforge-16.png', 16],
    ['icons/themeforge-32.png', 32],
    ['icons/themeforge-48.png', 48],
    ['apple-touch-icon.png', 180],
    ['icons/themeforge-192.png', 192],
    ['icons/themeforge-maskable-192.png', 192],
    ['icons/themeforge-512.png', 512],
    ['icons/themeforge-maskable-512.png', 512],
    ['icons/themeforge-1024.png', 1024],
  ])('%s is present at %ix%i', (file, size) => {
    expect(pngDimensions(file)).toEqual([size, size])
  })

  it('contains 16, 32, and 48 pixel images in favicon.ico', () => {
    const ico = readFileSync(resolve(publicDir, 'favicon.ico'))
    expect(ico.readUInt16LE(2)).toBe(1)
    expect(ico.readUInt16LE(4)).toBe(3)
    expect([ico[6], ico[22], ico[38]]).toEqual([16, 32, 48])
  })

  it('uses an opaque solid canvas for the iPhone home-screen icon', async () => {
    const { data, info } = await sharp(resolve(publicDir, 'apple-touch-icon.png'))
      .ensureAlpha()
      .raw()
      .toBuffer({ resolveWithObject: true })
    for (let index = info.channels - 1; index < data.length; index += info.channels) {
      expect(data[index]).toBe(255)
    }
  })

  it('keeps old user-facing branding out of active frontend screens', () => {
    const activeFiles = [resolve(root, 'index.html'), ...filesUnder(resolve(root, 'src', 'app')), ...filesUnder(resolve(root, 'src', 'components'))]
      .filter(file => /\.(?:html|tsx?)$/.test(file))
    const text = activeFiles.map(file => readFileSync(file, 'utf8'))
      .join('\n')
      .replaceAll('/opt/themearr', '/opt/legacy-install')
      .replaceAll('Themearr.API', 'Legacy.API')
    expect(text).not.toMatch(/Themearr/i)
  })
})
