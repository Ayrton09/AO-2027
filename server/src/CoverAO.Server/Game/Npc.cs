using CoverAO.Server.Data;

namespace CoverAO.Server.Game;

/// <summary>Una ranura del stock de un mercader: QUÉ y CUÁNTO, y nada más.
///
/// <para><b>Acá había un <c>Price</c> y era una impresora de oro.</b> El precio se guardaba en la ranura, y
/// al agotarse el stock la ranura se vaciaba conservándolo: el siguiente objeto que cayera ahí se vendía al
/// precio del ANTERIOR. Vender una leña, recomprarla y vender un lingote en esa ranura dejaba el lingote a
/// precio de leña — medido: +49 monedas por vuelta con el inventario intacto, repetible sin límite.</para>
///
/// <para>El original no tiene el campo: la ranura es <c>ObjIndex</c> + <c>Amount</c> y el precio se deriva de
/// <c>ObjData</c> en cada uso — <c>Precio = CLng((ObjData(...Object(slot).ObjIndex).Valor / Descuento(...)
/// * Cantidad) + 0.5)</c> (Comercio.bas:89). Un precio que se guarda es un precio que se puede quedar viejo;
/// derivarlo no.</para></summary>
public struct MerchantSlot
{
    public short ObjIndex;
    public short Amount;
}

/// <summary>A live NPC instance in the world, spawned from an <see cref="NpcInfo"/> template.</summary>
public sealed class Npc
{
    public required short CharIndex { get; init; }
    /// <summary>La ficha de la criatura. Tiene SET y no sólo init porque el familiar sube de nivel: sus barras
    /// salen de la plantilla, así que crecer es cambiarle la ficha por una con los números nuevos (un `with`
    /// sobre el record). Para cualquier otra criatura se fija una vez y no se toca.</summary>
    public required NpcInfo Template { get; set; }

    /// <summary>
    /// POOL DE EXPERIENCIA de esta criatura (VB6 <c>flags.ExpCount</c>, nace en <c>MODULO_NPCs.bas:998</c> con
    /// <c>= .GiveEXP</c>). El original NO da toda la experiencia al que remata: la va GOTEANDO por golpe en
    /// proporción al daño (<c>CalcularDarExp</c>, SistemaCombate.bas:1992) descontándola de acá, y al morir le
    /// entrega al matador lo que haya quedado (<c>MODULO_NPCs.bas:129-144</c>).
    ///
    /// El total se conserva: quien hace todo el daño termina con la misma experiencia que antes. Lo que cambia
    /// es que el que pelea y no remata ya no se va con las manos vacías — y que un elemental ajeno pegándole
    /// no le regala la parte a nadie.
    ///
    /// Arranca en -1 y se resuelve contra la plantilla en el primer uso para no tener que tocar los cinco
    /// lugares donde se construye un Npc (mundo, fixture, arena, invocación, doma).
    /// </summary>
    private long _expCount = -1;
    public long ExpCount
    {
        get => _expCount < 0 ? Template.GiveExp : _expCount;
        set => _expCount = value < 0 ? 0 : value;
    }

    /// <summary>
    /// El pozo con la TASA DEL SERVER (<c>Exp</c> del Server.ini) adentro. El original la hornea en la criatura
    /// al cargarla (<c>.GiveEXP = GiveEXP * Expc</c>, MODULO_NPCs.bas:996), así que el pozo nace multiplicado y
    /// cada golpe redondea sobre el número grande. Acá se resuelve en el primer uso, igual que
    /// <see cref="ExpCount"/>.
    ///
    /// <para>La tasa NO se mete en la ficha: de <c>GiveExp</c> cuelgan también el tiempo de respawn y cómo mide
    /// un bot el peligro de una criatura, y en un server de ×200 los bots verían todo como mortal.</para>
    /// </summary>
    public long PozoDeExperiencia(int tasa) => _expCount < 0 ? Template.GiveExp * Math.Max(1, tasa) : _expCount;
    public WorldPos Pos { get; set; }
    public byte Heading { get; set; } = 3;
    public int Hp { get; set; }

