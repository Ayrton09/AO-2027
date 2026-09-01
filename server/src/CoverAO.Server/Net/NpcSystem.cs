using CoverAO.Server.Data;
using CoverAO.Server.Game;

namespace CoverAO.Server.Net;

/// <summary>
/// Drives NPCs on the game-loop tick (runs on the loop thread, so it shares no state races with the
/// packet handlers). Slice behaviour: spawned NPCs attack an adjacent player, wander/chase/follow con
/// respetando las paredes del .csm (NpcTileWalkable), y los muertos respawnean en su tile
/// tras un delay. El MOVIMIENTO lo decide `Npc.Movement` (VB6 TipoAI): 0 = va hacia el usuario que ve
/// (IrUsuarioCercano), 1 = estatico, 3 = deambula. Las invocaciones expiran por tiempo.
/// </summary>
public sealed class NpcSystem
{
    // El barrido de la fase de ataque. NO es el que decide cada cuánto pega una criatura: eso lo fija el
    // contador POR CRIATURA de `IntervaloAtaqueTicks` (3 s, como el original). Este sólo dice cada cuánto se
    // revisa quién tiene a alguien al lado.
    private const int AttackEveryTicks = 25;  // NPCs act ~once per second (40ms tick)
    private const int MoveEveryTicks = 25;    // wander/chase check ~once per second
    private const int RespawnTicks = 125;     // ~5 seconds
    private const int AutoReviveTicks = 63;   // ~2500ms (VB6 IntervaloRevive) — delay del revive en arena
    // VB6 AI_NPC.bas:50-51: la caja de vision de IrUsuarioCercano es RECTANGULAR, no un radio.
    private const int RangoVisionX = 8;
    private const int RangoVisionY = 6;

    private readonly World _world;
    private readonly GameData _data;
    private readonly Random _rng = new();
    private readonly List<Spawn> _spawns = new();

    private sealed class Spawn
    {
        public required NpcInfo Template;
        public required WorldPos Pos;
        public int Movement;
        public bool Hostile;
        public short CharIndex;
        public bool Alive;
        public long RespawnAtTick;
    }

    /// <summary>Da de baja el respawn de una criatura (VB6 <c>QuitarNPC</c>, MODULO_NPCs.bas:328): la saca del
    /// ciclo para siempre, que es lo que distingue a /MATA y /MASSKILL de una muerte normal —en el original el
    /// respawn vive sólo en <c>MuereNpc</c>, no en QuitarNPC. Sin esto, "matar sin respawn" reaparecía a los 5 s.
    /// Se MARCA el spawn en vez de sacarlo de la lista a propósito: este método lo llama el hilo de la conexión
    /// mientras Update() itera <c>_spawns</c> en el hilo del loop, y mutar la colección sería una carrera; escribir
    /// los dos campos es seguro en cualquier intercalado.</summary>
    public void CancelRespawn(short charIndex)
    {
        foreach (var s in _spawns)
            if (s.CharIndex == charIndex) { s.Alive = false; s.RespawnAtTick = long.MaxValue; }
    }

    public NpcSystem(World world, GameData data, bool gmsPerseguibles = false)
    {
        _world = world;
        _data = data;
        _gmsPerseguibles = gmsPerseguibles;
    }

    /// <summary>Cómo sube una skill por uso. Lo pone el dispatcher, que es quien tiene <c>SubirSkill</c> con su
    /// tirada y su aviso; acá hace falta porque el rechazo con escudo contra una CRIATURA es la vía por la que
    /// Defensa entrena de verdad. Queda opcional para no atar al sistema de NPCs al dispatcher: si nadie lo
    /// cablea (tests que instancian NpcSystem suelto), el rechazo igual avisa y sólo no entrena.</summary>
    public Action<Player, int>? SubirSkillPorUso { get; set; }

    /// <summary>REGLA DE LA CASA <c>FamiliarBusca</c> del Server.ini (reporte del tester, 2026-08-24), apagada
    /// por defecto. El original NO la tiene: la mascota es <c>TipoAI.SigueAmo</c> (AI_NPC.bas:736), te sigue y
    /// se mueve al azar, y sólo pelea contra el objetivo que le ponen —el dueño atacando algo, o el bicho
    /// atacándolo—. Prendida, el familiar ocioso se va contra el hostil más cercano a su dueño y el que recibe
    /// el golpe se queda peleando con él en vez de seguir con lo suyo. Lo pone Program.cs desde el ini.</summary>
    public bool FamiliarBuscaEnemigo { get; set; }

    /// <summary>Corrección de una infidelidad del FIXTURE, no del original (mismo caso que el <c>Movement</c>
    /// que TestFixture le fija a mano a sus NPCs). El VB6 no deja que una criatura persiga ni golpee a un GM
    /// (<see cref="Player.AdminPerseguible"/>), y el protagonista demo del fixture —Rugura— es Dios, así que con
    /// el gate fiel el PvE del laboratorio se apagaba entero: ni los flows de combate contra NPCs ni el cliente
    /// web podían recibir un golpe. En un server de verdad quien juega no es GM, así que esto sólo se prende en
    /// modo <c>--fixture</c>; en producción el gate corre tal cual la fuente.</summary>
    private readonly bool _gmsPerseguibles;

    /// <summary>¿Las criaturas pueden ir por este jugador? (VB6 <c>flags.AdminPerseguible</c>.)</summary>
    private bool Perseguible(Player p) => p.AdminPerseguible || _gmsPerseguibles;
    /// <summary>Registers a spawn point and spawns its NPC immediately.
    ///
    /// <paramref name="movement"/> es el <c>TipoAI</c> del VB6 (ver <see cref="Npc.Movement"/>): null = el de su
    /// ficha, que es lo correcto para el mundo real. El FIXTURE lo fija a mano porque sus NPCs salen de
    /// plantillas genéricas (Aldeano/Murciélago/Lobo) cuyo Movement es 0 — o sea que, con la IA fiel, un
    /// "banquero" de test se pondría a caminar hacia el jugador. En la data real los servicios SON Movement=1,
    /// así que fijarlo no es hacer trampa: es corregir la infidelidad de reusar una plantilla cualquiera.</summary>
    public void AddSpawn(NpcInfo template, WorldPos pos, bool hostile = false, int? movement = null)
    {
        var spawn = new Spawn
        {
            Template = template, Pos = pos, Hostile = hostile,
            Movement = movement ?? template.Movement,
        };
        SpawnNow(spawn);
        _spawns.Add(spawn);
    }

    /// <summary>
    /// Spawnea un NPC por comando de GM (VB6 SpawnNpc con FX, MODULO_NPCs.bas:803, disparado por /ACC y /RACC).
    /// Devuelve el charIndex del NPC creado, o 0 si el template no existe (paridad con SpawnNpc, que ante un
    /// npcindex inválido devuelve 0 y no emite nada). Como en el original, el NPC queda registrado para
    /// respawn: /ACC y /RACC son equivalentes en el VB6 —el parámetro `respawn` es código muerto, lo pisa
    /// `.Respawn = 0`, que en esta variante significa "sí respawnea"—, así que no se distinguen.
    ///
    /// Emite, además del CharacterCreate que ya manda AddSpawn, el par de efectos del warp que ve el área:
    /// PlayWave(SND_WARP=3) + CreateFX(FXWARP=1). El original busca la posición legal más cercana con
    /// ClosestLegalPos; acá se usa la posición pedida directamente (el caller la valida antes).
    /// </summary>
    public int GmSpawn(NpcInfo template, WorldPos pos)
    {
        AddSpawn(template, pos);
        var spawn = _spawns[^1];
        _world.BroadcastToObservers(pos, ServerMessages.PlayWave(SndWarp, pos.X, pos.Y));
        _world.BroadcastToObservers(pos, ServerMessages.CreateFX(spawn.CharIndex, FxWarp, 0));
        return spawn.CharIndex;
    }

    private const short SndWarp = 3;  // Declares.bas:538
    private const short FxWarp = 1;   // Declares.bas:222
    private const short SndEscudo = 37;  // Declares.bas:548 SND_ESCUDO — el clang del rechazo con escudo
    private const short SndImpacto = 86; // Declares.bas:553 SND_IMPACTO — el golpe que entra
    private const short FxSangre = 14;   // Declares.bas:67 FXSANGRE
    private const short ElementalSinMelee = 92;  // AI_NPC.bas:433 — el elemental no pega cuerpo a cuerpo

    /// <summary>
    /// Vueltas del loop entre ataque y ataque de UNA criatura: los 3000 ms de
    /// <c>IntervaloPermiteAtacarNpc</c> (VB6 modNuevoTimer.bas:52) sobre ticks de 40 ms.
    /// </summary>
    private const long IntervaloAtaqueTicks = 3000 / Game.GameLoop.TickMs;

    /// <summary>
    /// ¿Puede atacar esta criatura AHORA? Consume el intervalo si puede (VB6
    /// <c>IntervaloPermiteAtacarNpc(npcIndex)</c> con <c>Actualizar=True</c>, modNuevoTimer.bas:38).
    /// Lo comparten el golpe y el hechizo, igual que el <c>Contadores.Ataque</c> del original.
    /// </summary>
    private bool IntervaloPermiteAtacar(Npc npc)
    {
        if (_tick < npc.NextAttackTick) return false;
        npc.NextAttackTick = _tick + IntervaloAtaqueTicks;
        return true;
    }

    /// <summary>
    /// Las CUATRO salidas tempranas con que arranca <c>NpcAtacaUser</c> (VB6 SistemaCombate.bas:700-707),
    /// que valen para los dos caminos por los que una criatura pega: el barrido de adyacentes y el de la
    /// mascota que sigue a su agresor.
    ///
    /// <para>Vivían en UN solo call site (<see cref="NpcGolpeaAdyacente"/>), así que por el otro camino
    /// —<c>NpcSeguirAgresor</c>— una criatura con dueño le pegaba a un GM no perseguible, que en el
    /// original sale por la segunda guarda sin emitir un byte. En el VB6 las cuatro están DENTRO de
    /// <c>NpcAtacaUser</c>: por eso las aplica siempre, la llame quien la llame.</para>
    /// </summary>
    private bool PuedeSerAtacado(Player p) =>
        p.IsAlive && !p.Invisible && !p.Oculto && Perseguible(p);

    /// <summary>El sonido propio de una criatura, al área donde está (VB6 <c>SendTarget.ToNPCArea</c>).
    ///
    /// <para>Los traen 66 de los 291 NPCs del install: <c>Snd1</c> al atacar, <c>Snd2</c> al recibir el golpe,
    /// <c>Snd3</c> al morir. El gate <c>&gt; 0</c> es del original — la mayoría de las criaturas no trae la
    /// clave y no suena, y emitir un <c>PlayWave(0)</c> sería un paquete de más.</para></summary>
    internal static void SonidoDeCriatura(World world, Npc npc, int snd)
    {
        if (snd > 0) world.BroadcastToObservers(npc.Pos, ServerMessages.PlayWave((short)snd, npc.Pos.X, npc.Pos.Y));
    }

