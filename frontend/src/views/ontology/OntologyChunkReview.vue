<template>
  <div class="ontology-chunk-review">
    <!-- Top bar -->
    <div class="review-topbar">
      <t-button variant="text" @click="goBack">
        <template #icon><t-icon name="chevron-left" /></template>
        {{ $t('ontologyReview.backToQueue') }}
      </t-button>
      <div class="topbar-info">
        <span class="chunk-id-label">{{ $t('ontologyReview.chunkId') }}:</span>
        <code class="chunk-id-mono">{{ chunkId }}</code>
        <t-tag v-if="reviewStatus" :theme="statusTheme(reviewStatus)" variant="light-outline" size="small">
          {{ statusLabel(reviewStatus) }}
        </t-tag>
      </div>
      <t-button
        v-if="canApproveAll"
        theme="success"
        variant="outline"
        :loading="approving"
        @click="handleApproveAll"
      >
        <template #icon><t-icon name="check-circle" /></template>
        {{ $t('ontologyReview.approveAll') }}
      </t-button>
    </div>

    <div v-if="loading" class="loading-state">
      <t-loading size="large" :text="$t('common.loading')" />
    </div>

    <template v-else-if="chunk">
      <!-- Split layout: left = source text, right = ontology review cards -->
      <div class="review-layout">
        <!-- LEFT: Source text with evidence highlights -->
        <div class="review-left">
          <div class="panel-header">{{ $t('ontologyReview.sourceText') }}</div>
          <div class="evidence-text" v-html="renderedEvidence"></div>
        </div>

        <!-- RIGHT: Ontology items -->
        <div class="review-right">
          <!-- Classes -->
          <div v-if="ontology.classes?.length" class="review-section">
            <div class="section-header">
              <t-icon name="layers" size="16px" />
              <span>{{ $t('ontologyReview.classes') }} ({{ ontology.classes.length }})</span>
            </div>
            <div v-for="(cls, idx) in ontology.classes" :key="'class-' + idx" class="review-card" :class="cardClass(cls.id, 'class')">
              <div class="card-header">
                <span class="card-title">{{ cls.id }}</span>
                <t-tag v-if="cls.label && cls.label !== cls.id" variant="text" size="small">{{ cls.label }}</t-tag>
                <span class="card-evidence" v-html="renderHighlight(cls.evidence)"></span>
              </div>
              <div class="card-details">
                <span v-if="cls.subClassOf" class="card-prop">
                  subClassOf <t-tag variant="light" size="small">{{ cls.subClassOf }}</t-tag>
                </span>
                <span v-if="cls.disjointWith?.length" class="card-prop">
                  disjointWith
                  <t-tag v-for="d in cls.disjointWith" :key="d" variant="light" size="small" class="disjoint-tag">{{ d }}</t-tag>
                </span>
              </div>
              <div class="card-actions">
                <t-button variant="text" size="small" theme="success" @click="doAction('accept', 'class', cls.id)">
                  <t-icon name="check" size="14px" />{{ $t('ontologyReview.accept') }}
                </t-button>
                <t-button variant="text" size="small" theme="danger" @click="doAction('reject', 'class', cls.id)">
                  <t-icon name="close" size="14px" />{{ $t('ontologyReview.reject') }}
                </t-button>
                <t-button variant="text" size="small" @click="startEdit('class', cls.id, cls)">
                  <t-icon name="edit" size="14px" />{{ $t('ontologyReview.edit') }}
                </t-button>
              </div>
            </div>
          </div>

          <!-- Properties -->
          <div v-if="ontology.properties?.length" class="review-section">
            <div class="section-header">
              <t-icon name="link" size="16px" />
              <span>{{ $t('ontologyReview.properties') }} ({{ ontology.properties.length }})</span>
            </div>
            <div v-for="(prop, idx) in ontology.properties" :key="'prop-' + idx" class="review-card" :class="cardClass(prop.id, 'property')">
              <div class="card-header">
                <span class="card-title">{{ prop.id }}</span>
                <span class="card-evidence" v-html="renderHighlight(prop.evidence)"></span>
              </div>
              <div class="card-details">
                <span class="card-prop">domain: {{ prop.domain || '—' }}</span>
                <span class="card-prop">range: {{ prop.range || '—' }}</span>
                <span v-if="prop.characteristics?.length" class="card-prop">
                  <t-tag v-for="ch in prop.characteristics" :key="ch" variant="light" size="small">{{ ch }}</t-tag>
                </span>
                <span v-if="prop.inverseOf" class="card-prop">
                  inverseOf: <t-tag variant="light" size="small">{{ prop.inverseOf }}</t-tag>
                </span>
              </div>
              <div class="card-actions">
                <t-button variant="text" size="small" theme="success" @click="doAction('accept', 'property', prop.id)">
                  <t-icon name="check" size="14px" />{{ $t('ontologyReview.accept') }}
                </t-button>
                <t-button variant="text" size="small" theme="danger" @click="doAction('reject', 'property', prop.id)">
                  <t-icon name="close" size="14px" />{{ $t('ontologyReview.reject') }}
                </t-button>
                <t-button variant="text" size="small" @click="startEdit('property', prop.id, prop)">
                  <t-icon name="edit" size="14px" />{{ $t('ontologyReview.edit') }}
                </t-button>
              </div>
            </div>
          </div>

          <!-- Shapes -->
          <div v-if="ontology.shapes?.length" class="review-section">
            <div class="section-header">
              <t-icon name="secured" size="16px" />
              <span>{{ $t('ontologyReview.shapes') }} ({{ ontology.shapes.length }})</span>
            </div>
            <div v-for="(shape, idx) in ontology.shapes" :key="'shape-' + idx" class="review-card">
              <div class="card-header">
                <span class="card-title">{{ shape.target_class }}</span>
                <span class="card-evidence" v-html="renderHighlight(shape.evidence)"></span>
              </div>
              <div class="card-details">
                <div v-for="(c, ci) in shape.constraints" :key="ci" class="constraint-row">
                  <t-tag variant="light" size="small">{{ c.property }}</t-tag>
                  <span v-if="c.min_count != null">min: {{ c.min_count }}</span>
                  <span v-if="c.max_count != null">max: {{ c.max_count }}</span>
                  <span v-if="c.datatype">{{ c.datatype }}</span>
                  <span v-if="c.in_values?.length">in: [{{ c.in_values.join(', ') }}]</span>
                </div>
              </div>
              <div class="card-actions">
                <t-button variant="text" size="small" theme="success" @click="doAction('accept', 'shape', shape.target_class)">
                  <t-icon name="check" size="14px" />{{ $t('ontologyReview.accept') }}
                </t-button>
                <t-button variant="text" size="small" theme="danger" @click="doAction('reject', 'shape', shape.target_class)">
                  <t-icon name="close" size="14px" />{{ $t('ontologyReview.reject') }}
                </t-button>
              </div>
            </div>
          </div>

          <!-- Axioms -->
          <div v-if="ontology.axioms?.length" class="review-section">
            <div class="section-header">
              <t-icon name="root-list" size="16px" />
              <span>{{ $t('ontologyReview.axioms') }} ({{ ontology.axioms.length }})</span>
            </div>
            <div v-for="(axiom, idx) in ontology.axioms" :key="'axiom-' + idx" class="review-card">
              <div class="card-header">
                <span class="card-title">Axiom #{{ idx + 1 }}</span>
              </div>
              <div class="card-details">
                <p class="axiom-statement">{{ axiom.statement }}</p>
                <p class="axiom-evidence" v-html="renderHighlight(axiom.evidence)"></p>
              </div>
              <div class="card-actions">
                <t-button variant="text" size="small" theme="success" @click="doAction('accept', 'axiom', `axiom_${idx}`)">
                  <t-icon name="check" size="14px" />{{ $t('ontologyReview.accept') }}
                </t-button>
                <t-button variant="text" size="small" theme="danger" @click="doAction('reject', 'axiom', `axiom_${idx}`)">
                  <t-icon name="close" size="14px" />{{ $t('ontologyReview.reject') }}
                </t-button>
              </div>
            </div>
          </div>

          <!-- Empty ontology -->
          <div v-if="isEmpty" class="empty-ontology">
            <t-icon name="file-unknown" size="32px" />
            <p>{{ $t('ontologyReview.emptyOntology') }}</p>
          </div>
        </div>
      </div>
    </template>
  </div>
