using System.Text.Json;

namespace CoverAO.Server.Data;

/// <summary>Static definition of one item, loaded from the transcoded obj.json (Fase 0).
/// SubType is the potion routing (SubTipo: 1=agility, 2=strength, 3=HP, 4=mana, 5=cast spell);
/// Min/MaxModifier is the restore/boost range; DuracionEfectoEnVueltas es cuanto dura el efecto temporal EN VUELTAS del loop
/// de 40 ms —no en segundos—, que es como lo cuenta el original: `General.bas:1156` le resta una vuelta por pasada.</summary>
public sealed record ObjInfo(short Index, string Name, int ObjType, int MaxModifier, int Value, int Anim,
    int SubType = 0, int MinModifier = 0, int DuracionEfectoEnVueltas = 0,
    int IngotIron = 0, int IngotSilver = 0, int IngotGold = 0, int SkillBlacksmith = 0,
    int Wood = 0, int SkillCarpentry = 0,
    int SkinWolf = 0, int SkinBrownBear = 0, int SkinPolarBear = 0, int SkillTailoring = 0,
    int Roots = 0, int SkillAlchemy = 0, int MinDef = 0, int MaxDef = 0, int MinHit = 0, int MaxHit = 0,
    int MinHam = 0, int MinSed = 0, int Ropaje = 0,
    int MinLevel = 0, int Gender = 0, int TwoHanded = 0, string ForbiddenClasses = "", string ForbiddenRaces = "",
    // obj.dat "MinSkill": skill mínimo para usar el ítem. En los barcos (otBarcos) es la Navegación requerida
    // (Barca 40, Galera 55, Galeón 75) — VB6 PuedeUsarSkill (Trabajo.bas:2311).
    int MinSkill = 0,
    // --- PUERTAS (eOBJType.otPuertas = 6). VB6 Declares.bas:989-995 + FileIO.bas:981-990 ---
    // OJO, TRAMPA DE DATOS: el campo `Cerrada` se carga de la clave `abierta` del obj.dat, con el sentido
    // INVERTIDO — `ObjData(o).Cerrada = val(Leer.GetValue("OBJ" & o, "abierta"))` (FileIO.bas:981). O sea que
    // `abierta=1` significa CERRADA. Verificado en los datos: obj 8 "Puerta cerrada" trae `abierta=1` y obj 7
    // "Puerta abierta" trae `abierta=0`. Por eso el campo se llama Closed y NO Open: leerlo como "está abierta"
    // dejaría todas las puertas del mundo al revés (arrancarían abiertas y el doble-clic las cerraría).
    int Closed = 0,
    // "Llave": si es > 0 la puerta NO se abre nunca con doble-clic (Acciones.bas:454 -> LocaleMsg 172); sólo se
    // destraba usando un ítem otLlaves cuya `clave` coincida. "clave" es el par cerradura-llave.
    int Llave = 0, int Clave = 0,
    // El par abierta/cerrada al que se conmuta el tile. NUNCA hardcodear 7/8: cada puerta tiene su par.
    int IndexAbierta = 0, int IndexCerrada = 0, int IndexCerradaLlave = 0,
    // "Agarrable": OJO, invertido también — `Agarrable = 1` significa que NO se puede levantar (decorado fijo).
    // VB6 InvUsuario.bas:619: `If ObjData(...).Agarrable <> 1 Then` recién ahí entra a meterlo al inventario.
    // De los 176 objetos del mapa 1, 175 son Agarrable=1 (árboles, puertas, carteles): sin este gate, un `g`
    // sobre cualquiera se lleva un árbol al inventario.
    int Agarrable = 0,
    // "NoSeCae": el ítem NO se cae al suelo al morir en un mapa Pk (VB6 ItemSeCae). Sólo 5 de los 1520 objetos
    // lo traen; ver DropsOnDeath.
    int NoSeCae = 0,
    // Combate a distancia. "Proyectil" (obj.dat, singular): 1 = arco/ballesta (usa munición), 2 = arrojadiza
    // (arpón/daga — CÓDIGO MUERTO en esta data: ninguna tiene el SubTipo=5 que el disparo exige). "Municiones"
    // (plural): 1 = el arma consume una flecha por disparo y le suma el daño de la flecha. Ver IsBow/IsAmmo.
    int Proyectil = 0, int Municiones = 0,
    // "CuantoAumento" (obj.dat): en una poción SubTipo=13 (cofre de créditos de donador) = créditos que otorga al
    // usarla (VB6 InvUsuario.bas:1683). Los cofres 1604/1605/1606 dan 100/50/10. Ver IsCreditChest.
    int CuantoAumento = 0,
    // "ResistenciaMagica" (obj.dat): puntos que el ítem RESTA al daño de un hechizo ofensivo. La suma de casco +
    // escudo + armadura + montura + anillo se descuenta del daño en VB6 `HechizoTerrenoHP` (modHechizos.bas:530).
    // La traen 15 objetos (cascos y escudos altos: 10, 20, 8...).
    int ResistenciaMagica = 0,
    // "EfectoMagico" (obj.dat) = eMagicType (Declares.bas:857). Sólo se usa el 14 (DañoMagico): un báculo que le
    // SUMA `CuantoAumento` al daño de los hechizos del portador (misma función, :523). El casing en el .dat viene
    // en tres variantes, de ahí las tres claves en el loader.
    int EfectoMagico = 0,
    // "HechizoIndex" (obj.dat): en un pergamino (otPergaminos=24) = el hechizo que enseña al usarlo (VB6 AgregarHechizo).
    int HechizoIndex = 0,
    // "Lingoteindex" (obj.dat): en un mineral crudo (otMinerales=23) = el lingote que produce al fundirlo (VB6 DoLingotes).
    int LingoteIndex = 0,
    // "Newbie" (obj.dat): ítem de principiante. VB6 <c>ItemNewbie</c> (InvUsuario.bas:2310, `ObjData(i).Newbie = 1`),
    // que <c>SalePrice</c> (Comercio.bas:312) usa para pagar CERO por él.
    // OJO: en ESTA data la clave no existe — 0 de los 1520 objetos del obj.dat la traen, así que ItemNewbie
    // devuelve siempre False y el gate es inalcanzable. Se carga igual (cuesta nada y es fiel a la fuente),
    // pero no hay que contar con él: lo que hace que el kit de novato no valga nada es su Valor=0, no este flag.
    int Newbie = 0,
    // "Aura": el brillo que rodea al personaje cuando tiene el ítem puesto (obj.dat, 15 objetos — armas
    // legendarias, las armaduras doradas, los orbes, el Collar De Rykan). El VB6 lo guarda en el char
    // (.Char.Arma_Aura / Body_Aura / Escudo_Aura / Head_Aura / Anillo_Aura) y lo difunde con AuraToChar al
    // equipar, y con aura 0 al desequipar (InvUsuario.bas:958-1178).
    int Aura = 0,
    // "Apuñala" (obj.dat): el arma sirve para apuñalar por la espalda. La traen 13 objetos —la Daga, la
    // Espada Corta y las dagas +1/+2/+3— y es la puerta de entrada de la mecánica: sin un arma así, ni un
    // asesino puede apuñalar (VB6 PuedeApuñalar, Modulo_UsUaRiOs.bas:1175).
    int Apunala = 0,
    // --- Campos que el transcode traía en el .json y no leía nadie (barrido del "quinto eje") ---
    // Van AL FINAL a propósito: el loader pasa los parámetros POSICIONALMENTE, así que insertarlos en el
    // medio corre todo lo que viene detrás. (Probado en carne propia: 7 tests en rojo por meterlos después
    // de MinSkill.)
    //
    // `Crucial`: el mercader NO descuenta el stock de un ítem crucial (VB6 QuitarNpcInvItem,
    // Modulo_InventANDobj.bas:217 — TODO el descuento está dentro de un `If ObjData(...).Crucial = 0 Then`).
    // O sea, es stock INFINITO: lo tienen 286 objetos y los vende 61 de los 84 NPCs con inventario, así que
    // sin esto se le podía vaciar el stock al alquimista y al herrero, que en el original nunca se agotan.
    int Crucial = 0,
    // `Snd1`: el sonido que un INSTRUMENTO (otInstrumentos = 26) hace sonar en el área al usarlo
    // (VB6 InvUsuario.bas:1854). Tambor 48, Laúd 49, Flauta 51. OJO: en el obj.dat la clave va en
    // MAYÚSCULAS ("SND1"); buscarla como "Snd1" da cero y el campo parece muerto.
    int Snd1 = 0,
    // `Snd2`: en una MUNICIÓN es el FX que se dibuja sobre la criatura cuando la flecha da en el blanco
    // (VB6 SistemaCombate.bas:918, `CreateFX(npc, ObjData(Municion).Snd2, 0)`). La clave estaba en el .dat y
    // en el JSON —89 objetos la traen— y no se leía: el campo entero faltaba en la ficha.
    int Snd2 = 0,
    // `SndEspecial`: el sonido que suena EN EL ÁREA al equipar un ítem que tiene aura ("Sonido de auras" en
    // el original). El VB6 lo emite en los cuatro caminos de equipar —arma, armadura, escudo y casco— y
    // siempre ADENTRO del `If Obj.Aura <> 0`, o sea que un ítem con sonido pero sin aura no suena
    // (InvUsuario.bas:1014, 1077, 1181, 1270).
    //
    // Lo traen 10 objetos, y los 10 tienen aura: son las armas y armaduras de tope —Espada Mata Dragones,
    // Harbinger Kin, Daga Infernal, Nudillos de Oro, los Báculos de Hechicero, la Armadura de Placas Dorada,
    // el Escudo de Reflexión, la Espada Saramiana—. Ponerse una de esas y que no suene nada es una diferencia
    // que se nota: en el original te la escucha toda el área.
    int SndEspecial = 0,
    // `QueSkill` (obj.dat): la skill que el ítem SUBE mientras está puesto. El original suma `CuantoAumento`
    // al equipar y lo resta al desequipar, y las dos ramas están adentro de un `Case eMagicType.ModificaSkill`
    // (InvUsuario.bas:973 y :747), o sea que el bonus sólo aplica si `EfectoMagico` vale 3.
    //
    // Lo traen 11 objetos y 10 tienen el EfectoMagico correcto: los anillos y pendientes que llevan el número
    // EN EL NOMBRE —Anillo del Guerrero (+20), Pendiente del Arquero (+30), Anillo de Comercio (+30)—. El que
    // sobra, el Manual de Liderazgo, no tiene EfectoMagico: en el original tampoco da nada, y acá igual.
    int QueSkill = 0,
    // `LanzaHechizo` (obj.dat): la poción de SubTipo 5 le lanza este hechizo A QUIEN LA TOMA. El original la
    // rutea por `HechizoEstadoUsuario` con TargetUser = uno mismo (InvUsuario.bas:1636), y si ese Sub no
    // aplicó nada devuelve `Casteo = False` y la poción NO SE CONSUME.
    //
    // Eso último decide el alcance: ese Sub lee Sanacion, Envenena, Incinera, CuraVeneno, Paraliza,
    // Inmoviliza, RemoverParalisis, Ceguera, Invisibilidad, SubeHP y los buffs de atributo — pero NO
    // metamorfosis ni desencantar. Así que de las 8 pociones que traen el campo, la de Metamorfosis: Dragón y
    // las dos de Desencantar (Anti-Ceguera y Anti-Estupidez) no hacen nada NI EN EL ORIGINAL.
    int LanzaHechizo = 0,
    // `QueAtributo` (obj.dat): el ATRIBUTO que el ítem sube mientras está puesto — el hermano exacto de
    // `QueSkill`, en la otra rama del mismo `Select Case Obj.EfectoMagico` del original (InvUsuario.bas:740 al
    // equipar, :967 al sacar). Lo traen 4 ítems, y los cuatro lo anuncian en el nombre: "Nudillos (+10)",
    // "Anillo de Agilidad (+5)", "Botas de Celeridad (+10)", "Anillo del Carisma (+10)". Sin esto el jugador
    // se pone el anillo, lee el +5 en el nombre y no le pasa nada.
    int QueAtributo = 0,
    // `MineralIndex` (obj.dat): el mineral crudo que da ESTE yacimiento al picarlo. El original lo lee del
    // yacimiento que tenés seleccionado (`ObjData(.flags.TargetObj).MineralIndex`, Trabajo.bas:2001) y acá el
    // mineral estaba CABLEADO a hierro, así que los 4 yacimientos de Oro y los 4 de Plata —8 de los 12 del
    // install— daban hierro, y la rama de plata y oro de la fundición no se alcanzaba minando.
    int MineralIndex = 0,
    // EL PASAJE del barco (`otPasajes` = 36): a dónde te lleva y desde dónde vale. OJO LAS CLAVES, que no se
    // llaman como el campo: el cargador del original lee `Desde`, `Map`, `X` e `Y` (FileIO.bas:940-943) y las
    // guarda en DesdeMap/HastaMap/HastaX/HastaY. Buscar "DesdeMap" en el obj.dat no encuentra nada.
    //
    // Los traen los 37 pasajes del install y los 37 tienen destino; `Desde` sólo lo traen algunos —el original
    // compara `Pos.Map = DesdeMap` y con 0 eso no matchea nunca, así que un pasaje sin `Desde` NO se puede usar
    // en ningún lado. Es del original: son pasajes muertos en la data, no un error del port.
    int PasajeDesdeMap = 0, int PasajeHastaMap = 0, int PasajeHastaX = 0, int PasajeHastaY = 0,
    // `Destruir` (obj.dat): el objeto NO se puede tirar al piso. El original corta el DropObj y en su lugar
    // manda un ShowMessageBox (Protocol.bas:2057). Lo traen 5: los sacos de créditos y de tiempo de donador y
    // el Anillo Dorado. Sin esto se los podía tirar —y perder— en el piso.
    bool NoSeTira = false,
    // `StaRequerido` (obj.dat): la ENERGIA que cuesta un golpe con esta arma (VB6 UsuarioAtaca,
    // SistemaCombate.bas:995-999). Lo traen 83 objetos —de 10 la daga a 150 el arma mas pesada— y no se leia:
    // el campo llegaba en el JSON del transcode y ObjInfo ni lo tenia, asi que pelear no cansaba y el combate
    // no tenia techo. El default de 10 es del cargador del original (FileIO.bas:999): al objeto que no trae la
    // clave le pone 10, no 0.
    int StaRequerido = 10)
{
    /// <summary>
    /// ÍTEM DE NOVATO. El original lo decide por la clave <c>Newbie</c> del <c>obj.dat</c> (FileIO.bas:819) y con
    /// eso gatea tres cosas: el mercader paga 0 (<c>SalePrice</c>, Comercio.bas:312), no se puede tirar ni
    /// comerciar (<c>LocaleMsg 260</c>, InvUsuario.bas:440) y no se puede robar (:52).
    ///
    /// EN ESTE INSTALL LA CLAVE NO EXISTE: se grepeó el <c>obj.dat</c> crudo —no nuestro JSON— y no hay un solo
    /// <c>Newbie=</c>, en ninguna grafía. O sea que las tres reglas del original están vivas en el código y
    /// MUERTAS por falta de dato, y por eso un novato podía vender su equipo inicial y perderlo al morir.
    ///
    /// Lo que la data SÍ trae es la convención del NOMBRE: 25 objetos terminan en <c>(Newbies)</c> —las
    /// vestimentas, la daga, el arco, el báculo, el martillo, las dos pociones, la manzana, el agua, la flecha,
    /// los nudillos y el mapa—, que son exactamente el equipo de arranque. Se deriva de ahí, y así las reglas
    /// del original vuelven a tener a quién aplicarse. La clave se sigue respetando si algún día aparece.
    /// </summary>
    public bool IsNewbie => Newbie == 1 || Name.EndsWith("(Newbies)", StringComparison.OrdinalIgnoreCase);

    /// <summary>Pergamino de hechizo (VB6 otPergaminos=24, Declares.bas:603): al usarlo, un caster aprende HechizoIndex.</summary>
    public bool IsPergamino => ObjType == 24;
    /// <summary>Mineral crudo (VB6 otMinerales=23): se funde en LingoteIndex lingotes (Trabajo.bas DoLingotes).</summary>
    public bool IsMineral => ObjType == 23 && LingoteIndex > 0;
    /// <summary>Cofre de créditos de donador: poción (otPociones=11) con SubTipo 13 que otorga CuantoAumento créditos.</summary>
    public bool IsCreditChest => IsPotion && SubType == 13 && CuantoAumento > 0;
    /// <summary>¿Es una puerta? (VB6 eOBJType.otPuertas = 6, Declares.bas:586).</summary>
    public bool IsDoor => ObjType == 6;
    /// <summary>VB6 <c>ItemNoEsDeMapa</c> (GameLogic.bas:1164): un objeto que NO es parte del decorado del mapa,
    /// o sea el que /MASSDEST puede borrar. Excluye puertas(6), carteles(8), árboles(4), yacimientos(22),
    /// teleports(19), correo(48), muebles(20), fraguas(28) y yunques(27).</summary>
    public bool IsNotMapFurniture => ObjType is not (6 or 8 or 4 or 22 or 19 or 48 or 20 or 28 or 27);
    /// <summary>¿Se puede levantar del suelo? VB6 InvUsuario.bas:619: `Agarrable <> 1`. El sentido del dato
    /// está invertido — 1 = fijo/decorado.</summary>
    public bool IsPickable => Agarrable != 1;
    /// <summary>¿La clase (eClass) tiene prohibido este ítem? (obj.dat ClasesProhibidas, lista separada por comas).</summary>
    public bool ClassForbidden(byte cls) => InCsv(ForbiddenClasses, cls);
    /// <summary>¿La raza (eRaza) tiene prohibido este ítem? (obj.dat RazasProhibidas).
    ///
    /// OJO: los números de <c>RazasProhibidas</c> NO son los del <c>eRaza</c> de este server. El obj.dat
    /// viene de Imperium, que ordena las razas distinto, y el VB6 los TRADUCE al cargar con
    /// <c>QueRazaEs</c> (FileIO.bas:1028 y 2447). Acá se comparaba el número crudo, y el resultado era que
    /// las restricciones quedaban rotadas: al Elfo (eRaza 2) le caía la ropa de razas ALTAS —que lleva
    /// <c>2,5</c>, o sea "prohibida a enano y gnomo"— y le quedaba permitida la de BAJAS. Un elfo sólo se
    /// podía vestir de enano.</summary>
    public bool RaceForbidden(byte race)
    {
        if (string.IsNullOrEmpty(ForbiddenRaces)) return false;
        foreach (var part in ForbiddenRaces.Split(','))
            if (int.TryParse(part.Trim(), out var n) && RazaDelDat(n) == race) return true;
        return false;
    }

    /// <summary>La traducción de <c>QueRazaEs</c> (FileIO.bas:2447): número del obj.dat → <c>eRaza</c> del
    /// server. El .dat ordena Humano, Enano, Elfo, Drow, Gnomo, Orco; el server, Humano, Elfo, Drow, gnomo,
    /// enano, Orco. (Los comentarios de esa función en el VB6 están mal escritos y nombran otra raza que la
    /// que devuelven; el que manda es el mapeo, y cierra con la data: los 145 ítems con <c>2,5</c> son la
    /// ropa de altos y los 95 con <c>1,3,4,6</c> la de bajos.)</summary>
    private static byte RazaDelDat(int n) => n switch
    {
        1 => 1,   // Humano → Humano
        2 => 5,   // Enano  → enano
        3 => 2,   // Elfo   → Elfo
        4 => 3,   // Drow   → Drow
        5 => 4,   // Gnomo  → gnomo
        6 => 6,   // Orco   → Orco
        _ => 0,
    };
    private static bool InCsv(string csv, int v)
    {
        if (string.IsNullOrEmpty(csv)) return false;
        foreach (var part in csv.Split(','))
            if (int.TryParse(part.Trim(), out var n) && n == v) return true;
        return false;
    }

    /// <summary>Monedas de oro sueltas (<c>otGuita</c> = 5, Declares.bas:585). No es un objeto de inventario
    /// como los demás: el original las manda derecho a la billetera cuando se levantan del piso
    /// (<c>' Oro directo a la billetera!</c>, InvUsuario.bas:632) y también si igual llegan al inventario y se
    /// usan (:1455).</summary>
    public bool IsGold => ObjType == 5;

    public bool IsPotion => ObjType == 11;
    /// <summary>Instrumento musical (VB6 <c>otInstrumentos</c> = 26): al usarlo suena su <see cref="Snd1"/> en
    /// el área. Son tres en el install — Tambor (48), Laúd (49) y Flauta (51).</summary>
    public bool IsInstrument => ObjType == 26;
    public bool IsFood => ObjType == 1;      // otUseOnce — comida (resets hunger via MinHam)
    public bool IsDrink => ObjType == 13;    // otbebidas — bebida (resets thirst via MinSed)
    public bool IsWeapon => ObjType == 2;
    public bool IsMount => ObjType == 44;
    /// <summary>Ocupa el slot de ANILLO (<c>otAnillo = 18</c>, Declares.bas). Ojo el nombre, que engaña: en esta
    /// variante ese slot no lo usan sólo los anillos sino <b>todas las herramientas de oficio</b> — Costurero
    /// (886), Olla (887), Martillo de herrero (389), Serrucho (198), Piquete (187), Hacha (127), Red (138) y Caña
    /// (881) son las ocho de la data, y las ocho son <c>otAnillo</c>. De ahí que el original chequee cosas como
    /// <c>Invent.AnilloEqpObjIndex = COSTURERO</c>: el "anillo equipado" ES la herramienta en la mano.</summary>
    public bool IsRing => ObjType == 18;
    public bool IsTree => ObjType == 4;          // otArboles — se tala (hacha) y se juntan raíces (tijeras)
    public bool IsDeposit => ObjType == 22;      // otYacimiento — se mina (piquete)
    public bool IsAnvil => ObjType == 27;        // otYunque — abre la herrería si se lleva el martillo puesto
    public bool IsForge => ObjType == 28;        // otFragua — funde mineral crudo en lingotes
    public bool IsKey => ObjType == 9;       // otLlaves (Declares.bas:589)
    public bool IsBow => IsWeapon && Proyectil == 1;    // arco/ballesta: dispara a distancia y consume munición
    public bool IsAmmo => ObjType == 32;                // otFlechas (Declares.bas:610) — munición del arco

    /// <summary>Nudillos (VB6 <c>otNudillos = 46</c>). Son un arma con slot PROPIO —<c>NudiEqpObjIndex</c>—,
    /// excluyente con el arma común: ponerse uno se saca el otro (InvUsuario.bas:995 y :1056). En combate el
    /// original los mira ANTES que el arma y pelea por <c>Wrestling</c> con el daño de los nudillos
    /// (SistemaCombate.bas:190 y :403). Son cuatro en la data —Hierro, Plata, Oro y el de novato— y ninguno
    /// trae la clave <c>Anim</c>, así que se pelea con las manos vacías a la vista.</summary>
    public bool IsKnuckles => ObjType == 46;

    /// <summary>Ítem mágico (VB6 <c>otItemsMagicos = 21</c>): anillos, pendientes, orbes y amuletos. Tienen su
    /// propio slot (<c>MagicIndex</c>/<c>MagicSlot</c>), no se ven puestos —no hay <c>CharacterChangeSlot</c>,
    /// sólo el aura tipo 6— y su efecto sale de <c>EfectoMagico</c>. Son 32 en la data.</summary>
    public bool IsMagicItem => ObjType == 21;

    /// <summary>
    /// ¿El ítem se cae al morir en mapa Pk? (VB6 ItemSeCae, InvUsuario.bas:2175). El original lo escribe así:
    /// <code>(.Real &lt;&gt; 1 Or .NoSeCae = 0) And (.Caos &lt;&gt; 1 Or .NoSeCae = 0) And .OBJType &lt;&gt; otLlaves
    /// And .OBJType &lt;&gt; otBarcos And .OBJType &lt;&gt; otMonturas And .NoSeCae = 0</code>
    /// Las dos primeras cláusulas son redundantes: el <c>.NoSeCae = 0</c> del final ya las implica. Queda
    /// entonces: se cae si no está marcado NoSeCae y no es llave, barco ni montura.
    /// </summary>
    public bool DropsOnDeath => NoSeCae == 0 && !IsKey && !IsBoat && !IsMount;
    public bool IsBoat => ObjType == 31;     // otBarcos — Barca/Galera/Galeón (usar para navegar)
    // Todo el equipo defensivo es ObjType 3 (otArmadura); el SubTipo lo distingue
    // (InvUsuario.bas:772-821): 0 = armadura (cuerpo), 1 = casco (cabeza), 2 = escudo.
    public bool IsArmor => ObjType == 3 && SubType == 0;
    public bool IsHelmet => ObjType == 3 && SubType == 1;
    public bool IsShield => ObjType == 3 && SubType == 2;
}