    /// <summary>Vuelta actual del loop. La guarda <see cref="Update"/> para que los efectos con duración —hoy
    /// la parálisis de los hechizos de NPC— puedan fijar su vencimiento sin arrastrar el tick por toda la
    /// cadena de llamadas de la IA.</summary>
    private long _tick;

    /// <summary>Cuánto dura la parálisis que tira una CRIATURA. El original usa <c>IntervaloParalizado</c>
    /// (Server.ini, 500 vueltas ≈ 20 s) en <c>NpcLanzaSpellSobreUser</c> (modHechizos.bas:224) — el mismo que
    /// el PvP, no el reducido que usa el jugador CONTRA una criatura.
    ///
    /// Lo pone el arranque desde el Server.ini, como el original: estaba cableado y quien hostea no lo podía
    /// tocar aunque la clave exista en la fuente.</summary>
    public long ParalisisDeNpcTicks { get; set; } = 500;

    public void Update(long tick)
    {
        _tick = tick;
        // Revive en ARENA con delay (VB6 DoResucitar, mMainLoop.bas:235 + Modulo_UsUaRiOs.bas:33): un jugador que
        // murió en zona de pelea queda `Resucitando` (lo marca KillPlayer) y DarVida lo levanta recién a los ~2500ms.
        // Fuera de arena NO hay auto-revive: el muerto queda fantasma hasta un revive manual (sacerdote/hechizo/hogar).
        foreach (var p in _world.Players)
        {
            if (!p.Resucitando) continue;
            if (p.IsAlive) { p.Resucitando = false; p.ReviveAtTick = 0; continue; }   // ya revivió por otra vía
            if (p.ReviveAtTick == 0) p.ReviveAtTick = tick + AutoReviveTicks;
            else if (tick >= p.ReviveAtTick) { PacketDispatcher.RevivePlayer(_world, _data, p); p.Resucitando = false; p.ReviveAtTick = 0; }
        }

        // Expiración de invocaciones (VB6 Contadores.TiempoExistencia, decrementado hasta 0 → MuereNpc):
        // las criaturas invocadas se despawnean solas al vencer su tiempo de vida. Se recorre la lista de los
        // que TIENEN vencimiento (casi siempre vacía) y no los 5404 del mundo: esto corre en cada vuelta del
        // loop, o sea 25 veces por segundo, y filtrar la población entera para encontrar cero era el precio de
        // no llevar la cuenta. Se itera al revés para poder remover sin romper el índice.
        var conVencimiento = _world.NpcsConVencimiento;
        for (int i = conVencimiento.Count - 1; i >= 0; i--)
        {
            var npc = conVencimiento[i];
            if (tick < npc.ExpireAtTick) continue;
            _world.RemoveNpc(npc.CharIndex);
            _world.BroadcastToObservers(npc.Pos, ServerMessages.CharacterRemove(npc.CharIndex, faded: true));
        }

        // Respawn management: notice kills (handler removed the NPC) and respawn after the delay.
        foreach (var s in _spawns)
        {
            if (s.Alive && !_world.HasNpc(s.CharIndex))
            {
                s.Alive = false;
                s.RespawnAtTick = tick + RespawnTicks;
            }
            else if (!s.Alive && tick >= s.RespawnAtTick)
            {
                SpawnNow(s);
            }
        }

        // ORDEN: ataque y DESPUÉS movimiento, como el NPCAI del VB6 (AI_NPC.bas:701 pelea, :718 el
        // `Select Case .Movement`). No es cosmético: la fase de ataque termina en RestoreOldMovement cuando no
        // encontró a quién pegarle, y eso le cambia el Movement al NPC — con los barridos al revés, una criatura
        // en modo /SEGUIR alcanzaba a dar un paso antes de que le revirtieran el estado. Las dos cadencias son
        // 25, así que las dos fases caen siempre en el mismo tick y el orden entre ellas es lo único que decide.
        // En CADA vuelta y no sólo en las del barrido de ataque: cuesta 4 tiles por jugador conectado y así el
        // conjunto nunca puede quedar de un tick viejo, pase lo que pase con las cadencias.
        RecalcularTilesJuntoAJugador();

        if (tick % AttackEveryTicks == 0)
            foreach (var npc in _world.Npcs)
            {
                if (tick < npc.ParalyzedUntilTick) continue;   // paralizada: tampoco pega
                NpcTryAttack(npc);
            }

        if (tick % MoveEveryTicks == 0)
            foreach (var npc in _world.Npcs)
            {
                // Las dos ramas de ataque hacen Exit Sub apenas enganchan a alguien, así que un NPC que está
                // pegándole a un usuario no se mueve: el de ataque manda mientras haya alguien adyacente.
                if (tick < npc.ParalyzedUntilTick) continue;   // paralizada: no se mueve (VB6 .flags.Paralizado)
                // TipoAI.SigueAmo (mascotas). El gate por dueño es un atajo del port —el VB6 distingue por
                // Movement, que a una mascota se lo pone FollowAmo— y hay que dejar pasar el caso en que /SEGUIR
                // se lo cambió a NPCDEFENSA: si no, una mascota puesta a seguir al GM igual iría tras su amo.
                if (npc.OwnerCharIndex != 0)
                {
                    // LA CURA VA ANTES DEL GATE DE MOVEMENT y no adentro de NpcTryFollowOwner, que es donde
                    // estaba: ahí sólo corría con el familiar en modo SEGUIR, o sea que dejaba de curar
                    // justo cuando entraba en combate —que es cuando hace falta—, porque pelear le cambia el
                    // Movement a NPCDEFENSA o NpcAtacaNpc.
                    _world.TryGet(npc.OwnerCharIndex, out var duenio);
                    if (duenio is not null && duenio.Pos.Map == npc.Pos.Map) FamiliarCuraAlAmo(npc, duenio);
                }
                if (npc.OwnerCharIndex != 0 && npc.Movement != GameLimits.TipoAiNpcDefensa
                    && npc.Movement != GameLimits.TipoAiNpcAtacaNpc)
                {
                    // La regla de la casa: el familiar ocioso se busca pelea solo. Si engancha una, este mismo
                    // tick ya no sigue al amo — le quedó puesto el Movement de atacar y entra por el switch de
                    // abajo en la próxima vuelta.
                    if (FamiliarBuscaEnemigo && npc.TargetNpcCharIndex == 0 && FamiliarBuscaPresa(npc)) continue;
                    NpcTryFollowOwner(npc); continue;
                }
                if (npc.SinIa) continue;   // NPCAI sale de una para GuardiasCity/facciones/Convertidores
                switch (npc.Movement)
                {
                    case 0: NpcIrUsuarioCercano(npc); break;   // TipoAI.NpcMaloAtacaUsersBuenos
                    case 3: NpcTryWander(npc); break;          // TipoAI.MueveAlAzar
                    case GameLimits.TipoAiNpcDefensa: NpcSeguirAgresor(npc); break;
                    case GameLimits.TipoAiNpcAtacaNpc: NpcAtacaANpcObjetivo(npc); break;
                    default: break;                            // ESTATICO(1) y los no modelados: quietos
                }
            }
    }

    private static readonly (int dx, int dy, byte heading)[] Steps =
        { (0, -1, 1), (1, 0, 2), (0, 1, 3), (-1, 0, 4) }; // N, E, S, W

    /// <summary>VB6 <c>Case TipoAI.MueveAlAzar</c> (AI_NPC.bas:719): un paso al azar con probabilidad
    /// <c>RandomNumber(1, 12) = 3</c>, o sea <b>1/12</b>. Y nada más — el VB6 no frena por tener un jugador cerca
    /// ni por ser mercader. Antes acá había un 40% y dos gates inventados: el NPC deambulaba 5 veces más de la
    /// cuenta y se congelaba cuando alguien se le acercaba, cosa que la fuente no hace.</summary>
    private void NpcTryWander(Npc npc)
    {
        if (_rng.Next(1, 13) != 3) return;
        StepRandom(npc);
    }

    /// <summary>Un paso al azar (VB6 `MoveNPCChar(npcindex, RandomNumber(NORTH, WEST))`).</summary>
    private void StepRandom(Npc npc)
    {
        var (dx, dy, heading) = Steps[_rng.Next(Steps.Length)];
        MoveNpc(npc, npc.Pos.X + dx, npc.Pos.Y + dy, heading);
    }

    /// <summary>Mueve al NPC un tile si el destino es legal (VB6 <c>MoveNPCChar</c> + <c>LegalPosNPC</c>).
    /// Devuelve si se movió. Un tile con un jugador VIVO o con otro NPC no es legal — pero uno con un jugador
    /// MUERTO sí: el NPC no lo atraviesa, lo <b>intercambia de lugar</b> (GameLogic.bas:642 `.UserIndex = 0 Or
    /// IsDeadChar`; el swap en MODULO_NPCs.bas:707). Al cadáver lo empuja al tile del que venía el NPC con un
    /// <c>ForceCharMove</c> de heading invertido, y avisa al área con un CharacterMove.</summary>
    private bool MoveNpc(Npc npc, int x, int y, byte heading)
    {
        if (!NpcTileWalkable(npc, x, y)) return false;                            // pared/.csm + límites 1..100
        var dest = new WorldPos(npc.Pos.Map, (byte)x, (byte)y);
        if (_world.NpcAt(dest) is not null) return false;
        var enElDestino = _world.PlayerAt(dest);
        if (enElDestino is not null && enElDestino.IsAlive) return false;         // a un vivo no lo mueve nadie
        var from = npc.Pos;
        if (enElDestino is not null)
        {
            // Casper: no se lo traslada de agua a tierra ni al revés (MODULO_NPCs.bas:710-714).
            bool aguaDest = _data.IsWater(from.Map, dest.X, dest.Y), aguaOrig = _data.IsWater(from.Map, from.X, from.Y);
            if (aguaDest != aguaOrig) return false;
            enElDestino.Pos = from;
            _world.BroadcastToObservers(from, ServerMessages.CharacterMove(enElDestino.CharIndex, from.X, from.Y),
                                        enElDestino.CharIndex);
            enElDestino.Connection?.Send(ServerMessages.ForceCharMove(InvertirHeading(heading)));
        }
        npc.OldPos = from;      // VB6 oldPos: FindDirection lo usa para no volver por donde vino
        _world.MoveNpc(npc, dest);   // por el World, no `npc.Pos = dest`: mantiene el índice por tile
        npc.Heading = heading;
        BroadcastNpcMove(npc, from, dest);
        return true;
    }

