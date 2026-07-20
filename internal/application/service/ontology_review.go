package service

import (
	"context"
	"fmt"
	"math"
	"strings"
	"time"

	"github.com/Tencent/WeKnora/internal/logger"
	"github.com/Tencent/WeKnora/internal/types"
	"github.com/Tencent/WeKnora/internal/types/interfaces"
)

// OntologyReviewService manages the expert review lifecycle for ontology slices.
type OntologyReviewService struct {
	reviewRepo interfaces.OntologyReviewRepository
	chunkRepo  interfaces.ChunkRepository
	canonical  interfaces.CanonicalMapRepository
}

// NewOntologyReviewService creates a new OntologyReviewService.
func NewOntologyReviewService(
	reviewRepo interfaces.OntologyReviewRepository,
	chunkRepo interfaces.ChunkRepository,
	canonical interfaces.CanonicalMapRepository,
) *OntologyReviewService {
	return &OntologyReviewService{
		reviewRepo: reviewRepo,
		chunkRepo:  chunkRepo,
		canonical:  canonical,
	}
}

// PriorityWeights mirrors the design doc §12.5 weighting table.
const (
	weightEntityFrequency  = 40
	weightAxiomComplexity  = 20
	weightConfidenceInv    = 20
	weightConflictDetected = 15
	weightQueryHit         = 5
)

// CalculatePriority computes a 0-100 priority score for a chunk's ontology review.
func (s *OntologyReviewService) CalculatePriority(
	ctx context.Context,
	chunk *types.Chunk,
	conflictDetected bool,
) (score int, reason string) {
	if chunk.OntologyJSON == nil {
		return 0, "no ontology"
	}

	reasons := make([]string, 0, 5)
	score = 0

	// 1. Entity frequency (40 pts) — measure by alias count in canonical_map
	entityScore := 0
	tbox := chunk.OntologyJSON
	for _, cls := range tbox.Classes {
		entityScore++
	}
	// Normalize: log(entityCount + 1) scaled to 0-40
	if entityScore > 0 {
		normalized := math.Log2(float64(entityScore)+1) / math.Log2(11) * float64(weightEntityFrequency)
		score += int(math.Min(normalized, float64(weightEntityFrequency)))
		if int(normalized) > 0 {
			reasons = append(reasons, fmt.Sprintf("entity count: %d", entityScore))
		}
	}

	// 2. Axiom complexity (20 pts)
	axiomCount := len(tbox.Classes) + len(tbox.Properties) + len(tbox.Axioms)
	if axiomCount > 0 {
		// 0 axioms = 0 pts, 1-3 = 5, 4-7 = 10, 8+ = 15-20
		axiomScore := int(math.Min(float64(axiomCount)/8.0*float64(weightAxiomComplexity), float64(weightAxiomComplexity)))
		score += axiomScore
		if axiomScore >= 10 {
			reasons = append(reasons, fmt.Sprintf("axiom complexity: %d items", axiomCount))
		}
	}

	// 3. LLM confidence inverse (20 pts) — lower confidence = higher priority
	confInv := int((1.0 - tbox.Confidence) * float64(weightConfidenceInv))
	if confInv < 0 {
		confInv = 0
	}
	score += confInv
	if tbox.Confidence < 0.5 {
		reasons = append(reasons, fmt.Sprintf("low confidence: %.2f", tbox.Confidence))
	}

	// 4. Conflict detected (15 pts)
	if conflictDetected {
		score += weightConflictDetected
		reasons = append(reasons, "class conflict detected")
	}

	// 5. Query hit (5 pts) — simplified: check if chunk was recently accessed
	// In MVP we use the ontology_extracted_at recency as a proxy;
	// a proper implementation would query search logs.
	if chunk.OntologyExtractedAt != nil && time.Since(*chunk.OntologyExtractedAt) < 24*time.Hour {
		score += 1 // Minimal score for recency proxy
	}

	if score > 100 {
		score = 100
	}

	return score, strings.Join(reasons, "; ")
}

// EnqueueParams carries data needed to insert a chunk into the review queue.
type EnqueueParams struct {
	Chunk           *types.Chunk
	Priority        int
	PriorityReason  string
	KnowledgeTitle  string
	ContentPreview  string
}

// EnqueueChunk inserts or no-ops a chunk in the review queue.
func (s *OntologyReviewService) EnqueueChunk(ctx context.Context, params *EnqueueParams) error {
	entry := &types.OntologyReviewQueueEntry{
		TenantID:        params.Chunk.TenantID,
		KnowledgeBaseID: params.Chunk.KnowledgeBaseID,
		ChunkID:         params.Chunk.ID,
		KnowledgeTitle:  params.KnowledgeTitle,
		ContentPreview:  params.ContentPreview,
		Priority:        params.Priority,
		PriorityReason:  params.PriorityReason,
		Status:          types.ReviewStatusPending,
		CreatedAt:       time.Now(),
		UpdatedAt:       time.Now(),
	}
	return s.reviewRepo.Enqueue(ctx, entry)
}

