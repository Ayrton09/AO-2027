using System.Linq;
using CoverAO.Server.Data;
using CoverAO.Server.Net;

namespace CoverAO.Server.Game;

/// <summary>
/// El FIXTURE de test: la población y el terreno que los flows de paridad esperan en el mapa 15 (comerciante,
/// banquero, sacerdote, enlistadores, bancos de trabajo, entrenador, criaturas PvE, arena, zona segura, corral).
///
/// Es <b>OPT-IN</b>: sólo se puebla con <c>--fixture</c> (o <c>COVERAO_FIXTURE=1</c>). En PRODUCCIÓN el mapa 15 es
/// un mapa real del mundo (sus 28 criaturas del .csm, poblado por <see cref="WorldPopulator"/>) y nada más. Sin
/// esto, un jugador que llega caminando desde el mapa 13 —339 tile-exits llevan al 15— se topaba con Rugura,
/// Merlin y los NPCs de test parados en medio del mundo. El seed del `cuenta_test` (Persistence.SeedDemoIfEmpty)
/// se gatea con el mismo flag: sin fixture no hay cuenta demo, como en un server de verdad.
///
/// Vive acá y no suelto en Program.cs por lo mismo que WorldPopulator: el arranque no se puede testear, y una
/// pared o un NPC de test que se filtre a producción no lo cazaría ningún test. <see cref="TestFixtureTests"/>
/// fija que producción deja el mapa 15 limpio.
/// </summary>
public static class TestFixture
{
    /// <summary>El mapa del fixture: 15, un mapa naturalmente ABIERTO (sin paredes en la zona de juego
    /// x40-68/y38-62). El mapa 1 (Ullathorpe) tiene el spawn (50,50) y varios tiles del cluster sobre PARED del
    /// .csm, incompatible con el gate de blocking de HandleWalk. Los flows son agnósticos al mapa (caminan
    /// relativo al spawn en las mismas coords x,y), así que sólo cambia el número de mapa.</summary>
    public const byte FixtureMap = 15;

    // VB6 `TipoAI` (AI_NPC.bas:32), el `Select Case .Movement` de NPCAI. El fixture fija estos a mano porque sus
    // NPCs salen de plantillas genéricas (Aldeano/Murciélago/Lobo) que son Movement=0 — o sea que, con la IA fiel,
    // un "banquero" de test se pondría a caminar hacia el jugador y rompería los flows que lo doble-clickean por
    // coordenada. En la DATA REAL los servicios SON Movement=1, así que esto corrige la infidelidad del fixture.
    private const int VaAlUsuario = 0;   // NpcMaloAtacaUsersBuenos: camina hacia el usuario que vea (IrUsuarioCercano)
    private const int Estatico = 1;      // ESTATICO
    private const int MueveAlAzar = 3;   // MueveAlAzar: deambula. En este install no lo es NINGÚN NPC.

    private const byte SvcRow = 47;      // fila de servicios (no atacables)
    private const byte PveRow = 53;      // fila de criaturas (atacables) — los flows la usan para elegir blanco

