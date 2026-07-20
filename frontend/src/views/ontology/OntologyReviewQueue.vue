<template>
  <div class="ontology-review-queue">
    <!-- Header -->
    <div class="queue-header">
      <div class="header-left">
        <t-button variant="text" @click="goBack">
          <template #icon><t-icon name="chevron-left" /></template>
          {{ $t('ontologyReview.backToKB') }}
        </t-button>
        <h2>{{ $t('ontologyReview.queueTitle') }}</h2>
        <t-tag v-if="kbName" variant="light">{{ kbName }}</t-tag>
      </div>
      <div class="header-right">
        <t-select
          v-model="statusFilter"
          :options="statusOptions"
          :placeholder="$t('ontologyReview.filterStatus')"
          clearable
          size="small"
          style="width: 140px"
          @change="loadQueue(1)"
        />
        <t-button variant="outline" size="small" :loading="loading" @click="loadQueue(currentPage)">
          <template #icon><t-icon name="refresh" /></template>
        </t-button>
      </div>
    </div>

    <!-- Queue Table -->
    <t-table
      v-if="entries.length > 0 || loading"
      :data="entries"
      :columns="columns"
      :loading="loading"
      row-key="id"
      hover
      :pagination="pagination"
      @page-change="onPageChange"
    >
      <template #priority="{ row }">
        <t-tag :theme="priorityTheme(row.priority)" variant="light" size="small">
          {{ row.priority }}
        </t-tag>
      </template>
      <template #chunk_id="{ row }">
        <span class="chunk-id-mono">{{ row.chunk_id.slice(0, 12) }}…</span>
      </template>
      <template #knowledge_title="{ row }">
        <span class="doc-title">{{ row.knowledge_title || '—' }}</span>
      </template>
      <template #content_preview="{ row }">
        <span class="content-preview" :title="row.content_preview">{{
          row.content_preview.length > 80 ? row.content_preview.slice(0, 80) + '…' : row.content_preview
        }}</span>
      </template>
      <template #priority_reason="{ row }">
        <t-tooltip v-if="row.priority_reason" :content="row.priority_reason" placement="top">
          <t-icon name="info-circle" size="14px" class="reason-icon" />
        </t-tooltip>
        <span v-else class="text-muted">—</span>
      </template>
      <template #status="{ row }">
        <t-tag :theme="statusTheme(row.status)" variant="light-outline" size="small">
          {{ statusLabel(row.status) }}
        </t-tag>
      </template>
      <template #updated_at="{ row }">
        <span class="text-muted text-sm">{{ formatDate(row.updated_at) }}</span>
      </template>
      <template #action="{ row }">
        <t-button size="small" variant="outline" @click="goToChunk(row.chunk_id)">
          {{ $t('ontologyReview.review') }}
        </t-button>
      </template>
    </t-table>

    <!-- Empty State -->
    <div v-else-if="!loading" class="empty-state">
      <t-icon name="file-unknown" size="48px" class="empty-icon" />
      <p class="empty-title">{{ $t('ontologyReview.emptyQueueTitle') }}</p>
      <p class="empty-desc">{{ $t('ontologyReview.emptyQueueDesc') }}</p>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, computed, onMounted } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { MessagePlugin } from 'tdesign-vue-next'
import { useI18n } from 'vue-i18n'
import { listReviewQueue, type OntologyReviewQueueEntry, type OntologyReviewStatus } from '@/api/ontology/index'
import { listKnowledgeBases } from '@/api/knowledge-base/index'

const route = useRoute()
const router = useRouter()
const { t } = useI18n()

const kbId = computed(() => (route.params as any).kbId as string || '')
const kbName = ref('')

const loading = ref(false)
const entries = ref<OntologyReviewQueueEntry[]>([])
const currentPage = ref(1)
const pageSize = ref(20)
const total = ref(0)
const statusFilter = ref<string>('')

const statusOptions = [
  { label: t('ontologyReview.statusPending'), value: 'pending' },
  { label: t('ontologyReview.statusInReview'), value: 'in_review' },
  { label: t('ontologyReview.statusApproved'), value: 'approved' },
  { label: t('ontologyReview.statusRejected'), value: 'rejected' },
]

