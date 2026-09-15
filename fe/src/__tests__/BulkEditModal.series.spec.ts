/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import BulkEditModal from '@/components/domain/collection/BulkEditModal.vue'
import { executeBulkEdit } from '@/utils/bulkEditOrchestration'
import { useLibraryStore } from '@/stores/library'
import type { Audiobook } from '@/types'

const { mockGetSeriesLookup, mockGetSeriesCatalog, mockGetImageUrl } = vi.hoisted(() => ({
  mockGetSeriesLookup: vi.fn(async () => null),
  mockGetSeriesCatalog: vi.fn(async () => null),
  mockGetImageUrl: vi.fn((url: string) => url),
}))

vi.mock('@/services/api', () => ({
  apiService: {
    getSeriesLookup: mockGetSeriesLookup,
    getSeriesCatalog: mockGetSeriesCatalog,
    getImageUrl: mockGetImageUrl,
    getQualityProfiles: vi.fn(async () => []),
    getApplicationSettings: vi.fn(async () => ({})),
    bulkUpdateAudiobooks: vi.fn(),
  },
}))

vi.mock('@/services/toastService', () => ({
  useToast: () => ({ success: vi.fn(), error: vi.fn(), info: vi.fn() }),
}))

vi.mock('@/utils/bulkEditOrchestration', () => ({
  executeBulkEdit: vi.fn(),
}))

const executeBulkEditMock = vi.mocked(executeBulkEdit)

const library = [
  {
    id: 1,
    title: 'The Final Empire',
    authors: ['Brandon Sanderson'],
    asin: 'BOOK1',
    seriesMemberships: [{ seriesName: 'Mistborn', seriesNumber: '1', isPrimary: true }],
    files: [],
  },
  {
    id: 2,
    title: 'The Well of Ascension',
    authors: ['Brandon Sanderson'],
    asin: 'BOOK2',
    seriesMemberships: [{ seriesName: 'Mistborn', seriesNumber: '2', isPrimary: true }],
    files: [],
  },
  {
    id: 3,
    title: 'Hero of Ages',
    authors: ['Brandon Sanderson'],
    asin: 'BOOK3',
    seriesMemberships: [{ seriesName: 'The Mistborn Saga', isPrimary: true }],
    files: [],
  },
] as unknown as Audiobook[]

interface ModalVm {
  formData: {
    seriesMode: string
    seriesName: string
    seriesAsin: string | null
    matchName: string
    numberingMode: string
    perBookNumbers: Record<number, string>
  }
  perBookOrder: number[]
  handleSave: () => Promise<void>
  showOrganizePrompt: boolean
}

function mountModal(props: Record<string, unknown> = {}) {
  const pinia = createPinia()
  setActivePinia(pinia)
  const store = useLibraryStore()
  store.audiobooks = library

  return mount(BulkEditModal, {
    props: {
      isOpen: true,
      selectedCount: 3,
      selectedIds: new Set([1, 2, 3]),
      selectedIdsOrdered: [1, 2, 3],
      ...props,
    },
    global: {
      plugins: [pinia],
      stubs: {
        Modal: { template: '<div><slot /><slot name="footer" /></div>' },
        ModalBody: { template: '<div><slot /></div>' },
        ModalHeader: true,
        MoveAudiobookModal: true,
        RootFolderSelect: true,
        Checkbox: true,
      },
    },
  })
}