    /// <summary>Non-null for merchant NPCs; the goods they sell.</summary>
    public List<MerchantSlot>? Stock { get; set; }
    public bool IsMerchant => Stock is not null;

    /// <summary>True for banker NPCs (open the vault).</summary>
    public bool IsBanker { get; set; }

    /// <summary>True for a Revividor (sacerdote): al hacerle doble-clic, revive al jugador muerto cercano
    /// (VB6 eNPCType.Revividor).</summary>
    public bool IsReviver { get; set; }

    /// <summary>VETERINARIA (<c>NPCs.dat NpcType = 11</c>, <c>eNPCType.Veterinarias</c>). En el original el rol
    /// está declarado y no lo usa NADIE: la constante aparece una sola vez en todo el servidor, en su propia
    /// línea del enum. Pero el NPC existe, tiene 27 apariciones en el mundo y su descripción ya dice para qué
    /// era — "Si tu familiar o mascota se encuentra en mal estado, acércamelo". Acá se le da ese trabajo.</summary>
    public bool IsVeterinaria { get; set; }

    /// <summary>El PIRATA que cobra el pasaje (VB6 <c>eNPCType.transportadores</c> = 7). No abre ninguna
    /// ventana: hay que USAR el pasaje teniéndolo clickeado, y él es el gate. Son 10 en el install.</summary>
    public bool IsTransportador { get; set; }

    /// <summary>Trade of a workbench NPC (a Fragua/carpintería/…): "blacksmith" | "carpenter" | "tailor" |
    /// "alchemy". Double-clicking it sends that trade's craftable list (BlacksmithWeapons/etc). Null = not one.</summary>
    public string? WorkbenchTrade { get; set; }

    /// <summary>True for workbench (crafting-station) NPCs.</summary>
    public bool IsWorkbench => WorkbenchTrade is not null;

    /// <summary>Tameable-creature names a trainer NPC offers (null = not a trainer). Double-clicking it
    /// sends TrainerCreatureList.</summary>
    public List<string>? TrainerCreatures { get; set; }
    /// <summary>VB6 <c>Npclist(x).MaestroNpc</c>: la criatura que spawneo un ENTRENADOR queda colgada de EL,
    /// no de un jugador. Sirve para el tope de 7 y para que no cuente en el cupo de nadie.</summary>
    public short MasterNpcCharIndex { get; set; }
    public bool IsTrainer => TrainerCreatures is not null;

    /// <summary>Modo de movimiento, de su ficha de NPCs.dat (VB6 <c>TipoAI</c>, AI_NPC.bas:32 — el
    /// <c>Select Case .Movement</c> de NPCAI). Los que importan acá:
    /// <list type="bullet">
    /// <item><b>0</b> = <c>NpcMaloAtacaUsersBuenos</c> → <c>IrUsuarioCercano</c>: camina hacia el usuario que
    /// vea, SIN mirar si es hostil. Es el default de hecho: el VB6 lee <c>val(GetValue(...))</c>, así que los
    /// NPCs sin la clave (el Aldeano entre ellos) caen acá.</item>
    /// <item><b>1</b> = <c>ESTATICO</c>: no se mueve.</item>
    /// <item><b>3</b> = <c>MueveAlAzar</c>: deambula. En este install no lo es NINGUNO.</item>
    /// </list>
    /// Se copia del template al spawnear, pero el fixture lo fija a mano: sus NPCs salen de plantillas
    /// genéricas (Aldeano/Murciélago/Lobo) que son Movement=0, y con la IA fiel se pondrían a caminar.</summary>
    public int Movement { get; set; }

    /// <summary>Hostilidad ACTUAL (VB6 <c>.Hostile</c>): un hostil le pega a cualquier jugador que tenga al
    /// lado. Es DINÁMICA — al atacarlo, <c>NPCAtacado</c> la prende (así el aldeano provocado se defiende, y de
    /// paso le pega a cualquier tercero que esté al lado, no sólo a su agresor), y <c>RestoreOldMovement</c> la
    /// devuelve a <see cref="OriginallyHostile"/> en cuanto no le queda nadie adyacente.</summary>
    public bool Hostile { get; set; }

