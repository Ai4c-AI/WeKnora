DROP INDEX IF EXISTS idx_chunks_ontology_class_ids;
DROP INDEX IF EXISTS idx_chunks_ontology_extracted;

ALTER TABLE chunks
  DROP COLUMN IF EXISTS instance_facts_json,
  DROP COLUMN IF EXISTS ontology_confidence,
  DROP COLUMN IF EXISTS ontology_extracted_at,
  DROP COLUMN IF EXISTS ontology_json;