describe('BulkEditModal series section', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mockGetSeriesLookup.mockResolvedValue(null)
    mockGetSeriesCatalog.mockResolvedValue(null)
    executeBulkEditMock.mockResolvedValue({
      results: [
        { id: 1, success: true, pathChangeOutcome: 'none', errors: [] },
        { id: 2, success: true, pathChangeOutcome: 'none', errors: [] },
        { id: 3, success: true, pathChangeOutcome: 'none', errors: [] },
      ],
    })
  })

  it('sends the stored spelling and per-book numbers 1, 2, 3 in one bulk update', async () => {
    const wrapper = mountModal()
    const vm = wrapper.vm as unknown as ModalVm

    // Choose "Set as primary series".
    await wrapper.find('[data-testid="series-mode-setPrimary"]').setValue()

    // Pick the existing spelling from the local suggestions rather than typing a new variant.
    const suggestions = wrapper.findAll('[data-testid="local-series-suggestions"] button')
    const target = suggestions.find((button) => button.text().includes('The Mistborn Saga'))
    expect(target).toBeDefined()
    await target!.trigger('click')
    expect(vm.formData.seriesName).toBe('The Mistborn Saga')

    // Set per book, then renumber 1..N in the order the view handed over.
    await wrapper.find('[data-testid="series-numbering-perBook"]').setValue()
    await wrapper.find('[data-testid="renumber-button"]').trigger('click')

    await vm.handleSave()

    expect(executeBulkEditMock).toHaveBeenCalledTimes(1)
    const request = executeBulkEditMock.mock.calls[0]![0]
    expect(request.ids).toEqual([1, 2, 3])
    expect(request.updates.series).toEqual({
      mode: 'setPrimary',
      seriesName: 'The Mistborn Saga',
      numbering: 'explicit',
    })
    expect(request.perIdOverrides).toEqual({
      1: {
        series: {
          mode: 'setPrimary',
          seriesName: 'The Mistborn Saga',
          numbering: 'explicit',
          seriesNumber: '1',
        },
      },
      2: {
        series: {
          mode: 'setPrimary',
          seriesName: 'The Mistborn Saga',
          numbering: 'explicit',
          seriesNumber: '2',
        },
      },
      3: {
        series: {
          mode: 'setPrimary',
          seriesName: 'The Mistborn Saga',
          numbering: 'explicit',
          seriesNumber: '3',
        },
      },
    })
  })

  it('renumbers in the order the user reorders the books into', async () => {
    const wrapper = mountModal()
    const vm = wrapper.vm as unknown as ModalVm

    await wrapper.find('[data-testid="series-mode-setPrimary"]').setValue()
    vm.formData.seriesName = 'The Mistborn Saga'
    await wrapper.find('[data-testid="series-numbering-perBook"]').setValue()
    await wrapper.find('[data-testid="per-book-down-1"]').trigger('click')
    await wrapper.find('[data-testid="renumber-button"]').trigger('click')

    expect(vm.perBookOrder).toEqual([2, 1, 3])
    expect(vm.formData.perBookNumbers).toEqual({ 1: '2', 2: '1', 3: '3' })
  })

  it('offers a target picker only for the modes that need one', async () => {
    const wrapper = mountModal()

    await wrapper.find('[data-testid="series-mode-removeMembership"]').setValue()
    expect(wrapper.find('[data-testid="series-name-input"]').exists()).toBe(false)
    expect(wrapper.find('[data-testid="series-match-select"]').exists()).toBe(true)
    expect(wrapper.find('[data-testid="series-match-select"]').text()).toContain('Mistborn')

    await wrapper.find('[data-testid="series-mode-renameMembership"]').setValue()
    expect(wrapper.find('[data-testid="series-name-input"]').exists()).toBe(true)
    expect(wrapper.find('[data-testid="series-match-select"]').exists()).toBe(true)

    await wrapper.find('[data-testid="series-mode-addMembership"]').setValue()
    expect(wrapper.find('[data-testid="series-match-select"]').exists()).toBe(false)
  })

  it('fills the name and the ASIN from a debounced Audible suggestion', async () => {
    vi.useFakeTimers()
    try {
      mockGetSeriesLookup.mockResolvedValue({
        asin: 'SERIES123',
        name: 'The Stormlight Archive',
      } as never)
      const wrapper = mountModal()
      const vm = wrapper.vm as unknown as ModalVm

      await wrapper.find('[data-testid="series-mode-setPrimary"]').setValue()
      await wrapper.find('[data-testid="series-name-input"]').setValue('Stormlight')
      expect(mockGetSeriesLookup).not.toHaveBeenCalled()

      await vi.advanceTimersByTimeAsync(400)
      await flushPromises()
      expect(mockGetSeriesLookup).toHaveBeenCalledWith('Stormlight')

      const remote = wrapper.findAll('[data-testid="remote-series-suggestions"] button')
      expect(remote).toHaveLength(1)
      await remote[0]!.trigger('click')
      expect(vm.formData.seriesName).toBe('The Stormlight Archive')
      expect(vm.formData.seriesAsin).toBe('SERIES123')
    } finally {
      vi.useRealTimers()
    }
  })

  it('fills positions from the Audible catalogue by ASIN', async () => {
    mockGetSeriesCatalog.mockResolvedValue({
      series: { asin: 'SERIES123', name: 'The Mistborn Saga' },
      totalBooks: 2,
      books: [
        { asin: 'BOOK1', title: 'The Final Empire', seriesNumber: '1' },
        { asin: 'BOOK3', title: 'Hero of Ages', seriesNumber: '3' },
      ],
    } as never)
    const wrapper = mountModal()
    const vm = wrapper.vm as unknown as ModalVm

    await wrapper.find('[data-testid="series-mode-setPrimary"]').setValue()
    vm.formData.seriesName = 'The Mistborn Saga'
    vm.formData.seriesAsin = 'SERIES123'
    await wrapper.find('[data-testid="series-numbering-perBook"]').setValue()
    await wrapper.find('[data-testid="fill-from-audible"]').trigger('click')
    await flushPromises()

    expect(mockGetSeriesCatalog).toHaveBeenCalledWith('The Mistborn Saga')
    expect(vm.formData.perBookNumbers[1]).toBe('1')
    expect(vm.formData.perBookNumbers[3]).toBe('3')
    expect(vm.formData.perBookNumbers[2]).toBeUndefined()
  })

  it('offers the Organize preview after a primary series change instead of moving files', async () => {
    const wrapper = mountModal()
    const vm = wrapper.vm as unknown as ModalVm

    await wrapper.find('[data-testid="series-mode-renameMembership"]').setValue()
    vm.formData.matchName = 'Mistborn'
    vm.formData.seriesName = 'The Mistborn Saga'

    await vm.handleSave()
    await flushPromises()

    expect(wrapper.emitted('saved')).toHaveLength(1)
    expect(wrapper.emitted('close')).toBeUndefined()
    expect(vm.showOrganizePrompt).toBe(true)

    await wrapper.find('[data-testid="organize-now"]').trigger('click')
    expect(wrapper.emitted('organize')).toEqual([[[1, 2, 3]]])
    expect(wrapper.emitted('close')).toHaveLength(1)
  })

  it('adding a membership does not offer Organize, because the primary is unchanged', async () => {
    const wrapper = mountModal()
    const vm = wrapper.vm as unknown as ModalVm

    await wrapper.find('[data-testid="series-mode-addMembership"]').setValue()
    vm.formData.seriesName = 'Cosmere'

    await vm.handleSave()
    await flushPromises()

    expect(vm.showOrganizePrompt).toBe(false)
    expect(wrapper.emitted('close')).toHaveLength(1)
  })

  it('opens pre-set to rename when the caller supplies a mode and a match name', () => {
    const wrapper = mountModal({
      initialSeriesMode: 'renameMembership',
      initialMatchName: 'Mistborn',
    })
    const vm = wrapper.vm as unknown as ModalVm
    expect(vm.formData.seriesMode).toBe('renameMembership')
    expect(vm.formData.matchName).toBe('Mistborn')
  })

  it('refuses to save a mode that is missing its series name', async () => {
    const wrapper = mountModal()
    const vm = wrapper.vm as unknown as ModalVm

    await wrapper.find('[data-testid="series-mode-setPrimary"]').setValue()
    await vm.handleSave()

    expect(executeBulkEditMock).not.toHaveBeenCalled()
  })
})