/// <summary>Static definition of one spell, from Hechizos.dat. SubeHP: 1=heal, 2=damage. The state
/// flags (Paraliza/RemoverParalisis/Envenena/CuraVeneno) route status effects onto the target.</summary>
public sealed record SpellInfo(short Index, string Name, int SubeHp, int MinHp, int MaxHp, int ManaCost, int Target,
    int Paraliza = 0, int RemoverParalisis = 0, int Envenena = 0, int CuraVeneno = 0,
    // "Sanacion" (Hechizos.dat): limpia de una la INCINERACIÓN y el VENENO (VB6 modHechizos.bas:1048).
    // Lo tiene un solo hechizo, el 32 ("Sanar"), pero es el ÚNICO antídoto de la incineración que hay: el
    // port ya sabía incinerar con dos hechizos (56 "Incinerar" y 69 "Descarga Flamígera") y no tenía con qué
    // apagarlo. "Sanar" hoy removía la parálisis y nada más, porque ese campo sí se leía y éste no.
    int Sanacion = 0,
    int Invoca = 0, int InvokeNpc = 0, int InvokeCount = 0, int FxGrh = 0, int FxLoops = 0, int Ceguera = 0,
    int Particle = 0, int Revivir = 0,
    // Requisitos de lanzamiento (VB6 PuedeLanzar, modHechizos.bas:319): skill de magia mínima y stamina.
    int MinSkill = 0, int StaRequerido = 0,
    // "Anillo": el hechizo exige un anillo puntual en el inventario (VB6 PuedeLanzar, modHechizos.bas:344).
    // 1 = Anillo Espectral (1329), 2 = Anillo de las Penumbras (1330). Lo traen 7 hechizos, todos de los caros
    // (Apocalipsis, Implosion, Juicio final, Lamento de la Banshee...).
    int Anillo = 0,
    // Buffs/debuffs de atributo (VB6 HechizoPropUsuario, modHechizos.bas): SubeFuerza/SubeAgilidad = 1 aumenta,
    // 2 reduce el atributo en RandomNumber(Min,Max). Reusa el camino de las pociones de atributo (ApplyAttrBonus),
    // ya verificado. Los valores (Min/Max) salen del data, así que la aritmética del bono es byte-fiel.
    int SubeFuerza = 0, int MinFuerza = 0, int MaxFuerza = 0,
    int SubeAgilidad = 0, int MinAgilidad = 0, int MaxAgilidad = 0,
    // Estados adicionales (VB6 Case uEstado): Invisibilidad (oculta al lanzador), Inmoviliza (frena el movimiento,
    // como Paraliza) e Incinera (quema: reusa el estado de veneno con daño periódico).
    int Invisibilidad = 0, int Inmoviliza = 0, int Incinera = 0,
    // HECHIZOS DE ÁREA (VB6 `HechizoTerrenoEstado`, modHechizos.bas:382). `HechizoDeArea = 1` junto con
    // `AreaEfecto <> 0` hacen que el hechizo se resuelva sobre el 3×3 que rodea al tile casteado en vez de
    // sobre un solo objetivo. OJO con `AreaEfecto`: la data lo describe como un radio ("cura en un diámetro de
    // 5 tiles") pero el original NO lo usa para dimensionar el bucle — recorre `-1..+1` fijo y `AreaEfecto`
    // sólo funciona como segundo interruptor de encendido. Se porta el bucle fijo, que es lo que corre.
    int HechizoDeArea = 0, int AreaEfecto = 0,
    // `Tipo` (TipoHechizo, Declares.bas:292): 1 uPropiedades, 2 uEstado, 4 uInvocacion, 5 uCreateTelep,
    // 6 uFamiliar, 7 uMaterializa, 12 uDetectarInvis. Importa porque `HandleHechizoTerreno` (modHechizos.bas:706)
    // rutea por él: SÓLO uPropiedades/uEstado entran a `HechizoTerrenoEstado`, que es la única función del
    // original que emite los paquetes de FX sobre el TERRENO.
    int Tipo = 0,
    // Sonido y duración de la partícula, que hasta ahora se tiraban. Los usa la emisión al terreno
    // (`PlayWave` con las coords del tile y `EfectoTerrenoParticula` con su Time).
    int Wav = 0, int TimeParticula = 0,
    // `autolanzar` (Hechizos.dat, EN MINÚSCULA en el archivo): el hechizo se lanza sobre UNO MISMO y sin pedir
    // objetivo. El original no pregunta nada — `If Hechizos(uh).AutoLanzar > 0 Then .flags.TargetUser =
    // UserIndex : LanzarHechizo(...)` (HandleCastSpell, Protocol.bas:2136) — y recién si vale 0 le pide al
    // cliente que marque un blanco. Lo traen 19 y son todos auto-buffs: Invisibilidad, las doce metamorfosis,
    // Aura sagrada, Golpe certero, Inspiración, Honor del Paladín y el Cobijo de Leomundo.
    int AutoLanzar = 0,
    // `ResucitaFamiliar` (Hechizos.dat): lo trae UN solo hechizo, el #79 "Resucitar familiar o mascota". El
    // sistema de familiares no existe en esta variante —el propio original comenta la rama con un "Solo damos
    // la animacion xd"— pero la rama SE EJECUTA y da el hechizo por lanzado, que es lo que cobra el maná y
    // saca las palabras mágicas. Sin leer esta clave el hechizo caía en el "aún no está implementado" y salía
    // gratis. Ver modHechizos.bas:1066.
    int ResucitaFamiliar = 0,
    // ── METAMORFOSIS ──────────────────────────────────────────────────────────────────────────────────
    // Los seis campos que traen los once hechizos de metamorfosis (#38-50) y NINGÚN otro. El VB6 lee dos
    // —`metamorfosis` en FileIO.bas:367 y `extrahit` en :366— y no usa ninguno: la única otra mención de
    // `.Metamorfosis` en todo el servidor está COMENTADA (Modulo_UsUaRiOs.bas:1255), y no hay un solo
    // `Char.body = ` que salga de un hechizo. O sea: los pergaminos se venden hasta 2.200.000 de oro, el
    // druida aprende el hechizo, lo lanza, y no pasa absolutamente nada. Es así en el original.
    //
    // La data en cambio está completa y es una progresión pensada: del Murciélago (skill 5, 50 de maná,
    // +6 al golpe y +20 a la defensa) al Dragón (skill 100, 1300 de maná, +50 y +20). Los once llevan
    // `head=0` — la criatura no tiene cabeza humana — y un `asusta` de 1 a 10.
    //
    // Se leen para la REGLA DE LA CASA `MetamorfosisDelDruida`. Apagada, sobran; encendida, son el hechizo.
    //
    // `asusta` (1 el murciélago, 10 el dragón) es CUÁN TEMIBLE se ve la forma, y con eso la ficha cumple lo
    // que promete: "las criaturas no te atacarán si aparentas ser más poderoso que ellas". Contra qué se
    // compara está en `NPCs.dat`: la clave **Nivel**, que traen 121 de los 124 hostiles y va de 1 a 100. Las
    // dos escalas encajan por los extremos —`asusta` 10 es el Dragón y el Nivel más alto del mundo es el 100
    // del Dragón Legendario—, así que el puente es ×10 y no un número elegido a dedo. Ver
    // <see cref="EscalaDelAsusta"/>.
    int Metamorfosis = 0, int BodyMetamorfosis = 0, int HeadMetamorfosis = 0,
    int ExtraHit = 0, int ExtraDef = 0, int Asusta = 0)
{
    /// <summary>Hechizo de metamorfosis con un cuerpo al que transformarse (Hechizos.dat
    /// <c>metamorfosis=1</c> + <c>body</c>). Sólo lo cumplen los once del bloque #38-50.</summary>
    public bool EsMetamorfosis => Metamorfosis > 0 && BodyMetamorfosis > 0;

    /// <summary>
    /// El puente entre el <c>asusta</c> de un hechizo (1..10) y el <c>Nivel</c> de una criatura (1..100).
    ///
    /// <para>No es un número a dedo: las dos escalas encajan por los extremos. El <c>asusta</c> más alto es 10
    /// y lo tienen el Dragón y el Árbol; el <c>Nivel</c> más alto del mundo es 100 y lo tienen los doce
    /// bichos de tope, el Dragón Legendario entre ellos. Diez por diez, cien.</para>
    ///
    /// <para>La comparación es ESTRICTA (<c>asusta * 10 &gt; Nivel</c>), que es lo que dice la ficha:
    /// "si aparentas ser más poderoso que ellas". Con eso, la forma de Dragón asusta a todo lo que esté por
    /// debajo del techo y los doce de Nivel 100 siguen atacando a cualquiera.</para>
    /// </summary>
    public const int EscalaDelAsusta = 10;

    /// <summary>El hechizo se tira sobre uno mismo, sin objetivo (Hechizos.dat <c>autolanzar</c>).</summary>
    public bool EsAutoLanzado => AutoLanzar > 0;

    /// <summary>VB6 <c>ResucitaFamiliar</c>: el hechizo se da por lanzado —con todo lo que eso emite y cuesta—
    /// pero no tiene efecto, porque el familiar al que resucitaría no existe en esta variante.</summary>
    public bool EsResucitaFamiliar => ResucitaFamiliar > 0;

    public bool IsHeal => SubeHp == 1;
    /// <summary>Hechizo de ÁREA: se resuelve sobre el 3×3 del tile casteado (VB6 modHechizos.bas:403, los DOS
    /// gates anidados <c>HechizoDeArea</c> y <c>AreaEfecto &lt;&gt; 0</c>).</summary>
    public bool IsArea => HechizoDeArea == 1 && AreaEfecto != 0;
    /// <summary>Entra a <c>HechizoTerrenoEstado</c>: hechizo al TERRENO (Target 4) de tipo uPropiedades(1) o
    /// uEstado(2) — el ruteo de <c>HandleHechizoTerreno</c> (modHechizos.bas:706). Es la condición exacta para
    /// que el FX salga sobre el tile (EfectoTerrenoFX/EfectoTerrenoParticula) en vez de colgado del personaje.</summary>
    public bool IsTerrainState => Target == 4 && (Tipo == 1 || Tipo == 2);
    public bool IsDamage => SubeHp == 2;
    public bool IsRevive => Revivir > 0;   // hechizo Resucitar (Hechizos.dat Revivir=1)
    public bool IsParalyze => Paraliza > 0;
    public bool IsRemoveParalysis => RemoverParalisis > 0;
    public bool IsPoison => Envenena > 0;
    public bool IsCurePoison => CuraVeneno > 0;
    /// <summary>VB6 <c>Sanacion</c>: apaga incineración Y veneno de un saque.</summary>
    public bool IsSanacion => Sanacion > 0;
    public bool IsInvoke => Invoca > 0 && InvokeNpc > 0 && InvokeCount > 0;
    /// <summary>Sube o baja Fuerza/Agilidad temporalmente (hechizos de buff como Fuerza/Debilidad/Celeridad).</summary>
    public bool IsAttrBuff => SubeFuerza > 0 || SubeAgilidad > 0;
    public bool IsInvisibility => Invisibilidad > 0;
    public bool IsImmobilize => Inmoviliza > 0;
    public bool IsIncinerate => Incinera > 0;
}

