import { expect, test, type Page } from '@playwright/test'

const dashboard = {
  coverage: 100,
  total: 5857,
  downloaded: 5857,
  pending: 24,
  ignored: 12,
  addedThisWeek: 37,
  recentActivity: [
    { id: 'h1', movieTitle: 'The Lord of the Rings: The Fellowship of the Ring', movieYear: 2001, themeTitle: 'The Breaking of the Fellowship — Complete Motion Picture Soundtrack', downloadedAt: new Date().toISOString() },
  ],
  recentlyAdded: [
    { id: 'm1', title: 'Mission: Impossible — The Final Reckoning', year: 2025, posterUrl: null },
  ],
}

async function mockApp(page: Page) {
  await page.addInitScript(() => localStorage.setItem('themeforge_token', 'e2e-token-at-least-16-chars'))
  await page.route('**/api/**', route => {
    const path = new URL(route.request().url()).pathname
    const fixtures: Record<string, unknown> = {
      '/api/setup/status': { plexConnected: true, plexAccountName: 'ChrisFlix', setupComplete: true },
      '/api/stats': dashboard,
      '/api/version': { current: 'vNext', latest: 'vNext', updateAvailable: false },
      '/api/sync/status': { inProgress: false, finished: false },
      '/api/system/health': { status: 'warning', checks: [{ source: 'Library', type: 'warning', message: 'Review a path mapping.' }] },
      '/api/movies': [],
      '/api/shows': [],
      '/api/history': [],
      '/api/system/tasks': [],
      '/api/settings': { selectedServers: [], selectedLibraries: {}, selectedShowLibraries: {}, pathMappings: [], libraryPaths: [], advanced: { maxSearchDirs: 20000, searchDepth: 4 }, autoDownload: false, autoSync: false, lastAutoSyncAt: '' },
      '/api/settings/library-source': { source: 'disabled', url: '', configured: false },
      '/api/settings/show-source': { source: 'disabled', url: '', configured: false },
      '/api/settings/arr-instances': [],
    }
    return route.fulfill({ status: fixtures[path] ? 200 : 404, contentType: 'application/json', body: JSON.stringify(fixtures[path] ?? { detail: 'Fixture not configured' }) })
  })
}

async function expectNoPageOverflow(page: Page) {
  await expect.poll(() => page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true)
}

for (const width of [320, 375, 390, 430, 768, 1440]) {
  test(`dashboard is usable without horizontal overflow at ${width}px`, async ({ page }) => {
    await page.setViewportSize({ width, height: 900 })
    await mockApp(page)
    await page.goto('/dashboard')
    await expect(page.getByRole('heading', { name: 'Library coverage' })).toBeVisible()
    await expectNoPageOverflow(page)

    const sidebar = page.getByLabel('Application sidebar')
    const menu = page.getByRole('button', { name: 'Open navigation menu' })
    if (width < 1024) {
      await expect(sidebar).toBeHidden()
      await expect(menu).toBeVisible()
      const coverage = await page.getByRole('heading', { name: 'Library coverage' }).boundingBox()
      const stats = await page.getByRole('link', { name: /Pending: 24/ }).boundingBox()
      const downloads = await page.getByRole('heading', { name: 'Recent downloads' }).boundingBox()
      expect(coverage && stats && downloads && coverage.y < stats.y && stats.y < downloads.y).toBeTruthy()
    } else {
      await expect(sidebar).toBeVisible()
      await expect(menu).toBeHidden()
    }
  })
}

test('mobile drawer is keyboard accessible and route-aware', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 })
  await mockApp(page)
  await page.goto('/dashboard')
  const menu = page.getByRole('button', { name: 'Open navigation menu' })
  await menu.click()
  const drawer = page.getByRole('dialog', { name: 'Main navigation' })
  await expect(drawer).toBeVisible()
  await expect(drawer.getByRole('link', { name: 'Dashboard' })).toHaveAttribute('aria-current', 'page')
  await expect(drawer.getByRole('button', { name: 'Close navigation menu' })).toBeFocused()
  await page.keyboard.press('Escape')
  await expect(drawer).toBeHidden()
  await expect(menu).toBeFocused()
})

for (const route of ['/queue', '/movies', '/shows', '/history', '/settings', '/system']) {
  test(`${route} remains overflow-free at phone width`, async ({ page }) => {
    await page.setViewportSize({ width: 390, height: 844 })
    await mockApp(page)
    await page.goto(route)
    await page.locator('main').waitFor()
    await expectNoPageOverflow(page)
  })
}

test('primary actions remain usable at 200% browser zoom', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 })
  await mockApp(page)
  await page.goto('/dashboard')
  await page.evaluate(() => { document.documentElement.style.zoom = '2' })
  await expect(page.getByRole('button', { name: 'Open navigation menu' })).toBeVisible()
  await expect(page.getByRole('link', { name: /Pending: 24/ })).toBeVisible()
  const overflow = await page.evaluate(() => {
    const width = document.documentElement.clientWidth
    return [...document.querySelectorAll<HTMLElement>('body *')]
      .map(element => ({ tag: element.tagName, className: element.className, text: element.innerText?.slice(0, 50), right: element.getBoundingClientRect().right, width: element.getBoundingClientRect().width }))
      .filter(item => item.right > width + 1)
      .slice(0, 10)
  })
  expect(overflow).toEqual([])
})
