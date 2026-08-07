import { render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { beforeEach, describe, expect, it, vi } from 'vitest'

vi.mock('@/lib/api', async () => (await import('@/test/apiMock')).makeApiMock())
vi.mock('@/components/layout/AppShell', () => ({ AppShell: ({ children }: { children: React.ReactNode }) => <main>{children}</main> }))

const api = await import('@/lib/api')
const DashboardPage = (await import('./page')).default

const stats = {
  coverage: 100, total: 5857, downloaded: 5857, pending: 24, ignored: 12, addedThisWeek: 37,
  recentActivity: [{ id: 'h1', movieTitle: 'A very long movie title that needs room on a phone', movieYear: 2026, themeTitle: 'A readable soundtrack title', downloadedAt: new Date().toISOString() }],
  recentlyAdded: [{ id: 'm1', title: 'A recently added movie with a useful full title', year: 2026, posterUrl: null }],
}

beforeEach(() => vi.clearAllMocks())

function renderDashboard() {
  return render(<MemoryRouter><DashboardPage /></MemoryRouter>)
}

describe('responsive dashboard structure', () => {
  it('renders semantic coverage, two-column phone stats, and readable activity actions', async () => {
    vi.mocked(api.statsApi.get).mockResolvedValue(stats as never)
    renderDashboard()

    const progress = await screen.findByRole('progressbar', { name: 'Movie theme library coverage' })
    expect(progress).toHaveAttribute('aria-valuetext', '5,857 of 5,857 movies have themes')
    expect(screen.getByText('5,857 of 5,857')).toBeInTheDocument()
    expect(screen.getByRole('link', { name: /Pending: 24/ }).parentElement).toHaveClass('min-[360px]:grid-cols-2')
    expect(screen.getByRole('link', { name: 'View all' })).toHaveClass('min-h-11')
    expect(screen.getByRole('link', { name: 'Go to queue' })).toHaveClass('min-h-11')
  })

  it('centers clean empty states in both activity sections', async () => {
    vi.mocked(api.statsApi.get).mockResolvedValue({ ...stats, pending: 0, recentActivity: [], recentlyAdded: [] } as never)
    renderDashboard()
    expect(await screen.findByText('No themes downloaded yet.')).toBeInTheDocument()
    expect(screen.getByText('All movies have themes!')).toBeInTheDocument()
  })

  it('exposes loading and error states', async () => {
    let rejectLoad: (error: Error) => void = () => {}
    vi.mocked(api.statsApi.get).mockReturnValue(new Promise((_, reject) => { rejectLoad = reject }) as never)
    const { unmount } = renderDashboard()
    expect(screen.getByRole('status', { name: 'Loading dashboard' })).toBeInTheDocument()
    rejectLoad(new Error('Dashboard unavailable'))
    await waitFor(() => expect(screen.getByText("Couldn't load the dashboard")).toBeInTheDocument())
    unmount()
  })
})
