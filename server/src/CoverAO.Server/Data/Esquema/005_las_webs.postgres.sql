-- CoverAO: el esquema de la base, versión 5, en Postgres. La misma que 005_las_webs.sqlite.sql —los nombres del staff, el
-- catálogo del juego y la versión del servidor—, y además LO QUE VE UNA PÁGINA WEB (fase5/postgres/DISENO.md, sección 8): el
-- esquema `publico`, con vistas. El usuario de la web (`CoverAO.Server crear-usuario-web`) sólo puede leer estas vistas; las
-- vistas corren con los permisos de su dueño —el usuario del juego—, así que la web ve lo de abajo sin poder tocar las
-- tablas. De las cuentas no se ve nada: ni contraseñas, ni códigos, ni IPs, ni baneos. Tampoco el oro, la bóveda, la
-- posición ni el inventario. Y el staff y los personajes borrados quedan afuera.

CREATE TABLE staff_visible (
    nombre  text NOT NULL PRIMARY KEY
);

CREATE TABLE catalogo_objetos (
    id      integer NOT NULL PRIMARY KEY,
    nombre  text    NOT NULL,
    tipo    integer NOT NULL
);
CREATE TABLE catalogo_hechizos (
    id      integer NOT NULL PRIMARY KEY,
    nombre  text    NOT NULL
);
CREATE TABLE catalogo_clases (
    id      integer NOT NULL PRIMARY KEY,
    nombre  text    NOT NULL
);
CREATE TABLE catalogo_razas (
    id      integer NOT NULL PRIMARY KEY,
    nombre  text    NOT NULL
);
CREATE TABLE catalogo_mapas (
    id      integer NOT NULL PRIMARY KEY,
    nombre  text    NOT NULL
);

ALTER TABLE mundo ADD COLUMN version_servidor text NOT NULL DEFAULT '';

-- IF NOT EXISTS: restaurar un backup rehace las tablas y, con ellas, esta versión; el esquema sobrevive a las tablas.
CREATE SCHEMA IF NOT EXISTS publico;

CREATE VIEW publico.personajes AS
SELECT p.nombre, p.nivel, p.exp AS experiencia, p.clase, c.nombre AS clase_nombre, p.raza, r.nombre AS raza_nombre,
       p.genero, nullif(p.clan, '') AS clan, p.bando, p.veces_muerto, p.usuarios_matados, p.criaturas_matadas,
       p.segundos_jugados, p.creado_en, p.conectado_desde IS NOT NULL AS conectado
FROM personajes p
LEFT JOIN catalogo_clases c ON c.id = p.clase
LEFT JOIN catalogo_razas r ON r.id = p.raza
WHERE p.borrado_en IS NULL
  AND upper(p.nombre) NOT IN (SELECT upper(s.nombre) FROM staff_visible s);

-- La tabla de posiciones, como la de la página del servidor: nivel, experiencia y nombre.
CREATE VIEW publico.ranking AS
SELECT row_number() OVER (ORDER BY p.nivel DESC, p.experiencia DESC, p.nombre) AS puesto, p.*
FROM publico.personajes p;

CREATE VIEW publico.clanes AS
SELECT c.nombre, c.alineacion, c.lider, c.descripcion, c.noticias, c.url,
       (SELECT count(*) FROM clanes_miembros m WHERE m.clan_id = c.id) AS miembros
FROM clanes c;

CREATE VIEW publico.miembros_de_clan AS
SELECT c.nombre AS clan, p.nombre, p.nivel, p.clase, p.clase_nombre
FROM clanes_miembros m
JOIN clanes c ON c.id = m.clan_id
JOIN publico.personajes p ON upper(p.nombre) = upper(m.nombre);

CREATE VIEW publico.conectados AS
SELECT p.nombre, p.nivel, p.clase, c.nombre AS clase_nombre, p.conectado_desde AS desde
FROM personajes p
LEFT JOIN catalogo_clases c ON c.id = p.clase
WHERE p.conectado_desde IS NOT NULL AND p.borrado_en IS NULL
  AND upper(p.nombre) NOT IN (SELECT upper(s.nombre) FROM staff_visible s);

CREATE VIEW publico.servidor AS
SELECT m.version_servidor AS version, m.record_usuarios AS record_de_conectados,
       (SELECT count(*) FROM publico.conectados) AS conectados
FROM mundo m;

CREATE VIEW publico.catalogo_objetos AS SELECT id, nombre, tipo FROM catalogo_objetos;
CREATE VIEW publico.catalogo_hechizos AS SELECT id, nombre FROM catalogo_hechizos;
CREATE VIEW publico.catalogo_clases AS SELECT id, nombre FROM catalogo_clases;
CREATE VIEW publico.catalogo_razas AS SELECT id, nombre FROM catalogo_razas;
CREATE VIEW publico.catalogo_mapas AS SELECT id, nombre FROM catalogo_mapas;
