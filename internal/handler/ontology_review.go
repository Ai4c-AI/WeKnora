package handler

import (
	"context"
	"net/http"

	"github.com/Tencent/WeKnora/internal/logger"
	"github.com/Tencent/WeKnora/internal/types"
	"github.com/Tencent/WeKnora/internal/types/interfaces"
	"github.com/gin-gonic/gin"
)

// OntologyReviewHandler handles HTTP requests for the ontology expert review loop.
type OntologyReviewHandler struct {
	reviewService interfaces.OntologyReviewService
}

// NewOntologyReviewHandler creates a new OntologyReviewHandler.
func NewOntologyReviewHandler(svc interfaces.OntologyReviewService) *OntologyReviewHandler {
	return &OntologyReviewHandler{reviewService: svc}
}

// ListQueue returns the paginated review queue for a knowledge base.
// GET /api/v1/tenants/:tenant_id/ontology/review/queue
func (h *OntologyReviewHandler) ListQueue(c *gin.Context) {
	ctx := c.Request.Context()
	tenantID := c.GetUint64(types.TenantIDContextKey.String())

	var query types.OntologyReviewQueueQuery
	if err := c.ShouldBindQuery(&query); err != nil {
		c.JSON(http.StatusBadRequest, gin.H{"error": err.Error()})
		return
	}
	if query.KnowledgeBaseID == "" {
		c.JSON(http.StatusBadRequest, gin.H{"error": "kb_id is required"})
		return
	}
	if query.Page <= 0 {
		query.Page = 1
	}
	if query.PageSize <= 0 || query.PageSize > 100 {
		query.PageSize = 20
	}

	entries, total, err := h.reviewService.ListQueue(ctx, tenantID, query)
	if err != nil {
		logger.Errorf(ctx, "Failed to list review queue: %v", err)
		c.JSON(http.StatusInternalServerError, gin.H{"error": "Failed to list review queue"})
		return
	}

	hasMore := int64((query.Page-1)*query.PageSize+len(entries)) < total
	c.JSON(http.StatusOK, types.OntologyReviewQueuePage{
		Items:    entries,
		Page:     query.Page,
		PageSize: query.PageSize,
		Total:    total,
		HasMore:  hasMore,
	})
}

// GetChunkDetail returns a single chunk's ontology review detail with evidence spans.
// GET /api/v1/tenants/:tenant_id/ontology/review/chunks/:chunkId
func (h *OntologyReviewHandler) GetChunkDetail(c *gin.Context) {
	ctx := c.Request.Context()
	tenantID := c.GetUint64(types.TenantIDContextKey.String())
	chunkID := c.Param("chunkId")

	detail, err := h.reviewService.GetChunkDetail(ctx, tenantID, chunkID)
	if err != nil {
		logger.Errorf(ctx, "Failed to get review chunk detail: %v", err)
		c.JSON(http.StatusNotFound, gin.H{"error": "Chunk not found"})
		return
	}

	c.JSON(http.StatusOK, detail)
}

// ApplyAction submits a single review action (accept/reject/edit).
// POST /api/v1/tenants/:tenant_id/ontology/review/chunks/:chunkId/actions
func (h *OntologyReviewHandler) ApplyAction(c *gin.Context) {
	ctx := c.Request.Context()
	tenantID := c.GetUint64(types.TenantIDContextKey.String())
	chunkID := c.Param("chunkId")
	reviewerID := c.GetUint64(types.UserIDContextKey.String())

	var req types.OntologyReviewActionRequest
	if err := c.ShouldBindJSON(&req); err != nil {
		c.JSON(http.StatusBadRequest, gin.H{"error": err.Error()})
		return
	}

	detail, err := h.reviewService.ApplyAction(ctx, tenantID, chunkID, reviewerID, &req)
	if err != nil {
		logger.Errorf(ctx, "Failed to apply review action: %v", err)
		c.JSON(http.StatusInternalServerError, gin.H{"error": err.Error()})
		return
	}

	c.JSON(http.StatusOK, detail)
}

// ApproveAll promotes the raw ontology to reviewed for an entire chunk.
// POST /api/v1/tenants/:tenant_id/ontology/review/chunks/:chunkId/approve_all
func (h *OntologyReviewHandler) ApproveAll(c *gin.Context) {
	ctx := c.Request.Context()
	tenantID := c.GetUint64(types.TenantIDContextKey.String())
	chunkID := c.Param("chunkId")
	reviewerID := c.GetUint64(types.UserIDContextKey.String())

	detail, err := h.reviewService.ApproveAll(ctx, tenantID, chunkID, reviewerID)
	if err != nil {
		logger.Errorf(ctx, "Failed to approve all: %v", err)
		c.JSON(http.StatusInternalServerError, gin.H{"error": err.Error()})
		return
	}

	c.JSON(http.StatusOK, detail)
}
