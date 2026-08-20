using System.Collections.Concurrent;

namespace CoverAO.Server.Game;

/// <summary>
/// The authoritative registry of connected players. Replaces the VB6 <c>UserList()</c> global
/// (which Protocol.bas referenced 1,040 times). Access is through methods, not a shared array,
/// and CharIndex allocation is centralized.
/// </summary>
public sealed class World
{
    private readonly ConcurrentDictionary<short, Player> _players = new();
    private int _nextCharIndex = 0;

    public EventBus Events { get; } = new();

    // Ground items keyed by tile. Only touched on the game-loop thread, so a plain dict is safe.
    private readonly Dictionary<WorldPos, (short ObjIndex, short Amount)> _ground = new();
    private readonly Dictionary<short, Npc> _npcs = new();

    public int OnlineCount => _players.Count;

    /// <summary>Tiles cuyo objeto es DECORADO FIJO del mapa (VB6 <c>MapData().ObjEsFijo</c>, que FileIO.bas:1329
    /// marca al cargar el .csm para todo <b>menos las puertas</b>). No se le mandan al cliente cuando entra al
    /// área: los dibuja de su propia copia del mapa. Las puertas SÍ, porque su estado cambia en runtime.</summary>
    private readonly HashSet<WorldPos> _groundFijo = new();

    /// <summary>Tiles con objeto, agrupados por mapa. El mundo entero tiene ~30 mil objetos y las dos consultas
    /// de abajo son SIEMPRE por mapa, así que sin este índice cada login y cada teleport recorría los treinta mil
    /// para quedarse con los del pueblo donde caés. Un jugador cruzando fronteras lo paga en cada salida.</summary>
    private readonly Dictionary<short, HashSet<WorldPos>> _groundPorMapa = new();

    public bool TryGetGround(WorldPos pos, out (short ObjIndex, short Amount) item) => _ground.TryGetValue(pos, out item);
    /// <summary>Pone un objeto en el suelo. <paramref name="esFijo"/> = decorado del .csm (VB6 ObjEsFijo).</summary>
    public void SetGround(WorldPos pos, short objIndex, short amount, bool esFijo = false)
    {
        _ground[pos] = (objIndex, amount);
        if (!_groundPorMapa.TryGetValue(pos.Map, out var delMapa)) _groundPorMapa[pos.Map] = delMapa = new HashSet<WorldPos>();
        delMapa.Add(pos);
        if (esFijo) _groundFijo.Add(pos); else _groundFijo.Remove(pos);
    }
    public void RemoveGround(WorldPos pos)
    {
        _ground.Remove(pos);
        _groundFijo.Remove(pos);
        if (_groundPorMapa.TryGetValue(pos.Map, out var delMapa) && delMapa.Remove(pos) && delMapa.Count == 0)
            _groundPorMapa.Remove(pos.Map);
    }
    /// <summary>Los objetos del suelo que este jugador debería ver desde donde está: los de su área que NO son
    /// decorado fijo del mapa (VB6 ModAreas.bas:274).</summary>
    /// <summary>Todos los ítems del suelo de un mapa, INCLUIDO el decorado colocado por el .csm. A diferencia de
    /// <see cref="GroundInView"/> no filtra <c>_groundFijo</c>: /MASSDEST necesita verlos porque el VB6 no
    /// distingue fijo de no-fijo (su único filtro es ItemNoEsDeMapa, por tipo de objeto).</summary>
    public IEnumerable<(WorldPos Pos, short ObjIndex, short Amount)> GroundOnMap(short map)
    {
        if (!_groundPorMapa.TryGetValue(map, out var delMapa)) yield break;
        foreach (var pos in delMapa)
            if (_ground.TryGetValue(pos, out var item)) yield return (pos, item.ObjIndex, item.Amount);
    }

    public IEnumerable<(WorldPos Pos, short ObjIndex, short Amount)> GroundInView(WorldPos from)
    {
        if (!_groundPorMapa.TryGetValue(from.Map, out var delMapa)) yield break;
        foreach (var pos in delMapa)
            if (!_groundFijo.Contains(pos) && InView(from, pos) && _ground.TryGetValue(pos, out var item))
                yield return (pos, item.ObjIndex, item.Amount);
    }

