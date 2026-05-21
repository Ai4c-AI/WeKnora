package repository

import (
	"context"

	"github.com/Tencent/WeKnora/internal/types"
	"github.com/Tencent/WeKnora/internal/types/interfaces"
	"github.com/lib/pq"
	"gorm.io/gorm"
	"gorm.io/gorm/clause"
)

type canonicalMapRepository struct {
	db *gorm.DB
}

func NewCanonicalMapRepository(db *gorm.DB) interfaces.CanonicalMapRepository {
	return &canonicalMapRepository{db: db}
}

func (r *canonicalMapRepository) Upsert(
	ctx context.Context,
	tenantID uint64,
	kbID string,
	kind types.CanonicalMapKind,
	canonicalID string,
	aliases []string,
	chunkID string,
) error {
	aliasArray := pq.StringArray(aliases)
	if aliasArray == nil {
		aliasArray = pq.StringArray{}
	}

	entry := types.CanonicalMapEntry{
		TenantID:        tenantID,
		KnowledgeBaseID: kbID,
		Kind:            kind,
		CanonicalID:     canonicalID,
		Aliases:         aliasArray,
		SourceChunks:    pq.StringArray{chunkID},
		Confidence:      0.5,
	}

	return r.db.WithContext(ctx).
		Clauses(clause.OnConflict{
			Columns: []clause.Column{
				{Name: "tenant_id"},
				{Name: "knowledge_base_id"},
				{Name: "kind"},
				{Name: "canonical_id"},
			},
			DoUpdates: clause.Assignments(map[string]interface{}{
				"aliases": gorm.Expr(
					"ARRAY(SELECT DISTINCT alias FROM unnest(ontology_canonical_map.aliases || EXCLUDED.aliases) AS merged(alias))",
				),
				"source_chunks": gorm.Expr(
					"ARRAY(SELECT DISTINCT chunk_id FROM unnest(ontology_canonical_map.source_chunks || EXCLUDED.source_chunks) AS merged(chunk_id))",
				),
				"confidence": gorm.Expr("GREATEST(ontology_canonical_map.confidence, EXCLUDED.confidence)"),
				"updated_at": gorm.Expr("NOW()"),
			}),
		}).Create(&entry).Error
}

func (r *canonicalMapRepository) GetByKB(
	ctx context.Context,
	tenantID uint64,
	kbID string,
) ([]types.CanonicalMapEntry, error) {
	var entries []types.CanonicalMapEntry
	err := r.db.WithContext(ctx).
		Where("tenant_id = ? AND knowledge_base_id = ?", tenantID, kbID).
		Find(&entries).Error
	return entries, err
}
