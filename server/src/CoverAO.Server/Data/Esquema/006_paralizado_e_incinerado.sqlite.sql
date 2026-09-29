-- CoverAO: el esquema de la base, versión 6, en SQLite. PARALIZADO E INCINERADO AL SALIR, que el original guarda en el .chr
-- (FileIO.bas:1635-1636) y devuelve al entrar (TCP.bas:1869-1882): sin ellos, salir y volver a entrar sacaba la parálisis
-- y el fuego. Los personajes que ya estaban quedan sin ninguno de los dos.

ALTER TABLE personajes ADD COLUMN paralizado INTEGER NOT NULL DEFAULT 0 CHECK (paralizado IN (0, 1));
ALTER TABLE personajes ADD COLUMN incinerado INTEGER NOT NULL DEFAULT 0 CHECK (incinerado IN (0, 1));
