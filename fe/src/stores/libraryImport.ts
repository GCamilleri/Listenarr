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
import { defineStore } from 'pinia'
import { ref, computed } from 'vue'
import { apiService } from '@/services/api'
import { signalRService } from '@/services/signalr'
import { logger } from '@/utils/logger'
import { buildLibraryImportSearchParams, looksLikeAsin } from '@/utils/libraryImportSearch'
import type { LibraryImportSearchStrategy } from '@/utils/libraryImportSearch'
import type {
  SearchResult,
  AudibleBookMetadata,
  AudiobookSeriesMembership,
  UnmatchedFileItem,
  UnmatchedScanDiagnostics,
} from '@/types'

/** Best score at or above this is auto-selected and ticked. */
export const AUTO_MATCH_THRESHOLD = 0.75

/** Two candidates closer together than this are too close to call. */
export const AMBIGUITY_MARGIN = 0.1

/** How many candidates a row keeps so the table can offer them inline. */
export const MAX_ROW_CANDIDATES = 5

export type LibraryImportMatchState = 'unsearched' | 'matched' | 'needs-review' | 'unmatched'

export type LibraryImportMatchIssue =
  | 'none'
  | 'no-results'
  | 'low-confidence'
  | 'ambiguous'
  | 'rate-limited'
  | 'search-failed'

export interface LibraryImportItem {
  id: string // = fullPath (unique key)
  fullPath: string // path to audio file
  sourceFiles: string[] // all source files represented by this row
  folderPath: string // bookFolder (parent directory)
  relativePath: string // relative to root folder
  folderName: string // search term: last non-empty path segment
  // Detected from file scan
  detectedTitle?: string
  detectedAuthor?: string
  detectedAsin?: string
  detectedSeries?: string
  format: string
  fileCount: number
  durationSeconds?: number
  // Match state
  selectedMatch: SearchResult | null
  matchState: LibraryImportMatchState
  matchIssue: LibraryImportMatchIssue
  candidates: SearchResult[]
  matchedByStrategy?: LibraryImportSearchStrategy
  hasSearched: boolean // true once auto-search was attempted
  isSearching: boolean // currently in-flight
  // Selection
  selected: boolean
}

function extractFolderName(relativePath: string): string {
  const parts = relativePath.replace(/\\/g, '/').split('/').filter(Boolean)
  // Prefer the last meaningful segment (author/title structure)
  return parts[parts.length - 1] ?? relativePath
}

export interface LibraryImportMatchOutcome {
  matchState: LibraryImportMatchState
  matchIssue: LibraryImportMatchIssue
  selectedMatch: SearchResult | null
  candidates: SearchResult[]
  selected: boolean
}

function scoreOf(result: SearchResult): number {
  return typeof result.matchScore === 'number' ? result.matchScore : -1
}

/**
 * Turn a list of candidates into one of three outcomes. The backend does the scoring; this only
 * decides whether the best candidate is good enough to tick without a human looking at it.
 */
export function classifyMatch(
  results: SearchResult[],
  options: { detectedAsin?: string } = {},
): LibraryImportMatchOutcome {
  const ranked = [...results].sort((a, b) => scoreOf(b) - scoreOf(a))
  const candidates = ranked.slice(0, MAX_ROW_CANDIDATES)
  const best = ranked[0] ?? null

  if (!best) {
    return {
      matchState: 'unmatched',
      matchIssue: 'no-results',
      selectedMatch: null,
      candidates: [],
      selected: false,
    }
  }

  const detectedAsin = options.detectedAsin?.trim()
  if (detectedAsin) {
    // An ASIN is an exact identifier. Anything that came back under a different one is not the
    // book that was asked for, whatever else it looks like.
    const exact = ranked.find((r) => r.asin?.toLowerCase() === detectedAsin.toLowerCase())
    if (exact) {
      return {
        matchState: 'matched',
        matchIssue: 'none',
        selectedMatch: exact,
        candidates,
        selected: true,
      }
    }
    return {
      matchState: 'needs-review',
      matchIssue: 'low-confidence',
      selectedMatch: best,
      candidates,
      selected: false,
    }
  }

  const bestScore = scoreOf(best)
  const runnerUp = ranked[1]
  const tooClose = !!runnerUp && bestScore - scoreOf(runnerUp) < AMBIGUITY_MARGIN

  if (bestScore >= AUTO_MATCH_THRESHOLD && !tooClose) {
    return {
      matchState: 'matched',
      matchIssue: 'none',
      selectedMatch: best,
      candidates,
      selected: true,
    }
  }

  return {
    matchState: 'needs-review',
    matchIssue: tooClose && bestScore >= AUTO_MATCH_THRESHOLD ? 'ambiguous' : 'low-confidence',
    selectedMatch: best,
    candidates,
    selected: false,
  }
}

