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
  <Modal :visible="isOpen" size="md" @close="close">
    <template #header>
      <ModalHeader :title="'Bulk Edit Audiobooks'" :icon="PhPencil" @close="close" />
    </template>

    <template #default>
      <ModalBody>
        <div class="info-section">
          <PhInfo />
          <p>
            Editing <strong>{{ selectedCount }}</strong> audiobook{{
              selectedCount !== 1 ? 's' : ''
            }}. Only the fields you change will be updated.
          </p>
        </div>

        <form @submit.prevent="handleSave" class="edit-form">
          <!-- Monitored Status -->
          <div class="form-group">
            <label class="form-label">
              <PhEye />
              Monitored Status
            </label>
            <div class="radio-group">
              <label class="radio-label">
                <input type="radio" v-model="formData.monitored" :value="null" name="monitored" />
                <span>No Change</span>
              </label>
              <label class="radio-label">
                <input type="radio" v-model="formData.monitored" :value="true" name="monitored" />
                <span>Monitored</span>
              </label>
              <label class="radio-label">
                <input type="radio" v-model="formData.monitored" :value="false" name="monitored" />
                <span>Unmonitored</span>
              </label>
            </div>
            <p class="help-text">
              Monitored audiobooks will be automatically upgraded when better quality releases are
              found
            </p>
          </div>

          <!-- Quality Profile -->
          <div class="form-group">
            <label class="form-label" for="quality-profile">
              <PhStar />
              Quality Profile
            </label>
            <select id="quality-profile" v-model="formData.qualityProfileId" class="form-select">
              <option :value="null">No Change</option>
              <option v-for="profile in qualityProfiles" :key="profile.id" :value="profile.id">
                {{ profile.name }}{{ profile.isDefault ? ' (Default)' : '' }}
              </option>
            </select>
            <p class="help-text">
              Controls which quality standards to use for downloads and upgrades
            </p>
          </div>

          <!-- Root Folder -->
          <div class="form-group">
            <label class="form-label">
              <PhFolder />
              Root Folder
            </label>

            <label class="radio-label">
              <Checkbox v-model="formData.rootChangeEnabled">Change root folder</Checkbox>
            </label>

            <div v-if="formData.rootChangeEnabled" class="mt-md">
              <RootFolderSelect v-model:rootId="formData.rootId" data-cy="bulk-root-select" />

              <p class="help-text">
                Select a configured root. If you choose "Use default" and no named default exists,
                the application default output path will be used.
              </p>
              <p v-if="resolvedRootPath" class="help-text" data-testid="effective-destination-root">
                Destination root: <code>{{ resolvedRootPath }}</code>
              </p>
            </div>
          </div>

          <!-- Series -->
          <div class="form-group" data-testid="series-section">
            <label class="form-label">
              <PhBooks />
              Series
            </label>
            <div class="radio-group">
              <label v-for="option in seriesModeOptions" :key="option.value" class="radio-label">
                <input
                  type="radio"
                  v-model="formData.seriesMode"
                  :value="option.value"
                  name="seriesMode"
                  :data-testid="`series-mode-${option.value}`"
                />
                <span>{{ option.label }}</span>
              </label>
            </div>

            <div v-if="needsMatchSeries" class="series-block">
              <label class="series-sublabel" for="series-match">Which series on these books</label>
              <select
                id="series-match"
                v-model="formData.matchName"
                class="form-select"
                data-testid="series-match-select"
              >
                <option value="">Select a series</option>
                <option v-for="entry in selectedBooksSeries" :key="entry.name" :value="entry.name">
                  {{ entry.name }} ({{ entry.count }})
                </option>
              </select>
            </div>

            <div v-if="needsTargetSeries" class="series-block">
              <label class="series-sublabel" for="series-name">{{ targetSeriesLabel }}</label>
              <input
                id="series-name"
                v-model="formData.seriesName"
                type="text"
                class="form-input"
                autocomplete="off"
                placeholder="Type a series name"
                data-testid="series-name-input"
                @input="onSeriesQueryChanged"
              />
              <p v-if="formData.seriesAsin" class="help-text" data-testid="series-asin">
                Audible series <code>{{ formData.seriesAsin }}</code>
              </p>
              <ul
                v-if="localSeriesSuggestions.length > 0"
                class="series-suggestions"
                data-testid="local-series-suggestions"
              >
                <li v-for="entry in localSeriesSuggestions" :key="`local-${entry.name}`">
                  <button
                    type="button"
                    class="series-suggestion"
                    @click="pickLocalSeries(entry.name)"
                  >
                    <span>{{ entry.name }}</span>
                    <span class="series-suggestion-meta">{{ entry.count }} in library</span>
                  </button>
                </li>
              </ul>
              <ul
                v-if="remoteSeriesSuggestions.length > 0"
                class="series-suggestions"
                data-testid="remote-series-suggestions"
              >
                <li v-for="entry in remoteSeriesSuggestions" :key="`remote-${entry.name}`">
                  <button type="button" class="series-suggestion" @click="pickRemoteSeries(entry)">
                    <span>{{ entry.name }}</span>
                    <span class="series-suggestion-meta">Audible</span>
                  </button>
                </li>
              </ul>
            </div>

            <div v-if="supportsNumbering" class="series-block">
              <span class="series-sublabel">Numbering</span>
              <div class="radio-group">
                <label class="radio-label">
                  <input
                    type="radio"
                    v-model="formData.numberingMode"
                    value="keep"
                    name="seriesNumbering"
                  />
                  <span>Keep existing</span>
                </label>
                <label class="radio-label">
                  <input
                    type="radio"
                    v-model="formData.numberingMode"
                    value="clear"
                    name="seriesNumbering"
                  />
                  <span>Clear</span>
                </label>
                <label class="radio-label">
                  <input
                    type="radio"
                    v-model="formData.numberingMode"
                    value="perBook"
                    name="seriesNumbering"
                    data-testid="series-numbering-perBook"
                  />
                  <span>Set per book</span>
                </label>
              </div>

              <div v-if="formData.numberingMode === 'perBook'" data-testid="per-book-numbers">
                <div class="per-book-actions">
                  <button
                    type="button"
                    class="btn"
                    data-testid="renumber-button"
                    @click="renumberInOrder"
                  >
                    Renumber 1..{{ perBookOrder.length }} in this order
                  </button>
                  <button
                    v-if="formData.seriesAsin"
                    type="button"
                    class="btn"
                    :disabled="fillingFromAudible"
                    data-testid="fill-from-audible"
                    @click="fillNumbersFromAudible"
                  >
                    Fill numbers from Audible
                  </button>
                </div>
                <ul class="per-book-list">
                  <li v-for="(id, index) in perBookPage" :key="id" class="per-book-row">
                    <img
                      v-if="bookFor(id)?.imageUrl"
                      class="per-book-cover"
                      :src="coverFor(id)"
                      alt=""
                    />
                    <div class="per-book-details">
                      <span class="per-book-title">{{
                        bookFor(id)?.title ?? `Audiobook ${id}`
                      }}</span>
                      <span class="per-book-series">{{ currentSeriesText(id) }}</span>
                    </div>
                    <input
                      v-model="formData.perBookNumbers[id]"
                      type="text"
                      class="form-input per-book-number"
                      :data-testid="`per-book-number-${id}`"
                      aria-label="Series number"
                    />
                    <div class="per-book-reorder">
                      <button
                        type="button"
                        class="btn"
                        :disabled="pageStart + index === 0"
                        :data-testid="`per-book-up-${id}`"
                        @click="moveBook(id, -1)"
                      >
                        <PhArrowUp />
                      </button>
                      <button
                        type="button"
                        class="btn"
                        :disabled="pageStart + index === perBookOrder.length - 1"
                        :data-testid="`per-book-down-${id}`"
                        @click="moveBook(id, 1)"
                      >
                        <PhArrowDown />
                      </button>
                    </div>
                  </li>
                </ul>
                <div v-if="perBookPageCount > 1" class="per-book-paging">
                  <button
                    type="button"
                    class="btn"
                    :disabled="perBookPage1 <= 1"
                    @click="perBookPage1--"
                  >
                    Previous
                  </button>
                  <span>Page {{ perBookPage1 }} of {{ perBookPageCount }}</span>
                  <button
                    type="button"
                    class="btn"
                    :disabled="perBookPage1 >= perBookPageCount"
                    @click="perBookPage1++"
                  >
                    Next
                  </button>
                </div>
              </div>
            </div>

            <p class="help-text">
              Changing a series never moves files. You can organize the affected books afterwards.
            </p>
          </div>

          <!-- Action Buttons moved to footer -->
        </form>

        <div v-if="showResults" class="results-section">
          <h4>Bulk Update Results</h4>
          <p>
            {{ successfulResults.length }} succeeded, {{ partialResults.length }} partially
            succeeded, {{ failedResults.length }} failed
          </p>

          <div class="results-list">
            <div v-for="res in results" :key="res.id" class="result-item">
              <div class="result-header">
                <strong>Audiobook ID {{ res.id }}</strong>
                <span
                  class="status"
                  :class="{
                    success: res.success,
                    partial: isPartialResult(res),
                    error: !res.success && !isPartialResult(res),
                  }"
                  >{{ resultStatusLabel(res) }}</span
                >
              </div>
              <p v-if="isPartialResult(res)" class="partial-message">
                {{ partialResultMessage(res) }}
              </p>
              <p v-if="resultSeriesText(res)" class="result-series">{{ resultSeriesText(res) }}</p>
              <ul v-if="res.errors && res.errors.length > 0" class="error-list">
                <li v-for="(err, idx) in res.errors" :key="idx">{{ err }}</li>
              </ul>
            </div>
          </div>

          <div v-if="showOrganizePrompt" class="organize-prompt" data-testid="organize-prompt">
            <p>
              These books now belong to a different series, so their folder pattern points somewhere
              new. Nothing has moved yet.
            </p>
            <div class="organize-actions">
              <button
                type="button"
                class="btn btn-primary"
                data-testid="organize-now"
                @click="organizeNow"
              >
                Organize these books now
              </button>
              <button type="button" class="btn" @click="close">Not now</button>
            </div>
          </div>
        </div>
      </ModalBody>
    </template>

    <template #footer>
      <button type="button" class="cancel-button btn" @click="close"><PhX /> Cancel</button>
      <button
        type="button"
        class="btn btn-primary"
        :disabled="saving || !hasChanges"
        @click="handleSave"
      >
        <PhSpinner v-if="saving" class="ph-spin" />
        <PhCheck v-else /> {{ saving ? 'Saving...' : 'Save Changes' }}
      </button>
    </template>
  </Modal>

  <MoveAudiobookModal
    :visible="showMoveConfirm"
    :pendingRootPath="pendingRootPath"
    v-model:moveFiles="modalMoveFiles"
    v-model:deleteEmpty="modalDeleteEmpty"
    @cancel="cancelMoveConfirm"
    @confirm="
      (payload) => {
        if (payload?.moveFiles) confirmMove()
        else confirmChangeWithoutMoving()
      }
    "
  />
