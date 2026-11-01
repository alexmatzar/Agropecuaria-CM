CREATE TABLE IF NOT EXISTS cloud_stores (tenant_id TEXT NOT NULL, store_id TEXT NOT NULL,
    PRIMARY KEY(tenant_id,store_id));
CREATE TABLE IF NOT EXISTS cloud_memberships (
    tenant_id TEXT NOT NULL, store_id TEXT NOT NULL, operator_id TEXT NOT NULL,
    uid TEXT NOT NULL, email TEXT NOT NULL, display_name TEXT NOT NULL,
    role TEXT NOT NULL CHECK(role IN ('Admin','Cashier','Viewer')),
    active INTEGER NOT NULL CHECK(active IN (0,1)), must_change INTEGER NOT NULL CHECK(must_change IN (0,1)),
    version BIGINT NOT NULL CHECK(version>0), PRIMARY KEY(tenant_id,store_id,operator_id),
    UNIQUE(tenant_id,store_id,uid), UNIQUE(tenant_id,store_id,email),
    FOREIGN KEY(tenant_id,store_id) REFERENCES cloud_stores(tenant_id,store_id));
