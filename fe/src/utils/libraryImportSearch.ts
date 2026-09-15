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
export interface LibraryImportSearchCandidate {
  fullPath: string
  folderName: string
  detectedTitle?: string
  detectedAuthor?: string
  detectedAsin?: string
  // Added by plan 02's scan changes. Optional here: when absent the backend simply omits the
  // runtime term from its match score.
  durationSeconds?: number
}

/**
 * How to build a query for a row. The default is what the scan detected; the rest are the
 * one-click retries offered for rows that came back unmatched or doubtful.
 */
export type LibraryImportSearchStrategy =
  | 'title-author'
  | 'folder-author'
  | 'folder'
  | 'filename'
  | 'title-only'

export const LIBRARY_IMPORT_SEARCH_STRATEGIES: {
  value: LibraryImportSearchStrategy
  label: string
}[] = [
  { value: 'title-author', label: 'Title and author' },
  { value: 'folder-author', label: 'Folder name and author' },
  { value: 'folder', label: 'Folder name only' },
  { value: 'filename', label: 'Filename only' },
  { value: 'title-only', label: 'Title without author' },
]

/**
 * Audible ASINs are "B0" plus eight alphanumerics, or a ten-digit ISBN-10 style identifier. The
 * looser "any ten alphanumerics" test sent plain titles like "Alchemised" down the ASIN lookup.
 * Mirrors AudibleProductSearchWorkflow.IsAsin.
 */
export function looksLikeAsin(value: string): boolean {
  const trimmed = value.trim()
  if (trimmed.length !== 10) return false
  if (!/^[A-Za-z0-9]+$/.test(trimmed)) return false
  return /^B0/i.test(trimmed) || /^[0-9]/.test(trimmed)
}

export function buildLibraryImportFilenameStem(
  item: Pick<LibraryImportSearchCandidate, 'fullPath'>,
): string {
  return (
    item.fullPath
      .replace(/\\/g, '/')
      .split('/')
      .pop()
      ?.replace(/\.[^.]+$/, '') ?? ''
  )
}

export function buildLibraryImportFallbackTitle(
  item: Pick<LibraryImportSearchCandidate, 'fullPath' | 'folderName'>,
): string {
  const filenameStem = buildLibraryImportFilenameStem(item)
  const numericMatch = /\((\d+)\)\s*$/.exec(filenameStem)
  const stemBase = filenameStem.replace(/\s*\(\d+\)\s*$/, '').trim()
  const base =
    stemBase && stemBase.toLowerCase() !== item.folderName.toLowerCase()
      ? filenameStem
      : item.folderName

  if (numericMatch) {
    return `${base.replace(/\s*\(\d+\)\s*$/, '').trim()} ${numericMatch[1]}`
  }

  return base
}

export function buildLibraryImportInitialQuery(item: LibraryImportSearchCandidate): string {
  const asin = item.detectedAsin?.trim()
  if (asin) return asin

  const detectedTitle = item.detectedTitle?.trim()
  if (detectedTitle) return detectedTitle

  return buildLibraryImportFallbackTitle(item)
}

export function buildLibraryImportInitialAuthor(item: LibraryImportSearchCandidate): string {
  if (item.detectedAsin?.trim()) return ''
  return item.detectedAuthor?.trim() ?? ''
}

export interface LibraryImportSearchParams {
  asin?: string
  title?: string
  author?: string
  durationSeconds?: number
}

/**
 * The query for one row. `cap` is deliberately absent: it used to reach the Audible author page
 * collector as a page size and turn one row into a walk of the author's whole catalogue.
 */
export function buildLibraryImportSearchParams(
  item: LibraryImportSearchCandidate,
  strategy: LibraryImportSearchStrategy = 'title-author',
): LibraryImportSearchParams {
  const asin = item.detectedAsin?.trim()
  if (asin) {
    return { asin }
  }

  const duration =
    typeof item.durationSeconds === 'number' && item.durationSeconds > 0
      ? { durationSeconds: item.durationSeconds }
      : {}

  const title = buildLibraryImportStrategyTitle(item, strategy)
  const author = strategyUsesAuthor(strategy) ? (item.detectedAuthor?.trim() ?? '') : ''

  return {
    title,
    ...(author ? { author } : {}),
    ...duration,
  }
}

export function buildLibraryImportStrategyTitle(
  item: LibraryImportSearchCandidate,
  strategy: LibraryImportSearchStrategy,
): string {
  switch (strategy) {
    case 'folder-author':
    case 'folder':
      return item.folderName
    case 'filename':
      return buildLibraryImportFilenameStem(item) || item.folderName
    case 'title-only':
    case 'title-author':
    default:
      return item.detectedTitle?.trim() || buildLibraryImportFallbackTitle(item)
  }
}

function strategyUsesAuthor(strategy: LibraryImportSearchStrategy): boolean {
  return strategy === 'title-author' || strategy === 'folder-author'
}