</template>

<script setup lang="ts">
import { ref, computed, watch } from 'vue'
import Checkbox from '@/components/form/Checkbox.vue'
import { Modal, ModalBody, ModalHeader } from '@/components/feedback'
import MoveAudiobookModal from '@/components/feedback/MoveAudiobookModal.vue'
import { apiService } from '@/services/api'
import { useMoveJobsStore } from '@/stores/moveJobs'
import { executeBulkEdit, type BulkEditItemResult } from '@/utils/bulkEditOrchestration'
import { buildApiPath } from '@/services/apiBase'
import { useToast } from '@/services/toastService'
import type {
  Audiobook,
  BulkSeriesUpdate,
  BulkSeriesUpdateMode,
  BulkUpdateValue,
  QualityProfile,
  SeriesLookupResponse,
} from '@/types'
import { useRootFoldersStore } from '@/stores/rootFolders'
import { useLibraryStore } from '@/stores/library'
import {
  collectSeriesNameCounts,
  getBookSeriesNames,
  getSeriesNumberFor,
  type SeriesNameCount,
} from '@/utils/seriesUtils'
import RootFolderSelect from '@/components/form/RootFolderSelect.vue'

interface Props {
  isOpen: boolean
  selectedCount: number
  selectedIds: Set<number>
  /** Selection in the order the calling view shows it, so per-book numbering starts sensibly. */
  selectedIdsOrdered?: number[]
  initialSeriesMode?: SeriesFormMode
  initialMatchName?: string
}

