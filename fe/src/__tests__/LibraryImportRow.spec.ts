/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */
import { mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import LibraryImportRow from '@/components/domain/audiobook/LibraryImportRow.vue'
import { useLibraryImportStore } from '@/stores/libraryImport'
import type { LibraryImportItem } from '@/stores/libraryImport'
import type { SearchResult } from '@/types'

vi.mock('@/composables/useProtectedImages', () => ({
  useProtectedImages: () => ({ getProtectedImageSrc: () => 'https://example.com/cover.jpg' }),
}))

function candidate(overrides: Partial<SearchResult>): SearchResult {
  return {
    id: overrides.asin ?? 'id',
    title: 'Mistborn',
    artist: 'Brandon Sanderson',
    album: '',
    category: '',
    source: 'Audible',
    publishedDate: '',
    format: '',
    size: 0,
    magnetLink: '',
    torrentUrl: '',
    nzbUrl: '',
    downloadType: '',
    ...overrides,
  } as SearchResult
}

function row(overrides: Partial<LibraryImportItem> = {}): LibraryImportItem {
  return {
    id: '/books/Mistborn/Mistborn.m4b',
    fullPath: '/books/Mistborn/Mistborn.m4b',
    sourceFiles: ['/books/Mistborn/Mistborn.m4b'],
    folderPath: '/books/Mistborn',
    relativePath: 'Mistborn',
    folderName: 'Mistborn',
    detectedTitle: 'Mistborn, The Final Empire',
    detectedAuthor: 'Brandon Sanderson',
    format: 'M4B',
    fileCount: 1,
    selectedMatch: null,
    matchState: 'unsearched',
    matchIssue: 'none',
    candidates: [],
    hasSearched: false,
    isSearching: false,
    selected: false,
    ...overrides,
  }
}

describe('LibraryImportRow', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
  })

  it('offers the candidates inline when the match needs review', async () => {
    const candidates = [
      candidate({
        asin: 'B004SOK2SE',
        title: 'Mistborn',
        subtitle: 'Mistborn, Book 1',
        lengthMinutes: 1499,
        releaseDate: '2009-05-06',
        matchScore: 0.62,
        matchReasons: ['author matches', 'title partially matches'],
        imageUrl: '/api/v1/images/B004SOK2SE',
      }),
      candidate({
        asin: 'B07F88TSBT',
        title: 'Mistborn: Secret History',
        lengthMinutes: 329,
        matchScore: 0.46,
      }),
    ]

    const wrapper = mount(LibraryImportRow, {
      props: {
        item: row({
          matchState: 'needs-review',
          matchIssue: 'low-confidence',
          hasSearched: true,
          selectedMatch: candidates[0]!,
          candidates,
        }),
      },
    })

    // No modal round trip: both candidates are on the row, with the detail that tells them apart.
    const list = wrapper.get('[data-testid="candidate-list"]')
    expect(list.findAll('.candidate')).toHaveLength(2)
    expect(list.text()).toContain('24h 59m')
    expect(list.text()).toContain('5h 29m')
    expect(list.text()).toContain('2009')

    const store = useLibraryImportStore()
    const selectMatch = vi.spyOn(store, 'selectMatch').mockImplementation(() => {})
    await list.findAll('.candidate')[1]!.trigger('click')
    expect(selectMatch).toHaveBeenCalledWith('/books/Mistborn/Mistborn.m4b', candidates[1])
  })

  it('explains why a row was not matched instead of saying "No match found"', () => {
    const wrapper = mount(LibraryImportRow, {
      props: {
        item: row({
          matchState: 'needs-review',
          matchIssue: 'low-confidence',
          hasSearched: true,
          selectedMatch: candidate({
            asin: 'B07F88TSBT',
            title: 'Mistborn: Secret History',
            matchScore: 0.46,
            matchReasons: ['author matches', 'runtime disagrees (329 min vs 1499 min)'],
          }),
          candidates: [],
        }),
      },
    })

    expect(wrapper.get('[data-testid="review-hint"]').text()).toContain(
      'runtime disagrees (329 min vs 1499 min)',
    )
  })

  it('reports a rate limit rather than a missing book', () => {
    const wrapper = mount(LibraryImportRow, {
      props: { item: row({ matchIssue: 'rate-limited' }) },
    })

    expect(wrapper.get('[data-testid="match-explanation"]').text()).toContain('Rate limited')
  })

  it('keeps a confident match quiet but lets it be changed', async () => {
    const candidates = [
      candidate({ asin: 'B004SOK2SE', title: 'Mistborn', matchScore: 0.96 }),
      candidate({ asin: 'B07F88TSBT', title: 'Mistborn: Secret History', matchScore: 0.46 }),
    ]
    const wrapper = mount(LibraryImportRow, {
      props: {
        item: row({
          matchState: 'matched',
          hasSearched: true,
          selected: true,
          selectedMatch: candidates[0]!,
          candidates,
        }),
      },
    })

    expect(wrapper.find('[data-testid="candidate-list"]').exists()).toBe(false)
    expect(wrapper.get('[data-testid="match-confidence"]').text()).toBe('96%')

    await wrapper.get('[data-testid="change-match"]').trigger('click')
    expect(wrapper.findAll('[data-testid="candidate-list"] .candidate')).toHaveLength(2)
  })
})
