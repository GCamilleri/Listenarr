<!--
  Listenarr - Audiobook Management System
  Copyright (C) 2024-2026 Listenarr Contributors

  This program is free software: you can redistribute it and/or modify
  it under the terms of the GNU Affero General Public License as published
  by the Free Software Foundation, either version 3 of the License, or
  (at your option) any later version.

  This program is distributed in the hope that it will be useful,
  but WITHOUT ANY WARRANTY; without even the implied warranty of
  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
  GNU Affero General Public License for more details.

  You should have received a copy of the GNU Affero General Public License
  along with this program. If not, see <https://www.gnu.org/licenses/>.
-->
<template>
  <Modal :visible="true" size="md" @close="emit('close')">
    <template #header>
      <ModalHeader :title="`Find Match — ${item.folderName}`" @close="emit('close')" />
    </template>
    <ModalBody>
      <div class="search-wrap">
        <div class="search-fields">
          <div class="search-input-row">
            <input
              ref="inputEl"
              v-model="searchQuery"
              class="form-input search-input"
              placeholder="Title or ASIN…"
              @input="onInput"
              @keydown.escape="emit('close')"
              @keydown.down.prevent="moveActive(1)"
              @keydown.up.prevent="moveActive(-1)"
              @keydown.enter.prevent="onEnter"
            />
          </div>
          <div class="search-input-row">
            <input
              v-model="authorQuery"
              class="form-input search-input"
              placeholder="Author (optional)…"
              @input="onInput"
              @keydown.escape="emit('close')"
              @keydown.down.prevent="moveActive(1)"
              @keydown.up.prevent="moveActive(-1)"
              @keydown.enter.prevent="onEnter"
            />
            <PhSpinner v-if="isSearching" class="ph-spin search-spinner" :size="16" />
          </div>
        </div>

        <div class="strategy-chips">
          <button
            v-for="strategy in strategies"
            :key="strategy.value"
            type="button"
            class="strategy-chip"
            :class="{ active: activeStrategy === strategy.value }"
            :data-strategy="strategy.value"
            @click="applyStrategy(strategy.value)"
          >
            {{ strategy.label }}
          </button>
        </div>

        <div v-if="searchError" class="search-error" data-testid="search-error">
          {{ searchError }}
        </div>

        <div v-if="searchResults.length > 0" class="results-list">
          <div
            v-for="(result, index) in searchResults"
            :key="result.asin ?? result.id ?? result.title"
            class="result-item"
            :class="{ active: index === activeIndex }"
            @click="select(result)"
            @mouseenter="activeIndex = index"
          >
            <img
              v-if="result.imageUrl"
              :src="getProtectedImageSrc(result.imageUrl, placeholderUrl)"
              class="result-thumb"
              alt=""
            />
            <div class="result-info">
              <span class="result-title">
                {{ result.title
                }}<span v-if="result.subtitle" class="result-subtitle"> - {{ result.subtitle }}</span>
              </span>
              <span class="result-meta">{{ formatCandidateMeta(result) }}</span>
              <span v-if="result.asin" class="result-asin">{{ result.asin }}</span>
            </div>
            <span
              v-if="result.matchScore != null"
              class="result-score"
              :title="(result.matchReasons ?? []).join(', ')"
            >
              {{ Math.round(result.matchScore * 100) }}%
            </span>
          </div>
        </div>

        <div v-else-if="hasSearched && !isSearching && !searchError" class="no-results">
          No results for "{{ searchQuery }}"{{ authorQuery ? ` by "${authorQuery}"` : '' }}
        </div>

        <div v-else-if="!hasSearched && !isSearching" class="hint-text">
          Type a title or paste an ASIN to search
        </div>
      </div>
    </ModalBody>
  </Modal>
</template>

<script setup lang="ts">
import { ref, onMounted, nextTick } from 'vue'
import { PhSpinner } from '@phosphor-icons/vue'
import { Modal, ModalHeader, ModalBody } from '@/components/feedback'
import { apiService } from '@/services/api'
import { useProtectedImages } from '@/composables/useProtectedImages'
import type { LibraryImportItem } from '@/stores/libraryImport'
import {
  LIBRARY_IMPORT_SEARCH_STRATEGIES,
  buildLibraryImportInitialAuthor,
  buildLibraryImportSearchParams,
  looksLikeAsin,
} from '@/utils/libraryImportSearch'
import type { LibraryImportSearchStrategy } from '@/utils/libraryImportSearch'
import { formatCandidateMeta } from '@/utils/libraryImportCandidate'
import { getPlaceholderUrl } from '@/utils/placeholder'
import type { SearchResult } from '@/types'

const props = defineProps<{ item: LibraryImportItem }>()
const emit = defineEmits<{
  close: []
  select: [result: SearchResult]
}>()

const { getProtectedImageSrc } = useProtectedImages()
const inputEl = ref<HTMLInputElement | null>(null)
const placeholderUrl = getPlaceholderUrl()
const strategies = LIBRARY_IMPORT_SEARCH_STRATEGIES

