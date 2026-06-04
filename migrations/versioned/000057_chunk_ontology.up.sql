ALTER TABLE chunks
  ADD COLUMN ontology_json          JSONB       DEFAULT NULL,
  ADD COLUMN ontology_extracted_at  TIMESTAMPTZ DEFAULT NULL,
  ADD COLUMN ontology_confidence    REAL        DEFAULT NULL,
  ADD COLUMN instance_facts_json    JSONB       DEFAULT NULL;

CREATE INDEX idx_chunks_ontology_extracted
  ON chunks (tenant_id, knowledge_base_id)
  WHERE ontology_json IS NOT NULL;

CREATE INDEX idx_chunks_ontology_class_ids
  ON chunks USING GIN ((jsonb_path_query_array(ontology_json, '$.classes[*].id')));
