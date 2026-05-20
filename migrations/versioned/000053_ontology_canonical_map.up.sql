CREATE TABLE ontology_canonical_map (
    id                BIGSERIAL PRIMARY KEY,
    tenant_id         BIGINT NOT NULL,
    knowledge_base_id TEXT NOT NULL,
    kind              TEXT NOT NULL CHECK (kind IN ('class', 'property')),
    canonical_id      TEXT NOT NULL,
    aliases           TEXT[] NOT NULL DEFAULT '{}',
    source_chunks     TEXT[] NOT NULL DEFAULT '{}',
    confidence        REAL NOT NULL DEFAULT 0.5,
    created_at        TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at        TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE (tenant_id, knowledge_base_id, kind, canonical_id)
);

CREATE INDEX idx_canonical_map_aliases
  ON ontology_canonical_map USING GIN (aliases);
