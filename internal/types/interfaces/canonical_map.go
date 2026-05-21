package interfaces

import (
	"context"

	"github.com/Tencent/WeKnora/internal/types"
)

type CanonicalMapRepository interface {
	Upsert(
		ctx context.Context,
		tenantID uint64,
		kbID string,
		kind types.CanonicalMapKind,
		canonicalID string,
		aliases []string,
		chunkID string,
	) error
	GetByKB(ctx context.Context, tenantID uint64, kbID string) ([]types.CanonicalMapEntry, error)
}
