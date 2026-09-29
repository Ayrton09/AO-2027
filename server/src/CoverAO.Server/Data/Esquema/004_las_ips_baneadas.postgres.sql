-- CoverAO: el esquema de la base, versión 4, en Postgres. La misma que 004_las_ips_baneadas.sqlite.sql: LAS IPS BANEADAS,
-- una fila por IP, tal como la escribió el que baneó. Texto y no inet: lo que se compara al conectarse es el texto.

CREATE TABLE ips_baneadas (
    ip  text NOT NULL PRIMARY KEY
);
