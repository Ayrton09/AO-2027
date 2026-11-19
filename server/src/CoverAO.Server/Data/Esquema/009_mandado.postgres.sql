ALTER TABLE personajes ADD COLUMN ultimo_mandado bigint NOT NULL DEFAULT 0 CHECK (ultimo_mandado >= 0);