    private readonly object _charIndexGate = new();

    /// <summary>Un CharIndex LIBRE, en 1..32767. Nunca devuelve uno que ya esté en uso.
    ///
    /// <para>Antes era <c>(short)Interlocked.Increment(ref _nextCharIndex)</c>: un contador que no reusaba
    /// nada y se casteaba a <c>short</c>. Cada respawn de NPC consume uno (5404 NPCs, respawn a los ~5 s), así
    /// que con unas horas de uptime el cast SE VA A NEGATIVO y después DA LA VUELTA. Al repetirse un índice,
    /// <c>Add</c> hacía <c>_players[p.CharIndex] = p</c> y PISABA EN SILENCIO a un jugador conectado: ese
    /// jugador desaparecía de <c>_players</c>, así que no lo alcanzaba el autosave ni el guardado del apagado,
    /// y perdía todo lo hecho desde el último guardado sin que nadie se enterara.</para>
    ///
    /// <para>El espacio de índices es COMPARTIDO entre jugadores y NPCs (los dos salen de acá), por eso se
    /// consultan los dos mapas. En la práctica devuelve al primer intento: con 5404 NPCs la ocupación es del
    /// 16% del rango.</para></summary>
    public short AllocateCharIndex()
    {
        lock (_charIndexGate)
        {
            for (int intentos = 0; intentos < short.MaxValue; intentos++)
            {
                if (++_nextCharIndex > short.MaxValue) _nextCharIndex = 1;   // 1..32767: nunca 0 ni negativo
                short ci = (short)_nextCharIndex;
                if (!_players.ContainsKey(ci) && !_npcs.ContainsKey(ci)) return ci;
            }
        }
        throw new InvalidOperationException("No quedan CharIndex libres (32767 en uso entre jugadores y NPCs).");
    }

    /// <summary>El PICO histórico de jugadores conectados a la vez (VB6 <c>recordusuarios</c>, que TCP.bas:947
    /// actualiza con `If NumUsers > recordusuarios`). Se lo muestra el <c>/ONLINE</c> a cualquiera, así que no
    /// es una métrica interna: es parte de lo que el jugador ve. Persiste en <c>Misc/RECORD.json</c>.</summary>
    public int RecordUsuarios { get; set; }

    public void Add(Player p)
    {
        _players[p.CharIndex] = p;
        if (_players.Count > RecordUsuarios) RecordUsuarios = _players.Count;
    }

    public bool TryGet(short charIndex, out Player? player)
    {
        var ok = _players.TryGetValue(charIndex, out var p);
        player = p;
        return ok;
    }

    public Player? Remove(short charIndex)
    {
        _players.TryRemove(charIndex, out var p);
        return p;
    }

    /// <summary>Los jugadores conectados, SIN copiar la colección.
    ///
    /// <para>Era <c>_players.Values</c>, y en un <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey,TValue}"/>
    /// esa propiedad NO devuelve un enumerador: toma los 4×NumProc locks internos, copia todos los valores a
    /// una <c>List</c> nueva y la envuelve en un <c>ReadOnlyCollection</c>. Dos asignaciones y una copia O(n)
    /// POR ACCESO. Y se accede ~10.000 veces por segundo: la IA lo consulta por cada NPC que persigue
    /// (<c>NpcIrUsuarioCercano</c> corre sobre 5043 de los 5404) más los broadcasts de cada paso y cada golpe.
    /// El costo de tomar los locks se paga aunque no haya NADIE conectado — de ahí el pico de 1,5 ms medido
    /// con cero jugadores.</para>
    ///
    /// <para>Iterar el diccionario directo usa el enumerador lock-free y no asigna nada. Es válido con
    /// escrituras concurrentes: puede ver o no una entrada agregada durante el recorrido, que es exactamente
    /// la garantía que el código ya asumía con la copia.</para></summary>
    public IEnumerable<Player> Players
    {
        get { foreach (var kv in _players) yield return kv.Value; }
    }

    /// <summary>Find an online player by (case-insensitive) name.</summary>
    public Player? PlayerByName(string name)
    {
        foreach (var kv in _players)
            if (string.Equals(kv.Value.Name, name, StringComparison.OrdinalIgnoreCase)) return kv.Value;
        return null;
    }

