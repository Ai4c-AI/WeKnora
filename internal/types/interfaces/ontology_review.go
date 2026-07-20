package interfaces

import (
	"context"

	"github.com/Tencent/WeKnora/internal/types"
)

// OntologyReviewRepository defines persistence operations for the review queue and audit log.
type OntologyReviewRepository interface {
	// Enqueue inserts a chunk into the review queue. No-op if already exists (ON CONFLICT).
	Enqueue(ctx context.Context, entry *types.OntologyReviewQueueEntry) error

	// ListQueue returns a paginated review queue for a given KB, filtered by status.
	ListQueue(ctx context.Context, tenantID uint64, kbID string, status types.OntologyReviewStatus, offset, limit int) ([]types.OntologyReviewQueueEntry, int64, error)

	// GetByChunkID returns the queue entry for a specific chunk, or nil if not queued.
	GetByChunkID(ctx context.Context, tenantID uint64, chunkID string) (*types.OntologyReviewQueueEntry, error)

	// UpdateStatus updates the status and optionally the assignee of a queue entry.
	UpdateStatus(ctx context.Context, tenantID uint64, chunkID string, status types.OntologyReviewStatus, assignedTo *uint64) error

	// InsertAudit appends an audit log entry (append-only).
	InsertAudit(ctx context.Context, entry *types.OntologyReviewAuditLog) error

	// UpdateChunkReviewFields persists reviewed ontology + metadata on the chunk row.
	UpdateChunkReviewFields(ctx context.Context, tenantID uint64, chunkID string, reviewed *types.MicroTBox, status types.OntologyReviewStatus, reviewerID uint64) error

	// FindChunksWithOntologyNotInQueue returns chunks that have ontology_json but are not yet in the review queue.
	// Used by the backfill command.
	FindChunksWithOntologyNotInQueue(ctx context.Context, tenantID uint64, kbID string, updatedSinceHours int) ([]*types.Chunk, error)

	// FindChunksWithConflictingClass returns chunks in the same KB that declare the given class
	// with a different subClassOf or disjointWith declaration. Used by conflict pre-check.
	FindChunksWithConflictingClass(ctx context.Context, tenantID uint64, kbID string, excludeChunkID string, classID string, subClassOf *string, disjointWith []string) ([]*types.Chunk, error)
}

// OntologyReviewService is the handler-facing interface for ontology review operations.
type OntologyReviewService interface {
	ListQueue(ctx context.Context, tenantID uint64, query types.OntologyReviewQueueQuery) ([]types.OntologyReviewQueueEntry, int64, error)
	GetChunkDetail(ctx context.Context, tenantID uint64, chunkID string) (*types.OntologyReviewChunkDetail, error)
	ApplyAction(ctx context.Context, tenantID uint64, chunkID string, reviewerID uint64, req *types.OntologyReviewActionRequest) (*types.OntologyReviewChunkDetail, error)
	ApproveAll(ctx context.Context, tenantID uint64, chunkID string, reviewerID uint64) (*types.OntologyReviewChunkDetail, error)
}
