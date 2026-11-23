ALTER TABLE personajes ADD COLUMN deuda_banco bigint NOT NULL DEFAULT 0 CHECK (deuda_banco >= 0);
