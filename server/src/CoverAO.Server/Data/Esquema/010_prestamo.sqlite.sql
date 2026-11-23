ALTER TABLE personajes ADD COLUMN deuda_banco INTEGER NOT NULL DEFAULT 0 CHECK (deuda_banco >= 0);
