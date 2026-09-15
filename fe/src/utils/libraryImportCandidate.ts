/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */
import type { SearchResult } from '@/types'

/**
 * Two response shapes share one SearchResult type. The ASIN path sends `runtime`, `series` as a
 * string and `seriesNumber`; the non-ASIN path sends `lengthMinutes`, `releaseDate` and
 * `series[]`. Both are read here so the candidate rows look the same either way.
 */
export function candidateRuntimeMinutes(result: SearchResult): number | undefined {
  return result.lengthMinutes ?? result.runtime
}

export function formatRuntime(minutes?: number): string {
  if (!minutes || minutes <= 0) return ''
  const hours = Math.floor(minutes / 60)
  const rest = minutes % 60
  if (hours === 0) return `${rest}m`
  return rest === 0 ? `${hours}h` : `${hours}h ${rest}m`
}

export function candidateAuthor(result: SearchResult): string {
  const named = (result.authors ?? []).map((a) => a?.name).filter(Boolean)
  return named[0] ?? result.artist ?? ''
}

export function candidateSeries(result: SearchResult): string {
  const raw = result.series as unknown
  if (Array.isArray(raw)) {
    const first = (raw as Array<{ name?: string; position?: string }>)[0]
    if (!first?.name) return ''
    return first.position ? `${first.name} #${first.position}` : first.name
  }
  if (typeof raw === 'string' && raw) {
    return result.seriesNumber ? `${raw} #${result.seriesNumber}` : raw
  }
  return ''
}

export function candidateYear(result: SearchResult): string {
  return (result.releaseDate ?? result.publishDate ?? result.publishedDate ?? '').slice(0, 4)
}

/** Author, series, runtime, year and language, in that order, skipping what is missing. */
export function formatCandidateMeta(result: SearchResult): string {
  return [
    candidateAuthor(result),
    candidateSeries(result),
    formatRuntime(candidateRuntimeMinutes(result)),
    candidateYear(result),
    result.language ?? '',
  ]
    .map((part) => part.trim())
    .filter(Boolean)
    .join(' · ')
}
