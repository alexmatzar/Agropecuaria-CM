CREATE TABLE cloud_replication (
    tenant_id TEXT NOT NULL, store_id TEXT NOT NULL, head_cursor BIGINT NOT NULL DEFAULT 0 CHECK(head_cursor>=0),
    PRIMARY KEY(tenant_id,store_id), FOREIGN KEY(tenant_id,store_id) REFERENCES cloud_stores(tenant_id,store_id));
CREATE TABLE cloud_devices (
    tenant_id TEXT NOT NULL, store_id TEXT NOT NULL, device_id UUID NOT NULL,
    secret_hash BYTEA NOT NULL CHECK(octet_length(secret_hash)=32), active BOOLEAN NOT NULL,
    can_write BOOLEAN NOT NULL, last_sequence BIGINT NOT NULL DEFAULT 0 CHECK(last_sequence>=0),
    registered_by TEXT NOT NULL, created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY(tenant_id,store_id,device_id), FOREIGN KEY(tenant_id,store_id) REFERENCES cloud_stores(tenant_id,store_id));
CREATE UNIQUE INDEX ux_cloud_active_writer ON cloud_devices(tenant_id,store_id) WHERE active AND can_write;
CREATE TABLE cloud_events (
    tenant_id TEXT NOT NULL, store_id TEXT NOT NULL, event_id UUID NOT NULL,
    device_id UUID NOT NULL, sequence BIGINT NOT NULL CHECK(sequence>0), cursor BIGINT NOT NULL CHECK(cursor>0),
    schema_version INTEGER NOT NULL, kind TEXT NOT NULL, envelope_json TEXT NOT NULL,
    content_hash BYTEA NOT NULL CHECK(octet_length(content_hash)=32), accepted_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY(tenant_id,store_id,event_id), UNIQUE(tenant_id,store_id,device_id,sequence),
    UNIQUE(tenant_id,store_id,cursor),
    FOREIGN KEY(tenant_id,store_id,device_id) REFERENCES cloud_devices(tenant_id,store_id,device_id));
CREATE TABLE cloud_products (
    tenant_id TEXT NOT NULL, store_id TEXT NOT NULL, id UUID NOT NULL,
    name TEXT NOT NULL, dimension TEXT NOT NULL CHECK(dimension IN ('Weight','Count')),
    stock_base BIGINT NOT NULL CHECK(stock_base>=0), details_json TEXT,
    PRIMARY KEY(tenant_id,store_id,id), FOREIGN KEY(tenant_id,store_id) REFERENCES cloud_stores(tenant_id,store_id));
CREATE TABLE cloud_prices (
    tenant_id TEXT NOT NULL, store_id TEXT NOT NULL, product_id UUID NOT NULL, unit TEXT NOT NULL,
    unit_price_minor BIGINT NOT NULL CHECK(unit_price_minor>=0),
    PRIMARY KEY(tenant_id,store_id,product_id,unit),
    FOREIGN KEY(tenant_id,store_id,product_id) REFERENCES cloud_products(tenant_id,store_id,id));
CREATE UNIQUE INDEX ux_cloud_barcode ON cloud_products (tenant_id,store_id,((details_json::jsonb)->>'Barcode')) WHERE details_json IS NOT NULL;
CREATE TABLE cloud_sales (
    tenant_id TEXT NOT NULL, store_id TEXT NOT NULL, id UUID NOT NULL, operation_id UUID NOT NULL,
    total_minor BIGINT NOT NULL CHECK(total_minor>=0), currency TEXT NOT NULL CHECK(currency='GTQ'),
    recorded_at TIMESTAMPTZ NOT NULL, operator_id UUID,
    PRIMARY KEY(tenant_id,store_id,id), UNIQUE(tenant_id,store_id,operation_id),
    FOREIGN KEY(tenant_id,store_id,operation_id) REFERENCES cloud_events(tenant_id,store_id,event_id));
CREATE TABLE cloud_sale_lines (
    tenant_id TEXT NOT NULL, store_id TEXT NOT NULL, sale_id UUID NOT NULL, line_number INTEGER NOT NULL,
    product_id UUID NOT NULL, product_name TEXT NOT NULL, unit TEXT NOT NULL, quantity NUMERIC(38,12) NOT NULL CHECK(quantity>0),
    quantity_base BIGINT NOT NULL CHECK(quantity_base>0), unit_price_minor BIGINT NOT NULL CHECK(unit_price_minor>=0),
    total_minor BIGINT NOT NULL CHECK(total_minor>=0),
    PRIMARY KEY(tenant_id,store_id,sale_id,line_number),
    FOREIGN KEY(tenant_id,store_id,sale_id) REFERENCES cloud_sales(tenant_id,store_id,id),
    FOREIGN KEY(tenant_id,store_id,product_id) REFERENCES cloud_products(tenant_id,store_id,id));
CREATE TABLE cloud_inventory_movements (
    tenant_id TEXT NOT NULL, store_id TEXT NOT NULL, event_id UUID NOT NULL, line_number INTEGER NOT NULL,
    product_id UUID NOT NULL, delta_base BIGINT NOT NULL CHECK(delta_base<>0), reason TEXT NOT NULL, recorded_at TIMESTAMPTZ NOT NULL,
    PRIMARY KEY(tenant_id,store_id,event_id,line_number),
    FOREIGN KEY(tenant_id,store_id,event_id) REFERENCES cloud_events(tenant_id,store_id,event_id),
    FOREIGN KEY(tenant_id,store_id,product_id) REFERENCES cloud_products(tenant_id,store_id,id));
CREATE TABLE cloud_legacy_records (
    tenant_id TEXT NOT NULL, store_id TEXT NOT NULL, source_hash TEXT NOT NULL,
    table_name TEXT NOT NULL, row_key TEXT NOT NULL, record_json TEXT NOT NULL,
    PRIMARY KEY(tenant_id,store_id,source_hash,table_name,row_key),
    FOREIGN KEY(tenant_id,store_id) REFERENCES cloud_stores(tenant_id,store_id));
CREATE TABLE cloud_device_audit (
    id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY, tenant_id TEXT NOT NULL, store_id TEXT NOT NULL,
    device_id UUID NOT NULL, actor_uid TEXT NOT NULL, action TEXT NOT NULL, at_utc TIMESTAMPTZ NOT NULL DEFAULT now());
