BEGIN;

CREATE INDEX runs_search_id_desc_idx ON runs (id DESC);
CREATE INDEX runs_search_type_id_desc_idx ON runs
    ((split_part(workflow_id,'/',1)||'/'||split_part(workflow_id,'/',2)), id DESC);
CREATE INDEX runs_search_type_status_id_desc_idx ON runs
    ((split_part(workflow_id,'/',1)||'/'||split_part(workflow_id,'/',2)), status, id DESC);
CREATE INDEX runs_search_status_id_desc_idx ON runs (status, id DESC);
CREATE INDEX runs_search_created_id_desc_idx ON runs (created_at, id DESC);

COMMIT;
