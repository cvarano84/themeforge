import http from 'node:http'
import { URL } from 'node:url'

const json = (res, body, status = 200) => {
  res.writeHead(status, { 'content-type': 'application/json' })
  res.end(JSON.stringify(body))
}

const recentActivity = [
  { id: 'h1', movieTitle: 'The Lord of the Rings: The Fellowship of the Ring', movieYear: 2001, themeTitle: 'The Breaking of the Fellowship — Complete Motion Picture Soundtrack', downloadedAt: new Date(Date.now() - 42 * 60 * 1000).toISOString() },
  { id: 'h2', movieTitle: 'Spider-Man: Across the Spider-Verse', movieYear: 2023, themeTitle: 'Am I Dreaming', downloadedAt: new Date(Date.now() - 6 * 60 * 60 * 1000).toISOString() },
  { id: 'h3', movieTitle: 'Dune: Part Two', movieYear: 2024, themeTitle: 'A Time of Quiet Between the Storms', downloadedAt: new Date(Date.now() - 26 * 60 * 60 * 1000).toISOString() },
]

const recentlyAdded = [
  { id: 'm1', title: 'Mission: Impossible — The Final Reckoning', year: 2025, posterUrl: null },
  { id: 'm2', title: 'How to Train Your Dragon', year: 2025, posterUrl: null },
  { id: 'm3', title: 'The Phoenician Scheme', year: 2025, posterUrl: null },
]

const server = http.createServer((req, res) => {
  const path = new URL(req.url ?? '/', 'http://127.0.0.1').pathname
  if (path === '/api/auth/verify') return json(res, { ok: true })
  if (path === '/api/setup/status') return json(res, { plexConnected: true, plexAccountName: 'ChrisFlix', setupComplete: true })
  if (path === '/api/stats') return json(res, { total: 5857, downloaded: 5857, pending: 24, ignored: 12, coverage: 100, addedThisWeek: 37, recentActivity, recentlyAdded })
  if (path === '/api/version') return json(res, { current: '1.48.0', latest: '1.48.0', updateAvailable: false })
  if (path === '/api/system/health') return json(res, { checks: [{ id: 'library', status: 'warning', message: 'One library path needs attention.' }] })
  if (path === '/api/sync/status') return json(res, { inProgress: false })
  if (path === '/api/setup/plex/logout') return json(res, { success: true })
  return json(res, { error: `No mobile preview fixture for ${path}` }, 404)
})

server.listen(5000, '127.0.0.1', () => {
  // Intentionally quiet: this fixture is commonly launched as a background QA helper.
})