    /// <summary>Modo de movimiento ORIGINAL, el de su ficha (VB6 <c>flags.OldMovement</c>). Es a lo que vuelve
    /// <c>RestoreOldMovement</c> — hace falta porque <see cref="Movement"/> SÍ cambia en runtime: /SEGUIR lo pone
    /// en <c>NPCDEFENSA</c>. Lo fija el spawn y lo re-anota <see cref="ToggleFollow"/> justo antes de pisarlo,
    /// así el valor es correcto incluso para un NPC armado a mano (fixture, invocaciones, tests) que no lo
    /// declare.</summary>
    public int OriginalMovement { get; set; }

    /// <summary>Está en modo "seguime" (VB6 <c>flags.Follow</c>, prendido por /SEGUIR). Es un toggle: el segundo
    /// /SEGUIR sobre la misma criatura lo apaga y le devuelve movimiento y hostilidad.</summary>
    public bool Follow { get; set; }

    /// <summary>VB6 <c>DoFollow</c> (MODULO_NPCs.bas:1123), el toggle que dispara /SEGUIR: la criatura pasa a
    /// perseguir a <paramref name="quien"/> —anotado en <see cref="AttackedBy"/>, que es el mismo campo que usa
    /// la IA para saber a quién devolverle el golpe—, se vuelve no hostil y cambia a <c>NPCDEFENSA</c>. El
    /// segundo llamado la devuelve a su ficha. En las dos direcciones le saca la parálisis, como el original.
    /// Vive acá y no en el handler para que el estado quede consistente en un solo lugar.</summary>
    public void ToggleFollow(string quien)
    {
        if (Follow)
        {
            AttackedBy = null;
            Follow = false;
            Movement = OriginalMovement;
            Hostile = OriginallyHostile;
        }
        else
        {
            AttackedBy = quien;
            Follow = true;
            OriginalMovement = Movement;   // lo que tenía justo antes: a esto vuelve el reset de la IA
            Movement = GameLimits.TipoAiNpcDefensa;
            Hostile = false;
        }
        ParalyzedUntilTick = 0;   // el original le baja Inmovilizado/Paralizado/Contadores.Paralisis
        InmovilizadaHastaTick = 0;
    }

    /// <summary>Hostilidad ORIGINAL, la de su ficha de NPCs.dat (VB6 <c>flags.OldHostil</c>, guardada al
    /// cargarlo: MODULO_NPCs.bas:994). Es a la que vuelve <c>RestoreOldMovement</c> cuando se le acaba la
    /// pelea. Es también la que decide si PERSIGUE: sólo una criatura hostil de verdad sale a cazar. Un
    /// pacífico provocado no persigue — el VB6 le pone <c>Movement = NPCDEFENSA</c> (<c>SeguirAgresor</c>),
    /// que en los hechos no lo mueve, porque el mismo tick le restaura el Movement apenas el agresor deja de
    /// estar adyacente.</summary>
    public bool OriginallyHostile { get; set; }

    /// <summary>Nombre del jugador que lo atacó por última vez (VB6 <c>flags.AttackedBy</c>, seteado en
    /// NPCAtacado, Modulo_UsUaRiOs.bas:1106). Es lo ÚNICO que hace pegar a un NPC no hostil: su IA
    /// (<c>HostilBuenoAI</c>, AI_NPC.bas:451) sólo devuelve el golpe al agresor y a nadie más. Se limpia
    /// cuando el agresor deja de estar adyacente (VB6 <c>RestoreOldMovement</c>).</summary>
    public string? AttackedBy { get; set; }

    /// <summary>CharIndex of the player who summoned this NPC (0 = none). Summons don't attack their owner.</summary>
    public short OwnerCharIndex { get; set; }