    /// <summary>The player standing on a tile, if any (linear scan — fine at slice scale).</summary>
    public Player? PlayerAt(WorldPos pos)
    {
        foreach (var (_, p) in _players)
            if (p.Pos == pos) return p;
        return null;
    }

    // --- Cola de consultas a los GMs (VB6 `Ayuda`, una instancia de ModCola.cls) ---
    // Cada elemento es el string que arma ModCola.Push: "[NOMBRE]-HH:MM:SS-Tipo=". Lo llena /GM
    // (HandleGMRequest), lo lista /SHOW SOS, saca uno SOSDONE y la vacía /BORRAR SOS. Se guarda el nombre
    // aparte porque los dos comparadores del original (Existe y Quitar) buscan el nombre DENTRO del string
    // formateado y lo hacen mal: `mid$(elemento, 10, ...)` cae en la hora, no en el nombre, así que en el VB6
    // vivo `Existe` siempre da False y `Quitar` nunca quita nada. Acá los dos funcionan: es el criterio ya
    // usado en el resto del port (el algoritmo está escrito y un bug de indexado lo desactiva), y está anotado
    // en fase5/GAPS-CODIGO-MUERTO.md §4k.
    private readonly List<(string Name, string Entry)> _help = new();

    /// <summary>¿Ya hay una consulta pendiente de este personaje? (VB6 Ayuda.Existe).</summary>
    public bool HelpExists(string name) =>
        _help.Any(h => string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Encola una consulta con el formato exacto del original: <c>[NOMBRE]-HH:MM:SS-Tipo=</c>.</summary>
    public void HelpPush(string name, byte tipo) =>
        _help.Add((name, $"[{name.ToUpperInvariant()}]-{DateTime.Now:HH:mm:ss}-{tipo}="));

    /// <summary>Saca la consulta de ese personaje, si la hay (VB6 Ayuda.Quitar).</summary>
    public bool HelpRemove(string name)
    {
        int i = _help.FindIndex(h => string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase));
        if (i < 0) return false;
        _help.RemoveAt(i);
        return true;
    }

    /// <summary>Las consultas encoladas, en orden de llegada (VB6 Ayuda.VerElemento 1..Longitud).</summary>
    public IReadOnlyList<string> HelpEntries => _help.Select(h => h.Entry).ToArray();

    /// <summary>Vacía la cola (VB6 Ayuda.Reset). Devuelve cuántas había.</summary>
    public int HelpClear() { int n = _help.Count; _help.Clear(); return n; }

    // --- Cuenta regresiva (VB6 CuentaRegresivaTimer / mapasegundos, Declares.bas:54 + General.bas:36) ---
    // Las setea el comando CuentaRegresiva y las consume el reloj del server, que cada segundo manda
    // LocaleMsg(434, N-1) —al mapa si CountdownMap > 0, a todos si es 0— y al terminar LocaleMsg(435).
    /// <summary>Segundos que faltan del conteo; 0 = sin conteo en curso.</summary>
    public byte CountdownRemaining { get; set; }
    /// <summary>Mapa al que va el conteo, o 0 para todo el servidor.</summary>
    public byte CountdownMap { get; set; }

    // --- Guilds (modGuilds) ---
    private readonly Dictionary<string, Guild> _guilds = new(StringComparer.OrdinalIgnoreCase);

    public Guild? GuildByName(string name) => _guilds.GetValueOrDefault(name);
    public void RemoveGuild(string name) => _guilds.Remove(name);
    /// <summary>All guilds (for persistence).</summary>
    public IEnumerable<Guild> AllGuilds => _guilds.Values;
    /// <summary>Repopulates the guild registry (e.g. from persistence at startup).</summary>
    public void LoadGuilds(IEnumerable<Guild> guilds)
    {
        _guilds.Clear();
        foreach (var g in guilds) _guilds[g.Name] = g;
    }

    /// <summary>Creates a guild led by the founder if the name is free; returns null if it exists.</summary>
    public Guild? CreateGuild(string name, string leaderName, string description)
    {
        if (string.IsNullOrWhiteSpace(name) || _guilds.ContainsKey(name)) return null;
        var g = new Guild { Name = name, LeaderName = leaderName, Description = description };
        g.Members.Add(leaderName);
        _guilds[name] = g;
        return g;
    }

