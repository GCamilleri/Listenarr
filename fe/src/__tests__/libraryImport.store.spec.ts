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
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { createPinia, setActivePinia } from 'pinia'
import type { SearchResult } from '@/types'

const startManualImport = vi.fn()
const addToLibrary = vi.fn()
const updateAudiobook = vi.fn()
const advancedSearch = vi.fn()
const scanUnmatchedFiles = vi.fn()
const getUnmatchedResults = vi.fn()
const getSavedUnmatchedFiles = vi.fn()
let unmatchedScanHandler:
  | ((payload: { jobId: string; error?: string }) => void | Promise<void>)
  | null = null

vi.mock('@/services/api', () => ({
  apiService: {
    addToLibrary,
    updateAudiobook,
    startManualImport,
    advancedSearch,
    getAudibleMetadata: vi.fn(),
    scanUnmatchedFiles,
    getUnmatchedResults,
    getSavedUnmatchedFiles,
  },
}))

vi.mock('@/services/signalr', () => ({
  signalRService: {
    onUnmatchedScanComplete: vi.fn((handler) => {
      unmatchedScanHandler = handler
      return () => {
        unmatchedScanHandler = null
      }
    }),
  },
}))

vi.mock('@/utils/logger', () => ({
  logger: {
    debug: vi.fn(),
  },
}))