/// <summary>Un ítem del botín de un NPC (VB6 <c>npc.Invent.Object(i)</c>, cargado del campo RandomDrop).
/// <paramref name="ProbTirar"/> es el tercer campo del .dat: el VB6 sólo trata especial el valor 100
/// (dropea SIEMPRE); con cualquier otro valor tira una moneda al 50% (<c>NPC_TIRAR_ITEMS</c>).</summary>
public sealed record NpcDrop(int ObjIndex, int Amount, double ProbTirar);

/// <summary>
/// Una entrada de la TABLA DE DROP GLOBAL (<c>DropGlobal.dat</c>): un ítem que cualquier criatura puede
/// soltar, además de su propio <c>RandomDrop</c>.
///
/// <para><b>En el VB6 esta tabla no la lee nadie.</b> El archivo existe y viaja con el install, y el menú de
/// recarga del panel lo lista junto a los demás .dat — pero su rama entera es <c>MsgBox "Drop Global"</c>
/// (frmServidor.frm:520). Ningún módulo lo abre. Quedó a mitad de camino.</para>
///
/// <para><see cref="Peso"/> es el <c>PorcentajeDrop</c> crudo del archivo. NO es una probabilidad que se
/// pueda usar tal cual: las 26 entradas activas suman 482 %, así que tirarlas por separado soltaría cinco
/// anillos legendarios por rata. Se usa como PESO RELATIVO —las dos entradas en 1 son veinte veces más raras
/// que las de 20— y quién sortea la tabla lo decide la regla de la casa.</para>
///
/// <para>El autor del original dejó escrito qué quería, en un comentario pegado al valor de la entrada 29:
/// <c>Porcentaje=-1 'le pongo -1, así no cualquiera puede dropear, sólo los que tienen TiraItem=x% +1</c>.
/// O sea: el porcentaje se SUMABA al del NPC. Ese campo <c>TiraItem</c> <b>no existe</b> en NPCs.dat ni en
/// una sola línea del servidor, así que el diseño no se puede completar como estaba pensado. Lo que SÍ se
/// honra es su convención para apagar entradas: las cuatro que dejó en <c>0</c> y <c>-1</c> (las dos de
/// Resistencia Mágica, el Reflex +30 y la Gema Lunar) no salen nunca.</para>
/// </summary>
public sealed record DropGlobalItem(int ObjIndex, int Peso);

/// <summary>Un ítem del kit inicial de una clase (NACIMIENTO.dat, VB6 <c>OBJNacimiento.Clase(i)</c>).
/// El slot 3 es el arma y el 4 la armadura racial (ver <c>RellenarInventario</c>, TCP.bas:2195).</summary>
public sealed record NewbieItem(int ObjIndex, int Amount, bool Equipped);

/// <summary>Static definition of one NPC template, from NPCs.dat. <see cref="Drops"/> viene del campo
/// RandomDrop: entradas "objIndex,amount,probTirar" separadas por '-' (el VB6 arma con eso el inventario
/// del NPC, MODULO_NPCs.bas:1027). Al morir se evalúa cada entrada por separado.</summary>
public sealed record NpcInfo(short Index, string Name, short Body, short Head, byte Heading,
    int MaxHp, int MinHit, int MaxHit, int Def, long GiveExp, int GiveGld, long PoderEvasion, long PoderAtaque,
    IReadOnlyList<NpcDrop>? Drops = null, string Description = "",
    bool Hostile = false, bool Attackable = false, int Movement = 0,
    // NPCs.dat "PosOrig": 1 = al reaparecer vuelve a su baldosa (la carga del mapa le guarda `Orig`, VB6 FileIO.bas:1348);
    // 0 = reaparece en una posición legal al azar del mapa, lejos de los jugadores (CrearNPC, MODULO_NPCs.bas:436). Lo
    // traen 157 de las 291 fichas del install, entre ellas el sacerdote y el mercader de la cuna y once jefes (los dragones
    // grandes); ninguna de las criaturas de la cuna.
    bool PosOrig = false,
    // NPCs.dat "Domable": puntos requeridos para domesticarla (0 = no domable). VB6 DoDomar exige
    // Domable <= Carisma * SkillDomar (+ una tirada). Ver HandleTrain.
    int Domable = 0,
    // NPCs.dat "NpcType" (VB6 eNPCType): 1=Revividor, 3=Entrenador, 4=Banquero, 5=facciones, 12=Timbero…
    // Le da su ROL a cada NPC poblado desde MapSpawns (antes eran todos inertes).
    int NpcType = 0,
    // NPCs.dat "Obj1..ObjN" ("objIndex-cantidad") de un NPC con Comercia=1: su stock de mercader.
    IReadOnlyList<(int ObjIndex, int Amount)>? StockItems = null,
    // NPCs.dat "TipoItems": el ObjType que este mercader ACEPTA cuando le vendés (VB6 Comercio.bas:123).
    // Lo traen 82 NPCs del install: el herrero compra armas, el sastre ropa, el de comidas comida. El valor
    // especial `otCualquiera` (=1000, Declares.bas:498) lo traen 5 y significa "compro de todo".
    // Sin esto un mercader compraba CUALQUIER cosa: se le vendía una armadura al vendedor de manzanas.
    int TipoItems = 0,
    // NPCs.dat "Inmunidad": la criatura NO se puede paralizar ni inmovilizar. El VB6 lo carga en
    // `flags.AfectaParalisis` (MODULO_NPCs.bas:1093) y OJO EL NOMBRE, que dice lo contrario de lo que hace:
    // el hechizo se aplica solo `If .flags.AfectaParalisis = 0`, o sea que 1 = INMUNE. Misma trampa que
    // `Agarrable`. Lo traen 66 NPCs -- los bosses del install-- y el port ni siquiera tenia el campo: se
    // podia congelar a cualquiera de ellos y matarlo sin que se moviera.
    int Inmunidad = 0,
    // NPCs.dat "Veneno": la criatura ENVENENA al que muerde. El VB6 lo tira al final de cada golpe que
    // acierta (SistemaCombate.bas:752 -> NpcEnvenenarUser), con un 30% de chance. Son 7 criaturas del
    // install; sin este campo el veneno de criatura no existía y sólo se podía envenenar con un hechizo.
    int Veneno = 0,
    // NPCs.dat flags de terreno (VB6 MoveNPCChar): AguaValida=1 → la criatura puede andar por agua (peces);
    // TierraInvalida=1 → acuática pura, no pisa tierra. Default terrestre (AguaValida=0, TierraInvalida=0).
    bool AguaValida = false, bool TierraInvalida = false,
    // VB6 `Npclist().Leveles`, que sale de la clave **"Nivel"** (MODULO_NPCs.bas:972) — no de "Leveles" — y con
    // una regla propia: `If Not .Leveles > 0 Then .Leveles = 100`, así que la criatura SIN la clave no queda en
    // 0 sino en 100. Lo manda CharMsgStatusNPC al clickearla.
    int Nivel = 100,
    // VB6 `Npclist().flags.Status` (MODULO_NPCs.bas:979, clave "Status"): el alineamiento que decide el color
    // del nombre. Sólo 48 de los 291 NPCs lo traen; el resto es 0.
    int Status = 0,
    // NPCs.dat "LanzaSpells" + "Sp1".."SpN": los hechizos que la criatura sabe tirar. Lo tienen **70 de los
    // 291 NPCs** del install —los Magos con Implosión y Juicio Final, los Nigromantes con Apocalipsis, los
    // Druidas, las Banshees— y el VB6 lo usa en tres puntos de su IA (AI_NPC.bas:169/230/295 →
    // `NpcLanzaUnSpell`, que elige uno al azar de la lista con `RandomNumber(1, LanzaSpells)`).
    //
    // Sin esto esas 70 criaturas peleaban SÓLO a melee, que es una pelea completamente distinta.
    IReadOnlyList<int>? Spells = null,
    // EL EQUIPO QUE SE LE VE PUESTO. El original lo manda en su CharacterCreate —`MakeNPCChar`,
    // MODULO_NPCs.bas:590, pasa `Char.WeaponAnim`, `Char.ShieldAnim` y `Char.CascoAnim`— y acá iban tres ceros
    // cableados, así que 57 de los 291 NPCs salían desarmados: los Guerreros de las ciudades sin espada ni
    // escudo, los Hechiceros y Nigromantes sin casco. Es de las cosas que se ven a los dos segundos de entrar.
    int WeaponAnim = 0, int ShieldAnim = 0, int HelmetAnim = 0,
    // NPCs.dat "NroCriaturas" + "CN1".."CNn": las criaturas que un ENTRENADOR (NpcType=3) te spawnea para
    // practicar. Lo trae UN solo NPC del install -- el "Entrenador", con Murcielago, Lobo, Serpiente,
    // Escorpion y Goblin-- y el campo estaba en el JSON pero NO se cargaba: `Npc.TrainerCreatures` sólo lo
    // llenaba el fixture a mano, así que en el mundo real el entrenador no listaba nada.
    IReadOnlyList<string>? TrainerCreatures = null,
    // Los `CI1..CIn`: el INDICE de plantilla de cada criatura. Los `CN` son para mostrar, estos para spawnear.
    IReadOnlyList<int>? TrainerCreatureIndexes = null,
    // LOS TRES SONIDOS PROPIOS DE LA CRIATURA (NPCs.dat Snd1/Snd2/Snd3, MODULO_NPCs.bas:1095-1097). Los traen
    // 66 de los 291 NPCs y el port no tenía el campo, así que pelear contra criaturas era MUDO: el rugido al
    // atacar, el quejido al recibir el golpe y el grito al morir no sonaban nunca.
    //
    // No lo veía ningún control, y por dos motivos distintos que conviene recordar. El barrido de assets
    // deriva los ids de sonido de los hechizos y de los objetos y **nunca miraba `NPCs.dat`**. Y el oráculo no
    // podía verlo porque su acción `Attack` pega AL AIRE — no hay criatura enfrente del personaje del barrido,
    // así que estas líneas no se ejecutan ni una vez en las 115 acciones.
    //
    //   Snd1 = la criatura ATACA        (SistemaCombate.bas:730 contra un usuario, :843 contra otra criatura)
    //   Snd2 = LA GOLPEAN               (:849 y :909 a melee, modHechizos.bas:438 con un hechizo)
    //   Snd3 = LA MATA UN JUGADOR       (MODULO_NPCs.bas:101, dentro de MuereNpc y sólo si `UserIndex > 0`)
    //
    // La clave del .dat viene en las dos grafías —`SND1` en 50 fichas y `Snd1` en 3—, así que se leen las dos:
    // buscar sólo una da cero en la mayoría y el campo parece muerto. Es la misma trampa de `obj.dat`.
    int Snd1 = 0, int Snd2 = 0, int Snd3 = 0)
{
    /// <summary>¿Este mercader te compra ese objeto? Es la condición de <c>Comercio.bas:123</c> dada vuelta:
    /// el VB6 RECHAZA (LocaleMsg 148) si <c>TipoItems &lt;&gt; OBJType And TipoItems &lt;&gt; otCualquiera</c>,
    /// o si el objeto es el ORO. Un NPC sin la clave queda en <c>TipoItems = 0</c> y no compra nada, que es
    /// exactamente lo que hace el original: ningún <c>ObjType</c> vale 0.</summary>
    public bool CompraObjeto(int objType, int objIndex) =>
        objIndex != Game.GameLimits.ObjOro
        && (TipoItems == objType || TipoItems == Game.GameLimits.TipoItemsCualquiera);
}

/// <summary>One NPC spawn point on a map: place NpcIndex's template at (X, Y).</summary>
public readonly record struct MapNpcSpawn(int X, int Y, int NpcIndex);

/// <summary>Un objeto que el .csm trae colocado en el mundo: árboles, carteles, muebles y PUERTAS.</summary>
public readonly record struct MapObjectPlacement(int X, int Y, int ObjIndex, int Amount);

/// <summary>
/// Loads the game data transcoded in Fase 0 (fase0/transcode/out/*.json). Closes the loop with the
/// "reuse the data, rewrite the code" principle: tuned balance (items, spells, NPCs) is read from
/// JSON, not reimplemented. Items are indexed for lookup by handlers (e.g. UseItem reads potion power).
/// </summary>
public sealed class GameData
{
    public IReadOnlyDictionary<int, ObjInfo> Objects { get; private set; } = new Dictionary<int, ObjInfo>();
    public IReadOnlyDictionary<int, SpellInfo> Spells { get; private set; } = new Dictionary<int, SpellInfo>();

    /// <summary>
    /// Modificadores de atributo por RAZA (Balance.dat, sección <c>MODRAZA</c>). El original los suma al crear
    /// el personaje (TCP.bas:343-347) y son fuertes: el Orco arranca con <b>+5 Fuerza y −5 Inteligencia</b>, el
    /// Gnomo al revés (−5 / +4), el Enano +3/+4 de Constitución. Sin esto elegir raza era puramente cosmético,
    /// y encima arrastraba a los stats derivados: la Constitución alimenta el MaxHP y la Inteligencia el MaxMana.
    ///
    /// Indexado por número de raza (1=Humano … 6=Orco, el orden de <c>ListaRazas</c>, General.bas:1720) y por
    /// atributo (1=Fuerza, 2=Agilidad, 3=Inteligencia, 4=Carisma, 5=Constitución).
    /// </summary>
    private readonly Dictionary<(int Raza, int Attr), int> _modRaza = new();

