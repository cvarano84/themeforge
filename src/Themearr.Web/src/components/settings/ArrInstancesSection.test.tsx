import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'

vi.mock('@/lib/api', async () => (await import('@/test/apiMock')).makeApiMock())
const api = await import('@/lib/api')
const { ArrInstancesSection } = await import('./ArrInstancesSection')

const instances = [
  { id: 'r1', serviceType: 'radarr' as const, name: 'Movies', url: 'http://radarr:7878',
    configured: true, enabled: true, qualityLabel: '1080p', priority: 10, tags: [],
    createdAt: '', updatedAt: '', lastSuccessfulSync: null, health: 'healthy' as const,
    healthDetail: null, unresolvedPathCount: 0, unresolvedPathSample: null },
  { id: 'r2', serviceType: 'radarr' as const, name: 'Movies - 4K', url: 'http://radarr4k:7878',
    configured: true, enabled: true, qualityLabel: '4K', priority: 0, tags: [],
    createdAt: '', updatedAt: '', lastSuccessfulSync: null, health: 'error' as const,
    healthDetail: 'connection timed out', unresolvedPathCount: 2, unresolvedPathSample: '/movies/A' },
  { id: 's1', serviceType: 'sonarr' as const, name: 'Anime', url: 'http://sonarr:8989',
    configured: true, enabled: false, qualityLabel: 'Anime', priority: 5, tags: [],
    createdAt: '', updatedAt: '', lastSuccessfulSync: null, health: 'unknown' as const,
    healthDetail: null, unresolvedPathCount: 0, unresolvedPathSample: null },
]

beforeEach(() => {
  vi.clearAllMocks()
  vi.mocked(api.arrInstancesApi.list).mockResolvedValue(instances)
})

describe('Arr instances settings', () => {
  it('renders multiple Radarr and Sonarr cards with quality and health state', async () => {
    render(<ArrInstancesSection />)
    expect(await screen.findByText('Movies')).toBeTruthy()
    expect(screen.getByText('Movies - 4K')).toBeTruthy()
    expect(screen.getAllByText('Anime').length).toBeGreaterThan(0)
    expect(screen.getByText('connection timed out')).toBeTruthy()
    expect(screen.getAllByText('4K').length).toBeGreaterThan(0)
  })

  it('opens Add Radarr and submits a write-only key', async () => {
    const user = userEvent.setup()
    vi.mocked(api.arrInstancesApi.create).mockResolvedValue({ ...instances[0], id: 'r3', name: 'Remux', qualityLabel: 'Remux' })
    render(<ArrInstancesSection />)
    await screen.findByText('Movies')
    await user.click(screen.getByRole('button', { name: 'Add Radarr' }))
    await user.clear(screen.getByLabelText('Instance name'))
    await user.type(screen.getByLabelText('Instance name'), 'Remux')
    await user.type(screen.getByLabelText('URL'), 'http://remux:7878')
    await user.type(screen.getByLabelText('API key'), 'write-only-secret')
    await user.click(screen.getByRole('button', { name: 'Save' }))
    await waitFor(() => expect(api.arrInstancesApi.create).toHaveBeenCalledWith(expect.objectContaining({
      name: 'Remux', url: 'http://remux:7878', apiKey: 'write-only-secret', serviceType: 'radarr',
    })))
  })

  it('requires explicit confirmation before deletion', async () => {
    const user = userEvent.setup()
    vi.spyOn(window, 'confirm').mockReturnValue(false)
    render(<ArrInstancesSection />)
    await screen.findByText('Movies')
    await user.click(screen.getAllByRole('button', { name: 'Delete' })[0])
    expect(api.arrInstancesApi.delete).not.toHaveBeenCalled()
  })
})
