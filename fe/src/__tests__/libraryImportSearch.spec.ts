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
import { describe, expect, it } from 'vitest'
import {
  buildLibraryImportFallbackTitle,
  buildLibraryImportInitialAuthor,
  buildLibraryImportInitialQuery,
  buildLibraryImportSearchParams,
} from '@/utils/libraryImportSearch'

// cap is deliberately absent from every expectation below. It used to reach the Audible author
// page collector as a page size and turn one row into a walk of the author's whole catalogue.

describe('library import search helpers', () => {
  it('prefers detected title and author before folder fallback', () => {
    const item = {
      fullPath: 'C:\\incoming\\Chapter 01.mp3',
      folderName: 'test-import',
      detectedTitle: 'Jack of Shadows',
      detectedAuthor: 'Roger Zelazny',
    }

    expect(buildLibraryImportInitialQuery(item)).toBe('Jack of Shadows')
    expect(buildLibraryImportInitialAuthor(item)).toBe('Roger Zelazny')
    expect(buildLibraryImportSearchParams(item)).toEqual({
      title: 'Jack of Shadows',
      author: 'Roger Zelazny',
    })
  })

  it('falls back to the filename or folder when no detected title exists', () => {
    const item = {
      fullPath: 'C:\\incoming\\The Land (3).m4b',
      folderName: 'The Land',
    }

    expect(buildLibraryImportFallbackTitle(item)).toBe('The Land 3')
    expect(buildLibraryImportInitialQuery(item)).toBe('The Land 3')
    expect(buildLibraryImportSearchParams(item)).toEqual({
      title: 'The Land 3',
    })
  })

  it('still prioritizes asin over title and author', () => {
    const item = {
      fullPath: 'C:\\incoming\\Book.m4b',
      folderName: 'Book',
      detectedTitle: 'Ignored Title',
      detectedAuthor: 'Ignored Author',
      detectedAsin: 'B0DQR9D4YG',
    }

    expect(buildLibraryImportInitialQuery(item)).toBe('B0DQR9D4YG')
    expect(buildLibraryImportInitialAuthor(item)).toBe('')
    expect(buildLibraryImportSearchParams(item)).toEqual({
      asin: 'B0DQR9D4YG',
    })
  })

  it('passes a duration hint through when the scan supplied one', () => {
    const item = {
      fullPath: '/books/Mistborn/Mistborn.m4b',
      folderName: 'Mistborn',
      detectedTitle: 'Mistborn, The Final Empire',
      detectedAuthor: 'Brandon Sanderson',
      durationSeconds: 89940,
    }

    expect(buildLibraryImportSearchParams(item)).toEqual({
      title: 'Mistborn, The Final Empire',
      author: 'Brandon Sanderson',
      durationSeconds: 89940,
    })
  })

  it('builds a different query for each retry strategy', () => {
    const item = {
      fullPath: '/books/Brandon Sanderson/Mistborn 01/Chapter 01.mp3',
      folderName: 'Mistborn 01',
      detectedTitle: 'The Final Empire',
      detectedAuthor: 'Brandon Sanderson',
    }

    expect(buildLibraryImportSearchParams(item, 'title-author')).toEqual({
      title: 'The Final Empire',
      author: 'Brandon Sanderson',
    })
    expect(buildLibraryImportSearchParams(item, 'folder-author')).toEqual({
      title: 'Mistborn 01',
      author: 'Brandon Sanderson',
    })
    expect(buildLibraryImportSearchParams(item, 'folder')).toEqual({ title: 'Mistborn 01' })
    expect(buildLibraryImportSearchParams(item, 'filename')).toEqual({ title: 'Chapter 01' })
    expect(buildLibraryImportSearchParams(item, 'title-only')).toEqual({
      title: 'The Final Empire',
    })
  })
})
