-- CoverAO: el esquema de la base, versión 4, en SQLite. LAS IPS BANEADAS, que vivían en data/banned-ips.txt (el
-- Dat/BanIps.dat del original): una fila por IP, tal como la escribió el que baneó.

CREATE TABLE ips_baneadas (
    ip  TEXT NOT NULL PRIMARY KEY
) STRICT;
