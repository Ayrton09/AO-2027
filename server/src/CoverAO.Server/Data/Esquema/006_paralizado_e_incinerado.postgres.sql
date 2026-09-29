-- CoverAO: el esquema de la base, versión 6, en Postgres. La misma que 006_paralizado_e_incinerado.sqlite.sql: PARALIZADO E
-- INCINERADO AL SALIR, que el original guarda en el .chr y devuelve al entrar. Los personajes que ya estaban quedan sin
-- ninguno de los dos.

ALTER TABLE personajes ADD COLUMN paralizado boolean NOT NULL DEFAULT false;
ALTER TABLE personajes ADD COLUMN incinerado boolean NOT NULL DEFAULT false;

-- Y LAS VISTAS QUE FILTRAN, CON BARRERA. Sin ella, Postgres puede evaluar una condición del que consulta —una función suya
-- que anote lo que ve— antes que el filtro de la vista, y así ver las filas del staff y de los borrados. Con
-- security_barrier el filtro de la vista va primero. Las demás leen de éstas, o no filtran.
ALTER VIEW publico.personajes SET (security_barrier = true);
ALTER VIEW publico.conectados SET (security_barrier = true);