    /// <summary>Los tiles ORTOGONALMENTE PEGADOS a algún jugador conectado. Lo consume
    /// <see cref="NpcGolpeaAdyacente"/> como corte rápido; se recalcula al principio de cada vuelta del loop.
    /// No filtra por estado del jugador a propósito: es un superconjunto, y como tal sólo sirve para negar.</summary>
    private readonly HashSet<WorldPos> _tilesJuntoAJugador = new();

    private void RecalcularTilesJuntoAJugador()
    {
        _tilesJuntoAJugador.Clear();
        foreach (var p in _world.Players)
        {
            var pos = p.Pos;
            if (pos.X > 1) _tilesJuntoAJugador.Add(new WorldPos(pos.Map, (byte)(pos.X - 1), pos.Y));
            if (pos.X < 255) _tilesJuntoAJugador.Add(new WorldPos(pos.Map, (byte)(pos.X + 1), pos.Y));
            if (pos.Y > 1) _tilesJuntoAJugador.Add(new WorldPos(pos.Map, pos.X, (byte)(pos.Y - 1)));
            if (pos.Y < 255) _tilesJuntoAJugador.Add(new WorldPos(pos.Map, pos.X, (byte)(pos.Y + 1)));
        }
    }

    /// <summary>VB6 <c>InvertHeading</c>: N↔S, E↔O.</summary>
    private static byte InvertirHeading(byte h) => h switch
        { HNorte => HSur, HSur => HNorte, HEste => HOeste, _ => HEste };

    /// <summary>VB6 <c>PuedeNpc</c> (GameLogic.bas:1136): el tile está libre para un NPC — sin NPC, sin bloqueo y
    /// sin usuario. OJO: NO mira agua ni terreno; eso lo filtra aparte <c>MoveNPCChar</c>.</summary>
    private bool PuedeNpc(short map, int x, int y)
    {
        if (x <= 0 || y <= 0 || x > 100 || y > 100) return false;
        var pos = new WorldPos(map, (byte)x, (byte)y);
        return _world.NpcAt(pos) is null && !_data.IsBlockedTile(map, x, y) && _world.PlayerAt(pos) is null;
    }

    /// <summary>VB6 <c>Rodeado</c> (GameLogic.bas:1120): los CUATRO ortogonales del OBJETIVO están ocupados.
    /// <c>FindDirection</c> sale con 0 — no tiene sentido forcejear contra alguien ya encerrado.</summary>
    private bool Rodeado(WorldPos p) =>
        !PuedeNpc(p.Map, p.X + 1, p.Y) && !PuedeNpc(p.Map, p.X - 1, p.Y)
        && !PuedeNpc(p.Map, p.X, p.Y + 1) && !PuedeNpc(p.Map, p.X, p.Y - 1);

    private const byte HNorte = 1, HEste = 2, HSur = 3, HOeste = 4;

    /// <summary>VB6 <c>FindDirection</c> (GameLogic.bas:853): hacia dónde dar UN paso para acercarse al objetivo,
    /// o 0 = no moverse. <b>No es un paso greedy: esquiva.</b> Convención de signos del VB6:
    /// <c>X = NPC.X - Target.X</c>, así que <c>Sgn(X)=1</c> = el NPC está al ESTE y quiere ir al OESTE.
    /// <list type="bullet">
    /// <item>Objetivo en un eje CARDINAL con el tile directo ocupado → <b>sidestep</b>: tira 50/50 un
    /// perpendicular y, si ése no se puede, devuelve el otro (aunque tampoco se pueda: el VB6 no re-chequea y el
    /// movimiento simplemente falla).</item>
    /// <item>Objetivo en DIAGONAL → mira los dos ejes; si los dos sirven descarta el que lo devolvería a
    /// <c>oldPos</c> y, si aun así quedan los dos, tira <c>RandomNumber(1,20) &lt; 10</c>. Si no puede por
    /// ninguno, escapa por el perpendicular.</item>
    /// </list>
    /// Antes acá había un paso greedy cuyo fallback para el caso cardinal era <b>inalcanzable</b> (<c>dx == 0</c>),
    /// así que el NPC quedaba clavado contra la pared en vez de rodearla.</summary>
    private byte FindDirection(Npc npc, WorldPos target)
    {
        var pos = npc.Pos;
        int x = pos.X - target.X, y = pos.Y - target.Y;
        if (x == 0 && y == 0) return 0;
        if (Math.Abs(x) + Math.Abs(y) == 1) return 0;   // Distancia (Manhattan) = 1: ya lo tiene al lado
        if (Rodeado(target)) return 0;

        // --- Cardinales: comparten eje. Si el tile directo no se puede, esquiva por un perpendicular.
        if (x == 0)
        {
            int paso = y < 0 ? 1 : -1;                      // y<0 → el objetivo está al SUR
            if (PuedeNpc(pos.Map, pos.X, pos.Y + paso)) return y < 0 ? HSur : HNorte;
            return _rng.Next(1, 11) > 5
                ? (PuedeNpc(pos.Map, pos.X - 1, pos.Y) ? HOeste : HEste)
                : (PuedeNpc(pos.Map, pos.X + 1, pos.Y) ? HEste : HOeste);
        }
        if (y == 0)
        {
            int paso = x > 0 ? -1 : 1;                      // x>0 → el objetivo está al OESTE
            if (PuedeNpc(pos.Map, pos.X + paso, pos.Y)) return x > 0 ? HOeste : HEste;
            return _rng.Next(1, 11) > 5
                ? (PuedeNpc(pos.Map, pos.X, pos.Y - 1) ? HNorte : HSur)
                : (PuedeNpc(pos.Map, pos.X, pos.Y + 1) ? HSur : HNorte);
        }

        // --- Diagonales. `hx`/`hy` son los headings hacia el objetivo por cada eje.
        int sx = x > 0 ? -1 : 1, sy = y > 0 ? -1 : 1;
        byte hx = x > 0 ? HOeste : HEste, hy = y > 0 ? HNorte : HSur;
        bool puedeX = PuedeNpc(pos.Map, pos.X + sx, pos.Y), puedeY = PuedeNpc(pos.Map, pos.X, pos.Y + sy);
        if (puedeX && puedeY)
        {
            // Los dos sirven: descartar el que lo devolvería al tile del que viene (VB6 oldPos).
            bool okX = npc.OldPos.X != pos.X + sx, okY = npc.OldPos.Y != pos.Y + sy;
            if (okX && okY) return _rng.Next(1, 21) < 10 ? hx : hy;
            if (okX) return hx;
            if (okY) return hy;
        }
        else if (puedeX) return hx;
        else if (puedeY) return hy;

        // "llego aca porque no pudo en nada": escapa por el perpendicular del eje Y.
        if (!PuedeNpc(pos.Map, pos.X, pos.Y - sy) || npc.OldPos.Y == pos.Y - sy) return x > 0 ? HEste : HOeste;
        return y > 0 ? HSur : HNorte;
    }

    /// <summary>VB6 <c>IrUsuarioCercano</c> (AI_NPC.bas:249), el movimiento de los <c>Movement = 0</c>
    /// (<c>NpcMaloAtacaUsersBuenos</c>) — que en esta data son 186 de los 291 NPCs, incluido todo el que no trae
    /// la clave (el VB6 lee <c>val("")</c> = 0). Detalles que sólo se ven leyendo la fuente:
    /// <list type="bullet">
    /// <item><b>No mira <c>Hostile</c></b>: cualquiera con Movement=0 camina hacia el usuario. Un gallo te sigue.
    /// Pegar o no es otra cosa (eso sí lo decide Hostile, en NpcTryAttack).</item>
    /// <item>Visión <b>RECTANGULAR 8×6</b> (<c>RANGO_VISION_X/Y</c>, AI_NPC.bas:50-51), no un radio.</item>
    /// <item>Se queda con el <b>PRIMERO</b> que encuentra recorriendo los usuarios del mapa, no con el más cercano.</item>
    /// <item>Paso <b>greedy</b> de a un tile con <c>FindDirection</c>, sin diagonales y <b>sin pathfinding</b> —
    /// el A* es el modo aparte <c>NpcPathfinding=10</c>, así que estos se traban contra las paredes.</item>
    /// <item><c>FindDirection</c> devuelve 0 si el objetivo está <b>pegado</b> (Distancia=1): no se mueve, ataca.</item>
    /// <item>Si no ve a nadie, <b>baila</b>: un paso al azar 1 de cada 11 ticks (AI_NPC.bas:361, "A bailar").</item>
    /// </list></summary>
    private void NpcIrUsuarioCercano(Npc npc)
    {
        foreach (var p in _world.Players)
        {
            if (p.Pos.Map != npc.Pos.Map) continue;
            int dx = p.Pos.X - npc.Pos.X, dy = p.Pos.Y - npc.Pos.Y;
            if (Math.Abs(dx) > RangoVisionX || Math.Abs(dy) > RangoVisionY) continue;
            if (!p.IsAlive || p.Invisible || p.Oculto) continue;   // VB6 filtra Muerto/Invisible/Oculto/protegidos
            if (!Perseguible(p)) continue;             // …y AdminPerseguible (AI_NPC.bas:344): a un GM tampoco lo persigue
            byte h = FindDirection(npc, p.Pos);        // decide el paso — y esquiva; ver FindDirection
            if (h != 0)
            {
                var (sx, sy) = h switch { HNorte => (0, -1), HEste => (1, 0), HSur => (0, 1), _ => (-1, 0) };
                MoveNpc(npc, npc.Pos.X + sx, npc.Pos.Y + sy, h);
            }
            return;   // el VB6 se queda con el PRIMERO que ve y sale, se haya movido o no
        }
        // "Si llega aca es que no habia ningun usuario cercano vivo. A bailar." (AI_NPC.bas:360-363)
        if (_rng.Next(0, 11) == 0) StepRandom(npc);
    }

