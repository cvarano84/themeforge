import { vi } from 'vitest'

/**
 * A fully-mocked `@/lib/api`. Every export is present and every method is a
 * `vi.fn()` that returns undefined until a test gives it a value, so a test only
 * has to configure the calls it cares about.
 *
 * Use it as:
 *   vi.mock('@/lib/api', async () => (await import('@/test/apiMock')).makeApiMock())
 */
export function makeApiMock() {
  const group = (...methods: string[]) =>
    Object.fromEntries(methods.map(m => [m, vi.fn()])) as Record<string, ReturnType<typeof vi.fn>>

  const moviesApi = group(
    'page', 'list', 'search', 'download', 'downloadUrl', 'downloadStatus', 'autoDownload',
    'deleteTheme', 'ignoreMovie', 'unignoreMovie', 'themeAudioObjectUrl',
  )
  const asPage = (items: Array<{ status?: string; aggregateStatus?: string; locations?: Array<{ qualityLabel?: string | null; instanceId?: string | null; instanceName?: string | null }> }> = []) => {
    const statusCounts: Record<string, number> = {}
    items.forEach(item => {
      const status = item.aggregateStatus ?? (item.status === 'pending' ? 'missing' : item.status ?? 'missing')
      statusCounts[status] = (statusCounts[status] ?? 0) + 1
    })
    return {
      items, page: 1, pageSize: 50, total: items.filter(item => item.status !== 'ignored').length,
      totalPages: items.length ? 1 : 0, statusCounts,
      qualities: [...new Set(items.flatMap(item => item.locations?.map(location => location.qualityLabel).filter(Boolean) ?? []))],
      instances: [], lastSyncedAt: null,
    }
  }
  const mockedMovieList = moviesApi.list as unknown as () => Promise<Array<{ status?: string; aggregateStatus?: string }> | undefined>
  moviesApi.page.mockImplementation(async () => asPage((await mockedMovieList()) ?? []))
  const queueApi = group('page')
  queueApi.page.mockImplementation(async () => {
    const items = ((await mockedMovieList()) ?? []).filter((item: { status?: string; aggregateStatus?: string }) =>
      item.status === 'pending' || item.aggregateStatus === 'missing')
    return asPage(items)
  })
  const statsApi = group('get', 'summary', 'activity')
  const mockedStatsGet = statsApi.get as unknown as () => Promise<Record<string, unknown>>
  statsApi.summary.mockImplementation(async () => {
    const stats = await mockedStatsGet()
    return {
      total: stats.total, downloaded: stats.downloaded, pending: stats.pending,
      ignored: stats.ignored, coverage: stats.coverage, addedThisWeek: stats.addedThisWeek,
    }
  })
  statsApi.activity.mockImplementation(async () => {
    const stats = await mockedStatsGet()
    return { recentActivity: stats.recentActivity, recentlyAdded: stats.recentlyAdded }
  })

  return {
    getAuthToken: () => 'test-token',
    setAuthToken: vi.fn(),
    clearAuthToken: vi.fn(),
    // Keep these in step with the exports of src/lib/api.ts.
    authApi: group('verify'),
    setupApi: group(
      'status', 'startPlexLogin', 'plexLoginStatus', 'plexServers', 'plexLibraries',
      'logout', 'saveSelection', 'reset', 'complete',
    ),
    moviesApi,
    queueApi,
    showsApi: group(
      'list', 'search', 'download', 'downloadUrl', 'downloadStatus',
      'deleteTheme', 'ignoreShow', 'unignoreShow', 'stats', 'themeAudioObjectUrl',
    ),
    settingsApi: group('get', 'save'),
    syncApi: group('start', 'status'),
    historyApi: group('get'),
    downloaderApi: {
      get: vi.fn().mockResolvedValue({
        ready: true, degraded: false, status: 'healthy', summary: 'Local theme downloader ready.',
        ytDlp: { available: true, status: 'available', version: '2026.07.04', detail: null },
        ffmpeg: { available: true, status: 'available', version: '7.1', detail: null },
        javaScriptRuntime: { available: true, status: 'available', version: '2.9.4', detail: null },
        ffprobe: { available: true, status: 'available', version: '7.1', detail: null },
        cookies: { configured: false, source: 'none', managedByEnvironment: false, canUpload: true,
          canDelete: false, valid: false, recordCount: 0, youtubeRecordCount: 0, uploadedAtUtc: null, detail: null },
        poTokenProvider: { mode: 'auto', status: 'notConfigured', pluginDetected: true,
          providerReachable: false, version: null, detail: 'The PO-token provider URL is not configured.' },
        audioQuality: '192K', timeoutSeconds: 300, concurrentDownloads: 1,
        audioQualityManagedByEnvironment: false, timeoutManagedByEnvironment: false,
        concurrencyManagedByEnvironment: false,
      }),
      save: vi.fn(),
      test: vi.fn(),
    },
    youtubeCookiesApi: group('get', 'upload', 'delete'),
    statsApi,
    versionApi: group('get', 'refresh', 'update', 'updateStatus'),
    systemApi: group('health', 'tasks', 'runTask'),
    radarrApi: group('get', 'save', 'test'),
    sonarrApi: {
      get: vi.fn().mockResolvedValue({ source: 'disabled', url: '', configured: false }),
      save: vi.fn(),
      test: vi.fn(),
    },
    arrInstancesApi: {
      list: vi.fn().mockResolvedValue([]),
      create: vi.fn(), update: vi.fn(), delete: vi.fn(), test: vi.fn(), sync: vi.fn(),
    },
    plexApi: group('test', 'saveUrl'),
    apiKeyApi: group('get', 'regenerate'),
  }
}
