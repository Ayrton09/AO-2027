-- CoverAO: el esquema de la base, versión 1, en SQLite. Ver fase5/postgres/DISENO.md (sección 2 y anexo B).
--
-- Tablas STRICT: rechazan un tipo equivocado, como Postgres. Las fechas van en TEXT (ISO 8601, UTC) y los booleanos en
-- INTEGER 0/1.
--
-- LAS RESTRICCIONES SON LAS DEL TIPO DEL MODELO, no reglas de juego. Un byte del servidor es un CHECK entre 0 y 255, un
-- short entre -32768 y 32767: la base no acepta nada que el servidor no pueda cargar. Pero tampoco puede rechazar nada que
-- el juego produzca, porque un guardado rechazado es una partida perdida. Por eso no hay "cantidad > 0" ni "nivel <= 50":
-- eso lo decide el juego.
--
-- El clan y la pareja van por NOMBRE, como en el .chr, y los amigos también. La membresía del clan y sus tablas son de la
-- versión que pase el mundo a tablas.

CREATE TABLE cuentas (
    id                 INTEGER PRIMARY KEY,
    nombre             TEXT    NOT NULL CHECK (length(nombre) BETWEEN 1 AND 20),
    clave_hash         TEXT    NOT NULL,             -- pbkdf2$... (o el SHA-256 viejo hasta el próximo ingreso)
    clave_sal          TEXT    NOT NULL DEFAULT '',  -- sólo la del SHA-256 viejo
    codigo_hash        TEXT    NOT NULL DEFAULT '',  -- el código de recuperación (UserCodigo)
    baneada            INTEGER NOT NULL DEFAULT 0 CHECK (baneada IN (0, 1)),
    es_bot             INTEGER NOT NULL DEFAULT 0 CHECK (es_bot IN (0, 1)),
    trampas            INTEGER NOT NULL DEFAULT 0 CHECK (trampas BETWEEN -2147483648 AND 2147483647),
    donador            INTEGER NOT NULL DEFAULT 0 CHECK (donador IN (0, 1)),
    email              TEXT,                         -- reservado para una web que escriba
    creada_en          TEXT,                         -- NULL: importada, fecha desconocida
    ultimo_ingreso_en  TEXT,
    ultima_ip          TEXT
) STRICT;
CREATE UNIQUE INDEX cuentas_nombre ON cuentas (upper(nombre));

