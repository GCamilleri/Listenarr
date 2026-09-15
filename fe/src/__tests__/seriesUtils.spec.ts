import { describe, it, expect } from 'vitest'
import {
  collectSeriesNameCounts,
  formatSeriesMemberships,
  getBookSeriesNames,
  getSeriesNumberFor,
} from '@/utils/seriesUtils'

describe('formatSeriesMemberships', () => {
  it('lists every series a book belongs to with its number', () => {
    const result = formatSeriesMemberships({
      series: 'Publication Order',
      seriesNumber: '1',
      seriesMemberships: [
        { seriesName: 'Publication Order', seriesNumber: '1', isPrimary: true, sortOrder: 0 },
        { seriesName: 'Chronological Order', seriesNumber: '3', isPrimary: false, sortOrder: 1 },
      ],
    })
    expect(result).toBe('Publication Order #1, Chronological Order #3')
  })

  it('omits the number when a membership has none', () => {
    const result = formatSeriesMemberships({
      seriesMemberships: [{ seriesName: 'Standalone Saga', isPrimary: true, sortOrder: 0 }],
    })
    expect(result).toBe('Standalone Saga')
  })

  it('falls back to the legacy single series when there are no memberships', () => {
    expect(formatSeriesMemberships({ series: 'Solo Series', seriesNumber: '2' })).toBe(
      'Solo Series #2',
    )
    expect(formatSeriesMemberships({ series: 'No Number' })).toBe('No Number')
  })

  it('returns an empty string when there is no series information', () => {
    expect(formatSeriesMemberships({})).toBe('')
    expect(formatSeriesMemberships({ seriesMemberships: [] })).toBe('')
  })
})

describe('getBookSeriesNames', () => {
  it('lists every series the book belongs to, deduped case-insensitively', () => {
    expect(
      getBookSeriesNames({
        seriesMemberships: [
          { seriesName: 'Mistborn', seriesNumber: '1', isPrimary: true },
          { seriesName: 'Cosmere', seriesNumber: '3' },
          { seriesName: 'mistborn', seriesNumber: '1' },
        ],
      }),
    ).toEqual(['Mistborn', 'Cosmere'])
  })

  it('falls back to the legacy single series and returns nothing when there is none', () => {
    expect(getBookSeriesNames({ series: 'Solo Series' })).toEqual(['Solo Series'])
    expect(getBookSeriesNames({})).toEqual([])
    expect(getBookSeriesNames({ seriesMemberships: [{ seriesName: '  ' }] })).toEqual([])
  })
})

describe('collectSeriesNameCounts', () => {
  it('counts books per series and keeps the first spelling it saw', () => {
    const counts = collectSeriesNameCounts([
      { seriesMemberships: [{ seriesName: 'Mistborn', isPrimary: true }] },
      { seriesMemberships: [{ seriesName: 'mistborn', isPrimary: true }] },
      { series: 'Cosmere' },
    ])
    expect(counts).toEqual([
      { name: 'Mistborn', count: 2 },
      { name: 'Cosmere', count: 1 },
    ])
  })
})

describe('getSeriesNumberFor', () => {
  it('returns the position in the named series, not the primary one', () => {
    const book = {
      series: 'Other Series',
      seriesNumber: '5',
      seriesMemberships: [
        { seriesName: 'Other Series', seriesNumber: '5', isPrimary: true },
        { seriesName: 'Mistborn', seriesNumber: '2' },
      ],
    }
    expect(getSeriesNumberFor(book, 'mistborn')).toBe('2')
    expect(getSeriesNumberFor(book, 'Other Series')).toBe('5')
    expect(getSeriesNumberFor(book, 'Cosmere')).toBe('')
  })

  it('reads the legacy columns when there are no memberships', () => {
    expect(getSeriesNumberFor({ series: 'Mistborn', seriesNumber: '1' }, 'Mistborn')).toBe('1')
  })
})
