-- CoverAO: el esquema de la base, versión 3, en SQLite. EL ORDEN DE LAS LISTAS DEL MUNDO, en una columna.
--
-- Los miembros, relaciones, propuestas, aspirantes y votos de un clan, y los rechazos de clan, se leen en el orden en que se
-- escribieron. Hasta la versión 2 ese orden era el `rowid` de SQLite, que Postgres no tiene: las dos bases leen ahora por
-- `id`. En SQLite, `id INTEGER PRIMARY KEY` ES el rowid con nombre, así que cada tabla se rehace copiando el rowid al `id` y
-- el orden no cambia.
--
-- Nadie referencia a estas tablas: se las puede tirar y renombrar con las claves foráneas prendidas.

CREATE TABLE clanes_miembros_v3 (
    id       INTEGER PRIMARY KEY,
    clan_id  INTEGER NOT NULL REFERENCES clanes (id) ON DELETE CASCADE,
    nombre   TEXT    NOT NULL
) STRICT;
INSERT INTO clanes_miembros_v3 (id, clan_id, nombre) SELECT rowid, clan_id, nombre FROM clanes_miembros;
DROP TABLE clanes_miembros;
ALTER TABLE clanes_miembros_v3 RENAME TO clanes_miembros;
CREATE INDEX clanes_miembros_clan ON clanes_miembros (clan_id);

CREATE TABLE clanes_relaciones_v3 (
    id         INTEGER PRIMARY KEY,
    clan_id    INTEGER NOT NULL REFERENCES clanes (id) ON DELETE CASCADE,
    otro_clan  TEXT    NOT NULL,
    relacion   TEXT    NOT NULL CHECK (relacion IN ('aliado', 'enemigo'))
) STRICT;
INSERT INTO clanes_relaciones_v3 (id, clan_id, otro_clan, relacion) SELECT rowid, clan_id, otro_clan, relacion FROM clanes_relaciones;
DROP TABLE clanes_relaciones;
ALTER TABLE clanes_relaciones_v3 RENAME TO clanes_relaciones;
CREATE INDEX clanes_relaciones_clan ON clanes_relaciones (clan_id);

CREATE TABLE clanes_propuestas_v3 (              -- las RECIBIDAS por clan_id
    id       INTEGER PRIMARY KEY,
    clan_id  INTEGER NOT NULL REFERENCES clanes (id) ON DELETE CASCADE,
    de_clan  TEXT    NOT NULL,
    tipo     TEXT    NOT NULL CHECK (tipo IN ('alianza', 'paz')),
    texto    TEXT    NOT NULL
) STRICT;
INSERT INTO clanes_propuestas_v3 (id, clan_id, de_clan, tipo, texto) SELECT rowid, clan_id, de_clan, tipo, texto FROM clanes_propuestas;
DROP TABLE clanes_propuestas;
ALTER TABLE clanes_propuestas_v3 RENAME TO clanes_propuestas;
CREATE INDEX clanes_propuestas_clan ON clanes_propuestas (clan_id);

CREATE TABLE clanes_aspirantes_v3 (
    id         INTEGER PRIMARY KEY,
    clan_id    INTEGER NOT NULL REFERENCES clanes (id) ON DELETE CASCADE,
    nombre     TEXT    NOT NULL,
    solicitud  TEXT    NOT NULL
) STRICT;
INSERT INTO clanes_aspirantes_v3 (id, clan_id, nombre, solicitud) SELECT rowid, clan_id, nombre, solicitud FROM clanes_aspirantes;
DROP TABLE clanes_aspirantes;
ALTER TABLE clanes_aspirantes_v3 RENAME TO clanes_aspirantes;
CREATE INDEX clanes_aspirantes_clan ON clanes_aspirantes (clan_id);

CREATE TABLE clanes_votos_v3 (
    id         INTEGER PRIMARY KEY,
    clan_id    INTEGER NOT NULL REFERENCES clanes (id) ON DELETE CASCADE,
    votante    TEXT    NOT NULL,
    candidato  TEXT    NOT NULL
) STRICT;
INSERT INTO clanes_votos_v3 (id, clan_id, votante, candidato) SELECT rowid, clan_id, votante, candidato FROM clanes_votos;
DROP TABLE clanes_votos;
ALTER TABLE clanes_votos_v3 RENAME TO clanes_votos;
CREATE INDEX clanes_votos_clan ON clanes_votos (clan_id);

-- El motivo con el que se rechazó a un aspirante, hasta que entra y lo ve. Un motivo por personaje, como antes.
CREATE TABLE rechazos_de_clan_v3 (
    id         INTEGER PRIMARY KEY,
    personaje  TEXT    NOT NULL UNIQUE,
    motivo     TEXT    NOT NULL
) STRICT;
INSERT INTO rechazos_de_clan_v3 (id, personaje, motivo) SELECT rowid, personaje, motivo FROM rechazos_de_clan;
DROP TABLE rechazos_de_clan;
ALTER TABLE rechazos_de_clan_v3 RENAME TO rechazos_de_clan;