    /// <summary>El bono de un atributo para una raza; 0 si no hay dato.</summary>
    public int ModRaza(int raza, int attr) => _modRaza.GetValueOrDefault((raza, attr), 0);

    private static readonly string[] NombresDeRaza = { "", "Humano", "Elfo", "Drow", "Gnomo", "Enano", "Orco" };
    private static readonly string[] NombresDeAtributo = { "", "Fuerza", "Agilidad", "Inteligencia", "Carisma", "Constitucion" };

    private static void LoadModRaza(GameData data, string path)
    {
        if (!File.Exists(path)) return;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        // El transcode envuelve Balance.dat con las secciones adentro; MODRAZA puede estar a cualquier nivel.
        var modraza = BuscarPropiedad(doc.RootElement, "MODRAZA");
        if (modraza is null) return;
        for (int raza = 1; raza < NombresDeRaza.Length; raza++)
            for (int attr = 1; attr < NombresDeAtributo.Length; attr++)
                if (modraza.Value.TryGetProperty(NombresDeRaza[raza] + NombresDeAtributo[attr], out var v)
                    && int.TryParse(v.GetString(), System.Globalization.NumberStyles.AllowLeadingSign,
                                    System.Globalization.CultureInfo.InvariantCulture, out int bono))
                    data._modRaza[(raza, attr)] = bono;   // los valores vienen con signo ("+1", "-5")

        // Balance.dat §EXTRA: el porcentaje del maná MÁXIMO que devuelve cada acierto de meditación
        // (VB6 `cant = Porcentaje(MaxMAN, PorcentajeRecuperoMana) / 10`, Trabajo.bas:2099). Vale 6 en el
        // install. Si el archivo no lo trae, queda el mismo 6 del default.
        if (BuscarPropiedad(doc.RootElement, "EXTRA") is { } extra
            && extra.TryGetProperty("PorcentajeRecuperoMana", out var prm)
            && int.TryParse(prm.GetString(), out int pct) && pct > 0)
            data.PorcentajeRecuperoMana = pct;
    }