// Start from the same query the automatic search used, so retyping the same words is not the
// first thing a user has to do. The detected title is included: after the scan changes it is the
// album tag or the folder name, and both are worth trying.
const activeStrategy = ref<LibraryImportSearchStrategy>('title-author')
const searchQuery = ref(buildLibraryImportSearchParams(props.item, 'title-author').title ?? '')
const authorQuery = ref(buildLibraryImportInitialAuthor(props.item))
const searchResults = ref<SearchResult[]>([])
const isSearching = ref(false)
const hasSearched = ref(false)
const searchError = ref<string | null>(null)
const activeIndex = ref(0)

const RESULT_LIMIT = 20

let debounceTimer: ReturnType<typeof setTimeout> | null = null

onMounted(async () => {
  await nextTick()
  inputEl.value?.focus()
  inputEl.value?.select()
  if (searchQuery.value.trim()) runSearch()
})

function onInput() {
  if (debounceTimer) clearTimeout(debounceTimer)
  debounceTimer = setTimeout(() => runSearch(), 400)
}

function applyStrategy(strategy: LibraryImportSearchStrategy) {
  activeStrategy.value = strategy
  const params = buildLibraryImportSearchParams(props.item, strategy)
  searchQuery.value = params.title ?? searchQuery.value
  authorQuery.value = params.author ?? ''
  runSearch()
}

function moveActive(delta: number) {
  if (searchResults.value.length === 0) return
  const next = activeIndex.value + delta
  activeIndex.value = Math.min(Math.max(next, 0), searchResults.value.length - 1)
}

function onEnter() {
  const active = searchResults.value[activeIndex.value]
  if (active) {
    select(active)
    return
  }
  runSearch()
}

async function runSearch() {
  const q = searchQuery.value.trim()
  if (!q) return
  isSearching.value = true
  hasSearched.value = false
  searchError.value = null
  try {
    const isAsin = looksLikeAsin(q)
    const params = isAsin
      ? { asin: q }
      : {
          title: q,
          author: authorQuery.value.trim() || undefined,
          pagination: { limit: RESULT_LIMIT },
          ...(props.item.durationSeconds ? { durationSeconds: props.item.durationSeconds } : {}),
        }
    const results = await apiService.advancedSearch(params)
    searchResults.value = [...results]
      .sort((a, b) => (b.matchScore ?? -1) - (a.matchScore ?? -1))
      .slice(0, RESULT_LIMIT)
    activeIndex.value = 0
    hasSearched.value = true
  } catch (e) {
    // Without this the spinner just stopped and the modal looked like it found nothing.
    searchResults.value = []
    hasSearched.value = true
    searchError.value =
      (e as { status?: number })?.status === 429
        ? 'Rate limited by the metadata provider. Wait a moment and try again.'
        : `The search failed: ${(e as Error)?.message ?? 'unknown error'}`
  } finally {
    isSearching.value = false
  }
}

function select(result: SearchResult) {
  emit('select', result)
  emit('close')
}
</script>

<style scoped>
.search-wrap {
  display: flex;
  flex-direction: column;
  gap: 0.5rem;
}

.search-fields {
  display: flex;
  flex-direction: column;
  gap: 0.4rem;
}

.search-input-row {
  display: flex;
  align-items: center;
  gap: 0.5rem;
}

.search-input {
  flex: 1;
}

.search-spinner {
  color: #888;
  flex-shrink: 0;
}

.results-list {
  max-height: 320px;
  overflow-y: auto;
  border: 1px solid #333;
  border-radius: 6px;
}

.result-item {
  display: flex;
  align-items: center;
  gap: 0.6rem;
  padding: 0.5rem 0.75rem;
  cursor: pointer;
  transition: background 0.15s;
  border-bottom: 1px solid #2a2a2a;
}

.result-item:last-child {
  border-bottom: none;
}

.result-item:hover {
  background: #2a2a2a;
}

.result-thumb {
  width: 36px;
  height: 36px;
  object-fit: cover;
  border-radius: 3px;
  flex-shrink: 0;
}

.result-info {
  min-width: 0;
  flex: 1;
}

.result-title {
  display: block;
  font-size: 0.875rem;
  color: #e0e0e0;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.result-meta {
  display: block;
  font-size: 0.75rem;
  color: #888;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.result-asin {
  font-family: monospace;
  font-size: 0.7rem;
  color: #6f7888;
}

.result-subtitle {
  color: #9aa4b5;
}

.result-item.active {
  background: #2a2a2a;
}

.result-score {
  flex-shrink: 0;
  font-size: 0.72rem;
  color: #9aa4b5;
}

.strategy-chips {
  display: flex;
  flex-wrap: wrap;
  gap: 0.3rem;
}

.strategy-chip {
  border: 1px solid #333;
  border-radius: 999px;
  background: none;
  color: #9aa4b5;
  font-size: 0.7rem;
  padding: 0.16rem 0.5rem;
  cursor: pointer;
}

.strategy-chip.active,
.strategy-chip:hover {
  border-color: var(--brand-500, #6366f1);
  color: #e0e0e0;
}

.search-error {
  font-size: 0.78rem;
  color: #f59e0b;
}

.no-results,
.hint-text {
  padding: 0.75rem 0;
  font-size: 0.85rem;
  color: #666;
  text-align: center;
}
</style>
