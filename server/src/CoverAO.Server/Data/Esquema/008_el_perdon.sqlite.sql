-- CoverAO: el esquema de la base, versión 8, en SQLite. EL NIVEL DEL ÚLTIMO PERDÓN (regla de la casa UnPerdonPorNivel): con la
-- regla prendida no se puede pedir otro perdón en el mismo nivel. 0 = nunca pidió uno, que es como quedan los que ya estaban.

ALTER TABLE personajes ADD COLUMN nivel_del_perdon INTEGER NOT NULL DEFAULT 0 CHECK (nivel_del_perdon >= 0);