    /// <summary>Busca una propiedad por nombre a cualquier profundidad (el transcode anida distinto por archivo).</summary>
    private static JsonElement? BuscarPropiedad(JsonElement el, string nombre)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        if (el.TryGetProperty(nombre, out var found)) return found;
        foreach (var prop in el.EnumerateObject())
            if (BuscarPropiedad(prop.Value, nombre) is { } r) return r;
        return null;
    }

    /// <summary>Inyecta un hechizo sin cargar el .dat entero (sólo para tests).</summary>
    public void AddSpellForTest(SpellInfo spell)
    {
        var d = Spells as Dictionary<int, SpellInfo> ?? new Dictionary<int, SpellInfo>(Spells);
        d[spell.Index] = spell;
        Spells = d;
    }
    public IReadOnlyDictionary<int, NpcInfo> Npcs { get; private set; } = new Dictionary<int, NpcInfo>();
    /// <summary>Per-map NPC spawn points decoded from the .csm maps (MapSpawns.json): map number →
    /// list of (X, Y, NpcIndex). Populates each map with its authentic AO population.</summary>
    public IReadOnlyDictionary<int, IReadOnlyList<MapNpcSpawn>> MapSpawns { get; private set; }
        = new Dictionary<int, IReadOnlyList<MapNpcSpawn>>();
    /// <summary>Objetos que cada mapa trae colocados en su .csm (MapObjects.json): árboles, carteles, muebles y
    /// puertas. Sin esto el mundo estaba vacío de objetos y no se podía entrar a ningún edificio.</summary>
    public IReadOnlyDictionary<int, IReadOnlyList<MapObjectPlacement>> MapObjects { get; private set; }
        = new Dictionary<int, IReadOnlyList<MapObjectPlacement>>();
    /// <summary>Terreno por-tile de los mapas (MapTerrain.json, extraído de los .csm): dos bitmaps de 100x100
    /// por mapa — agua (VB6 HayAgua: Grh de capa 1 en los rangos de agua con capa 2 vacía) y bloqueos.
    /// Habilita el gate de movimiento (a pie no se entra al agua; navegando sólo se anda por agua).</summary>
    private Dictionary<int, (byte[] Water, byte[] Blocked)> _terrain = new();

    /// <summary>El byte de trigger de cada tile con trigger, por mapa (MapTriggers.json, extraído de los .csm).
    /// Clave interna: <c>(y &lt;&lt; 8) | x</c>; un tile ausente vale 0 (<c>eTrigger.Nada</c>).
    ///
    /// Del enum documentado (Declares.bas:244) tres <b>gatean gameplay</b>: <b>ZONAPELEA</b> (6, arena: morir
    /// ahí auto-revive), <b>ZONASEGURA</b> (4: no se pelea ni se roba — Acciones.bas:543) y <b>POSINVALIDA</b>
    /// (3: los NPCs no pisan el tile — LegalPosNPC).
    ///
    /// SE GUARDA EL VALOR CRUDO Y NO UNA CLASIFICACIÓN, y no es un detalle de estilo: antes había seis
    /// conjuntos, uno por valor del enum, así que un tile con cualquier otro valor caía en el olvido. Este fork
    /// usa valores propios <b>de 20 en adelante</b> para marcar los INTERIORES, y de ellos cuelgan dos reglas
    /// vivas (no se puede montar bajo techo; entrar a caballo te desmonta) más el desvanecido de techos del
    /// cliente. Con el filtro viejo el server veía 9.931 de los 90.014 tiles de interior de los 863 mapas
    /// —el 11%—, y Ullathorpe entero le parecía a la intemperie porque no tiene un solo trigger 1, 2 ni 4.</summary>
    private readonly Dictionary<int, Dictionary<int, byte>> _triggers = new();

    /// <summary>Tiles bloqueados agregados en runtime (fixture: el mapa de test es abierto y no trae una pared
    /// interior para ejercer el gate de blocking). Se suman a los bloqueos del .csm en IsBlockedTile.</summary>
    private readonly Dictionary<int, HashSet<int>> _fixtureBlocked = new();

    /// <summary>
    /// Mapas donde morir cuesta el inventario (MapInfo.json, del header tMapDat de cada .csm). El VB6 deriva
    /// el flag en FileIO.bas:1409-1428 y ahí hay una trampa: la línea que apaga el Pk cuando la zona empieza
    /// con "CIUDAD" queda pisada INMEDIATAMENTE por el bloque de battle_mode, así que no hace nada. La regla
    /// efectiva —y la que extrae decode_mapinfo.py— es <c>Pk = (battle_mode == 0) &amp;&amp; map != 457</c>.
    /// Da 781 mapas Pk y 82 seguros, y los seguros coinciden con la lista hardcodeada que el propio original
    /// dejó comentada dentro de TirarTodo (1 Ullathorpe, 34 Nix, 62 Lindos, 49 Intermundia…).
    /// A las ciudades no las protege este flag sino sus tiles ZONASEGURA, que se chequean aparte.
    /// </summary>
    private readonly HashSet<int> _safeMaps = new();

    /// <summary>¿En este mapa se dropea el inventario al morir? (VB6 MapInfo(map).Pk)</summary>
    public bool IsPkMap(int map) => !_safeMaps.Contains(map);

    /// <summary>
    /// Mapas de NIEVE (campo <c>terrain</c> del mismo header <c>tMapDat</c>). Son 95 de los 863 del install, y
    /// no son cosméticos: <c>EfectoFrio</c> (General.bas:866) le saca a quien ande DESNUDO un 5% del HP máximo
    /// cada <c>IntervaloFrio</c>, y <b>puede matarlo</b>. En cualquier otro terreno el mismo efecto le saca
    /// energía en vez de vida.
    ///
    /// El transcode leía el header para sacar el flag Pk pero se salteaba este campo, así que los 95 mapas
    /// nevados eran indistinguibles de un bosque y andar en bolas por la nieve no costaba nada.
    /// </summary>
    /// <summary>El terreno TAL COMO LO GUARDA EL ORIGINAL: con el relleno del <c>String * 16</c>.
    ///
    /// Suena a detalle y decide la feature entera. El original hace
    /// <code>MapInfo(Map).Terreno = MapDat.terrain</code>
    /// (FileIO.bas:1413) <b>sin</b> <c>Trim$</c> — a diferencia de la línea de abajo, que sí se lo pone a
    /// <c>Zona</c>. Y <c>terrain</c> es un <c>String * 16</c> que en los .csm de este install viene relleno con
    /// ESPACIOS: los bytes crudos del Mapa217 son <c>b"NIEVE           "</c>. Como el gate es
    /// <code>If MapInfo(.Pos.Map).Terreno = Nieve   ' Nieve = "NIEVE"</code>
    /// (General.bas:869) y VB6 compara binario sin rellenar, <c>"NIEVE           " = "NIEVE"</c> da
    /// <b>False</b>. O sea: <b>en el original ningún mapa cargado de un .csm es de nieve</b>, el frío siempre
    /// saca ENERGÍA y no mata a nadie. Lo confirma que la única forma de hacer verdadera esa condición sea
    /// <c>/MODMAPINFO TERRENO</c> (Protocol.bas:10652), que asigna el string ya trimmeado que llegó por el
    /// paquete — y que la lista hardcodeada de mapas 217..250 que había antes del <c>If</c> quedara comentada,
    /// apagada justamente por este chequeo.
    ///
    /// El port trimeaba en el transcode y comparaba case-insensitive, así que activaba los 95 mapas nevados y
    /// mataba de frío a un desnudo donde el original le saca energía en silencio. Se replica el original,
    /// padding incluido, en vez de "arreglarle" el bug: <see cref="MapTerrain"/> es ahora la ÚNICA fuente de
    /// verdad (antes había un HashSet que leía el frío y un diccionario que escribía el comando GM, sin
    /// conexión entre sí: <c>/MODMAPINFO TERRENO NIEVE</c> contestaba que sí y no cambiaba nada).</summary>
    public const string TerrenoNieve = "NIEVE";

    /// <summary>¿El mapa es de nieve? (VB6 <c>MapInfo(map).Terreno = Nieve</c>) — comparación EXACTA, que es
    /// lo que hace que el .csm relleno con espacios nunca matchee. Ver <see cref="TerrenoNieve"/>.</summary>
    public bool IsSnowMap(int map) => MapTerrain(map) == TerrenoNieve;

    /// <summary>Marca el terreno en runtime (lo necesita el fixture, que no sale de un .csm). Escribe el
    /// string SIN relleno, o sea que se comporta como el <c>/MODMAPINFO TERRENO</c> del original: es el único
    /// camino por el que la nieve se enciende de verdad.</summary>
    public void SetMapSnow(int map, bool nieve) => _mapTerrain[map] = nieve ? TerrenoNieve : "BOSQUE";

    /// <summary>Marca un mapa como seguro en runtime (el fixture necesita las dos variantes).</summary>
    public void SetMapSafe(int map, bool safe)
    {
        if (safe) _safeMaps.Add(map); else _safeMaps.Remove(map);
    }

    // Los otros dos campos que edita /MODMAPINFO. En esta variante del VB6 son campos que SÓLO SE ESCRIBEN
    // (FileIO.bas:1415/1417 al cargar el .dat, y los handlers del comando): ningún sitio los lee para decidir
    // nada, así que acá tampoco gatean nada. Se guardan para que el estado del server coincida con el del
    // original y para que /GUARDAMAPA los pueda volcar. Ver fase5/GAPS-CODIGO-MUERTO.md §4k.
    private readonly HashSet<int> _mapBackup = new();
    private readonly Dictionary<int, string> _mapRestrict = new();

    /// <summary>¿El mapa está marcado para el backup periódico? (VB6 MapInfo(map).BackUp, campo inerte).</summary>
    public bool MapBackup(int map) => _mapBackup.Contains(map);
    public void SetMapBackup(int map, bool on) { if (on) _mapBackup.Add(map); else _mapBackup.Remove(map); }

    /// <summary>Modo restringido del mapa (VB6 MapInfo(map).Restringir: "NEWBIE"/"NO"/"ARMADA"/"CAOS"/"FACCION",
    /// campo inerte). Default "NO", que es lo que trae el .dat de casi todos los mapas.</summary>
    public string MapRestrict(int map) => _mapRestrict.GetValueOrDefault(map, "NO");
    public void SetMapRestrict(int map, string modo) => _mapRestrict[map] = modo;

    // Los tres flags de MapInfo que SÍ gatean gameplay y que —a diferencia de Pk o Zona— NO se cargan de ningún
    // archivo: son estado puramente runtime que sólo prenden los /MODMAPINFO correspondientes (y el ResuSinEfecto
    // de la prisión, que el original clava al arrancar en General.bas:298).
    private readonly HashSet<int> _noMagic = new();
    private readonly HashSet<int> _noInvi = new();
    private readonly HashSet<int> _noResu = new();

    /// <summary>¿Mapa sin magia? (VB6 MapInfo(map).MagiaSinEfecto): ahí no se puede lanzar ningún hechizo.</summary>
    public bool MapNoMagic(int map) => _noMagic.Contains(map);
    public void SetMapNoMagic(int map, bool on) { if (on) _noMagic.Add(map); else _noMagic.Remove(map); }

    /// <summary>¿Mapa sin invisibilidad? (VB6 InviSinEfecto): no se puede ocultar ni lanzar Invisibilidad.</summary>
    public bool MapNoInvi(int map) => _noInvi.Contains(map);
    public void SetMapNoInvi(int map, bool on) { if (on) _noInvi.Add(map); else _noInvi.Remove(map); }

    /// <summary>¿Mapa sin resurrección? (VB6 ResuSinEfecto): el hechizo Resucitar no funciona ahí. El original lo
    /// clava en el mapa de la PRISIÓN al arrancar, para que a un preso no lo revivan de afuera.</summary>
    public bool MapNoResu(int map) => _noResu.Contains(map);
    public void SetMapNoResu(int map, bool on) { if (on) _noResu.Add(map); else _noResu.Remove(map); }

    /// <summary>Zona del mapa (VB6 MapInfo(map).Zona: "CIUDAD"/"CAMPO"/"DUNGEON"/"NEWBIE"…). Se carga de
    /// MapInfo.json y la puede cambiar /MODMAPINFO ZONA.</summary>
    public string MapZone(int map) => _mapZone.GetValueOrDefault(map, "CAMPO");
    public void SetMapZone(int map, string zona) => _mapZone[map] = zona;
    private readonly Dictionary<int, string> _mapZone = new();

    /// <summary>Nombre del mapa (VB6 <c>MapInfo(map).Name</c>), tal como viene del header del .csm.</summary>
    public string MapName(int map) => _mapName.GetValueOrDefault(map, "");
    private readonly Dictionary<int, string> _mapName = new();

    /// <summary>
    /// ¿Es un mapa de MAR ABIERTO? Lo decide el NOMBRE, que es el único lugar donde el mundo lo dice: 403 de
    /// los 863 mapas se llaman "Océano abierto" y son 100&#37; agua. Ningún otro campo sirve — la zona de todos
    /// ellos es <c>CAMPO</c> y el terreno <c>BOSQUE</c>, igual que la de un bosque cualquiera.
    ///
    /// <para>La comparación va SIN acentos y sin distinguir mayúsculas porque el header del .csm trae TRES
    /// grafías del mismo nombre —"Oceano abierto", "Océano abierto" y "Océano Abierto"—: comparando literal se
    /// perdían dos de las tres. Es la misma trampa que las claves del <c>obj.dat</c>.</para>
    ///
    /// <para>Esto NO es del original: el VB6 no distingue el mar abierto en ninguna parte. Lo usa sólo la regla
    /// de la casa <see cref="ServerIni.HipocamposEnMarAbierto"/>, y por eso vive acá y no en un gate de pesca.</para>
    /// </summary>
    public bool EsOceanoAbierto(int map) => _oceanoAbierto.Contains(map);
    private readonly HashSet<int> _oceanoAbierto = new();

    /// <summary>Quita acentos y pasa a mayúsculas, para comparar nombres que la data escribe de varias formas.</summary>
    private static string SinAcentos(string s)
    {
        var d = (s ?? "").Normalize(System.Text.NormalizationForm.FormD);
        var sb = new System.Text.StringBuilder(d.Length);
        foreach (char c in d)
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c)
                != System.Globalization.UnicodeCategory.NonSpacingMark) sb.Append(c);
        return sb.ToString().ToUpperInvariant().Trim();
    }

    /// <summary>Marca un mapa como mar abierto (lo necesita el fixture, que no sale de un .csm).</summary>
    public void SetOceanoAbierto(int map, bool esMar)
    {
        if (esMar) _oceanoAbierto.Add(map); else _oceanoAbierto.Remove(map);
    }

    /// <summary>El <c>battle_mode</c> crudo del header <c>tMapDat</c> (VB6 <c>MapInfo(map).battle_mode</c>,
    /// FileIO.bas:1412). Hasta ahora sólo se usaba DERIVADO —el flag Pk es <c>battle_mode == 0</c>— y el número
    /// en sí se tiraba, pero hay un gate que lo lee tal cual: el mercader que no le vende a los de una facción
    /// fuera de su territorio (Protocol.bas:5205 y Acciones.bas:182), donde 2 y 3 son los valores que habilitan.
    ///
    /// <para>El reparto del install: 0 = el campo (782 mapas), 1 = las ciudades viejas (Ullathorpe, Nix,
    /// Banderbill, Arghâl…), 2 = Lindos (12), 3 = Rinkel / Nueva Esperanza / el Dungeon de Newbies (43), 4 = 7
    /// mapas sueltos. O sea que en este fork el comercio vive en Rinkel y Lindos y las ciudades clásicas
    /// quedaron cerradas — medido contra el oráculo vivo, no deducido.</para></summary>
    public int MapBattleMode(int map) => _mapBattleMode.GetValueOrDefault(map, 0);
    public void SetMapBattleMode(int map, int modo) => _mapBattleMode[map] = modo;
    private readonly Dictionary<int, int> _mapBattleMode = new();

    /// <summary>Terreno del mapa (VB6 MapInfo(map).Terreno: "BOSQUE"/"NIEVE"/"DESIERTO"…). El transcode NO
    /// extrajo este campo del header de los .csm, así que arranca en "BOSQUE" para todos y sólo lo mueve
    /// /MODMAPINFO TERRENO. Su único consumidor en el original es el daño por frío en los mapas de nieve
    /// (General.bas:869), que este slice no modela.</summary>
    public string MapTerrain(int map) => _mapTerrain.GetValueOrDefault(map, "BOSQUE");
    public void SetMapTerrain(int map, string terreno) => _mapTerrain[map] = terreno;
    private readonly Dictionary<int, string> _mapTerrain = new();

    private static void LoadMapInfo(GameData data, string path)
    {
        if (!File.Exists(path)) return;   // sin el archivo, todo mapa es Pk (el default del original)
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var m in doc.RootElement.EnumerateObject())
        {
            if (!int.TryParse(m.Name, out int map)) continue;
            if (m.Value.TryGetProperty("pk", out var pk) && pk.ValueKind == JsonValueKind.False)
                data._safeMaps.Add(map);
            // La zona ("CIUDAD"/"CAMPO"/"DUNGEON"/…) es el otro campo de MapInfo que el transcode sí extrajo.
            if (m.Value.TryGetProperty("zone", out var zone) && zone.ValueKind == JsonValueKind.String)
                data._mapZone[map] = zone.GetString() ?? "CAMPO";
            // El TERRENO, con el relleno del `String * 16` del original puesto de vuelta: el transcode hace
            // `.strip()` al leer el .csm y el VB6 NO (FileIO.bas:1413, sin Trim$), y de esa diferencia depende
            // que el frío saque vida o energía. Ver TerrenoNieve.
            if (m.Value.TryGetProperty("terrain", out var ter) && ter.ValueKind == JsonValueKind.String)
                data._mapTerrain[map] = (ter.GetString() ?? "BOSQUE").PadRight(16);
            // El NOMBRE del mapa. Estaba en MapInfo.json desde siempre y no lo leía nadie; lo pide el anuncio
            // del casamiento, que el original manda a TODO el servidor con "mapa%novio%novia" (Trabajo.bas:32).
            if (m.Value.TryGetProperty("name", out var nom) && nom.ValueKind == JsonValueKind.String)
            {
                data._mapName[map] = nom.GetString() ?? "";
                // Ver EsOceanoAbierto: el nombre es el único campo que marca el mar abierto, y viene con tres
                // grafías distintas.
                if (SinAcentos(data._mapName[map]).Contains("OCEANO ABIERTO")) data._oceanoAbierto.Add(map);
            }
            // El battle_mode CRUDO: el `pk` de arriba ya es su derivado, pero el gate del comercio por facción
            // compara contra el 2 y el 3, que desde `pk` no se distinguen (los dos son "no Pk"). Ver MapBattleMode.
            if (m.Value.TryGetProperty("battleMode", out var bm) && bm.TryGetInt32(out int modo))
                data._mapBattleMode[map] = modo;
        }
    }

    private static bool Bit(byte[]? bm, int x, int y)
    {
        if (bm is null || x < 1 || x > 100 || y < 1 || y > 100) return false;
        int i = (y - 1) * 100 + (x - 1);
        return (bm[i >> 3] & (1 << (i & 7))) != 0;
    }

    /// <summary>¿El tile es agua? (VB6 HayAgua). Fuera de mapa conocido → false.</summary>
    public bool IsWater(int map, int x, int y) => _terrain.TryGetValue(map, out var t) && Bit(t.Water, x, y);

    /// <summary>¿El mapa existe? (VB6 <c>MapaValido</c>, General.bas:411 — `Map >= 1 And Map &lt;= NumMaps`).
    /// Los comandos GM que reciben un número de mapa lo usan para callarse ante un mapa inválido.</summary>
    public bool MapExists(int map) => _terrain.ContainsKey(map);

    /// <summary>Tiles DESbloqueados en runtime: el `Blocked` del VB6 es mutable (`Bloquear`, General.bas:112) y
    /// una puerta al abrirse lo pone en 0. Como el .csm trae el tile de cada puerta ya bloqueado, sin este
    /// override no habría forma de entrar a un edificio: abrir la puerta es, literalmente, quitar el bloqueo.</summary>
    private readonly Dictionary<int, HashSet<int>> _unblocked = new();

    /// <summary>¿El tile está bloqueado? (VB6 MapData.Blocked): pared del .csm o bloqueo agregado por el fixture,
    /// salvo que algo lo haya desbloqueado en runtime (una puerta abierta).</summary>
    public bool IsBlockedTile(int map, int x, int y) =>
        !(_unblocked.TryGetValue(map, out var ub) && ub.Contains((y << 8) | x))
        && ((_terrain.TryGetValue(map, out var t) && Bit(t.Blocked, x, y))
            || (_fixtureBlocked.TryGetValue(map, out var fb) && fb.Contains((y << 8) | x)));

    /// <summary>Marca un tile como bloqueado en runtime (fixture: pared interior para el test de blocking).</summary>
    public void AddBlocked(int map, int x, int y)
    {
        if (!_fixtureBlocked.TryGetValue(map, out var fb)) _fixtureBlocked[map] = fb = new HashSet<int>();
        fb.Add((y << 8) | x);
    }

    /// <summary>Pone/quita el bloqueo de un tile en runtime (VB6 `Bloquear`): lo que hace una puerta al abrirse
    /// o cerrarse. Puede desbloquear un tile que el .csm trae bloqueado.</summary>
    public void SetTileBlocked(int map, int x, int y, bool blocked)
    {
        if (!_unblocked.TryGetValue(map, out var ub)) _unblocked[map] = ub = new HashSet<int>();
        // `_unblocked` es un override NEGATIVO: quitar una entrada sólo revoca un desbloqueo previo. Para BLOQUEAR
        // de verdad un tile que el .csm trae libre (lo que hace /BLOQ, que se tira parado sobre un tile caminable)
        // hay que además marcarlo en `_fixtureBlocked`, que es lo que IsBlockedTile consulta. Sin esto el comando
        // era un no-op que encima MENTÍA al cliente (le mandaba BlockPosition(true) y el server lo dejaba pasar).
        if (blocked) { ub.Remove((y << 8) | x); AddBlocked(map, x, y); }
        else ub.Add((y << 8) | x);
    }

    /// <summary>El byte de trigger de un tile (VB6 <c>MapData(map,x,y).Trigger</c>), que es lo que informa el
    /// comando GM <c>/TRIGGER</c>. Un tile sin trigger es 0 (eTrigger.Nada).</summary>
    public byte TileTrigger(int map, int x, int y) =>
        _triggers.TryGetValue(map, out var t) && t.TryGetValue((y << 8) | x, out var v) ? v : (byte)0;

    /// <summary>¿El tile es zona de pelea/arena? (VB6 eTrigger.ZONAPELEA). Fuera de mapa conocido → false.</summary>
    public bool IsArena(int map, int x, int y) => TileTrigger(map, x, y) == 6;

    /// <summary>¿El tile es zona segura? (VB6 eTrigger.ZONASEGURA): ahí no se puede pelear ni robar.</summary>
    public bool IsSafeZone(int map, int x, int y) => TileTrigger(map, x, y) == 4;

    /// <summary>¿El tile es posición inválida para NPCs? (VB6 eTrigger.POSINVALIDA): no lo pueden pisar.</summary>
    public bool IsNpcInvalidTile(int map, int x, int y) => TileTrigger(map, x, y) == 3;

    // NO hay acá un `IsUnderRoof`. La regla de "bajo techo" (Trigger = 1, 2, 4 o >= 20) es del CLIENTE —
    // TileEngine.bas:1690 y RoofAlphaCalculate :3837, para desvanecer los techos— y en el SERVER los valores
    // 1, 2 y 4 no gatean NADA vivo, aunque a primera vista parezca que sí:
    //   . `Intemperie` (General.bas:798), la función que decide si la lluvia te pega, tiene CERO call sites;
    //   . los dos guards de regeneración (General.bas:1088 y :1243) dicen
    //     `If Trigger = 1 And Trigger = 2 And Trigger = 4 Then Exit Sub` — tres igualdades sobre la MISMA
    //     variable unidas por `And`, o sea que nunca son ciertas las tres. El autor quiso `Or`; quedó `And`.
    // Ponerlo en el server sería inventar una regla que la fuente no aplica.

    /// <summary>¿Se puede andar a caballo acá? El original lo prohíbe con <c>Trigger &gt;= 20</c> <b>o</b> zona
    /// <c>"DUNGEON"</c> — las dos mitades de la misma condición, en los dos sentidos: intentar montar ahí lo
    /// rechaza (Trabajo.bas:2345, <c>LocaleMsg(253)</c>) y entrar a caballo <b>desmonta</b>
    /// (Modulo_UsUaRiOs.bas:718). OJO que acá el umbral es <c>&gt;= 20</c> pelado y NO la regla de techo:
    /// un tile con trigger 1 o 4 está bajo techo para el render y aun así se puede montar.</summary>
    public bool NoSeMontaAca(int map, int x, int y) =>
        TileTrigger(map, x, y) >= 20 || MapZone(map) == "DUNGEON";

    /// <summary>Marca un tile como arena en runtime (para el fixture: el mapa de test no trae zonas de pelea).</summary>
    public void AddArena(int map, int x, int y) => SetTileTrigger(map, x, y, 6);

    /// <summary>Marca un tile como zona segura en runtime (fixture: el mapa de test no trae ZONASEGURA).</summary>
    public void AddSafeZone(int map, int x, int y) => SetTileTrigger(map, x, y, 4);

    /// <summary>Cambia el trigger de un tile (VB6 HandleSetTrigger: <c>MapData(...).Trigger = tTrigger</c>).
    /// El trigger ES el dato que consultan los gates, así que escribirlo alcanza: poner 4 lo vuelve zona segura
    /// de verdad, 6 lo vuelve arena, 3 lo hace impisable para NPCs y ≥20 lo pone bajo techo.</summary>
    public void SetTileTrigger(int map, int x, int y, byte trigger)
    {
        if (!_triggers.TryGetValue(map, out var t)) _triggers[map] = t = new Dictionary<int, byte>();
        int key = (y << 8) | x;
        if (trigger == 0) t.Remove(key); else t[key] = trigger;
    }

    /// <summary>¿Hay agua en alguno de los 4 tiles adyacentes? (VB6 DoNavega exige costa para embarcar).</summary>
    public bool WaterAdjacent(int map, int x, int y) =>
        IsWater(map, x + 1, y) || IsWater(map, x - 1, y) || IsWater(map, x, y + 1) || IsWater(map, x, y - 1);

    /// <summary>Tile-exits (teleports) decoded from the .csm maps (MapExits.json): (map,x,y) → destination
    /// (map,x,y). Stepping onto an exit tile warps the player. Keyed for O(1) lookup on each Walk.</summary>
    public IReadOnlyDictionary<(int Map, int X, int Y), (int DestMap, int DestX, int DestY)> MapExits => _mapExits;
    private Dictionary<(int Map, int X, int Y), (int DestMap, int DestX, int DestY)> _mapExits = new();

    /// <summary>Crea/reemplaza la salida de un tile en RUNTIME (VB6 <c>MapData(...).TileExit</c>, que /CT escribe
    /// a mano). Como el <c>TileExit</c> del original, vive en memoria: no se reescribe el .csm, así que un
    /// teleport creado por un GM se pierde al reiniciar — igual que en la fuente.</summary>
    public void SetMapExit(int map, int x, int y, int destMap, int destX, int destY) =>
        _mapExits[(map, x, y)] = (destMap, destX, destY);

    /// <summary>Borra la salida de un tile (VB6 <c>.TileExit.Map = 0</c> de /DT). Devuelve si había una.</summary>
    public bool RemoveMapExit(int map, int x, int y) => _mapExits.Remove((map, x, y));
    /// <summary>Experience needed to advance FROM each level, loaded from Niveles.dat (levelELU).</summary>
    /// <summary>Kit inicial por clase (NACIMIENTO.dat): clase → ítems con los que nace el personaje.</summary>
    public IReadOnlyDictionary<int, IReadOnlyList<NewbieItem>> NewbieKits { get; private set; }
        = new Dictionary<int, IReadOnlyList<NewbieItem>>();

    public IReadOnlyDictionary<int, long> LevelExp { get; private set; } = new Dictionary<int, long>();
    /// <summary>Per-class melee damage modifier (unarmed / wrestling), from Balance.dat MODDANOWRESTLING.</summary>
    /// <summary>MODVIDA (Balance.dat): HP promedio que gana por nivel cada clase. Lo usa Leveling.</summary>
    public IReadOnlyDictionary<int, double> LifeMod { get; private set; } = new Dictionary<int, double>();

    /// <summary>Balance.dat §EXTRA <c>PorcentajeRecuperoMana</c>: qué porcentaje del maná MÁXIMO devuelve cada
    /// acierto de meditación. Vale 6 en el install, y el original todavía lo divide por 10 — o sea que un
    /// acierto devuelve el 0,6% del maná máximo, con un piso de 1. Ver <c>Survival.ManaAlMeditar</c>.</summary>
    public int PorcentajeRecuperoMana { get; private set; } = 6;

    public IReadOnlyDictionary<int, double> WrestlingMod { get; private set; } = new Dictionary<int, double>();
    /// <summary>Per-class melee damage modifier (weapon), from Balance.dat MODDANOARMAS.</summary>
    public IReadOnlyDictionary<int, double> WeaponMod { get; private set; } = new Dictionary<int, double>();
    /// <summary>Per-class evasion modifier, from Balance.dat MODEVASION.</summary>
    public IReadOnlyDictionary<int, double> EvasionMod { get; private set; } = new Dictionary<int, double>();
    /// <summary>Per-class weapon attack-power modifier, from Balance.dat MODATAQUEARMAS.</summary>
    public IReadOnlyDictionary<int, double> AttackMod { get; private set; } = new Dictionary<int, double>();
    /// <summary>Modificador de clase al ACIERTO con arco (Balance.dat MODATAQUEPROYECTILES → PoderAtaqueProyectil).</summary>
    public IReadOnlyDictionary<int, double> ProjectileAttackMod { get; private set; } = new Dictionary<int, double>();
    /// <summary>Modificador de clase al DAÑO con arco (Balance.dat MODDAÑOPROYECTILES → CalcularDaño). La ñ se cae en Norm.</summary>
    public IReadOnlyDictionary<int, double> ProjectileDamageMod { get; private set; } = new Dictionary<int, double>();
    /// <summary>Per-class shield block/evasion modifier, from Balance.dat MODESCUDO (PoderEvasionEscudo).</summary>
    public IReadOnlyDictionary<int, double> ShieldMod { get; private set; } = new Dictionary<int, double>();
    /// <summary>Object indices a blacksmith can forge, from ArmasHerrero.dat (the [Arma] Index list).</summary>
    public IReadOnlySet<int> BlacksmithWeapons { get; private set; } = new HashSet<int>();
    /// <summary>Recetas de ARMADURA del herrero (ArmadurasHerrero.json). Cascos y escudos van aparte: el VB6
    /// tiene un paquete propio para cada lista (BlacksmithArmors/Helmet/Shield).</summary>
    public IReadOnlySet<int> BlacksmithArmors { get; private set; } = new HashSet<int>();
    /// <summary>Recetas de CASCO del herrero (CascosHerrero.json) → paquete BlacksmithHelmet (id 50).</summary>
    public IReadOnlySet<int> BlacksmithHelmets { get; private set; } = new HashSet<int>();
    /// <summary>Recetas de ESCUDO del herrero (EscudosHerrero.json) → paquete BlacksmithShield (id 51).</summary>
    public IReadOnlySet<int> BlacksmithShields { get; private set; } = new HashSet<int>();
    /// <summary>Todo lo forjable en la herrería = armas ∪ armaduras/cascos/escudos. El VB6 PuedeConstruirHerreria
    /// (Trabajo.bas:608) acepta membresía en CUALQUIERA de las cuatro listas, no solo las armas; el gate de
    /// fabricación debe usar este set combinado (BlacksmithWeapons solo alcanzaba a forjar armas).</summary>
    public IReadOnlySet<int> BlacksmithRecipes { get; private set; } = new HashSet<int>();
    /// <summary>Object indices a carpenter can build, from ObjCarpintero.dat ([INIT] Index list).</summary>
    public IReadOnlySet<int> CarpenterItems { get; private set; } = new HashSet<int>();
    /// <summary>Object indices a tailor can sew, from ObjSastre.dat ([INIT] Index list).</summary>
    public IReadOnlySet<int> TailorItems { get; private set; } = new HashSet<int>();
    /// <summary>Object indices an alchemist can brew, from ObjDruida.dat ([INIT] Index list).</summary>
    public IReadOnlySet<int> AlchemyItems { get; private set; } = new HashSet<int>();
    /// <summary>La tabla de DROP GLOBAL (DropGlobal.dat), ya filtrada: sólo las entradas con peso positivo.
    /// Vacía si el archivo no está. Ver <see cref="DropGlobalItem"/> — la usa la regla de la casa del mismo
    /// nombre; con la regla apagada (el default) nadie la mira.</summary>
    public IReadOnlyList<DropGlobalItem> DropsGlobales { get; private set; } = Array.Empty<DropGlobalItem>();
    /// <summary>La suma de los pesos de <see cref="DropsGlobales"/>, para sortear sin recorrer dos veces.</summary>
    public int PesoTotalDropGlobal { get; private set; }

    /// <summary>
    /// Qué ítem de la tabla global sale para una <paramref name="tirada"/> en <c>[0, PesoTotalDropGlobal)</c>.
    /// Ruleta por peso: camina la lista restando, así que cada entrada se lleva una franja del tamaño de su
    /// <c>PorcentajeDrop</c> — las dos que valen 1 salen veinte veces menos que las que valen 20.
    ///
    /// <para>Es pura y toma la tirada por parámetro a propósito: así el reparto se puede verificar barriendo
    /// las <see cref="PesoTotalDropGlobal"/> tiradas posibles, sin azar y sin levantar un servidor.</para>
    /// </summary>
    public DropGlobalItem? DropGlobalPara(int tirada)
    {
        if (tirada < 0 || tirada >= PesoTotalDropGlobal) return null;
        foreach (var g in DropsGlobales)
        {
            tirada -= g.Peso;
            if (tirada < 0) return g;
        }
        return null;
    }
    public int ItemCount => Objects.Count;
    public int SpellCount => Spells.Count;
    public int NpcCount => Npcs.Count;
    public string? SourceDir { get; private set; }

    // --- Recarga en caliente (VB6 CargaNpcsDat / CargarHechizos / LoadOBJData, los tres /RELOAD*) ---
    // Releen su archivo y reemplazan el diccionario entero, igual que el original. Devuelven cuántas
    // entradas quedaron, o -1 si no hay de dónde leer. Los NPCs YA SPAWNEADOS conservan la plantilla vieja
    // (guardan la referencia), que es exactamente lo que pasa en el VB6: la recarga afecta a los que nazcan
    // después. Los ítems que ya están en un inventario tampoco cambian de índice, sólo de datos.

    /// <summary>Recarga NPCs.json (VB6 CargaNpcsDat).</summary>
    public int ReloadNpcs()
    {
        if (SourceDir is null) return -1;
        Npcs = LoadNpcs(Path.Combine(SourceDir, "NPCs.json"));
        return Npcs.Count;
    }

    /// <summary>Recarga Hechizos.json (VB6 CargarHechizos).</summary>
    public int ReloadSpells()
    {
        if (SourceDir is null) return -1;
        Spells = LoadSpells(Path.Combine(SourceDir, "Hechizos.json"));
        return Spells.Count;
    }

    /// <summary>Recarga obj.json (VB6 LoadOBJData).</summary>
    public int ReloadObjects()
    {
        if (SourceDir is null) return -1;
        Objects = LoadObjects(Path.Combine(SourceDir, "obj.json"));
        return Objects.Count;
    }

    public ObjInfo? Obj(int index) => Objects.TryGetValue(index, out var o) ? o : null;
    public SpellInfo? Spell(int index) => Spells.TryGetValue(index, out var s) ? s : null;
    public NpcInfo? Npc(int index) => Npcs.TryGetValue(index, out var n) ? n : null;

    /// <summary>Kit inicial de la clase (NACIMIENTO.dat). Vacío si la clase no está en la tabla.</summary>
    public IReadOnlyList<NewbieItem> NewbieKit(int @class) =>
        NewbieKits.TryGetValue(@class, out var k) ? k : Array.Empty<NewbieItem>();

    /// <summary>Los archivos que el server espera encontrar, y qué se pierde si falta cada uno.
    ///
    /// <para>Todos los cargadores devuelven vacío cuando no encuentran su archivo y siguen como si nada. Sin
    /// <c>Balance.json</c> los modificadores de combate quedan en cero; sin <c>NACIMIENTO.json</c> los
    /// personajes nuevos nacen desnudos; sin <c>MapTriggers.json</c> no hay un solo interior. Y la línea de
    /// resumen sólo cuenta cinco de las veinte cosas, así que el resto desaparece sin una palabra: el juego
    /// arranca roto de un modo que no se parece a un error, se parece a que el original era así.</para>
    ///
    /// <para>Es la falla que ve el que RECIBE el paquete, no nosotros — acá los veinte están. Un archivo que
    /// no viaja en el zip, un <c>.gitignore</c> de más, un transcode a medias, y del otro lado falta medio
    /// juego. Agregar un cargador nuevo es agregar su renglón acá.</para></summary>
    public static readonly (string Archivo, string QueSePierde)[] ArchivosDeDatos =
    {
        ("obj.json", "todos los objetos del juego"),
        ("Niveles.json", "la experiencia que pide cada nivel"),
        ("Balance.json", "los modificadores de combate por clase y los bonos de raza"),
        ("Hechizos.json", "todos los hechizos"),
        ("NPCs.json", "todos los NPCs"),
        ("ArmasHerrero.json", "las armas que forja el herrero"),
        ("ArmadurasHerrero.json", "las armaduras que forja el herrero"),
        ("CascosHerrero.json", "los cascos que forja el herrero"),
        ("EscudosHerrero.json", "los escudos que forja el herrero"),
        ("ObjCarpintero.json", "lo que fabrica el carpintero"),
        ("ObjSastre.json", "lo que fabrica el sastre"),
        ("ObjDruida.json", "lo que prepara el druida"),
        ("MapTerrain.json", "el agua y el terreno de los mapas"),
        ("MapTriggers.json", "los interiores y las zonas especiales"),
        ("MapSpawns.json", "los NPCs que pueblan el mundo"),
        ("MapObjects.json", "los objetos tirados por el mundo"),
        ("MapExits.json", "las salidas entre mapas"),
        ("MapInfo.json", "la ficha de cada mapa: sin esto TODO mapa queda Pk"),
        ("NACIMIENTO.json", "el equipo inicial de cada clase"),
        ("NombresInvalidos.json", "el filtro de nombres prohibidos"),
        ("DropGlobal.json", "la tabla del drop global (sólo la usa la regla de la casa DropGlobal)"),
    };

    public static GameData Load()
    {
        var data = new GameData();
        var dir = FindDataDir();
        if (dir is null)
        {
            Console.WriteLine("AVISO: no encontre fase0/transcode/out — el servidor arranca sin datos de juego.");
            return data;
        }
        data.SourceDir = dir;
        var faltantes = ArchivosDeDatos.Where(a => !File.Exists(Path.Combine(dir, a.Archivo))).ToArray();
        if (faltantes.Length > 0)
        {
            Console.Error.WriteLine($"AVISO: faltan {faltantes.Length} archivo(s) de datos en {dir}. El servidor arranca SIN:");
            foreach (var (archivo, pierde) in faltantes)
                Console.Error.WriteLine($"  - {archivo}: {pierde}");
        }
        data.Objects = LoadObjects(Path.Combine(dir, "obj.json"));
        data.LevelExp = LoadLevels(Path.Combine(dir, "Niveles.json"));
        var mods = LoadClassMods(Path.Combine(dir, "Balance.json"));
        data.WrestlingMod = mods.GetValueOrDefault("moddaowrestling", new());
        data.WeaponMod = mods.GetValueOrDefault("moddaoarmas", new());
        data.EvasionMod = mods.GetValueOrDefault("modevasion", new());
        data.AttackMod = mods.GetValueOrDefault("modataquearmas", new());
        data.ProjectileAttackMod = mods.GetValueOrDefault("modataqueproyectiles", new());
        data.ProjectileDamageMod = mods.GetValueOrDefault("moddaoproyectiles", new());   // MODDAÑOPROYECTILES sin ñ
        data.ShieldMod = mods.GetValueOrDefault("modescudo", new());
        data.LifeMod = mods.GetValueOrDefault("modvida", new());   // MODVIDA: HP que gana por nivel cada clase
        data.Spells = LoadSpells(Path.Combine(dir, "Hechizos.json"));
        data.Npcs = LoadNpcs(Path.Combine(dir, "NPCs.json"));
        data.BlacksmithWeapons = LoadCraftIndices(Path.Combine(dir, "ArmasHerrero.json"), "Arma");
        data.BlacksmithArmors = LoadCraftIndices(Path.Combine(dir, "ArmadurasHerrero.json"), "Armadura");
        data.BlacksmithHelmets = LoadCraftIndices(Path.Combine(dir, "CascosHerrero.json"), "Casco");
        data.BlacksmithShields = LoadCraftIndices(Path.Combine(dir, "EscudosHerrero.json"), "Escudo");
        var forjables = new HashSet<int>(data.BlacksmithWeapons);
        forjables.UnionWith(data.BlacksmithArmors);
        forjables.UnionWith(data.BlacksmithHelmets);
        forjables.UnionWith(data.BlacksmithShields);
        data.BlacksmithRecipes = forjables;   // el gate de fabricación acepta cualquiera de las cuatro listas
        data.CarpenterItems = LoadRecipeIndexList(Path.Combine(dir, "ObjCarpintero.json"));
        data.TailorItems = LoadRecipeIndexList(Path.Combine(dir, "ObjSastre.json"));
        data.AlchemyItems = LoadRecipeIndexList(Path.Combine(dir, "ObjDruida.json"));
        data.DropsGlobales = LoadDropsGlobales(Path.Combine(dir, "DropGlobal.json"));
        data.PesoTotalDropGlobal = data.DropsGlobales.Sum(d => d.Peso);
        data._terrain = LoadTerrain(Path.Combine(dir, "MapTerrain.json"));
        LoadTriggers(data, Path.Combine(dir, "MapTriggers.json"));
        data.MapSpawns = LoadMapSpawns(Path.Combine(dir, "MapSpawns.json"));
        data.MapObjects = LoadMapObjects(Path.Combine(dir, "MapObjects.json"));
        data._mapExits = LoadMapExits(Path.Combine(dir, "MapExits.json"));
        data.NewbieKits = LoadNewbieKits(Path.Combine(dir, "NACIMIENTO.json"));
        LoadMapInfo(data, Path.Combine(dir, "MapInfo.json"));
        LoadModRaza(data, Path.Combine(dir, "Balance.json"));
        Naming.LoadForbiddenNames(Path.Combine(dir, "NombresInvalidos.json"));   // VB6 NombrePermitido
        Console.WriteLine($"Datos cargados desde {dir}: {data.ItemCount} objetos, {data.SpellCount} hechizos, {data.NpcCount} NPCs, {data.LevelExp.Count} niveles, spawns en {data.MapSpawns.Count} mapa(s), objetos de mundo en {data.MapObjects.Count} mapa(s), {data.MapExits.Count} tile-exits.");
        return data;
    }

    private static Dictionary<int, ObjInfo> LoadObjects(string file)
    {
        var map = new Dictionary<int, ObjInfo>();
        if (!File.Exists(file)) return map;
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        if (!doc.RootElement.TryGetProperty("OBJ", out var arr) || arr.ValueKind != JsonValueKind.Array) return map;
        foreach (var el in arr.EnumerateArray())
        {
            int index = el.TryGetProperty("_index", out var ix) ? ix.GetInt32() : 0;
            if (index <= 0) continue;
            map[index] = new ObjInfo(
                (short)index,
                Str(el, "Name"),
                Int(el, "ObjType", "Objtype"),
                Int(el, "MaxModificador"),
                Int(el, "Valor"),
                Int(el, "Anim"),
                Int(el, "SubTipo", "Subtipo"),  // OBJ.dat mezcla casing: pociones "SubTipo", cascos/escudos "Subtipo"
                Int(el, "MinModificador"),
                Int(el, "DuracionEfecto"),
                Int(el, "LingH"), Int(el, "LingP"), Int(el, "LingO"), Int(el, "SkHerreria"),
                Int(el, "Madera"), Int(el, "SkCarpinteria"),
                Int(el, "PielLobo"), Int(el, "PielOsoPardo"), Int(el, "PielOsoPolar"),
                Int(el, "Sksastreria", "SkSastreria"),
                Int(el, "Raices", "Raiz"), Int(el, "SkAlquimia", "SkPociones"),
                Int(el, "MINDEF", "MinDef"), Int(el, "MAXDEF", "MaxDef"),
                Int(el, "MinHIT", "MinHit"), Int(el, "MaxHIT", "MaxHit"),
                Int(el, "MinHam", "MinHAM"), Int(el, "MinAgu", "MinAGU"),
                Int(el, "NumRopaje"),   // armadura: body-graphic (el que muestra el personaje al vestirla)
                Int(el, "MinELV"), Int(el, "Genero", "genero"), Int(el, "DosManos"),
                Str(el, "ClasesProhibidas", "clasesprohibidas"),
                Str(el, "RazasProhibidas", "razasprohibidas", "Razasprohibidas"),
                Int(el, "MinSkill", "Minskill"),   // barcos: Navegación requerida (Barca 40 / Galera 55 / Galeón 75)
                // PUERTAS. `Closed` sale de la clave `abierta` — INVERTIDA (FileIO.bas:981): abierta=1 ⇒ cerrada.
                Int(el, "abierta", "Abierta"),
                Int(el, "Llave", "llave"), Int(el, "Clave", "clave"),
                Int(el, "IndexAbierta", "Indexabierta"), Int(el, "IndexCerrada", "Indexcerrada"),
                Int(el, "IndexCerradaLlave", "Indexcerradallave"),
                Int(el, "Agarrable", "agarrable"),
                Int(el, "NoSeCae", "nosecae"),   // ItemSeCae: si es 1 el ítem no se cae al morir
                Int(el, "Proyectil", "proyectil"), Int(el, "Municiones", "municiones"),   // arco / munición
                Int(el, "CuantoAumento", "cuantoaumento"),   // cofre de créditos (SubTipo 13)
                Int(el, "ResistenciaMagica", "resistenciamagica"),
                Int(el, "EfectoMagico", "efectomagico", "Efectomagico"),
                Int(el, "HechizoIndex", "hechizoindex"),   // pergamino: hechizo que enseña
                Int(el, "Lingoteindex", "LingoteIndex"),   // mineral crudo: lingote que produce al fundir
                Int(el, "Newbie"),                         // ítem de novato: SalePrice paga 0 por él
                Int(el, "Aura", "aura"),                   // brillo del ítem equipado (AuraToChar)
                Int(el, "Apuñala", "apuñala"),             // arma que sirve para apuñalar (13 objetos)
                Int(el, "Crucial", "crucial"),             // stock infinito en el mercader (casing mezclado en el .dat)
                Int(el, "SND1", "Snd1"),                   // sonido del instrumento (la clave del .dat va en MAYÚSCULAS)
                Int(el, "SND2", "Snd2"),                   // munición: el FX que deja la flecha al acertar
                Int(el, "SndEspecial", "SNDESPECIAL", "sndespecial"),   // sonido al equipar un ítem con aura
                Int(el, "QueSkill", "Queskill", "queskill"),             // skill que sube el ítem equipado
                Int(el, "LanzaHechizo", "Lanzahechizo", "lanzahechizo"),   // poción SubTipo 5: hechizo que lanza
                Int(el, "QueAtributo", "QUEATRIBUTO", "queatributo"),      // atributo que sube el ítem equipado
                Int(el, "MineralIndex", "MINERALINDEX", "mineralindex"),   // mineral que da este yacimiento
                // El pasaje: las claves del .dat son `Desde`/`Map`/`X`/`Y`, no los nombres del campo.
                Int(el, "Desde", "desde"), Int(el, "Map", "map", "MAP"), Int(el, "X", "x"), Int(el, "Y", "y"),
                Int(el, "Destruir", "DESTRUIR", "destruir") == 1,          // no se puede tirar al piso
                StaDeGolpe(Int(el, "StaRequerido", "STAREQUERIDO", "starequerido")));
        }

        return map;
    }

    /// <summary>Los dos pases que vende el pirata de Rinkel (NPC 116), y el mapa donde ese pirata está
    /// parado. Ver <see cref="ArreglarPasesDeRinkel"/>.</summary>
    private static readonly int[] PasesDeRinkel = { 595, 1053 };
    private const int PuertoDeRinkel = 99;
    private const int TipoPasaje = 36;   // eOBJType.otPasajes (Declares.bas:614)

    /// <summary>
    /// REGLA DE LA CASA <c>PasesDeRinkel</c>: hace usables los dos pases del pirata de Rinkel.
    ///
    /// <para>En la data del install los dos traen <c>Desde=20</c> (la ciudad) y el único transportador del
    /// lugar está en el 99 (el puerto). Como usar un pase pide estar en su mapa de origen Y al lado de un
    /// transportador (VB6 InvUsuario.bas:1404), esos dos no se pueden usar en ningún lado: el pirata los
    /// cobra y nunca los honra. Pasa igual en el VB6 — es la data, no el port.</para>
    ///
    /// <para>NO se compara contra el 20 sino contra "el mapa donde está su vendedor": si la data se arregla
    /// del otro lado —moviendo el spawn del pirata a la ciudad, que es adonde llegan los otros nueve pases
    /// "a Rinkel"— esto no la vuelve a romper, se apaga solo. El destino no se toca nunca.</para>
    /// </summary>
    public void ArreglarPasesDeRinkel()
    {
        var mapasDelPirata = new HashSet<int>();
        foreach (var (mapa, spawns) in MapSpawns)
            foreach (var s in spawns)
                if (Npcs.TryGetValue(s.NpcIndex, out var n) && n.NpcType == 7
                    && n.StockItems is { } stock && stock.Any(it => PasesDeRinkel.Contains(it.ObjIndex)))
                    mapasDelPirata.Add(mapa);
        if (mapasDelPirata.Count != 1) return;   // no está poblado, o está en varios: no se toca
        int donde = mapasDelPirata.First();

        var objetos = new Dictionary<int, ObjInfo>(Objects);
        foreach (var idx in PasesDeRinkel)
            if (objetos.TryGetValue(idx, out var pase) && pase.ObjType == TipoPasaje
                && pase.PasajeDesdeMap != donde)
                objetos[idx] = pase with { PasajeDesdeMap = donde };
        Objects = objetos;
    }

    /// <summary>Loads a crafting recipe list ({ "&lt;section&gt;": [ { _index, Index } ] }) into a set
    /// of produced object indices.</summary>
    private static HashSet<int> LoadCraftIndices(string file, string section)
    {
        var set = new HashSet<int>();
        if (!File.Exists(file)) return set;
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        if (!doc.RootElement.TryGetProperty(section, out var arr) || arr.ValueKind != JsonValueKind.Array) return set;
        foreach (var el in arr.EnumerateArray())
        {
            int idx = Int(el, "Index");
            if (idx > 0) set.Add(idx);
        }
        return set;
    }

    /// <summary>Lista de recetas de ObjCarpintero.dat / ObjSastre.dat / ObjDruida.dat: una sección `[OBJn]` por
    /// receta, cada una con un `Index=` que apunta al ítem de obj.dat que fabrica.
    ///
    /// Antes esto leía `INIT.Index` + `INIT._duplicates.Index`, o sea que **estaba escrito alrededor de un bug
    /// del transcoder**: esos .dat comentan el header (`[OBJ1]'Flecha`), el parser exigía que la línea terminara
    /// en `]`, no abría sección, y los `Index=` terminaban todos apilados dentro de `[INIT]` — el primero como
    /// clave y el resto como `_duplicates`. La lista salía bien de casualidad, cosechando la contaminación.
    /// Arreglado el transcoder, la forma correcta es el array `OBJ`.</summary>
    /// <summary>
    /// Lee <c>DropGlobal.json</c>: una sección <c>[DROPn]</c> por ítem, con <c>Index</c> (el objeto) y
    /// <c>PorcentajeDrop</c> (el peso).
    ///
    /// <para>Dos cosas del archivo que hay que respetar. Una: los valores traen comentarios VB6 pegados
    /// —<c>Index=992 'RM +10</c>, <c>PorcentajeDrop=-1 'le pongo -1...</c>— y por eso se leen con
    /// <see cref="Int"/>, que corta en el apóstrofo; con un <c>int.Parse</c> pelado las cuatro últimas
    /// entradas se caían. Dos: el <c>NumDrops=30</c> del encabezado no manda, mandan las secciones que
    /// realmente están.</para>
    ///
    /// <para>Se descartan acá las de peso &lt;= 0, que es como el autor apagó cuatro (ver
    /// <see cref="DropGlobalItem"/>). Descartarlas en el CARGADOR y no en el sorteo es a propósito: así una
    /// entrada apagada no puede colarse por un camino nuevo que se agregue después.</para>
    /// </summary>
    private static IReadOnlyList<DropGlobalItem> LoadDropsGlobales(string file)
    {
        var lista = new List<DropGlobalItem>();
        if (!File.Exists(file)) return lista;
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        if (!doc.RootElement.TryGetProperty("DROP", out var arr) || arr.ValueKind != JsonValueKind.Array) return lista;
        foreach (var el in arr.EnumerateArray())
        {
            int obj = Int(el, "Index"), peso = Int(el, "PorcentajeDrop");
            if (obj > 0 && peso > 0) lista.Add(new DropGlobalItem(obj, peso));
        }
        return lista;
    }

    private static HashSet<int> LoadRecipeIndexList(string file)
    {
        var set = new HashSet<int>();
        if (!File.Exists(file)) return set;
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        if (!doc.RootElement.TryGetProperty("OBJ", out var arr) || arr.ValueKind != JsonValueKind.Array) return set;
        foreach (var el in arr.EnumerateArray())
            if (el.TryGetProperty("Index", out var v) && v.ValueKind == JsonValueKind.String
                && int.TryParse(v.GetString(), out var i)) set.Add(i);
        return set;
    }

    /// <summary>Los hechizos que sabe tirar un NPC: la clave <c>LanzaSpells</c> dice CUÁNTOS y
    /// <c>Sp1</c>..<c>SpN</c> cuáles (VB6 MODULO_NPCs.bas). Devuelve null si no lanza ninguno, que es el caso

    /// <summary>Las criaturas de un Entrenador: `NroCriaturas` y los `CN1..CNn` con el NOMBRE de cada una
    /// (VB6 MODULO_NPCs.bas, `tCriaturasEntrenador`). El paquete `TrainerCreatureList` manda los nombres, no
    /// los indices: los `CI1..CIn` son para el spawn, que resuelve por indice.</summary>
    /// <summary>Los `CI1..CIn` del Entrenador: el indice de plantilla de cada criatura que puede spawnear.</summary>
    private static IReadOnlyList<int>? LeerIndicesDelEntrenador(JsonElement el)
    {
        int n = Int(el, "NroCriaturas");
        if (n <= 0) return null;
        var ids = new List<int>(n);
        for (int i = 1; i <= n; i++) ids.Add(Int(el, $"CI{i}"));
        return ids;
    }

    private static IReadOnlyList<string>? LeerCriaturasDelEntrenador(JsonElement el)
    {
        int n = Int(el, "NroCriaturas");
        if (n <= 0) return null;
        var nombres = new List<string>(n);
        for (int i = 1; i <= n; i++)
        {
            string nombre = Str(el, $"CN{i}");
            if (!string.IsNullOrWhiteSpace(nombre)) nombres.Add(nombre);
        }
        return nombres.Count > 0 ? nombres : null;
    }

    /// de 221 de los 291 NPCs.</summary>
    private static IReadOnlyList<int>? LeerHechizosDeNpc(JsonElement el)
    {
        int cuantos = Int(el, "LanzaSpells");
        if (cuantos <= 0) return null;
        var lista = new List<int>(cuantos);
        for (int i = 1; i <= cuantos; i++)
        {
            int idx = Int(el, $"Sp{i}");
            if (idx > 0) lista.Add(idx);
        }
        return lista.Count > 0 ? lista : null;
    }

    private static Dictionary<int, NpcInfo> LoadNpcs(string file)
    {
        var map = new Dictionary<int, NpcInfo>();
        if (!File.Exists(file)) return map;
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        if (!doc.RootElement.TryGetProperty("NPC", out var arr) || arr.ValueKind != JsonValueKind.Array) return map;
        foreach (var el in arr.EnumerateArray())
        {
            int index = el.TryGetProperty("_index", out var ix) ? ix.GetInt32() : 0;
            if (index <= 0) continue;
            map[index] = new NpcInfo(
                (short)index, Str(el, "Name"),
                (short)Int(el, "Body"), (short)Int(el, "Head"), (byte)Math.Clamp(Int(el, "Heading"), 1, 4),
                MaxOr(Int(el, "MaxHP"), 1), Int(el, "MinHIT"), Int(el, "MaxHIT"),
                Int(el, "DEF", "Def"), Int(el, "GiveEXP"), Int(el, "GiveGLD"), Int(el, "PoderEvasion"), Int(el, "PoderAtaque"),
                ParseDrops(Str(el, "RandomDrop")), Str(el, "Desc"),
                Hostile: Int(el, "Hostile") == 1, Attackable: Int(el, "Attackable") == 1, Movement: Int(el, "Movement"),
                PosOrig: Int(el, "PosOrig") == 1,
                Domable: Int(el, "Domable"),
                NpcType: Int(el, "NpcType"),
                StockItems: Int(el, "Comercia") == 1 ? ParseStock(el) : null,
                TipoItems: Int(el, "TipoItems"),
                Inmunidad: Int(el, "Inmunidad"),
                Veneno: Int(el, "Veneno"),
                AguaValida: Int(el, "AguaValida") == 1, TierraInvalida: Int(el, "TierraInvalida") == 1,
                // `If Not .Leveles > 0 Then .Leveles = 100` (MODULO_NPCs.bas:973): sin la clave "Nivel" el
                // original NO deja el 0, lo sube a 100 — y en esta data no la trae NINGUNO de los 291.
                Nivel: MaxOr(Int(el, "Nivel"), 100),
                Status: Int(el, "Status"),
                Spells: LeerHechizosDeNpc(el),
                WeaponAnim: Int(el, "WeaponAnim"), ShieldAnim: Int(el, "ShieldAnim"),
                HelmetAnim: Int(el, "CascoAnim"),   // el .dat la llama CascoAnim, el paquete Casco
                TrainerCreatures: LeerCriaturasDelEntrenador(el),
                TrainerCreatureIndexes: LeerIndicesDelEntrenador(el),
                // Las dos grafías: `SND1` en 50 fichas del install y `Snd1` en 3. Con una sola, 3 criaturas
                // quedaban mudas sin que nada avisara.
                // Las dos grafías —`SND1` en 50 fichas del install y `Snd1` en 3 (los tres Lobos y el Demonio
                // Abisal)— se nombran para que se vean, pero el que las resuelve es `Candidatos`, que compara
                // sin distinguir mayúsculas. O sea que acá el segundo nombre es documentación, no mecanismo:
                // sacarlo NO rompe nada, y por eso ningún test puede fijarlo. Lo que sí hay que cuidar es que
                // `Candidatos` conserve ese barrido — es lo que evita la trampa que en `obj.dat` nos costó 92
                // precios y un hechizo de área.
                Snd1: Int(el, "SND1", "Snd1"), Snd2: Int(el, "SND2", "Snd2"), Snd3: Int(el, "SND3", "Snd3"));
        }
        return map;
    }

    private static int MaxOr(int v, int fallback) => v > 0 ? v : fallback;

    /// <summary>Carga MapTerrain.json — {"&lt;map&gt;": {"w": base64, "b": base64}} — con los bitmaps de agua y
    /// bloqueos por mapa (100x100 bits, 1250 bytes cada uno).</summary>
    private static Dictionary<int, (byte[] Water, byte[] Blocked)> LoadTerrain(string file)
    {
        var map = new Dictionary<int, (byte[], byte[])>();
        if (!File.Exists(file)) return map;
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        foreach (var m in doc.RootElement.EnumerateObject())
        {
            if (!int.TryParse(m.Name, out var num)) continue;
            var w = m.Value.TryGetProperty("w", out var we) ? Convert.FromBase64String(we.GetString() ?? "") : Array.Empty<byte>();
            var b = m.Value.TryGetProperty("b", out var be) ? Convert.FromBase64String(be.GetString() ?? "") : Array.Empty<byte>();
            map[num] = (w, b);
        }
        return map;
    }

    /// <summary>Carga MapTriggers.json — {"&lt;map&gt;": {"arena": [[x,y],…], "segura": […], "posinvalida": […]}} —
    /// con los tiles de trigger por mapa (ZONAPELEA / ZONASEGURA / POSINVALIDA).</summary>
    private static void LoadTriggers(GameData data, string file)
    {
        if (!File.Exists(file)) return;
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (!int.TryParse(prop.Name, out var mapNum) || prop.Value.ValueKind != JsonValueKind.Array) continue;
            var tiles = new Dictionary<int, byte>();
            foreach (var t in prop.Value.EnumerateArray())
            {
                if (t.ValueKind != JsonValueKind.Array || t.GetArrayLength() < 3) continue;
                int x = t[0].GetInt32(), y = t[1].GetInt32(), trig = t[2].GetInt32();
                if (x is >= 1 and <= 100 && y is >= 1 and <= 100 && trig is > 0 and <= 255)
                    tiles[(y << 8) | x] = (byte)trig;
            }
            if (tiles.Count > 0) data._triggers[mapNum] = tiles;
        }
    }

    /// <summary>Loads MapSpawns.json — {"&lt;map&gt;": [[x,y,npcIndex], ...]} — into a lookup by map.</summary>
    private static Dictionary<int, IReadOnlyList<MapNpcSpawn>> LoadMapSpawns(string file)
    {
        var map = new Dictionary<int, IReadOnlyList<MapNpcSpawn>>();
        if (!File.Exists(file)) return map;
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (!int.TryParse(prop.Name, out var mapNum) || prop.Value.ValueKind != JsonValueKind.Array) continue;
            var list = new List<MapNpcSpawn>();
            foreach (var tuple in prop.Value.EnumerateArray())
            {
                if (tuple.ValueKind != JsonValueKind.Array || tuple.GetArrayLength() < 3) continue;
                list.Add(new MapNpcSpawn(tuple[0].GetInt32(), tuple[1].GetInt32(), tuple[2].GetInt32()));
            }
            map[mapNum] = list;
        }
        return map;
    }

    /// <summary>Carga MapObjects.json — {"&lt;mapa&gt;": [[x,y,objIndex,amount], ...]} — los objetos que el .csm trae
    /// colocados en el mundo (VB6 FileIO.bas:1326, que los vuelca a MapData(m,x,y).ObjInfo). El mapa 1 trae 176:
    /// 137 árboles, 17 puertas, 10 carteles, muebles y un teleport.</summary>
    private static Dictionary<int, IReadOnlyList<MapObjectPlacement>> LoadMapObjects(string file)
    {
        var map = new Dictionary<int, IReadOnlyList<MapObjectPlacement>>();
        if (!File.Exists(file)) return map;
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (!int.TryParse(prop.Name, out var mapNum) || prop.Value.ValueKind != JsonValueKind.Array) continue;
            var list = new List<MapObjectPlacement>();
            foreach (var t in prop.Value.EnumerateArray())
            {
                if (t.ValueKind != JsonValueKind.Array || t.GetArrayLength() < 4) continue;
                list.Add(new MapObjectPlacement(t[0].GetInt32(), t[1].GetInt32(), t[2].GetInt32(), t[3].GetInt32()));
            }
            map[mapNum] = list;
        }
        return map;
    }

    /// <summary>Loads MapExits.json — {"&lt;map&gt;": [[x,y,destMap,destX,destY], ...]} — into a (map,x,y)→dest lookup.</summary>
    private static Dictionary<(int, int, int), (int, int, int)> LoadMapExits(string file)
    {
        var map = new Dictionary<(int, int, int), (int, int, int)>();
        if (!File.Exists(file)) return map;
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (!int.TryParse(prop.Name, out var mapNum) || prop.Value.ValueKind != JsonValueKind.Array) continue;
            foreach (var t in prop.Value.EnumerateArray())
            {
                if (t.ValueKind != JsonValueKind.Array || t.GetArrayLength() < 5) continue;
                map[(mapNum, t[0].GetInt32(), t[1].GetInt32())] = (t[2].GetInt32(), t[3].GetInt32(), t[4].GetInt32());
            }
        }
        return map;
    }

    private static Dictionary<int, SpellInfo> LoadSpells(string file)
    {
        var map = new Dictionary<int, SpellInfo>();
        if (!File.Exists(file)) return map;
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        if (!doc.RootElement.TryGetProperty("HECHIZO", out var arr) || arr.ValueKind != JsonValueKind.Array) return map;
        foreach (var el in arr.EnumerateArray())
        {
            int index = el.TryGetProperty("_index", out var ix) ? ix.GetInt32() : 0;
            if (index <= 0) continue;
            map[index] = new SpellInfo(
                (short)index, Str(el, "Nombre"),
                Int(el, "SubeHP"), Int(el, "MinHP"), Int(el, "MaxHP"),
                Int(el, "ManaRequerido"), Int(el, "Target"),
                Int(el, "Paraliza"), Int(el, "RemoverParalisis"), Int(el, "Envenena"), Int(el, "CuraVeneno"),
                Int(el, "Sanacion"),
                Int(el, "Invoca"), Int(el, "NumNpc"), Int(el, "Cant"),
                Int(el, "FXgrh", "FXGrh"), Int(el, "Loops"), Int(el, "Ceguera"),
                Int(el, "Particle"), Int(el, "Revivir"),
                Int(el, "MinSkill"), Int(el, "StaRequerido"), Int(el, "Anillo"),
                Int(el, "SubeFU"), Int(el, "MinFU"), Int(el, "MaxFU"),
                Int(el, "SubeAG"), Int(el, "MinAG"), Int(el, "MaxAG"),
                Int(el, "Invisibilidad"), Int(el, "Inmoviliza"), Int(el, "Incinera"),
                Int(el, "HechizoDeArea"), Int(el, "AreaEfecto"), Int(el, "Tipo"),
                Int(el, "WAV", "Wav"), Int(el, "TimeParticula"),
                // En el Hechizos.dat la clave viene TODA EN MINÚSCULA; el lector del original normaliza con
                // UCase$ y por eso allá da igual. Se prueban las tres grafías, como con las otras claves
                // que resultaron tener más de una escritura en este mismo archivo.
                Int(el, "autolanzar", "AutoLanzar", "AUTOLANZAR"),
                Int(el, "ResucitaFamiliar"),
                // Los seis de la metamorfosis. En este archivo vienen con UNA sola grafía cada uno
                // (`metamorfosis`, `body`, `head` y `asusta` en minúscula; `ExtraHIT`/`ExtraDEF` en mixta) y
                // sólo en los once hechizos del bloque — verificado sobre los 113.
                Int(el, "metamorfosis"), Int(el, "body"), Int(el, "head"),
                Int(el, "ExtraHIT"), Int(el, "ExtraDEF"), Int(el, "asusta"));
        }
        return map;
    }

    private static Dictionary<int, long> LoadLevels(string file)
    {
        // Niveles.json shape: { "meta": { "INIT": { "Nivel1": "300", "Nivel2": "450", ... } } }
        var map = new Dictionary<int, long>();
        if (!File.Exists(file)) return map;
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        if (!doc.RootElement.TryGetProperty("meta", out var meta) ||
            !meta.TryGetProperty("INIT", out var init)) return map;
        foreach (var prop in init.EnumerateObject())
        {
            if (prop.Name.StartsWith("Nivel", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(prop.Name.AsSpan(5), out var lvl)
                && long.TryParse(prop.Value.GetString(), out var exp))
                map[lvl] = exp;
        }
        return map;
    }

    // Class enum (Declares.bas eClass), keyed by ASCII-normalized name so the 'ñ' encoding is irrelevant.
    private static readonly Dictionary<string, int> ClassId = new()
    {
        ["clerigo"] = 1, ["mago"] = 2, ["guerrero"] = 3, ["asesino"] = 4, ["ladron"] = 5, ["bardo"] = 6,
        ["druida"] = 7, ["gladiador"] = 8, ["paladin"] = 9, ["cazador"] = 10, ["pescador"] = 11,
        ["herrero"] = 12, ["leador"] = 13, ["minero"] = 14, ["carpintero"] = 15, ["sastre"] = 16,
        ["mercenario"] = 17, ["nigromante"] = 18,
    };

    private static string Norm(string s)
    {
        var chars = s.Where(c => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z')).Select(char.ToLowerInvariant);
        return new string(chars.ToArray());
    }

    // Loads every MOD* class-modifier section into a map keyed by ASCII-normalized section name,
    // each value a (classId -> modifier) dict. Callers pick the sections they need.
    private static Dictionary<string, Dictionary<int, double>> LoadClassMods(string file)
    {
        var result = new Dictionary<string, Dictionary<int, double>>();
        if (!File.Exists(file)) return result;
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        if (!doc.RootElement.TryGetProperty("meta", out var meta)) return result;
        foreach (var section in meta.EnumerateObject())
        {
            var byClass = new Dictionary<int, double>();
            foreach (var cls in section.Value.EnumerateObject())
                if (ClassId.TryGetValue(Norm(cls.Name), out var id)
                    && double.TryParse(cls.Value.GetString(), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var v))
                    byClass[id] = v;
            if (byClass.Count > 0) result[Norm(section.Name)] = byClass;
        }
        return result;
    }

    /// <summary>Busca una clave del .dat transcodeado <b>ignorando mayúsculas</b>, que es lo que hace el
    /// original: <c>clsIniManager</c> guarda la clave con <c>UCase$</c> (clsIniManager.cls:203) y la busca con
    /// <c>UCase$</c> (:370), o sea que para el VB6 <c>Valor</c>, <c>valor</c> y <c>VALOR</c> son la misma.
    ///
    /// Acá se comparaba string exacto contra una lista de alias escrita A MANO, y esa lista se fue parcheando
    /// de a un campo por vez, cada vez que un bug delataba el que faltaba. El barrido de <c>obj.dat</c> mostró
    /// el tamaño real del problema: <b>22 de sus 98 claves aparecen con más de una grafía</b>, y lo que se
    /// perdía no era cosmético — 92 objetos se quedaban sin PRECIO porque la fuente escribe <c>valor</c>
    /// (entre ellos la Piel de Lobo, que es lo que vende un cazador, y el Manual de Liderazgo de 20 millones);
    /// los 4 Nudillos sin <c>MINHIT</c>/<c>MAXHIT</c>, o sea pegando 0; 11 prendas sin <c>SKSastreria</c>.
    ///
    /// Se prueba primero la coincidencia exacta —es el camino rápido y el de siempre— y sólo si ninguna pega
    /// se recorre el elemento comparando sin distinguir mayúsculas. Los alias explícitos de los call sites se
    /// dejan: ya no hacen falta para el case, pero documentan qué grafías tiene la fuente.</summary>
    /// Devuelve los valores candidatos EN ORDEN: primero las coincidencias exactas (el camino de siempre) y
    /// después las que sólo difieren en mayúsculas. Se devuelven todos y no el primero porque el `Int` de abajo
    /// sigue probando cuando un valor no parsea, y ese comportamiento no cambia.
    private static IEnumerable<string> Candidatos(JsonElement el, string[] keys)
    {
        foreach (var key in keys)
            if (el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
                yield return v.GetString()!;
        foreach (var prop in el.EnumerateObject())
        {
            if (prop.Value.ValueKind != JsonValueKind.String) continue;
            foreach (var key in keys)
                if (prop.Name != key && string.Equals(prop.Name, key, StringComparison.OrdinalIgnoreCase))
                    yield return prop.Value.GetString()!;
        }
    }

    private static string Str(JsonElement el, params string[] keys)
    {
        foreach (var s in Candidatos(el, keys)) return s;
        return "";
    }

    private static int Int(JsonElement el, params string[] keys)
    {
        foreach (var s in Candidatos(el, keys))
        {
            if (int.TryParse(s, out var n)) return n;
            // VB6 INI values may carry a trailing ' comment (e.g. "31254 '878 ..."). Take the leading int.
            if (LeadingInt(s) is int m) return m;
        }
        return 0;
    }

    /// <summary>La energía que cuesta un golpe con este objeto. El cargador del original hace
    /// <c>If ObjData(Object).StaRequerido &lt; 1 Then ObjData(Object).StaRequerido = 10</c> (FileIO.bas:999):
    /// el que no trae la clave —o la trae en 0— cuesta 10, no gratis. Va acá y no en el sitio de uso porque
    /// en el original es una regla del CARGADOR: vale para todos los objetos, no sólo para el arma en la mano.</summary>
    private static int StaDeGolpe(int leido) => leido < 1 ? 10 : leido;

    /// <summary>Parses a RandomDrop field "objIndex,amount,chance" (e.g. "737,1,0.5"). Missing → zeros.</summary>
    /// <summary>Stock de un mercader (NPCs.dat, NPC con Comercia=1): claves <c>Obj1..ObjN</c> con el formato
    /// <c>"objIndex-cantidad"</c>. Se leen hasta <c>NROITEMS</c> (y se tolera el case irregular del .dat).</summary>
    private static IReadOnlyList<(int ObjIndex, int Amount)> ParseStock(JsonElement el)
    {
        var list = new List<(int, int)>();
        int n = Int(el, "NROITEMS");
        for (int i = 1; i <= Math.Max(n, 25); i++)
        {
            var raw = Str(el, $"Obj{i}", $"obj{i}", $"OBj{i}");
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var parts = raw.Split('-');
            if (parts.Length < 2) continue;
            if (!int.TryParse(parts[0].Trim(), out var obj) || obj <= 0) continue;
            if (!int.TryParse(parts[1].Trim(), out var amt) || amt <= 0) continue;
            list.Add((obj, amt));
        }
        return list;
    }

    /// <summary>
    /// Parsea el campo <c>RandomDrop</c> de NPCs.dat: una o varias entradas <c>"objIndex,amount,probTirar"</c>
    /// separadas por <c>'-'</c> (VB6 MODULO_NPCs.bas:1027, que cuenta los '-' y arma el inventario del NPC con
    /// N+1 ítems). Ej: <c>"414,1,100-976,1,0.8-578,1,1.5"</c> = 3 drops. Antes se partía sólo por ',' y todo lo
    /// que venía después del primer '-' se comía la chance: los 38 NPCs multidrop (bosses incluidos) no
    /// dropeaban NADA.
    /// </summary>
    /// <summary>
    /// Kit inicial por clase, de NACIMIENTO.json (VB6 <c>LoadObjNacimiento</c>, General.bas:2040). El JSON trae
    /// las secciones del .ini bajo <c>meta</c>: <c>INIT.Clases</c> y una sección por clase con
    /// <c>CantidadObj</c> + <c>ObjIndexN</c>/<c>ObjAmountN</c>/<c>ObjEquippedN</c>. Los índices vienen con el
    /// comentario del .dat pegado (<c>"573 'Manzana"</c>), así que se lee el número inicial igual que el
    /// <c>val()</c> del VB6. Las entradas con índice 0 se conservan como huecos: el slot 4 es la armadura
    /// racial, que el VB6 rellena por raza y no desde el .dat.
    /// </summary>
    private static IReadOnlyDictionary<int, IReadOnlyList<NewbieItem>> LoadNewbieKits(string file)
    {
        var map = new Dictionary<int, IReadOnlyList<NewbieItem>>();
        if (!File.Exists(file)) return map;
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        if (!doc.RootElement.TryGetProperty("meta", out var meta) || meta.ValueKind != JsonValueKind.Object) return map;
        foreach (var section in meta.EnumerateObject())
        {
            if (!int.TryParse(section.Name, out var klass) || klass <= 0) continue;   // salta "INIT"
            int count = Int(section.Value, "CantidadObj");
            if (count <= 0) continue;
            var items = new List<NewbieItem>(count);
            for (int i = 1; i <= count; i++)
            {
                int obj = LeadingInt(Str(section.Value, $"ObjIndex{i}")) ?? 0;
                int amt = LeadingInt(Str(section.Value, $"ObjAmount{i}")) ?? 0;
                bool eq = (LeadingInt(Str(section.Value, $"ObjEquipped{i}")) ?? 0) == 1;
                items.Add(new NewbieItem(obj, Math.Max(0, amt), eq));
            }
            map[klass] = items;
        }
        return map;
    }

    private static IReadOnlyList<NpcDrop> ParseDrops(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return Array.Empty<NpcDrop>();
        var list = new List<NpcDrop>();
        foreach (var entry in s.Split('-', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = entry.Split(',');
            if (parts.Length < 2) continue;
            if (!int.TryParse(parts[0].Trim(), out var obj) || obj <= 0) continue;
            if (!int.TryParse(parts[1].Trim(), out var amt) || amt <= 0) continue;
            double prob = parts.Length > 2 && double.TryParse(parts[2].Trim(),
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var c) ? c : 0;
            list.Add(new NpcDrop(obj, amt, prob));
        }
        return list;
    }

    private static int? LeadingInt(string? s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        int i = 0, n = s.Length;
        while (i < n && char.IsWhiteSpace(s[i])) i++;
        int start = i;
        if (i < n && (s[i] == '-' || s[i] == '+')) i++;
        int digits = i;
        while (i < n && char.IsDigit(s[i])) i++;
        return i > digits && int.TryParse(s.AsSpan(start, i - start), out var val) ? val : null;
    }

    private static string? FindDataDir()
    {
        // La data del juego se llama SIEMPRE "gamedata": al lado del ejecutable en la distribución, y en la
        // raíz del repo cuando se corre desde el árbol de desarrollo. Un solo nombre y una sola búsqueda.
        // Se sube desde el directorio del ejecutable y desde el directorio de trabajo, porque el .exe puede
        // estar en bin/Release/... y el cwd en cualquier lado.
        foreach (var root in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var dir = new DirectoryInfo(root);
            for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "gamedata");
                if (Directory.Exists(candidate)) return candidate;
            }
        }
        return null;
    }
}
