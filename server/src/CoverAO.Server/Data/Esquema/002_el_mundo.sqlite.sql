-- CoverAO: el esquema de la base, versión 2, en SQLite. LO QUE GUARDA EL MUNDO, en tablas: los clanes —con sus miembros,
-- relaciones, propuestas, aspirantes y votos—, los rechazos de clan, el correo, el suelo, las llaves vendidas y el récord.
--
-- Todo va por NOMBRE, como en el juego: el líder, los miembros, el clan aliado, el votante, el destinatario. Una referencia
-- entre tablas rechazaría lo que el juego produce en el orden en que lo produce —un clan recién fundado cuyo volcado todavía
-- no llegó, una carta a alguien que se borró—, y un guardado rechazado es un mundo perdido.
--
-- Las listas conservan el orden del juego —el de los aspirantes es el que ve el líder—: se leen en el orden en que se
-- escribieron (rowid).
--
-- Lo que una versión 1 guardaba como JSON en `mundo_json` lo pasa el servidor a estas tablas al abrir la base, en la misma
-- transacción (AlmacenSqlite), y esa tabla se va.

CREATE TABLE clanes (
    id                   INTEGER PRIMARY KEY,
    nombre               TEXT    NOT NULL,
    lider                TEXT    NOT NULL,
    descripcion          TEXT    NOT NULL,
    noticias             TEXT    NOT NULL,
    alineacion           INTEGER NOT NULL CHECK (alineacion BETWEEN 0 AND 255),
    url                  TEXT    NOT NULL,
    codex                TEXT    NOT NULL,
    elecciones_abiertas  INTEGER NOT NULL CHECK (elecciones_abiertas IN (0, 1))
) STRICT;
CREATE UNIQUE INDEX clanes_nombre ON clanes (upper(nombre));

CREATE TABLE clanes_miembros (
    clan_id  INTEGER NOT NULL REFERENCES clanes (id) ON DELETE CASCADE,
    nombre   TEXT    NOT NULL
) STRICT;
CREATE INDEX clanes_miembros_clan ON clanes_miembros (clan_id);

CREATE TABLE clanes_relaciones (
    clan_id    INTEGER NOT NULL REFERENCES clanes (id) ON DELETE CASCADE,
    otro_clan  TEXT    NOT NULL,
    relacion   TEXT    NOT NULL CHECK (relacion IN ('aliado', 'enemigo'))
) STRICT;
CREATE INDEX clanes_relaciones_clan ON clanes_relaciones (clan_id);

CREATE TABLE clanes_propuestas (                 -- las RECIBIDAS por clan_id
    clan_id  INTEGER NOT NULL REFERENCES clanes (id) ON DELETE CASCADE,
    de_clan  TEXT    NOT NULL,
    tipo     TEXT    NOT NULL CHECK (tipo IN ('alianza', 'paz')),
    texto    TEXT    NOT NULL
) STRICT;
CREATE INDEX clanes_propuestas_clan ON clanes_propuestas (clan_id);

CREATE TABLE clanes_aspirantes (
    clan_id    INTEGER NOT NULL REFERENCES clanes (id) ON DELETE CASCADE,
    nombre     TEXT    NOT NULL,
    solicitud  TEXT    NOT NULL
) STRICT;
CREATE INDEX clanes_aspirantes_clan ON clanes_aspirantes (clan_id);

CREATE TABLE clanes_votos (
    clan_id    INTEGER NOT NULL REFERENCES clanes (id) ON DELETE CASCADE,
    votante    TEXT    NOT NULL,
    candidato  TEXT    NOT NULL
) STRICT;
CREATE INDEX clanes_votos_clan ON clanes_votos (clan_id);

-- El motivo con el que se rechazó a un aspirante, hasta que entra y lo ve.
CREATE TABLE rechazos_de_clan (
    personaje  TEXT PRIMARY KEY,
    motivo     TEXT NOT NULL
) STRICT;

-- El orden de las cartas de una casilla es el de su id: el cliente las señala por posición.
CREATE TABLE correo (
    id            INTEGER PRIMARY KEY AUTOINCREMENT,
    destinatario  TEXT    NOT NULL,
    remitente     TEXT    NOT NULL,
    mensaje       TEXT    NOT NULL,
    objeto        INTEGER NOT NULL CHECK (objeto BETWEEN -32768 AND 32767),
    cantidad      INTEGER NOT NULL CHECK (cantidad BETWEEN -32768 AND 32767),
    leida         INTEGER NOT NULL CHECK (leida BETWEEN 0 AND 255)
) STRICT;
CREATE INDEX correo_casilla ON correo (upper(destinatario), id);

-- Lo que dejaron los jugadores en el suelo (regla de la casa PersistirSuelo): un objeto por baldosa, como en el mundo.
CREATE TABLE suelo (
    mapa      INTEGER NOT NULL CHECK (mapa BETWEEN -32768 AND 32767),
    x         INTEGER NOT NULL CHECK (x BETWEEN 0 AND 255),
    y         INTEGER NOT NULL CHECK (y BETWEEN 0 AND 255),
    objeto    INTEGER NOT NULL CHECK (objeto BETWEEN -32768 AND 32767),
    cantidad  INTEGER NOT NULL CHECK (cantidad BETWEEN -32768 AND 32767),
    PRIMARY KEY (mapa, x, y)
) STRICT, WITHOUT ROWID;

-- Las llaves de casa ya vendidas: la ficha del mercader, la ranura y la llave.
CREATE TABLE llaves_vendidas (
    mercader  INTEGER NOT NULL,
    ranura    INTEGER NOT NULL,
    objeto    INTEGER NOT NULL CHECK (objeto BETWEEN -32768 AND 32767),
    PRIMARY KEY (mercader, ranura)
) STRICT, WITHOUT ROWID;

-- Una sola fila: el pico histórico de conectados.
CREATE TABLE mundo (
    id               INTEGER PRIMARY KEY CHECK (id = 1),
    record_usuarios  INTEGER NOT NULL DEFAULT 0
) STRICT;
INSERT INTO mundo (id, record_usuarios) VALUES (1, 0);
