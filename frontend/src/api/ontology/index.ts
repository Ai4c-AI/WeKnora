// Ontology review API client. Provides typed access to the expert review
// loop endpoints: queue listing, chunk detail, single action, and approve-all.

import { get, post } from '../../utils/request'

// ---- Types (mirrors server DTOs) ----

export interface OntologyReviewQueueEntry {
  id: number
  tenant_id: number
  knowledge_base_id: string
  chunk_id: string
  knowledge_title: string
  content_preview: string
  priority: number
  priority_reason: string
  assigned_to: number | null
  status: OntologyReviewStatus
  created_at: string
  updated_at: string
}

export type OntologyReviewStatus = 'pending' | 'in_review' | 'approved' | 'rejected' | 'no_review'
export type OntologyReviewActionType = 'accept' | 'reject' | 'edit' | 'approve_all'
export type OntologyReviewTargetKind = 'class' | 'property' | 'shape' | 'alias' | 'axiom'

export interface OntologyReviewQueuePage {
  items: OntologyReviewQueueEntry[]
  page: number
  page_size: number
  total: number
  has_more: boolean
}

export interface EvidenceSpan {
  target_id: string
  target_kind: string
  evidence: string
  start_offset: number
  end_offset: number
}

export interface OntologyReviewChunkDetail {
  chunk: Record<string, any>
  evidence_spans: EvidenceSpan[]
}

export interface OntologyReviewActionRequest {
  action: OntologyReviewActionType
  target_kind: OntologyReviewTargetKind
  target_id: string
  reviewed_ontology?: Record<string, any> | null
}

// ---- API functions ----

/** List review queue entries for a knowledge base. */
export async function listReviewQueue(
  kbId: string,
  params?: { status?: string; page?: number; page_size?: number }
): Promise<OntologyReviewQueuePage> {
  const query = new URLSearchParams()
  query.set('kb_id', kbId)
  if (params?.status) query.set('status', params.status)
  if (params?.page) query.set('page', String(params.page))
  if (params?.page_size) query.set('page_size', String(params.page_size))
  return get(`/api/v1/ontology/review/queue?${query.toString()}`) as unknown as Promise<OntologyReviewQueuePage>
}

/** Get a single chunk's review detail with evidence spans and raw/reviewed ontology. */
export async function getReviewChunkDetail(chunkId: string): Promise<OntologyReviewChunkDetail> {
  return get(`/api/v1/ontology/review/chunks/${chunkId}`) as unknown as Promise<OntologyReviewChunkDetail>
}

/** Submit a single review action (accept/reject/edit). */
export async function applyReviewAction(
  chunkId: string,
  req: OntologyReviewActionRequest
): Promise<OntologyReviewChunkDetail> {
  return post(`/api/v1/ontology/review/chunks/${chunkId}/actions`, req) as unknown as Promise<OntologyReviewChunkDetail>
}

/** Approve all raw ontology items for a chunk in one operation. */
export async function approveAllReview(chunkId: string): Promise<OntologyReviewChunkDetail> {
  return post(`/api/v1/ontology/review/chunks/${chunkId}/approve_all`) as unknown as Promise<OntologyReviewChunkDetail>
}