// EnqueueChunksFromGraph is called by graphBuilder after micro-TBox extraction completes.
// It runs conflict detection, calculates priorities, and enqueues chunks in batch.
func (s *OntologyReviewService) EnqueueChunksFromGraph(
	ctx context.Context,
	tenantID uint64,
	kbID string,
	chunks []*types.Chunk,
	knowledgeTitles map[string]string, // chunkID → knowledge title
) error {
	if len(chunks) == 0 {
		return nil
	}

	// Step 1: In-batch conflict detection
	conflicts := detectInBatchConflicts(chunks)

	// Step 2: For single-occurrence classes, query DB for cross-batch conflicts
	dbConflicts := detectCrossBatchConflicts(ctx, s.reviewRepo, tenantID, kbID, chunks, conflicts)
	for classID, chunkIDs := range dbConflicts {
		conflicts[classID] = append(conflicts[classID], chunkIDs...)
	}

	// Step 3: Build conflict lookup: chunkID → has any conflict
	chunkHasConflict := make(map[string]bool)
	for _, chunkIDs := range conflicts {
		for _, cid := range chunkIDs {
			chunkHasConflict[cid] = true
		}
	}

	// Step 4: Enqueue each chunk
	for _, chunk := range chunks {
		if chunk.OntologyJSON == nil {
			continue
		}
		priority, reason := s.CalculatePriority(ctx, chunk, chunkHasConflict[chunk.ID])
		title := knowledgeTitles[chunk.ID]
		preview := truncateContent(chunk.Content, 200)

		if err := s.EnqueueChunk(ctx, &EnqueueParams{
			Chunk:          chunk,
			Priority:       priority,
			PriorityReason: reason,
			KnowledgeTitle: title,
			ContentPreview: preview,
		}); err != nil {
			logger.GetLogger(ctx).WithError(err).Warnf("Failed to enqueue chunk %s for review", chunk.ID)
		}
	}

	logger.GetLogger(ctx).Infof("Enqueued %d chunks for ontology review (kb=%s)", len(chunks), kbID)
	return nil
}

// classRecord holds the class declaration from a single chunk for conflict comparison.
type classRecord struct {
	ChunkID      string
	SubClassOf   *string
	DisjointWith []string
}

// detectInBatchConflicts finds class declaration conflicts within the current batch.
// Returns map[classID][]conflictingChunkIDs.
func detectInBatchConflicts(chunks []*types.Chunk) map[string][]string {
	decls := make(map[string][]classRecord)
	for _, chunk := range chunks {
		if chunk.OntologyJSON == nil {
			continue
		}
		for _, cls := range chunk.OntologyJSON.Classes {
			decls[cls.ID] = append(decls[cls.ID], classRecord{
				ChunkID:      chunk.ID,
				SubClassOf:   cls.SubClassOf,
				DisjointWith: cls.DisjointWith,
			})
		}
	}

	conflicts := make(map[string][]string)
	for classID, records := range decls {
		if len(records) < 2 {
			continue
		}
		first := records[0]
		for _, rec := range records[1:] {
			if classDeclsDiffer(first, rec) {
				conflicts[classID] = append(conflicts[classID], rec.ChunkID)
				if !containsStr(conflicts[classID], first.ChunkID) {
					conflicts[classID] = append(conflicts[classID], first.ChunkID)
				}
			}
		}
	}
	return conflicts
}

// detectCrossBatchConflicts queries the DB for chunks in the same KB that declare the same class
// with different subClassOf/disjointWith values.
func detectCrossBatchConflicts(
	ctx context.Context,
	repo interfaces.OntologyReviewRepository,
	tenantID uint64,
	kbID string,
	chunks []*types.Chunk,
	knownConflicts map[string][]string,
) map[string][]string {
	result := make(map[string][]string)

	// Collect all class declarations from this batch
	seenClasses := make(map[string]classRecord)
	for _, chunk := range chunks {
		if chunk.OntologyJSON == nil {
			continue
		}
		for _, cls := range chunk.OntologyJSON.Classes {
			// Only check classes that appear exactly once in this batch
			if _, exists := seenClasses[cls.ID]; !exists {
				seenClasses[cls.ID] = classRecord{
					ChunkID:      chunk.ID,
					SubClassOf:   cls.SubClassOf,
					DisjointWith: cls.DisjointWith,
				}
			}
		}
	}

	for classID, record := range seenClasses {
		// Skip if already flagged as in-batch conflict
		if _, ok := knownConflicts[classID]; ok {
			continue
		}

		conflicting, err := repo.FindChunksWithConflictingClass(
			ctx, tenantID, kbID, record.ChunkID, classID, record.SubClassOf, record.DisjointWith,
		)
		if err != nil {
			logger.GetLogger(ctx).WithError(err).Warnf("Conflict check failed for class %s", classID)
			continue
		}
		if len(conflicting) > 0 {
			result[classID] = []string{record.ChunkID}
			for _, c := range conflicting {
				result[classID] = append(result[classID], c.ID)
			}
		}
	}
	return result
}

