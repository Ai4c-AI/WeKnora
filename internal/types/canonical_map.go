package types

import (
	"time"

	"github.com/lib/pq"
)

type CanonicalMapKind string

const (
	CanonicalMapKindClass    CanonicalMapKind = "class"
	CanonicalMapKindProperty CanonicalMapKind = "property"
)

type CanonicalMapEntry struct {
	ID              int64            `json:"id"                gorm:"primaryKey;autoIncrement"`
	TenantID        uint64           `json:"tenant_id"         gorm:"not null"`
	KnowledgeBaseID string           `json:"knowledge_base_id" gorm:"not null"`
	Kind            CanonicalMapKind `json:"kind"              gorm:"type:text;not null"`
	CanonicalID     string           `json:"canonical_id"      gorm:"not null"`
	Aliases         pq.StringArray   `json:"aliases"           gorm:"type:text[];not null;default:'{}'"`
	SourceChunks    pq.StringArray   `json:"source_chunks"     gorm:"type:text[];not null;default:'{}'"`
	Confidence      float32          `json:"confidence"        gorm:"not null;default:0.5"`
	CreatedAt       time.Time        `json:"created_at"`
	UpdatedAt       time.Time        `json:"updated_at"`
}

func (CanonicalMapEntry) TableName() string {
	return "ontology_canonical_map"
}
