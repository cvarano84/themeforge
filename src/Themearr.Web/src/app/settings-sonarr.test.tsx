import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { AuthProvider } from '@/lib/auth'

vi.mock('@/lib/api', async () => (await import('@/test/apiMock')).makeApiMock())
const api = await import('@/lib/api')
const SettingsPage = (await import('@/app/settings/page')).default

beforeEach(() => {
  vi.clearAllMocks()
  vi.mocked(api.setupApi.status).mockResolvedValue({ plexConnected: false, setupComplete: true } as never)
  vi.mocked(api.settingsApi.get).mockResolvedValue({
    selectedServers: [], selectedLibraries: {}, selectedShowLibraries: {},
    movieLibrarySource: 'disabled', showLibrarySource: 'sonarr',
    pathMappings: [], libraryPaths: [], advanced: { maxSearchDirs: 20000, searchDepth: 4 },
    autoDownload: false, autoSync: false, lastAutoSyncAt: '',
  })
  vi.mocked(api.radarrApi.get).mockResolvedValue({ source: 'disabled', url: '', configured: false })
  vi.mocked(api.sonarrApi.get).mockResolvedValue({ source: 'sonarr', url: 'http://sonarr:8989', configured: true })
  vi.mocked(api.versionApi.get).mockResolvedValue({ current: 'v1', latest: 'v1', updateAvailable: false } as never)
  vi.mocked(api.apiKeyApi.get).mockResolvedValue({ key: 'external-key' })
  vi.mocked(api.systemApi.health).mockResolvedValue({ status: 'ok', checks: [] })
  vi.mocked(api.systemApi.tasks).mockResolvedValue([])
  vi.mocked(api.syncApi.status).mockResolvedValue({ inProgress: false, finished: false } as never)
})

function renderPage() {
  return render(<MemoryRouter><AuthProvider><SettingsPage /></AuthProvider></MemoryRouter>)
}

describe('Sonarr settings', () => {
  it('persists the source and URL while leaving the stored key blank', async () => {
    renderPage()

    const url = await screen.findByDisplayValue('http://sonarr:8989')
    expect(url).toBeTruthy()
    const key = screen.getByLabelText('API key', { selector: 'input[type="password"]' }) as HTMLInputElement
    expect(key.value).toBe('')
    expect(screen.getByText(/stored key is write-only/i)).toBeTruthy()
  })

  it('surfaces a failed Sonarr connection test', async () => {
    const user = userEvent.setup()
    vi.mocked(api.sonarrApi.test).mockRejectedValue(new Error('Sonarr test failed'))
    renderPage()

    await user.click(await screen.findByRole('button', { name: /Test connection/i }))

    await waitFor(() => expect(screen.getByText('Sonarr test failed')).toBeTruthy())
  })
})