    /// <summary>Online players belonging to the named guild.</summary>
    public IEnumerable<Player> GuildMembersOnline(string guildName)
    {
        foreach (var (_, p) in _players)
            if (!string.IsNullOrEmpty(p.GuildName) && string.Equals(p.GuildName, guildName, StringComparison.OrdinalIgnoreCase))
                yield return p;
    }

    // --- Mail (correo) — per-recipient mailboxes, delivered at login (session-scoped registry) ---
    private readonly Dictionary<string, List<MailEntry>> _mailboxes = new(StringComparer.OrdinalIgnoreCase);

    public void DeliverMail(string recipient, MailEntry entry)
    {
        if (!_mailboxes.TryGetValue(recipient, out var box)) _mailboxes[recipient] = box = new();
        box.Add(entry);
    }


    /// <summary>El buzón SIN vaciarlo, que es como lo trata el original: las cartas se quedan hasta que el
    /// jugador borra cada una (<c>ResetCorreos</c>). Devuelve la lista viva — mutarla muta el buzón.</summary>
    public List<MailEntry> PeekMail(string recipient) =>
        _mailboxes.TryGetValue(recipient, out var box) ? box : new List<MailEntry>();

    /// <summary>Deja el buzón sin cartas (y sin entrada en el diccionario, para que no se persista vacío).</summary>
    public void DropMailboxIfEmpty(string recipient)
    {
        if (_mailboxes.TryGetValue(recipient, out var box) && box.Count == 0) _mailboxes.Remove(recipient);
    }

    /// <summary>All non-empty mailboxes (for persistence).</summary>
    public IReadOnlyDictionary<string, List<MailEntry>> AllMailboxes => _mailboxes;

    /// <summary>Repopulates the mailboxes (e.g. from persistence at startup).</summary>
    public void LoadMailboxes(Dictionary<string, List<MailEntry>> boxes)
    {
        _mailboxes.Clear();
        foreach (var kv in boxes)
            if (kv.Value.Count > 0) _mailboxes[kv.Key] = kv.Value;
    }

    // --- NPCs ---
    //
    // ÍNDICE POR TILE. `NpcAt` era un barrido lineal de todos los NPCs, y con el mundo real poblado (5404
    // criaturas) eso costaba caro por un camino que no se ve venir: la IA de persecución (`NpcIrUsuarioCercano`,
    // el Movement de 4704 de esas criaturas) termina en "no hay ningún usuario cerca, a bailar" y tira un paso
    // al azar con probabilidad 1/11. O sea ~430 pasos por segundo, y CADA paso llamaba a `NpcAt` para ver si el
    // destino estaba libre: 2,3 millones de comparaciones por segundo con el servidor VACÍO, y creciendo con el
    // CUADRADO de la población. El índice lo baja a un lookup.
    //
    // Es una LISTA por tile y no un solo NPC porque dos criaturas SÍ pueden terminar en el mismo tile: el
    // respawn (`SpawnNow`) devuelve al NPC a su punto de origen sin mirar si mientras tanto se le paró otro
    // encima. Con un `Dictionary<WorldPos, Npc>` uno de los dos desaparecería del índice y el tile quedaría
    // "libre" para siempre. Con la lista, `NpcAt` devuelve uno de los dos — lo mismo que devolvía el barrido.
    private readonly Dictionary<WorldPos, List<Npc>> _npcsByPos = new();
    /// <summary>Los NPCs de cada mapa. El mundo tiene 5404 criaturas repartidas en 322 mapas —17 de promedio,
    /// 60 en el peor— y todas las consultas de visibilidad son por mapa. Sin esto,
    /// <c>RefreshAreaVisibility</c> recorría las 5404 en CADA PASO de CADA jugador.</summary>
    private readonly Dictionary<short, List<Npc>> _npcsPorMapa = new();
    // NPCs con vencimiento (invocaciones). Se mantiene aparte para que el reloj no tenga que filtrar los 5404
    // en cada vuelta buscando los dos o tres que expiran.
    private readonly List<Npc> _npcsConVencimiento = new();

