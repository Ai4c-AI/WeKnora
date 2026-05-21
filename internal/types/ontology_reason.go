package types

type OntologyReasonRequest struct {
	TenantID         uint64        `json:"tenant_id"`
	KnowledgeBaseIDs []string      `json:"knowledge_base_ids"`
	ChunkIDs         []string      `json:"chunk_ids"`
	InstanceFacts    []Triple      `json:"instance_facts,omitempty"`
	Query            OntologyQuery `json:"query"`
}

type OntologyQuery struct {
	Type    string `json:"type"`
	Body    string `json:"body"`
	Profile string `json:"profile,omitempty"`
}

type OntologyReasonResponse struct {
	Status          string                   `json:"status"`
	Results         []map[string]interface{} `json:"results"`
	InferredTriples []Triple                 `json:"inferred_triples"`
	DataSource      string                   `json:"data_source"`
	EvidenceChunks  []string                 `json:"evidence_chunks"`
	Warnings        []string                 `json:"warnings"`
	ElapsedMs       int                      `json:"elapsed_ms"`
}
