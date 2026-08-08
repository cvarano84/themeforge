export interface Movie {
  id: string
  source: string
  sourceRef: string
  title: string
  year: number | null
  sourcePath: string | null
  folderName: string
  status: 'pending' | 'downloaded' | 'unresolved' | 'ignored'
  posterUrl: string | null
  logicalId?: string
  aggregateStatus?: AggregateMediaStatus
  qualityLabels?: string[]
  locations?: MediaLocation[]
}

export interface MoviePage {
  items: Movie[]
  page: number
  pageSize: number
  total: number
  totalPages: number
  statusCounts: Record<string, number>
  qualities: string[]
  instances: { id: string; name: string }[]
  lastSyncedAt: string | null
}

export interface MoviePageQuery {
  page?: number
  pageSize?: 25 | 50 | 100
  search?: string
  status?: string
  instanceId?: string
  quality?: string
  sort?: 'title' | 'year' | 'status' | 'syncedAt'
  direction?: 'asc' | 'desc'
}

export interface YoutubeResult {
  videoId: string
  title: string
  thumbnail: string | null
  duration: string | null
  channel: string
  score: number
  bestMatch: boolean
}

export interface PlexServer {
  id: string
  name: string
  url: string
  urls: string[]
  token: string
  owned: boolean
  presence: boolean
}

/** Every status any media type can report. Movies never use 'plexTheme'. */
export type MediaStatus = 'pending' | 'downloaded' | 'plexTheme' | 'unresolved' | 'ignored'
export type AggregateMediaStatus = 'missing' | 'partial' | 'downloaded' | 'ignored' | 'unavailable'

export interface MediaLocation {
  id: string
  instanceId: string | null
  instanceName: string | null
  remoteItemId: string | null
  sourceRef: string
  sourcePath: string | null
  folderName: string
  qualityLabel: string | null
  status: MediaStatus
  priority: number
}

/**
 * The shape MediaGrid renders. `Movie` is assignable to this — its narrower status
 * union is a subset of MediaStatus — so nothing about movies has to change.
 */
export interface MediaItem {
  id: string
  source: string
  sourceRef: string
  title: string
  year: number | null
  sourcePath: string | null
  folderName: string
  status: MediaStatus
  posterUrl: string | null
  logicalId?: string
  aggregateStatus?: AggregateMediaStatus
  qualityLabels?: string[]
  locations?: MediaLocation[]
}

export interface Show extends MediaItem {
  /** True when Plex already has a theme for this show (Plex Pass, or a local file it found). */
  plexHasTheme: boolean
}

export interface ShowStats {
  total: number
  downloaded: number
  plexTheme: number
  pending: number
  ignored: number
  coverage: number
}

export interface PlexLibrary {
  key: string
  title: string
  type: string
}

export interface PathMapping {
  source: string
  target: string
  instanceId?: string
  serviceType?: ArrServiceType
}

export interface SetupStatus {
  setupComplete: boolean
  plexConnected: boolean
  plexAccountName: string
  selectedServers: PlexServer[]
  selectedLibraries: Record<string, string[]>
  selectedShowLibraries: Record<string, string[]>
  movieLibrarySource: 'plex' | 'radarr' | 'disabled'
  showLibrarySource: 'plex' | 'sonarr' | 'disabled'
  pathMappings: PathMapping[]
  libraryPaths: string[]
}

export interface Settings {
  selectedServers: PlexServer[]
  selectedLibraries: Record<string, string[]>
  /** Optional: absent on a response from a server older than 1d. */
  selectedShowLibraries?: Record<string, string[]>
  movieLibrarySource: 'plex' | 'radarr' | 'disabled'
  showLibrarySource: 'plex' | 'sonarr' | 'disabled'
  pathMappings: PathMapping[]
  libraryPaths: string[]
  advanced: {
    maxSearchDirs: number
    searchDepth: number
  }
  autoDownload: boolean
  autoSync: boolean
  lastAutoSyncAt: string
}

export interface SyncStatus {
  inProgress: boolean
  finished: boolean
  error: string
  synced: number
  logs: string[]
  sourceItemsReceived: number
  directlyResolved: number
  resolvedByMapping: number
  resolvedBySuffix: number
  unresolved: number
  stalePathsRepaired: number
  outsideRootPathsRejected: number
  duplicates: number
}

export interface HistoryEntry {
  id: number
  movieId: string
  movieTitle: string
  movieYear: number | null
  themeTitle: string | null
  sourceUrl: string | null
  downloadedAt: string
}