type SeriesFormMode = 'none' | BulkSeriesUpdateMode
type NumberingMode = 'keep' | 'clear' | 'perBook'

interface FormData {
  monitored: boolean | null
  qualityProfileId: number | null
  // root change controls
  rootChangeEnabled: boolean
  rootId: number | null
  // series controls
  seriesMode: SeriesFormMode
  seriesName: string
  seriesAsin: string | null
  matchName: string
  numberingMode: NumberingMode
  perBookNumbers: Record<number, string>
}

const props = defineProps<Props>()
const emit = defineEmits<{
  close: []
  saved: []
  organize: [ids: number[]]
}>()

const qualityProfiles = ref<QualityProfile[]>([])
const rootStore = useRootFoldersStore()
const moveJobsStore = useMoveJobsStore()
const saving = ref(false)

// Root change helper values
const defaultOutputPath = ref<string | null>(null)
const results = ref<BulkEditItemResult[]>([])
const showResults = ref(false)
const bulkUpdateEndpoint = buildApiPath('/library/bulk-update')

// Move confirmation modal state
const showMoveConfirm = ref(false)
const pendingRootPath = ref<string | null>(null)
const modalMoveFiles = ref(true)
const modalDeleteEmpty = ref(true)
let moveConfirmResolver:
  | ((r: { proceed: boolean; moveFiles: boolean; deleteEmptySource: boolean }) => void)
  | null = null

const libraryStore = useLibraryStore()
const PER_BOOK_PAGE_SIZE = 100
const seriesModeOptions: Array<{ value: SeriesFormMode; label: string }> = [
  { value: 'none', label: 'No Change' },
  { value: 'setPrimary', label: 'Set as primary series' },
  { value: 'addMembership', label: 'Add to series' },
  { value: 'removeMembership', label: 'Remove from series' },
  { value: 'renameMembership', label: 'Rename series' },
]

const formData = ref<FormData>({
  monitored: null,
  qualityProfileId: null,
  rootChangeEnabled: false,
  rootId: null,
  seriesMode: props.initialSeriesMode ?? 'none',
  seriesName: '',
  seriesAsin: null,
  matchName: props.initialMatchName ?? '',
  numberingMode: 'keep',
  perBookNumbers: {},
})

const perBookOrder = ref<number[]>([])
const perBookPage1 = ref(1)
const remoteSeriesSuggestions = ref<SeriesLookupResponse[]>([])
const fillingFromAudible = ref(false)
const showOrganizePrompt = ref(false)
let remoteLookupTimer: ReturnType<typeof setTimeout> | null = null
let remoteLookupToken = 0

const orderedSelectedIds = computed(() => {
  const ordered = (props.selectedIdsOrdered ?? []).filter((id) => props.selectedIds.has(id))
  const seen = new Set(ordered)
  for (const id of props.selectedIds) {
    if (!seen.has(id)) ordered.push(id)
  }
  return ordered
})

const booksById = computed(() => {
  const map = new Map<number, Audiobook>()
  for (const book of libraryStore.audiobooks) {
    map.set(book.id, book)
  }
  return map
})

const selectedBooks = computed(() =>
  orderedSelectedIds.value
    .map((id) => booksById.value.get(id))
    .filter((book): book is Audiobook => Boolean(book)),
)

/** Series present on the selected books, for the "which series on these books" picker. */
const selectedBooksSeries = computed<SeriesNameCount[]>(() =>
  collectSeriesNameCounts(selectedBooks.value),
)

const librarySeriesCounts = computed<SeriesNameCount[]>(() =>
  collectSeriesNameCounts(libraryStore.audiobooks),
)

const localSeriesSuggestions = computed<SeriesNameCount[]>(() => {
  const query = formData.value.seriesName.trim().toLowerCase()
  const matches = query
    ? librarySeriesCounts.value.filter((entry) => entry.name.toLowerCase().includes(query))
    : librarySeriesCounts.value
  return matches.filter((entry) => entry.name.toLowerCase() !== query).slice(0, 8)
})

const needsTargetSeries = computed(
  () =>
    formData.value.seriesMode === 'setPrimary' ||
    formData.value.seriesMode === 'addMembership' ||
    formData.value.seriesMode === 'renameMembership',
)

const needsMatchSeries = computed(
  () =>
    formData.value.seriesMode === 'removeMembership' ||
    formData.value.seriesMode === 'renameMembership',
)

