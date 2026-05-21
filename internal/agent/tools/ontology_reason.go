package tools

import (
	"context"
	"encoding/json"
	"fmt"
	"strings"

	"github.com/Tencent/WeKnora/internal/types"
	"github.com/Tencent/WeKnora/internal/types/interfaces"
	"github.com/Tencent/WeKnora/internal/utils"
)

var ontologyReasonTool = BaseTool{
	name: ToolOntologyReason,
	description: `Perform formal reasoning over knowledge base ontology fragments using RDFS, N3 rules, and SHACL validation.

## Core Function
Executes structural reasoning on chunk-scoped ontology data stored in the knowledge base.

## When to Use
Use for transitive queries, type hierarchy checks, schema constraint validation, mutual exclusion checks, and SPARQL queries over inferred knowledge.

Do not use for fuzzy text search, entity relationship browsing, or general Q&A; use knowledge_search or query_knowledge_graph instead.

## Parameters
- knowledge_base_ids (required): Array of knowledge base IDs (1-10)
- query (required): Natural language query or SPARQL query
- reasoner_profile (optional): rdfs, n3-extended (default), or shacl
- include_evidence (optional): Include source chunk references`,
	schema: utils.GenerateSchema[OntologyReasonInput](),
}

type OntologyReasonInput struct {
	KnowledgeBaseIDs []string `json:"knowledge_base_ids" jsonschema:"Array of knowledge base IDs to reason over"`
	Query            string   `json:"query" jsonschema:"Natural language or SPARQL query"`
	ReasonerProfile  string   `json:"reasoner_profile,omitempty" jsonschema:"Reasoning profile: rdfs, n3-extended, shacl"`
	IncludeEvidence  bool     `json:"include_evidence,omitempty" jsonschema:"Include evidence chunk references"`
}

type OntologyReasonClient interface {
	Reason(ctx context.Context, req *types.OntologyReasonRequest) (*types.OntologyReasonResponse, error)
}

type OntologyReasonTool struct {
	BaseTool
	knowledgeService interfaces.KnowledgeBaseService
	ontologyClient   OntologyReasonClient
}

func NewOntologyReasonTool(
	knowledgeService interfaces.KnowledgeBaseService,
	ontologyClient OntologyReasonClient,
) *OntologyReasonTool {
	return &OntologyReasonTool{
		BaseTool:         ontologyReasonTool,
		knowledgeService: knowledgeService,
		ontologyClient:   ontologyClient,
	}
}

func (t *OntologyReasonTool) Execute(ctx context.Context, args json.RawMessage) (*types.ToolResult, error) {
	var input OntologyReasonInput
	if err := json.Unmarshal(args, &input); err != nil {
		return &types.ToolResult{
			Success: false,
			Error:   fmt.Sprintf("Failed to parse args: %v", err),
		}, err
	}

	if len(input.KnowledgeBaseIDs) == 0 {
		return &types.ToolResult{
			Success: false,
			Error:   "knowledge_base_ids is required and must be a non-empty array",
		}, fmt.Errorf("knowledge_base_ids is required")
	}
	if len(input.KnowledgeBaseIDs) > 10 {
		return &types.ToolResult{
			Success: false,
			Error:   "knowledge_base_ids must contain at most 10 KB IDs",
		}, fmt.Errorf("too many KB IDs")
	}
	if strings.TrimSpace(input.Query) == "" {
		return &types.ToolResult{
			Success: false,
			Error:   "query is required",
		}, fmt.Errorf("query is required")
	}

	tenantID, ok := types.TenantIDFromContext(ctx)
	if !ok {
		return &types.ToolResult{
			Success: false,
			Error:   "tenant ID is missing from context",
		}, fmt.Errorf("tenant ID is missing from context")
	}

	profile := input.ReasonerProfile
	if profile == "" {
		profile = "n3-extended"
	}

	searchParams := types.SearchParams{
		QueryText:  input.Query,
		MatchCount: 50,
	}

	allChunkIDs := make([]string, 0, 50)
	seenChunks := make(map[string]bool)
	for _, kbID := range input.KnowledgeBaseIDs {
		results, err := t.knowledgeService.HybridSearch(ctx, kbID, searchParams)
		if err != nil {
			continue
		}
		for _, result := range results {
			if result == nil || result.ID == "" || seenChunks[result.ID] {
				continue
			}
			seenChunks[result.ID] = true
			allChunkIDs = append(allChunkIDs, result.ID)
			if len(allChunkIDs) >= 50 {
				break
			}
		}
		if len(allChunkIDs) >= 50 {
			break
		}
	}

	if len(allChunkIDs) == 0 {
		return &types.ToolResult{
			Success: true,
			Output:  "No relevant chunks found for ontology reasoning.",
		}, nil
	}

	resp, err := t.ontologyClient.Reason(ctx, &types.OntologyReasonRequest{
		TenantID:         tenantID,
		KnowledgeBaseIDs: input.KnowledgeBaseIDs,
		ChunkIDs:         allChunkIDs,
		Query: types.OntologyQuery{
			Type:    "entailment",
			Body:    input.Query,
			Profile: profile,
		},
	})
	if err != nil {
		return &types.ToolResult{
			Success: false,
			Error:   fmt.Sprintf("Ontology reasoning failed: %v", err),
		}, nil
	}

	return &types.ToolResult{
		Success: true,
		Output:  formatOntologyResult(resp, input.IncludeEvidence),
		Data: map[string]interface{}{
			"status":           resp.Status,
			"results":          resp.Results,
			"inferred_triples": resp.InferredTriples,
			"evidence_chunks":  resp.EvidenceChunks,
			"elapsed_ms":       resp.ElapsedMs,
			"display_type":     "ontology_reason_results",
		},
	}, nil
}

func formatOntologyResult(resp *types.OntologyReasonResponse, includeEvidence bool) string {
	var sb strings.Builder
	sb.WriteString("=== Ontology Reasoning Results ===\n\n")
	sb.WriteString(fmt.Sprintf("Status: %s\n", resp.Status))
	sb.WriteString(fmt.Sprintf("Elapsed: %dms\n", resp.ElapsedMs))
	sb.WriteString(fmt.Sprintf("Data Source: %s\n\n", resp.DataSource))

	if len(resp.Results) > 0 {
		sb.WriteString("=== Query Results ===\n")
		for i, result := range resp.Results {
			sb.WriteString(fmt.Sprintf("\nResult #%d:\n", i+1))
			for key, value := range result {
				sb.WriteString(fmt.Sprintf("  %s: %v\n", key, value))
			}
		}
		sb.WriteString("\n")
	}

	if len(resp.InferredTriples) > 0 {
		sb.WriteString(fmt.Sprintf("=== Inferred Triples (%d) ===\n", len(resp.InferredTriples)))
		for _, triple := range resp.InferredTriples {
			sb.WriteString(fmt.Sprintf("  %s -[%s]-> %s\n", triple.Subject, triple.Predicate, triple.Object))
		}
		sb.WriteString("\n")
	}

	if includeEvidence && len(resp.EvidenceChunks) > 0 {
		sb.WriteString(fmt.Sprintf("=== Evidence Chunks (%d) ===\n", len(resp.EvidenceChunks)))
		for _, chunkID := range resp.EvidenceChunks {
			sb.WriteString(fmt.Sprintf("  - %s\n", chunkID))
		}
		sb.WriteString("\n")
	}

	if len(resp.Warnings) > 0 {
		sb.WriteString("=== Warnings ===\n")
		for _, warning := range resp.Warnings {
			sb.WriteString(fmt.Sprintf("  %s\n", warning))
		}
	}

	return sb.String()
}