func classDeclsDiffer(a, b classRecord) bool {
	if (a.SubClassOf == nil) != (b.SubClassOf == nil) {
		return true
	}
	if a.SubClassOf != nil && *a.SubClassOf != *b.SubClassOf {
		return true
	}
	return disjointSlicesDiffer(a.DisjointWith, b.DisjointWith)
}

func disjointSlicesDiffer(a, b []string) bool {
	if len(a) != len(b) {
		return true
	}
	am := make(map[string]struct{}, len(a))
	for _, s := range a {
		am[s] = struct{}{}
	}
	for _, s := range b {
		if _, ok := am[s]; !ok {
			return true
		}
	}
	return false
}

// ComputeEvidenceSpans calculates start/end offsets for every evidence string in the chunk content.
func ComputeEvidenceSpans(chunk *types.Chunk) []types.EvidenceSpan {
	if chunk.OntologyJSON == nil || chunk.Content == "" {
		return nil
	}

	var spans []types.EvidenceSpan

	for _, cls := range chunk.OntologyJSON.Classes {
		if span, ok := findEvidence(chunk.Content, cls.Evidence, cls.ID, "class"); ok {
			spans = append(spans, span)
		}
	}
	for _, prop := range chunk.OntologyJSON.Properties {
		if span, ok := findEvidence(chunk.Content, prop.Evidence, prop.ID, "property"); ok {
			spans = append(spans, span)
		}
	}
	for _, shape := range chunk.OntologyJSON.Shapes {
		if span, ok := findEvidence(chunk.Content, shape.Evidence, shape.TargetClass, "shape"); ok {
			spans = append(spans, span)
		}
	}
	for i, axiom := range chunk.OntologyJSON.Axioms {
		targetID := fmt.Sprintf("axiom_%d", i)
		if span, ok := findEvidence(chunk.Content, axiom.Evidence, targetID, "axiom"); ok {
			spans = append(spans, span)
		}
	}

	return spans
}

func findEvidence(content, evidence, targetID, targetKind string) (types.EvidenceSpan, bool) {
	if evidence == "" {
		return types.EvidenceSpan{}, false
	}
	idx := strings.Index(content, evidence)
	if idx < 0 {
		return types.EvidenceSpan{TargetID: targetID, TargetKind: targetKind, Evidence: evidence, StartOffset: -1, EndOffset: -1}, true
	}
	return types.EvidenceSpan{
		TargetID:    targetID,
		TargetKind:  targetKind,
		Evidence:    evidence,
		StartOffset: idx,
		EndOffset:   idx + len(evidence),
	}, true
}

func truncateContent(content string, maxLen int) string {
	runes := []rune(content)
	if len(runes) <= maxLen {
		return content
	}
	return string(runes[:maxLen]) + "..."
}

func containsStr(slice []string, s string) bool {
	for _, item := range slice {
		if item == s {
			return true
		}
	}
	return false
}

// ListQueue returns the paginated review queue.
func (s *OntologyReviewService) ListQueue(
	ctx context.Context, tenantID uint64, query types.OntologyReviewQueueQuery,
) ([]types.OntologyReviewQueueEntry, int64, error) {
	offset := (query.Page - 1) * query.PageSize
	return s.reviewRepo.ListQueue(ctx, tenantID, query.KnowledgeBaseID, query.Status, offset, query.PageSize)
}

// GetChunkDetail returns the full chunk detail for review, including evidence spans.
func (s *OntologyReviewService) GetChunkDetail(
	ctx context.Context, tenantID uint64, chunkID string,
) (*types.OntologyReviewChunkDetail, error) {
	chunk, err := s.chunkRepo.GetChunkByID(ctx, tenantID, chunkID)
	if err != nil {
		return nil, fmt.Errorf("get chunk: %w", err)
	}

	detail := &types.OntologyReviewChunkDetail{
		Chunk: chunk,
	}

	if chunk.OntologyJSON != nil {
		detail.EvidenceSpans = ComputeEvidenceSpans(chunk)
	}

	return detail, nil
}