    public IEnumerable<Npc> Npcs => _npcs.Values;
    public int NpcCount => _npcs.Count;
    /// <summary>Los NPCs que se despawnean solos (invocaciones). Es la lista VIVA: el reloj remueve mientras la
    /// recorre, y por eso la recorre al revés por índice.</summary>
    public IReadOnlyList<Npc> NpcsConVencimiento => _npcsConVencimiento;

    public void AddNpc(Npc npc)
    {
        // Si ese CharIndex ya estaba ocupado hay que SACAR al anterior de los índices antes de pisarlo. Sin
        // esto quedaría indexado para siempre sin estar en `_npcs`: un fantasma que bloquea un tile donde no
        // hay nada y al que nadie puede matar. `AllocateCharIndex` es un contador que se castea a `short`, así
        // que con suficiente uptime (respawns) da la vuelta y los índices se repiten — sin índice el choque
        // pasaba desapercibido, con índice deja basura permanente.
        if (_npcs.TryGetValue(npc.CharIndex, out var anterior) && !ReferenceEquals(anterior, npc))
        {
            DesindexarNpc(anterior);
            if (anterior.ExpireAtTick > 0) _npcsConVencimiento.Remove(anterior);
        }
        _npcs[npc.CharIndex] = npc;
        IndexarNpc(npc);
        if (npc.ExpireAtTick > 0) _npcsConVencimiento.Add(npc);
    }

    /// <summary>Saca una criatura del mundo.
    ///
    /// <para>Si tenía dueño le libera el lugar de mascota, y va acá a propósito y no en cada uno de los siete
    /// lugares que sacan criaturas: el original hace lo mismo —<c>ResetNpcMainInfo</c> (MODULO_NPCs.bas:289)
    /// llama a <c>QuitarMascota</c>, y por ahí pasan tanto <c>QuitarNPC</c> como <c>MuereNpc</c>—, así que la
    /// muerte, el vencimiento de una invocación y el despawn liberan el cupo por la MISMA línea. Repartir la
    /// limpieza entre los call sites es la forma segura de olvidarse de uno.</para></summary>
    public void RemoveNpc(short charIndex)
    {
        if (!_npcs.Remove(charIndex, out var npc)) return;
        DesindexarNpc(npc);
        if (npc.ExpireAtTick > 0) _npcsConVencimiento.Remove(npc);
        if (npc.OwnerCharIndex != 0 && TryGet(npc.OwnerCharIndex, out var amo)) amo!.QuitarMascota(charIndex);
    }

    /// <summary>Mueve un NPC a otro tile manteniendo el índice. <b>Es el único camino legítimo</b>: asignarle
    /// <c>Pos</c> a mano deja el índice apuntando al tile viejo y el NPC se vuelve invisible para
    /// <see cref="NpcAt"/> (los demás le caminan por encima) mientras bloquea un tile donde ya no está.</summary>
    public void MoveNpc(Npc npc, WorldPos dest)
    {
        // El índice POR MAPA sólo se toca si el mapa cambió: hoy un NPC nunca cruza de mapa (MoveNpc siempre
        // arma el destino con `npc.Pos.Map`), así que en la práctica esto es un Remove+Add en el índice por tile
        // y nada más. La rama de cambio de mapa está igual porque el método no puede depender de eso.
        if (npc.Pos.Map != dest.Map) { DesindexarNpc(npc); npc.Pos = dest; IndexarNpc(npc); return; }
        if (_npcsByPos.TryGetValue(npc.Pos, out var enElTileViejo))
        {
            enElTileViejo.Remove(npc);
            if (enElTileViejo.Count == 0) _npcsByPos.Remove(npc.Pos);
        }
        npc.Pos = dest;
        if (!_npcsByPos.TryGetValue(dest, out var enElTile)) _npcsByPos[dest] = enElTile = new List<Npc>(1);
        enElTile.Add(npc);
    }

    /// <summary>Los NPCs parados en un mapa. Vacío si no hay ninguno. <b>Es la lista viva del índice</b>: quien
    /// vaya a remover NPCs mientras recorre tiene que materializarla antes.</summary>
    public IReadOnlyList<Npc> NpcsOnMap(short map) =>
        _npcsPorMapa.TryGetValue(map, out var delMapa) ? delMapa : Array.Empty<Npc>();