const columns = [
  { colKey: 'priority', title: t('ontologyReview.priority'), width: 70 },
  { colKey: 'chunk_id', title: t('ontologyReview.chunkId'), width: 130 },
  { colKey: 'knowledge_title', title: t('ontologyReview.document'), ellipsis: true },
  { colKey: 'content_preview', title: t('ontologyReview.preview'), ellipsis: true },
  { colKey: 'priority_reason', title: t('ontologyReview.reason'), width: 50, align: 'center' as const },
  { colKey: 'status', title: t('ontologyReview.status'), width: 90 },
  { colKey: 'updated_at', title: t('ontologyReview.updatedAt'), width: 110 },
  { colKey: 'action', title: t('ontologyReview.action'), width: 80, fixed: 'right' as const },
]

const pagination = computed(() => ({
  current: currentPage.value,
  pageSize: pageSize.value,
  total: total.value,
  showJumper: true,
  pageSizeOptions: [10, 20, 50],
}))

function priorityTheme(priority: number): string {
  if (priority >= 80) return 'danger'
  if (priority >= 60) return 'warning'
  if (priority >= 40) return 'primary'
  return 'default'
}

function statusTheme(status: OntologyReviewStatus): string {
  switch (status) {
    case 'approved': return 'success'
    case 'rejected': return 'danger'
    case 'in_review': return 'warning'
    default: return 'default'
  }
}

function statusLabel(status: OntologyReviewStatus): string {
  switch (status) {
    case 'pending': return t('ontologyReview.statusPending')
    case 'in_review': return t('ontologyReview.statusInReview')
    case 'approved': return t('ontologyReview.statusApproved')
    case 'rejected': return t('ontologyReview.statusRejected')
    case 'no_review': return t('ontologyReview.statusNoReview')
    default: return status
  }
}

function formatDate(iso: string): string {
  if (!iso) return '—'
  const d = new Date(iso)
  return d.toLocaleDateString('zh-CN', { month: '2-digit', day: '2-digit' }) +
    ' ' + d.toLocaleTimeString('zh-CN', { hour: '2-digit', minute: '2-digit' })
}

async function loadQueue(page: number) {
  loading.value = true
  try {
    currentPage.value = page
    const result = await listReviewQueue(kbId.value, {
      status: statusFilter.value || undefined,
      page,
      page_size: pageSize.value,
    })
    entries.value = result.items
    total.value = result.total
  } catch (err: any) {
    MessagePlugin.error(err?.message || t('common.error'))
  } finally {
    loading.value = false
  }
}

function onPageChange(pageInfo: { current: number; pageSize: number }) {
  pageSize.value = pageInfo.pageSize
  loadQueue(pageInfo.current)
}

function goToChunk(chunkId: string) {
  router.push(`/platform/knowledge-bases/${kbId.value}/ontology-review/${chunkId}`)
}

async function loadKBName() {
  try {
    const result = await listKnowledgeBases()
    const kb = (result as any)?.data?.find?.((b: any) => b.id === kbId.value) ??
                (Array.isArray(result) ? result.find((b: any) => b.id === kbId.value) : null)
    if (kb) kbName.value = kb.name || ''
  } catch { /* ignore, name is best-effort */ }
}

function goBack() {
  router.push(`/platform/knowledge-bases/${kbId.value}`)
}

onMounted(() => {
  loadKBName()
  loadQueue(1)
})
</script>

<style scoped lang="less">
.ontology-review-queue {
  padding: 24px;
  max-width: 1200px;
  margin: 0 auto;

  .queue-header {
    display: flex;
    align-items: center;
    justify-content: space-between;
    margin-bottom: 20px;

    .header-left {
      display: flex;
      align-items: center;
      gap: 12px;

      h2 { margin: 0; font-size: 1.25rem; font-weight: 600; }
    }

    .header-right {
      display: flex;
      align-items: center;
      gap: 8px;
    }
  }

  .chunk-id-mono {
    font-family: 'SF Mono', 'Menlo', monospace;
    font-size: 12px;
    color: var(--td-text-color-secondary);
  }

  .doc-title {
    font-weight: 500;
  }

  .content-preview {
    font-size: 13px;
    color: var(--td-text-color-placeholder);
  }

  .reason-icon {
    color: var(--td-warning-color);
    cursor: help;
  }

  .text-muted { color: var(--td-text-color-placeholder); }
  .text-sm { font-size: 12px; }

  .empty-state {
    text-align: center;
    padding: 64px 24px;

    .empty-icon {
      color: var(--td-text-color-disabled);
      margin-bottom: 16px;
    }
    .empty-title {
      font-size: 1.1rem;
      font-weight: 500;
      margin: 0 0 8px;
    }
    .empty-desc {
      font-size: 14px;
      color: var(--td-text-color-secondary);
      margin: 0;
    }
  }
}
</style>