    /// <summary>Puebla el mapa 15 con el fixture de test. Sólo se llama en modo <c>--fixture</c>.</summary>
    public static void Populate(World world, NpcSystem npcSystem, GameData gameData)
    {
        // Arena de FIXTURE: el mapa 15 no trae zonas de pelea (ZONAPELEA) propias, así que se marca un recuadro
        // chico (41..45 × 41..45) como arena — lejos del spawn (50,50) y del cluster de NPCs (46..60). Morir dentro
        // auto-revive al instante (VB6 UserDie → RevivirUsuario); morir afuera deja el cadáver (así death_flow
        // sigue probando la muerte persistente). Las arenas REALES de los otros mapas salen de MapArenas.json.
        for (byte ax = 41; ax <= 45; ax++)
            for (byte ay = 41; ay <= 45; ay++)
                gameData.AddArena(FixtureMap, ax, ay);

        // Pared de FIXTURE: el mapa 15 es abierto en la zona de juego (sin pared interior), así que se marca un
        // tile bloqueado en (50,45) —al norte del spawn, camino despejado de NPCs— para que blocking_flow ejerza el
        // gate de blocking contra una pared INTERIOR (el borde del mapa quedaría lejos, a y=7). No es arena.
        gameData.AddBlocked(FixtureMap, 50, 45);

        // CIUDAD DE COMERCIO: el mapa 15 real es "Desierto Rinkel" y trae battle_mode 0, y con ese número los
        // mercaderes del fixture no le venderían a NADIE — la regla del original es que sólo se comercia en los
        // mapas battle_mode 2 y 3 (ver Player.MercaderMeAtiende). Sin esto, los flows de comercio medirían el
        // rechazo en vez del comercio.
        //
        // Y se elige el 2 (el de Lindos) y no el 3 A PROPÓSITO, porque los dos umbrales del original NO son el
        // mismo: en battle_mode 2 el mercader atiende a los imperiales y republicanos —o sea a todo personaje
        // recién creado, que es lo que usan los flows— pero le sigue cerrando la puerta al CAOS y al renegado.
        // Como el fixture tiene un enlistador de la Legión del Caos en (60,47), eso deja el rechazo alcanzable
        // desde un flow: enlistarse y volver al mercader. Con el 3 no habría forma de ejercerlo sin salir del mapa.
        gameData.SetMapBattleMode(FixtureMap, 2);

        // YUNQUE del fixture (obj 384, otYunque) en (44,49): un tile sin tránsito, lejos de la fila de servicios
        // (y=47, ocupada de x=34 a 62 en pares), de la de criaturas (y=53) y de la columna del spawn. Hace falta
        // un yunque REAL en el suelo para ejercer el `Accion` del original: doble clic con el MARTILLO puesto
        // abre la herrería, y sin el martillo no pasa nada. Los bancos de trabajo del fixture son NPCs, que es
        // otro camino (el del `WorkbenchTrade`), así que no servían para probar éste.
        world.SetGround(new WorldPos(FixtureMap, 44, 49), 384, 1, esFijo: true);

        // BUZÓN DE CORREO (obj 1491, otCorreo=48) en (45,49), al lado del yunque. Doble clic manda la bandeja
        // (CorreoList) y abre el formulario 6, que es el ÚNICO camino por el que el original lista el correo
        // (AccionParaCorreo, Acciones.bas:592).
        //
        // Que el objeto se llame "Estandarte de la Armada Real" no es un error: en la data de este install los
        // seis objetos con OBJType=48 son puentes y estandartes, así que en el juego original hacer doble clic
        // en un estandarte te abre el correo. Rareza de la data, no del código — y hay 276 repartidos en 44
        // mapas (4 en Ullathorpe), así que el buzón es perfectamente alcanzable jugando.
        world.SetGround(new WorldPos(FixtureMap, 45, 49), 1491, 1, esFijo: true);

        // FRAGUA del fixture (obj 385, otFragua) en (43,49), del otro lado del yunque. Es la que ejerce fundir:
        // usar un mineral crudo pide objetivo y el clic ACÁ lo convierte en lingotes. Va pegada al yunque a
        // propósito, porque el par fragua+yunque es lo que arma la herrería y así un solo flow recorre los dos.
        world.SetGround(new WorldPos(FixtureMap, 43, 49), 385, 1, esFijo: true);

        // ZONA SEGURA de FIXTURE (47..48 × 51..52): el mapa 15 no trae tiles ZONASEGURA propios. Es un recuadro
        // chico al sudoeste del spawn, accesible por la columna hueca 47. En zona segura NO se puede invocar (VB6
        // HechizoInvocacion) — el único efecto de ZONASEGURA que esta variante realmente aplica (su chequeo en
        // PuedeAtacar está COMENTADO en el original, así que pelear en zona segura SÍ se puede). Las zonas seguras
        // reales de los otros mapas salen de MapTriggers.json.
        for (byte sx = 47; sx <= 48; sx++)
            for (byte sy = 51; sy <= 52; sy++)
                gameData.AddSafeZone(FixtureMap, sx, sy);

        // Paredes del CORRAL de los errantes (perímetro de 55..61 × 35..41; interior libre 56..60 × 36..40). Ver el
        // spawn de los errantes más abajo: encerrados no pueden derivar hasta los carriles y volver flaky a los flows.
        for (byte px = 55; px <= 61; px++) { gameData.AddBlocked(FixtureMap, px, 35); gameData.AddBlocked(FixtureMap, px, 41); }
        for (byte py = 35; py <= 41; py++) { gameData.AddBlocked(FixtureMap, 55, py); gameData.AddBlocked(FixtureMap, 61, py); }

        // ── LAYOUT DEL FIXTURE (compatible con la COLISIÓN de personajes) ────────────────────────────────────────
        // El VB6 no deja pisar un tile con un jugador vivo o un NPC, así que el layout debe dar un camino LIBRE del
        // spawn a cada NPC. Reglas:
        //   · La CRUZ del spawn queda despejada: fila y=50 (autopista este-oeste) y columna x=50 (blocking_flow
        //     camina al norte hasta la pared de (50,45); auto_revive va al oeste por y=50 y sube a la arena).
        //   · Los NPCs viven en dos filas, en columnas PARES espaciadas de a 2 (saltando x=50): y=47 (servicios, no
        //     atacables) e y=53 (criaturas atacables). Así `walk_to(nx-1, ny)` aterriza en la columna IMPAR libre
        //     entre dos NPCs, y la bajada/subida por esa columna está despejada.
        //   · Todo debe entrar en x,y ∈ [36,62]: `World.InView` ve por bloques de 9 (±1), así que fuera de ahí el
        //     NPC no manda CharacterCreate y los flows que lo descubren dinámicamente no lo encuentran.
        //   · Los que se MUEVEN (errantes, hostil que persigue) van lejos de toda autopista/columna, para que no
        //     bloqueen un carril de forma no determinista (flake). El hostil además a >6 (AggroRange) de la autopista.
        var npcTemplate = gameData.Npcs.Values.FirstOrDefault(n => n.MaxHp is > 0 and <= 60 && n.GiveExp > 0);
        if (npcTemplate is not null)
        {
            // Murciélagos PvE: PRIMEROS en spawnear, así el `npcs[0]` de npc_flow/npc_ai_flow (orden de
            // CharacterCreate) cae siempre en uno de ellos y no en el bruiser de 300 HP.
            for (byte i = 0; i < 3; i++)
                npcSystem.AddSpawn(npcTemplate, new WorldPos(FixtureMap, (byte)(52 + i * 2), PveRow), movement: Estatico, posOrig: true);   // (52|54|56, 53)
            // A guaranteed-drop variant: forces RandomDrop to 100% so loot is observable/testable.
            // ProbTirar = 100 es el "dropea siempre" del VB6 (NPC_TIRAR_ITEMS), así el loot es observable/testable.
            var dropSrc = npcTemplate.Drops is { Count: > 0 } ds ? ds[0] : null;
            var dropper = npcTemplate with
            {
                Drops = new[] { new Data.NpcDrop(dropSrc?.ObjIndex > 0 ? dropSrc.ObjIndex : 38, System.Math.Max(1, dropSrc?.Amount ?? 1), 100) },
            };
            npcSystem.AddSpawn(dropper, new WorldPos(FixtureMap, 46, PveRow), movement: Estatico, posOrig: true);
            // A hard-hitting, high-HP stationary NPC so equipment defence vs NPCs is observable (the default bat
            // hits for 1, which armour can't visibly reduce). Used by armor_npc_flow y potion_flow (que necesita 2
            // tiles libres al oeste: (59,53) para Rugura y (58,53) para Merlin).
            var bruiser = npcTemplate with { MinHit = 10, MaxHit = 10, MaxHp = 300, GiveExp = 0 };
            npcSystem.AddSpawn(bruiser, new WorldPos(FixtureMap, 60, PveRow), movement: Estatico, posOrig: true);
            // A frail, high-XP dummy: a single kill grants enough exp to level up, so emit_flow can verify the
            // LevelUp packet deterministically (PvP exp is faithfully low now, so killing players won't).
            // Un HECHICERO estático en (62,53): tira el hechizo 36 ("Hechizo de NPC", SubeHP=2, 30-50 de daño),
            // que es el genérico que el install le da a los Magos. Son 70 los NPCs que lanzan hechizos en el
            // mundo real y NINGUNO de los del fixture lo hacía, así que sin este la rama no se podía ejercer.
            // MinHit/MaxHit en 0: que el daño observable venga sólo de la magia.
            var hechicero = npcTemplate with { MinHit = 0, MaxHit = 0, MaxHp = 300, GiveExp = 0, Spells = new[] { 36 } };
            // hostile: true — `AddSpawn` lo toma por PARÁMETRO, no del NpcInfo, y su default es false: el resto
            // del fixture nace pacífico y sólo devuelve el golpe. Acá se quiere que tire magia sin que lo
            // provoquen, que es lo que hacen 31 de los 70 casters del mundo real.
            npcSystem.AddSpawn(hechicero, new WorldPos(FixtureMap, 62, PveRow), hostile: true, movement: Estatico, posOrig: true);
            // INMUNIDAD A LA PARÁLISIS (NPCs.dat "Inmunidad"). Dos criaturas gemelas y ESTÁTICAS, que sólo se
            // diferencian en ese campo: la de (42,53) se paraliza y la de (40,53) no. Van al fixture y no se
            // invocan con /ACC porque una criatura recién invocada CAMINA — se la vio salirse del tile de
            // enfrente entre el spawn y el casteo, y entonces el hechizo no encontraba objetivo y el control
            // del flow fallaba sin que la inmunidad tuviera nada que ver. Estáticas, la mira no se mueve.
            // OJO LA COLUMNA: la fila y=53 ya esta poblada (42 el lobo domable de pet_flow, 44 el dummy de XP,
            // 46 el dropper, 52|54|56 los murcielagos, 60 el bruiser, 62 el hechicero). La primera version puso
            // esta pareja en la 42 y CHOCO con el lobo: una de las dos no llegaba a spawnear, y el flow medía
            // "no hay objetivo" creyendo que medía la inmunidad. Se usan la 36 y la 38, que estan libres.
            var paralizable = npcTemplate with { MaxHp = 300, GiveExp = 0, Inmunidad = 0 };
            npcSystem.AddSpawn(paralizable, new WorldPos(FixtureMap, 38, PveRow), movement: Estatico, posOrig: true);
            var inmune = npcTemplate with { MaxHp = 300, GiveExp = 0, Inmunidad = 1 };
            npcSystem.AddSpawn(inmune, new WorldPos(FixtureMap, 36, PveRow), movement: Estatico, posOrig: true);
            // VENENOSA (veneno_criatura_flow): clonada del murciélago —pega de a 1, así que uno se puede quedar
            // parado al lado— con `Veneno = 1` y hostil para que ataque sola. Va al final de la fila, después
            // del hechicero: los huecos del medio están sobre el camino que otros flows recorren.
            var venenosa = npcTemplate with { MaxHp = 300, GiveExp = 0, Veneno = 1 };
            npcSystem.AddSpawn(venenosa, new WorldPos(FixtureMap, 64, PveRow), hostile: true, movement: Estatico, posOrig: true);
            // AGRESOR para mascota_ataca_flow: hostil DE NACIMIENTO —pega sin que lo provoquen— y con 300 de
            // vida para que la mascota no lo mate y termine volviendo con el amo. Va una fila más abajo, no en
            // la PvE: parado en (41,53) uno doma al lobo, y ahí al lado no puede haber nada que pegue, o
            // pet_flow se comería mordiscones que no pidió.
            var agresor = npcTemplate with { MaxHp = 300, GiveExp = 0 };
            npcSystem.AddSpawn(agresor, new WorldPos(FixtureMap, 41, PveRow + 2), hostile: true, movement: Estatico, posOrig: true);
            System.Console.WriteLine($"Spawn: paralizable en (38,{PveRow}) e INMUNE en (36,{PveRow}).");

            var xpDummy = npcTemplate with { MaxHp = 20, Def = 0, GiveExp = 100000 };
            npcSystem.AddSpawn(xpDummy, new WorldPos(FixtureMap, 44, PveRow), movement: Estatico, posOrig: true);
            // A TAMEABLE creature for pet_flow: the default bat has no `Domable` in NPCs.dat, and the faithful taming
            // gate (VB6 DoDomar) requires Domable > 0 and <= Carisma * Domar skill. The Wolf (501) has Domable=160.
            var tameable = gameData.Npc(501);
            if (tameable is not null) npcSystem.AddSpawn(tameable, new WorldPos(FixtureMap, 42, PveRow), movement: Estatico, posOrig: true);
            // Errantes, DENTRO DE UN CORRAL (56..60 × 36..40): deambulan ~1 tile/s y, sueltos, derivan hasta los
            // carriles y bloquean el paso de un flow de forma NO determinista (con la colisión activa eso los vuelve
            // flaky — medido: potion_flow fallaba 1 de cada 4 por un errante cruzado). Encerrados siguen moviéndose
            // (wander_flow los ve) pero no pueden salir. El corral entra en el InView del spawn y está lejos de la
            // autopista y=50 y de las dos filas de NPCs; sus paredes no cortan ninguna columna de acceso.
            for (byte i = 0; i < 2; i++)
                npcSystem.AddSpawn(npcTemplate, new WorldPos(FixtureMap, (byte)(57 + i * 2), 38), movement: MueveAlAzar, posOrig: true);
            // Hostil (persigue al que entra en AggroRange=6): al SUR en (58,60), a 10 de la autopista y a 7 del
            // puesto de potion_flow (59,53) — así NADIE lo despierta de paso; sólo aggro_flow baja a buscarlo.
            //
            // NO va en corral, a diferencia de los errantes: encerrarlo le taparía el camino hacia el jugador y
            // dejaría de perseguir, que es justo lo que aggro_flow mide.
            //
            // OJO que el comentario viejo decía "no deambula, se queda quieto hasta que alguien entra en rango".
            // Es FALSO: es Movement=0, y `IrUsuarioCercano` (AI_NPC.bas:361) termina en "A bailar" —un paso al
            // azar 1 de cada 11 ticks— justamente cuando NO ve a nadie. Medido contra el server: a los 15 s de
            // arrancar ya se había corrido un tile y a los 40 s estaba a más de 6. Es fiel al original, pero
            // significa que NINGÚN flow puede darlo por quieto en (58,60): aggro_flow lo busca por ser, de los
            // NPCs al sur de y=56, el más cercano a este tile. El "más cercano" hace falta porque el mapa 15 no
            // es sólo fixture — también trae su población del .csm, y ahí abajo vive una Araña Gigante en
            // (50,61).
            npcSystem.AddSpawn(npcTemplate, new WorldPos(FixtureMap, 58, 60), hostile: true, movement: VaAlUsuario, posOrig: true);
            System.Console.WriteLine($"Spawn: 3x '{npcTemplate.Name}' PvE en y={PveRow} + dropper + bruiser + dummy + lobo; 2 errantes (57|59,38); 1 hostil (58,60).");
        }

        // Servicios en la fila SvcRow (y=47), columnas pares espaciadas de a 2 (ver el layout de arriba).
        var merchantTemplate = gameData.Npcs.Values.FirstOrDefault(n => n.Body > 0);
        if (merchantTemplate is not null)
        {
            // El comerciante general del fixture COMPRA DE TODO: `TipoItems = otCualquiera`, como los 5 NPCs
            // del install que lo traen. La plantilla base sale de `FirstOrDefault(Body > 0)` —un NPC cualquiera,
            // que no comercia y por lo tanto tiene TipoItems = 0—, así que sin este `with` el gate de la venta
            // (Comercio.bas:123) le rechazaría TODO y los flows que venden se caerían por el fixture, no por el
            // código. El mercader especializado de al lado (54,47) es el que ejerce el rechazo.
            world.AddNpc(new Npc
            {
                CharIndex = world.AllocateCharIndex(), Movement = Estatico,
                Template = merchantTemplate with { TipoItems = GameLimits.TipoItemsCualquiera },
                Hp = merchantTemplate.MaxHp,
                Pos = new WorldPos(FixtureMap, 52, SvcRow),
                Stock = new List<MerchantSlot>
                {
                    new() { ObjIndex = 38, Amount = 100 },   // pociones rojas
                    new() { ObjIndex = 2,  Amount = 10 },    // espada larga
                },
            });
            System.Console.WriteLine($"Spawn: comerciante en (52,{SvcRow}) con 2 productos.");

            // MERCADER ESPECIALIZADO: sólo compra ObjType 2 (armas). Existe para ejercer el RECHAZO del gate
            // `TipoItems` —venderle una poción tiene que dar LocaleMsg 148 y dejar el oro intacto—, que es la
            // rama que el port no tenía y que ningún flow podía tocar mientras todos los mercaderes compraban
            // todo.
            //
            // Va en la columna 64, SIGUIENDO la convención de la fila (pares, de a 2). Estuvo un rato en la 50
            // —el único hueco entre 34 y 62— y eso rompió blocking_flow y gm_tanda3_flow: la 50 no está libre,
            // está RESERVADA, porque es el pasillo del spawn (50,50) hacia la pared de (50,45). El comentario
            // del layout ya lo decía ("saltando x=50") y va repetido acá para el que venga a agregar el próximo.
            world.AddNpc(new Npc
            {
                CharIndex = world.AllocateCharIndex(), Movement = Estatico,
                Template = merchantTemplate with { TipoItems = 2 },
                Hp = merchantTemplate.MaxHp,
                Pos = new WorldPos(FixtureMap, 64, SvcRow),
                Stock = new List<MerchantSlot>
                {
                    new() { ObjIndex = 2, Amount = 10 },
                },
            });
            System.Console.WriteLine($"Spawn: mercader de armas (TipoItems=2) en (64,{SvcRow}).");

            world.AddNpc(new Npc
            {
                CharIndex = world.AllocateCharIndex(), Movement = Estatico,
                Template = merchantTemplate,
                Hp = merchantTemplate.MaxHp,
                Pos = new WorldPos(FixtureMap, 48, SvcRow),
                IsBanker = true,
            });
            System.Console.WriteLine($"Spawn: banquero en (48,{SvcRow}).");

            // Revividor (sacerdote): doble-clic revive a un jugador muerto cercano.
            world.AddNpc(new Npc
            {
                CharIndex = world.AllocateCharIndex(), Movement = Estatico,
                Template = merchantTemplate,
                Hp = merchantTemplate.MaxHp,
                Pos = new WorldPos(FixtureMap, 56, SvcRow),
                IsReviver = true,
            });
            System.Console.WriteLine($"Spawn: sacerdote Revividor en (56,{SvcRow}).");

            // VETERINARIA (eNPCType.Veterinarias = 11): doble-clic revive el familiar muerto y cura al herido.
            // La data trae 27 en el mundo pero ninguna en el mapa 15, y `familiar_progreso_flow` no puede
            // cruzar medio mapa para llegar a una.
            //
            // EN 68 Y NO EN 50, y el hueco del 50 no era casualidad: la columna x=50 es el PASILLO que sale
            // del punto de aparicion, y `blocking_flow` camina por ahi al norte para chocar contra la pared
            // de (50,45). Con la veterinaria en (50,47) chocaba contra ELLA cuatro baldosas antes. La fila de
            // servicios esta ocupada de 34 a 66 de a dos; 68 esta libre y fuera de todo carril.
            world.AddNpc(new Npc
            {
                CharIndex = world.AllocateCharIndex(), Movement = Estatico,
                Template = merchantTemplate,
                Hp = merchantTemplate.MaxHp,
                Pos = new WorldPos(FixtureMap, 68, SvcRow),
                IsVeterinaria = true,
            });
            System.Console.WriteLine($"Spawn: veterinaria en (68,{SvcRow}).");

            // TIMBERO (VB6 eNPCType.Timbero = 12): el casino. Se le apuesta oro con `Gamble` y paga el 47% de las
            // veces. La data del install SÍ trae uno, pero está en otro mapa, y el fixture necesita el suyo para
            // que `casino_flow` no dependa de caminar medio mundo.
            world.AddNpc(new Npc
            {
                CharIndex = world.AllocateCharIndex(), Movement = Estatico,
                Template = merchantTemplate with { NpcType = 12 },
                Hp = merchantTemplate.MaxHp,
                // (66,SvcRow) y NO (50,SvcRow): la columna 50 es el PASILLO por el que blocking_flow camina al
                // norte desde el spawn hasta chocar con la pared, y un NPC ahí lo frena antes — el flow se ponía
                // rojo culpando a la pared. Tampoco 54, que es del aldeano de npc_dialogue_flow.
                Pos = new WorldPos(FixtureMap, 66, SvcRow),
            });
            System.Console.WriteLine($"Spawn: timbero en (66,{SvcRow}).");

            // Enlistadores de facción (VB6 eNPCType.facciones): el NPCs.dat de este install no trae ninguno, así que
            // se agregan al fixture. Doble-clic (o Enlist/Reward de frente) para enlistarse o ascender de rango.
            world.AddNpc(new Npc
            {
                CharIndex = world.AllocateCharIndex(), Movement = Estatico, Template = merchantTemplate, Hp = merchantTemplate.MaxHp,
                Pos = new WorldPos(FixtureMap, 58, SvcRow), FactionEnlist = 5,   // Armada Real
            });
            world.AddNpc(new Npc
            {
                CharIndex = world.AllocateCharIndex(), Movement = Estatico, Template = merchantTemplate, Hp = merchantTemplate.MaxHp,
                Pos = new WorldPos(FixtureMap, 60, SvcRow), FactionEnlist = 4,   // Legión del Caos
            });
            world.AddNpc(new Npc
            {
                CharIndex = world.AllocateCharIndex(), Movement = Estatico, Template = merchantTemplate, Hp = merchantTemplate.MaxHp,
                Pos = new WorldPos(FixtureMap, 38, SvcRow), FactionEnlist = 6,   // Milicia Republicana
            });
            // CONVERTIDORES (VB6 eNPCType.Convertidores): le dan la ciudadanía a un renegado — el imperial abre la
            // rama Ciudadano→Armada y el republicano la rama Republicano→Milicia. El NPCs.dat de este install no
            // trae ninguno (no hay NpcType=5 ni Convertidores), así que van al fixture como los enlistadores.
            world.AddNpc(new Npc
            {
                CharIndex = world.AllocateCharIndex(), Movement = Estatico, Template = merchantTemplate, Hp = merchantTemplate.MaxHp,
                Pos = new WorldPos(FixtureMap, 34, SvcRow), FactionConvert = Player.StatusCiudadano,   // imperial
            });
            world.AddNpc(new Npc
            {
                CharIndex = world.AllocateCharIndex(), Movement = Estatico, Template = merchantTemplate, Hp = merchantTemplate.MaxHp,
                Pos = new WorldPos(FixtureMap, 36, SvcRow), FactionConvert = Player.StatusRepublicano,
            });
            System.Console.WriteLine($"Spawn: enlistadores Armada (58,{SvcRow}) / Caos (60,{SvcRow}) / Milicia (38,{SvcRow}) + " +
                              $"convertidores imperial (34,{SvcRow}) y republicano (36,{SvcRow}).");

            // Aldeano con diálogo (Desc) — doble-clic para hablarle.
            var greeter = gameData.Npc(1);
            if (greeter is not null)
                world.AddNpc(new Npc
                {
                    CharIndex = world.AllocateCharIndex(), Movement = Estatico, Template = greeter, Hp = greeter.MaxHp,
                    Pos = new WorldPos(FixtureMap, 54, SvcRow),
                });

            // Workbench NPCs: double-clicking one sends that trade's craftable list to the client.
            byte wx = 40;
            foreach (var trade in new[] { "blacksmith", "carpenter", "tailor", "alchemy" })
            {
                world.AddNpc(new Npc
                {
                    CharIndex = world.AllocateCharIndex(), Movement = Estatico,
                    Template = merchantTemplate,
                    Hp = merchantTemplate.MaxHp,
                    Pos = new WorldPos(FixtureMap, wx, SvcRow),
                    WorkbenchTrade = trade,
                });
                wx += 2;
            }
            System.Console.WriteLine($"Spawn: 4 bancos de trabajo (herrero/carpintero/sastre/alquimista) en (40|42|44|46,{SvcRow}).");

            // A trainer NPC (stable): double-clicking it lists the creatures it can train.
            world.AddNpc(new Npc
            {
                CharIndex = world.AllocateCharIndex(), Movement = Estatico,
                Template = merchantTemplate,
                Hp = merchantTemplate.MaxHp,
                Pos = new WorldPos(FixtureMap, 62, SvcRow),
                TrainerCreatures = new List<string> { "Lobo", "Oso Pardo", "Oso Polar", "Murciélago" },
            });
            System.Console.WriteLine($"Spawn: entrenador en (62,{SvcRow}).");
        }
    }
}