    private void IndexarNpc(Npc npc)
    {
        if (!_npcsByPos.TryGetValue(npc.Pos, out var enElTile)) _npcsByPos[npc.Pos] = enElTile = new List<Npc>(1);
        enElTile.Add(npc);
        if (!_npcsPorMapa.TryGetValue(npc.Pos.Map, out var delMapa)) _npcsPorMapa[npc.Pos.Map] = delMapa = new List<Npc>();
        delMapa.Add(npc);
    }

    private void DesindexarNpc(Npc npc)
    {
        if (_npcsByPos.TryGetValue(npc.Pos, out var enElTile))
        {
            enElTile.Remove(npc);
            if (enElTile.Count == 0) _npcsByPos.Remove(npc.Pos);
        }
        if (_npcsPorMapa.TryGetValue(npc.Pos.Map, out var delMapa))
        {
            delMapa.Remove(npc);
            if (delMapa.Count == 0) _npcsPorMapa.Remove(npc.Pos.Map);
        }
    }

    public bool HasNpc(short charIndex) => _npcs.ContainsKey(charIndex);
    /// <summary>El NPC de ese CharIndex, o null. Es el diccionario que ya existe: buscarlo con un
    /// <c>FirstOrDefault</c> sobre los 5404 era recorrer el mundo para leer una clave.</summary>
    public Npc? NpcByIndex(short charIndex) => _npcs.GetValueOrDefault(charIndex);

    public Npc? NpcAt(WorldPos pos) =>
        _npcsByPos.TryGetValue(pos, out var enElTile) && enElTile.Count > 0 ? enElTile[0] : null;

    /// <summary>Send a prebuilt packet to every connected player (naive broadcast for the slice).</summary>
    public void Broadcast(byte[] packet)
    {
        foreach (var (_, p) in _players)
            p.Connection?.Send(packet);
    }

    /// <summary>Broadcast to everyone except the given character (e.g. self-originated events).</summary>
    public void BroadcastExcept(short exceptCharIndex, byte[] packet)
    {
        foreach (var (_, p) in _players)
            if (p.CharIndex != exceptCharIndex)
                p.Connection?.Send(packet);
    }

    // --- Area-scoped visibility (ModAreas.bas). The world is tiled into 9×9 "areas"; a character sees
    // the 3×3 block of areas around its own (a 27×27 tile window). Two entities are mutually visible iff
    // their area-block coords differ by ≤1 on each axis — symmetric, and it only flips at block crossings.
    public const int AreaBlock = 9;

    public static bool InView(WorldPos a, WorldPos b) =>
        a.Map == b.Map
        && Math.Abs(a.X / AreaBlock - b.X / AreaBlock) <= 1
        && Math.Abs(a.Y / AreaBlock - b.Y / AreaBlock) <= 1;

    /// <summary>Connected players who can currently see <paramref name="pos"/>.</summary>
    public IEnumerable<Player> Observers(WorldPos pos)
    {
        foreach (var (_, p) in _players)
            if (InView(p.Pos, pos)) yield return p;
    }

    /// <summary>Send a packet only to players who can see <paramref name="pos"/> (optionally excluding one).</summary>
    public void BroadcastToObservers(WorldPos pos, byte[] packet, short exceptCharIndex = 0)
    {
        foreach (var (_, p) in _players)
            if (p.CharIndex != exceptCharIndex && InView(p.Pos, pos))
                p.Connection?.Send(packet);
    }

    /// <summary>Manda un paquete a TODOS los usuarios de un mapa, vean o no el tile (VB6
    /// <c>SendData(SendTarget.toMap, ...)</c> → <c>SendToMap</c>, modSendData.bas:515, que itera
    /// <c>ConnGroups(Map)</c> SIN filtro de área). Lo necesita <c>BlockPosition</c>: el cliente mantiene su
    /// propio mapa de bloqueos, así que una puerta que se abre le importa aunque esté del otro lado del pueblo.</summary>
    public void BroadcastToMap(short map, byte[] packet)
    {
        foreach (var (_, p) in _players)
            if (p.Pos.Map == map) p.Connection?.Send(packet);
    }
}