    /// <summary>VB6 <c>SeguirAgresor</c> (AI_NPC.bas:375), la fase de movimiento de un NPC en
    /// <c>TipoAI.NPCDEFENSA</c>: persigue al usuario cuyo nombre tiene anotado en <c>AttackedBy</c> —el agresor,
    /// o el GM que le tiró un /SEGUIR— mientras lo vea en la caja 8×6, y lo pierde si está muerto, invisible u
    /// oculto. Un paso greedy con FindDirection, como todo lo demás.
    ///
    /// Acá también PEGA, y no es redundante: <c>NpcTryAttack</c> sale de una para toda criatura con dueño
    /// (AI_NPC.bas:702 `If .MaestroUser = 0`), y las criaturas con dueño son justamente las únicas que llegan a
    /// esta rama — a las otras el reset les saca el NPCDEFENSA antes de la fase de movimiento. O sea que
    /// SeguirAgresor es el ÚNICO lugar por el que golpea una mascota, igual que en el original. Para las que no
    /// tienen dueño no se repite el golpe (el original ahí sí pega dos veces, una por rama; no se replica).
    ///
    /// Si no encuentra a quién seguir, el original cae en <c>RestoreOldMovement</c>. Acá eso lo hace
    /// NpcTryAttack, que corre en el mismo tick por el mismo motivo — salvo para las criaturas con dueño, a las
    /// que el original tampoco les restaura nada.</summary>
    private void NpcSeguirAgresor(Npc npc)
    {
        if (string.IsNullOrEmpty(npc.AttackedBy)) return;
        foreach (var p in _world.Players)
        {
            if (p.Pos.Map != npc.Pos.Map) continue;
            int dx = p.Pos.X - npc.Pos.X, dy = p.Pos.Y - npc.Pos.Y;
            if (Math.Abs(dx) > RangoVisionX || Math.Abs(dy) > RangoVisionY) continue;
            if (!string.Equals(p.Name, npc.AttackedBy, StringComparison.OrdinalIgnoreCase)) continue;
            // Freno de facción (AI_NPC.bas:455-462): una mascota de la ARMADA no persigue a alguien que no sea
            // renegado. El amo se come un LocaleMsg(8) y la mascota vuelve con él (FollowAmo). Ojo que el gate
            // mira al AMO, no a la criatura, y sólo aplica a la Armada — el Caos no tiene freno equivalente.
            if (npc.OwnerCharIndex != 0)
            {
                // TryGet es O(1): _players está keyeado por CharIndex. El FirstOrDefault pagaba snapshot +
                // closure + barrido lineal para leer una clave que el índice ya tiene.
                _world.TryGet(npc.OwnerCharIndex, out var amo);
                if (amo is not null && !EsRenegado(amo) && !EsRenegado(p) && amo.IsArmada)
                {
                    amo.Connection?.Send(ServerMessages.LocaleMsg(8));
                    npc.AttackedBy = null;
                    npc.Follow = true; npc.Movement = GameLimits.TipoAiSigueAmo; npc.Hostile = false;   // VB6 FollowAmo
                    return;
                }
            }
            if (!p.IsAlive || p.Invisible || p.Oculto) continue;
            // CASTEA O PEGA, NUNCA LAS DOS (VB6 AI_NPC.bas:428-436). Es un If/Else excluyente: la criatura
            // con hechizos SÓLO castea y no llega nunca al melee, y el NPC 92 —el elemental— está excluido
            // del cuerpo a cuerpo con nombre y apellido. El port pegaba en los dos casos: el corte de
            // familiar mago de `NpcGolpeaA` exige `FamiliarDe(npc) is not null`, y eso da null justamente
            // para las domadas y las invocadas, que son las que llegan por acá.
            if (npc.OwnerCharIndex != 0)
            {
                if (npc.Template.Spells is { Count: > 0 }) NpcLanzaUnHechizo(npc, p);
                else if (Math.Abs(dx) + Math.Abs(dy) <= 1 && npc.Template.Index != ElementalSinMelee)
                    NpcGolpeaA(npc, p);
            }
            byte h = FindDirection(npc, p.Pos);
            if (h != 0)
            {
                var (sx, sy) = h switch { HNorte => (0, -1), HEste => (1, 0), HSur => (0, 1), _ => (-1, 0) };
                MoveNpc(npc, npc.Pos.X + sx, npc.Pos.Y + sy, h);
            }
            return;
        }
    }

    /// <summary>VB6 <c>AiNpcAtacaNpc</c> (AI_NPC.bas:538): la criatura va hasta el NPC que tiene marcado y le
    /// pega cuando lo alcanza. Es la mitad PvE de las mascotas y no existía: el golpe de una criatura CON
    /// DUEÑO no sale de la fase de ataque —el original no se la corre (<c>If .MaestroUser = 0</c>)— sino de
    /// acá. Sin esta rama, una mascota domada o una invocación no le pegaba a nadie NUNCA.</summary>
    /// <summary>El familiar ocioso elige presa (regla de la casa <see cref="FamiliarBuscaEnemigo"/>). Busca el
    /// hostil más cercano <b>a su dueño</b> y no a sí mismo: el tester lo pidió con esas palabras —"que vaya a
    /// pelear contra el animal que esté más cerca mío"— y la diferencia es real, porque el familiar se aleja
    /// persiguiendo y desde su posición el "más cercano" iría corriéndose cada vez más lejos del jugador.
    ///
    /// <para>Sólo se mete con lo que el dueño podría atacar: nada de mercaderes, banqueros, sacerdotes ni de
    /// otras mascotas —ni la propia ni la de un tercero, que sería empezar una pelea entre jugadores sin que
    /// nadie la haya pedido—. El alcance es el rectángulo de visión del original (8×6) medido desde el dueño.
    /// Devuelve true si enganchó a alguien.</para></summary>
    private bool FamiliarBuscaPresa(Npc npc)
    {
        _world.TryGet(npc.OwnerCharIndex, out var amo);
        if (amo is null || !amo.IsAlive || amo.Pos.Map != npc.Pos.Map) return false;
        Npc? mejor = null; int mejorDist = int.MaxValue;
        foreach (var otro in _world.NpcsOnMap(npc.Pos.Map))
        {
            if (!otro.IsAlive || otro.CharIndex == npc.CharIndex) continue;
            if (otro.OwnerCharIndex != 0) continue;                 // mascotas ajenas y la propia, afuera
            if (!otro.Hostile || !otro.Attackable) continue;         // sólo lo que de verdad es una amenaza
            if (otro.IsMerchant || otro.IsBanker || otro.IsReviver) continue;
            int dx = Math.Abs(otro.Pos.X - amo.Pos.X), dy = Math.Abs(otro.Pos.Y - amo.Pos.Y);
            if (dx > RangoVisionX || dy > RangoVisionY) continue;
            int dist = dx + dy;
            if (dist >= mejorDist) continue;
            mejor = otro; mejorDist = dist;
        }
        if (mejor is null) return false;
        npc.TargetNpcCharIndex = mejor.CharIndex;
        npc.Movement = GameLimits.TipoAiNpcAtacaNpc;
        return true;
    }

    private void NpcAtacaANpcObjetivo(Npc npc)
    {
        var blanco = npc.TargetNpcCharIndex != 0 ? _world.NpcByIndex(npc.TargetNpcCharIndex) : null;
        // `If Not bNoEsta` (AI_NPC.bas:625): si el blanco ya no está, la mascota vuelve con su amo y una
        // criatura sin dueño recupera el movimiento que traía.
        if (blanco is null || !blanco.IsAlive || blanco.Pos.Map != npc.Pos.Map || blanco.CharIndex == npc.CharIndex)
        {
            npc.TargetNpcCharIndex = 0;
            npc.Movement = npc.OwnerCharIndex != 0 ? GameLimits.TipoAiSigueAmo : npc.OriginalMovement;
            return;
        }
        int dx = blanco.Pos.X - npc.Pos.X, dy = blanco.Pos.Y - npc.Pos.Y;
        if (Math.Abs(dx) + Math.Abs(dy) <= 1) { NpcGolpeaA(npc, blanco); return; }   // `If Distancia <= 1 Then NpcAtacaNpc`
        byte h = FindDirection(npc, blanco.Pos);
        if (h != 0)
        {
            var (sx, sy) = h switch { HNorte => (0, -1), HEste => (1, 0), HSur => (0, 1), _ => (-1, 0) };
            MoveNpc(npc, npc.Pos.X + sx, npc.Pos.Y + sy, h);
        }
    }

    /// <summary>VB6 <c>CheckPets</c> (SistemaCombate.bas:630), llamado desde <c>NpcAtacaUser</c>: cuando una
    /// criatura le pega a alguien, TODAS las mascotas de ese alguien salen a pelear con ella. Ojo el orden
    /// del original —el blanco se pisa sólo si la mascota no tenía uno (<c>If TargetNPC = 0 Then</c>)— y que
    /// una mascota no se ataca a sí misma ni a otra del mismo dueño.</summary>
    private void MascotasDefiendenAlAmo(Player amo, Npc agresor)
    {
        if (!agresor.Attackable || agresor.IsMerchant || agresor.IsBanker || agresor.IsReviver) return;   // VB6 PuedeAtacarNPC
        foreach (var mascota in _world.Npcs)
        {
            if (mascota.OwnerCharIndex != amo.CharIndex || !mascota.IsAlive) continue;
            if (mascota.CharIndex == agresor.CharIndex || agresor.OwnerCharIndex == amo.CharIndex) continue;
            if (mascota.TargetNpcCharIndex == 0) mascota.TargetNpcCharIndex = agresor.CharIndex;
            mascota.Movement = GameLimits.TipoAiNpcAtacaNpc;
        }
    }

    /// <summary>VB6 <c>esRene</c>: el renegado, que en el port es el status criminal (ver Player.FactionStatus).</summary>
    private static bool EsRenegado(Player p) => p.FactionStatus == Player.StatusCriminal;

    /// <summary>Area-scoped NPC movement: players who still see it get a CharacterMove; players it just
    /// walked into view of get a CharacterCreate; players it left get a CharacterRemove.</summary>
    private void BroadcastNpcMove(Npc npc, WorldPos from, WorldPos dest)
    {
        foreach (var pl in _world.Players)
        {
            bool was = World.InView(pl.Pos, from), now = World.InView(pl.Pos, dest);
            if (was && now) pl.Connection?.Send(ServerMessages.CharacterMove(npc.CharIndex, dest.X, dest.Y));
            else if (now) pl.Connection?.Send(ServerMessages.CharacterCreate(npc));
            else if (was) pl.Connection?.Send(ServerMessages.CharacterRemove(npc.CharIndex, faded: true));
        }
    }