const supportsNumbering = computed(
  () =>
    formData.value.seriesMode === 'setPrimary' ||
    formData.value.seriesMode === 'addMembership' ||
    formData.value.seriesMode === 'renameMembership',
)

const targetSeriesLabel = computed(() =>
  formData.value.seriesMode === 'renameMembership' ? 'Rename it to' : 'Series',
)

const perBookPageCount = computed(() =>
  Math.max(1, Math.ceil(perBookOrder.value.length / PER_BOOK_PAGE_SIZE)),
)

const pageStart = computed(() => (perBookPage1.value - 1) * PER_BOOK_PAGE_SIZE)

const perBookPage = computed(() =>
  perBookOrder.value.slice(pageStart.value, pageStart.value + PER_BOOK_PAGE_SIZE),
)

const resolvedRootPath = computed(() => {
  if (!formData.value.rootChangeEnabled) return null
  if (formData.value.rootId && formData.value.rootId > 0) {
    return rootStore.folders.find((folder) => folder.id === formData.value.rootId)?.path ?? null
  }
  return defaultOutputPath.value
})

const hasChanges = computed(() => {
  return (
    formData.value.monitored !== null ||
    formData.value.qualityProfileId !== null ||
    formData.value.rootChangeEnabled === true ||
    formData.value.seriesMode !== 'none'
  )
})

const toast = useToast()
const successfulResults = computed(() => results.value.filter((result) => result.success))
const partialResults = computed(() => results.value.filter(isPartialResult))
const failedResults = computed(() =>
  results.value.filter((result) => !result.success && !isPartialResult(result)),
)

watch(
  () => props.isOpen,
  async (isOpen) => {
    if (isOpen) {
      await loadData()
      resetForm()
    }
  },
)

// Keep the per-book ordering in step with the selection without discarding a manual reorder.
watch(
  orderedSelectedIds,
  (ids) => {
    const kept = perBookOrder.value.filter((id) => ids.includes(id))
    perBookOrder.value = [...kept, ...ids.filter((id) => !kept.includes(id))]
    if (perBookPage1.value > perBookPageCount.value) perBookPage1.value = 1
  },
  { immediate: true },
)

async function loadData() {
  try {
    // Load quality profiles
    qualityProfiles.value = await apiService.getQualityProfiles()

    // Load root folders from configuration
    await rootStore.load()
    if (rootStore.folders.length > 0) {
      // Capture default output path for fallback when user picks "Use default"
      const def = rootStore.folders.find((f) => f.isDefault)
      defaultOutputPath.value = def?.path ?? null
    } else {
      const appSettings = await apiService.getApplicationSettings()
      defaultOutputPath.value = appSettings.outputPath || null
    }
  } catch (error) {
    console.error('Failed to load bulk edit data:', error)
  }
}

function resetForm() {
  formData.value = {
    monitored: null,
    qualityProfileId: null,
    rootChangeEnabled: false,
    rootId: null,
    seriesMode: props.initialSeriesMode ?? 'none',
    seriesName: '',
    seriesAsin: null,
    matchName: props.initialMatchName ?? '',
    numberingMode: 'keep',
    perBookNumbers: {},
  }
  remoteSeriesSuggestions.value = []
  showOrganizePrompt.value = false
  resetPerBookOrder()
}

function resetPerBookOrder() {
  perBookOrder.value = [...orderedSelectedIds.value]
  perBookPage1.value = 1
}

function bookFor(id: number): Audiobook | undefined {
  return booksById.value.get(id)
}

function coverFor(id: number): string {
  const url = bookFor(id)?.imageUrl
  return url ? apiService.getImageUrl(url) : ''
}

function currentSeriesText(id: number): string {
  const book = bookFor(id)
  if (!book) return ''
  const names = getBookSeriesNames(book)
  if (names.length === 0) return 'No series'
  return names
    .map((name) => {
      const number = getSeriesNumberFor(book, name)
      return number ? `${name} #${number}` : name
    })
    .join(', ')
}

function pickLocalSeries(name: string) {
  // Send the exact stored spelling, never a normalised one, or the books land on a third tile.
  formData.value.seriesName = name
  formData.value.seriesAsin = null
  remoteSeriesSuggestions.value = []
  prefillPerBookNumbers()
}

function pickRemoteSeries(series: SeriesLookupResponse) {
  formData.value.seriesName = series.name
  formData.value.seriesAsin = series.asin ?? null
  remoteSeriesSuggestions.value = []
  prefillPerBookNumbers()
}

function onSeriesQueryChanged() {
  formData.value.seriesAsin = null
  const query = formData.value.seriesName.trim()
  if (remoteLookupTimer) clearTimeout(remoteLookupTimer)
  if (query.length < 3) {
    remoteSeriesSuggestions.value = []
    return
  }

  const token = ++remoteLookupToken
  remoteLookupTimer = setTimeout(async () => {
    try {
      const match = await apiService.getSeriesLookup(query)
      if (token !== remoteLookupToken) return
      remoteSeriesSuggestions.value = match ? [match] : []
    } catch (error) {
      logger.debug('[BulkEditModal] Series lookup failed', error)
      if (token === remoteLookupToken) remoteSeriesSuggestions.value = []
    }
  }, 300)
}

/** Seeds each book's number input from the position it already has in the target series. */
function prefillPerBookNumbers() {
  const target = formData.value.seriesName.trim()
  if (!target) return
  for (const id of perBookOrder.value) {
    if (formData.value.perBookNumbers[id]) continue
    const book = bookFor(id)
    if (!book) continue
    const existing = getSeriesNumberFor(book, target)
    if (existing) formData.value.perBookNumbers[id] = existing
  }
}

function renumberInOrder() {
  perBookOrder.value.forEach((id, index) => {
    formData.value.perBookNumbers[id] = String(index + 1)
  })
}

