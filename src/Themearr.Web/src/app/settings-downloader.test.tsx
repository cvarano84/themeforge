import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { AuthProvider } from '@/lib/auth'

vi.mock('@/lib/api', async () => (await import('@/test/apiMock')).makeApiMock())
const api = await import('@/lib/api')

const diagnostics = {
  ready: true, degraded: false, status: 'healthy', summary: 'Local theme downloader ready.',
  ytDlp: { available: true, status: 'available', version: '2026.07.04', detail: null },
  ffmpeg: { available: true, status: 'available', version: '7.1', detail: null },
  ffprobe: { available: true, status: 'available', version: '7.1', detail: null },
  javaScriptRuntime: { available: true, status: 'available', version: '2.9.4', detail: null },
  cookies: { configured: false, source: 'none', managedByEnvironment: false, canUpload: true,
    canDelete: false, valid: false, recordCount: 0, youtubeRecordCount: 0, uploadedAtUtc: null, detail: null },
  poTokenProvider: { mode: 'auto', status: 'ready', pluginDetected: true,
    providerReachable: true, version: '1.3.1', detail: null },
  audioQuality: '192K', timeoutSeconds: 300, concurrentDownloads: 1,
  audioQualityManagedByEnvironment: false, timeoutManagedByEnvironment: false,
  concurrencyManagedByEnvironment: false,
} as const

function renderPage(ui: React.ReactElement) {
  return render(<MemoryRouter><AuthProvider>{ui}</AuthProvider></MemoryRouter>)
}

beforeEach(() => {
  vi.clearAllMocks()
  vi.mocked(api.setupApi.status).mockResolvedValue({ plexConnected: true, setupComplete: true } as never)
  vi.mocked(api.systemApi.health).mockResolvedValue({ status: 'ok', checks: [] } as never)
  vi.mocked(api.syncApi.status).mockResolvedValue({ inProgress: false, finished: false } as never)
  vi.mocked(api.settingsApi.get).mockResolvedValue({
    selectedServers: [], selectedLibraries: {}, pathMappings: [], libraryPaths: [],
    advanced: { maxSearchDirs: 20000, searchDepth: 4 }, autoDownload: false, autoSync: false, lastAutoSyncAt: '',
  } as never)
  vi.mocked(api.versionApi.get).mockResolvedValue({ current: 'v1.47.0', latest: 'v1.47.0', updateAvailable: false } as never)
  vi.mocked(api.radarrApi.get).mockResolvedValue({ source: 'plex', url: '', configured: false } as never)
  vi.mocked(api.apiKeyApi.get).mockResolvedValue({ key: 'k'.repeat(64) } as never)
  vi.mocked(api.downloaderApi.get).mockResolvedValue(diagnostics as never)
})