    /// <summary>VB6 <c>Case TipoAI.SigueAmo</c> (AI_NPC.bas:736): <c>SeguirAmo</c> y DESPUÉS un paso al azar 1/12
    /// — el `Exit Sub` de SeguirAmo sale de SeguirAmo, no del NPCAI, así que el paso al azar corre igual, haya
    /// seguido al amo o no (una mascota puede moverse dos tiles en el mismo tick; es lo que hace la fuente).
    ///
    /// <c>SeguirAmo</c> (AI_NPC.bas:505) no es "caminar hasta ponerse al lado con A*": tiene **correa de 3**
    /// (`Distancia > 3`, MANHATTAN — o sea que se queda quieta mientras el amo esté cerca), exige verlo dentro
    /// de la caja **8×6**, lo filtra por muerto/invisible/oculto, y da UN paso greedy con
    /// <c>FindDirection</c>, sin pathfinding.</summary>
    /// <summary>
    /// EL FAMILIAR QUE SABE CURAR le tira una curación a su dueño cuando lo ve herido.
    ///
    /// <para>Es la habilidad que el Ely aprende al nivel 10 y la única de las trece que hoy tiene efecto — las
    /// otras se aprenden, se guardan y se muestran en el panel, y están anotadas como pendientes en
    /// <c>fase5/familiares-plan.md</c>. Se prefirió dejarlas visibles y sin efecto antes que esconder que el
    /// familiar sube de nivel.</para>
    ///
    /// <para>La habilidad número 5 ES el hechizo 5, "Curar heridas graves" (15 a 25 puntos): el enum de
    /// habilidades del módulo de referencia usa índices de hechizo. Así que se cura con el mismo camino que
    /// usa un sacerdote para curar a otro NPC, y sale con su sonido y su efecto.</para>
    ///
    /// <para>Va acá, en el barrido de las criaturas que siguen a su dueño, porque a una criatura CON DUEÑO el
    /// original no le corre la fase de ataque — que es donde vive <c>NpcLanzaUnHechizo</c>. Sin esto la
    /// habilidad no tendría ningún tick donde ocurrir.</para>
    /// </summary>
    private void FamiliarCuraAlAmo(Npc npc, Player amo)
    {
        var fam = amo.Familiar;
        if (fam.CharIndexEnElMundo != npc.CharIndex || !fam.Sabe(Familiar.HabCurar)) return;
        if (!amo.IsAlive || amo.Hp >= amo.MaxHp) return;
        if (_data.MapNoMagic(amo.Pos.Map)) return;
        // TIENE QUE VERLO. Sin esto el único límite era "el mismo mapa", y eso convertía la habilidad en lo
        // contrario de lo que quiere ser: al familiar te convenía ESCONDERLO en un rincón —donde nadie puede
        // matarlo— y seguía curándote gratis desde el otro extremo del mapa. Es la misma caja 8×6 con la que
        // el original decide si una criatura ve a alguien.
        if (Math.Abs(amo.Pos.X - npc.Pos.X) > RangoVisionX
            || Math.Abs(amo.Pos.Y - npc.Pos.Y) > RangoVisionY) return;
        // Una de cada cuatro vueltas, y la vuelta de las mascotas es una por segundo: cura de a ratos, no
        // todo el tiempo. La proporción sale de medir y no de copiar — el módulo de referencia tira 30 de 100
        // POR GOLPE, que acá no significa nada porque este barrido no es el de los golpes. Con una de veinte
        // salía una cura cada veinte segundos y no se notaba que el familiar estuviera haciendo algo.
        if (_rng.Next(4) != 0) return;
        var spell = _data.Spell(Familiar.HabCurar);
        if (spell is null) return;

        int cura = spell.MaxHp >= spell.MinHp ? _rng.Next(spell.MinHp, spell.MaxHp + 1) : spell.MinHp;
        amo.Hp = Math.Min(amo.MaxHp, amo.Hp + cura);
        EmitirFxDeHechizo(npc, amo, spell);
        var conn = amo.Connection;
        conn?.Send(ServerMessages.ConsoleMsg($"{fam.Nombre} te cura {cura} puntos de vida.", 0));
        conn?.Send(ServerMessages.UpdateHP((short)amo.Hp));
    }

    private void NpcTryFollowOwner(Npc npc)
    {
        // El dueño se desconectó: OnDisconnect ya despawnea sus invocaciones/mascotas. NO la revertimos a NPC
        // salvaje (OwnerCharIndex=0): matar una invocación huérfana no debe dar recompensa — el gate en
        // AttackNpc es OwnerCharIndex==0, así que mantenerlo distinto de 0 evita la canilla de exp/oro via relog.
        _world.TryGet(npc.OwnerCharIndex, out var owner);   // O(1) por índice, no barrido lineal
        if (owner is not null && owner.Pos.Map == npc.Pos.Map)
        {
            int dx = owner.Pos.X - npc.Pos.X, dy = owner.Pos.Y - npc.Pos.Y;
            if (Math.Abs(dx) <= RangoVisionX && Math.Abs(dy) <= RangoVisionY
                && owner.IsAlive && !owner.Invisible && !owner.Oculto
                && Math.Abs(dx) + Math.Abs(dy) > 3)          // la correa: Distancia (Manhattan) > 3
            {
                byte h = FindDirection(npc, owner.Pos);
                if (h != 0)
                {
                    var (sx, sy) = h switch { HNorte => (0, -1), HEste => (1, 0), HSur => (0, 1), _ => (-1, 0) };
                    MoveNpc(npc, npc.Pos.X + sx, npc.Pos.Y + sy, h);
                }
            }
        }
        if (_rng.Next(1, 13) == 3) StepRandom(npc);   // el paso al azar del Case SigueAmo, corra o no SeguirAmo
    }

    /// <summary>¿Este NPC puede pisar este tile? VB6 MoveNPCChar/LegalPosNPC: dentro del mapa, sin bloqueo del .csm
    /// (no se atraviesan paredes), y respetando el terreno de la criatura — un NPC terrestre (AguaValida=0) no entra
    /// al agua, y uno acuático puro (TierraInvalida=1) no pisa tierra.</summary>
    private bool NpcTileWalkable(Npc npc, int x, int y)
    {
        var map = npc.Pos.Map;
        if (x < 1 || x > 100 || y < 1 || y > 100 || _data.IsBlockedTile(map, x, y)) return false;
        // VB6 LegalPosNPC (GameLogic.bas:642): `(.Trigger <> eTrigger.POSINVALIDA Or IsPet)` — los NPCs no pisan
        // un POSINVALIDA, PERO las mascotas SÍ (si no, se le quedarían atrás al amo al cruzar uno).
        if (npc.OwnerCharIndex == 0 && _data.IsNpcInvalidTile(map, x, y)) return false;
        bool water = _data.IsWater(map, x, y);
        if (water && !npc.Template.AguaValida) return false;   // terrestre no entra al agua
        if (!water && npc.Template.TierraInvalida) return false; // acuático puro no pisa tierra
        return true;
    }


    private void SpawnNow(Spawn s)
    {
        var npc = new Npc
        {
            CharIndex = _world.AllocateCharIndex(),
            Template = s.Template,
            Hp = s.Template.MaxHp,
            Pos = s.Pos,
            StartPos = s.Pos,                // VB6 StartPos: de acá sale si el click le muestra su diálogo
            Hostile = s.Hostile,
            OriginallyHostile = s.Hostile,   // VB6 MODULO_NPCs.bas:994: `.flags.OldHostil = .Hostile` al cargarlo
            Movement = s.Movement,           // VB6 TipoAI: 0 = va al usuario cercano, 1 = estatico, 3 = deambula
            OriginalMovement = s.Movement,   // VB6 flags.OldMovement: a esto vuelve RestoreOldMovement
        };
        // ROL desde la data (VB6 eNPCType / Comercia): los NPCs poblados desde MapSpawns eran inertes — el
        // Sacerdote no revivía, el Banquero no atendía y los mercaderes no vendían. Ahora cada uno cumple su
        // función según su ficha del NPCs.dat. (Los NPCs de test de Program.cs siguen fijando sus flags a mano.)
        if (s.Template.NpcType == 1) npc.IsReviver = true;                       // eNPCType.Revividor
        if (s.Template.NpcType == 11) npc.IsVeterinaria = true;                  // eNPCType.Veterinarias
        if (s.Template.NpcType == 4) npc.IsBanker = true;                        // eNPCType.Banquero
        if (s.Template.NpcType == 7) npc.IsTransportador = true;                 // eNPCType.transportadores (el pirata del pasaje)
        // eNPCType.Entrenador: sus criaturas salen de la ficha (NroCriaturas + CN1..CNn). `IsTrainer` se
        // deriva de que la lista NO sea nula, asi que con esto el entrenador del mundo por fin lista algo.
        if (s.Template.TrainerCreatures is { Count: > 0 } criaturas)
            npc.TrainerCreatures = criaturas.ToList();
        // Los NPCs de servicio/guardias (Attackable=0 en el NPCs.dat) no se pueden atacar (VB6 NpcAtacable).
        // Los NPCs de test de Program.cs quieren ser atacables → sólo los poblados por data respetan el flag.
        npc.Attackable = s.Template.Attackable;
        if (s.Template.StockItems is { Count: > 0 } items)                       // Comercia=1 → mercader con su stock
            npc.Stock = items.Select(it => new MerchantSlot
            {
                ObjIndex = (short)it.ObjIndex,
                Amount = (short)Math.Clamp(it.Amount, 1, short.MaxValue),
            }).ToList();
        _world.AddNpc(npc);
        s.CharIndex = npc.CharIndex;
        s.Alive = true;
        _world.BroadcastToObservers(npc.Pos, ServerMessages.CharacterCreate(npc)); // only players who can see the tile
    }

    private void NpcTryAttack(Npc npc)
    {
        // VB6 NPCAI (AI_NPC.bas:700) sale ANTES de pelear para GuardiasCity/facciones/Convertidores: no corren
        // IA, así que ni siquiera devuelven el golpe — ni se les restaura nada.
        if (npc.SinIa) return;
        // `If .MaestroUser = 0` (AI_NPC.bas:702): a una criatura CON DUEÑO el original no le corre la fase de
        // ataque, y por lo tanto tampoco el RestoreOldMovement del final. Su golpe sale de SeguirAgresor.
        if (npc.OwnerCharIndex != 0) return;
        // Mercaderes y banqueros no pelean (atajo del port). PERO tienen que llegar igual al reset de abajo: el
        // original no los excluye de NPCAI, así que un /SEGUIR sobre el herrero se le revierte en el tick
        // siguiente como a cualquier otro. Con el `return` acá arriba se quedaba en NPCDEFENSA para siempre y se
        // iba caminando de la ciudad detrás del GM, sin volver nunca a su puesto.
        if (!npc.IsMerchant && !npc.IsBanker && NpcGolpeaAdyacente(npc)) return;
        RestoreOldMovement(npc);
    }

