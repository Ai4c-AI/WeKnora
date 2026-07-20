package repository

import (
	"context"
	"fmt"
	"strings"

	"github.com/Tencent/WeKnora/internal/types"
	"github.com/Tencent/WeKnora/internal/types/interfaces"
	"gorm.io/gorm"
	"gorm.io/gorm/clause"
)

type ontologyReviewRepository struct {
	db *gorm.DB
}

func NewOntologyReviewRepository(db *gorm.DB) interfaces.OntologyReviewRepository {
	return &ontologyReviewRepository{db: db}
}

func (r *ontologyReviewRepository) Enqueue(ctx context.Context, entry *types.OntologyReviewQueueEntry) error {
	return r.db.WithContext(ctx).
		Clauses(clause.OnConflict{
			Columns:   []clause.Column{{Name: "tenant_id"}, {Name: "chunk_id"}},
			DoNothing: true,
		}).
		Create(entry).Error
}

func (r *ontologyReviewRepository) ListQueue(
	ctx context.Context,
	tenantID uint64,
	kbID string,
	status types.OntologyReviewStatus,
	offset, limit int,
) ([]types.OntologyReviewQueueEntry, int64, error) {
	query := r.db.WithContext(ctx).
		Model(&types.OntologyReviewQueueEntry{}).
		Where("tenant_id = ? AND knowledge_base_id = ?", tenantID, kbID)

	if status != "" {
		query = query.Where("status = ?", status)
	}

	var total int64
	if err := query.Count(&total).Error; err != nil {
		return nil, 0, fmt.Errorf("review queue count: %w", err)
	}

	var entries []types.OntologyReviewQueueEntry
	if err := query.
		Order("priority DESC, created_at ASC").
		Offset(offset).
		Limit(limit).
		Find(&entries).Error; err != nil {
		return nil, 0, fmt.Errorf("review queue list: %w", err)
	}

	return entries, total, nil
}

func (r *ontologyReviewRepository) GetByChunkID(
	ctx context.Context, tenantID uint64, chunkID string,
) (*types.OntologyReviewQueueEntry, error) {
	var entry types.OntologyReviewQueueEntry
	err := r.db.WithContext(ctx).
		Where("tenant_id = ? AND chunk_id = ?", tenantID, chunkID).
		First(&entry).Error
	if err != nil {
		if err == gorm.ErrRecordNotFound {
			return nil, nil
		}
		return nil, fmt.Errorf("review queue get: %w", err)
	}
	return &entry, nil
}

func (r *ontologyReviewRepository) UpdateStatus(
	ctx context.Context, tenantID uint64, chunkID string, status types.OntologyReviewStatus, assignedTo *uint64,
) error {
	updates := map[string]interface{}{
		"status":     status,
		"updated_at": gorm.Expr("NOW()"),
	}
	if assignedTo != nil {
		updates["assigned_to"] = *assignedTo
	}
	return r.db.WithContext(ctx).
		Model(&types.OntologyReviewQueueEntry{}).
		Where("tenant_id = ? AND chunk_id = ?", tenantID, chunkID).
		Updates(updates).Error
}

func (r *ontologyReviewRepository) InsertAudit(ctx context.Context, entry *types.OntologyReviewAuditLog) error {
	return r.db.WithContext(ctx).Create(entry).Error
}

func (r *ontologyReviewRepository) UpdateChunkReviewFields(
	ctx context.Context, tenantID uint64, chunkID string,
	reviewed *types.MicroTBox, status types.OntologyReviewStatus, reviewerID uint64,
) error {
	return r.db.WithContext(ctx).
		Model(&types.Chunk{}).
		Where("tenant_id = ? AND id = ?", tenantID, chunkID).
		Updates(map[string]interface{}{
			"ontology_json_reviewed": reviewed,
			"ontology_review_status": status,
			"ontology_reviewed_by":   reviewerID,
			"ontology_reviewed_at":   gorm.Expr("NOW()"),
		}).Error
}

func (r *ontologyReviewRepository) FindChunksWithOntologyNotInQueue(
	ctx context.Context, tenantID uint64, kbID string, updatedSinceHours int,
) ([]*types.Chunk, error) {
	subQuery := r.db.WithContext(ctx).
		Model(&types.OntologyReviewQueueEntry{}).
		Select("chunk_id").
		Where("tenant_id = ? AND knowledge_base_id = ?", tenantID, kbID)

	query := r.db.WithContext(ctx).
		Model(&types.Chunk{}).
		Where("tenant_id = ? AND knowledge_base_id = ? AND ontology_json IS NOT NULL", tenantID, kbID).
		Where("id NOT IN (?)", subQuery)

	if updatedSinceHours > 0 {
		query = query.Where("updated_at >= NOW() - (? || ' hours')::INTERVAL", fmt.Sprint(updatedSinceHours))
	}

	var chunks []*types.Chunk
	if err := query.Find(&chunks).Error; err != nil {
		return nil, fmt.Errorf("find chunks not in review queue: %w", err)
	}
	return chunks, nil
}

func (r *ontologyReviewRepository) FindChunksWithConflictingClass(
	ctx context.Context, tenantID uint64, kbID string,
	excludeChunkID string, classID string, subClassOf *string, disjointWith []string,
) ([]*types.Chunk, error) {
	// Build a JSON path condition: ontology_json -> 'classes' contains an object with "id" == classID
	// and either subClassOf differs or disjointWith differs.
	jsonConditions := []string{
		fmt.Sprintf(`ontology_json::jsonb @> '{"classes":[{"id":"%s"}]}'`, classID),
	}

	if subClassOf != nil {
		jsonConditions = append(jsonConditions,
			fmt.Sprintf(`NOT ontology_json::jsonb @> '{"classes":[{"id":"%s","subClassOf":"%s"}]}'`, classID, *subClassOf))
	} else {
		// We declared subClassOf is null, so a conflict exists if ANY chunk has a non-null subClassOf
		jsonConditions = append(jsonConditions,
			fmt.Sprintf(`ontology_json::jsonb @? '$.classes[*] ? (@.id == "%s" && exists(@.subClassOf))'`, classID))
	}

	if len(disjointWith) > 0 {
		// Conflict if disjointWith arrays differ
		for _, dw := range disjointWith {
			jsonConditions = append(jsonConditions,
				fmt.Sprintf(`NOT ontology_json::jsonb @> '{"classes":[{"id":"%s","disjointWith":["%s"]}]}'`, classID, dw))
		}
	}

	whereClause := strings.Join(jsonConditions, " OR ")

	var chunks []*types.Chunk
	err := r.db.WithContext(ctx).
		Model(&types.Chunk{}).
		Where("tenant_id = ? AND knowledge_base_id = ? AND id != ? AND ontology_json IS NOT NULL", tenantID, kbID, excludeChunkID).
		Where(whereClause).
		Limit(10). // Cap results for performance
		Find(&chunks).Error
	if err != nil {
		return nil, fmt.Errorf("find conflicting class chunks: %w", err)
	}
	return chunks, nil
}