describe('Local YouTube Downloader settings', () => {
  it('renders local status and versions without legacy credential controls', async () => {
    const { default: SettingsPage } = await import('@/app/settings/page')
    renderPage(<SettingsPage />)

    expect(await screen.findByText('Local YouTube Downloader')).not.toBeNull()
    expect(screen.queryByText('2026.07.04')).not.toBeNull()
    expect(screen.getAllByText('7.1')).toHaveLength(2)
    expect(screen.queryByText('2.9.4')).not.toBeNull()
    expect(screen.getByText('Not configured')).not.toBeNull()
    expect(screen.getByRole('button', { name: 'Upload cookies.txt' })).not.toBeNull()
    expect(screen.getByText(/Ready · 1.3.1/)).not.toBeNull()
    expect(screen.queryByPlaceholderText(/API key/i)).toBeNull()
    expect(screen.queryByPlaceholderText(/username/i)).toBeNull()
  })

  it('validates bounded timeout and concurrency before save', async () => {
    const user = userEvent.setup()
    const { default: SettingsPage } = await import('@/app/settings/page')
    renderPage(<SettingsPage />)

    const timeout = await screen.findByLabelText('Timeout (seconds)')
    const concurrency = screen.getByLabelText('Concurrent downloads')
    await user.clear(timeout)
    await user.type(timeout, '29')
    await user.clear(concurrency)
    await user.type(concurrency, '4')

    expect(screen.queryByText('Use 30–1800 seconds.')).not.toBeNull()
    expect(screen.queryByText('Use 1–3 downloads.')).not.toBeNull()
    expect(screen.getByRole('button', { name: /save settings/i })).toBeDisabled()
    expect(api.downloaderApi.save).not.toHaveBeenCalled()
  })

  it('shows environment-managed cookies as read only and test result components', async () => {
    const managed = {
      ...diagnostics, degraded: true, status: 'degraded',
      cookies: { ...diagnostics.cookies, configured: true, source: 'environment', managedByEnvironment: true,
        canUpload: false, canDelete: false, valid: false, detail: 'The environment-managed cookies file is missing or unreadable.' },
      audioQualityManagedByEnvironment: true, timeoutManagedByEnvironment: true,
      concurrencyManagedByEnvironment: true,
    } as const
    vi.mocked(api.downloaderApi.get).mockResolvedValue(managed as never)
    vi.mocked(api.downloaderApi.test).mockResolvedValue({
      ok: true, status: 'degraded', detail: 'Local test complete.', diagnostics: managed,
    } as never)
    const user = userEvent.setup()
    const { default: SettingsPage } = await import('@/app/settings/page')
    renderPage(<SettingsPage />)

    expect(await screen.findByText('Configured by environment')).not.toBeNull()
    expect(screen.getByText('Read only')).not.toBeNull()
    expect(screen.queryByRole('button', { name: /upload cookies/i })).toBeNull()
    expect(screen.queryByRole('button', { name: /delete cookies/i })).toBeNull()
    expect(screen.getByLabelText('Audio quality')).toBeDisabled()
    expect(screen.getByLabelText('Timeout (seconds)')).toBeDisabled()
    expect(screen.getByLabelText('Concurrent downloads')).toBeDisabled()
    await user.click(screen.getByRole('button', { name: /test downloader/i }))
    await waitFor(() => expect(screen.queryByText('Local test complete.')).not.toBeNull())
    expect(screen.getByText('Cookies: environment · invalid')).not.toBeNull()
    expect(screen.getByText('PO-token provider: ready')).not.toBeNull()
    expect(api.downloaderApi.test).toHaveBeenCalledTimes(1)
  })

  it('uploads a selected file and refreshes status without rendering cookie content', async () => {
    const uploaded = { ...diagnostics.cookies, configured: true, source: 'managed', canDelete: true,
      valid: true, recordCount: 2, youtubeRecordCount: 2, uploadedAtUtc: '2026-08-04T20:00:00Z' } as const
    vi.mocked(api.youtubeCookiesApi.upload).mockResolvedValue(uploaded as never)
    const user = userEvent.setup()
    const { default: SettingsPage } = await import('@/app/settings/page')
    renderPage(<SettingsPage />)

    const file = new File([
      '# Netscape HTTP Cookie File\n.youtube.com\tTRUE\t/\tTRUE\t1\tFAKE_NAME\tNEVER_RENDER_FAKE_VALUE\n',
    ], 'youtube-cookies.txt', { type: 'text/plain' })
    await user.upload(await screen.findByLabelText('Choose YouTube cookies.txt'), file)

    await waitFor(() => expect(screen.getByText(/Cookies uploaded and validated/)).not.toBeNull())
    expect(api.youtubeCookiesApi.upload).toHaveBeenCalledWith(file)
    expect(screen.getByRole('button', { name: 'Replace cookies.txt' })).not.toBeNull()
    expect(screen.getByRole('button', { name: 'Delete cookies' })).not.toBeNull()
    expect(screen.queryByText('NEVER_RENDER_FAKE_VALUE')).toBeNull()
    expect(screen.queryByRole('button', { name: /download cookies/i })).toBeNull()
  })

  it('shows sanitized upload errors and confirms replace and delete', async () => {
    const existing = { ...diagnostics, cookies: { ...diagnostics.cookies, configured: true,
      source: 'managed', canDelete: true, valid: true, recordCount: 1, youtubeRecordCount: 1,
      uploadedAtUtc: '2026-08-04T20:00:00Z' } } as const
    vi.mocked(api.downloaderApi.get).mockResolvedValue(existing as never)
    vi.mocked(api.youtubeCookiesApi.upload).mockRejectedValue(new Error('Upload a Netscape cookies.txt export.'))
    vi.mocked(api.youtubeCookiesApi.delete).mockResolvedValue(diagnostics.cookies as never)
    const confirm = vi.spyOn(window, 'confirm').mockReturnValue(true)
    const user = userEvent.setup()
    const { default: SettingsPage } = await import('@/app/settings/page')
    renderPage(<SettingsPage />)

    await user.upload(await screen.findByLabelText('Choose YouTube cookies.txt'),
      new File(['not cookies'], 'cookies.txt', { type: 'text/plain' }))
    expect(await screen.findByText('Upload a Netscape cookies.txt export.')).not.toBeNull()
    expect(confirm).toHaveBeenCalledWith('Replace the current uploaded cookies.txt file?')

    await user.click(screen.getByRole('button', { name: 'Delete cookies' }))
    await waitFor(() => expect(screen.getByText('Uploaded cookies deleted.')).not.toBeNull())
    expect(confirm).toHaveBeenCalledWith(expect.stringContaining('Delete the uploaded YouTube cookies'))
  })

  it('renders degraded and disabled PO provider states', async () => {
    vi.mocked(api.downloaderApi.get).mockResolvedValue({ ...diagnostics,
      poTokenProvider: { ...diagnostics.poTokenProvider, status: 'degraded', providerReachable: false, version: null } } as never)
    const { default: SettingsPage } = await import('@/app/settings/page')
    const view = renderPage(<SettingsPage />)
    expect(await screen.findByText('Unavailable')).not.toBeNull()

    view.unmount()
    vi.mocked(api.downloaderApi.get).mockResolvedValue({ ...diagnostics,
      poTokenProvider: { ...diagnostics.poTokenProvider, mode: 'disabled', status: 'disabled',
        pluginDetected: false, providerReachable: false, version: null } } as never)
    renderPage(<SettingsPage />)
    expect(await screen.findByText('Disabled')).not.toBeNull()
  })
})