    /// <summary>El jugador que se APROPIÓ de esta criatura por pegarle (VB6 <c>Npclist().Owner</c>). <b>No es
    /// lo mismo que <see cref="OwnerCharIndex"/></b>, que es el amo de una MASCOTA (<c>MaestroUser</c>): esto
    /// es la pertenencia temporal de una criatura salvaje, la que decide a quién persigue y a quién le deja
    /// pegarle. Dura <c>[TIMERS] IntervaloOwnedNpc</c> desde el último golpe y se limpia al morir.</summary>
    public short ApropiadoPor { get; set; }

    /// <summary>El NPC al que esta criatura le está pegando (VB6 <c>Npclist().TargetNPC</c>). Se lo pone
    /// <c>CheckPets</c> (SistemaCombate.bas:630) cuando el AMO ataca a alguien: así una mascota pelea lo
    /// que pelea su dueño. 0 = ninguno.</summary>
    public short TargetNpcCharIndex { get; set; }

    /// <summary>¿Se puede atacar? (NPCs.dat "Attackable"; VB6 NpcAtacable, SistemaCombate.bas:1737). Los NPCs
    /// de servicio (sacerdote/banquero/guardias/veterinaria…) tienen Attackable=0 y rechazan el ataque.
    /// Las invocaciones son siempre atacables (default true). </summary>
    public bool Attackable { get; set; } = true;

    /// <summary>Enlistador de facción (VB6 eNPCType.facciones=5): 0 = no lo es, 5 = Armada Real, 4 = Caos.
    /// Doble-clic sobre él permite enlistarse (HandleEnlist) o cobrar recompensa/ascender (HandleReward).</summary>
    public byte FactionEnlist { get; set; }

    /// <summary>NPC CONVERTIDOR (VB6 eNPCType.Convertidores, Acciones.bas:225): el que le da la ciudadanía a un
    /// renegado. Guarda el status civil que otorga — <see cref="Player.StatusCiudadano"/> (imperial, VB6
    /// EntrarImperial) o <see cref="Player.StatusRepublicano"/> (VB6 EntrarRepublica). 0 = no es convertidor.</summary>
    public byte FactionConvert { get; set; }

    /// <summary>Tick del game-loop en el que esta invocación expira y se despawnea sola (0 = no expira).
    /// VB6 <c>Contadores.TiempoExistencia</c>, inicializado con <c>IntervaloInvocacion</c> (Server.ini = 1001).</summary>
    public long ExpireAtTick { get; set; }

    /// <summary>El tile donde spawneó (VB6 <c>Npclist().StartPos</c>). Lo mira <c>LookatTile</c>: a una criatura
    /// que se corrió de ahí, el click izquierdo le saca además su diálogo sobre la cabeza.</summary>
    public WorldPos StartPos { get; set; }

    /// <summary>Tile anterior (VB6 <c>Npclist().oldPos</c>). Lo usa <c>FindDirection</c> en las diagonales para
    /// NO retroceder: si un eje lo devolvería al tile del que viene, prefiere el otro. Sin esto un NPC que
    /// bordea un obstáculo puede quedar oscilando entre dos tiles.</summary>
    public WorldPos OldPos { get; set; }

    /// <summary>
    /// Tick hasta el que la criatura está PARALIZADA (VB6 <c>.flags.Paralizado</c> + <c>.Contadores.Paralisis</c>,
    /// seteados por HechizoEstadoNPC en modHechizos.bas:1546 y por el golpe que paraliza). Mientras corre no se le
    /// corre la IA: ni camina, ni pega, ni castea (mMainLoop.bas:88-91). 0 = libre. La inmunidad del original
    /// (<c>AfectaParalisis</c>, el 399) sale del <c>Inmunidad</c> de su ficha: ver CastStateOnNpc.
    /// </summary>
    public long ParalyzedUntilTick { get; set; }

    /// <summary>
    /// Tick hasta el que la criatura está INMOVILIZADA (VB6 <c>.flags.Inmovilizado</c>). No es la parálisis: la
    /// inmovilizada no camina, pero la IA le sigue corriendo y pega y castea según su rutina (mMainLoop.bas:93-104,
    /// ver NpcSystem). En el original las dos marcas comparten el contador (<c>Contadores.Paralisis</c>): con las dos
    /// activas, el último hechizo reinicia a ambas y vencen juntas. Va en un campo aparte para que lo que ya
    /// paraliza siga paralizando.
    /// </summary>
    public long InmovilizadaHastaTick { get; set; }