// ApplyAction processes a single review action on a chunk's ontology.
func (s *OntologyReviewService) ApplyAction(
	ctx context.Context, tenantID uint64, chunkID string, reviewerID uint64, req *types.OntologyReviewActionRequest,
) (*types.OntologyReviewChunkDetail, error) {
	chunk, err := s.chunkRepo.GetChunkByID(ctx, tenantID, chunkID)
	if err != nil {
		return nil, fmt.Errorf("get chunk: %w", err)
	}

	// Record audit log
	auditEntry := &types.OntologyReviewAuditLog{
		TenantID:    tenantID,
		ChunkID:     chunkID,
		ReviewerID:  reviewerID,
		Action:      req.Action,
		TargetKind:  req.TargetKind,
		TargetID:    req.TargetID,
		BeforeValue: chunk.OntologyJSONReviewed,
	}
	if req.ReviewedOntology != nil {
		auditEntry.AfterValue = req.ReviewedOntology
	} else if req.Action == types.ActionApproveAll || req.Action == types.ActionAccept {
		auditEntry.AfterValue = chunk.OntologyJSON
	}
	if err := s.reviewRepo.InsertAudit(ctx, auditEntry); err != nil {
		return nil, fmt.Errorf("insert audit: %w", err)
	}

	// Update reviewed ontology on chunk
	var reviewed *types.MicroTBox
	var status types.OntologyReviewStatus

	switch req.Action {
	case types.ActionAccept:
		reviewed = chunk.OntologyJSONReviewed
		if reviewed == nil {
			reviewed = chunk.OntologyJSON
		}
		status = types.ReviewStatusApproved
	case types.ActionReject:
		status = types.ReviewStatusRejected
		if chunk.OntologyJSONReviewed != nil {
			reviewed = chunk.OntologyJSONReviewed
		}
	case types.ActionEdit:
		if req.ReviewedOntology == nil {
			return nil, fmt.Errorf("edit action requires reviewed_ontology")
		}
		reviewed = req.ReviewedOntology
		status = types.ReviewStatusApproved
	default:
		return nil, fmt.Errorf("unsupported action: %s", req.Action)
	}

	if err := s.reviewRepo.UpdateChunkReviewFields(ctx, tenantID, chunkID, reviewed, status, reviewerID); err != nil {
		return nil, fmt.Errorf("update chunk review: %w", err)
	}

	// Update queue status
	_ = s.reviewRepo.UpdateStatus(ctx, tenantID, chunkID, status, &reviewerID)

	// Refresh chunk
	chunk, _ = s.chunkRepo.GetChunkByID(ctx, tenantID, chunkID)
	return &types.OntologyReviewChunkDetail{
		Chunk:         chunk,
		EvidenceSpans: ComputeEvidenceSpans(chunk),
	}, nil
}

// ApproveAll promotes the entire raw ontology to reviewed status.
func (s *OntologyReviewService) ApproveAll(
	ctx context.Context, tenantID uint64, chunkID string, reviewerID uint64,
) (*types.OntologyReviewChunkDetail, error) {
	chunk, err := s.chunkRepo.GetChunkByID(ctx, tenantID, chunkID)
	if err != nil {
		return nil, fmt.Errorf("get chunk: %w", err)
	}

	if chunk.OntologyJSON == nil {
		return nil, fmt.Errorf("chunk has no raw ontology to approve")
	}

	// Record audit
	auditEntry := &types.OntologyReviewAuditLog{
		TenantID:    tenantID,
		ChunkID:     chunkID,
		ReviewerID:  reviewerID,
		Action:      types.ActionApproveAll,
		TargetKind:  types.TargetKindClass,
		TargetID:    "all",
		BeforeValue: chunk.OntologyJSONReviewed,
		AfterValue:  chunk.OntologyJSON,
	}
	if err := s.reviewRepo.InsertAudit(ctx, auditEntry); err != nil {
		return nil, fmt.Errorf("insert audit: %w", err)
	}

	if err := s.reviewRepo.UpdateChunkReviewFields(
		ctx, tenantID, chunkID, chunk.OntologyJSON, types.ReviewStatusApproved, reviewerID,
	); err != nil {
		return nil, fmt.Errorf("update chunk review: %w", err)
	}

	_ = s.reviewRepo.UpdateStatus(ctx, tenantID, chunkID, types.ReviewStatusApproved, &reviewerID)

	chunk, _ = s.chunkRepo.GetChunkByID(ctx, tenantID, chunkID)
	return &types.OntologyReviewChunkDetail{
		Chunk:         chunk,
		EvidenceSpans: ComputeEvidenceSpans(chunk),
	}, nil
}