    /// <summary>Busca un jugador adyacente al que pegarle y le pega. Devuelve si enganchó a alguien — el
    /// <c>Exit Sub</c> de las dos ramas de ataque del original, que es lo que evita que además se le corra el
    /// RestoreOldMovement.</summary>
    private bool NpcGolpeaAdyacente(Npc npc)
    {
        // Corte barato antes de los cuatro PlayerAt: si este tile no está pegado a NINGÚN jugador, no hay a
        // quién pegarle y el método iba a devolver false igual. El conjunto se arma una vez por barrido
        // (4 tiles por jugador) y ahorra 4 barridos de la lista de jugadores por cada uno de los 5404 NPCs.
        // Es un SUPERCONJUNTO a propósito —se arma sin mirar vivo/invisible/oculto/perseguible— así que sólo
        // puede decir "seguro que no": los filtros de verdad siguen abajo, intactos.
        if (!_tilesJuntoAJugador.Contains(npc.Pos)) return false;
        // Look at the four adjacent tiles for a player to hit.
        Span<(int dx, int dy)> dirs = stackalloc (int, int)[] { (0, -1), (1, 0), (0, 1), (-1, 0) };
        foreach (var (dx, dy) in dirs)
        {
            int x = npc.Pos.X + dx, y = npc.Pos.Y + dy;
            if (x is < 0 or > 255 || y is < 0 or > 255) continue;
            var player = _world.PlayerAt(new WorldPos(npc.Pos.Map, (byte)x, (byte)y));
            if (player is null)
            {
                // `ElseIf NPCI > 0` (AI_NPC.bas:189): sin jugador en el tile, la criatura le pega a la MASCOTA
                // que tenga al lado. Sólo a las que tienen dueño y no estén paralizadas — las salvajes no se
                // pelean entre sí. Sin esto una invocación era invulnerable al mundo entero.
                var otro = _world.NpcAt(new WorldPos(npc.Pos.Map, (byte)x, (byte)y));
                if (otro is not null && otro.IsAlive && otro.OwnerCharIndex != 0
                    && otro.CharIndex != npc.CharIndex && _tick >= otro.ParalyzedUntilTick)
                {
                    NpcGolpeaA(npc, otro);
                    return true;
                }
                continue;
            }
            if (!player.IsAlive || player.Invisible || player.Oculto) continue;   // VB6 NpcAtacaUser: no pega al oculto/invisible
            if (player.AsustaAUnaCriaturaDeNivel(npc.Template.Nivel)) continue;   // se transformó en algo que le da miedo a ESTA criatura
            if (player.CharIndex == npc.OwnerCharIndex) continue; // summons never attack their summoner
            // Las TRES ramas de la IA exigen `flags.AdminPerseguible` (AI_NPC.bas:88/160/229), que el original
            // sólo prende para el usuario común: a un GM ninguna criatura le pega ni lo persigue.
            if (!Perseguible(player)) continue;
            // VB6 NPCAI (AI_NPC.bas:705) se bifurca por Hostile: Hostile<>0 -> HostilMalvadoAI, que pega a
            // CUALQUIER usuario adyacente; Hostile=0 -> HostilBuenoAI (AI_NPC.bas:451), que sólo pega si el
            // usuario adyacente es `flags.AttackedBy`, o sea su agresor. Sin este gate todo NPC que no fuera
            // mercader/banquero (aldeanos, sacerdotes, entrenadores) le pegaba sin provocación al que se le
            // parara al lado.
            if (!npc.Hostile && !string.Equals(player.Name, npc.AttackedBy, StringComparison.OrdinalIgnoreCase))
                continue;

            // Los NPCs que saben MAGIA la tiran en vez de pegar (VB6 AI_NPC.bas:169/295 → NpcLanzaUnSpell,
            // que elige uno al azar de su lista). Son 70 de los 291 del install: sin esto, los Magos, los
            // Nigromantes y las Banshees peleaban sólo a melee.
            if (npc.Template.Spells is { Count: > 0 } && NpcLanzaUnHechizo(npc, player)) return true;
            NpcGolpeaA(npc, player);
            return true; // one attack per tick
        }
        return false;
    }


    /// <summary>
    /// El NPC tira uno de sus hechizos, elegido al azar (VB6 <c>NpcLanzaUnSpell</c>, AI_NPC.bas:878:
    /// <c>RandomNumber(1, LanzaSpells)</c>) y aplicado por <c>NpcLanzaSpellSobreUser</c> (modHechizos.bas:7).
    ///
    /// Gates del original, en orden: no le tira a un invisible ni a un oculto, y no tira nada en un mapa con
    /// magia deshabilitada. Devuelve true si lanzó — el caller no pega ese tick.
    /// </summary>
    private bool NpcLanzaUnHechizo(Npc npc, Player player)
    {
        if (player.Invisible || player.Oculto) return false;
        if (_data.MapNoMagic(player.Pos.Map)) return false;
        // EL MISMO CONTADOR QUE EL GOLPE (VB6 modHechizos.bas:100: `If Not IntervaloPermiteAtacarNpc(...)
        // Then Exit Sub`). Se consume acá y no en el caller, igual que en el original, así que una criatura
        // maga castea O pega en una vuelta, nunca las dos cosas.
        if (!IntervaloPermiteAtacar(npc)) return false;
        var lista = npc.Template.Spells!;
        var spell = _data.Spell((short)lista[_rng.Next(lista.Count)]);
        if (spell is null) return false;

        var conn = player.Connection;
        if (spell.SubeHp == 1)          // hechizo que CURA (los sacerdotes se curan entre ellos)
        {
            int cura = spell.MaxHp >= spell.MinHp ? _rng.Next(spell.MinHp, spell.MaxHp + 1) : spell.MinHp;
            player.Hp = Math.Min(player.MaxHp, player.Hp + cura);
            EmitirFxDeHechizo(npc, player, spell);
            conn?.Send(ServerMessages.LocaleMsg(34, $"${npc.Template.Index}%{cura}"));
            conn?.Send(ServerMessages.UpdateHP((short)player.Hp));
            return true;
        }
        // HECHIZOS DE ESTADO (VB6 :208, `ElseIf Paraliza = 1 Or Inmoviliza = 1`). Acá había un
        // `if (spell.SubeHp != 2) return false` con el comentario "los de estado los cubre la rama de abajo del
        // original" — pero esa rama no estaba portada, así que el NPC devolvía false y el caller le pegaba a
        // melee. Quedaban SIN su mecánica: el Dark Ruthnar (602), el Chamán Ártico (603), el Elemental de
        // tierra (94/129), el Paladín (75/85/114) y el Asesino (78/87/136) —cuyo ÚNICO hechizo es paralizar—,
        // más la Medusa, el Gran Dragón Verde, el Rey Dragón y el Clérigo, que lo tiran la mitad de las veces.
        if (spell.IsParalyze || spell.IsImmobilize)
        {
            // El original sólo actúa si NO estaba ya paralizado (`If .flags.Paralizado = 0`): no refresca el
            // contador, así que la parálisis no se puede encadenar indefinidamente.
            if (player.IsParalyzed(_tick)) return false;
            player.ParalyzedUntilTick = _tick + ParalisisDeNpcTicks;
            player.ParalyzedByCharIndex = 0;                       // lo paralizó una criatura, no un jugador
            EmitirFxDeHechizo(npc, player, spell);
            if (!player.ClientParalyzed) { player.ClientParalyzed = true; conn?.Send(ServerMessages.ParalizeOK()); }
            return true;
        }
        // `Estupidez` (:232, WriteDumb) no se porta: el port no modela ese estado, igual que en la magia del
        // jugador. Ningún NPC del install trae un hechizo con esa bandera.
        if (spell.SubeHp != 2) return false;

        // Daño: el equipo RESTA su ResistenciaMagica (casco + escudo + armadura + montura + anillo,
        // modHechizos.bas:136-170). Es la misma suma de cinco slots que ya usa la magia del jugador.
        int dano = spell.MaxHp >= spell.MinHp ? _rng.Next(spell.MinHp, spell.MaxHp + 1) : spell.MinHp;
        dano = Math.Max(0, dano - Equipment.MagicResistance(player, _data));
        EmitirFxDeHechizo(npc, player, spell);
        player.Hp = Math.Max(0, player.Hp - dano);
        conn?.Send(ServerMessages.LocaleMsg(34, $"${npc.Template.Index}%{dano}"));
        conn?.Send(ServerMessages.UpdateHP((short)player.Hp));
        if (player.Hp < 1) PacketDispatcher.KillPlayer(_world, _data, player);
        return true;
    }

    /// <summary>Sonido + FX + partícula del hechizo de un NPC, al área (modHechizos.bas:116-120).</summary>
    private void EmitirFxDeHechizo(Npc npc, Player player, SpellInfo spell)
    {
        if (spell.Wav > 0)
            _world.BroadcastToObservers(player.Pos, ServerMessages.PlayWave((short)spell.Wav, player.Pos.X, player.Pos.Y));
        if (spell.FxGrh > 0)
            _world.BroadcastToObservers(player.Pos, ServerMessages.CreateFX(player.CharIndex, (short)spell.FxGrh, (short)spell.FxLoops));
        // LA PARTÍCULA (`If Hechizos(Spell).Particle <> 0`, modHechizos.bas:120/178/216 — está en las TRES
        // ramas). Faltaba, y no es un detalle cosmético menor: 32 NPCs tiran hechizos que son SÓLO partícula,
        // sin FXgrh — Apocalipsis (25, 7 NPCs), Descarga Flamígera (69), Semillas de fuego (76), Lamento de
        // las almas (84), Descarga eléctrica (93), Ráfaga ígnea (94) y los trece elementales/fantasmas. Un
        // Nigromante te bajaba 75 de vida y en pantalla no pasaba absolutamente nada.
        if (spell.Particle > 0)
            _world.BroadcastToObservers(player.Pos,
                ServerMessages.EfectoCharParticula(player.CharIndex, (short)spell.Particle, spell.TimeParticula, false));
        // El cartel sobre la cabeza del NPC con el nombre del hechizo (ChatOverHeadLocale, modo 1).
        _world.BroadcastToObservers(npc.Pos, ServerMessages.ChatOverHeadLocale(npc.CharIndex, spell.Index, 1));
    }

    /// <summary>El golpe en sí (VB6 <c>NpcAtacaUser</c>): tirada de impacto contra el poder de evasión del
    /// jugador, daño absorbido por el equipo, y la muerte si le llega a 0. Separado del barrido porque hay DOS
    /// caminos que pegan — la fase de ataque y <c>SeguirAgresor</c> (el único por el que golpea una mascota).</summary>
    /// <summary>
    /// UNA CRIATURA LE PEGA A OTRA (VB6 <c>NpcAtacaNpc</c>, llamado desde <c>AI_NPC.bas:192</c>). Es la rama
    /// <c>ElseIf NPCI &gt; 0</c> del barrido de adyacentes: sólo entra cuando en ese tile NO hay un jugador.
    ///
    /// <para>Dos condiciones del original, y las dos importan: la víctima tiene que tener <b>dueño</b>
    /// (<c>MaestroUser &gt; 0</c>, o sea una mascota o invocación — las criaturas salvajes no se pelean entre
    /// sí) y <b>no estar paralizada</b>. Sin esta rama una invocación era invulnerable a todo el mundo: un
    /// blanco perfecto para tanquear gratis.</para>
    ///
    /// <para>La víctima no lleva equipo, así que no hay absorción por armadura: el daño es el crudo del
    /// atacante contra la defensa de la plantilla de la víctima, con piso 1 como en el camino del jugador.</para>
    /// </summary>
    /// <summary>Le saca el arma a la víctima. Lo resuelve el DESPACHADOR y no acá: desequipar toca el
    /// inventario, las animaciones y tres paquetes, y todo eso ya está escrito allá para el desarme del
    /// jugador. Se cablea igual que <see cref="SubirSkillPorUso"/>.</summary>
    public System.Action<Player>? DesarmarPorFamiliar;