CREATE TABLE personajes (
    id                    INTEGER PRIMARY KEY,
    cuenta_id             INTEGER NOT NULL REFERENCES cuentas (id) ON DELETE RESTRICT,
    nombre                TEXT    NOT NULL CHECK (length(nombre) BETWEEN 1 AND 20),
    creado_en             TEXT,
    guardado_en           TEXT,
    borrado_en            TEXT,                      -- apartado: el nombre queda libre, la fila no se destruye
    conectado_desde       TEXT,
    -- [INIT]
    genero                INTEGER NOT NULL CHECK (genero BETWEEN 0 AND 255),
    raza                  INTEGER NOT NULL CHECK (raza BETWEEN 0 AND 255),
    clase                 INTEGER NOT NULL CHECK (clase BETWEEN 0 AND 255),
    tierra_natal          INTEGER NOT NULL CHECK (tierra_natal BETWEEN 0 AND 255),
    ciudad_hogar          INTEGER NOT NULL CHECK (ciudad_hogar BETWEEN 0 AND 255),
    minutos_de_carcel     INTEGER NOT NULL CHECK (minutos_de_carcel BETWEEN -2147483648 AND 2147483647),
    descripcion           TEXT    NOT NULL,
    rumbo                 INTEGER NOT NULL CHECK (rumbo BETWEEN 0 AND 255),
    cabeza                INTEGER NOT NULL CHECK (cabeza BETWEEN -32768 AND 32767),
    cuerpo                INTEGER NOT NULL CHECK (cuerpo BETWEEN -32768 AND 32767),
    anim_arma             INTEGER NOT NULL CHECK (anim_arma BETWEEN -32768 AND 32767),
    anim_escudo           INTEGER NOT NULL CHECK (anim_escudo BETWEEN -32768 AND 32767),
    anim_casco            INTEGER NOT NULL CHECK (anim_casco BETWEEN -32768 AND 32767),
    segundos_jugados      INTEGER NOT NULL,
    mapa                  INTEGER NOT NULL CHECK (mapa BETWEEN -32768 AND 32767),
    x                     INTEGER NOT NULL CHECK (x BETWEEN 0 AND 255),
    y                     INTEGER NOT NULL CHECK (y BETWEEN 0 AND 255),
    -- [STATS]
    oro                   INTEGER NOT NULL CHECK (oro BETWEEN -2147483648 AND 2147483647),
    oro_en_banco          INTEGER NOT NULL,
    vida                  INTEGER NOT NULL CHECK (vida BETWEEN -2147483648 AND 2147483647),
    vida_max              INTEGER NOT NULL CHECK (vida_max BETWEEN -2147483648 AND 2147483647),
    energia               INTEGER NOT NULL CHECK (energia BETWEEN -2147483648 AND 2147483647),
    energia_max           INTEGER NOT NULL CHECK (energia_max BETWEEN -2147483648 AND 2147483647),
    mana                  INTEGER NOT NULL CHECK (mana BETWEEN -2147483648 AND 2147483647),
    mana_max              INTEGER NOT NULL CHECK (mana_max BETWEEN -2147483648 AND 2147483647),
    golpe_min             INTEGER NOT NULL CHECK (golpe_min BETWEEN -2147483648 AND 2147483647),
    golpe_max             INTEGER NOT NULL CHECK (golpe_max BETWEEN -2147483648 AND 2147483647),
    sed                   INTEGER NOT NULL CHECK (sed BETWEEN 0 AND 255),
    sed_max               INTEGER NOT NULL CHECK (sed_max BETWEEN 0 AND 255),
    hambre                INTEGER NOT NULL CHECK (hambre BETWEEN 0 AND 255),
    hambre_max            INTEGER NOT NULL CHECK (hambre_max BETWEEN 0 AND 255),
    skill_pts_libres      INTEGER NOT NULL CHECK (skill_pts_libres BETWEEN -32768 AND 32767),
    nivel                 INTEGER NOT NULL CHECK (nivel BETWEEN 0 AND 255),
    exp                   REAL    NOT NULL,
    exp_para_subir        INTEGER NOT NULL,
    -- [ATRIBUTOS]
    fuerza                INTEGER NOT NULL CHECK (fuerza BETWEEN 0 AND 255),
    agilidad              INTEGER NOT NULL CHECK (agilidad BETWEEN 0 AND 255),
    inteligencia          INTEGER NOT NULL CHECK (inteligencia BETWEEN 0 AND 255),
    carisma               INTEGER NOT NULL CHECK (carisma BETWEEN 0 AND 255),
    constitucion          INTEGER NOT NULL CHECK (constitucion BETWEEN 0 AND 255),
    -- [FLAGS]
    privilegios           INTEGER NOT NULL CHECK (privilegios BETWEEN 0 AND 255),   -- el civil: el de staff no se guarda
    baneado               INTEGER NOT NULL CHECK (baneado IN (0, 1)),
    muerto                INTEGER NOT NULL CHECK (muerto IN (0, 1)),
    navegando             INTEGER NOT NULL CHECK (navegando IN (0, 1)),
    montando              INTEGER NOT NULL CHECK (montando IN (0, 1)),
    veneno                INTEGER NOT NULL CHECK (veneno BETWEEN 0 AND 255),
    veces_muerto          INTEGER NOT NULL CHECK (veces_muerto BETWEEN -2147483648 AND 2147483647),
    -- [FACCIONES] y [MUERTES]
    bando                 INTEGER NOT NULL CHECK (bando BETWEEN 0 AND 255),
    rango                 INTEGER NOT NULL CHECK (rango BETWEEN 0 AND 255),
    renegados_matados     INTEGER NOT NULL CHECK (renegados_matados BETWEEN -2147483648 AND 2147483647),
    ciudadanos_matados    INTEGER NOT NULL CHECK (ciudadanos_matados BETWEEN -2147483648 AND 2147483647),
    republicanos_matados  INTEGER NOT NULL CHECK (republicanos_matados BETWEEN -2147483648 AND 2147483647),
    armada_matados        INTEGER NOT NULL CHECK (armada_matados BETWEEN -2147483648 AND 2147483647),
    milicianos_matados    INTEGER NOT NULL CHECK (milicianos_matados BETWEEN -2147483648 AND 2147483647),
    caos_matados          INTEGER NOT NULL CHECK (caos_matados BETWEEN -2147483648 AND 2147483647),
    usuarios_matados      INTEGER NOT NULL CHECK (usuarios_matados BETWEEN -2147483648 AND 2147483647),
    criaturas_matadas     INTEGER NOT NULL CHECK (criaturas_matadas BETWEEN -2147483648 AND 2147483647),
    -- [CASAMIENTO], [GUILD], [DONADOR], BarcoSlot
    pareja                TEXT    NOT NULL,
    clan                  TEXT    NOT NULL,
    donador               INTEGER NOT NULL CHECK (donador IN (0, 1)),
    creditos_donador      INTEGER NOT NULL,
    ranura_del_barco      INTEGER NOT NULL CHECK (ranura_del_barco BETWEEN 0 AND 255)
) STRICT;
CREATE UNIQUE INDEX personajes_nombre_vivo ON personajes (upper(nombre)) WHERE borrado_en IS NULL;
CREATE INDEX personajes_cuenta  ON personajes (cuenta_id);
CREATE INDEX personajes_ranking ON personajes (nivel DESC, exp DESC) WHERE borrado_en IS NULL;