describe('library import store', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    unmatchedScanHandler = null
    setActivePinia(createPinia())
    addToLibrary.mockResolvedValue({ audiobook: { id: 42 } })
    updateAudiobook.mockResolvedValue({})
    startManualImport.mockResolvedValue({ importedCount: 3, totalCount: 3, results: [] })
    advancedSearch.mockResolvedValue([])
    getSavedUnmatchedFiles.mockResolvedValue({ items: [], lastScannedAt: null })
  })

  it('submits every grouped source file during import', async () => {
    const { useLibraryImportStore } = await import('@/stores/libraryImport')
    const store = useLibraryImportStore()

    store.items = {
      'C:\\incoming\\Part 1.mp3': {
        id: 'C:\\incoming\\Part 1.mp3',
        fullPath: 'C:\\incoming\\Part 1.mp3',
        sourceFiles: [
          'C:\\incoming\\Part 1.mp3',
          'C:\\incoming\\Part 2.mp3',
          'C:\\incoming\\Part 10.mp3',
        ],
        folderPath: 'C:\\incoming',
        relativePath: 'Ordered Book',
        folderName: 'Ordered Book',
        format: 'MP3',
        fileCount: 3,
        selectedMatch: {
          title: 'Ordered Book',
          authors: [],
        } as unknown as SearchResult,
        matchState: 'matched',
        matchIssue: 'none',
        candidates: [],
        hasSearched: true,
        isSearching: false,
        selected: true,
      },
    }

    store.action = 'move'
    startManualImport.mockResolvedValueOnce({
      importedCount: 3,
      totalCount: 3,
      results: [
        {
          success: true,
          sourcePath: 'C:\\incoming\\Part 1.mp3',
          destinationPath: 'D:\\library\\Ordered Book\\Part 1.mp3',
          warning: 'The source file was retained because durable identity is unavailable.',
        },
      ],
    })

    const result = await store.importSelected('D:\\library')

    expect(addToLibrary).toHaveBeenCalledTimes(1)
    expect(startManualImport).toHaveBeenCalledTimes(1)
    expect(startManualImport).toHaveBeenCalledWith({
      path: 'C:\\incoming',
      mode: 'interactive',
      action: 'move',
      includeCompanionFiles: true,
      cleanupEmptySourceFolders: true,
      items: [
        { fullPath: 'C:\\incoming\\Part 1.mp3', matchedAudiobookId: 42 },
        { fullPath: 'C:\\incoming\\Part 2.mp3', matchedAudiobookId: 42 },
        { fullPath: 'C:\\incoming\\Part 10.mp3', matchedAudiobookId: 42 },
      ],
    })
    expect(result.warnings).toEqual([
      'The source file was retained because durable identity is unavailable.',
    ])
  })

  it('registers files in place using the discovered book folder and backend success result', async () => {
    const { useLibraryImportStore } = await import('@/stores/libraryImport')
    const store = useLibraryImportStore()

    store.items = {
      '/audiobooks/Author/Book/Book.m4b': {
        id: '/audiobooks/Author/Book/Book.m4b',
        fullPath: '/audiobooks/Author/Book/Book.m4b',
        sourceFiles: ['/audiobooks/Author/Book/Book.m4b'],
        folderPath: '/audiobooks/Author/Book',
        relativePath: 'Author/Book',
        folderName: 'Book',
        format: 'M4B',
        fileCount: 1,
        selectedMatch: {
          title: 'Book',
          authors: [{ name: 'Author' }],
        } as unknown as SearchResult,
        matchState: 'matched',
        matchIssue: 'none',
        candidates: [],
        hasSearched: true,
        isSearching: false,
        selected: true,
      },
    }
    store.action = 'none'
    startManualImport.mockResolvedValueOnce({
      importedCount: 1,
      totalCount: 1,
      results: [
        {
          success: true,
          sourcePath: '/audiobooks/Author/Book/Book.m4b',
          destinationPath: '/audiobooks/Author/Book/Book.m4b',
        },
      ],
    })

    const result = await store.importSelected('')

    expect(addToLibrary).toHaveBeenCalledWith(
      expect.any(Object),
      expect.objectContaining({
        destinationPath: '/audiobooks/Author/Book',
      }),
    )
    expect(startManualImport).toHaveBeenCalledWith({
      path: '/audiobooks/Author/Book',
      mode: 'interactive',
      action: 'none',
      includeCompanionFiles: false,
      cleanupEmptySourceFolders: false,
      items: [
        {
          fullPath: '/audiobooks/Author/Book/Book.m4b',
          matchedAudiobookId: 42,
        },
      ],
    })
    expect(result).toEqual({ imported: 1, errors: [], warnings: [] })
    expect(store.itemList).toHaveLength(0)
  })

  it('keeps the library import item when the backend skips in-place registration', async () => {
    const { useLibraryImportStore } = await import('@/stores/libraryImport')
    const store = useLibraryImportStore()

    store.items = {
      '/audiobooks/Author/Book/Book.m4b': {
        id: '/audiobooks/Author/Book/Book.m4b',
        fullPath: '/audiobooks/Author/Book/Book.m4b',
        sourceFiles: ['/audiobooks/Author/Book/Book.m4b'],
        folderPath: '/audiobooks/Author/Book',
        relativePath: 'Author/Book',
        folderName: 'Book',
        format: 'M4B',
        fileCount: 1,
        selectedMatch: {
          title: 'Book',
          authors: [{ name: 'Author' }],
        } as unknown as SearchResult,
        matchState: 'matched',
        matchIssue: 'none',
        candidates: [],
        hasSearched: true,
        isSearching: false,
        selected: true,
      },
    }
    store.action = 'none'
    startManualImport.mockResolvedValueOnce({
      importedCount: 0,
      totalCount: 1,
      results: [
        {
          success: false,
          skipped: true,
          skipReason: 'The existing file could not be registered safely in place.',
        },
      ],
    })

    const result = await store.importSelected('')

    expect(result.imported).toBe(0)
    expect(result.errors).toEqual([
      'Book: The existing file could not be registered safely in place.',
    ])
    expect(store.itemList).toHaveLength(1)
  })

  it('keeps a multi-file book selected when only part of the backend registration succeeds', async () => {
    const { useLibraryImportStore } = await import('@/stores/libraryImport')
    const store = useLibraryImportStore()

    store.items = {
      '/audiobooks/Author/Book/Part 1.m4b': {
        id: '/audiobooks/Author/Book/Part 1.m4b',
        fullPath: '/audiobooks/Author/Book/Part 1.m4b',
        sourceFiles: ['/audiobooks/Author/Book/Part 1.m4b', '/audiobooks/Author/Book/Part 2.m4b'],
        folderPath: '/audiobooks/Author/Book',
        relativePath: 'Author/Book',
        folderName: 'Book',
        format: 'M4B',
        fileCount: 2,
        selectedMatch: {
          title: 'Book',
          authors: [{ name: 'Author' }],
        } as unknown as SearchResult,
        matchState: 'matched',
        matchIssue: 'none',
        candidates: [],
        hasSearched: true,
        isSearching: false,
        selected: true,
      },
    }
    store.action = 'none'
    startManualImport.mockResolvedValueOnce({
      importedCount: 1,
      totalCount: 2,
      results: [
        { success: true, sourcePath: '/audiobooks/Author/Book/Part 1.m4b' },
        {
          success: false,
          sourcePath: '/audiobooks/Author/Book/Part 2.m4b',
          error: 'The existing file could not be registered safely in place.',
        },
      ],
    })

    const result = await store.importSelected('')

    expect(result.imported).toBe(0)
    expect(result.errors).toHaveLength(1)
    expect(store.itemList).toHaveLength(1)
    expect(store.itemList[0]?.selected).toBe(true)
  })

  it('does not rewrite an existing audiobook BasePath for in-place registration', async () => {
    const { useLibraryImportStore } = await import('@/stores/libraryImport')
    const store = useLibraryImportStore()

    store.items = {
      '/audiobooks/Author/Book/Book.m4b': {
        id: '/audiobooks/Author/Book/Book.m4b',
        fullPath: '/audiobooks/Author/Book/Book.m4b',
        sourceFiles: ['/audiobooks/Author/Book/Book.m4b'],
        folderPath: '/audiobooks/Author/Book',
        relativePath: 'Author/Book',
        folderName: 'Book',
        format: 'M4B',
        fileCount: 1,
        selectedMatch: {
          title: 'Book',
          authors: [{ name: 'Author' }],
        } as unknown as SearchResult,
        matchState: 'matched',
        matchIssue: 'none',
        candidates: [],
        hasSearched: true,
        isSearching: false,
        selected: true,
      },
    }
    store.action = 'none'
    addToLibrary.mockRejectedValueOnce({
      status: 409,
      body: { audiobook: { id: 77 } },
    })
    startManualImport.mockResolvedValueOnce({
      importedCount: 1,
      totalCount: 1,
      results: [{ success: true }],
    })

    const result = await store.importSelected('')

    expect(updateAudiobook).not.toHaveBeenCalled()
    expect(startManualImport).toHaveBeenCalledWith(
      expect.objectContaining({
        action: 'none',
        items: [
          {
            fullPath: '/audiobooks/Author/Book/Book.m4b',
            matchedAudiobookId: 77,
          },
        ],
      }),
    )
    expect(result.imported).toBe(1)
  })

  it('ignores foreign scan completions until its own job id is assigned', async () => {
    const { useLibraryImportStore } = await import('@/stores/libraryImport')
    const store = useLibraryImportStore()

    scanUnmatchedFiles.mockImplementation(async () => {
      await unmatchedScanHandler?.({ jobId: 'foreign-job' })
      return { jobId: 'own-job' }
    })
    getUnmatchedResults.mockImplementation(async (jobId: string) => {
      expect(jobId).toBe('own-job')
      return {
        status: 'Completed',
        error: null,
        items: [
          {
            fullPath: 'C:\\incoming\\Book A.mp3',
            sourceFiles: ['C:\\incoming\\Book A.mp3'],
            bookFolder: 'C:\\incoming',
            relativePath: 'Book A',
            title: 'Book A',
            author: 'Author A',
            series: null,
            asin: null,
            format: 'MP3',
            fileCount: 1,
          },
        ],
      }
    })

    await store.triggerScan(7)

    expect(getUnmatchedResults).not.toHaveBeenCalledWith('foreign-job')
    expect(getUnmatchedResults).toHaveBeenCalledWith('own-job')
    expect(Object.keys(store.items)).toEqual(['C:\\incoming\\Book A.mp3'])
    expect(store.scanStatus).toBe('done')
  })

  it('prefers detected title and author for automatic matching before folder fallback', async () => {
    const { useLibraryImportStore } = await import('@/stores/libraryImport')
    const store = useLibraryImportStore()

    advancedSearch.mockResolvedValue([
      {
        title: 'Jack of Shadows',
        authors: [{ name: 'Roger Zelazny' }],
        matchScore: 0.95,
      },
    ])

    store.items = {
      'C:\\incoming\\Chapter 01.mp3': {
        id: 'C:\\incoming\\Chapter 01.mp3',
        fullPath: 'C:\\incoming\\Chapter 01.mp3',
        sourceFiles: ['C:\\incoming\\Chapter 01.mp3'],
        folderPath: 'C:\\incoming',
        relativePath: 'test-import',
        folderName: 'test-import',
        detectedTitle: 'Jack of Shadows',
        detectedAuthor: 'Roger Zelazny',
        format: 'MP3',
        fileCount: 1,
        selectedMatch: null,
        matchState: 'unsearched',
        matchIssue: 'none',
        candidates: [],
        hasSearched: false,
        isSearching: false,
        selected: false,
      },
    }

    store.startProcessing()
    await new Promise((resolve) => setTimeout(resolve, 0))

    // cap is gone: it used to reach the Audible author page collector as a page size.
    expect(advancedSearch).toHaveBeenCalledWith({
      title: 'Jack of Shadows',
      author: 'Roger Zelazny',
    })
    const row = store.items['C:\\incoming\\Chapter 01.mp3']
    expect(row?.selectedMatch?.title).toBe('Jack of Shadows')
    expect(row?.matchState).toBe('matched')
    expect(row?.selected).toBe(true)
  })

  function unsearchedRow(overrides: Record<string, unknown> = {}) {
    return {
      id: 'C:\\incoming\\Chapter 01.mp3',
      fullPath: 'C:\\incoming\\Chapter 01.mp3',
      sourceFiles: ['C:\\incoming\\Chapter 01.mp3'],
      folderPath: 'C:\\incoming',
      relativePath: 'test-import',
      folderName: 'test-import',
      detectedTitle: 'Chapter 1',
      detectedAuthor: 'Michael Kramer',
      format: 'MP3',
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

  it('leaves a low-confidence row unticked and keeps its candidates', async () => {
    const { useLibraryImportStore } = await import('@/stores/libraryImport')
    const store = useLibraryImportStore()

    advancedSearch.mockResolvedValue([
      { title: 'The Way of Kings', authors: [{ name: 'Brandon Sanderson' }], matchScore: 0.31 },
      { title: 'Words of Radiance', authors: [{ name: 'Brandon Sanderson' }], matchScore: 0.12 },
    ])

    store.items = { 'C:\\incoming\\Chapter 01.mp3': unsearchedRow() as never }

    store.startProcessing()
    await new Promise((resolve) => setTimeout(resolve, 0))

    const row = store.items['C:\\incoming\\Chapter 01.mp3']
    expect(row?.matchState).toBe('needs-review')
    expect(row?.matchIssue).toBe('low-confidence')
    expect(row?.selected).toBe(false)
    expect(row?.candidates).toHaveLength(2)
    expect(row?.selectedMatch?.title).toBe('The Way of Kings')
  })

  it('auto-matches a confident result and ticks it', async () => {
    const { useLibraryImportStore } = await import('@/stores/libraryImport')
    const store = useLibraryImportStore()

    advancedSearch.mockResolvedValue([
      { asin: 'B004SOK2SE', title: 'Mistborn', matchScore: 0.96, lengthMinutes: 1499 },
      { asin: 'B07F88TSBT', title: 'Mistborn: Secret History', matchScore: 0.46, lengthMinutes: 329 },
    ])

    store.items = {
      'C:\\incoming\\Chapter 01.mp3': unsearchedRow({
        detectedTitle: 'Mistborn, The Final Empire',
        detectedAuthor: 'Brandon Sanderson',
        durationSeconds: 1499 * 60,
      }) as never,
    }

    store.startProcessing()
    await new Promise((resolve) => setTimeout(resolve, 0))

    expect(advancedSearch).toHaveBeenCalledWith({
      title: 'Mistborn, The Final Empire',
      author: 'Brandon Sanderson',
      durationSeconds: 1499 * 60,
    })
    const row = store.items['C:\\incoming\\Chapter 01.mp3']
    expect(row?.matchState).toBe('matched')
    expect(row?.selected).toBe(true)
    expect(row?.selectedMatch?.asin).toBe('B004SOK2SE')
  })

  it('does not auto-select an ASIN search that came back under a different ASIN', async () => {
    const { useLibraryImportStore } = await import('@/stores/libraryImport')
    const store = useLibraryImportStore()

    advancedSearch.mockResolvedValue([
      { asin: 'B000DIFFERENT', title: 'Something Else', matchScore: 0.99 },
    ])

    store.items = {
      'C:\\incoming\\Chapter 01.mp3': unsearchedRow({ detectedAsin: 'B004SOK2SE' }) as never,
    }

    store.startProcessing()
    await new Promise((resolve) => setTimeout(resolve, 0))

    expect(advancedSearch).toHaveBeenCalledWith({ asin: 'B004SOK2SE' })
    const row = store.items['C:\\incoming\\Chapter 01.mp3']
    expect(row?.matchState).toBe('needs-review')
    expect(row?.selected).toBe(false)
  })

  it('refuses two near-identical scores as an automatic match', async () => {
    const { useLibraryImportStore } = await import('@/stores/libraryImport')
    const store = useLibraryImportStore()

    advancedSearch.mockResolvedValue([
      { title: 'Mistborn', matchScore: 0.82 },
      { title: 'Mistborn: Secret History', matchScore: 0.79 },
    ])

    store.items = { 'C:\\incoming\\Chapter 01.mp3': unsearchedRow() as never }

    store.startProcessing()
    await new Promise((resolve) => setTimeout(resolve, 0))

    const row = store.items['C:\\incoming\\Chapter 01.mp3']
    expect(row?.matchState).toBe('needs-review')
    expect(row?.matchIssue).toBe('ambiguous')
    expect(row?.selected).toBe(false)
  })

  it('leaves a rate-limited row unsearched and pauses the queue', async () => {
    const { useLibraryImportStore } = await import('@/stores/libraryImport')
    const store = useLibraryImportStore()

    advancedSearch.mockRejectedValue(Object.assign(new Error('Rate limited'), {
      status: 429,
      retryAfter: 30,
    }))

    store.items = {
      'C:\\incoming\\Chapter 01.mp3': unsearchedRow() as never,
      'C:\\incoming\\Chapter 02.mp3': unsearchedRow({
        id: 'C:\\incoming\\Chapter 02.mp3',
        fullPath: 'C:\\incoming\\Chapter 02.mp3',
      }) as never,
    }

    store.startProcessing()
    await new Promise((resolve) => setTimeout(resolve, 0))

    const row = store.items['C:\\incoming\\Chapter 01.mp3']
    expect(row?.matchState).toBe('unsearched')
    expect(row?.hasSearched).toBe(false)
    expect(row?.matchIssue).toBe('rate-limited')
    expect(store.isProcessing).toBe(false)
    expect(store.queuePausedReason).toContain('rate limited')
    // The second row was never attempted, so the limit is not burned through twice over.
    expect(advancedSearch).toHaveBeenCalledTimes(1)
  })

  it('retries only unmatched and needs-review rows with the folder-name strategy', async () => {
    const { useLibraryImportStore } = await import('@/stores/libraryImport')
    const store = useLibraryImportStore()

    advancedSearch.mockResolvedValue([])

    store.items = {
      matched: unsearchedRow({
        id: 'matched',
        fullPath: 'C:\\incoming\\Matched.mp3',
        folderName: 'Matched Folder',
        matchState: 'matched',
        hasSearched: true,
        selected: true,
        selectedMatch: { title: 'Matched' },
      }) as never,
      review: unsearchedRow({
        id: 'review',
        fullPath: 'C:\\incoming\\Review.mp3',
        folderName: 'Review Folder',
        matchState: 'needs-review',
        hasSearched: true,
        selectedMatch: { title: 'Maybe' },
      }) as never,
      missing: unsearchedRow({
        id: 'missing',
        fullPath: 'C:\\incoming\\Missing.mp3',
        folderName: 'Missing Folder',
        matchState: 'unmatched',
        hasSearched: true,
      }) as never,
    }

    store.retryUnmatched('folder')
    await new Promise((resolve) => setTimeout(resolve, 0))

    expect(advancedSearch).toHaveBeenCalledTimes(2)
    expect(advancedSearch).toHaveBeenCalledWith({ title: 'Review Folder' })
    expect(advancedSearch).toHaveBeenCalledWith({ title: 'Missing Folder' })
    expect(advancedSearch).not.toHaveBeenCalledWith({ title: 'Matched Folder' })
  })

  it('select all ticks only rows the scorer was confident about', async () => {
    const { useLibraryImportStore } = await import('@/stores/libraryImport')
    const store = useLibraryImportStore()

    store.items = {
      matched: unsearchedRow({
        id: 'matched',
        matchState: 'matched',
        hasSearched: true,
        selectedMatch: { title: 'Matched' },
      }) as never,
      review: unsearchedRow({
        id: 'review',
        matchState: 'needs-review',
        hasSearched: true,
        selectedMatch: { title: 'Maybe' },
      }) as never,
    }

    store.toggleSelectAll()

    expect(store.items['matched']?.selected).toBe(true)
    expect(store.items['review']?.selected).toBe(false)
    expect(store.selectedCount).toBe(1)
  })

  it('keeps runtime in minutes and sends isbn as an array', async () => {
    const { useLibraryImportStore } = await import('@/stores/libraryImport')
    const store = useLibraryImportStore()

    store.items = {
      'C:\\incoming\\Book.m4b': unsearchedRow({
        id: 'C:\\incoming\\Book.m4b',
        fullPath: 'C:\\incoming\\Book.m4b',
        sourceFiles: ['C:\\incoming\\Book.m4b'],
        matchState: 'matched',
        hasSearched: true,
        selected: true,
        selectedMatch: {
          title: 'Mistborn',
          asin: 'B004SOK2SE',
          lengthMinutes: 600,
          isbn: '9780575089914',
          series: [
            { name: 'The Mistborn Saga', position: '1', asin: 'B0S1' },
            { name: 'The Cosmere', position: '', asin: 'B0S2' },
          ],
        },
      }) as never,
    }
    store.action = 'none'
    startManualImport.mockResolvedValueOnce({
      importedCount: 1,
      totalCount: 1,
      results: [{ success: true }],
    })

    await store.importSelected('')

    const [metadata, options] = addToLibrary.mock.calls[0] as [
      Record<string, unknown>,
      { searchResult?: Record<string, unknown> },
    ]
    expect(metadata.runtime).toBe(600)
    expect(metadata.seriesMemberships).toEqual([
      { seriesName: 'The Mistborn Saga', seriesNumber: '1', seriesAsin: 'B0S1', isPrimary: true, sortOrder: 0 },
      { seriesName: 'The Cosmere', seriesNumber: '', seriesAsin: 'B0S2', isPrimary: false, sortOrder: 1 },
    ])
    expect(options.searchResult?.isbn).toEqual(['9780575089914'])
  })

  it('refuses to import a row that was never confirmed', async () => {
    const { useLibraryImportStore } = await import('@/stores/libraryImport')
    const store = useLibraryImportStore()

    store.items = {
      review: unsearchedRow({
        id: 'review',
        matchState: 'needs-review',
        hasSearched: true,
        selected: true,
        selectedMatch: { title: 'Maybe' },
      }) as never,
    }

    const result = await store.importSelected('')

    expect(result.imported).toBe(0)
    expect(addToLibrary).not.toHaveBeenCalled()
  })
})