// The backend binds SearchResult.Isbn as List<string>. Audible sends a bare string.
function normalizeIsbn(isbn: unknown): string[] | undefined {
  if (isbn == null) return undefined
  if (Array.isArray(isbn)) return isbn.filter((v): v is string => typeof v === 'string' && !!v)
  if (typeof isbn === 'string') return isbn.trim() ? [isbn.trim()] : []
  return undefined
}

function normalizeGenres(genres: unknown): string[] | undefined {
  if (!Array.isArray(genres)) return genres as string[] | undefined
  return genres
    .map((g) => (typeof g === 'string' ? g : ((g as { name?: string })?.name ?? '')))
    .filter(Boolean)
}

function unmatchedToImportItem(item: UnmatchedFileItem): LibraryImportItem {
  return {
    id: item.fullPath,
    fullPath: item.fullPath,
    sourceFiles:
      item.sourceFiles && item.sourceFiles.length > 0 ? item.sourceFiles : [item.fullPath],
    folderPath: item.bookFolder,
    relativePath: item.relativePath,
    folderName: extractFolderName(item.relativePath),
    detectedTitle: item.title,
    detectedAuthor: item.author,
    detectedAsin: item.asin,
    detectedSeries: item.series,
    format: item.format,
    fileCount: item.fileCount,
    durationSeconds: item.durationSeconds,
    selectedMatch: null,
    matchState: 'unsearched',
    matchIssue: 'none',
    candidates: [],
    hasSearched: false,
    isSearching: false,
    selected: false,
  }
}

function matchToMetadata(result: SearchResult): AudibleBookMetadata {
  const authors: string[] =
    result.authors && result.authors.length > 0
      ? result.authors.map((a) => a.name ?? '').filter(Boolean)
      : []

  // series may come back as AudibleSeries[] from the search endpoint
  const seriesRaw = result.series as unknown
  const seriesEntries = Array.isArray(seriesRaw)
    ? (seriesRaw as Array<{ name?: string; asin?: string; position?: string }>)
    : []
  const seriesItem = seriesEntries[0] ?? null
  const series = seriesItem?.name ?? (typeof seriesRaw === 'string' ? seriesRaw : undefined)
  const seriesNumber = seriesItem?.position ?? result.seriesNumber
  const seriesAsin = seriesItem?.asin ?? result.seriesAsin

  // Keep every series the book belongs to, not just the first. The backend accepts and applies
  // seriesMemberships; flattening to one entry silently drops the rest.
  const seriesMemberships: AudiobookSeriesMembership[] = seriesEntries
    .filter((entry) => !!entry?.name)
    .map((entry, index) => ({
      seriesName: entry.name!,
      seriesNumber: entry.position,
      seriesAsin: entry.asin,
      isPrimary: index === 0,
      sortOrder: index,
    }))

  return {
    title: result.title ?? '',
    asin: result.asin ?? '',
    authors,
    subtitle: result.subtitle,
    series,
    seriesNumber,
    seriesAsin,
    ...(seriesMemberships.length > 0 ? { seriesMemberships } : {}),
    description: result.description,
    publisher: result.publisher,
    language: result.language,
    // Runtime is minutes on the backend (Audiobook.Runtime, MetadataConverters), and
    // lengthMinutes is already minutes. Do not convert.
    runtime: result.runtime ?? result.lengthMinutes,
    imageUrl: result.imageUrl,
    // SearchResult.genres comes as objects {asin, name, type} from Audible;
    // AudibleBookMetadata.genres expects string[] (genre names only)
    genres: normalizeGenres(result.genres),
    narrators: result.narrators?.map((n) => n.name ?? '').filter(Boolean),
    publishYear: result.releaseDate?.substring(0, 4) ?? result.publishDate?.substring(0, 4),
    metadataSource: result.metadataSource,
  }
}