-- Las ranuras: una fila por ranura OCUPADA (objeto distinto de 0). Ranura y objeto con el rango del arreglo y del tipo.
CREATE TABLE inventario (
    personaje_id  INTEGER NOT NULL REFERENCES personajes (id) ON DELETE CASCADE,
    ranura        INTEGER NOT NULL CHECK (ranura BETWEEN 1 AND 25),
    objeto        INTEGER NOT NULL CHECK (objeto BETWEEN -32768 AND 32767 AND objeto <> 0),
    cantidad      INTEGER NOT NULL CHECK (cantidad BETWEEN -32768 AND 32767),
    equipado      INTEGER NOT NULL CHECK (equipado IN (0, 1)),
    PRIMARY KEY (personaje_id, ranura)
) STRICT, WITHOUT ROWID;
CREATE TABLE boveda (
    personaje_id  INTEGER NOT NULL REFERENCES personajes (id) ON DELETE CASCADE,
    ranura        INTEGER NOT NULL CHECK (ranura BETWEEN 1 AND 40),
    objeto        INTEGER NOT NULL CHECK (objeto BETWEEN -32768 AND 32767 AND objeto <> 0),
    cantidad      INTEGER NOT NULL CHECK (cantidad BETWEEN -32768 AND 32767),
    PRIMARY KEY (personaje_id, ranura)
) STRICT, WITHOUT ROWID;
CREATE TABLE hechizos (
    personaje_id  INTEGER NOT NULL REFERENCES personajes (id) ON DELETE CASCADE,
    ranura        INTEGER NOT NULL CHECK (ranura BETWEEN 1 AND 35),
    hechizo       INTEGER NOT NULL CHECK (hechizo BETWEEN -32768 AND 32767 AND hechizo <> 0),
    PRIMARY KEY (personaje_id, ranura)
) STRICT, WITHOUT ROWID;
CREATE TABLE skills (                              -- las 27, siempre
    personaje_id  INTEGER NOT NULL REFERENCES personajes (id) ON DELETE CASCADE,
    skill         INTEGER NOT NULL CHECK (skill BETWEEN 1 AND 27),
    valor         INTEGER NOT NULL CHECK (valor BETWEEN 0 AND 255),
    PRIMARY KEY (personaje_id, skill)
) STRICT, WITHOUT ROWID;
CREATE TABLE amigos (
    personaje_id  INTEGER NOT NULL REFERENCES personajes (id) ON DELETE CASCADE,
    orden         INTEGER NOT NULL CHECK (orden >= 1),
    nombre        TEXT    NOT NULL,
    PRIMARY KEY (personaje_id, orden)
) STRICT, WITHOUT ROWID;
CREATE TABLE penas (
    personaje_id  INTEGER NOT NULL REFERENCES personajes (id) ON DELETE CASCADE,
    orden         INTEGER NOT NULL CHECK (orden >= 1),
    texto         TEXT    NOT NULL,
    PRIMARY KEY (personaje_id, orden)
) STRICT, WITHOUT ROWID;
CREATE TABLE familiares (                          -- sólo el que eligió familiar
    personaje_id    INTEGER PRIMARY KEY REFERENCES personajes (id) ON DELETE CASCADE,
    tipo            INTEGER NOT NULL CHECK (tipo BETWEEN 1 AND 255),
    nombre          TEXT    NOT NULL,
    nivel           INTEGER NOT NULL CHECK (nivel BETWEEN 0 AND 255),
    exp             INTEGER NOT NULL,
    exp_para_subir  INTEGER NOT NULL,
    vida            INTEGER NOT NULL,
    vida_max        INTEGER NOT NULL,
    golpe_min       INTEGER NOT NULL,
    golpe_max       INTEGER NOT NULL,
    muerto          INTEGER NOT NULL CHECK (muerto IN (0, 1)),
    habilidad_1     INTEGER NOT NULL CHECK (habilidad_1 BETWEEN -2147483648 AND 2147483647),
    habilidad_2     INTEGER NOT NULL CHECK (habilidad_2 BETWEEN -2147483648 AND 2147483647),
    habilidad_3     INTEGER NOT NULL CHECK (habilidad_3 BETWEEN -2147483648 AND 2147483647),
    habilidad_4     INTEGER NOT NULL CHECK (habilidad_4 BETWEEN -2147483648 AND 2147483647)
) STRICT;

-- LO QUE GUARDA EL MUNDO (clanes, correo, suelo, llaves vendidas, rechazos de clan, récord): el mismo JSON que los
-- archivos de Misc/, por clave. Pasa a tablas propias con la versión que haga lo mismo con el mundo.
CREATE TABLE mundo_json (
    clave  TEXT PRIMARY KEY,
    texto  TEXT NOT NULL
) STRICT, WITHOUT ROWID;
