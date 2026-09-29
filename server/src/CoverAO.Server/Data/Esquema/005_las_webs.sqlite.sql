-- CoverAO: el esquema de la base, versión 5, en SQLite. LO QUE LEEN LAS WEBS (fase5/postgres/DISENO.md, secciones 2 y 8): los
-- nombres del staff y el catálogo del juego, que el servidor copia en cada arranque, y la versión del servidor. En SQLite
-- no hay vistas ni usuario web —el que juega solo no tiene webs—, pero las tablas son las mismas que en Postgres.

-- Los nombres del staff del Server.ini, SÓLO para dejarlos afuera de lo público. El juego nunca los lee de acá para dar
-- permisos: la autoridad es el Server.ini.
CREATE TABLE staff_visible (
    nombre  TEXT NOT NULL PRIMARY KEY
) STRICT;

-- El catálogo del juego, copiado de gamedata/ en cada arranque: para que una web muestre "Espada Larga" y no "2". La
-- autoridad sigue siendo gamedata/.
CREATE TABLE catalogo_objetos (
    id      INTEGER NOT NULL PRIMARY KEY,
    nombre  TEXT    NOT NULL,
    tipo    INTEGER NOT NULL
) STRICT;
CREATE TABLE catalogo_hechizos (
    id      INTEGER NOT NULL PRIMARY KEY,
    nombre  TEXT    NOT NULL
) STRICT;
CREATE TABLE catalogo_clases (
    id      INTEGER NOT NULL PRIMARY KEY,
    nombre  TEXT    NOT NULL
) STRICT;
CREATE TABLE catalogo_razas (
    id      INTEGER NOT NULL PRIMARY KEY,
    nombre  TEXT    NOT NULL
) STRICT;
CREATE TABLE catalogo_mapas (
    id      INTEGER NOT NULL PRIMARY KEY,
    nombre  TEXT    NOT NULL
) STRICT;

-- La versión del servidor que abrió la base por última vez.
ALTER TABLE mundo ADD COLUMN version_servidor TEXT NOT NULL DEFAULT '';