export const useLibraryImportStore = defineStore('libraryImport', () => {
  const items = ref<Record<string, LibraryImportItem>>({})
  const lookupQueue = ref<string[]>([])
  const isProcessing = ref(false)
  const rootFolderId = ref<number | null>(null)
  const scanStatus = ref<'idle' | 'scanning' | 'done' | 'error'>('idle')
  const scanError = ref<string | null>(null)
  const scanDiagnostics = ref<UnmatchedScanDiagnostics | null>(null)
  // Set when the scan completed but could not read some or all embedded tags, so the
  // UI can say why rows have no title or author instead of looking simply wrong.
  const scanWarning = computed(() => scanDiagnostics.value?.message ?? null)
  const lastScannedAt = ref<string | null>(null)
  const action = ref<'none' | 'move' | 'hardlink/copy'>('none')
  const monitor = ref<'none' | 'all'>('all')
  const metadataFetchCount = ref(0)
  const importErrors = ref<string[]>([])
  const queuePausedReason = ref<string | null>(null)
  const retryStrategy = ref<LibraryImportSearchStrategy>('title-author')

  // ─── Computed ────────────────────────────────────────────────────────────────

  const itemList = computed(() => Object.values(items.value))
  const selectedCount = computed(() => itemList.value.filter((i) => i.selected).length)
  const hasUnprocessedItems = computed(() => itemList.value.some((i) => !i.hasSearched))
  const processedCount = computed(() => itemList.value.filter((i) => i.hasSearched).length)
  const matchedCount = computed(
    () => itemList.value.filter((i) => i.matchState === 'matched').length,
  )
  const needsReviewCount = computed(
    () => itemList.value.filter((i) => i.matchState === 'needs-review').length,
  )
  const unmatchedCount = computed(
    () => itemList.value.filter((i) => i.matchState === 'unmatched').length,
  )
  const retryableCount = computed(
    () =>
      itemList.value.filter((i) => i.matchState === 'unmatched' || i.matchState === 'needs-review')
        .length,
  )

  // ─── Scan ─────────────────────────────────────────────────────────────────

  async function initFromRootFolder(id: number) {
    rootFolderId.value = id
    scanStatus.value = 'idle'
    try {
      const saved = await apiService.getSavedUnmatchedFiles(id)
      if (saved.lastScannedAt) lastScannedAt.value = saved.lastScannedAt
      scanDiagnostics.value = saved.diagnostics ?? null
      const persisted = _loadPersistedMatches(id)
      const newItems: Record<string, LibraryImportItem> = {}
      for (const item of saved.items) {
        const existing = items.value[item.fullPath]
        const p = persisted[item.fullPath]
        if (existing) {
          newItems[item.fullPath] = {
            ...unmatchedToImportItem(item),
            ...restoreMatchState(existing),
            isSearching: false,
          }
        } else if (p) {
          newItems[item.fullPath] = {
            ...unmatchedToImportItem(item),
            ...restoreMatchState(p),
            isSearching: false,
          }
        } else {
          newItems[item.fullPath] = unmatchedToImportItem(item)
        }
      }
      items.value = newItems
      if (saved.items.length > 0) scanStatus.value = 'done'
    } catch (e) {
      logger.debug('[libraryImport] Failed to load saved results:', e)
    }
  }

  async function triggerScan(id: number) {
    rootFolderId.value = id
    scanStatus.value = 'scanning'
    scanError.value = null
    scanDiagnostics.value = null
    try {
      localStorage.removeItem(_storageKey(id))
    } catch {
      /* non-fatal */
    }

    let jobId = ''
    let settled = false
    let offSignalR: (() => void) | null = null
    let pollInterval: ReturnType<typeof setInterval> | null = null

    function cleanUp() {
      offSignalR?.()
      if (pollInterval) {
        clearInterval(pollInterval)
        pollInterval = null
      }
    }

    async function onComplete(completedJobId: string) {
      if (settled) return
      settled = true
      cleanUp()
      try {
        const response = await apiService.getUnmatchedResults(completedJobId)
        scanDiagnostics.value = response.diagnostics ?? null
        _populateFromItems(response.items)
        _persistMatches()
        lastScannedAt.value = new Date().toISOString()
        scanStatus.value = 'done'
      } catch (e) {
        scanStatus.value = 'error'
        scanError.value = (e as Error)?.message ?? 'Failed to fetch results'
      }
    }

    function onFailed(error?: string) {
      if (settled) return
      settled = true
      cleanUp()
      scanStatus.value = 'error'
      scanError.value = error ?? 'Scan failed'
    }

    // Register before POST — if scan is fast, SignalR may fire before scanUnmatchedFiles() returns.
    // Allow the event if jobId is not yet assigned (jobId === '') — it must be ours.
    offSignalR = signalRService.onUnmatchedScanComplete(async (payload) => {
      if (!jobId || payload.jobId !== jobId) return
      if (payload.error) {
        onFailed(payload.error)
        return
      }
      await onComplete(payload.jobId)
    })

    try {
      const result = await apiService.scanUnmatchedFiles(id)
      jobId = result.jobId
      if (settled) return // SignalR already handled it before this line
      // Poll once immediately — handles fast scans
      const check = await apiService.getUnmatchedResults(jobId)
      if (check.status === 'Completed') {
        await onComplete(jobId)
      } else if (check.status === 'Failed') {
        onFailed(check.error)
      } else {
        // Polling fallback — keeps checking every 2.5s if SignalR is unavailable or slow
        pollInterval = setInterval(async () => {
          if (settled || !jobId) return
          try {
            const poll = await apiService.getUnmatchedResults(jobId)
            if (poll.status === 'Completed') await onComplete(jobId)
            else if (poll.status === 'Failed') onFailed(poll.error)
          } catch {
            /* ignore transient errors, keep polling */
          }
        }, 2500)
      }
    } catch (e) {
      onFailed((e as Error)?.message ?? 'Failed to start scan')
    }
  }

  // Entries written before match states existed only carry selectedMatch/hasSearched, so infer
  // a state rather than leaving every restored row unsearched.
  function restoreMatchState(entry: {
    selectedMatch: SearchResult | null
    matchState?: LibraryImportMatchState
    matchIssue?: LibraryImportMatchIssue
    candidates?: SearchResult[]
    matchedByStrategy?: LibraryImportSearchStrategy
    hasSearched: boolean
    selected: boolean
  }): Pick<
    LibraryImportItem,
    | 'selectedMatch'
    | 'matchState'
    | 'matchIssue'
    | 'candidates'
    | 'matchedByStrategy'
    | 'hasSearched'
    | 'selected'
  > {
    const inferred: LibraryImportMatchState = entry.matchState
      ? entry.matchState
      : !entry.hasSearched
        ? 'unsearched'
        : entry.selectedMatch
          ? 'needs-review'
          : 'unmatched'
    return {
      selectedMatch: entry.selectedMatch,
      matchState: inferred,
      matchIssue: entry.matchIssue ?? 'none',
      candidates: entry.candidates ?? [],
      matchedByStrategy: entry.matchedByStrategy,
      hasSearched: entry.hasSearched,
      selected: inferred === 'matched' ? entry.selected : false,
    }
  }

  function _populateFromItems(scanItems: UnmatchedFileItem[]) {
    const newItems: Record<string, LibraryImportItem> = {}
    for (const item of scanItems) {
      const existing = items.value[item.fullPath]
      const fresh = unmatchedToImportItem(item)
      newItems[item.fullPath] = existing
        ? {
            ...fresh,
            ...restoreMatchState(existing),
          }
        : fresh
    }
    items.value = newItems
  }

  // ─── localStorage persistence ──────────────────────────────────────────────

  type PersistedEntry = {
    selectedMatch: SearchResult | null
    matchState?: LibraryImportMatchState
    matchIssue?: LibraryImportMatchIssue
    candidates?: SearchResult[]
    matchedByStrategy?: LibraryImportSearchStrategy
    hasSearched: boolean
    selected: boolean
  }

  function _storageKey(folderId: number): string {
    return `listenarr-import-matches-${folderId}`
  }

  function _persistMatches(): void {
    if (!rootFolderId.value) return
    const state: Record<string, PersistedEntry> = {}
    for (const [path, item] of Object.entries(items.value)) {
      state[path] = {
        selectedMatch: item.selectedMatch,
        matchState: item.matchState,
        matchIssue: item.matchIssue,
        candidates: item.candidates,
        matchedByStrategy: item.matchedByStrategy,
        hasSearched: item.hasSearched,
        selected: item.selected,
      }
    }
    try {
      localStorage.setItem(_storageKey(rootFolderId.value), JSON.stringify(state))
    } catch {
      /* non-fatal */
    }
  }

  function _loadPersistedMatches(folderId: number): Record<string, PersistedEntry> {
    try {
      const raw = localStorage.getItem(_storageKey(folderId))
      if (!raw) return {}
      return JSON.parse(raw) as Record<string, PersistedEntry>
    } catch {
      return {}
    }
  }

  // ─── Queue Processing ─────────────────────────────────────────────────────

  function startProcessing() {
    const unsearched = itemList.value.filter((i) => !i.hasSearched).map((i) => i.id)
    if (unsearched.length === 0) return
    queuePausedReason.value = null
    retryStrategy.value = 'title-author'
    lookupQueue.value = unsearched
    isProcessing.value = true
    metadataFetchCount.value = 0
    processNext()
  }

  /**
   * Re-run every doubtful and unmatched row with a different way of building the query. Rows that
   * already matched are left alone.
   */
  function retryUnmatched(strategy: LibraryImportSearchStrategy) {
    const retryable = itemList.value
      .filter((i) => i.matchState === 'unmatched' || i.matchState === 'needs-review')
      .map((i) => i.id)
    if (retryable.length === 0) return
    queuePausedReason.value = null
    retryStrategy.value = strategy
    lookupQueue.value = retryable
    isProcessing.value = true
    processNext()
  }

  function stopProcessing() {
    lookupQueue.value = []
    isProcessing.value = false
    queuePausedReason.value = null
    // Clear any in-flight isSearching flags
    for (const id of Object.keys(items.value)) {
      const entry = items.value[id]
      if (entry?.isSearching) {
        items.value = { ...items.value, [id]: { ...entry, isSearching: false } }
      }
    }
  }

  async function processNext() {
    while (isProcessing.value && lookupQueue.value.length > 0) {
      const id: string = lookupQueue.value[0]!
      lookupQueue.value = lookupQueue.value.slice(1)

      const item = items.value[id]
      if (!item) continue

      items.value = { ...items.value, [id]: { ...item, isSearching: true } }

      const strategy = retryStrategy.value
      try {
        const searchParams = buildLibraryImportSearchParams(item, strategy)
        const results = await apiService.advancedSearch(searchParams)
        metadataFetchCount.value++
        const outcome = classifyMatch(results, { detectedAsin: searchParams.asin })
        const current = items.value[id]!
        items.value = {
          ...items.value,
          [id]: {
            ...current,
            ...outcome,
            matchedByStrategy:
              outcome.matchState === 'matched' ? strategy : current.matchedByStrategy,
            isSearching: false,
            hasSearched: true,
          },
        }
        _persistMatches()
      } catch (e) {
        // A rate limit or a transport failure says nothing about the book. Leave the row
        // unsearched so "Start matching" picks it up again, and stop the queue so the next
        // hundred rows do not burn through the same limit.
        const status = (e as { status?: number })?.status
        const retryAfter = (e as { retryAfter?: number })?.retryAfter
        const rateLimited = status === 429
        const current = items.value[id]
        if (current) {
          items.value = {
            ...items.value,
            [id]: {
              ...current,
              isSearching: false,
              hasSearched: false,
              matchState: 'unsearched',
              matchIssue: rateLimited ? 'rate-limited' : 'search-failed',
            },
          }
        }
        lookupQueue.value = [id, ...lookupQueue.value]
        isProcessing.value = false
        queuePausedReason.value = rateLimited
          ? `Audible rate limited the search${retryAfter ? `, retry in ${retryAfter}s` : ''}. Matching paused.`
          : `The search request failed (${(e as Error)?.message ?? 'unknown error'}). Matching paused.`
        logger.debug('[libraryImport] Search paused:', queuePausedReason.value)
        return
      }
    }
    isProcessing.value = false
  }

  // ─── Per-row manual search ────────────────────────────────────────────────

  async function searchItem(id: string, query: string) {
    const item = items.value[id]
    if (!item) return

    items.value[id] = { ...item, isSearching: true }
    try {
      const isAsin = looksLikeAsin(query)
      const results = await apiService.advancedSearch(
        isAsin
          ? { asin: query.trim() }
          : {
              title: query,
              ...(item.durationSeconds ? { durationSeconds: item.durationSeconds } : {}),
            },
      )
      const current = items.value[id]!
      items.value[id] = {
        ...current,
        isSearching: false,
        hasSearched: true,
        candidates: [...results]
          .sort((a, b) => (b.matchScore ?? -1) - (a.matchScore ?? -1))
          .slice(0, MAX_ROW_CANDIDATES),
        matchState:
          current.matchState === 'unsearched' && results.length === 0
            ? 'unmatched'
            : current.matchState,
      }
      _persistMatches()
      return results
    } catch {
      items.value[id] = { ...items.value[id]!, isSearching: false }
      return []
    }
  }

  // ─── Match management ─────────────────────────────────────────────────────

  // A human picking from the list is the strongest signal there is.
  function selectMatch(id: string, match: SearchResult) {
    const item = items.value[id]
    if (!item) return
    items.value[id] = {
      ...item,
      selectedMatch: match,
      matchState: 'matched',
      matchIssue: 'none',
      hasSearched: true,
      selected: true,
    }
    _persistMatches()
  }

  function clearMatch(id: string) {
    const item = items.value[id]
    if (!item) return
    items.value[id] = {
      ...item,
      selectedMatch: null,
      matchState: item.candidates.length > 0 ? 'needs-review' : 'unmatched',
      matchIssue: item.candidates.length > 0 ? 'low-confidence' : 'no-results',
      selected: false,
    }
    _persistMatches()
  }

  // ─── Selection ────────────────────────────────────────────────────────────

  function toggleSelect(id: string) {
    const item = items.value[id]
    if (!item) return
    // Ticking a doubtful row by hand is a decision, so record it as one.
    const nextSelected = !item.selected
    items.value[id] = {
      ...item,
      selected: nextSelected,
      matchState: nextSelected && item.selectedMatch ? 'matched' : item.matchState,
      matchIssue: nextSelected && item.selectedMatch ? 'none' : item.matchIssue,
    }
    _persistMatches()
  }

  // "Select all" only ticks rows the scorer was confident about. A doubtful row has to be looked
  // at, which is the whole point of the confidence gate.
  function toggleSelectAll() {
    const matched = itemList.value.filter((i) => i.matchState === 'matched')
    if (matched.length === 0) return
    const allSelected = matched.every((i) => i.selected)
    for (const item of matched) {
      items.value[item.id] = { ...item, selected: !allSelected }
    }
    _persistMatches()
  }

  // ─── Import ───────────────────────────────────────────────────────────────

  // Enrich metadata with full Audible data before adding to library.
  // Search results often have authors: [{ asin, name: undefined }] — the full
  // metadata fetch is the only way to get real author/narrator names.
  async function _enrichMetadata(match: SearchResult): Promise<AudibleBookMetadata> {
    const base = matchToMetadata(match)
    if (!match.asin) return base
    try {
      type AudiblePayload = {
        authors?: { name?: string }[]
        narrators?: { name?: string }[]
      }
      const resp = await apiService.getAudibleMetadata<
        { source?: string; metadata?: AudiblePayload } | AudiblePayload
      >(match.asin)
      const raw: AudiblePayload =
        resp && 'metadata' in resp && resp.metadata ? resp.metadata : (resp as AudiblePayload)
      const enrichedAuthors = (raw.authors ?? []).map((a) => a?.name ?? '').filter(Boolean)
      const enrichedNarrators = (raw.narrators ?? []).map((n) => n?.name ?? '').filter(Boolean)
      return {
        ...base,
        ...(enrichedAuthors.length > 0 ? { authors: enrichedAuthors } : {}),
        ...(enrichedNarrators.length > 0 ? { narrators: enrichedNarrators } : {}),
      }
    } catch {
      return base
    }
  }

  async function importSelected(
    rootFolderPath: string,
  ): Promise<{ imported: number; errors: string[]; warnings: string[] }> {
    // Never import a row nobody confirmed: an unreviewed wrong match merges two books.
    const toImport = itemList.value.filter(
      (i) => i.selected && i.selectedMatch && i.matchState === 'matched',
    )
    importErrors.value = []
    const warnings: string[] = []
    let imported = 0

    for (const item of toImport) {
      const match = item.selectedMatch!
      try {
        let audiobookId: number
        try {
          const metadata = await _enrichMetadata(match)
          const sanitizedMatch = {
            ...match,
            genres: normalizeGenres(match.genres),
            series: Array.isArray(match.series)
              ? ((match.series as Array<{ name?: string }>)[0]?.name ?? undefined)
              : match.series,
            // Audible emits isbn as a string; the backend binds List<string> with no lenient
            // converter, so a result carrying one made the add 400.
            isbn: normalizeIsbn(match.isbn),
          }
          const { audiobook } = await apiService.addToLibrary(metadata, {
            monitored: monitor.value != 'none',
            destinationPath: action.value === 'none' ? item.folderPath : rootFolderPath,
            searchResult: sanitizedMatch,
          })
          audiobookId = audiobook.id
        } catch (e: unknown) {
          // 409 = book already in library, extract existing audiobook from response body
          const err = e as { status?: number; body?: unknown }
          if (err?.status === 409 && err?.body) {
            const body = typeof err.body === 'string' ? JSON.parse(err.body) : err.body
            if (body?.audiobook?.id) {
              audiobookId = body.audiobook.id
              // Mutation imports may compatibility-route an existing audiobook to the
              // selected destination. In-place registration must never rewrite BasePath:
              // the existing file has to belong to the audiobook's current managed folder.
              if (action.value !== 'none' && rootFolderPath) {
                try {
                  await apiService.updateAudiobook(audiobookId, { basePath: rootFolderPath })
                } catch {
                  // Non-critical — import continues, file may go to OutputPath fallback
                }
              }
            } else {
              throw e
            }
          } else {
            throw e
          }
        }
        const importResult = await apiService.startManualImport({
          path: item.folderPath,
          mode: 'interactive',
          action: action.value,
          includeCompanionFiles: action.value !== 'none',
          cleanupEmptySourceFolders: action.value === 'move',
          items: item.sourceFiles.map((fullPath) => ({
            fullPath,
            matchedAudiobookId: audiobookId,
          })),
        })
        const failedResult = importResult.results?.find((result) => !result.success)
        if (failedResult || importResult.importedCount !== item.sourceFiles.length) {
          const reason = failedResult?.error ?? failedResult?.skipReason
          throw new Error(
            reason ??
              `Only ${importResult.importedCount} of ${item.sourceFiles.length} file(s) were imported`,
          )
        }

        for (const result of importResult.results ?? []) {
          if (result.success && result.warning && !warnings.includes(result.warning)) {
            warnings.push(result.warning)
          }
        }

        // Remove imported item from store only after the backend confirms every
        // source file represented by this book row was registered successfully.
        const updated = { ...items.value }
        delete updated[item.id]
        items.value = updated
        _persistMatches()
        imported++
      } catch (e) {
        const msg = `${item.folderName}: ${(e as Error)?.message ?? 'Import failed'}`
        importErrors.value.push(msg)
        logger.debug('[libraryImport] Import error:', msg)
      }
    }

    return { imported, errors: importErrors.value, warnings }
  }

  return {
    // State
    items,
    lookupQueue,
    isProcessing,
    rootFolderId,
    scanStatus,
    scanError,
    scanDiagnostics,
    scanWarning,
    lastScannedAt,
    action,
    monitor,
    metadataFetchCount,
    importErrors,
    queuePausedReason,
    retryStrategy,
    // Computed
    itemList,
    selectedCount,
    hasUnprocessedItems,
    processedCount,
    matchedCount,
    needsReviewCount,
    unmatchedCount,
    retryableCount,
    // Actions
    initFromRootFolder,
    triggerScan,
    startProcessing,
    retryUnmatched,
    stopProcessing,
    processNext,
    searchItem,
    selectMatch,
    clearMatch,
    toggleSelect,
    toggleSelectAll,
    importSelected,
  }
})
