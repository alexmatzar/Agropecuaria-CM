CREATE TABLE cloud_user_provisioning (
    tenant_id TEXT NOT NULL, store_id TEXT NOT NULL, operation_id TEXT NOT NULL,
    target_uid TEXT NOT NULL UNIQUE, operator_id TEXT NOT NULL,
    request_hash TEXT NOT NULL, email TEXT NOT NULL, display_name TEXT NOT NULL, role TEXT NOT NULL,
    state TEXT NOT NULL CHECK(state IN ('Pending','Completed','Conflict')),
    result_json TEXT, created_at TEXT NOT NULL, completed_at TEXT,
    PRIMARY KEY(tenant_id,store_id,operation_id), UNIQUE(tenant_id,store_id,email),
    FOREIGN KEY(tenant_id,store_id) REFERENCES cloud_stores(tenant_id,store_id));
CREATE TABLE cloud_account_audit (
    sequence BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    tenant_id TEXT NOT NULL, store_id TEXT NOT NULL, actor_uid TEXT NOT NULL,
    action TEXT NOT NULL, subject_id TEXT, operation_id TEXT, recorded_at TEXT NOT NULL,
    details_json TEXT NOT NULL,
    FOREIGN KEY(tenant_id,store_id) REFERENCES cloud_stores(tenant_id,store_id));
CREATE INDEX ix_cloud_account_audit_scope ON cloud_account_audit(tenant_id,store_id,sequence DESC);
