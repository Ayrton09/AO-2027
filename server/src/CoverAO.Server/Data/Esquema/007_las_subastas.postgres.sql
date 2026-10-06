-- CoverAO: el esquema de la base, versión 7, en Postgres. La misma que 007_las_subastas.sqlite.sql: EL CENTRO DE SUBASTAS
-- (regla de la casa, 06/10), las subastas abiertas y lo que el Centro le debe a cada personaje.

-- Una fila por subasta abierta. comprador vacío = nadie ofertó; precio_final 0 = sin compra directa; termina en segundos
-- Unix (UTC).
CREATE TABLE subastas (
    id              integer  NOT NULL PRIMARY KEY,
    vendedor        text     NOT NULL,
    objeto          smallint NOT NULL,
    cantidad        smallint NOT NULL,
    precio_inicial  bigint   NOT NULL,
    precio_final    bigint   NOT NULL,
    oferta          bigint   NOT NULL,
    comprador       text     NOT NULL,
    termina         bigint   NOT NULL
);

-- Lo pendiente de entregar, en el orden en que se debió: un objeto, oro, o las dos cosas.
CREATE TABLE subastas_entregas (
    orden     integer  NOT NULL PRIMARY KEY,
    personaje text     NOT NULL,
    objeto    smallint NOT NULL,
    cantidad  smallint NOT NULL,
    oro       bigint   NOT NULL
);
