ALTER TABLE personajes ADD COLUMN ultimo_mandado INTEGER NOT NULL DEFAULT 0 CHECK (ultimo_mandado >= 0);
