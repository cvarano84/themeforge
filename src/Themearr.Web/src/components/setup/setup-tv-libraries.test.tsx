import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { AuthProvider } from '@/lib/auth'

vi.mock('@/lib/api', async () => (await import('@/test/apiMock')).makeApiMock())
const api = await import('@/lib/api')
const { SetupWizard } = await import('./SetupWizard')

beforeEach(() => {
  vi.clearAllMocks()
  vi.mocked(api.setupApi.status).mockResolvedValue({
    setupComplete: false, plexConnected: true, plexAccountName: 'User', selectedServers: [],
    selectedLibraries: {}, selectedShowLibraries: {}, movieLibrarySource: 'plex', showLibrarySource: 'disabled',
    pathMappings: [], libraryPaths: [],
  })
  vi.mocked(api.setupApi.plexServers).mockResolvedValue({ servers: [
    { id: 'srv', name: 'Tower', url: 'http://plex', urls: ['http://plex'], token: 'token', owned: true, presence: true },
  ] })
  vi.mocked(api.setupApi.plexLibraries).mockImplementation(async (_servers, type = 'movie') => ({
    libraries: { srv: type === 'show'
      ? [{ key: 'tv', title: 'Television', type: 'show' }]
      : [{ key: 'movies', title: 'Movies', type: 'movie' }] },
  }))
})

describe('setup Plex movie and TV libraries', () => {
  it('fetches both types separately and allows a TV-only selection to continue', async () => {
    const user = userEvent.setup()
    render(<MemoryRouter><AuthProvider><SetupWizard /></AuthProvider></MemoryRouter>)

    await waitFor(() => expect(api.setupApi.status).toHaveBeenCalledTimes(2))
    await user.click(screen.getByRole('button', { name: /^Plex\b/i }))
    await user.click(await screen.findByRole('button', { name: /Tower/i }))
    await user.click(screen.getByRole('button', { name: /Continue/i }))

    await screen.findByText('Movie Libraries')
    expect(screen.getByText('TV Show Libraries')).toBeTruthy()
    expect(api.setupApi.plexLibraries).toHaveBeenCalledWith(expect.any(Array), 'movie')
    expect(api.setupApi.plexLibraries).toHaveBeenCalledWith(expect.any(Array), 'show')

    await user.click(screen.getByRole('button', { name: /Television/i }))
    await user.click(screen.getByRole('button', { name: /Continue/i }))

    await waitFor(() => expect(screen.getByText('Local library paths')).toBeTruthy())
  })

  it('does not auto-select the TV library', async () => {
    const user = userEvent.setup()
    render(<MemoryRouter><AuthProvider><SetupWizard /></AuthProvider></MemoryRouter>)
    await waitFor(() => expect(api.setupApi.status).toHaveBeenCalledTimes(2))
    await user.click(screen.getByRole('button', { name: /^Plex\b/i }))
    await user.click(await screen.findByRole('button', { name: /Tower/i }))
    await user.click(screen.getByRole('button', { name: /Continue/i }))

    const tv = await screen.findByRole('button', { name: /Television/i })
    expect(tv.className).not.toContain('bg-[#BB0000]/10')
  })
})
