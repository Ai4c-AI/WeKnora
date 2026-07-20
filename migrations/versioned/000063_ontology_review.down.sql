-- +goose Down
DROP INDEX IF EXISTS idx_review_audit_chunk;
DROP TABLE IF EXISTS ontology_review_audit;

DROP INDEX IF EXISTS idx_review_queue_prio;
DROP TABLE IF EXISTS ontology_review_queue;

ALTER TABLE chunks
  DROP COLUMN IF EXISTS ontology_reviewed_at,
  DROP COLUMN IF EXISTS ontology_reviewed_by,
  DROP COLUMN IF EXISTS ontology_review_status,
  DROP COLUMN IF EXISTS ontology_json_reviewed;