export interface DashboardStats {
  total: number
  downloaded: number
  pending: number
  ignored: number
  coverage: number
  addedThisWeek: number
  recentActivity: HistoryEntry[]
  recentlyAdded: { id: string; title: string; year: number | null; syncedAt: string | null; posterUrl: string | null }[]
}

export type DashboardSummary = Omit<DashboardStats, 'recentActivity' | 'recentlyAdded'>
export type DashboardActivity = Pick<DashboardStats, 'recentActivity' | 'recentlyAdded'>

export interface VersionInfo {
  current: string
  latest: string
  updateAvailable: boolean
  updating: boolean
  updateError: string
  checkError: string
  repo: string
}

export type HealthType = 'ok' | 'warning' | 'error'

export interface HealthItem {
  source: string
  type: HealthType
  message: string
  wikiUrl: string | null
}

export interface HealthResponse {
  status: HealthType
  checks: HealthItem[]
}

export interface SystemTask {
  id: string
  name: string
  /**
   * Serialized .NET TimeSpan in the constant format `[d.]hh:mm:ss`, e.g.
   * "1.00:00:00" for 24 hours (the leading "1." is a day component, not
   * an hour count).
   */
  interval: string
  lastRunUtc: string | null
  lastDurationMs: number | null
  lastResult: string | null
  nextRunUtc: string | null
  isRunning: boolean
}

export interface RadarrSettings {
  source: 'plex' | 'radarr' | 'disabled'
  url: string
  /** The API key is never sent to the browser; this only says whether one is stored. */
  configured: boolean
}

export interface ApiKey {
  key: string
}

export interface SonarrSettings {
  source: 'plex' | 'sonarr' | 'disabled'
  url: string
  /** The API key is never sent to the browser; this only says whether one is stored. */
  configured: boolean
}

export type ArrServiceType = 'radarr' | 'sonarr'

export interface ArrInstance {
  id: string
  serviceType: ArrServiceType
  name: string
  url: string
  configured: boolean
  enabled: boolean
  qualityLabel: string | null
  priority: number
  tags: string[]
  createdAt: string
  updatedAt: string
  lastSuccessfulSync: string | null
  health: 'unknown' | 'healthy' | 'error'
  healthDetail: string | null
  unresolvedPathCount: number
  unresolvedPathSample: string | null
}

export interface ArrInstanceInput {
  serviceType: ArrServiceType
  name: string
  url: string
  apiKey: string
  enabled: boolean
  qualityLabel: string
  priority: number
  tags: string[]
}

export interface PathMappingTestResult {
  sourceFilePath: string
  sourceFolderPath: string
  matchedMapping: PathMapping | null
  mappedCandidate: string | null
  candidateExists: boolean
  candidateWithinRoots: boolean
  resolutionMode: 'direct' | 'mapping' | 'suffix' | 'unresolved'
  resolvedFolderPath: string | null
  failureReason: string | null
}

export interface PathRepairResult {
  examined: number
  unchanged: number
  repaired: number
  unresolved: number
}

export interface DownloaderComponentStatus {
  available: boolean
  status: string
  version: string | null
  detail: string | null
}

export interface DownloaderDiagnostics {
  ready: boolean
  degraded: boolean
  status: 'healthy' | 'degraded' | 'unhealthy'
  summary: string
  ytDlp: DownloaderComponentStatus
  ffmpeg: DownloaderComponentStatus
  ffprobe: DownloaderComponentStatus
  javaScriptRuntime: DownloaderComponentStatus
  cookies: YoutubeCookieStatus
  poTokenProvider: PoTokenProviderStatus
  audioQuality: '128K' | '192K' | '256K' | '320K'
  timeoutSeconds: number
  concurrentDownloads: number
  audioQualityManagedByEnvironment: boolean
  timeoutManagedByEnvironment: boolean
  concurrencyManagedByEnvironment: boolean
}

export interface YoutubeCookieStatus {
  configured: boolean
  source: 'none' | 'managed' | 'environment'
  managedByEnvironment: boolean
  canUpload: boolean
  canDelete: boolean
  valid: boolean
  recordCount: number
  youtubeRecordCount: number
  uploadedAtUtc: string | null
  detail: string | null
}

export interface PoTokenProviderStatus {
  mode: 'auto' | 'disabled' | 'required'
  status: 'disabled' | 'notConfigured' | 'ready' | 'degraded' | 'requiredUnavailable'
  pluginDetected: boolean
  providerReachable: boolean
  version: string | null
  detail: string | null
}

export interface DownloaderTestResult {
  ok: boolean
  status: string
  detail: string
  diagnostics: DownloaderDiagnostics
}