    /// <summary>El familiar de alguien, si esta criatura lo es. Devuelve null para cualquier otra — incluidas
    /// las mascotas domadas y las invocaciones, que tienen dueño pero no habilidades.</summary>
    private Familiar? FamiliarDe(Npc npc)
    {
        if (npc.OwnerCharIndex == 0) return null;
        _world.TryGet(npc.OwnerCharIndex, out var amo);
        var fam = amo?.Familiar;
        return fam is not null && fam.CharIndexEnElMundo == npc.CharIndex ? fam : null;
    }

    /// <summary>Cada cuánto sale una habilidad de golpe. El módulo de referencia no da un número para éstas;
    /// se usa el que el original usa para el veneno de una criatura (<c>RandomNumber(1,100) &lt; 30</c>,
    /// MODULO_NPCs.bas:793), que es la mecánica más parecida que hay en la fuente.</summary>
    private const int ProbHabilidadDeGolpe = 30;

    /// <summary>
    /// LAS HABILIDADES DE GOLPE del familiar, sobre un JUGADOR. Son las cinco que aprenden el Ent, el Oso, el
    /// Tigre y el Lobo, y se aplican después de que el golpe entró.
    ///
    /// <para>OJO con los números: en el enum de referencia las habilidades MÁGICAS son índices de hechizo
    /// —la 5 es "Curar heridas graves"— pero las de golpe NO lo son. La 11 sería "Resucitar" y la 10
    /// "Remover parálisis": tratarlas como hechizos haría que el Ent reviviera a su víctima. Son banderas.</para>
    /// </summary>
    private void FamiliarGolpeoA(Npc npc, Player victima)
    {
        var fam = FamiliarDe(npc);
        if (fam is null || !victima.IsAlive) return;

        if (fam.Sabe(Familiar.HabGolpeEnvenena) && !victima.IsPoisoned(_tick) && _rng.Next(1, 101) < ProbHabilidadDeGolpe)
        {
            victima.PoisonedUntilTick = long.MaxValue;   // el veneno del original no caduca solo: se cura
            victima.Connection?.Send(ServerMessages.LocaleMsg(182));
        }
        if (fam.Sabe(Familiar.HabGolpeParaliza) && !victima.IsParalyzed(_tick) && _rng.Next(1, 101) < ProbHabilidadDeGolpe)
        {
            victima.ParalyzedUntilTick = _tick + ParalisisDeNpcTicks;
            victima.ParalyzedByCharIndex = 0;
            if (!victima.ClientParalyzed) { victima.ClientParalyzed = true; victima.Connection?.Send(ServerMessages.ParalizeOK()); }
        }
        if (fam.Sabe(Familiar.HabGolpeCiega) && !victima.IsBlinded(_tick) && _rng.Next(1, 101) < ProbHabilidadDeGolpe)
        {
            victima.BlindedUntilTick = _tick + CegueraDeFamiliarTicks;
            victima.Connection?.Send(ServerMessages.Blind());
        }
        if (fam.Sabe(Familiar.HabGolpeDesarma) && _rng.Next(1, 101) < ProbHabilidadDeGolpe)
            DesarmarPorFamiliar?.Invoke(victima);
        // El `AttrBonus[Agilidad] >= 0` es la guarda de "todavía no está entorpecido", igual que las tres
        // hermanas de arriba miran su propio estado. Sin ella cada mordisco apilaba otro -6 y la agilidad se
        // hundía sin piso — un familiar de nivel 30 dejaba a su víctima en cero.
        if (fam.Sabe(Familiar.HabGolpeEntorpece) && victima.AttrBonus[GameLimits.AttrAgilidad] >= 0
            && _rng.Next(1, 101) < ProbHabilidadDeGolpe)
        {
            // "Entorpecer" en el original es bajarle la AGILIDAD, que es lo que hace el hechizo Torpeza. Se
            // reusa el mismo mecanismo de bonos temporales que usan los hechizos de atributo, en vez de
            // inventar un estado nuevo: así caduca solo por el mismo camino que todo lo demás.
            victima.ApplyAttrBonus(GameLimits.AttrAgilidad, -EntorpecerPuntos, _tick, EntorpecerTicks);
            victima.Connection?.Send(ServerMessages.UpdateDexterity(victima.Agility));
        }
    }

    /// <summary>Las mismas habilidades, sobre otra CRIATURA. De las cinco, la única que una criatura puede
    /// sufrir en este motor es la parálisis: no tiene inventario que desarmar, ni agilidad, ni ceguera, ni
    /// veneno. Las otras cuatro no se fingen.</summary>
    private void FamiliarGolpeoA(Npc npc, Npc victima)
    {
        var fam = FamiliarDe(npc);
        if (fam is null || !victima.IsAlive) return;
        if (fam.Sabe(Familiar.HabGolpeParaliza) && _tick >= victima.ParalyzedUntilTick
            && _rng.Next(1, 101) < ProbHabilidadDeGolpe)
            victima.ParalyzedUntilTick = _tick + ParalisisDeNpcTicks;
    }

    /// <summary>Cuánto dura la ceguera que tira un familiar: lo mismo que la del hechizo de ceguera del
    /// jugador (<c>IntervaloParalizado / 3</c>, modHechizos.bas:1309).</summary>
    private const long CegueraDeFamiliarTicks = 166;
    /// <summary>Y el entorpecimiento: seis puntos de agilidad por unos veinte segundos.</summary>
    private const int EntorpecerPuntos = 6;
    private const long EntorpecerTicks = 500;

    private void NpcGolpeaA(Npc npc, Npc victima)
    {
        if (npc.Template.Spells is { Count: > 0 } && FamiliarDe(npc) is not null
            && _rng.Next(1, 101) < ProbHabilidadDeGolpe && FamiliarLanzaSobreCriatura(npc, victima)) return;
        // El intervalo de 3 s también entre criaturas (VB6 `NpcAtacaNpc`, SistemaCombate.bas:831): es el
        // MISMO `Contadores.Ataque`, así que pegarle a otra criatura le consume el turno igual.
        if (!IntervaloPermiteAtacar(npc)) return;
        // Criatura contra criatura: el rugido del atacante ANTES de la tirada (VB6 SistemaCombate.bas:843) y el
        // quejido de la víctima sólo si el golpe entra (:849).
        SonidoDeCriatura(_world, npc, npc.Template.Snd1);
        if (_rng.Next(1, 101) > Combat.HitChance(npc.Template.PoderAtaque, victima.Template.PoderEvasion))
            return;   // falló
        SonidoDeCriatura(_world, victima, victima.Template.Snd2);
        int rawDmg = npc.Template.MaxHit >= npc.Template.MinHit
            ? _rng.Next(npc.Template.MinHit, npc.Template.MaxHit + 1)
            : npc.Template.MinHit;
        int dmg = Math.Max(1, rawDmg - victima.Template.Def);
        victima.Hp = Math.Max(0, victima.Hp - dmg);
        _world.BroadcastToObservers(victima.Pos, ServerMessages.DamageOverHead(victima.CharIndex, dmg));
        FamiliarGolpeoA(npc, victima);
        // Y el que recibe el golpe se queda peleando con el familiar (regla de la casa FamiliarBusca). Sin
        // esto la presa cobraba sin enterarse y seguía con su IA de siempre — normalmente yendo hacia el
        // DUEÑO, que es justo lo que el familiar tenía que evitarle. Sólo se le pone objetivo al que no tiene
        // (`If TargetNPC = 0`, el mismo orden que usa CheckPets para no pisarle la pelea a nadie).
        if (FamiliarBuscaEnemigo && npc.OwnerCharIndex != 0 && victima.OwnerCharIndex == 0
            && victima.Hp > 0 && victima.TargetNpcCharIndex == 0)
        {
            victima.TargetNpcCharIndex = npc.CharIndex;
            victima.Movement = GameLimits.TipoAiNpcAtacaNpc;
        }
        if (victima.Hp == 0) MatarNpcSinDueno(victima);
    }

    /// <summary>El familiar le tira uno de sus hechizos a otra CRIATURA. Es la versión corta de
    /// <see cref="NpcLanzaUnHechizo"/>: una criatura no tiene equipo que le absorba magia ni cliente al que
    /// avisarle, así que queda el daño y la parálisis. Devuelve false —y entonces pega a mano— si el hechizo
    /// que salió no tiene efecto modelable sobre una criatura.</summary>
    private bool FamiliarLanzaSobreCriatura(Npc npc, Npc victima)
    {
        var lista = npc.Template.Spells!;
        var spell = _data.Spell((short)lista[_rng.Next(lista.Count)]);
        if (spell is null) return false;

        if (spell.IsParalyze || spell.IsImmobilize)
        {
            if (_tick < victima.ParalyzedUntilTick) return false;   // no se encadena, como en el original
            victima.ParalyzedUntilTick = _tick + ParalisisDeNpcTicks;
            _world.BroadcastToObservers(victima.Pos, ServerMessages.ChatOverHeadLocale(npc.CharIndex, spell.Index, 1));
            if (spell.Wav > 0)
                _world.BroadcastToObservers(victima.Pos, ServerMessages.PlayWave((short)spell.Wav, victima.Pos.X, victima.Pos.Y));
            return true;
        }
        if (spell.SubeHp != 2) return false;

        int dano = spell.MaxHp >= spell.MinHp ? _rng.Next(spell.MinHp, spell.MaxHp + 1) : spell.MinHp;
        victima.Hp = Math.Max(0, victima.Hp - dano);
        _world.BroadcastToObservers(victima.Pos, ServerMessages.ChatOverHeadLocale(npc.CharIndex, spell.Index, 1));
        if (spell.FxGrh > 0)
            _world.BroadcastToObservers(victima.Pos, ServerMessages.CreateFX(victima.CharIndex, (short)spell.FxGrh, (short)spell.FxLoops));
        if (spell.Wav > 0)
            _world.BroadcastToObservers(victima.Pos, ServerMessages.PlayWave((short)spell.Wav, victima.Pos.X, victima.Pos.Y));
        _world.BroadcastToObservers(victima.Pos, ServerMessages.DamageOverHead(victima.CharIndex, dano));
        if (victima.Hp == 0) MatarNpcSinDueno(victima);
        return true;
    }

    /// <summary>Muerte de una criatura a manos de OTRA criatura: no hay jugador al que darle exp, oro ni
    /// loot, así que sólo se la saca del mundo. El dueño se entera porque deja de verla.</summary>
    private void MatarNpcSinDueno(Npc victima)
    {
        _world.BroadcastToObservers(victima.Pos, ServerMessages.CharacterRemove(victima.CharIndex, faded: true));
        _world.RemoveNpc(victima.CharIndex);
    }

