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
import type { Audiobook, AudiobookSeriesMembership } from '@/types'

type SeriesBearer = Pick<Audiobook, 'series' | 'seriesNumber' | 'seriesMemberships'>

/**
 * All series a book belongs to, formatted for display (e.g. "Publication Order #1,
 * Chronological Order #3"). Uses every series membership so a multi-series book shows all of
 * them; falls back to the legacy single series/number when no memberships are present.
 */
export function formatSeriesMemberships(book: SeriesBearer): string {
  const memberships = book.seriesMemberships
  if (memberships && memberships.length > 0) {
    const parts = memberships.map(formatMembership).filter(Boolean)
    if (parts.length > 0) return parts.join(', ')
  }

  const legacyName = (book.series || '').trim()
  if (!legacyName) return ''
  const legacyNumber = (book.seriesNumber || '').trim()
  return legacyNumber ? `${legacyName} #${legacyNumber}` : legacyName
}

function formatMembership(membership: AudiobookSeriesMembership): string {
  const name = (membership.seriesName || '').trim()
  if (!name) return ''
  const number = (membership.seriesNumber || '').trim()
  return number ? `${name} #${number}` : name
}

/**
 * All series a book belongs to (deduped case-insensitively), so a multi-series book is grouped
 * under each of its series rather than only its primary. Falls back to the legacy single series.
 */
export function getBookSeriesNames(book: SeriesBearer): string[] {
  const memberships = book.seriesMemberships
  if (memberships && memberships.length > 0) {
    const names: string[] = []
    const seen = new Set<string>()
    for (const membership of memberships) {
      const name = (membership.seriesName || '').trim()
      if (!name) continue
      const dedupeKey = name.toLowerCase()
      if (seen.has(dedupeKey)) continue
      seen.add(dedupeKey)
      names.push(name)
    }
    if (names.length > 0) return names
  }
  const legacy = (book.series || '').trim()
  return legacy ? [legacy] : []
}

export interface SeriesNameCount {
  name: string
  count: number
}

/**
 * Distinct series names across the given books with the number of books in each, ordered by
 * count and then name. The first spelling encountered wins, so picking a suggestion sends the
 * stored spelling rather than a normalised one and the books land on the same tile.
 */
export function collectSeriesNameCounts(books: SeriesBearer[]): SeriesNameCount[] {
  const counts = new Map<string, SeriesNameCount>()
  for (const book of books) {
    for (const name of getBookSeriesNames(book)) {
      const key = name.toLowerCase()
      const existing = counts.get(key)
      if (existing) {
        existing.count += 1
      } else {
        counts.set(key, { name, count: 1 })
      }
    }
  }
  return [...counts.values()].sort(
    (left, right) => right.count - left.count || left.name.localeCompare(right.name),
  )
}

/** The book's position in one specific series, matched case-insensitively by name. */
export function getSeriesNumberFor(book: SeriesBearer, seriesName: string): string {
  const target = seriesName.trim().toLowerCase()
  if (!target) return ''
  const membership = (book.seriesMemberships ?? []).find(
    (candidate) => (candidate.seriesName || '').trim().toLowerCase() === target,
  )
  if (membership) return (membership.seriesNumber || '').trim()
  if ((book.series || '').trim().toLowerCase() === target) {
    return (book.seriesNumber || '').trim()
  }
  return ''
}