function moveBook(id: number, offset: number) {
  const from = perBookOrder.value.indexOf(id)
  const to = from + offset
  if (from < 0 || to < 0 || to >= perBookOrder.value.length) return
  const next = [...perBookOrder.value]
  const [moved] = next.splice(from, 1)
  next.splice(to, 0, moved as number)
  perBookOrder.value = next
}

async function fillNumbersFromAudible() {
  const name = formData.value.seriesName.trim()
  if (!name) return
  fillingFromAudible.value = true
  try {
    const catalog = await apiService.getSeriesCatalog(name)
    const byAsin = new Map<string, string>()
    for (const entry of catalog?.books ?? []) {
      const asin = (entry.asin || '').trim().toUpperCase()
      const position = (entry.seriesNumber || '').trim()
      if (asin && position) byAsin.set(asin, position)
    }
    let filled = 0
    for (const id of perBookOrder.value) {
      const asin = (bookFor(id)?.asin || '').trim().toUpperCase()
      const position = asin ? byAsin.get(asin) : undefined
      if (!position) continue
      formData.value.perBookNumbers[id] = position
      filled += 1
    }
    if (filled === 0) {
      toast.error('No positions found', 'No selected book matched a catalogue entry by ASIN.')
    } else {
      toast.success('Positions filled', `Filled ${filled} position(s) from Audible.`)
    }
  } catch (error) {
    logger.debug('[BulkEditModal] Series catalogue lookup failed', error)
    toast.error('Audible lookup failed', 'The series catalogue could not be loaded.')
  } finally {
    fillingFromAudible.value = false
  }
}

/** Throws with a user-facing message when the chosen mode is missing something it needs. */
function buildSeriesUpdate(): BulkSeriesUpdate | null {
  const mode = formData.value.seriesMode
  if (mode === 'none') return null

  const seriesName = formData.value.seriesName.trim()
  const matchName = formData.value.matchName.trim()
  if (needsTargetSeries.value && !seriesName) {
    throw new Error('Enter the series these books should be in.')
  }
  if (needsMatchSeries.value && !matchName) {
    throw new Error('Choose which series on these books to change.')
  }

  const update: BulkSeriesUpdate = { mode }
  if (needsTargetSeries.value) update.seriesName = seriesName
  if (needsMatchSeries.value) update.matchName = matchName
  if (formData.value.seriesAsin) update.seriesAsin = formData.value.seriesAsin
  if (supportsNumbering.value) {
    update.numbering = formData.value.numberingMode === 'clear' ? 'clear' : 'keep'
    if (formData.value.numberingMode === 'perBook') update.numbering = 'explicit'
  }
  return update
}

function buildPerIdOverrides(
  seriesUpdate: BulkSeriesUpdate,
): Record<number, Record<string, BulkUpdateValue>> | undefined {
  if (!supportsNumbering.value || formData.value.numberingMode !== 'perBook') return undefined
  const overrides: Record<number, Record<string, BulkUpdateValue>> = {}
  for (const id of perBookOrder.value) {
    overrides[id] = {
      series: {
        ...seriesUpdate,
        numbering: 'explicit',
        seriesNumber: (formData.value.perBookNumbers[id] ?? '').trim(),
      },
    }
  }
  return overrides
}

