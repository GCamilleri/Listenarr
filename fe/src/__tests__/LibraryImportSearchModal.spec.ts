/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <https://www.gnu.org/licenses/>.
 */
import { mount } from '@vue/test-utils'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { apiService } from '@/services/api'
import type { SearchResult } from '@/types'
import LibraryImportSearchModal from '@/components/domain/audiobook/LibraryImportSearchModal.vue'

const getProtectedImageSrc = vi.fn(() => 'https://example.com/protected.jpg')

vi.mock('@/composables/useProtectedImages', () => ({
  useProtectedImages: () => ({
    getProtectedImageSrc,
  }),
}))

describe('LibraryImportSearchModal', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    vi.spyOn(apiService, 'advancedSearch').mockResolvedValue([
      {
        asin: 'B000APXZHK',
        title: 'Alchemised',
        imageUrl: '/api/v1/images/B000APXZHK',
        authors: [{ name: 'SenLinYu' }],
      } as unknown as SearchResult,
    ])
  })

  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('routes result thumbnails through the protected image helper', async () => {
    const wrapper = mount(LibraryImportSearchModal, {
      props: {
        item: {
          id: 'C:\\incoming\\Alchemised.m4b',
          fullPath: 'C:\\incoming\\Alchemised.m4b',
          sourceFiles: ['C:\\incoming\\Alchemised.m4b'],
          folderPath: 'C:\\incoming',
          relativePath: 'Alchemised',
          folderName: 'Alchemised',
          detectedTitle: 'Alchemised',
          detectedAuthor: 'SenLinYu',
          format: 'M4B',
          fileCount: 1,
          selectedMatch: null,
          matchState: 'unsearched',
          matchIssue: 'none',
          candidates: [],
          hasSearched: false,
          isSearching: false,
          selected: false,
        },
      },
    })

    await new Promise((resolve) => setTimeout(resolve, 0))
    await wrapper.vm.$nextTick()

    expect(getProtectedImageSrc).toHaveBeenCalledWith(
      '/api/v1/images/B000APXZHK',
      '/placeholder.svg',
    )
    expect(wrapper.find('img').attributes('src')).toBe('https://example.com/protected.jpg')
  })

  it('starts from the detected title and asks for more than five results', async () => {
    const advancedSearch = vi.mocked(apiService.advancedSearch)
    const wrapper = mount(LibraryImportSearchModal, { props: { item: item() } })

    await new Promise((resolve) => setTimeout(resolve, 0))
    await wrapper.vm.$nextTick()

    // The old modal deliberately skipped detectedTitle, so it searched a different string than
    // the automatic pass did, and it capped results at five.
    expect(advancedSearch).toHaveBeenCalledWith(
      expect.objectContaining({ title: 'Alchemised', author: 'SenLinYu' }),
    )
    const params = advancedSearch.mock.calls[0]?.[0] as Record<string, unknown>
    expect(params.cap).toBeUndefined()
    expect(params.pagination).toEqual({ limit: 20 })
  })

  it('re-searches with the folder-name strategy when its chip is clicked', async () => {
    const advancedSearch = vi.mocked(apiService.advancedSearch)
    const wrapper = mount(LibraryImportSearchModal, { props: { item: item() } })

    await new Promise((resolve) => setTimeout(resolve, 0))
    advancedSearch.mockClear()

    await wrapper.get('[data-strategy="folder"]').trigger('click')
    await new Promise((resolve) => setTimeout(resolve, 0))

    expect(advancedSearch).toHaveBeenCalledWith(
      expect.objectContaining({ title: 'Alchemised Folder', author: undefined }),
    )
  })

  it('says so when the search request fails instead of stopping silently', async () => {
    vi.mocked(apiService.advancedSearch).mockRejectedValue(
      Object.assign(new Error('Rate limited'), { status: 429 }),
    )
    const wrapper = mount(LibraryImportSearchModal, { props: { item: item() } })

    await new Promise((resolve) => setTimeout(resolve, 0))
    await wrapper.vm.$nextTick()

    expect(wrapper.get('[data-testid="search-error"]').text()).toContain('Rate limited')
  })

  function item() {
    return {
      id: 'C:\\incoming\\Alchemised.m4b',
      fullPath: 'C:\\incoming\\Alchemised.m4b',
      sourceFiles: ['C:\\incoming\\Alchemised.m4b'],
      folderPath: 'C:\\incoming',
      relativePath: 'Alchemised Folder',
      folderName: 'Alchemised Folder',
      detectedTitle: 'Alchemised',
      detectedAuthor: 'SenLinYu',
      format: 'M4B',
      fileCount: 1,
      selectedMatch: null,
      matchState: 'unsearched' as const,
      matchIssue: 'none' as const,
      candidates: [],
      hasSearched: false,
      isSearching: false,
      selected: false,
    }
  }
})