    private void NpcGolpeaA(Npc npc, Player player)
    {
        {
            // LAS CUATRO GUARDAS DEL ORIGINAL, adentro y no en el caller (VB6 SistemaCombate.bas:700-707).
            if (!PuedeSerAtacado(player)) return;
            // EL FAMILIAR QUE SABE MAGIA la tira en vez de pegar, como cualquier criatura con hechizos: sus
            // habilidades viajan en la ficha (`Template.Spells`), asi que el camino es el MISMO que ya usan
            // los NPCs magos -- con su dano, la resistencia del equipo y el efecto de estado. Va acá y no en
            // la fase de ataque porque a una criatura CON DUEÑO el original no le corre esa fase.
            //
            // Va ANTES del intervalo porque `NpcLanzaUnHechizo` lo consume él mismo, igual que en el VB6
            // (modHechizos.bas:100): el contador es UNO SOLO para golpe y hechizo, así que la criatura que
            // castea no pega también en la misma vuelta.
            if (npc.Template.Spells is { Count: > 0 } && FamiliarDe(npc) is not null
                && _rng.Next(1, 101) < ProbHabilidadDeGolpe && NpcLanzaUnHechizo(npc, player)) return;
            // EL INTERVALO DE 3 SEGUNDOS (VB6 SistemaCombate.bas:713: `If IntervaloPermiteAtacarNpc(...)
            // ... Else NpcAtacaUser = False : Exit Function`). Sale MUDA: ni rugido, ni tirada, ni skill.
            if (!IntervaloPermiteAtacar(npc)) return;
            // EL RUGIDO AL ATACAR (VB6 SistemaCombate.bas:730). Suena ANTES de la tirada de acierto, así que
            // la criatura ruge igual cuando erra — que es lo que hace el original y lo que se escucha en el
            // juego.
            SonidoDeCriatura(_world, npc, npc.Template.Snd1);
            double evMod = _data.EvasionMod.TryGetValue(player.Class, out var ev) ? ev : 1.0;
            long pe = Combat.PoderEvasion(player.Skills[GameLimits.SkillTacticas], player.Agility, player.Level, evMod);
            // NpcImpacto (SistemaCombate.bas:222) adds the shield's block/evasion when one is equipped.
            if (Equipment.EquippedOfCategory(player, _data, 4) is not null)
            {
                double shMod = _data.ShieldMod.TryGetValue(player.Class, out var sm) ? sm : 1.0;
                pe += Combat.PoderEvasionEscudo(player.Skills[GameLimits.SkillDefensa], shMod);
            }
            // The NPC's own attack power (PoderAtaque from NPCs.dat), NOT its evasion, drives its hit chance.
            if (_rng.Next(1, 101) > Combat.HitChance(npc.Template.PoderAtaque, pe))
            {
                // RECHAZO CON ESCUDO contra una CRIATURA (VB6 NpcImpacto, SistemaCombate.bas:254-270). Existía
                // sólo en el camino PvP; contra NPCs el golpe fallado terminaba en silencio. Son dos cosas que
                // faltaban: el aviso —y acá el original manda el LocaleMsg 73, no el ConsoleMsg que usa en PvP,
                // que es la misma asimetría del combate por parte del cuerpo— y el ENTRENAMIENTO de Defensa,
                // que contra criaturas es donde de verdad sube: nadie entrena la skill dejándose pegar por otro
                // jugador.
                if (Equipment.EquippedOfCategory(player, _data, 4) is not null)
                {
                    int probRechazo = Combat.ProbabilidadRechazoEscudo(
                        player.Skills[GameLimits.SkillDefensa], player.Skills[GameLimits.SkillTacticas]);
                    if (probRechazo > 0 && _rng.Next(1, 101) <= probRechazo)
                    {
                        _world.BroadcastToObservers(player.Pos,
                            ServerMessages.PlayWave(SndEscudo, (byte)player.Pos.X, (byte)player.Pos.Y));
                        player.Connection?.Send(ServerMessages.LocaleMsg(73));
                        SubirSkillPorUso?.Invoke(player, GameLimits.SkillDefensa);
                    }
                }
                // EL "FALLAS" (VB6 SistemaCombate.bas:756: `WriteChatOverHeadLocale(UserIndex,
                // Npclist(npcIndex).Char.CharIndex, 0, 2)`) — el cuerpo del `Else` del impacto, que no
                // estaba portado: la criatura que erra le pone un 0 flotante sobre la cabeza al jugador.
                player.Connection?.Send(ServerMessages.DamageOverHead(npc.CharIndex, 0));
                // SER ATACADO ENTRENA TÁCTICAS (VB6 NpcAtacaUser, SistemaCombate.bas:759): el SubirSkill va
                // DESPUÉS del If/Else de impacto, o sea que el defensor entrena tanto si el golpe entra como
                // si lo esquiva. Es LA manera de subir la evasión — el atacante nunca la sube (:218 entrena
                // la skill del arma, y sólo al acertar). Faltaba en las dos salidas de esta rutina.
                SubirSkillPorUso?.Invoke(player, GameLimits.SkillTacticas);
                return; // missed
            }

            // EL GOLPE QUE ENTRA SUENA Y SANGRA (VB6 SistemaCombate.bas:740-747): `PlayWave(SND_IMPACTO)` al
            // área y, si el jugador no está meditando ni navegando ni montado, `CreateFX(FXSANGRE)`. Los dos
            // faltaban: el PvE era mudo en el impacto y no salpicaba.
            _world.BroadcastToObservers(player.Pos,
                ServerMessages.PlayWave(SndImpacto, (byte)player.Pos.X, (byte)player.Pos.Y));
            if (!player.Meditating && !player.Navigating && !player.Mounted)
                _world.BroadcastToObservers(player.Pos, ServerMessages.CreateFX(player.CharIndex, FxSangre, 0));

            int rawDmg = npc.Template.MaxHit >= npc.Template.MinHit
                ? _rng.Next(npc.Template.MinHit, npc.Template.MaxHit + 1)
                : npc.Template.MinHit;
            // The player's equipment absorbs the blow (NpcDaño, SistemaCombate.bas:548-586): head→helmet,
            // body→armour(+shield). NpcDaño clamps damage below 1 up to 1 (even an exactly-absorbed hit).
            int zona = _rng.Next(1, 7); // RandomNumber(bCabeza=1, bTorso=6)
            long afterDef = Combat.AplicarDefensa(rawDmg, zona,
                Equipment.DefRange(Equipment.EquippedOfCategory(player, _data, 3)),
                Equipment.DefRange(Equipment.EquippedOfCategory(player, _data, 2)),
                Equipment.DefRange(Equipment.EquippedOfCategory(player, _data, 4)),
                (lo, hi) => hi > lo ? _rng.Next(lo, hi + 1) : lo,
                player.MetamorfosisExtraDef);   // ExtraDEF de la forma (regla de la casa; 0 si no está transformado)
            int dmg = (int)Math.Max(1, afterDef);
            player.Hp = Math.Max(0, player.Hp - dmg);
            // CON LA PARTE DEL CUERPO, como el original: la `zona` que acaba de decidir qué armadura absorbe
            // el golpe viaja al cliente y él arma "La criatura te ha pegado en la cabeza por N". Antes se
            // mandaba un texto nuestro sin la zona, así que la mecánica corría pero el jugador no la veía.
            player.Connection?.Send(ServerMessages.GolpeDeCriatura(zona, dmg));
            // El "-N" flotante sobre la cabeza (VB6 SistemaCombate.bas:590: WriteChatOverHeadLocale con el
            // charIndex del ATACANTE y modo 2). Va JUNTO al mensaje de texto, no en su lugar.
            player.Connection?.Send(ServerMessages.DamageOverHead(npc.CharIndex, dmg));
            // VB6 SistemaCombate.bas:749 (NpcAtacaUser): `Call NpcDaño(...)` y acto seguido `WriteUpdateHP`.
            // Éste era el ÚLTIMO call site del UpdateUserStats gordo en un camino caliente — y el más caliente
            // de todos, porque es el PvE: cada golpe que te pega una criatura mandaba 26 bytes en vez de 3.
            player.Connection?.Send(ServerMessages.UpdateHP((short)player.Hp));
            // VB6 SistemaCombate.bas:752: `If Npclist(npcIndex).Veneno > 0 Then Call NpcEnvenenarUser(...)`,
            // que envenena con un 30% (`RandomNumber(1,100) < 30`, MODULO_NPCs.bas:793) y avisa con el 182.
            // Son 7 criaturas del install; el port nunca leyó el campo, así que su veneno no existía.
            if (npc.Template.Veneno > 0 && !player.IsPoisoned(_tick) && _rng.Next(1, 101) < 30)
            {
                player.PoisonedUntilTick = long.MaxValue;
                player.Connection?.Send(ServerMessages.LocaleMsg(182));
            }
            FamiliarGolpeoA(npc, player);
            // Y acá —no cuando el amo ataca— es donde el original manda a las mascotas a pelear.
            MascotasDefiendenAlAmo(player, npc);
            if (player.Hp == 0)
            {
                // VB6 SistemaCombate.bas:606: "te mató la criatura X" (LocaleMsg 72) + UserDie -> cadáver donde cayó.
                player.Connection?.Send(ServerMessages.LocaleMsg(72, npc.Name));
                PacketDispatcher.KillPlayer(_world, _data, player);
            }
            // El cierre de NpcAtacaUser (:759): el golpe recibido entrena Tácticas — incluso el que mata,
            // que en el original también pasa por acá. La otra salida (el fallo) tiene su gemelo arriba.
            SubirSkillPorUso?.Invoke(player, GameLimits.SkillTacticas);
        }
    }

    /// <summary>VB6 <c>RestoreOldMovement</c> (AI_NPC.bas:494), al que caen las dos ramas de la IA cuando no
    /// encontraron a nadie a quien pegarle: el NPC se olvida de su agresor, y su hostilidad y su modo de
    /// movimiento vuelven a los de su ficha. Por eso el estado "provocado" dura poco — se apaga apenas no le
    /// queda nadie al lado. Y por eso mismo un /SEGUIR sobre una criatura sin dueño no se llega a notar: esto
    /// corre antes que la fase de movimiento y le saca el NPCDEFENSA en el mismo tick (ver GmNpcFollow).</summary>
    private static void RestoreOldMovement(Npc npc)
    {
        npc.AttackedBy = null;
        npc.Hostile = npc.OriginallyHostile;
        // El Movement se restaura SÓLO si está en el modo que /SEGUIR le puso. Es el único escritor de Movement
        // en runtime que tiene el port, así que restaurar es exactamente deshacerlo — y así no se le pisa el
        // modo a ningún NPC armado a mano que no haya declarado su OriginalMovement.
        if (npc.Movement == GameLimits.TipoAiNpcDefensa) npc.Movement = npc.OriginalMovement;
    }

}
