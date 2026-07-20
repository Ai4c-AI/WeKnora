package types

import "time"

// OntologyReviewStatus represents the review state of a chunk's ontology.
type OntologyReviewStatus string

const (
	ReviewStatusPending   OntologyReviewStatus = "pending"
	ReviewStatusInReview  OntologyReviewStatus = "in_review"
	ReviewStatusApproved  OntologyReviewStatus = "approved"
	ReviewStatusRejected  OntologyReviewStatus = "rejected"
	ReviewStatusNoReview  OntologyReviewStatus = "no_review"
)

// OntologyReviewAction represents a single review operation.
type OntologyReviewAction string

const (
	ActionAccept     OntologyReviewAction = "accept"
	ActionReject     OntologyReviewAction = "reject"
	ActionEdit       OntologyReviewAction = "edit"
	ActionApproveAll OntologyReviewAction = "approve_all"
)

// OntologyReviewTargetKind identifies which part of the micro-TBox is being reviewed.
type OntologyReviewTargetKind string

const (
	TargetKindClass    OntologyReviewTargetKind = "class"
	TargetKindProperty OntologyReviewTargetKind = "property"
	TargetKindShape    OntologyReviewTargetKind = "shape"
	TargetKindAlias    OntologyReviewTargetKind = "alias"
	TargetKindAxiom    OntologyReviewTargetKind = "axiom"
)

// OntologyReviewQueueEntry represents a row in ontology_review_queue.
type OntologyReviewQueueEntry struct {
	ID              int64                 `json:"id"`
	TenantID        uint64                `json:"tenant_id"`
	KnowledgeBaseID string                `json:"knowledge_base_id"`
	ChunkID         string                `json:"chunk_id"`
	KnowledgeTitle  string                `json:"knowledge_title"`
	ContentPreview  string                `json:"content_preview"`
	Priority        int                   `json:"priority"`
	PriorityReason  string                `json:"priority_reason,omitempty"`
	AssignedTo      *uint64               `json:"assigned_to,omitempty"`
	Status          OntologyReviewStatus  `json:"status"`
	CreatedAt       time.Time             `json:"created_at"`
	UpdatedAt       time.Time             `json:"updated_at"`
}

// OntologyReviewAuditLog records a single review action (append-only).
type OntologyReviewAuditLog struct {
	ID          int64                    `json:"id"`
	TenantID    uint64                   `json:"tenant_id"`
	ChunkID     string                   `json:"chunk_id"`
	ReviewerID  uint64                   `json:"reviewer_id"`
	Action      OntologyReviewAction     `json:"action"`
	TargetKind  OntologyReviewTargetKind `json:"target_kind"`
	TargetID    string                   `json:"target_id"`
	BeforeValue *MicroTBox               `json:"before_value,omitempty"  gorm:"type:jsonb;serializer:json"`
	AfterValue  *MicroTBox               `json:"after_value,omitempty"   gorm:"type:jsonb;serializer:json"`
	CreatedAt   time.Time                `json:"created_at"`
}

// OntologyReviewActionRequest is the request body for a single review action.
type OntologyReviewActionRequest struct {
	Action           OntologyReviewAction      `json:"action"`
	TargetKind       OntologyReviewTargetKind  `json:"target_kind"`
	TargetID         string                    `json:"target_id"`
	ReviewedOntology *MicroTBox                `json:"reviewed_ontology,omitempty"`
}

// EvidenceSpan describes the location of an evidence string within chunk content.
type EvidenceSpan struct {
	TargetID    string `json:"target_id"`
	TargetKind  string `json:"target_kind"`   // class | property | shape | axiom
	Evidence    string `json:"evidence"`
	StartOffset int    `json:"start_offset"`  // -1 means not found in source text
	EndOffset   int    `json:"end_offset"`
}

// OntologyReviewChunkDetail is the response for GET .../chunks/{chunkId}.
type OntologyReviewChunkDetail struct {
	Chunk         *Chunk          `json:"chunk"`
	EvidenceSpans []EvidenceSpan  `json:"evidence_spans"`
}

// OntologyReviewQueuePage is a paginated queue response.
type OntologyReviewQueuePage struct {
	Items    []OntologyReviewQueueEntry `json:"items"`
	Page     int                        `json:"page"`
	PageSize int                        `json:"page_size"`
	Total    int64                      `json:"total"`
	HasMore  bool                       `json:"has_more"`
}

// OntologyReviewQueueQuery filters the review queue.
type OntologyReviewQueueQuery struct {
	KnowledgeBaseID string               `json:"knowledge_base_id,omitempty" form:"kb_id"`
	Status          OntologyReviewStatus `json:"status,omitempty"             form:"status"`
	Page            int                  `json:"page,omitempty"               form:"page"`
	PageSize        int                  `json:"page_size,omitempty"          form:"page_size"`
}

// EnqueueParams carries the data needed to insert a chunk into the review queue.
type EnqueueParams struct {
	Chunk           *Chunk
	Priority        int
	PriorityReason  string
	KnowledgeTitle  string
	ContentPreview  string
}