    /// <summary>VB6 <c>flags.Paralizado</c>: quieta y sin IA.</summary>
    public bool EstaParalizada(long tick) => tick < ParalyzedUntilTick;

    /// <summary>VB6 <c>flags.Inmovilizado</c> sin <c>Paralizado</c>: no camina, pero pega y castea.</summary>
    public bool EstaInmovilizada(long tick) => tick < InmovilizadaHastaTick && !EstaParalizada(tick);

    /// <summary>Paralizada o inmovilizada, lo que el original pregunta con <c>Paralizado = 1 Or Inmovilizado = 1</c>.</summary>
    public bool EstaQuieta(long tick) => tick < ParalyzedUntilTick || tick < InmovilizadaHastaTick;

    /// <summary>Un hechizo que la deja quieta (VB6 HechizoEstadoNPC, modHechizos.bas:1557-1565): prende su marca y
    /// reinicia el contador COMPARTIDO, así que la otra marca, si seguía activa, vence junto con ésta.</summary>
    public void Aquietar(long hastaTick, bool paraliza, bool inmoviliza, long tick)
    {
        bool estabaParalizada = tick < ParalyzedUntilTick, estabaInmovilizada = tick < InmovilizadaHastaTick;
        if (paraliza || estabaParalizada) ParalyzedUntilTick = hastaTick;
        if (inmoviliza || estabaInmovilizada) InmovilizadaHastaTick = hastaTick;
    }

    /// <summary>
    /// Tick del game-loop a partir del cual esta criatura puede volver a ATACAR — de un golpe o de un
    /// hechizo, que en el original comparten el mismo contador (VB6 <c>Contadores.Ataque</c>, consumido por
    /// <c>IntervaloPermiteAtacarNpc</c> en modNuevoTimer.bas:52, que exige 3000 ms).
    ///
    /// <para>Es POR CRIATURA, y esa es la diferencia que importa: el port sólo tenía una cadencia global
    /// —la fase de ataque del loop, una vez por segundo— así que cada criatura pegaba cada 1000 ms en vez
    /// de cada 3000. El triple de golpes es el triple de daño recibido en todo el PvE, y desde que ser
    /// atacado entrena Tácticas también era el triple de paquetes de subida de habilidad.</para>
    ///
    /// <para>Lo comparten el melee y el casteo porque en el VB6 lo consumen los tres call sites del mismo
    /// contador: <c>NpcAtacaUser</c> (SistemaCombate.bas:713), <c>NpcAtacaNpc</c> (:831) y los dos de
    /// <c>modHechizos.bas</c> (:100 y :145). Con contadores separados una criatura maga castearía Y pegaría
    /// en la misma vuelta, cosa que el original no hace nunca.</para>
    /// </summary>
    public long NextAttackTick { get; set; }

    /// <summary>NPCs a los que el VB6 NO les corre la IA: ni pelean ni se mueven. <c>NPCAI</c> sale de una en su
    /// PRIMERA línea (AI_NPC.bas:700) para tres <c>eNPCType</c> — <b>GuardiasCity(2)</b>, <b>facciones(5)</b> y
    /// <b>Convertidores(18)</b> — con el comentario "Mermas temporal para que los bichos nose muevan".
    /// El rol sale de la ficha (NPCs.dat "NpcType"); los flags de facción los pone a mano el fixture, que no
    /// tiene NpcType. Los 7 entrenadores de clase de Ullathorpe (Clérigo, Guerrero, Bardo, Paladín, Mago,
    /// Mercenario, Asesino) son GuardiasCity: sin este gate abandonan su puesto y siguen al jugador.</summary>
    public bool SinIa => Template.NpcType is 2 or 5 or 18 || FactionEnlist != 0 || FactionConvert != 0;

    public bool IsAlive => Hp > 0;
    public string Name => Template.Name;
}
