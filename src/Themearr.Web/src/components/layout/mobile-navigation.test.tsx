import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { AppShell } from './AppShell'

vi.mock('@/lib/api', async () => (await import('@/test/apiMock')).makeApiMock())
vi.mock('@/lib/auth', () => ({
  useAuth: () => ({ loading: false, authorized: true, accountName: 'Chris', logout: vi.fn() }),
}))

const api = await import('@/lib/api')

beforeEach(() => {
  vi.clearAllMocks()
  vi.mocked(api.versionApi.get).mockResolvedValue({ current: 'vNext', latest: 'vNext', updateAvailable: false } as never)
  vi.mocked(api.syncApi.status).mockResolvedValue({ inProgress: false } as never)
  vi.mocked(api.systemApi.health).mockResolvedValue({ checks: [{ source: 'Library' }, { source: 'Downloader' }] } as never)
})

function renderShell(path = '/dashboard') {
  return render(<MemoryRouter initialEntries={[path]}><AppShell title="Dashboard"><p>Page content</p></AppShell></MemoryRouter>)
}

describe('mobile navigation', () => {
  it('opens with the active route and preserves live badges', async () => {
    const user = userEvent.setup()
    renderShell('/system')

    await user.click(screen.getByRole('button', { name: 'Open navigation menu' }))
    const dialog = screen.getByRole('dialog', { name: 'Main navigation' })
    expect(within(dialog).getByRole('link', { name: /System.*2 system warnings/ })).toHaveAttribute('aria-current', 'page')
    expect(document.body.style.overflow).toBe('hidden')
  })

  it('closes on Escape and restores focus to the menu button', async () => {
    const user = userEvent.setup()
    renderShell()
    const menu = screen.getByRole('button', { name: 'Open navigation menu' })

    await user.click(menu)
    await waitFor(() => expect(within(screen.getByRole('dialog')).getByRole('button', { name: 'Close navigation menu' })).toHaveFocus())
    await user.keyboard('{Escape}')

    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(menu).toHaveFocus()
    expect(document.body.style.overflow).toBe('')
  })

  it('traps keyboard focus and closes after navigation or backdrop activation', async () => {
    const user = userEvent.setup()
    renderShell()
    const menu = screen.getByRole('button', { name: 'Open navigation menu' })
    await user.click(menu)
    const dialog = screen.getByRole('dialog')
    const close = within(dialog).getByRole('button', { name: 'Close navigation menu' })
    await waitFor(() => expect(close).toHaveFocus())
    await user.keyboard('{Shift>}{Tab}{/Shift}')
    expect(within(dialog).getByRole('button', { name: 'Sign out' })).toHaveFocus()

    await user.click(within(dialog).getByRole('link', { name: 'Movies' }))
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()

    await user.click(menu)
    await user.click(screen.getAllByRole('button', { name: 'Close navigation menu' })[0])
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })
})
