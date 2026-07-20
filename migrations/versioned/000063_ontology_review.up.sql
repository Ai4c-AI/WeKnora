-- +goose Up
-- 000063_ontology_review.up.sql
-- Expert review loop for ontology slices: reviewed storage, review queue, audit log.

-- 1. Extend chunks table with reviewed ontology fields
ALTER TABLE chunks
  ADD COLUMN ontology_json_reviewed   JSONB       DEFAULT NULL,
  ADD COLUMN ontology_review_status   TEXT        DEFAULT 'pending'
      CHECK (ontology_review_status IN
             ('pending', 'in_review', 'approved', 'rejected', 'no_review')),
  ADD COLUMN ontology_reviewed_by     BIGINT      REFERENCES users(id),
  ADD COLUMN ontology_reviewed_at     TIMESTAMPTZ DEFAULT NULL;

-- 2. Review queue table (with display-friendly snapshot columns)
CREATE TABLE ontology_review_queue (
    id                BIGSERIAL PRIMARY KEY,
    tenant_id         BIGINT NOT NULL,
    knowledge_base_id TEXT NOT NULL,
    chunk_id          TEXT NOT NULL,
    knowledge_title   TEXT NOT NULL DEFAULT '',
    content_preview   TEXT NOT NULL DEFAULT '',
    priority          INT NOT NULL DEFAULT 50,
    priority_reason   TEXT,
    assigned_to       BIGINT REFERENCES users(id),
    status            TEXT NOT NULL DEFAULT 'pending'
        CHECK (status IN ('pending', 'in_review', 'approved', 'rejected', 'no_review')),
    created_at        TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at        TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE (tenant_id, chunk_id)
);

CREATE INDEX idx_review_queue_prio ON ontology_review_queue
    (tenant_id, knowledge_base_id, status, priority DESC);

-- 3. Audit log table (append-only, never overwrite)
CREATE TABLE ontology_review_audit (
    id            BIGSERIAL PRIMARY KEY,
    tenant_id     BIGINT NOT NULL,
    chunk_id      TEXT NOT NULL,
    reviewer_id   BIGINT NOT NULL,
    action        TEXT NOT NULL
        CHECK (action IN ('accept', 'reject', 'edit', 'approve_all')),
    target_kind   TEXT NOT NULL
        CHECK (target_kind IN ('class', 'property', 'shape', 'alias', 'axiom')),
    target_id     TEXT NOT NULL,
    before_value  JSONB,
    after_value   JSONB,
    created_at    TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_review_audit_chunk ON ontology_review_audit (tenant_id, chunk_id);
