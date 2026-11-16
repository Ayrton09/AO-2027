-- CoverAO: el esquema de la base, versión 8, en Postgres. La misma que 008_el_perdon.sqlite.sql: EL NIVEL DEL ÚLTIMO PERDÓN
-- (regla de la casa UnPerdonPorNivel). 0 = nunca pidió uno, que es como quedan los que ya estaban.

ALTER TABLE personajes ADD COLUMN nivel_del_perdon integer NOT NULL DEFAULT 0 CHECK (nivel_del_perdon >= 0);