function resultSeriesText(result: BulkEditItemResult): string {
  const memberships = result.seriesMemberships
  if (!memberships || memberships.length === 0) return ''
  return `Series: ${memberships
    .map((membership) =>
      membership.seriesNumber
        ? `${membership.seriesName} #${membership.seriesNumber}`
        : membership.seriesName,
    )
    .join(', ')}`
}

function organizeNow() {
  emit('organize', [...perBookOrder.value])
  close()
}

function askMoveConfirmation(newRootPath: string) {
  modalMoveFiles.value = true
  modalDeleteEmpty.value = true
  pendingRootPath.value = newRootPath
  showMoveConfirm.value = true
  return new Promise<{ proceed: boolean; moveFiles: boolean; deleteEmptySource: boolean }>(
    (resolve) => {
      moveConfirmResolver = resolve
    },
  )
}

function cancelMoveConfirm() {
  if (moveConfirmResolver)
    moveConfirmResolver({ proceed: false, moveFiles: false, deleteEmptySource: false })
  moveConfirmResolver = null
  showMoveConfirm.value = false
  pendingRootPath.value = null
}

function confirmChangeWithoutMoving() {
  if (moveConfirmResolver)
    moveConfirmResolver({ proceed: true, moveFiles: false, deleteEmptySource: false })
  moveConfirmResolver = null
  showMoveConfirm.value = false
  pendingRootPath.value = null
}

function confirmMove() {
  if (moveConfirmResolver)
    moveConfirmResolver({
      proceed: true,
      moveFiles: Boolean(modalMoveFiles.value),
      deleteEmptySource: Boolean(modalDeleteEmpty.value),
    })
  moveConfirmResolver = null
  showMoveConfirm.value = false
  pendingRootPath.value = null
}

import { logger } from '@/utils/logger'
import {
  PhX,
  PhSpinner,
  PhCheck,
  PhPencil,
  PhInfo,
  PhEye,
  PhStar,
  PhFolder,
  PhBooks,
  PhArrowUp,
  PhArrowDown,
} from '@phosphor-icons/vue'

async function handleSave() {
  if (!hasChanges.value) return

  saving.value = true
  try {
    const updates: Record<string, BulkUpdateValue> = {}
    if (formData.value.monitored !== null) {
      updates.monitored = formData.value.monitored
    }
    if (formData.value.qualityProfileId !== null) {
      updates.qualityProfileId = formData.value.qualityProfileId
    }

    const seriesUpdate = buildSeriesUpdate()
    let perIdOverrides: Record<number, Record<string, BulkUpdateValue>> | undefined
    if (seriesUpdate) {
      updates.series = seriesUpdate
      perIdOverrides = buildPerIdOverrides(seriesUpdate)
    }

    let userWantsMove = false
    let userWantsDeleteEmpty = false
    let newRootPath: string | null = null
    if (formData.value.rootChangeEnabled === true) {
      newRootPath = resolvedRootPath.value
      if (!newRootPath) {
        throw new Error('Select a valid destination root before saving.')
      }

      const choice = await askMoveConfirmation(newRootPath)
      if (!choice?.proceed) return
      userWantsMove = Boolean(choice.moveFiles)
      userWantsDeleteEmpty = Boolean(choice.deleteEmptySource)
      updates.rootFolder = newRootPath
    }

    const ids = seriesUpdate ? [...perBookOrder.value] : Array.from(props.selectedIds)
    logger.debug('[BulkEditModal] Preparing bulk update', {
      endpoint: bulkUpdateEndpoint,
      ids,
      updates,
      physicalMove: userWantsMove,
      timestamp: new Date().toISOString(),
    })

    const outcome = await executeBulkEdit(
      {
        ids,
        updates,
        perIdOverrides,
        destinationRoot: newRootPath,
        moveFiles: userWantsMove,
        deleteEmptySource: userWantsDeleteEmpty,
      },
      {
        bulkUpdateAudiobooks: (audiobookIds, metadataUpdates, pathChange, overrides) =>
          apiService.bulkUpdateAudiobooks(audiobookIds, metadataUpdates, pathChange, overrides),
        trackQueuedJob: (job) => moveJobsStore.trackQueuedJob(job),
      },
    )

    results.value = outcome.results
    showResults.value = true
    const successCount = successfulResults.value.length
    const partialCount = partialResults.value.length
    const failureCount = failedResults.value.length
    if (partialCount > 0 || failureCount > 0) {
      toast.error(
        'Bulk update incomplete',
        `${successCount} succeeded, ${partialCount} partially succeeded, and ${failureCount} failed. Review the per-audiobook results.`,
      )
      return
    }

    if (userWantsMove) {
      toast.info(
        'Move jobs queued',
        `Queued ${successCount} move job(s). Files will be moved in the background.`,
      )
    } else {
      toast.success('Bulk update', `Updated ${successCount} audiobook(s)`)
    }

    // A primary-series change moves where the folder pattern points, so offer the Organize
    // preview rather than closing. Nothing on disk has moved.
    if (seriesUpdate && seriesUpdate.mode !== 'addMembership' && successCount > 0) {
      showOrganizePrompt.value = true
      emit('saved')
      return
    }

    emit('saved')
    close()
  } catch (error) {
    try {
      const err = error as Error & { url?: string }
      console.error('[BulkEditModal] Failed to save bulk edits:', {
        name: err?.name,
        message: err?.message,
        stack: err?.stack,
        url: err?.url || bulkUpdateEndpoint,
      })
    } catch {
      console.error('Failed to save bulk edits (minimal):', error)
    }

    let message = 'Failed to save changes. Please try again.'
    try {
      const err = error as Error & { body?: string }
      if (err.body) {
        try {
          const parsed = JSON.parse(err.body)
          if (parsed?.message) message = parsed.message
        } catch {
          message = err.body
        }
      } else if (err.message) {
        message = err.message
      }
    } catch {
      // Keep the generic message when an error cannot be inspected.
    }
    toast.error('Bulk update failed', message)
  } finally {
    saving.value = false
  }
}

function isPartialResult(result: BulkEditItemResult): boolean {
  return !result.success && result.metadataUpdated === true
}

function resultStatusLabel(result: BulkEditItemResult): 'Success' | 'Partial' | 'Failed' {
  if (result.success) return 'Success'
  return isPartialResult(result) ? 'Partial' : 'Failed'
}

function partialResultMessage(result: BulkEditItemResult): string {
  switch (result.pathChangeOutcome) {
    case 'failed':
      return 'Metadata saved; the requested path change failed.'
    case 'not-enqueued':
      return 'Metadata saved; the requested move was not queued.'
    default:
      return 'Metadata saved; the requested path change did not complete.'
  }
}

function close() {
  // Reset results when closing
  results.value = []
  showResults.value = false
  showOrganizePrompt.value = false
  emit('close')
}
</script>

<style scoped>
.modal-overlay {
  position: fixed;
  top: 0;
  left: 0;
  right: 0;
  bottom: 0;
  background-color: rgba(0, 0, 0, 0.75);
  display: flex;
  align-items: center;
  justify-content: center;
  z-index: 1000;
  padding: 2rem;
}

.modal-container {
  background-color: #1e1e1e;
  border-radius: 6px;
  width: 100%;
  max-width: 600px;
  max-height: 90vh;
  display: flex;
  flex-direction: column;
  box-shadow: 0 8px 32px rgba(0, 0, 0, 0.4);
}

.modal-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  padding: 1.5rem 2rem;
  border-bottom: 1px solid #3a3a3a;
}

.modal-header h2 {
  margin: 0;
  color: white;
  font-size: 1.5rem;
  display: flex;
  align-items: center;
  gap: 0.75rem;
}

.btn-close {
  background: none;
  border: none;
  color: #ccc;
  cursor: pointer;
  padding: 0.5rem;
  border-radius: 6px;
  transition: all 0.2s;
  font-size: 1.5rem;
  display: flex;
  align-items: center;
  justify-content: center;
}

.btn-close:hover {
  background-color: #3a3a3a;
  color: white;
}

.modal-body {
  padding: 2rem;
  overflow-y: auto;
  flex: 1;
}

.results-section {
  margin-top: 1.5rem;
  padding: 1rem;
  background: #141414;
  border: 1px solid #2a2a2a;
  border-radius: 6px;
}

.results-list {
  margin-top: 0.75rem;
  display: flex;
  flex-direction: column;
  gap: 0.5rem;
}

.result-item {
  padding: 0.75rem;
  background: #1a1a1a;
  border: 1px solid #2e2e2e;
  border-radius: 6px;
}

.result-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  gap: 1rem;
}

.status.success {
  color: #4caf50;
}

.status.error {
  color: #f44336;
}

.status.partial,
.partial-message {
  color: #ffb74d;
}

.partial-message {
  margin: 0.5rem 0 0;
}

.error-list {
  margin: 0.5rem 0 0 0;
  padding-left: 1.25rem;
  color: #f44336;
}

.info-section {
  display: flex;
  align-items: flex-start;
  gap: 0.75rem;
  padding: 1rem;
  background-color: rgba(52, 152, 219, 0.1);
  border: 1px solid rgba(52, 152, 219, 0.3);
  border-radius: 6px;
  margin-bottom: 2rem;
  color: #3498db;
}

.info-section svg {
  width: 20px;
  height: 20px;
  flex-shrink: 0;
  margin-top: 0.125rem;
  fill: currentColor;
}

.info-section p {
  margin: 0;
  color: #ccc;
  line-height: 1.5;
}

.info-section strong {
  color: white;
}

.edit-form {
  display: flex;
  flex-direction: column;
  gap: 2rem;
}

.form-group {
  display: flex;
  flex-direction: column;
  gap: 0.75rem;
}

.form-label {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  color: white;
  font-weight: 500;
  font-size: 0.95rem;
}

.form-label svg {
  color: var(--brand-500);
  width: 18px;
  height: 18px;
}

.radio-group {
  display: flex;
  flex-direction: column;
  gap: 0.75rem;
}

.radio-label {
  display: flex;
  align-items: center;
  gap: 0.75rem;
  padding: 0.75rem 1rem;
  background-color: #2a2a2a;
  border: 1px solid #3a3a3a;
  border-radius: 6px;
  cursor: pointer;
  transition: all 0.2s;
  color: #ccc;
}

.radio-label:hover {
  background-color: #333;
  border-color: var(--brand-500);
}

.radio-label input[type='radio'] {
  width: 18px;
  height: 18px;
  cursor: pointer;
  accent-color: var(--brand-500);
}

.radio-label input[type='radio']:checked + span {
  color: white;
  font-weight: 500;
}

.form-select {
  padding: 0.75rem 1rem;
  background-color: #2a2a2a;
  border: 1px solid #3a3a3a;
  border-radius: 6px;
  color: white;
  font-size: 0.95rem;
  cursor: pointer;
  transition: all 0.2s;
}

.form-select:hover {
  border-color: #555;
}

.form-select:focus {
  outline: none;
  border-color: var(--brand-focus);
  box-shadow: 0 0 0 3px rgba(var(--brand-rgb), 0.1);
}

.form-input {
  padding: 0.75rem 1rem;
  background-color: #2a2a2a;
  border: 1px solid #3a3a3a;
  border-radius: 6px;
  color: white;
  font-size: 0.95rem;
  width: 100%;
}

.form-input:focus {
  outline: none;
  border-color: var(--brand-focus);
  box-shadow: 0 0 0 3px rgba(var(--brand-rgb), 0.1);
}

.series-block {
  display: flex;
  flex-direction: column;
  gap: 0.5rem;
}

.series-sublabel {
  color: #ccc;
  font-size: 0.85rem;
}

.series-suggestions {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: 0.25rem;
  max-height: 12rem;
  overflow-y: auto;
}

.series-suggestion {
  display: flex;
  justify-content: space-between;
  gap: 0.75rem;
  width: 100%;
  padding: 0.5rem 0.75rem;
  background: #252526;
  border: 1px solid #333;
  border-radius: 6px;
  color: #ddd;
  cursor: pointer;
  text-align: left;
}

.series-suggestion:hover {
  border-color: var(--brand-500);
}

.series-suggestion-meta {
  color: #999;
  font-size: 0.8rem;
  white-space: nowrap;
}

.per-book-actions {
  display: flex;
  gap: 0.5rem;
  flex-wrap: wrap;
  margin-bottom: 0.5rem;
}

.per-book-list {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: 0.5rem;
}

.per-book-row {
  display: flex;
  align-items: center;
  gap: 0.75rem;
  padding: 0.5rem;
  background: #1a1a1a;
  border: 1px solid #2e2e2e;
  border-radius: 6px;
}

.per-book-cover {
  width: 32px;
  height: 48px;
  object-fit: cover;
  border-radius: 4px;
}

.per-book-details {
  display: flex;
  flex-direction: column;
  flex: 1;
  min-width: 0;
}

.per-book-title {
  color: white;
  font-size: 0.9rem;
}

.per-book-series {
  color: #999;
  font-size: 0.8rem;
}

.per-book-number {
  width: 5rem;
  padding: 0.4rem 0.5rem;
}

.per-book-reorder {
  display: flex;
  flex-direction: column;
  gap: 0.25rem;
}

.per-book-paging {
  display: flex;
  align-items: center;
  gap: 0.75rem;
  margin-top: 0.75rem;
  color: #999;
}

.result-series {
  margin: 0.5rem 0 0;
  color: #ccc;
}

.organize-prompt {
  margin-top: 1rem;
  padding: 0.75rem;
  background: #1a1a1a;
  border: 1px solid #2e2e2e;
  border-radius: 6px;
  color: #ccc;
}

.organize-actions {
  display: flex;
  gap: 0.5rem;
  margin-top: 0.75rem;
  flex-wrap: wrap;
}

.help-text {
  /* rely on centralized modal spacing; use compact variant if needed */
  font-size: 0.85rem;
  color: #999;
  line-height: 1.4;
}

/* modal-footer centralized in src/assets/modals.css; keep local spacing adjustment */
.modal-footer {
  margin-top: 1rem;
}

/* Base .btn styles centralized in src/assets/modals.css; keeping this file's spacing adjustments only */
.btn:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}

/* Button color variants are centralized in `src/assets/modals.css` - use `.btn` / `.btn-primary`. */

.btn i.ph-spin {
  animation: spin 1s linear infinite;
}

/* @keyframes spin is centralized in src/assets/main.css */

/* Move confirmation modal styles — now aligned with centralized modal styles */
.confirm-overlay.separate-modal {
  position: fixed;
  inset: 0;
  background: rgba(0, 0, 0, 0.85);
  display: flex;
  align-items: center;
  justify-content: center;
  z-index: 1000;
  padding: 1rem;
}

.confirm-dialog {
  background: #2a2a2a;
  border-radius: 6px;
  box-shadow: 0 10px 40px rgba(0, 0, 0, 0.5);
  max-width: 700px; /* standardized to modal-md */
  width: 100%;
  max-height: 90vh;
  overflow-y: auto;
  border: 1px solid #444;
}

.confirm-header {
  display: flex;
  align-items: center;
  gap: 0.75rem;
  padding: 1.25rem 1.5rem 1rem;
  border-bottom: 1px solid #444;
  margin-bottom: 0;
}
.confirm-header i {
  font-size: 1.5rem;
  color: var(--brand-500);
}

.confirm-header h3 {
  margin: 0;
  font-size: 1.25rem;
  font-weight: 500;
  color: #ffffff;
}

.confirm-body {
  padding: 1.5rem;
  display: flex;
  flex-direction: column;
  gap: 1.5rem;
}

.confirm-description p {
  margin: 0;
  color: #cccccc;
  line-height: 1.5;
}

.path-comparison {
  display: flex;
  flex-direction: column;
  gap: 1rem;
  background: #252526;
  border-radius: 8px;
  padding: 1rem;
  border: 1px solid #333;
}

.path-section {
  display: flex;
  flex-direction: column;
  gap: 0.5rem;
}

.path-label {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  font-weight: 500;
  color: #ffffff;
  font-size: 0.9rem;
}

.path-label i {
  color: var(--brand-500);
  font-size: 1rem;
}

.path-display {
  background: #1e1e1e;
  border: 1px solid #333;
  border-radius: 6px;
  padding: 0.75rem;
  font-family: 'Consolas', 'Monaco', 'Courier New', monospace;
  font-size: 0.85rem;
  color: #cccccc;
  word-break: break-all;
  line-height: 1.4;
}

.path-display code {
  background: transparent;
  color: inherit;
  padding: 0;
  border: none;
  font-family: inherit;
}

.confirm-options {
  display: flex;
  flex-direction: column;
  gap: 1rem;
}

.checkbox-row {
  display: flex;
  align-items: flex-start;
  gap: 0.75rem;
  padding: 0.75rem;
  background: #252526;
  border-radius: 8px;
  border: 1px solid #333;
  transition: all 0.2s ease;
}

.checkbox-row:hover {
  background: #2d2d30;
  border-color: var(--brand-500);
}

.checkbox-row label {
  display: flex;
  align-items: flex-start;
  gap: 0.75rem;
  cursor: pointer;
  width: 100%;
  margin: 0;
}

.checkbox-row input[type='checkbox'] {
  margin-top: 0.125rem;
  width: 1rem;
  height: 1rem;
  accent-color: var(--brand-500);
  cursor: pointer;
}

.checkbox-content {
  display: flex;
  flex-direction: column;
  gap: 0.25rem;
  flex: 1;
}

.checkbox-title {
  font-weight: 500;
  color: #ffffff;
  font-size: 0.95rem;
}

.checkbox-content small {
  color: #aaaaaa;
  font-size: 0.8rem;
  line-height: 1.3;
}

.confirm-actions {
  display: flex;
  gap: 0.75rem;
  padding: 1rem 1.5rem 1.5rem;
  border-top: 1px solid #333;
  justify-content: flex-end;
  flex-wrap: wrap;
}

.confirm-actions .btn {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  padding: 0.75rem 1.25rem;
  border-radius: 6px;
  font-weight: 500;
  font-size: 0.9rem;
  transition: all 0.2s ease;
  border: 1px solid transparent;
  cursor: pointer;
  min-width: fit-content;
}

.confirm-actions .btn:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}

/* Local confirm action button variants are centralized in `src/assets/modals.css`. Use `.btn-primary` for confirm actions. */

/* Mobile responsive adjustments */
@media (max-width: 640px) {
  .confirm-overlay.separate-modal {
    padding: 0.5rem;
  }

  .confirm-dialog {
    max-width: 100%;
    margin: 0;
  }

  .confirm-header {
    padding: 1rem 1rem 0.75rem;
  }

  .confirm-header h3 {
    font-size: 1.1rem;
  }

  .confirm-body {
    padding: 1rem;
    gap: 1rem;
  }

  .path-comparison {
    padding: 0.75rem;
  }

  .path-display {
    padding: 0.5rem;
    font-size: 0.8rem;
  }

  .checkbox-row {
    padding: 0.5rem;
  }

  .confirm-actions {
    padding: 0.75rem 1rem 1rem;
    gap: 0.5rem;
  }

  .confirm-actions .btn {
    padding: 0.625rem 1rem;
    font-size: 0.85rem;
    flex: 1;
    justify-content: center;
  }
}
</style>