</template>

<script setup lang="ts">
import { ref, computed, onMounted } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { MessagePlugin, DialogPlugin } from 'tdesign-vue-next'
import { useI18n } from 'vue-i18n'
import {
  getReviewChunkDetail,
  applyReviewAction,
  approveAllReview,
  type OntologyReviewChunkDetail,
  type OntologyReviewStatus,
  type OntologyReviewActionType,
  type OntologyReviewTargetKind,
} from '@/api/ontology/index'

const route = useRoute()
const router = useRouter()
const { t } = useI18n()

const kbId = computed(() => (route.params as any).kbId as string || '')
const chunkId = computed(() => (route.params as any).chunkId as string || '')

const loading = ref(true)
const approving = ref(false)
const detail = ref<OntologyReviewChunkDetail | null>(null)
const actionStates = ref<Record<string, string>>({})  // "targetId" → "accepted" | "rejected"

const chunk = computed(() => detail.value?.chunk || null)
const ontology = computed(() => {
  const raw = chunk.value?.ontology_json || {}
  if (typeof raw === 'string') {
    try { return JSON.parse(raw) } catch { return {} }
  }
  return raw || {}
})
const isEmpty = computed(() =>
  !ontology.value.classes?.length &&
  !ontology.value.properties?.length &&
  !ontology.value.shapes?.length &&
  !ontology.value.axioms?.length
)
const reviewStatus = computed(() => chunk.value?.ontology_review_status as OntologyReviewStatus | undefined)
const canApproveAll = computed(() => !isEmpty.value && reviewStatus.value !== 'approved')

