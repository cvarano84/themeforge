import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'

vi.mock('@/lib/api', async () => (await import('@/test/apiMock')).makeApiMock())

const api = await import('@/lib/api')
const { MediaGrid } = await import('./MediaGrid')
const { moviesAdapter } = await import('@/lib/media/adapter')

const movie = (id: string, title: string) => ({ id, source: 'plex', sourceRef: id, title, year: 2026, sourcePath: null, folderName: title, posterUrl: null, status: 'pending' }) as never

describe('mobile media bulk actions', () => {
  it('selects keyboard-operable cards and ignores selected items from a sticky action region', async () => {
    const user = userEvent.setup()
    const onUpdated = vi.fn()
    vi.mocked(api.moviesApi.ignoreMovie).mockResolvedValue({ ignored: true } as never)
    render(<MediaGrid items={[movie('a', 'Movie Alpha'), movie('b', 'Movie Beta')]} adapter={moviesAdapter} onUpdated={onUpdated} emptyDescription="" />)

    await user.click(screen.getByRole('button', { name: 'Select' }))
    await user.click(screen.getByRole('button', { name: /Select Movie Alpha/ }))
    await user.click(screen.getByRole('button', { name: /Select Movie Beta/ }))
    expect(screen.getByRole('region', { name: 'Bulk actions' })).toHaveTextContent('2 movies selected')

    await user.click(screen.getByRole('button', { name: 'Ignore selected' }))
    await waitFor(() => expect(api.moviesApi.ignoreMovie).toHaveBeenCalledTimes(2))
    expect(onUpdated).toHaveBeenCalledWith('a', 'ignored')
    expect(onUpdated).toHaveBeenCalledWith('b', 'ignored')
    expect(screen.queryByRole('region', { name: 'Bulk actions' })).not.toBeInTheDocument()
  })
})
