-- CoverAO: el esquema de la base, versión 7, en SQLite. EL CENTRO DE SUBASTAS (regla de la casa, 06/10): las subastas
-- abiertas y lo que el Centro le debe a cada personaje hasta que lo pueda poner en su bóveda.

-- Una fila por subasta abierta. comprador vacío = nadie ofertó; precio_final 0 = sin compra directa; termina en segundos
-- Unix (UTC).
CREATE TABLE subastas (
    id              INTEGER NOT NULL PRIMARY KEY,
    vendedor        TEXT    NOT NULL,
    objeto          INTEGER NOT NULL CHECK (objeto BETWEEN -32768 AND 32767),
    cantidad        INTEGER NOT NULL CHECK (cantidad BETWEEN -32768 AND 32767),
    precio_inicial  INTEGER NOT NULL,
    precio_final    INTEGER NOT NULL,
    oferta          INTEGER NOT NULL,
    comprador       TEXT    NOT NULL,
    termina         INTEGER NOT NULL
) STRICT;

-- Lo pendiente de entregar, en el orden en que se debió: un objeto, oro, o las dos cosas.
CREATE TABLE subastas_entregas (
    orden     INTEGER NOT NULL PRIMARY KEY,
    personaje TEXT    NOT NULL,
    objeto    INTEGER NOT NULL CHECK (objeto BETWEEN -32768 AND 32767),
    cantidad  INTEGER NOT NULL CHECK (cantidad BETWEEN -32768 AND 32767),
    oro       INTEGER NOT NULL
) STRICT;