// Evidence spans for precise highlighting
const evidenceSpans = computed(() => detail.value?.evidence_spans || [])

const sourceText = computed(() => chunk.value?.content || '')

const renderedEvidence = computed(() => {
  let text = sourceText.value
  if (!text || !evidenceSpans.value.length) return escapeHtml(text)

  // Sort spans by start_offset descending so we insert markers from back to front
  const sorted = [...evidenceSpans.value]
    .filter(s => s.start_offset >= 0)
    .sort((a, b) => b.start_offset - a.start_offset)

  for (const span of sorted) {
    const before = escapeHtml(text.slice(0, span.start_offset))
    const middle = escapeHtml(text.slice(span.start_offset, span.end_offset))
    const after = escapeHtml(text.slice(span.end_offset))
    text = before +
      `<mark class="evidence-highlight" title="${escapeAttr(span.target_id)} (${span.target_kind})">${middle}</mark>` +
      after
  }

  return text.replace(/\n/g, '<br>')
})

function escapeHtml(s: string): string {
  return s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
}
function escapeAttr(s: string): string {
  return s.replace(/&/g, '&amp;').replace(/"/g, '&quot;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
}

function renderHighlight(evidence: string): string {
  if (!evidence) return ''
  return `<mark class="inline-evidence">${escapeHtml(evidence)}</mark>`
}

function cardClass(targetId: string, kind: string): string {
  const state = actionStates.value[`${kind}:${targetId}`]
  if (state === 'accepted') return 'card-accepted'
  if (state === 'rejected') return 'card-rejected'
  return ''
}

async function doAction(action: OntologyReviewActionType, kind: OntologyReviewTargetKind, targetId: string) {
  const key = `${kind}:${targetId}`
  try {
    const result = await applyReviewAction(chunkId.value, {
      action,
      target_kind: kind,
      target_id: targetId,
    })
    // Update local state
    actionStates.value[key] = action === 'accept' ? 'accepted' : action === 'reject' ? 'rejected' : 'accepted'
    // Refresh detail
    detail.value = result
    MessagePlugin.success(t('ontologyReview.actionApplied'))
  } catch (err: any) {
    MessagePlugin.error(err?.message || t('common.error'))
  }
}

function startEdit(kind: string, targetId: string, item: any) {
  // MVP: open a simple inline edit using a prompt-based approach.
  // For now, we use a dialog that lets the reviewer type a corrected JSON snippet.
  const jsonStr = JSON.stringify(item, null, 2)
  DialogPlugin.confirm({
    header: t('ontologyReview.editItem', { id: targetId }),
    body: () => {
      // Simple text area approach for MVP
      const div = document.createElement('div')
      div.innerHTML = `<p style="margin-bottom:8px;font-size:13px;color:var(--td-text-color-secondary)">${t('ontologyReview.editHint')}</p>`
      const ta = document.createElement('textarea')
      ta.style.cssText = 'width:100%;min-height:120px;font-family:monospace;font-size:12px'
      ta.value = jsonStr
      div.appendChild(ta)
      return div
    },
    onConfirm: async () => {
      try {
        const textarea = document.querySelector('.t-dialog textarea') as HTMLTextAreaElement
        if (!textarea) return
        const edited = JSON.parse(textarea.value)
        await applyReviewAction(chunkId.value, {
          action: 'edit',
          target_kind: kind as OntologyReviewTargetKind,
          target_id: targetId,
          reviewed_ontology: edited,
        })
        actionStates.value[`${kind}:${targetId}`] = 'accepted'
        MessagePlugin.success(t('ontologyReview.actionApplied'))
      } catch (err: any) {
        MessagePlugin.error(err?.message || t('common.error'))
      }
    },
  })
}

async function handleApproveAll() {
  const confirmed = await new Promise<boolean>(resolve => {
    DialogPlugin.confirm({
      header: t('ontologyReview.approveAll'),
      body: t('ontologyReview.approveAllConfirm'),
      onConfirm: () => resolve(true),
      onCancel: () => resolve(false),
    })
  })
  if (!confirmed) return

  approving.value = true
  try {
    const result = await approveAllReview(chunkId.value)
    detail.value = result
    // Mark all items as accepted
    for (const cls of ontology.value.classes || []) actionStates.value[`class:${cls.id}`] = 'accepted'
    for (const prop of ontology.value.properties || []) actionStates.value[`property:${prop.id}`] = 'accepted'
    for (const shape of ontology.value.shapes || []) actionStates.value[`shape:${shape.target_class}`] = 'accepted'
    for (let i = 0; i < (ontology.value.axioms || []).length; i++) actionStates.value[`axiom:axiom_${i}`] = 'accepted'
    MessagePlugin.success(t('ontologyReview.approveAllSuccess'))
  } catch (err: any) {
    MessagePlugin.error(err?.message || t('common.error'))
  } finally {
    approving.value = false
  }
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

async function loadDetail() {
  loading.value = true
  try {
    detail.value = await getReviewChunkDetail(chunkId.value)
  } catch (err: any) {
    MessagePlugin.error(err?.message || t('common.error'))
  } finally {
    loading.value = false
  }
}

function goBack() {
  router.push(`/platform/knowledge-bases/${kbId.value}/ontology-review`)
}

onMounted(loadDetail)
</script>

<style scoped lang="less">
.ontology-chunk-review {
  height: 100vh;
  display: flex;
  flex-direction: column;
  overflow: hidden;

  .review-topbar {
    display: flex;
    align-items: center;
    gap: 16px;
    padding: 10px 24px;
    border-bottom: 1px solid var(--td-component-stroke);
    background: var(--td-bg-color-container);
    flex-shrink: 0;

    .topbar-info {
      display: flex;
      align-items: center;
      gap: 8px;
      flex: 1;

      .chunk-id-label {
        font-size: 13px;
        color: var(--td-text-color-secondary);
      }
      .chunk-id-mono {
        font-family: 'SF Mono', 'Menlo', monospace;
        font-size: 12px;
        padding: 2px 6px;
        background: var(--td-bg-color-secondarycontainer);
        border-radius: 4px;
      }
    }
  }

  .loading-state {
    display: flex;
    justify-content: center;
    align-items: center;
    flex: 1;
  }

  .review-layout {
    display: flex;
    flex: 1;
    overflow: hidden;

    .review-left {
      flex: 1;
      overflow-y: auto;
      padding: 20px;
      border-right: 1px solid var(--td-component-stroke);
      background: var(--td-bg-color-page);

      .panel-header {
        font-weight: 600;
        font-size: 14px;
        margin-bottom: 12px;
        color: var(--td-text-color-primary);
      }

      .evidence-text {
        font-size: 14px;
        line-height: 1.8;
        color: var(--td-text-color-primary);

        :deep(.evidence-highlight) {
          background: #fff3b0;
          padding: 1px 2px;
          border-radius: 2px;
          cursor: help;

          &:hover {
            background: #ffe066;
          }
        }
      }
    }

    .review-right {
      flex: 1;
      overflow-y: auto;
      padding: 20px;
      background: var(--td-bg-color-container);

      .review-section {
        margin-bottom: 24px;

        .section-header {
          display: flex;
          align-items: center;
          gap: 8px;
          font-weight: 600;
          font-size: 14px;
          margin-bottom: 10px;
          padding-bottom: 8px;
          border-bottom: 1px solid var(--td-component-stroke);
        }
      }

      .review-card {
        border: 1px solid var(--td-component-stroke);
        border-radius: 6px;
        padding: 12px;
        margin-bottom: 8px;
        background: var(--td-bg-color-container);
        transition: border-color .2s;

        &.card-accepted {
          border-color: var(--td-success-color-3);
          background: var(--td-success-color-1);
        }
        &.card-rejected {
          border-color: var(--td-error-color-3);
          background: var(--td-error-color-1);
          .card-title { text-decoration: line-through; }
        }

        .card-header {
          display: flex;
          align-items: center;
          gap: 8px;
          margin-bottom: 6px;
          flex-wrap: wrap;

          .card-title {
            font-weight: 600;
            font-size: 14px;
            font-family: 'SF Mono', monospace;
          }
          .card-evidence {
            font-size: 12px;
            color: var(--td-text-color-secondary);
            font-style: italic;

            :deep(.inline-evidence) {
              background: #e8f4fd;
              padding: 1px 3px;
              border-radius: 2px;
            }
          }
        }

        .card-details {
          margin-bottom: 8px;

          .card-prop {
            display: inline-flex;
            align-items: center;
            gap: 4px;
            font-size: 13px;
            color: var(--td-text-color-secondary);
            margin-right: 12px;
            margin-bottom: 4px;

            .disjoint-tag { margin-left: 2px; }
          }

          .constraint-row {
            display: flex;
            align-items: center;
            gap: 8px;
            font-size: 13px;
            color: var(--td-text-color-secondary);
            margin-bottom: 4px;
          }

          .axiom-statement {
            font-size: 13px;
            color: var(--td-text-color-primary);
            margin: 0 0 4px;
          }
          .axiom-evidence {
            font-size: 12px;
            margin: 0;
          }
        }

        .card-actions {
          display: flex;
          gap: 4px;

          :deep(.t-button) { font-size: 12px; }
        }
      }
    }

    .empty-ontology {
      text-align: center;
      padding: 32px;
      color: var(--td-text-color-disabled);

      p { margin: 8px 0 0; font-size: 14px; }
    }
  }
}
</style>
