using System.Text.Json;
using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;

namespace CoverAO.Server.Net;

/// <summary>
/// Reads one inbound packet and routes it to game logic. Fields are read synchronously (on the I/O
/// thread) so the stream cursor advances correctly; the resulting state mutation is enqueued onto
/// the <see cref="GameLoop"/> so all game state changes run single-threaded (no data races).
///
/// Handlers read ALL their fields up front, then act — mirroring the AO invariant that a truncated
/// packet (NotEnoughData) leaves no partial side effect.
/// </summary>
public sealed partial class PacketDispatcher
{
    private readonly World _world;
    private readonly GameLoop _loop;
    private readonly Persistence _db;
    private readonly GameData _data;
    private readonly Leveling _leveling;
    private readonly Random _rng = new();
    // Anti-cheat de cadencia (ms). 0 = desactivado (modo test/fixture, donde los flows mandan en ráfaga).
    // En producción se setean desde Program.cs con los valores tipo VB6 (IntervaloParaCaminar/ParaGolpe).
    private readonly long _walkIntervalMs;
    private readonly long _attackIntervalMs;
    private readonly LoginThrottle? _throttle;   // rate-limit de login por IP (anti brute-force / DoS por PBKDF2)
    private readonly AdminConfig? _admin;        // eventos exp/oro/drop + modo solo-GMs (lo muta el panel de admin)
    private readonly ServerContent _content;     // MOTD + novedades del login (VB6 History.bas)
    private readonly Data.ServerIni _ini;        // Server.ini: quién es GM, si se pueden crear personajes, etc.
    // Relojes de combate del [INTERVALOS] (ver Game/CombatTiming.cs). Se resuelven UNA vez acá y no se leen
    // del ini en cada golpe. Van en CERO con --fixture, igual que la cadencia de ataque y la de caminata: los
    // flows mandan paquetes tan rápido como pueden y una cadencia real los volvería lentos y flakys.
    private readonly long _intervaloCastear;
    private readonly long _intervaloMagiaGolpe;
    private readonly long _intervaloGolpeMagia;
    private readonly long _intervaloGolpeUsar;

    public PacketDispatcher(World world, GameLoop loop, Persistence db, GameData data, Leveling leveling,
        long walkIntervalMs = 0, long attackIntervalMs = 0, LoginThrottle? throttle = null, AdminConfig? admin = null,
        ServerContent? content = null, NpcSystem? npcs = null, Action<bool>? shutdown = null,
        bool fixtureMode = false, Data.ServerIni? serverIni = null)
    {
        _ini = serverIni ?? new Data.ServerIni();
        _fixtureMode = fixtureMode;
        _shutdown = shutdown;
        _world = world;
        _loop = loop;
        _db = db;
        _data = data;
        _leveling = leveling;
        _walkIntervalMs = walkIntervalMs;
        _attackIntervalMs = attackIntervalMs;
        _intervaloCastear = fixtureMode ? 0 : _ini.IntervaloCastear;
        _intervaloMagiaGolpe = fixtureMode ? 0 : _ini.IntervaloMagiaGolpe;
        _intervaloGolpeMagia = fixtureMode ? 0 : _ini.IntervaloGolpeMagia;
        _intervaloGolpeUsar = fixtureMode ? 0 : _ini.IntervaloGolpeUsar;
        _throttle = throttle;
        _admin = admin;
        _content = content ?? ServerContent.Default;
        _npcs = npcs;
        // El rechazo con escudo contra una CRIATURA entrena Defensa, y la tirada de entrenamiento vive acá.
        // Se cablea al revés (el sistema de NPCs pide, el dispatcher provee) para no atarlos entre sí.
        if (_npcs is not null)
            _npcs.SubirSkillPorUso = (jugador, skill) =>
                RaiseSkill(jugador.Connection as ClientSession, jugador, skill);
        EngancharAvisosDelFamiliar();
        if (_npcs is not null) _npcs.DesarmarPorFamiliar = DesarmarPorElFamiliar;
    }

    private readonly NpcSystem? _npcs;   // para /ACC /RACC (spawn de NPCs con respawn); null en tests sin mundo vivo

    /// <summary>Modo laboratorio (<c>--fixture</c>). Lo necesita el gate de UNA SESIÓN POR CUENTA: ver
    /// <see cref="AplicaSesionUnicaPorCuenta"/>.</summary>
    private readonly bool _fixtureMode;

    /// <summary>
    /// ¿Se le aplica a esta cuenta el gate de UNA SESIÓN POR CUENTA del original (Protocol.bas:1204)?
    ///
    /// Sí, salvo a la cuenta demo cuando el server corre en modo laboratorio. El fixture es un banco de
    /// pruebas donde UN operador maneja varios personajes a la vez —los flujos de PvP, comercio, clan o
    /// susurro necesitan dos conectados—, algo que en producción no pasa. Es el mismo criterio con el que el
    /// stack ya desactiva la cadencia anti-cheat en fixture (Program.cs: <c>fixtureMode ? 0 : 130</c>).
    ///
    /// La exención es de la cuenta demo, no del modo: una cuenta creada a mano contra un server de pruebas
    /// SÍ recibe el gate, y por eso <c>sesion_por_cuenta_flow.py</c> puede ejercerlo en vivo.
    /// </summary>
    /// OJO: este gate NO depende de <c>AllowMultiLogins</c>. En el original el <c>CuentaConectada</c> de
    /// Protocol.bas:1205 corre SIEMPRE; lo que <c>AllowMultiLogins=0</c> gatea es otra cosa —el
    /// <c>CheckForSameIP</c> de Cuentas.bas:369, que es por IP, no por cuenta—. Son dos límites distintos y
    /// conviene no confundirlos.
    private bool AplicaSesionUnicaPorCuenta(string account) =>
        !(_fixtureMode && string.Equals(account, "cuenta_test", StringComparison.OrdinalIgnoreCase));

    /// <summary>Apagar el servidor (el mismo callback que usa el panel de admin: <c>cts.Cancel()</c>). Lo necesita
    /// <c>/APAGAR</c>. Null en tests, donde el comando avisa y no apaga nada.</summary>
    private readonly Action<bool>? _shutdown;

    /// <summary>Chequeo de rate-limit al inicio de un handler de login. true = bloqueado (ya rechazó).</summary>
    private bool LoginBlocked(ClientSession session)
    {
        if (_throttle is null) return false;
        string ip = ConnectionGuard.IpOf(session.RemoteEndpoint);
        if (_throttle.IsBlocked(ip)) { Reject(session, "Demasiados intentos fallidos. Esperá unos minutos."); return true; }
        return false;
    }
    private void LoginFail(ClientSession session) => _throttle?.RecordFailure(ConnectionGuard.IpOf(session.RemoteEndpoint));
    private void LoginOk(ClientSession session) => _throttle?.RecordSuccess(ConnectionGuard.IpOf(session.RemoteEndpoint));

    /// <summary>
    /// Carga la cuenta y verifica la contraseña FUERA del hilo del game loop, y vuelve a él con el resultado.
    ///
    /// PBKDF2 son 210.000 iteraciones: 25,5 ms medidos en esta máquina, contra un presupuesto de tick de 40.
    /// Las tres rutas de login corrían adentro de un `_loop.Enqueue`, así que cada intento —incluido el
    /// DummyVerify de una cuenta que NO existe, que cuesta lo mismo a propósito para no delatar por timing si
    /// la cuenta existe— congelaba media vuelta del mundo para todos los que estaban jugando. Dos intentos en
    /// la misma vuelta ya la pasaban de largo. Es la amplificación clásica: un paquete de unos pocos bytes le
    /// compra al atacante decenas de milisegundos de CPU del hilo que no se puede bloquear.
    ///
    /// Lo que se mueve es SÓLO leer la cuenta del disco y derivar la clave: ni una línea que toque el mundo.
    /// El resto —rechazos, baneo, migración del hash viejo, entrada al mundo— sigue corriendo en el loop, en
    /// el mismo orden que antes. Y el <c>TomarVerificacion</c> de la sesión limita a UNA verificación en vuelo
    /// por conexión: sin eso, mandar N paquetes de login seguidos compraba N hashes en paralelo, y dos que
    /// terminaran juntos podían meter al mismo personaje al mundo dos veces.
    /// </summary>
    private void VerificarCuenta(ClientSession session, string account, string password,
                                 Action<Data.Account?, bool, bool> alTerminar)
    {
        if (session.Cerrada) return;
        if (!session.TomarVerificacion())
        {
            // Ya hay una verificación en vuelo para esta conexión: pasa cuando el cliente manda
            // ConnectAccount y LoginExistingChar seguidos, porque las dos arrancan desde el loop y no desde
            // el despacho del paquete. NO se descarta —descartarla dejaría al cliente esperando para
            // siempre—: se reintenta en la vuelta siguiente, o sea ~40 ms, contra los 25 que tarda el hash.
            _loop.Enqueue(() => VerificarCuenta(session, account, password, alTerminar));
            return;
        }
        Task.Run(() =>
        {
            Data.Account? acc = null;
            bool ok = false, migrado = false;
            try
            {
                acc = _db.LoadAccount(account);
                if (acc is null) PasswordHasher.DummyVerify(password);
                else ok = acc.VerifyPassword(password, out migrado);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[{session.RemoteEndpoint}] verificacion de cuenta: {ex.Message}");
            }
            _loop.Enqueue(() =>
            {
                session.SoltarVerificacion();
                // Entre que arrancó el hash y que terminó, el cliente se pudo haber ido (o lo pudo haber
                // cerrado el timeout de autenticación). Nada de tocar el mundo por una conexión muerta.
                if (session.Cerrada) return;
                try { alTerminar(acc, ok, migrado); }
                // Lo que el cliente haya mandado DETRÁS del login quedó retenido en el buffer esperando esta
                // verificación (ver ClientSession.DrainInbound). Se drena pase lo que pase, también si el
                // login falló: si no, esos bytes se quedan ahí hasta que llegue el próximo segmento TCP.
                finally { session.ReanudarInbound(); }
            });
        });
    }

    // LAS FUENTES DEL `ConsoleMsg`, tal como salen por el cable: son los ordinales del enum
    // `FontTypeNames` del original (Servidor/Codigo/Protocol.bas:281), que arranca en 0.
    //
    // No son decorado: el cliente elige el COLOR con este byte, así que mandar todo con 0 le pintaba de
    // blanco de charla mensajes que en el original salen celestes (info) o violetas (clan). El oráculo no lo
    // veía hasta que se lo afinó para comparar el FontIndex además del nombre del paquete.

    /// <summary>`FONTTYPE_INFO`: las respuestas informativas del servidor (la ficha del /EST, /ONLINE,
    /// /UPTIME, descansar, el correo, los amigos).</summary>
    private const byte FuenteInfo = 3;

    /// <summary>`FONTTYPE_GUILD`: TODO lo que emite un handler de clan, incluida la rama de rechazo
    /// "no perteneces a ningún clan".</summary>
    private const byte FuenteClan = 7;

    /// <summary>`FONTTYPE_GUILDMSG`: sólo el `/ONLINECLAN` (HandleGuildOnline). Es OTRA que la del clan, y la
    /// diferencia se ve: no hay una "fuente de clan" única.</summary>
    private const byte FuenteClanMsg = 9;

    /// <summary>`FONTTYPE_FIGHT`. OJO que en este código NO es la del daño —el combate viaja por `LocaleMsg`,
    /// que sin fuente explícita va con 0—: la usan el apuñalar y el desarmar (Trabajo.bas), el anti-cheat y
    /// las expulsiones.</summary>
    private const byte FuenteCombate = 1;

    /// <summary>`FONTTYPE_INFOBOLD4`: la usa un solo camino, el mensaje a la lista de amigos.</summary>
    private const byte FuenteAmigos = 24;

    /// <summary>`FONTTYPE_INFOBOLD2`: el SUSURRO, en sus dos líneas —la del que lo manda y la del que lo
    /// recibe—, que en el original llevan el mismo texto y la misma fuente (Protocol.bas:1495).</summary>
    private const byte FuenteSusurro = 22;

    // LO QUE SE QUEDA EN 0 A PROPÓSITO, para que no lo "arregle" el que pase después:
    //
    //  - EL COMBATE (fallar, pegar, matar, el efecto de un hechizo). El original no manda eso por
    //    `ConsoleMsg` sino por `WriteLocaleMsg` (Protocol.bas:17303), cuyo parámetro `fuente` es OPCIONAL y
    //    vale 0 — y en los caminos de combate se lo omite. O sea que el 0 es la fuente FIEL, no la que
    //    faltaba poner. (Sí se le pasa fuente en unos pocos: el 391 va con 12, el 47 con 5.)
    //  - El `/RMSG` y el `/DARPUN`, que el original difunde con `FONTTYPE_TALK` = 0
    //    (Protocol.bas:8867 y :16583).
    //  - El motivo de rechazo del login: no es una línea del chat, el cliente lo muestra en el formulario.

    /// <summary>
    /// ANTI-SPAM EN EL JUEGO. Distinto del techo de paquetes: acá el que se pasa es un jugador, no un
    /// atacante, así que la acción se DESCARTA y se le avisa — nunca se lo desconecta.
    ///
    /// Dos listas y no una, porque son dos costos distintos. Las RUIDOSAS le llegan a otros: hablar se
    /// difunde a toda el área, el clan a todos los miembros, el susurro a un tercero, el /GM les suena a los
    /// administradores. Las CONSULTAS sólo le hacen contestar el servidor a quien preguntó, así que molestan
    /// menos y van más sueltas — pero cuestan trabajo, y `/online` recorre la lista entera de conectados.
    ///
    /// Lo que NO entra acá a propósito: caminar, pegar y castear, que ya tienen sus propios intervalos del
    /// original (IntervaloParaCaminar, IntervaloParaGolpe, IntervaloLanzaHechizo) y son reglas del juego, no
    /// anti-spam. Meterlos también sería aplicar dos gates a lo mismo.
    /// </summary>
    /// <summary>true si esta acción hay que descartarla por spam (y ya se le avisó al jugador).
    ///
    /// Se llama DESPUÉS de parsear los argumentos del paquete, nunca antes. Cortar antes obligaría a saber de
    /// memoria cuántos bytes ocupa cada uno para poder saltearlos, y una tabla de layouts paralela al parser
    /// real se desincroniza sola en cuanto alguien toca un handler — es el mismo problema que los subcomandos
    /// GM, que necesitó su propio test para no romper el stream. Parseando siempre, el stream queda sano
    /// pase lo que pase y lo único que se descarta es el EFECTO.</summary>
    private static bool Frenado(ClientSession session, bool ruidosa)
    {
        long ahora = Environment.TickCount64;
        if (ruidosa ? session.Acciones.PermiteRuidosa(ahora) : session.Acciones.PermiteConsulta(ahora)) return false;
        if (session.Acciones.TocaAvisar(ahora))
            session.Send(ServerMessages.ConsoleMsg("Esperá un momento antes de volver a hacer eso.", FuenteInfo));
        return true;
    }

    public void HandleOne(ClientSession session, ByteQueueReader reader)
    {
        var id = (ClientPacketId)reader.ReadByte();
        switch (id)
        {
            case ClientPacketId.LoginExistingChar: HandleLoginExistingChar(session, reader); break;
            case ClientPacketId.ConnectAccount: HandleConnectAccount(session, reader); break;
            case ClientPacketId.CreateNewAccount: HandleCreateNewAccount(session, reader); break;
            case ClientPacketId.LoginNewChar: HandleLoginNewChar(session, reader); break;
            case ClientPacketId.ElegirFamiliar: HandleElegirFamiliar(session, reader); break;
            case ClientPacketId.Walk: HandleWalk(session, reader); break;
            case ClientPacketId.ChangeHeading: HandleChangeHeading(session, reader); break;
            case ClientPacketId.attack: HandleAttack(session); break;
            case ClientPacketId.CastSpell: HandleCastSpell(session, reader); break;
            case ClientPacketId.Talk: HandleTalk(session, reader); break;
            case ClientPacketId.Whisper: HandleWhisper(session, reader); break;
            case ClientPacketId.Ping: HandlePing(session, reader); break;
            case ClientPacketId.RequestPositionUpdate: HandleRequestPositionUpdate(session); break;
            case ClientPacketId.Meditate: HandleMeditate(session); break;
            case ClientPacketId.Quit: HandleQuit(session); break;
            case ClientPacketId.Drop: HandleDrop(session, reader); break;
            case ClientPacketId.PickUp: HandlePickUp(session); break;
            case ClientPacketId.UseItem: HandleUseItem(session, reader); break;
            case ClientPacketId.EquipItem: HandleEquipItem(session, reader); break;
            case ClientPacketId.CommerceStart: HandleCommerceStart(session); break;
            case ClientPacketId.CommerceBuy: HandleCommerceBuy(session, reader); break;
            case ClientPacketId.CommerceSell: HandleCommerceSell(session, reader); break;
            case ClientPacketId.CommerceEnd: HandleCommerceEnd(session); break;
            case ClientPacketId.BankStart: HandleBankStart(session); break;
            case ClientPacketId.BankEnd: HandleBankEnd(session); break;
            case ClientPacketId.BankDeposit: HandleBankDeposit(session, reader); break;
            case ClientPacketId.BankExtractItem: HandleBankExtract(session, reader); break;
            case ClientPacketId.BankDepositGold: HandleBankDepositGold(session, reader); break;
            case ClientPacketId.BankExtractGold: HandleBankExtractGold(session, reader); break;
            case ClientPacketId.GMCommands: HandleGMCommands(session, reader); break;
            case ClientPacketId.CreateNewGuild: HandleCreateNewGuild(session, reader); break;
            case ClientPacketId.GuildMessage: HandleGuildMessage(session, reader); break;
            case ClientPacketId.GuildLeave: HandleGuildLeave(session); break;
            case ClientPacketId.GuildOnline: HandleGuildOnline(session); break;
            case ClientPacketId.GuildRequestMembership: HandleGuildJoin(session, reader); break;
            case ClientPacketId.GuildDeclareWar: HandleGuildDeclareWar(session, reader); break;
            case ClientPacketId.GuildOfferPeace: HandleGuildOfferPeace(session, reader); break;
            case ClientPacketId.GuildAcceptPeace: HandleGuildAcceptPeace(session, reader); break;
            // VB6 HandleCasamiento (Protocol.bas:16376): ASCIIString(nombre) + Byte(modo). El nombre NO es
            // decorativo — el original resuelve `NameIndex(UserName)` y con eso decide: offline → LocaleMsg 75,
            // online → PuedeCasarse. Acá se leía y se tiraba, así que uno se casaba con QUIEN TUVIERA ENFRENTE
            // aunque hubiera pedido casarse con otro. El `modo` sí es código muerto en esta variante.
            case ClientPacketId.Casamiento:
            {
                string conQuien = reader.ReadAsciiString(); reader.ReadByte();
                HandleCasamiento(session, conQuien);
                break;
            }
            case ClientPacketId.divorciar: HandleDivorciar(session); break;
            case ClientPacketId.Gamble: HandleGamble(session, reader); break;
            case ClientPacketId.RequestGuildLeaderInfo: HandleGuildLeaderInfo(session); break;
            // VB6 HandleGuildRequestDetails (Protocol.bas:4890): `SendGuildDetails(UserIndex, ReadASCIIString())`
            // — o sea que contesta con el clan PEDIDO, no con el propio. Acá el nombre se descartaba, así que
            // pedir la ficha de un clan ajeno devolvía siempre la del de uno.
            case ClientPacketId.GuildRequestDetails:
            {
                string clan = reader.ReadAsciiString();
                HandleGuildInfo(session, clan);
                break;
            }
            case ClientPacketId.GuildMemberInfo: HandleGuildMemberInfo(session, reader.ReadAsciiString()); break; // VB6: ASCIIString(nombre)
            case ClientPacketId.ShowGuildNews: HandleShowGuildNews(session); break;
            case ClientPacketId.GuildUpdateNews: HandleGuildUpdateNews(session, reader); break;
            case ClientPacketId.GuildRejectAlliance: HandleGuildRejectAlliance(session, reader); break;
            case ClientPacketId.GuildRejectPeace: HandleGuildRejectPeace(session, reader); break;
            case ClientPacketId.Resucitate: HandleResucitate(session); break;
            case ClientPacketId.RequestMiniStats: HandleRequestMiniStats(session); break;
            case ClientPacketId.MoveBank: HandleMoveBank(session, reader); break;
            case ClientPacketId.Enlist: HandleEnlist(session); break;
            case ClientPacketId.Reward: HandleReward(session); break;
            // /GM — VB6 HandleGMRequest (Protocol.bas:5670): encola la consulta en `Ayuda` y avisa a los GMs.
            // Antes esto mandaba SÓLO el LocaleMsg(85) y perdía las otras dos mitades del handler: el
            // LocaleMsg(500) a los admins con el nombre del que pide, y el LocaleMsg(427) ("volviste a mandar,
            // esperá") cuando el mismo personaje ya tiene una consulta pendiente. El barrido del oráculo no lo
            // vio porque usa un char fresco por acción y la manda UNA vez, sin GMs conectados.
            case ClientPacketId.GMRequest:
            {
                byte tipo = reader.ReadByte();
                string consulta = reader.ReadAsciiString();
                if (Frenado(session, ruidosa: true)) break;   // el /GM les suena a todos los administradores
                _loop.Enqueue(() =>
                {
                    var p = session.Player;
                    if (p is null) return;
                    if (_world.HelpExists(p.Name)) { session.Send(ServerMessages.LocaleMsg(427)); return; }
                    session.Send(ServerMessages.LocaleMsg(85));
                    _world.HelpPush(p.Name, tipo);
                    BroadcastToAdmins(ServerMessages.LocaleMsg(500, p.Name));
                    Console.WriteLine($"[{session.RemoteEndpoint}] /GM {p.Name} (tipo {tipo}): {consulta}");
                });
                break;
            }
            case ClientPacketId.InitCrafting: reader.ReadLong(); reader.ReadInteger(); break; // VB6 HandleInitCrafting solo setea el estado interno de fabricación (Cantidad/PorCiclo); no emite ningún paquete
            case ClientPacketId.Work: HandleWork(session, reader); break;
            case ClientPacketId.HayEventos: break;   // VB6 HandleHayEventos: sin evento activo no emite nada
            case ClientPacketId.AbrirForms: { byte form = reader.ReadByte(); _loop.Enqueue(() => { if (session.Player is null) return; if (form == 1) session.Send(ServerMessages.AbrirFormularios(9)); }); break; } // VB6 HandleAbrirForms: Case 1 -> donador?AbrirForm(10):AbrirForm(9); el slice no modela donador -> 9. Otros forms: silencio.
            case ClientPacketId.ProcesosLogin: HandleProcesosLogin(session, reader); break;
            case ClientPacketId.PidePremios: HandlePidePremios(session); break;
            case ClientPacketId.RPremios: HandleRPremios(session, reader); break;
            case ClientPacketId.EquiparSkin: HandleEquiparSkin(session, reader); break;
            // VB6 HandleParticulaUsuario: inexistente->LocaleMsg(80), offline->75, online->broadcast particula+LocaleMsg(461).
            //
            // SIN GATE DE PRIVILEGIO, Y ES FIEL. El original parece tenerlo (Protocol.bas:15637) pero no:
            // `If PlayerType.Admin Or PlayerType.Dios Or PlayerType.SemiDios Then` OR-ea tres CONSTANTES del
            // enum y nunca las compara contra `.flags.Privilegios`, así que en VB6 es `If <distinto de cero>`
            // — siempre verdadero. Cualquiera puede ponerle una partícula a cualquiera, allá y acá.
            // NO agregarle un gate: sería divergir del oráculo en un paquete que el barrido compara.
            case ClientPacketId.ParticulaUsuario: { string ptName = reader.ReadAsciiString(); short particle = reader.ReadInteger(); _loop.Enqueue(() => { if (session.Player is null) return; var t = _world.PlayerByName(ptName); if (t is not null) { _world.BroadcastToObservers(t.Pos, ServerMessages.EfectoCharParticula(t.CharIndex, particle, -1f, false)); session.Send(ServerMessages.LocaleMsg(461, t.Name)); } else session.Send(ServerMessages.LocaleMsg(_db.CharacterExists(ptName) ? (short)75 : (short)80)); }); break; }
            case ClientPacketId.CentinelReport: reader.ReadInteger(); break; // VB6 CentinelaCheckClave: sin desafio activo, silencio
            // Los dos GUARDAN el dato y no responden nada (VB6 ChangeCodexAndDesc modGuilds.bas:402 y
            // ActualizarWebSite :383). Antes se leían los strings y se tiraban, así que editar la descripción,
            // el códex o la web del clan no tenía ningún efecto: GuildDetails los mandaba siempre vacíos.
            case ClientPacketId.ClanCodexUpdate: HandleClanCodexUpdate(session, reader.ReadAsciiString(), reader.ReadAsciiString()); break;
            case ClientPacketId.GuildNewWebsite: HandleGuildNewWebsite(session, reader.ReadAsciiString()); break;
            case ClientPacketId.GuildFundate: HandleGuildFundate(session); break;
            case ClientPacketId.GuildFundation: HandleGuildFundation(session, reader); break;
            case ClientPacketId.GuildAcceptNewMember: HandleGuildAcceptMember(session, reader); break;
            case ClientPacketId.GuildRejectNewMember: HandleGuildRejectMember(session, reader); break;
            case ClientPacketId.GuildRequestJoinerInfo: HandleGuildJoinerInfo(session, reader); break;
            case ClientPacketId.GuildAllianceDetails: HandleGuildRelationDetails(session, "alianza", reader.ReadAsciiString()); break;
            case ClientPacketId.GuildPeaceDetails: HandleGuildRelationDetails(session, "paz", reader.ReadAsciiString()); break;
            case ClientPacketId.GuildAlliancePropList: HandleGuildOfferList(session, alliance: true); break;
            case ClientPacketId.GuildPeacePropList: HandleGuildOfferList(session, alliance: false); break;
            case ClientPacketId.Packets_Correo: HandlePacketsCorreo(session, reader); break;
            case ClientPacketId.GuildOpenElections: HandleGuildOpenElections(session); break;
            case ClientPacketId.GuildVote: HandleGuildVote(session, reader); break;
            case ClientPacketId.GuildOfferAlliance: HandleGuildOfferAlliance(session, reader); break;
            case ClientPacketId.GuildAcceptAlliance: HandleGuildAcceptAlliance(session, reader); break;
            case ClientPacketId.CraftBlacksmith: HandleCraftBlacksmith(session, reader); break;
            case ClientPacketId.CraftCarpenter: HandleCraftCarpenter(session, reader); break;
            case ClientPacketId.CraftSastre: HandleCraftSastre(session, reader); break;
            case ClientPacketId.Craftalquimia: HandleCraftAlquimia(session, reader); break;
            case ClientPacketId.WorkLeftClick: HandleWorkLeftClick(session, reader); break;
            case ClientPacketId.EnviarCorreo: HandleEnviarCorreo(session, reader); break;
            case ClientPacketId.DropDestroy: HandleDropDestroy(session, reader); break;
            case ClientPacketId.Train: HandleTrain(session, reader.ReadByte()); break;   // VB6: Byte(petIndex)
            case ClientPacketId.LeftClick: HandleLeftClick(session, reader); break;
            case ClientPacketId.DoubleClick: HandleDoubleClick(session, reader); break;
            case ClientPacketId.RequestSkills: HandleRequestSkills(session); break;
            case ClientPacketId.RequestAtributes: HandleRequestAttributes(session); break;
            case ClientPacketId.RequestStats: HandleRequestStats(session); break;
            case ClientPacketId.ChangeDescription: HandleChangeDescription(session, reader); break;
            case ClientPacketId.RetirarFaccion: HandleRetirarFaccion(session); break;
            case ClientPacketId.TransferGOLD: HandleTransferGold(session, reader); break;
            case ClientPacketId.ModifySkills: HandleModifySkills(session, reader); break;
            case ClientPacketId.CloseGuild: HandleCloseGuild(session); break;
            case ClientPacketId.Denounce: HandleDenounce(session, reader); break;
            case ClientPacketId.SeleccionarHogar: HandleSelectHome(session, reader); break;
            case ClientPacketId.DesconectarCuenta: HandleDesconectarCuenta(session, reader); break;
            case ClientPacketId.BorrarPersonaje: HandleBorrarPersonaje(session, reader); break;
            case ClientPacketId.UpTime: HandleUpTime(session); break;
            case ClientPacketId.RegresarHogar: HandleRegresarHogar(session); break;
            case ClientPacketId.MoveSpell: HandleMoveSpell(session, reader); break;
            case ClientPacketId.SwapObjects: HandleSwapObjects(session, reader); break;
            case ClientPacketId.GuildKickMember: HandleGuildKick(session, reader); break;
            case ClientPacketId.AddAmigos: HandleAddFriend(session, reader); break;
            case ClientPacketId.DelAmigos: HandleDelFriend(session, reader); break;
            case ClientPacketId.OnAmigos: HandleFriendsOnline(session); break;
            case ClientPacketId.MsgAmigos: HandleFriendMessage(session, reader); break;
            case ClientPacketId.Information: HandleInformation(session); break;
            case ClientPacketId.Online: HandleOnline(session); break;
            case ClientPacketId.Rest: HandleRest(session); break;
            case ClientPacketId.ResuscitationSafeToggle: HandleResurrectionSafeToggle(session); break;
            case ClientPacketId.CombatModeToggle: HandleCombatModeToggle(session); break;
            default:
                // No length prefix means we cannot safely skip an unimplemented packet — doing so
                // would desync the stream. In the Fase 1 slice we close; later handlers fill this in.
                Console.WriteLine($"[{session.RemoteEndpoint}] paquete no implementado: {id} ({(byte)id})");
                session.Close($"unhandled packet {id}");
                break;
        }
    }

    private void HandleLoginExistingChar(ClientSession session, ByteQueueReader r)
    {
        string account = r.ReadAsciiString();
        string password = r.ReadAsciiString();
        byte verMajor = r.ReadByte(), verMinor = r.ReadByte(), verRev = r.ReadByte();
        string userName = r.ReadAsciiString();
        string mac = r.ReadAsciiString();
        int hdSerial = r.ReadLong();
        _loop.Enqueue(() => EnterWorld(session, account, password, userName, $"v{verMajor}.{verMinor}.{verRev}"));
    }

    private void HandleConnectAccount(ClientSession session, ByteQueueReader r)
    {
        string account = r.ReadAsciiString();
        string password = r.ReadAsciiString();
        byte verMajor = r.ReadByte(), verMinor = r.ReadByte(), verRev = r.ReadByte();
        string mac = r.ReadAsciiString();
        int hdSerial = r.ReadLong();
        // Real AO flow: account connect returns the character list; the client then shows the
        // selection form and sends LoginExistingChar to actually enter the world.
        _loop.Enqueue(() => SendAccountCharList(session, account, password));
    }

    private void SendAccountCharList(ClientSession session, string account, string password)
    {
        if (LoginBlocked(session)) return;   // rate-limit por IP (anti brute-force)
        if (!Naming.IsValidName(account)) { Reject(session, "Nombre de cuenta invalido."); return; }   // valida antes de tocar el store (path traversal)
        // Mismo mensaje genérico + mismo costo de tiempo (el DummyVerify que hace VerificarCuenta) para
        // cuenta-inexistente y password-malo: no delatar si la cuenta existe (enumeración por texto o por
        // timing). El baneo se chequea DESPUÉS de verificar el password (si no, un baneado se puede enumerar
        // sin conocer la contraseña).
        VerificarCuenta(session, account, password, (acc, ok, upgraded) =>
        {
            if (acc is null || !ok) { LoginFail(session); Reject(session, "Cuenta o contrasena invalida."); return; }
            if (acc.Banned) { Reject(session, "Cuenta baneada."); return; }
            LoginOk(session);

            // LA LISTA DE PERSONAJES SALE DEL DISCO: un `.chr` por personaje, hasta DIEZ, y hasta acá se leían
            // adentro del hilo del game loop —este callback vuelve al loop— cuyo presupuesto por vuelta es de
            // 40 ms. O sea que cada conexión de cuenta le cobraba al mundo diez lecturas de archivo (diez
            // consultas con SQLite), y varias personas entrando a la vez las apilan en la misma vuelta.
            //
            // Es exactamente el mismo defecto que este archivo ya corrigió dos veces: el `AccountExists` de la
            // creación de cuentas y la ficha del miembro de clan (:5318, "ESE LEER ES DISCO, y acá adentro
            // estamos en el hilo del game loop"). Faltaba el tercero, que además es el que corre SIEMPRE.
            //
            // El guardado del hash migrado se va al mismo hilo por la misma razón: es una escritura.
            var nombres = acc.Characters.ToArray();
            var cuenta = acc;
            Task.Run(() =>
            {
                var roster = new List<CharacterSummary>(nombres.Length);
                try
                {
                    if (upgraded) _db.SaveAccount(cuenta);   // el hash SHA-256 viejo, ya migrado a PBKDF2
                    foreach (var name in nombres)
                        if (_db.LoadSummary(name) is { } s) roster.Add(s);
                }
                catch (Exception ex)
                {
                    // Un `.chr` ilegible no puede dejar al jugador sin pantalla de personajes: se manda lo que
                    // se pudo leer, que es lo que hacía el `Where(s => s is not null)` de antes.
                    Console.Error.WriteLine($"[{session.RemoteEndpoint}] roster de {account}: {ex.Message}");
                }
                _loop.Enqueue(() =>
                {
                    if (session.Cerrada) return;
                    session.Send(ServerMessages.AddPj(account, roster));       // char list from the account's .cnt
                    session.Send(ServerMessages.AbrirFormularios(1));          // 1 = account/char-selection form
                    Console.WriteLine($"[{session.RemoteEndpoint}] ACCOUNT account={account} chars={roster.Count}");
                });
            });
        });
    }

    private void HandleCreateNewAccount(ClientSession session, ByteQueueReader r)
    {
        string account = r.ReadAsciiString();
        string password = r.ReadAsciiString();
        string userCode = r.ReadAsciiString();
        byte verMajor = r.ReadByte(), verMinor = r.ReadByte(), verRev = r.ReadByte();
        _loop.Enqueue(() =>
        {
            // RATE-LIMIT, como los otros tres caminos de auth. Era el único que NO lo tenía, y encima es el
            // más caro: CreateAccount corre PBKDF2 con 210.000 iteraciones (~50-150 ms) DENTRO del hilo del
            // game loop, cuyo presupuesto por vuelta es de 40 ms, y además escribe un .cnt al disco. Un
            // atacante con UNA conexión mandaba CreateNewAccount en bucle con nombres distintos y congelaba el
            // mundo mientras le llenaba el disco al que hostea. El propio docstring de LoginThrottle nombra
            // este vector ("amplificación de DoS por PBKDF2") y esta ruta se había quedado afuera.
            if (LoginBlocked(session)) return;
            if (!Naming.IsValidName(account)) { LoginFail(session); Reject(session, "Nombre de cuenta invalido (solo letras y espacios)."); return; }   // VB6 AsciiValidos/NombrePermitido — evita path traversal en el .cnt
            CrearCuentaFueraDelLoop(session, account, password, userCode);
        });
    }

    /// <summary>
    /// Crea la cuenta FUERA del hilo del game loop, igual que <see cref="VerificarCuenta"/>, y vuelve al loop
    /// con el resultado.
    ///
    /// Era el ÚLTIMO camino de autenticación que hasheaba adentro del tick: `CreateAccount` deriva la clave con
    /// PBKDF2 (210.000 iteraciones, 25,5 ms medidos contra un presupuesto de 40) y encima escribe el .cnt al
    /// disco. El rate-limit que se le había puesto no alcanzaba, porque <c>LoginThrottle</c> sólo cuenta
    /// FALLOS: una creación exitosa no suma nada, así que un cliente con nombres válidos distintos podía
    /// sostener el ritmo sin que el contador se moviera nunca.
    ///
    /// Lo que lo frena de verdad no es contar, es <c>TomarVerificacion</c>: deja UNA operación en vuelo por
    /// conexión y, mientras tanto, la sesión deja de drenar lo que llega (ClientSession.DrainInbound). O sea
    /// que mandar mil paquetes de creación seguidos ya no compra mil hashes en paralelo — compra uno y el
    /// resto espera. El reintento se apoya en que <c>Drain</c> ahora corta por vuelta.
    /// </summary>
    private void CrearCuentaFueraDelLoop(ClientSession session, string account, string password, string userCode)
    {
        if (session.Cerrada) return;
        if (!session.TomarVerificacion()) { _loop.Enqueue(() => CrearCuentaFueraDelLoop(session, account, password, userCode)); return; }
        Task.Run(() =>
        {
            bool existia = false, creada = false;
            try
            {
                // La CONSULTA de existencia también toca el disco, y estaba quedando adentro del tick: un
                // `File.Exists` por paquete de creación. No hashea, así que no congela el mundo como el
                // PBKDF2 —son microsegundos contra 25 ms—, pero es I/O en el hilo sagrado y no tiene por qué
                // estar ahí. Se hace acá, junto con la creación, y el veredicto vuelve por el loop.
                existia = _db.AccountExists(account);
                if (!existia) { _db.CreateAccount(account, password, userCode); creada = true; }
            }
            catch (Exception ex) { Console.Error.WriteLine($"[{session.RemoteEndpoint}] crear cuenta: {ex.Message}"); }
            _loop.Enqueue(() =>
            {
                session.SoltarVerificacion();
                // El cliente se pudo haber ido mientras se hasheaba (o lo cerró el timeout de autenticación).
                if (session.Cerrada) return;
                try
                {
                    // Cuenta ya existente: cuenta como intento fallido. Sin esto el handler era además un
                    // oráculo de enumeración gratis — se podía sondear qué cuentas existen sin credenciales.
                    if (existia) { LoginFail(session); Reject(session, $"La cuenta '{account}' ya existe."); return; }
                    if (!creada) { Reject(session, "No se pudo crear la cuenta."); return; }
                    LoginOk(session);
                    Console.WriteLine($"[{session.RemoteEndpoint}] CUENTA CREADA account={account}");
                    // Lista de personajes vacía, para que el cliente siga a crear el primero.
                    session.Send(ServerMessages.AddPj(account, Array.Empty<CharacterSummary>()));
                    session.Send(ServerMessages.AbrirFormularios(1));
                }
                finally { session.ReanudarInbound(); }
            });
        });
    }

    // Los tres subcomandos de ProcesosLogin (VB6 `Cuenta.EsChange`, que el cliente arma en frmRecuperarCuenta,
    // frmCambiarContraseña y frmPregunta). En los dos primeros el campo `UserCode` del paquete lleva cosas
    // distintas: con TipoCambiarClave viaja la contraseña ACTUAL, y con TipoRecuperar el código de recuperación.
    private const byte TipoRecuperarCuenta = 0, TipoCambiarClave = 1, TipoBorrarCuenta = 2;
    // Los índices de `locale_error_es.ind` que el original manda como cuerpo del SendMsgBox con Modo=1 — el
    // cliente los resuelve contra su tabla, así que por el cable viaja el NÚMERO como texto.
    private const string ErrCuentaNoExiste = "39", ErrClaveVacia = "49", ErrClaveRestablecida = "50",
                         ErrCodigoIncorrecto = "51", ErrClaveIncorrecta = "52";
    private const byte AccionResultadoLogin = 5;   // WriteEjecutarAccion(5, "1"|"0") — el "salió bien / salió mal"

    /// <summary>
    /// ProcesosLogin (id 101): cambiar la contraseña y recuperar la cuenta (VB6 HandleProcesosLogin,
    /// Protocol.bas:15684). Son las dos operaciones de cuenta que el original tiene fuera del juego y que acá
    /// no existían: el handler leía los siete campos y no hacía nada, así que quien perdía la clave perdía
    /// la cuenta y sus personajes, y nadie podía cambiarla nunca.
    ///
    /// <para>El tercer subcomando —<c>Tipo = 2</c>, borrar— <b>no se porta</b>: en el original esa rama no
    /// borra nada, contesta <c>EjecutarAccion(5, "0")</c> y sale (Protocol.bas:15743). Está muerta allá, así
    /// que acá se responde igual.</para>
    ///
    /// <para>Que la cuenta no exista se contesta con el aviso 39, como el original, aunque eso deje sondear
    /// qué cuentas hay: es la misma información que ya suelta la creación de cuentas ("la cuenta X ya
    /// existe"). Lo que sí se conserva es el rate-limit por IP y el costo constante del hash, para que el
    /// TIEMPO de respuesta no diga por otro lado lo que el mensaje ya dice.</para>
    /// </summary>
    /// <summary>BORRAR UN PERSONAJE (el 1b del informe). <b>No se porta de ningún lado</b>: el original no
    /// tiene borrado —su `Tipo = 2` de `ProcesosLogin` contesta que no y no toca nada (Protocol.bas:15742)— así
    /// que esto es protocolo NUEVO, el id 114, uno después del último del VB6.
    ///
    /// Cuatro reglas, y las cuatro existen para que un borrado no sea una desgracia:
    /// <list type="number">
    /// <item>Pide la CLAVE DE LA CUENTA. Es el mismo PBKDF2 del login, así que corre fuera del game loop —
    ///   verificarla adentro congelaría el mundo unos 100 ms por intento.</item>
    /// <item>El personaje tiene que ser DE ESA CUENTA. Sin esto, cualquiera con una cuenta propia borraría los
    ///   personajes ajenos mandando el nombre.</item>
    /// <item>No se borra un personaje CONECTADO: el mundo lo tiene en memoria y el autosave lo volvería a
    ///   escribir un rato después, así que el borrado no habría servido de nada.</item>
    /// <item>El `.chr` se APARTA, no se destruye (<c>ArchivarCharacter</c>). Perder un personaje es
    ///   irreversible para quien lo jugó; que el archivo siga estando no le cuesta nada a nadie.</item>
    /// </list>
    /// Al terminar se manda la lista de personajes de nuevo, que es lo que refresca la pantalla de selección.</summary>
    private void HandleBorrarPersonaje(ClientSession session, ByteQueueReader r)
    {
        string cuenta = r.ReadAsciiString();
        string clave = r.ReadAsciiString();
        string personaje = r.ReadAsciiString();
        _loop.Enqueue(() =>
        {
            if (LoginBlocked(session)) return;
            // El nombre arma la ruta del archivo: se valida ANTES de tocar el disco (path traversal).
            if (!Naming.IsValidName(cuenta) || !Naming.IsValidName(personaje))
            { LoginFail(session); session.Send(ServerMessages.SendMsgBox(ErrCuentaNoExiste, 1)); return; }
            // Conectado: no se borra. Se mira en el mundo, que es la autoridad de quién está adentro.
            if (_world.PlayerByName(personaje) is not null)
            { session.Send(ServerMessages.SendMsgBox("Ese personaje está conectado. Salí del juego y volvé a intentarlo.", 1)); return; }
            BorrarPersonajeFueraDelLoop(session, cuenta, clave, personaje);
        });
    }

    /// <summary>La parte cara del borrado: verificar la clave es un PBKDF2. Mismo patrón que el cambio de
    /// clave — una operación en vuelo por conexión y el veredicto vuelve por el loop.</summary>
    private void BorrarPersonajeFueraDelLoop(ClientSession session, string cuenta, string clave, string personaje)
    {
        if (session.Cerrada) return;
        if (!session.TomarVerificacion())
        { _loop.Enqueue(() => BorrarPersonajeFueraDelLoop(session, cuenta, clave, personaje)); return; }
        Task.Run(() =>
        {
            Data.Account? acc = null;
            bool claveOk = false, eraSuyo = false, borrado = false;
            var roster = new List<CharacterSummary>();
            try
            {
                acc = _db.LoadAccount(cuenta);
                if (acc is null) PasswordHasher.DummyVerify(clave);
                else
                {
                    claveOk = acc.VerifyPassword(clave);
                    eraSuyo = acc.Characters.Any(n => string.Equals(n, personaje, StringComparison.OrdinalIgnoreCase));
                    if (claveOk && eraSuyo)
                    {
                        borrado = _db.ArchivarCharacter(personaje);
                        acc.Characters.RemoveAll(n => string.Equals(n, personaje, StringComparison.OrdinalIgnoreCase));
                        _db.SaveAccount(acc);
                    }
                    if (claveOk)
                        foreach (var n in acc.Characters)
                            if (_db.LoadSummary(n) is { } s) roster.Add(s);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[{session.RemoteEndpoint}] borrar personaje: {ex.Message}");
            }
            _loop.Enqueue(() =>
            {
                session.SoltarVerificacion();
                if (session.Cerrada) return;
                if (acc is null || !claveOk)
                {
                    LoginFail(session);
                    session.Send(ServerMessages.SendMsgBox(acc is null ? ErrCuentaNoExiste : ErrClaveIncorrecta, 1));
                    return;
                }
                LoginOk(session);
                if (!eraSuyo)
                { session.Send(ServerMessages.SendMsgBox("Ese personaje no es de esta cuenta.", 1)); return; }
                Console.WriteLine($"[{session.RemoteEndpoint}] PERSONAJE BORRADO account={cuenta} pj={personaje} archivado={borrado}");
                session.Send(ServerMessages.AddPj(cuenta, roster));   // la pantalla de seleccion, ya sin el
                session.Send(ServerMessages.SendMsgBox($"{personaje} fue borrado.", 1));
            });
        });
    }

    private void HandleProcesosLogin(ClientSession session, ByteQueueReader r)
    {
        string account = r.ReadAsciiString();
        string pin = r.ReadAsciiString();          // clave actual (cambio) o código de recuperación (recuperar)
        string nuevaClave = r.ReadAsciiString();
        r.ReadByte(); r.ReadByte(); r.ReadByte();  // versión: este stack no la valida en ningún camino de login
        byte tipo = r.ReadByte();
        _loop.Enqueue(() =>
        {
            void Falla(string aviso)
            {
                session.Send(ServerMessages.SendMsgBox(aviso, 1));
                session.Send(ServerMessages.EjecutarAccion(AccionResultadoLogin, "0"));
            }
            if (LoginBlocked(session)) return;
            if (tipo == TipoBorrarCuenta)
            { session.Send(ServerMessages.EjecutarAccion(AccionResultadoLogin, "0")); return; }
            // El nombre se valida antes de tocar el disco: es el que arma la ruta del .cnt (path traversal).
            if (!Naming.IsValidName(account)) { LoginFail(session); Falla(ErrCuentaNoExiste); return; }
            if (string.IsNullOrEmpty(nuevaClave)) { Falla(ErrClaveVacia); return; }
            CambiarClaveFueraDelLoop(session, account, pin, nuevaClave, tipo);
        });
    }

    /// <summary>La parte cara de <see cref="HandleProcesosLogin"/>: leer el .cnt, verificar el PIN y hashear la
    /// clave nueva son dos PBKDF2 y dos toques de disco, o sea diez veces el presupuesto de una vuelta del
    /// game loop. Mismo patrón que <see cref="VerificarCuenta"/>: una operación en vuelo por conexión y el
    /// veredicto vuelve por el loop.</summary>
    private void CambiarClaveFueraDelLoop(ClientSession session, string account, string pin, string nuevaClave, byte tipo)
    {
        if (session.Cerrada) return;
        if (!session.TomarVerificacion())
        { _loop.Enqueue(() => CambiarClaveFueraDelLoop(session, account, pin, nuevaClave, tipo)); return; }
        Task.Run(() =>
        {
            Data.Account? acc = null;
            bool pinOk = false;
            try
            {
                acc = _db.LoadAccount(account);
                // Sin cuenta se quema el mismo tiempo igual, como en el login (PasswordHasher.DummyVerify).
                if (acc is null) PasswordHasher.DummyVerify(pin);
                else
                {
                    pinOk = tipo == TipoCambiarClave ? acc.VerifyPassword(pin) : acc.VerifyUserCode(pin);
                    if (pinOk) { acc.PasswordHash = PasswordHasher.HashModern(nuevaClave); acc.Salt = ""; _db.SaveAccount(acc); }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[{session.RemoteEndpoint}] procesos de login: {ex.Message}");
            }
            _loop.Enqueue(() =>
            {
                session.SoltarVerificacion();
                if (session.Cerrada) return;
                try
                {
                    if (acc is null)
                    {
                        LoginFail(session);
                        session.Send(ServerMessages.SendMsgBox(ErrCuentaNoExiste, 1));
                    }
                    else if (pinOk)
                    {
                        LoginOk(session);
                        Console.WriteLine($"[{session.RemoteEndpoint}] CLAVE CAMBIADA account={account} tipo={tipo}");
                        session.Send(ServerMessages.SendMsgBox(ErrClaveRestablecida, 1));
                    }
                    else
                    {
                        LoginFail(session);
                        session.Send(ServerMessages.SendMsgBox(
                            tipo == TipoCambiarClave ? ErrClaveIncorrecta : ErrCodigoIncorrecto, 1));
                    }
                    session.Send(ServerMessages.EjecutarAccion(AccionResultadoLogin, pinOk ? "1" : "0"));
                }
                finally { session.ReanudarInbound(); }
            });
        });
    }

    private void HandleLoginNewChar(ClientSession session, ByteQueueReader r)
    {
        string account = r.ReadAsciiString();
        string password = r.ReadAsciiString();
        byte verMajor = r.ReadByte(), verMinor = r.ReadByte(), verRev = r.ReadByte();
        string charName = r.ReadAsciiString();
        byte race = r.ReadByte(), gender = r.ReadByte(), klass = r.ReadByte(), homeland = r.ReadByte();
        short head = r.ReadInteger();
        string mac = r.ReadAsciiString();
        int hd = r.ReadLong();
        _loop.Enqueue(() =>
        {
            // La cuenta admite dígitos y guión bajo; el PERSONAJE no (VB6 ValidarNombre, TCP.bas:181 — sólo
            // letras y espacio). Ver el porqué en Naming.IsValidCharName: un nombre numérico haría que el
            // cliente confundiera al personaje con una criatura.
            // Se rechazan por separado para que el motivo sea el correcto: decirle "solo letras y espacios" a
            // quien puso mal la CUENTA sería mentirle, porque ahí los dígitos sí valen.
            // El host puede cerrar la creación de personajes desde el Server.ini (VB6 PuedeCrearPersonajes,
            // Cuentas.bas:378 → mensaje 3). Sirve para congelar el ingreso sin bajar el server.
            if (!_ini.PuedeCrearPersonajes)
            { Reject(session, "La creacion de personajes esta deshabilitada en este servidor."); return; }
            if (!Naming.IsValidName(account)) { Reject(session, "Nombre de cuenta invalido."); return; }
            if (!Naming.IsValidCharName(charName)) { Reject(session, "Nombre de personaje invalido (solo letras y espacios)."); return; }
            // VB6 TCP.bas:383-453: valida los rangos de raza/sexo/clase/tierra al crear (rechaza 0 y fuera de tabla).
            // La TIERRA NATAL admite dos, no doce: el original corta en 2 con el comentario "porque al nacimiento
            // solo hay Nix o Illi" (TCP.bas:455). Las otras diez son ciudades del mundo a las que se muda el hogar
            // más tarde, con el sacerdote; aceptarlas acá dejaba nacer en el Dungeon Newbie o en la cárcel.
            if (gender is < 1 or > 2 || race is < 1 or > 10 || klass is < 1 or > 18 || homeland is < 1 or > Cities.MaxTierraNatal)
            { Reject(session, "Datos de personaje invalidos."); return; }
            // La CABEZA se valida aparte porque su rango depende de raza+género (VB6 ValidarCabeza, TCP.bas:125):
            // no es 1..N sino un tramo propio por combinación. Acá sólo se exigía `head >= 1`, así que se podía
            // crear un humano con cabeza de orco — y el cliente lo dibujaba mal o directamente no lo dibujaba.
            if (!Equipment.IsValidHead(race, gender, head))
            { Reject(session, "Esa cara no corresponde a la raza y el genero elegidos."); return; }
            if (LoginBlocked(session)) return;   // rate-limit por IP (anti brute-force)
            VerificarCuenta(session, account, password, (acc, ok, upgraded) =>
            {
                if (acc is null || !ok) { LoginFail(session); Reject(session, "Cuenta o contrasena invalida."); return; }
                if (acc.Banned) { Reject(session, "Cuenta baneada."); return; }   // un baneado tampoco crea personajes nuevos
                LoginOk(session);
                if (upgraded) _db.SaveAccount(acc);   // legacy SHA-256 hash migrated to PBKDF2 → persist it
                if (_db.CharacterExists(charName)) { Reject(session, $"El nombre '{charName}' ya esta en uso."); return; }
                if (acc.Characters.Count >= AccountFile.MaxCharacters) { Reject(session, "Maximo de personajes alcanzado."); return; }
                // Personaje REAL: nace con el kit de su clase (NACIMIENTO.dat). El kit demo maestro queda sólo para
                // los personajes sembrados del fixture, de cuyo layout dependen los flows.
                var player = _db.CreateCharacter(acc, charName, _world.AllocateCharIndex(), race, gender, klass, homeland, head,
                    _data.NewbieKit(klass), _data.ModRaza);
                Console.WriteLine($"[{session.RemoteEndpoint}] PERSONAJE CREADO name={charName} cuenta={account}");
                SpawnIntoWorld(session, player, $"v{verMajor}.{verMinor}.{verRev}");
            });
        });
    }

    private void Reject(ClientSession session, string reason)
    {
        session.Send(ServerMessages.ConsoleMsg(reason, 0));
        Console.WriteLine($"[{session.RemoteEndpoint}] RECHAZADO: {reason}");
        session.Close(reason);
    }

    private void EnterWorld(ClientSession session, string account, string password, string charName, string version)
    {
        if (LoginBlocked(session)) return;   // rate-limit por IP (anti brute-force)
        if (!Naming.IsValidName(account) || !Naming.IsValidName(charName)) { Reject(session, "Nombre invalido."); return; }   // valida antes de tocar el store (path traversal)
        VerificarCuenta(session, account, password, (acc, ok, upgraded) =>
        {
            if (acc is null || !ok) { LoginFail(session); Reject(session, "Cuenta o contrasena invalida."); return; }
            if (acc.Banned) { Reject(session, "Cuenta baneada."); return; }   // el baneo también aplica al login directo (no solo a ConnectAccount)
            LoginOk(session);
            if (upgraded) _db.SaveAccount(acc);   // legacy SHA-256 hash migrated to PBKDF2 → persist it
            EntrarAlMundo(session, acc, account, charName, version);
        });
    }

    /// <summary>La segunda mitad de EnterWorld: todo lo que toca el MUNDO, ya con la cuenta verificada.
    /// Está separada porque la verificación de la contraseña se hace fuera del hilo del game loop (ver
    /// VerificarCuenta) y ésta corre de vuelta en el loop, que es donde tiene que correr.</summary>
    private void EntrarAlMundo(ClientSession session, Data.Account acc, string account, string charName, string version)
    {
        if (!acc.Characters.Contains(charName, StringComparer.OrdinalIgnoreCase))
        { Reject(session, $"El personaje '{charName}' no pertenece a la cuenta."); return; }

        // ¿YA ESTÁ CONECTADO ESE PERSONAJE? (VB6 Protocol.bas:1187-1202). El original rechaza el login nuevo Y
        // cierra la sesión vieja, avisando a las dos puntas; si la vieja está en la cuenta regresiva del
        // /SALIR, sólo pide esperar. Sin este gate el port dejaba DOS copias del mismo personaje en el mundo,
        // las dos escribiendo el mismo .chr: además de verse dos veces, la última en guardar pisa a la otra,
        // que es la forma clásica de duplicar oro e ítems.
        //
        // Nota de emisión: el original llama `WriteShowMessageBox(UserIndex, 46)` con un parámetro declarado
        // `As String`, así que VB6 coerciona el número y manda el TEXTO "46". Acá va el mensaje de verdad.
        // AllowMultiLogins=0 en el Server.ini: no entran DOS desde la misma IP (VB6 CheckForSameIP,
        // Cuentas.bas:369 → mensaje 25). Es un límite distinto del de "una sesión por cuenta" de más abajo:
        // éste es por IP y lo decide el host; aquél va por cuenta y corre siempre.
        if (!_ini.AllowMultiLogins && MismaIpYaConectada(session))
        { Reject(session, "Tu conexion ya alcanzo el limite de sesiones simultaneas de este servidor."); return; }
        var yaOnline = _world.PlayerByName(charName);
        if (yaOnline is not null)
        {
            // FANTASMA SIN CONEXIÓN: si el que figura adentro ya no tiene socket, no hay a quién avisarle ni
            // a quién echar — y rechazar al que entra dejaría el personaje inaccesible PARA SIEMPRE. Se lo
            // saca del mundo y el login sigue su curso. El original no necesitaba esta rama (un slot sin
            // conexión no existía en su modelo), pero acá el estado vive en objetos y una desconexión sucia
            // puede dejar el Player colgado.
            if (yaOnline.Connection is null)
            {
                _world.Remove(yaOnline.CharIndex);
                _world.BroadcastToObservers(yaOnline.Pos, ServerMessages.CharacterRemove(yaOnline.CharIndex));
                Console.WriteLine($"[{session.RemoteEndpoint}] LOGIN: '{charName}' estaba en el mundo sin conexión; se lo saca y se deja entrar");
            }
            else if (yaOnline.Saliendo)
            {
                session.Send(ServerMessages.ShowMessageBox("Ese personaje está saliendo del mundo. Esperá unos segundos e intentá de nuevo."));
                return;
            }
            else
            {
                session.Send(ServerMessages.ShowMessageBox("Ese personaje ya está conectado. Se cerró la otra sesión: volvé a entrar."));
                yaOnline.Connection.Send(ServerMessages.ShowMessageBox("Alguien entró con tu personaje desde otro lado. Se cierra esta sesión."));
                // Por IConnection, NO por un cast al ClientSession de TCP: el cliente web entra por WebSocket y
                // un cast fallido dejaría la sesión vieja viva, trabando el personaje para siempre.
                yaOnline.Connection.Close("login duplicado");
                return;   // el login nuevo NO procede, igual que el original
            }
        }

        // ¿YA HAY ALGUIEN DE ESTA CUENTA EN EL MUNDO? (VB6 Protocol.bas:1204, mensaje 47: "solo puedes logear un
        // personaje por cuenta"). El original lo lleva con un flag `Conectada` que escribe en el .cnt al entrar
        // (TCP.bas:959) y borra al salir; acá se pregunta por los jugadores online, que es lo mismo sin el
        // problema del original —si el server se cae con el flag en 1, la cuenta queda trabada—.
        //
        // A diferencia del gate de arriba, este NO protege ningún dato: son personajes distintos con .chr
        // distintos. Es una regla de producto del original — y por eso la cuenta demo queda exenta en modo
        // laboratorio, donde un solo operador maneja varios personajes a la vez (ver AplicaSesionUnicaPorCuenta).
        var otroDeLaCuenta = !AplicaSesionUnicaPorCuenta(account) ? null : _world.Players.FirstOrDefault(
            pl => string.Equals(pl.Account, account, StringComparison.OrdinalIgnoreCase));
        if (otroDeLaCuenta is not null)
        {
            session.Send(ServerMessages.ShowMessageBox(
                $"Ya hay un personaje de esta cuenta conectado ({otroDeLaCuenta.Name}). Solo se puede jugar con uno a la vez."));
            return;
        }

        // ESTA LECTURA SE QUEDA EN EL LOOP, A PROPÓSITO. Es disco —un `.chr` entero con inventario, banco,
        // hechizos y skills— y por eso rompe la regla que este archivo sigue en todos lados (el roster de
        // personajes y la ficha de clan se leen afuera). La diferencia es lo que hay ARRIBA: los cuatro gates
        // que acaba de pasar —el personaje pertenece a la cuenta, no está ya conectado, la IP no repite, la
        // cuenta no tiene otro adentro— miran el estado VIVO del mundo. Sacar la lectura afuera obliga a
        // volver al loop para spawnear, y entre una cosa y otra el mundo cambió: dos logins del mismo
        // personaje pueden pasar los dos gates y entrar los dos, que es exactamente lo que ese gate impide y
        // la forma clásica de duplicar oro e ítems (los dos escriben el mismo .chr y el último pisa).
        //
        // O sea que el costo es UNA lectura por entrada al mundo, contra abrir una ventana de carrera en la
        // compuerta que protege los datos. Se paga la lectura. Si algún día molesta, el arreglo NO es moverla
        // sino reservar el personaje ANTES de leer (marcarlo como "entrando" en el mundo), y recién ahí se
        // puede hacer la lectura afuera sin ventana.
        var player = _db.LoadCharacter(charName, _world.AllocateCharIndex(), account);
        if (player is null) { Reject(session, $"No se encontro el personaje '{charName}'."); return; }
        if (player.Banned) { Reject(session, "Este personaje esta baneado."); return; }   // VB6 flags.Ban — baneo por personaje (/BAN)
        if (acc.Donador) player.Donador = true;   // VB6 TCP.bas:1863 — el flag de la CUENTA fuerza donador en el char
        // Modo mantenimiento "Solo GMs" (chkServerHabilitado del VB6): sólo entran privilegiados.
        // MANTENIMIENTO: quién es staff lo decide el Server.ini, NO el `.chr`. Acá se miraba `player.IsGm`, que
        // en este punto sale del `.chr` recién cargado — y como este mismo commit dejó de persistir privilegios,
        // el `.chr` de todo GM legítimo tiene `Privilegios=1`. O sea que `ServerSoloGMs=1` echaba exactamente a
        // quien tenía que dejar pasar, y si el host había arrancado con `--no-admin` no quedaba forma de
        // destrabarlo. `AplicarPrivilegiosDeStaff` —lo único que lee el ini— corre recién en SpawnIntoWorld,
        // veinte líneas más abajo. El VB6 no podía tener este bug porque `ChequeosServerIni` usa
        // `EsGmChar(UserName)` (Cuentas.bas:347), que consulta las listas del ini.
        //
        // Sin listas en el ini se cae al privilegio en memoria, que es lo que mantiene vivo el GM cableado del
        // fixture (Rugura), del que dependen los flows de comandos GM.
        bool esStaff = _ini.CantidadStaff > 0 ? _ini.EsStaff(player.Name) : player.IsGm;
        if (_admin?.OnlyGms == true && !esStaff) { Reject(session, "El servidor está en mantenimiento (solo administradores)."); return; }
        SpawnIntoWorld(session, player, version);
    }

    /// <summary>¿Hay ya alguien EN EL MUNDO desde la misma IP? (VB6 <c>CheckForSameIP</c>, Cuentas.bas:369).
    /// Se compara sólo el host del endpoint, sin el puerto — que cambia en cada conexión.</summary>
    private bool MismaIpYaConectada(ClientSession session)
    {
        // Se compara la IP PELADA, sin el prefijo de transporte. Acá había un `SoloHost` propio que sólo
        // cortaba el puerto, así que dejaba el prefijo puesto: la misma máquina daba "tcp:1.2.3.4" entrando
        // por el cliente nativo y "ws:1.2.3.4" por el cliente web, no matcheaban, y `AllowMultiLogins=0` se
        // esquivaba abriendo la segunda sesión desde el navegador. `ConnectionGuard.NormalizeIp` ya existía
        // justamente para descartar ese prefijo — `SoloHost` era una copia divergente. (De yapa: con un
        // endpoint sin puerto, "tcp:?" colapsaba a "tcp" y dos conexiones cualesquiera colisionaban.)
        string ip = ConnectionGuard.NormalizeIp(session.RemoteEndpoint);
        return ip.Length > 0 && _world.Players.Any(pl => pl.Connection is not null
                                                         && ConnectionGuard.NormalizeIp(pl.Connection.RemoteEndpoint) == ip);
    }

    /// <summary>Le da (o le saca) los privilegios de staff al entrar, según las listas del Server.ini
    /// (VB6 TCP.bas:1941, la cadena de <c>EsAdmin/EsDios/EsSemiDios/EsConsejero/EsRolesMaster</c>).
    ///
    /// Se resuelve EN CADA LOGIN y no se persiste, que es lo que hace el original y además es lo correcto:
    /// el host saca un nombre del ini y esa persona deja de ser GM la próxima vez que entra, sin tocar su
    /// <c>.chr</c>. Por eso también hay que BAJAR el privilegio del que ya no está en la lista.
    ///
    /// Ser staff pisa el <c>Faccion.Status</c> con 7/8/9 — no es cosmético: ese byte viaja en el
    /// <c>CharacterCreate</c> y es lo que hace que el cliente pinte el nick con el color de GM.</summary>
    private void AplicarPrivilegiosDeStaff(Player player)
    {
        // EL RANGO SE DECIDE ACÁ, EN CADA LOGIN, Y ES LA ÚNICA AUTORIDAD. El .chr NO manda: viene con un
        // `Privilegios` guardado, pero no se le cree — se pisa siempre con lo que dice la config. Antes había
        // un `if (_ini.CantidadStaff == 0) return;` que, sin lista de staff, dejaba en pie el rango del .chr:
        // un data/ sembrado con --fixture llevaba un RUGURA.chr con Privilegios=8, y al hostear ESE data/ sin
        // fixture, quien entrara como Rugura seguía siendo Dios. La compuerta se cierra derivando siempre.
        //
        // El GM del fixture se cablea acá (no en el .chr): sólo con --fixture, y sólo por el nombre del seed.
        // Un server de producción tiene _fixtureMode=false, así que este cableado no lo alcanza nunca.
        bool gmDeFixture = _fixtureMode && string.Equals(player.Name, "Rugura", StringComparison.Ordinal);
        byte priv = gmDeFixture
            ? (byte)8                          // Dios: los flows de comandos GM lo exigen
            : _ini.PrivilegioDe(player.Name);  // Server.ini, o 1 (User) si no es staff
        // El status 7/8/9 pinta el nick de GM en el cliente, pero TAMBIÉN es el byte de facción, y de él
        // dependen las reglas de ciudadano/criminal (robar, atacar, las facciones). El GM del fixture recibe
        // el privilegio pero NO el status: los flows de facción y de robo usan a Rugura como sujeto y esperan
        // su facción del seed. Pisársela puso en rojo faction_flow, robar_flow y robar_item_flow.
        byte status = gmDeFixture ? (byte)0 : Data.ServerIni.StatusDeStaff(priv);
        // Lo pisado se guarda aparte y es lo que va al .chr (ver el override de staff en Player): así sacar un
        // nombre del ini realmente le saca el rango, y no le borra la facción al GM que era Armada Real.
        // El override guarda lo civil DEBAJO de lo que pisa el staff, y mientras existe, CambiarFaccion escribe
        // ahí y NO en el status vivo (Player.cs:82). Así que sólo puede existir si de verdad estamos pisando el
        // status: con el GM del fixture (status 0, no se pisa nada) dejarlo puesto congelaba la facción —
        // enlistarse o volverse criminal no tenía efecto, y eso puso en rojo faction_flow, robar_flow y
        // robar_item_flow. El privilegio y el status se guardan por separado, cada uno si corresponde.
        if (priv != 1)
        {
            player.PrivilegioCivilBajoStaff = player.Privileges;
            if (status != 0) player.StatusCivilBajoStaff = player.FactionStatus;
            else player.StatusCivilBajoStaff = null;
        }
        else
        {
            player.PrivilegioCivilBajoStaff = null;
            player.StatusCivilBajoStaff = null;
        }
        player.Privileges = priv;
        if (status != 0) player.FactionStatus = status;
    }

    /// <summary>Binds a loaded/created character to the session and runs the full world-entry sequence.</summary>
    private void SpawnIntoWorld(ClientSession session, Player player, string version)
    {
        player.Connection = session;
        session.Player = player;
        session.MarkAuthed();   // entró al mundo → desarma el timeout de autenticación (SEC-3)
        AplicarPrivilegiosDeStaff(player);
        player.OrigHead = player.Head;   // cabeza viva, para restaurarla al revivir (VB6 OrigChar.Head)
        RefreshAppearance(player, _data); // apariencia inicial desde el equipo: Ropaje de armadura / desnudo + anims de arma/escudo/casco
        if (!player.IsAlive)             // relogueó MUERTO (el .chr guardó Muerto=1): entra como cadáver
        {
            player.Body = CorpseBody; player.Head = CorpseHead;
            player.WeaponAnim = 0; player.ShieldAnim = 0; player.HelmetAnim = 0;
        }
        // Relogueó NAVEGANDO (FLAGS/Navegando del .chr): el cuerpo es la barca —o la fragata fantasmal si está
        // muerto—, igual que en DoNavega. Va DESPUÉS del cadáver: navegando muerto manda la fragata, no el hueso.
        if (player.Navigating)
        {
            player.Body = player.IsAlive ? BoatBody : GhostShipBody;
            player.Head = 0;
            player.WeaponAnim = 0; player.ShieldAnim = 0; player.HelmetAnim = 0;
        }
        // El récord ANTES de sumarlo: `World.Add` lo actualiza solo, así que después de esta línea ya no se
        // puede saber si el que acaba de entrar fue el que lo rompió.
        int recordPrevio = _world.RecordUsuarios;
        _world.Add(player);

        // In-world login sequence — ORDER matches the original server's ConnectUser (TCP.bas:975),
        // verified via the parity harness. Two facts that were wrong in the first draft and are now
        // fixed: (1) LoggedSuccessful (id 0) is sent FIRST, not Logged; (2) Logged (id 1), which
        // carries the per-session XOR key, is sent LAST — the client keeps using key 13 for its
        // outbound packets until it receives it. Omitted here (slice): inventory/spell slots, MOTD
        // multi-lines, and the conditional packets (guild/rain/skills/navigate/mail).
        session.Send(ServerMessages.LoggedSuccessful());
        // Inventory + spell slots — real contents loaded from the character's .chr file.
        for (byte slot = 1; slot <= GameLimits.MaxInventorySlots; slot++)
        {
            var it = player.Inventory[slot];
            session.Send(ServerMessages.ChangeInventorySlot(slot, it.ObjIndex, it.Amount, it.Equipped, salePrice: 0f, usable: (byte)(it.IsEmpty ? 0 : 1)));
        }
        for (byte slot = 1; slot <= GameLimits.MaxUserSpells; slot++)
            session.Send(ServerMessages.ChangeSpellSlot(slot, player.Spells[slot]));
        session.Send(ServerMessages.DumbNoMore());                      // clear stun overlay (VB6 ConnectUser #61)
        session.Send(ServerMessages.UserIndexInServer(player.CharIndex));
        session.Send(ServerMessages.ChangeMap(player.Pos.Map));
        // VB6 ConnectUser (Modulo_UsUaRiOs.bas:1607): justo despues del ChangeMap, si esta lloviendo le
        // manda el clima SOLO a ese usuario. El .NET no lo hacia y el cliente entraba sin lluvia.
        // `Efectivo` y no `At`: si un GM forzó el clima con /LLUVIA, el que entra tiene que ver ESE, no el del
        // reloj. En el original es literalmente la misma global que lee el login (`If Lloviendo Then`).
        byte climaNow = Weather.Efectivo(_loop.Tick);
        if (climaNow != 0) session.Send(ServerMessages.RainToggle(climaNow));
        session.Send(ServerMessages.AreaChanged((byte)player.Pos.X, (byte)player.Pos.Y)); // visible-area init (#64)
        session.Send(ServerMessages.CharacterCreate(player));           // position comes from here
        session.Send(ServerMessages.CreateFX(player.CharIndex, 1, 0));  // FXWARP spawn effect, FX=1 (#71)
        session.Send(ServerMessages.UserCharIndexInServer(player.CharIndex));
        session.Send(ServerMessages.UpdateUserStats(player));
        // SendMOTD + SendUpdate (VB6 ConnectUser, TCP.bas:296-298): el mensaje del día como líneas ConsoleMsg
        // (FontIndex 8) y las novedades como EjecutarAccion(4). Reemplaza el ConsoleMsg único genérico; el
        // contenido viene de ServerContent (data/motd.txt + data/updates.txt, o el default de CoverAO).
        // La ficha del familiar, para que el panel del cliente arranque poblado. SÓLO si el personaje lleva
        // uno, y esa condición es lo importante: el paquete es una extensión nuestra, así que mandarlo siempre
        // le agregaría un paquete AL BOOTSTRAP DE TODOS LOS LOGINS — y el bootstrap es lo primero que compara
        // el oráculo VB6, con lo que las 113 acciones del barrido pasarían a acusar "emite de más". Con este
        // gate, quince de las dieciocho clases entran byte por byte como el original. El cliente arranca con
        // la ficha vacía, así que la ausencia del paquete ya significa "no tiene familiar".
        if (player.Familiar.Existe) session.Send(ServerMessages.FamiliarInfo(player.Familiar));
        foreach (var line in _content.Motd) session.Send(ServerMessages.ConsoleMsg(line, ServerContent.MotdFont));   // SendMOTD
        foreach (var line in _content.Updates) session.Send(ServerMessages.EjecutarAccion(4, line));                 // SendUpdate
        // Preso que reloguea: el VB6 (TCP.bas:368) le recuerda la condena que le queda con LocaleMsg 146.
        if (player.JailMinutesLeft > 0) session.Send(ServerMessages.LocaleMsg(146, player.JailMinutesLeft.ToString()));
        // Los CONDICIONALES del login (VB6 ConnectUser, TCP.bas:312-374). Estaban anotados como TODO desde el
        // primer corte y tres seguían sin portarse; el barrido del oráculo no los ve porque un personaje recién
        // creado no cumple ninguna de las condiciones (no tiene puntos libres, ni clan, ni barco).
        if (player.FreeSkillPoints > 0)   // TCP.bas:312 — el par SendSkills+LevelUp va DENTRO del if
        {
            session.Send(ServerMessages.SendSkills(player.Skills));
            session.Send(ServerMessages.LevelUp(player.FreeSkillPoints));
        }
        if (player.Navigating) session.Send(ServerMessages.NavigateToggle());   // TCP.bas:320 — relogueó en el barco
        SendGuildNewsAlEntrar(session, player);                                 // TCP.bas:346 — modGuilds.SendGuildNews
        // TCP.bas:374 — al staff conectado se le anuncia quién entró (SendTarget.ToADMINS). Un servidor
        // hosteado sin esto deja a los GMs sin ver el movimiento de gente.
        BroadcastToAdmins(ServerMessages.LocaleMsg(489, player.Name));
        // EL RÉCORD DE CONECTADOS (TCP.bas:376-384). El pico ya se venía guardando y el /ONLINE lo muestra,
        // pero el original lo ANUNCIA a todo el mundo en el momento en que se rompe, que es lo que lo hace un
        // evento del servidor y no una estadística. Va después del 489 porque ahí es donde el original ya
        // sumó al recién llegado a NumUsers. Se compara contra el récord de ANTES de sumarlo, y ése sale del
        // disco al arrancar: un server que rearranca no vuelve a cantar un récord que ya había cantado.
        if (_world.OnlineCount > recordPrevio)
            _world.Broadcast(ServerMessages.LocaleMsg(6, _world.OnlineCount.ToString()));

        // FINAL packet: Logged carries the session XOR key. Switch the inbound key only now, since
        // the client won't XOR with it until after this packet is received.
        byte redundance = (byte)_rng.Next(15, 251);
        session.Send(ServerMessages.Logged(redundance));
        session.SetInboundKey(redundance);

        Console.WriteLine($"[{session.RemoteEndpoint}] LOGIN name={player.Name} char={player.CharIndex} {version} online={_world.OnlineCount}");
        _world.Events.Publish(new PlayerLoggedIn(player));

        // Area-scoped visibility (ModAreas): show the newcomer only the players/NPCs in their view
        // window, and announce the newcomer only to observers who can see them.
        foreach (var other in _world.Players)
            if (other.CharIndex != player.CharIndex && World.InView(player.Pos, other.Pos))
                session.Send(ServerMessages.CharacterCreate(other));
        // Objetos del suelo del área (VB6 ModAreas.bas:274): se mandan los que NO son decorado fijo — o sea los
        // ítems tirados y las PUERTAS. Los árboles y carteles nunca viajan: el cliente los dibuja de su mapa
        // (`EsObjetoFijo`, Modulo_UsUaRiOs.bas:2277 + el flag ObjEsFijo del tile). Y por cada puerta van además
        // sus DOS BlockPosition, para que el que llega sepa si está abierta — sin esto, un jugador que loguea
        // después de que alguien la abrió la ve cerrada.
        foreach (var (pos, objIndex, amount) in _world.GroundInView(player.Pos))
        {
            var info = _data.Obj(objIndex);
            if (info is not null && EsObjetoFijo(info.ObjType)) continue;
            session.Send(ServerMessages.ObjectCreate(pos.X, pos.Y, objIndex, amount));
            if (info is { IsDoor: true })
            {
                session.Send(ServerMessages.BlockPosition(pos.X, pos.Y, _data.IsBlockedTile(pos.Map, pos.X, pos.Y)));
                if (pos.X >= 1)
                    session.Send(ServerMessages.BlockPosition((byte)(pos.X - 1), pos.Y,
                                                              _data.IsBlockedTile(pos.Map, pos.X - 1, pos.Y)));
            }
        }
        foreach (var npc in _world.NpcsOnMap(player.Pos.Map))   // InView ya exige mismo mapa: los otros 861 sobran
            if (World.InView(player.Pos, npc.Pos))
                session.Send(ServerMessages.CharacterCreate(npc));
        _world.BroadcastToObservers(player.Pos, ServerMessages.CharacterCreate(player), player.CharIndex);

        // Correo pendiente: el original NO vuelca el buzón al inventario al entrar — sólo prende el indicador
        // de "tenés carta" (TCP.bas:1172, `If .flags.RecibioCorreo > 0 Then WriteMensajeSigno 1`). Las cartas
        // se leen y los adjuntos se retiran en el buzón, con doble clic sobre el objeto de correo.
        if (_world.PeekMail(player.Name).Count > 0) session.Send(ServerMessages.MensajeSigno(true));
        // Y avisarle a los amigos que llegó (VB6 ConnectUser → ObtenerIndexAmigos(_, False)). Va al final,
        // con el jugador ya adentro del mundo: el aviso viaja al socket del OTRO, así que no toca el orden
        // de paquetes de este login. Sólo el 240 de la despedida estaba portado — la llegada era muda.
        AvisarAmigosQueLlegue(player);
    }

    private void HandleWalk(ClientSession session, ByteQueueReader r)
    {
        byte heading = r.ReadByte();
        _loop.Enqueue(() =>
        {
            var p = session.Player;
            if (p is null) return;
            // PARALIZADO: el original no mueve, no gira y NO manda PosUpdate — sólo el aviso 54, y una sola vez
            // (VB6 HandleWalk, Protocol.bas:1632-1640: `If Not .flags.UltimoMensaje = 1`). Acá mandábamos
            // PosUpdate + LocaleMsg en CADA tecla: diez intentos de caminar paralizado eran veinte paquetes
            // contra uno del original, y el cartel parpadeando encima.
            if (p.IsParalyzed(_loop.Tick))
            {
                if (p.UltimoMensaje != 1)
                {
                    p.UltimoMensaje = 1;
                    session.Send(ServerMessages.LocaleMsg(54));           // "No podes moverte porque estas paralizado."
                }
                return;
            }
            // MEDITAR SE CORTA AL CAMINAR (VB6 Protocol.bas:1564-1573), y va ANTES del movimiento: el original
            // apaga el flag aunque el paso después sea rechazado por una pared. Era una CANILLA DE MANÁ: el tick
            // de meditación (Program.cs) regala maná mientras `Meditating` esté prendido y nadie lo apagaba al
            // moverse, así que se podía meditar una vez y recorrer el mundo entero regenerando.
            if (p.Meditating)
            {
                p.Meditating = false;
                session.Send(ServerMessages.MeditateToggle());
                session.Send(ServerMessages.LocaleMsg(123));              // "Dejas de meditar."
                _world.BroadcastToObservers(p.Pos, ServerMessages.EfectoCharParticula(p.CharIndex, (short)ParticleToLevel(p), 0f, true));
            }
            // CAMINAR CORTA LA FAENA (VB6 HandleWalk, Protocol.bas:1587). Sin esto se podía arrancar a pescar y
            // después recorrer el mapa entero sin dejar de producir: el loop no revalida el terreno —el original
            // tampoco—, así que lo único que ata la faena al lugar es justamente este corte. Era una canilla de
            // recursos, y de las caras: los recursos se venden a los NPCs.
            //
            // Ojo el mensaje: acá el original manda `WriteLocaleMsg(391, "", 1)` SIN el argumento de fuente que
            // sí lleva el mismo aviso en `Accion` (`..., 1, 12`). Se replica la diferencia.
            if (p.Laborando)
            {
                p.Laborando = false;
                session.Send(ServerMessages.LocaleMsg(391, "", 1));
            }
            // DESCANSAR SE CORTA AL CAMINAR (VB6 Protocol.bas:1579-1585). Otra canilla, y la más grosa de las
            // tres: `Survival.HealRestingIntervalTicks` cura cada 5 vueltas contra las 1600 de estar parado, o
            // sea 320× más rápido. Sin este corte te sentabas junto a la fogata, salías corriendo y seguías
            // curando a ese ritmo en la otra punta del mapa, para siempre.
            //
            // Acá el original SÍ manda texto literal por ConsoleMsg (no hay LocaleMsg para esto), así que se
            // copia la cadena tal cual.
            if (p.Resting)
            {
                p.Resting = false;
                session.Send(ServerMessages.RestOK());
                session.Send(ServerMessages.ConsoleMsg("Has dejado de descansar.", FuenteInfo));
            }
            // CAMINAR CANCELA EL /SALIR en curso (VB6 CancelExit, Modulo_UsUaRiOs.bas:1908-1921, llamado desde
            // HandleWalk en Protocol.bas:1628). La cuenta regresiva de salida es anti-escape: si te atacan y
            // corrés, el original la cancela. Acá seguía corriendo y te sacaba del mundo igual mientras huías.
            if (p.Saliendo)
            {
                p.Saliendo = false; p.SalirSegundos = 0;
                session.Send(ServerMessages.EjecutarAccion(1));           // el cliente baja el cartel de salida
                session.Send(ServerMessages.LocaleMsg(365, "", 1));       // "Se ha cancelado la salida."
            }
            // Los tres cortes de arriba (meditar/faena/descansar) y este CancelExit van ANTES del paso, no
            // después como en el original: el VB6 llama `MoveUserChar` —que nunca sale del Sub— y sigue de
            // largo, así que un paso rechazado por una pared CORTA IGUAL. Acá el rechazo hace `return`, de modo
            // que ponerlos después los volvería salteables caminando contra una pared. Cambia el orden de los
            // paquetes en el cable respecto del original, no la semántica.
            //
            // Cadencia de movimiento (VB6 IntervaloParaCaminar): rechaza el paso que llega demasiado pronto
            // (anti-speedhack) con snap-back; girar sí se permite. Consumimos el timer aunque el destino esté
            // bloqueado, para que no se pueda spamear contra una pared para saltear el intervalo.
            long nowMs = Environment.TickCount64;
            long walkMs = _admin?.WalkIntervalMs ?? _walkIntervalMs;   // el panel puede ajustarlo en caliente
            if (walkMs > 0 && nowMs - p.LastWalkMs < walkMs)
            {
                p.Heading = heading;
                session.Send(ServerMessages.PosUpdate(p.Pos.X, p.Pos.Y));
                return;
            }
            p.LastWalkMs = nowMs;
            var from = p.Pos;
            var (nx, ny) = Step(from.X, from.Y, heading);
            p.Heading = heading;
            // Borde caminable (VB6 MoveToLegalPos, GameLogic.bas:510): el área jugable real es [9,92]×[7,94] por el
            // tamaño de la ventana de cámara, no 1..100 — el "anillo muerto" del borde nunca se pisa (todos los
            // tile-exits caen dentro de esa caja). Gate de AGUA (PuedeAgua := Navegando): a pie no se entra al agua,
            // navegando sólo se anda por agua. Gate de BLOQUEO (MapData.Blocked → pared). Todos rechazan con snap-back.
            // (Los teleports/spawns bypassean estos gates — sólo el movimiento del jugador se valida, como el VB6.)
            if (nx < GameLimits.MinXBorder || nx > GameLimits.MaxXBorder || ny < GameLimits.MinYBorder || ny > GameLimits.MaxYBorder
                || _data.IsWater(from.Map, nx, ny) != p.Navigating
                || _data.IsBlockedTile(from.Map, nx, ny))
            {
                session.Send(ServerMessages.PosUpdate(from.X, from.Y));
                return;
            }
            // Colisión de personajes (VB6 MoveToLegalPos: `(UserIndex=0 Or IsDeadChar Or IsAdminInvisible) And
            // .npcindex=0`): no se pisa un tile ocupado por un jugador VIVO no-invisible ni por un NPC. Sí se puede
            // pisar un muerto/fantasma (lógica CasperIndex del VB6). El layout del fixture (Program.cs) está diseñado
            // para dejar un camino libre del spawn a cada NPC con este gate activo.
            var destTile = new WorldPos(from.Map, nx, ny);
            var occ = _world.PlayerAt(destTile);
            if ((occ is not null && occ.IsAlive && !occ.Invisible) || _world.NpcAt(destTile) is not null)
            {
                session.Send(ServerMessages.PosUpdate(from.X, from.Y));
                return;
            }
            // ANTI-ESCAPE: recién agredido no se mapea (VB6 Modulo_UsUaRiOs.bas:731). El gate mira el tile DESTINO
            // y rebota ANTES de mover — el jugador se queda donde estaba, no pisa la salida y vuelve a caer. Un
            // MUERTO sí pasa: el original mete el aviso adentro de un `If .flags.Muerto = 0`, así que el fantasma
            // puede irse del mapa donde lo mataron.
            long faltan = p.MapeoBloqueadoHastaTick - _loop.Tick;
            if (faltan > 0 && p.IsAlive && _data.MapExits.ContainsKey((destTile.Map, destTile.X, destTile.Y)))
            {
                int segundos = (int)Math.Ceiling(faltan * GameLoop.TickMs / 1000.0);
                session.Send(ServerMessages.LocaleMsg(488, segundos.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                session.Send(ServerMessages.PosUpdate(from.X, from.Y));
                return;
            }
            p.Pos = new WorldPos(from.Map, nx, ny);
            session.Send(ServerMessages.PosUpdate(nx, ny));                                  // self
            // Acá había un `RevealHidden(p)` con el comentario "caminar rompe el ocultamiento (VB6
            // Protocol.bas:1387)". La cita apunta a `HandleTalk`, no a `HandleWalk`: el original revela al
            // oculto al HABLAR (:1387), al ATACAR (:1719), al robar y al trabajar — pero NUNCA al caminar.
            // `MoveUserChar` sólo le descuenta un crédito al contador del centinela. Con el gate inventado,
            // ocultarse quedaba inservible: un paso y ya te veían.
            RefreshAreaVisibility(_world, p, from, p.Pos);                                   // enter/leave view diffs
            _world.BroadcastToObservers(p.Pos, ServerMessages.CharacterMove(p.CharIndex, nx, ny), p.CharIndex);
            _world.Events.Publish(new PlayerMoved(p, from, p.Pos));
            // Los créditos del centinela se gastan CAMINANDO, no con el reloj: el VB6 los baja de a 1 dentro de
            // `MoveUserChar` (Modulo_UsUaRiOs.bas:802/804), o sea que quien trabaja QUIETO sigue apareciendo en
            // /TRABAJANDO — es lo que hace útil al comando. También bajan al pegar (SistemaCombate.bas:1052).
            if (p.Trabajando > 0) p.Trabajando--;
            if (p.Ocultando > 0) p.Ocultando--;
            // Entrar A CABALLO a un interior (trigger >= 20) o a un dungeon te BAJA del animal (VB6
            // Modulo_UsUaRiOs.bas:718, dentro del propio movimiento). Va acá, con la posición nueva ya aplicada,
            // porque el original mira `.Pos` y no el destino.
            DesmontarSiEntraBajoTecho(session, p);

            // Tile-exit (teleport): stepping onto an exit tile warps the player to its destination map/tile.
            // OJO con el tipo del mapa: es short, no byte — AO tiene 863 mapas. Acá había un `(byte)DestMap`
            // que truncaba: la salida al mapa 555 dejaba al jugador en el 43 (555 & 255).
            if (_data.MapExits.TryGetValue((p.Pos.Map, p.Pos.X, p.Pos.Y), out var dest) && dest.DestMap > 0)
            {
                // EL DESTINO SE VALIDA (VB6 GameLogic.bas:104-114). El original no warpea a ciegas: si el tile
                // de llegada no es legal busca el legal MÁS CERCANO, y si no encuentra ninguno NO TELEPORTA —
                // el jugador se queda donde está. Acá se saltaba entero: había salidas que dejaban al personaje
                // adentro de una pared o encima de otro, y de ahí no se sale caminando.
                var destino = new WorldPos((short)dest.DestMap, (byte)dest.DestX, (byte)dest.DestY);
                bool puedeAgua = p.Navigating;   // VB6 PuedeAtravesarAgua: Navegando o Vuela (Vuela no existe acá)
                // MAPA DE NEWBIES (VB6 GameLogic.bas:93). Si la Zona del mapa de DESTINO es "NEWBIE" y el que
                // pisó la salida ya dejó de serlo —y no es GM—, el original no lo deja pasar: le avisa con
                // LocaleMsg 152 y lo reubica en el tile ESTABLE más cercano a donde está. Que sea estable no
                // es un detalle: cayendo sobre otra salida el rebote no terminaría nunca.
                if (string.Equals(_data.MapZone(destino.Map).Trim(), "NEWBIE", StringComparison.OrdinalIgnoreCase)
                    && p.Level > GameLimits.NewbieMaxLevel && !p.IsGm)
                {
                    session.Send(ServerMessages.LocaleMsg(152, "1%14"));
                    var estable = PosLegalMasCercana(p.Pos, puedeAgua, soloEstable: true);
                    if (estable is not null) TeleportTo(session, p, estable.Value);
                    return;
                }
                if (!PosLegal(destino, puedeAgua))
                {
                    var alterna = PosLegalMasCercana(destino, puedeAgua);
                    if (alterna is null) return;   // sin lugar donde caer: el original tampoco mueve a nadie
                    destino = alterna.Value;
                }
                TeleportTo(session, p, destino);
            }
        });
    }

    /// <summary>¿Se puede PISAR este tile? (VB6 <c>LegalPos</c>, GameLogic.bas:458.) Cuatro condiciones, las
    /// mismas del original: dentro de los bordes jugables, no bloqueado, sin nadie encima —ni jugador ni
    /// criatura— y, salvo que se vaya navegando, que no sea agua.</summary>
    private bool PosLegal(WorldPos pos, bool puedeAgua)
    {
        if (pos.Map <= 0) return false;
        if (pos.X < GameLimits.MinXBorder || pos.X > GameLimits.MaxXBorder
            || pos.Y < GameLimits.MinYBorder || pos.Y > GameLimits.MaxYBorder) return false;
        if (_data.IsBlockedTile(pos.Map, pos.X, pos.Y)) return false;
        if (_world.PlayerAt(pos) is not null || _world.NpcAt(pos) is not null) return false;
        return puedeAgua || !_data.IsWater(pos.Map, pos.X, pos.Y);
    }

    /// <summary>El tile legal más cercano a <paramref name="dest"/> (VB6 <c>ClosestLegalPos</c>,
    /// GameLogic.bas:243): anillos concéntricos de radio 0 a 12 alrededor del destino, y se toma el primero
    /// que sirva. <c>null</c> = no hay ninguno en ese radio, y el original entonces NO mueve al jugador (su
    /// `nPos` queda en 0 y el `If nPos.X &lt;&gt; 0` de arriba no entra).</summary>
    /// <param name="soloEstable">Pide además que el tile NO tenga salida — el <c>ClosestStablePos</c> del
    /// original (GameLogic.bas:295), que es el mismo barrido con un <c>TileExit.Map = 0</c> de más. Es lo
    /// que evita el rebote infinito al sacar a alguien de encima de un portal.</param>
    private WorldPos? PosLegalMasCercana(WorldPos dest, bool puedeAgua, bool soloEstable = false)
    {
        for (int anillo = 0; anillo <= 12; anillo++)
            for (int y = dest.Y - anillo; y <= dest.Y + anillo; y++)
                for (int x = dest.X - anillo; x <= dest.X + anillo; x++)
                {
                    if (x is < 0 or > 255 || y is < 0 or > 255) continue;
                    var cand = new WorldPos(dest.Map, (byte)x, (byte)y);
                    if (!PosLegal(cand, puedeAgua)) continue;
                    if (soloEstable && _data.MapExits.ContainsKey((cand.Map, cand.X, cand.Y))) continue;
                    return cand;
                }
        return null;
    }

    /// <summary>Warp a player to a destination map/tile (tile-exits). Sends ChangeMap on a map change and
    /// refreshes area visibility across the jump — InView is map-aware, so old-map characters are removed
    /// and new-map characters created automatically. The player's own client keeps its char + moves via
    /// PosUpdate.</summary>
    private void TeleportTo(ClientSession session, Player p, WorldPos dest)
    {
        var from = p.Pos;
        // Los DIÁLOGOS se limpian ANTES de mover (VB6 WarpUserChar, Modulo_UsUaRiOs.bas:1594-1595, con este
        // orden): primero al ÁREA VIEJA, para que los que se quedan dejen de ver la frase del que se fue —por
        // eso va con `from`, no con `dest`—, y después al que warpea, para que no se lleve puestos los
        // diálogos de la gente que deja atrás.
        _world.BroadcastToObservers(from, ServerMessages.RemoveCharDialog(p.CharIndex), p.CharIndex);
        session.Send(ServerMessages.RemoveDialogs());
        p.Pos = dest;
        DisembarkIfStranded(p);                           // no dejar a un navegante varado en tierra tras el warp
        DesmontarSiEntraBajoTecho(session, p);            // ni a caballo adentro de un interior/dungeon
        if (dest.Map != from.Map) session.Send(ServerMessages.ChangeMap(dest.Map));
        session.Send(ServerMessages.PosUpdate(dest.X, dest.Y));
        RefreshAreaVisibility(_world, p, from, dest);     // remove old-area chars, create new-area chars (map-aware)
        WarpMascotas(p);                                  // las mascotas viajan con vos (VB6 WarpUserChar:1657)
        WarpFamiliar(p);                                  // y el familiar también (agregado nuestro)
        _world.Events.Publish(new PlayerMoved(p, from, dest));
    }

    /// <summary>
    /// El <c>WarpUserChar</c> del original para un jugador que NO es el que mandó el paquete: los dos caminos
    /// de la cárcel (<c>Encarcelar</c>, Admin.bas:175, y <c>PurgarPenas</c>, Admin.bas:152) lo llaman, y los dos
    /// acá movían <c>Pos</c> a mano.
    ///
    /// Mover la posición sin refrescar el área tiene DOS mitades, y las dos se notan jugando: el preso queda
    /// DIBUJADO donde estaba para todos los que lo veían —se lo llevaron y para el resto sigue parado en medio
    /// de la ciudad— y él llega a la cárcel sin ver a nadie de ahí, arrastrando los personajes del lugar que
    /// dejó. Ninguna de las dos se arregla sola: el cliente dibuja lo que le dijeron hasta que le digan otra cosa.
    /// </summary>
    public void WarpOtroJugador(Player t, WorldPos dest)
    {
        var from = t.Pos;
        _world.BroadcastToObservers(from, ServerMessages.RemoveCharDialog(t.CharIndex), t.CharIndex);
        t.Connection?.Send(ServerMessages.RemoveDialogs());
        t.Pos = dest;
        DisembarkIfStranded(t);                           // ni el preso ni el liberado quedan varados a bordo
        if (dest.Map != from.Map) t.Connection?.Send(ServerMessages.ChangeMap(dest.Map));
        t.Connection?.Send(ServerMessages.PosUpdate(dest.X, dest.Y));
        RefreshAreaVisibility(_world, t, from, dest);
        WarpMascotas(t);                                  // también al preso y al liberado
        WarpFamiliar(t);
        _world.Events.Publish(new PlayerMoved(t, from, dest));
    }

    /// <summary>Si el jugador quedó navegando sobre TIERRA (tras un teleport/regreso al hogar/revive que no
    /// respeta el barco), lo baja del barco server-side — si no, el gate de agua lo dejaría trabado (a pie
    /// no puede entrar al agua y navegando no puede pisar tierra, pero está en tierra). Fuerza el desembarque
    /// y re-broadcastea la apariencia.
    ///
    /// Avisa por <see cref="Player.Connection"/> y no por la sesión que mandó el paquete: hay caminos —la
    /// cárcel— donde el que se mueve NO es el que pidió nada.</summary>
    private void DisembarkIfStranded(Player p)
    {
        if (!p.Navigating || _data.IsWater(p.Pos.Map, p.Pos.X, p.Pos.Y)) return;
        p.Navigating = false;
        p.BoatSlot = 0;
        if (p.IsAlive) { p.Head = p.OrigHead; RefreshAppearance(p, _data); }
        else { p.Body = CorpseBody; p.Head = CorpseHead; }
        p.Connection?.Send(ServerMessages.NavigateToggle());
        _world.BroadcastToObservers(p.Pos, ServerMessages.CharacterChange(p));
    }

    private void HandleAttack(ClientSession session)
    {
        _loop.Enqueue(() =>
        {
            var p = session.Player;
            if (p is null || !p.IsAlive) return;
            // VB6 SeguroCheck(_, 1): attacking requires combat mode. Not in it -> LocaleMsg 102, no swing
            // (Protocol.HandleAttack short-circuits before UsuarioAtaca). This is the ModoCombate gate.
            if (!p.CombatMode) { session.Send(ServerMessages.LocaleMsg(102)); return; }
            // VB6: a meditating user can't attack (silent exit on .flags.Meditando in HandleAttack).
            if (p.Meditating) return;
            // Cadencia de golpe (VB6 IntervaloParaGolpe): no se puede swingear más rápido que el intervalo
            // (anti attack-speed hack / farmeo a velocidad de red). Rechazo silencioso, como el VB6.
            //
            // La forma es la del original (SistemaCombate.bas:978-986) y es un **OR**, no un AND: primero se
            // prueba el ATAJO de pegar-después-de-castear, y sólo si ése no habilita se exige la cadencia
            // normal. Escrito al revés —exigiendo las dos— se rompería todo el melee del juego, porque el
            // atajo vale una sola vez por casteo y después devuelve false para siempre.
            long nowMs = Environment.TickCount64;
            long attackMs = _admin?.AttackIntervalMs ?? _attackIntervalMs;   // el panel puede ajustarlo en caliente
            if (!CombatTiming.PermiteMagiaGolpe(p, nowMs, _intervaloMagiaGolpe)
                && !CombatTiming.PermiteAtacar(p, nowMs, attackMs)) return;
            RevealHidden(p);   // atacar rompe el ocultamiento (VB6 Protocol.bas:1719, SistemaCombate.bas:899)

            // LA ENERGÍA DEL GOLPE (VB6 UsuarioAtaca, SistemaCombate.bas:990-1006). El port no la descontaba:
            // `StaRequerido` llegaba en la data de 83 armas y ObjInfo ni siquiera tenía el campo, así que
            // pelear no cansaba — sin techo de energía el combate no tiene ritmo y no hay por qué descansar.
            //
            // El ORDEN es el del original y decide dos cosas: va DESPUÉS de los intervalos (un golpe que la
            // cadencia rechaza no gasta) y ANTES de resolver el objetivo, así que pegarle al aire cuesta igual.
            // Sin energía suficiente: LocaleMsg 93 y no se pega.
            //
            // El orden de las tres ramas es el del original: primero los NUDILLOS (SistemaCombate.bas:994),
            // después el arma equipada, y sin nada la tirada de 1..10 (RandomNumber, que incluye los dos
            // extremos). Los nudillos de la data cuestan 10, 20 y 30 — pegar con los de oro cansa el triple.
            var armaDelGolpe = Equipment.EquippedOfCategory(p, _data, 7) ?? Equipment.EquippedOfCategory(p, _data, 1);
            int energiaGolpe = armaDelGolpe?.StaRequerido ?? _rng.Next(1, 11);
            if (p.Stamina < energiaGolpe) { session.Send(ServerMessages.LocaleMsg(93)); return; }
            p.Stamina -= energiaGolpe;
            session.Send(ServerMessages.UpdateSta((short)p.Stamina));

            var (tx, ty) = Step(p.Pos.X, p.Pos.Y, p.Heading);   // the tile the attacker faces
            var facedPos = new WorldPos(p.Pos.Map, tx, ty);

            var target = _world.PlayerAt(facedPos);
            if (target is not null && target.CharIndex != p.CharIndex) { AttackPlayer(session, p, target); return; }

            var npc = _world.NpcAt(facedPos);
            if (npc is not null && npc.IsAlive) { AttackNpc(session, p, npc); return; }

            // Swinging at empty air: VB6 UsuarioAtaca plays only SND_SWING here (2.wav), no message.
            const short SwingWave = 2;
            session.Send(ServerMessages.PlayWave(SwingWave, p.Pos.X, p.Pos.Y));
            _world.BroadcastToObservers(p.Pos, ServerMessages.PlayWave(SwingWave, p.Pos.X, p.Pos.Y), p.CharIndex);
        });
    }

    /// <summary>
    /// Con qué habilidad se calcula el poder de ataque (VB6 UserImpactoNpc, SistemaCombate.bas:183-219, y
    /// UserImpactoUser :1073-1121 — los dos hacen la misma selección). Antes esto era Wrestling FIJO, así que
    /// pelear con espada no usaba —ni entrenaba— la habilidad de armas: daba igual el arma que llevaras.
    ///
    /// <c>PoderAtaqueArma</c> y <c>PoderAtaqueWrestling</c> son idénticas en forma (mismos cortes 31/61/91,
    /// el MISMO modificador de clase <c>ModClase.AtaqueArmas</c> y el mismo <c>+2.5 * max(nivel-12, 0)</c>),
    /// así que la fórmula ya portada sirve tal cual: lo único que cambia es de qué skill sale el valor.
    ///
    /// El original mira PRIMERO los nudillos y recién después el arma, así que con nudillos puestos se pelea
    /// —y se entrena— por <c>Wrestling</c> aunque haya un arma en la mochila. Las ramas de proyectil (arco →
    /// Proyectiles, arpón → ArmasArrojadizas) piden el campo <c>proyectil</c> del obj.dat, que hoy no existe en
    /// ObjInfo: quedan diferidas, no inventadas.
    /// </summary>
    private int AttackSkillOf(Player p) =>
        Equipment.EquippedOfCategory(p, _data, 7) is null && Equipment.EquippedOfCategory(p, _data, 1) is not null
            ? GameLimits.SkillArmas : GameLimits.SkillWrestling;

    private void AttackPlayer(ClientSession session, Player p, Player target)
    {
        if (!PuedeAtacar(session, p, target)) return;    // VB6 UsuarioAtacaUsuario:1183 — el primer statement
        // Las mascotas de los DOS se meten en la pelea (VB6 SistemaCombate.bas:1473-1474). Van por
        // AttackedBy + NPCDEFENSA, que es la rama de IA que el port ya tenía (NpcSeguirAgresor).
        MascotasAtacanAlUsuario(victima: target, amo: p);
        MascotasAtacanAlUsuario(victima: p, amo: target);
        FlagCriminalIfLegalTarget(session, p, target);   // atacar a un legal sin provocación → criminal (idéntico en magia)
        // Hit chance: attacker Wrestling power vs target Tacticas evasion (real .chr skills).
        double attackMod = _data.AttackMod.TryGetValue(p.Class, out var am) ? am : 1.0;
        double evasionMod = _data.EvasionMod.TryGetValue(target.Class, out var ev) ? ev : 1.0;
        int atkSkill = AttackSkillOf(p);
        long pa = Combat.PoderAtaqueWrestling(p.Skills[atkSkill], p.Agility, p.Level, attackMod);
        long pe = Combat.PoderEvasion(target.Skills[GameLimits.SkillTacticas], target.Agility, target.Level, evasionMod);
        // EL ESCUDO EN PvP. `PoderEvasionEscudo` tenía UN solo call site en todo el server —la evasión contra
        // NPC (NpcSystem.cs)— así que un escudo equipado era decorativo entre jugadores. El VB6 lo usa en las
        // dos direcciones y hace DOS cosas distintas con él (SistemaCombate.bas:1090-1096 y :1133-1155):
        // primero lo suma a la evasión, y después, SÓLO si el golpe ya falló, tira una segunda chance de
        // rechazo. Acá va la primera mitad; la segunda está abajo.
        var escudoVictima = Equipment.EquippedOfCategory(target, _data, 4);
        if (escudoVictima is not null)
        {
            double shMod = _data.ShieldMod.TryGetValue(target.Class, out var sm) ? sm : 1.0;
            pe += Combat.PoderEvasionEscudo(target.Skills[GameLimits.SkillDefensa], shMod);
        }
        if (_rng.Next(1, 101) > Combat.HitChance(pa, pe))
        {
            // El fallo tambien se muestra: un "0" flotante sobre uno mismo (SistemaCombate.bas:1248).
            session.Send(ServerMessages.DamageOverHead(p.CharIndex, 0));
            target.Connection?.Send(ServerMessages.LocaleMsg(460, p.Name));
            // RECHAZO CON ESCUDO (VB6 SistemaCombate.bas:1136-1151). Segunda tirada, sólo sobre un golpe ya
            // fallado. Y es la ÚNICA vía de entrenamiento de Defensa que tiene el juego: sin esto la skill no
            // subía por uso en ninguna parte —`RaiseSkill(SkillDefensa)` no tenía un solo call site—, así que
            // los puntos que un jugador le pusiera quedaban congelados para siempre.
            if (escudoVictima is not null)
            {
                int probRechazo = Combat.ProbabilidadRechazoEscudo(
                    target.Skills[GameLimits.SkillDefensa], target.Skills[GameLimits.SkillTacticas]);
                if (probRechazo > 0)
                {
                    if (_rng.Next(1, 101) <= probRechazo)
                    {
                        _world.BroadcastToObservers(target.Pos,
                            ServerMessages.PlayWave(SndEscudo, (byte)target.Pos.X, (byte)target.Pos.Y));
                        session.Send(ServerMessages.ConsoleMsg("¡El usuario rechazó el ataque con su escudo!", FuenteInfo));
                        target.Connection?.Send(ServerMessages.ConsoleMsg("¡Has rechazado el ataque con el escudo!", FuenteInfo));
                        RaiseSkill(target.Connection as ClientSession, target, GameLimits.SkillDefensa);
                    }
                }
            }
            return;
        }
        int rawDamage = MeleeDamage(p, contraNpc: false);
        // Victim's equipment absorbs the blow (UserDañoUser): head→helmet, body→armour(+shield).
        int zona = _rng.Next(1, 7); // RandomNumber(bCabeza=1, bTorso=6)
        var casco = Equipment.EquippedOfCategory(target, _data, 3);
        var armadura = Equipment.EquippedOfCategory(target, _data, 2);
        var escudo = Equipment.EquippedOfCategory(target, _data, 4);
        int damage = (int)Combat.AplicarDefensa(rawDamage, zona,
            Equipment.DefRange(casco), Equipment.DefRange(armadura), Equipment.DefRange(escudo),
            (lo, hi) => hi > lo ? _rng.Next(lo, hi + 1) : lo);
        target.Hp = Math.Max(0, target.Hp - damage);
        // Sin linea de consola: el original no narra el golpe, lo muestra (UserDanoUser,
        // SistemaCombate.bas:1355-1356 — dos `WriteChatOverHeadLocale`, uno a cada participante, y
        // NINGUN texto). Y va a los DOS que pelean, no al area: `Write*` escribe a un usuario.
        target.Connection?.Send(ServerMessages.UpdateHP((short)target.Hp));   // VB6 SistemaCombate.bas:749
        session.Send(ServerMessages.DamageOverHead(target.CharIndex, damage));
        target.Connection?.Send(ServerMessages.DamageOverHead(target.CharIndex, damage));
        // El PvP no entrenaba NADA. El VB6 lo hace en UserDañoUser (SistemaCombate.bas:1360-1385), DESPUÉS de
        // aplicar el daño; el gate de hambre/sed ya vive dentro de SubirSkill, así que RaiseSkill lo aplica solo.
        RaiseSkill(session, p, atkSkill);
        // APUÑALAR en PvP (VB6 SistemaCombate.bas:1385). Va con el daño BASE —el de antes de que la armadura
        // se lo coma— y sólo si la víctima sobrevivió al golpe principal.
        if (target.IsAlive && PuedeApunalar(p)) DoApunalar(session, p, null, target, rawDamage);
        // DESARMAR (VB6 `Desarmar`, Trabajo.bas:2269, llamado desde SistemaCombate.bas:1221): un ladrón(5),
        // bardo(6) o gladiador(8) puede arrancarle el arma a su víctima al pegarle. La probabilidad sale del
        // Wrestling y el nivel DEL ATACANTE, no de la víctima.
        //
        // OJO el mensaje, que es una rareza de la fuente y se porta igual: el aviso "¡Tu oponente te ha
        // desarmado!" sólo se manda si la víctima es de nivel < 20 — el original avisa al novato y deja que el
        // veterano se dé cuenta solo.
        //
        // El `DoDesequipar` que el original llama en la línea siguiente NO se porta, y no por falta de ganas:
        // su gate es `If Not Clase = Gladiador Or Not Clase = Bardo Then Exit Sub`, que es **siempre verdadero**
        // para cualquier clase (si es Gladiador da `False Or True`; si es Bardo, `True Or False`; si es otra,
        // `True Or True`). O sea que la función sale SIEMPRE y nunca desequipa nada: es código muerto por un bug
        // de lógica, del mismo tipo que los de precedencia que ya documenta fase5/GAPS-CODIGO-MUERTO.md.
        // OJO el `target.IsAlive`, que NO está: en el original la secuencia es `UserIncinera` → `Desarmar` →
        // `GolpeParalizaUsuario` → **`UserDañoUser`**, o sea que desarmar corre ANTES de aplicar el daño y por
        // lo tanto también desarma al que muere con ese mismo golpe. Acá el daño ya se aplicó arriba, así que
        // condicionarlo a que siga vivo le sacaría el arma a todos MENOS al que matás — justo al revés.
        if (p.Class is 5 or 6 or 8)
        {
            int prob = Combat.ProbabilidadDesarmar(p.Skills[GameLimits.SkillWrestling], p.Level);
            if (_rng.Next(1, 101) <= prob)
            {
                int slot = EquippedSlotOfCategory(target, 1);   // el arma equipada de la víctima
                if (slot > 0)
                {
                    var inv = target.Inventory[slot];
                    inv.Equipped = false;
                    target.Inventory[slot] = inv;
                    var arma = _data.Obj(inv.ObjIndex);
                    if (arma is not null) ClearEquipAnim(target, arma);
                    target.Connection?.Send(ServerMessages.ChangeInventorySlot((byte)slot, inv.ObjIndex, inv.Amount, false, 0f, 1));
                    if (arma is not null) BroadcastEquipChange(target, arma, equipped: false);
                    if (target.Level < 20)
                        target.Connection?.Send(ServerMessages.ConsoleMsg("¡Tu oponente te ha desarmado!", FuenteCombate));
                }
            }
        }
        GolpeParalizaUsuario(session, p, target);
        if (target.Hp == 0) OnKill(session, p, target);
    }

    /// <summary>
    /// EL GOLPE QUE PARALIZA (VB6 <c>GolpeParalizaUsuario</c>, SistemaCombate.bas:2254, llamado desde la línea
    /// 1225 justo antes de <c>UserDañoUser</c>): pelear A MANO LIMPIA puede dejar clavada a la víctima.
    ///
    /// El gate del original es <c>Orbe = False And WeaponEqpObjIndex = 0</c>, o sea que la función sólo hace algo
    /// si el atacante NO tiene arma equipada; con cualquier fierro en la mano no pasa nada. Eso le da sentido al
    /// dado más chico de gladiador y bardo, que son los que pelean sin arma.
    ///
    /// La OTRA rama del original —la del "orbe"— no se porta porque es <b>inalcanzable con esta data</b>: pide
    /// munición equipada con <c>Paraliza &gt; 0</c> y en obj.dat no hay UN SOLO objeto que declare ese campo (ni
    /// siquiera la Flecha Paralizante, 1085). La línea que activaba el orbe de verdad
    /// (<c>ItemsMagicosEqpObjIndex = 869</c>) está comentada en la fuente. Ver fase5/GAPS-CODIGO-MUERTO.md.
    ///
    /// La probabilidad es la MISMA que desarmar (Wrestling*0.2 + nivel*0.66) pero contra un dado mucho más
    /// grande: 1..500, y 1..300 para gladiador y bardo.
    /// </summary>
    private void GolpeParalizaUsuario(ClientSession session, Player p, Player target)
    {
        long now = _loop.Tick;
        if (target.IsParalyzed(now)) return;                                    // ya paralizado/inmovilizado → sale
        if (Equipment.EquippedOfCategory(p, _data, 1) is not null) return;      // con arma equipada no aplica
        int prob = Combat.ProbabilidadDesarmar(p.Skills[GameLimits.SkillWrestling], p.Level);
        if (_rng.Next(1, Combat.TopeDadoGolpeParaliza(p.Class) + 1) > prob) return;
        // Counters.Paralisis = 150. El original corre su loop cada GAME_TIMER_INTERVAL = 40 ms (mMainLoop.bas:7),
        // exactamente el TickMs del port, así que los contadores son ticks 1:1: 150 ticks = 6 segundos.
        target.ParalyzedUntilTick = now + 150;
        target.ParalyzedByCharIndex = p.CharIndex;
        session.Send(ServerMessages.LocaleMsg(135, target.Name));             // "Has paralizado a #1"
        target.Connection?.Send(ServerMessages.LocaleMsg(134, p.Name));         // "Has sido paralizado por el ataque de #1"
        if (!target.ClientParalyzed) { target.ClientParalyzed = true; target.Connection?.Send(ServerMessages.ParalizeOK()); }
        // El wave suena en la posición del ATACANTE (el `.Pos` del original está dentro de `With UserList(UserIndex)`)
        // pero se difunde al área de la VÍCTIMA; el FX 8 va sobre la víctima.
        _world.BroadcastToObservers(target.Pos, ServerMessages.PlayWave(17, p.Pos.X, p.Pos.Y));
        _world.BroadcastToObservers(target.Pos, ServerMessages.CreateFX(target.CharIndex, 8, 0));
    }


    /// <summary>Slot del inventario con el ítem equipado de esa categoría (o 0). Para consumir munición hace
    /// falta el slot, no sólo el ObjInfo.</summary>
    private int EquippedSlotOfCategory(Player p, int category)
    {
        for (int i = 1; i <= GameLimits.MaxInventorySlots; i++)
        {
            var inv = p.Inventory[i];
            if (inv.IsEmpty || !inv.Equipped) continue;
            var o = _data.Obj(inv.ObjIndex);
            if (o is not null && Equipment.Category(o) == category) return i;
        }
        return 0;
    }

    /// <summary>NPC atacable a distancia (VB6 Protocol.bas:2860 'If Attackable &lt;&gt; 0' + roles de servicio del melee).</summary>
    private static bool RangedNpcAtacable(Npc npc) =>
        npc.Attackable && !npc.IsMerchant && !npc.IsBanker && !npc.IsReviver && !npc.IsWorkbench && !npc.IsTrainer && npc.FactionConvert == 0;

    /// <summary>
    /// Disparo con arco (VB6 HandleWorkLeftClick, Case eSkill.Proyectiles, Protocol.bas:2751-2916). El objetivo
    /// sale del tile clickeado (X,Y), no del tile encarado. Orden fiel al VB6: gate arma/munición → stamina →
    /// resolver objetivo → gates de visión/distancia por tipo → daño → consumo de munición. Al impactar, el daño =
    /// tirada del arco + tirada de la flecha (la skill Proyectiles decide el acierto). La flecha se consume DESPUÉS
    /// del intento, aun si falla el golpe o va al aire — pero NO si el disparo se rechaza antes (visión/auto/cadáver/
    /// distancia salen con Exit Sub, antes del bloque de consumo). Sólo se modela el arco (Proyectil=1); el arpón
    /// (Proyectil=2) es código muerto en esta data.
    /// </summary>
    private void RangedShot(ClientSession session, Player p, byte tx, byte ty)
    {
        // Acá había un `if (!p.CombatMode) → LocaleMsg(102)` con el comentario "el VB6 Case Proyectiles no lo
        // pide explícito, pero evita disparos accidentales". Era un gate INVENTADO, y de los caros: con el arco
        // equipado y sin modo combate, el original dispara —gasta stamina, consume la flecha y hace daño— y el
        // port respondía "debés estar en modo combate" y no hacía nada. Leí el `Case eSkill.Proyectiles`
        // entero (Protocol.bas:2751-2916): no hay `SeguroCheck` ni equivalente. El seguro es del MELEE
        // (UsuarioAtaca), no del disparo.

        // Gate arma/munición (VB6 Protocol.bas:2766-2811, DummyInt): sin arma equipada → LocaleMsg 127 (DummyInt=1);
        // arma equipada que NO es arco (Proyectil<>1) → SILENCIO (DummyInt=2, el VB6 sólo desequipa la munición y sale);
        // munición inválida/sin stock → LocaleMsg 127 (DummyInt=1). El VB6 además desequipa arco/munición en el error,
        // limpieza defensiva de un estado que en este stack no se alcanza por vías legítimas (se omite ese detalle).
        var bow = Equipment.EquippedOfCategory(p, _data, 1);
        if (bow is null) { session.Send(ServerMessages.LocaleMsg(127)); return; }     // DummyInt=1: sin arma equipada
        if (!bow.IsBow) return;                                                       // DummyInt=2: arma no-proyectil → sin mensaje
        int ammoSlot = EquippedSlotOfCategory(p, 5);
        var ammo = ammoSlot == 0 ? null : _data.Obj(p.Inventory[ammoSlot].ObjIndex);
        if (bow.Municiones == 1 && (ammoSlot == 0 || ammo is null || !ammo.IsAmmo || p.Inventory[ammoSlot].Amount < 1))
        { session.Send(ServerMessages.LocaleMsg(127)); return; }

        // Stamina (VB6 Protocol.bas:2813-2822): el disparo EXIGE un piso de 10 de energía; si no lo tiene, LocaleMsg 93
        // y NO dispara. El descuento —QuitarSta(RandomNumber(1,10))— ocurre ACÁ, antes de resolver el objetivo, así que
        // hasta el tiro al aire cuesta stamina. Sin esto el arco era una canilla de recurso (farmeo/PvP indefinido con
        // 0 de energía): el gate de UseItem (HandleUseItem) es esquivable mandando WorkLeftClick directo. QuitarSta
        // clampa a 0 y refleja el stat (Trabajo.bas:1816 → WriteUpdateSta).
        if (p.Stamina < 10) { session.Send(ServerMessages.LocaleMsg(93)); return; }
        p.Stamina -= _rng.Next(1, 11);                 // RandomNumber(1, 10)
        if (p.Stamina < 0) p.Stamina = 0;
        session.Send(ServerMessages.UpdateSta((short)p.Stamina));

        // Objetivo por tile clickeado (VB6 LookatTile, GameLogic.bas:676): sólo "ve" el objetivo si el tile está dentro
        // de RANGO_VISION (|dX|<=8 y |dY|<=6); fuera de eso NO fija objetivo → tiro al aire. Este gate SUBSUME los de
        // visión/distancia posteriores del handler: un objetivo visible está a lo sumo a 8+6=14 de Manhattan, por debajo
        // de MAXDISTANCIAARCO=18, así que el chequeo de distancia (usuario '>18' / NPC '>=18', off-by-one del original)
        // y el gate de visión por eje quedan como código muerto y no se replican. Sólo restan los rechazos alcanzables:
        // auto-disparo (298) y cadáver (7 vía PuedeAtacar) — ambos salen con Exit Sub, antes del consumo, así que NO gastan.
        var pos = new WorldPos(p.Pos.Map, tx, ty);
        bool visible = Math.Abs(tx - p.Pos.X) <= RangoVisionX && Math.Abs(ty - p.Pos.Y) <= RangoVisionY;
        var targetPlayer = visible ? _world.PlayerAt(pos) : null;
        var targetNpc = visible ? _world.NpcAt(pos) : null;

        if (targetPlayer is not null)
        {
            if (targetPlayer.CharIndex == p.CharIndex) { session.Send(ServerMessages.LocaleMsg(298)); return; }   // auto-disparo (VB6 :2840)
            if (!PuedeAtacar(session, p, targetPlayer)) return;   // el mismo gate del melee — no consume munición
            RangedHitPlayer(session, p, targetPlayer, bow, ammo);
        }
        else if (targetNpc is not null)
        {
            // Sólo se ataca si el NPC es atacable (VB6 :2860). Si NO lo es, el VB6 no manda mensaje e IGUAL consume la
            // flecha (Atacked queda True) — a diferencia del melee, que sí avisa con LocaleMsg 298.
            if (RangedNpcAtacable(targetNpc))
                RangedHitNpc(session, p, targetNpc, bow, ammo);
        }
        // TIRO AL AIRE: el original NO hace ruido. Su `Case eSkill.Proyectiles` (Protocol.bas:2751-2870) no tiene
        // un solo PlayWave — cuando no hay blanco simplemente marca `Atacked` y consume la flecha. Acá salía un
        // `PlayWave(6)`, que además es SND_NIVEL: errar una flecha sonaba igual que subir de nivel.

        // Consumo de munición: 1 flecha DESPUÉS del intento (VB6 Protocol.bas:2870 'If Atacked'). Los early-returns de
        // arriba (visión/auto/cadáver/distancia) NO llegan acá → NO consumen, fiel al VB6.
        if (bow.Municiones == 1 && ammoSlot != 0)
        {
            var inv = p.Inventory[ammoSlot];
            inv.Amount = (short)(inv.Amount - 1);
            if (inv.Amount <= 0) inv = default;   // se acabó: el slot queda vacío (y desequipado)
            p.Inventory[ammoSlot] = inv;
            session.Send(ServerMessages.ChangeInventorySlot((byte)ammoSlot, inv.ObjIndex, inv.Amount, inv.Equipped, 0f, inv.IsEmpty ? (byte)0 : (byte)1));
        }
    }

    /// <summary>Daño de un disparo (VB6 CalcularDaño rama proyectil): tirada del arco + tirada de la flecha,
    /// con el modificador de clase de DAÑO de proyectil. El máximo NO suma el de la flecha (bug intencional del
    /// original, línea comentada "For some reason this isn't done").</summary>
    private int RangedDamage(Player p, Data.ObjInfo bow, Data.ObjInfo? ammo)
    {
        long userHit = p.MaxHit >= p.MinHit ? _rng.Next(p.MinHit, p.MaxHit + 1) : p.MinHit;
        long weaponRoll = bow.MaxHit >= bow.MinHit ? _rng.Next(bow.MinHit, bow.MaxHit + 1) : bow.MinHit;
        if (bow.Municiones == 1 && ammo is not null)
            weaponRoll += ammo.MaxHit >= ammo.MinHit ? _rng.Next(ammo.MinHit, ammo.MaxHit + 1) : ammo.MinHit;
        double classMod = _data.ProjectileDamageMod.TryGetValue(p.Class, out var dm) ? dm : 1.0;
        return Math.Max(1, (int)Combat.CalcularDano(weaponRoll, bow.MaxHit, userHit, p.Strength, classMod));
    }

    /// <summary>Poder de ataque a distancia (VB6 PoderAtaqueProyectil): misma forma que Wrestling pero con la
    /// skill Proyectiles y el modificador de clase de ataque de proyectil.</summary>
    private long RangedAttackPower(Player p)
    {
        double mod = _data.ProjectileAttackMod.TryGetValue(p.Class, out var am) ? am : 1.0;
        return Combat.PoderAtaqueWrestling(p.Skills[GameLimits.SkillProyectiles], p.Agility, p.Level, mod);
    }

    /// <summary>Aplica un disparo a un NPC ya validado como atacable/en-rango por RangedShot (VB6 UsuarioAtacaNpc).
    /// El gate de atacable y los de rango viven en el llamador; acá sólo va la provocación + tirada de acierto + daño.</summary>
    private void RangedHitNpc(ClientSession session, Player p, Npc npc, Data.ObjInfo bow, Data.ObjInfo? ammo)
    {
        RevealOnAttack(p);   // disparar a un NPC revela ocultamiento e invisibilidad por hechizo (VB6 UsuarioAtacaNpc)
        npc.AttackedBy = p.Name; npc.Hostile = true;   // provocar (igual que el melee), aun si falla
        if (_rng.Next(1, 101) > Combat.HitChance(RangedAttackPower(p), npc.Template.PoderEvasion))
        { session.Send(ServerMessages.DamageOverHead(p.CharIndex, 0)); return; }   // el fallo, sin texto
        int damage = RangedDamage(p, bow, ammo) - npc.Template.Def;
        // El disparo gotea experiencia igual que la espada, y con el MISMO orden (antes de aplicar el daño):
        // en el original el arco no tiene un camino propio — `UsuarioAtacaNpc` ramifica por proyectil sólo
        // para elegir el sonido y la munición (SistemaCombate.bas:916-920) y después cae en el mismo
        // `UserDañoNpc` (:932) que el melee, que es quien llama a `CalcularDarExp` en :470.
        CalcularDarExp(session, p, npc, damage);
        npc.Hp = Math.Clamp(npc.Hp - damage, 0, npc.Template.MaxHp);
        // Sin texto y solo al tirador (UserDanoNpc, SistemaCombate.bas:467).
        session.Send(ServerMessages.DamageOverHead(npc.CharIndex, damage));
        RaiseSkill(session, p, GameLimits.SkillProyectiles);   // el arco entrena Proyectiles
        if (npc.Hp == 0) NpcKilledByPlayer(session, p, npc);
    }

    private void RangedHitPlayer(ClientSession session, Player p, Player target, Data.ObjInfo bow, Data.ObjInfo? ammo)
    {
        FlagCriminalIfLegalTarget(session, p, target);
        double evasionMod = _data.EvasionMod.TryGetValue(target.Class, out var ev) ? ev : 1.0;
        long pe = Combat.PoderEvasion(target.Skills[GameLimits.SkillTacticas], target.Agility, target.Level, evasionMod);
        if (_rng.Next(1, 101) > Combat.HitChance(RangedAttackPower(p), pe))
        {
            session.Send(ServerMessages.DamageOverHead(p.CharIndex, 0));   // el fallo, sin texto
            target.Connection?.Send(ServerMessages.LocaleMsg(460, p.Name));
            return;
        }
        int rawDamage = RangedDamage(p, bow, ammo);
        int zona = _rng.Next(1, 7);
        var casco = Equipment.EquippedOfCategory(target, _data, 3);
        var armadura = Equipment.EquippedOfCategory(target, _data, 2);
        var escudo = Equipment.EquippedOfCategory(target, _data, 4);
        int damage = (int)Combat.AplicarDefensa(rawDamage, zona,
            Equipment.DefRange(casco), Equipment.DefRange(armadura), Equipment.DefRange(escudo),
            (lo, hi) => hi > lo ? _rng.Next(lo, hi + 1) : lo);
        target.Hp = Math.Max(0, target.Hp - damage);
        // Sin linea de consola: el original no narra el golpe, lo muestra (UserDanoUser,
        // SistemaCombate.bas:1355-1356 — dos `WriteChatOverHeadLocale`, uno a cada participante, y
        // NINGUN texto). Y va a los DOS que pelean, no al area: `Write*` escribe a un usuario.
        target.Connection?.Send(ServerMessages.UpdateHP((short)target.Hp));   // VB6 SistemaCombate.bas:749
        session.Send(ServerMessages.DamageOverHead(target.CharIndex, damage));
        target.Connection?.Send(ServerMessages.DamageOverHead(target.CharIndex, damage));
        RaiseSkill(session, p, GameLimits.SkillProyectiles);
        if (target.Hp == 0) OnKill(session, p, target);
    }

    /// <summary>
    /// El gate FACCIONARIO de <c>PuedeAtacarNPC</c> (VB6 SistemaCombate.bas:1752), el que corre después del
    /// 298 y del gate de distancia: <b>un Armada Real no puede atacar directamente a una criatura sin
    /// dueño</b> — LocaleMsg 272 y el ataque no sale. Vale para el melee, la magia y el tiro, porque en el
    /// original los tres pasan por la misma función.
    ///
    /// OJO con el comentario del original ("No era un Guardia, asi que es una criatura No-Hostil común"):
    /// el <c>If</c> de hostilidad que lo acotaba <b>no está</b> en este código —quedó la sangría huérfana—,
    /// así que la regla alcanza a TODA criatura sin dueño. El veredicto lo da la línea que corre, no el
    /// comentario que la acompaña.
    ///
    /// El original anida el chequeo bajo <c>If Not esRene</c>, que es redundante (renegado es status 1 y
    /// Armada 5: nunca los dos a la vez); se deja escrito igual para que se lea como el original.
    ///
    /// La OTRA rama que emite 272 —mascota de un Caos atacada por un Caos, :1798— es <b>inalcanzable</b>:
    /// vive dentro del <c>Else</c> de <c>If Not esRene(dueño)</c>, o sea con el dueño en status 1, y ahí
    /// pregunta <c>esCaos(dueño)</c>, que es status 4. No se porta.
    /// </summary>
    private bool ArmadaPuedeAtacarNpc(ClientSession session, Player p, Npc npc)
    {
        if (npc.OwnerCharIndex != 0) return true;                       // VB6: `If .MaestroUser = 0`
        if (p.FactionStatus == Player.StatusCriminal || !p.IsArmada) return true;
        session.Send(ServerMessages.LocaleMsg(272));
        return false;
    }

    private void AttackNpc(ClientSession session, Player p, Npc npc)
    {
        // VB6 NpcAtacable (SistemaCombate.bas:1737): no se atacan los NPCs de servicio. Se rechaza si el NPC
        // no es atacable (Attackable=0 en el NPCs.dat: sacerdotes/guardias/veterinarias reales) O tiene un rol
        // de servicio (los NPCs de test de Program.cs traen su flag pero el default Attackable=true). LocaleMsg 298.
        if (!npc.Attackable || npc.IsMerchant || npc.IsBanker || npc.IsReviver || npc.IsWorkbench || npc.IsTrainer || npc.FactionConvert != 0)
        { session.Send(ServerMessages.LocaleMsg(298)); return; }
        if (!ArmadaPuedeAtacarNpc(session, p, npc)) return;   // VB6 PuedeAtacarNPC:1752 — va después del 298
        RevealOnAttack(p);   // golpear a un NPC revela ocultamiento e invisibilidad por hechizo (VB6 UsuarioAtacaNpc, SistemaCombate.bas:898)
        // VB6 NPCAtacado (Modulo_UsUaRiOs.bas:1106), llamado desde UsuarioAtacaNpc (SistemaCombate.bas:906) ANTES
        // de la tirada de acierto — o sea que un golpe FALLADO igual provoca. El NPC recuerda a su agresor y se
        // vuelve hostil para defenderse; NpcSystem lo devuelve a su hostilidad original en cuanto no le quede
        // nadie adyacente. Para un NPC pacífico esto es lo único que lo hace pegar.
        npc.AttackedBy = p.Name;
        npc.Hostile = true;
        double attackMod = _data.AttackMod.TryGetValue(p.Class, out var am) ? am : 1.0;
        int atkSkill = AttackSkillOf(p);
        long pa = Combat.PoderAtaqueWrestling(p.Skills[atkSkill], p.Agility, p.Level, attackMod);
        if (_rng.Next(1, 101) > Combat.HitChance(pa, npc.Template.PoderEvasion))
        {
            session.Send(ServerMessages.DamageOverHead(p.CharIndex, 0));   // el fallo, sin texto
            return;
        }
        // VB6 UserImpactoNpc:216-218: entrena UNA sola skill —la que efectivamente usó— y SÓLO al acertar.
        // Antes esto subía Wrestling Y Tácticas, siempre, incluso al fallar. Tácticas no es basura: el VB6 la
        // sube en NpcAtacaUser:759, o sea que la entrena el DEFENSOR cuando un NPC le pega, no el atacante.
        RaiseSkill(session, p, atkSkill);
        // EL QUEJIDO DE LA CRIATURA AL RECIBIR EL GOLPE (VB6 SistemaCombate.bas:909). Va acá y no en otro lado:
        // el original lo emite apenas `UserImpactoNpc` devuelve verdadero —o sea después de la tirada de
        // acierto y del entrenamiento de skill, que viven adentro de esa función— y ANTES del golpe que
        // paraliza (:930) y del daño. El orden de las emisiones es parte del contrato que mide el oráculo.
        NpcSystem.SonidoDeCriatura(_world, npc, npc.Template.Snd2);
        GolpeParalizaNpc(session, p, npc);   // VB6 SistemaCombate.bas:930, entre el impacto y el daño
        // UserDañoNpc (SistemaCombate.bas:465): daño = dañoBase - npc.def, WITHOUT a floor at 0 — when the
        // player's raw damage is below the NPC's defence the result is negative and the NPC heals (VB6 parity).
        // `danoBase` se calcula UNA vez: apuñalar lo reusa, y volver a llamar a MeleeDamage tiraría el dado
        // de nuevo — la puñalada saldría de un golpe distinto del que acaba de pegar.
        int danoBase = MeleeDamage(p, contraNpc: true);
        int damage = danoBase - npc.Template.Def;
        // La exp se gotea ANTES de aplicar el daño (VB6 SistemaCombate.bas:470 contra :472), porque el recorte
        // usa el HP que la criatura TODAVÍA tiene: el golpe que remata cobra por lo que quedaba, no por el
        // exceso. Invertir estas dos líneas rompería esa cuenta en silencio.
        CalcularDarExp(session, p, npc, damage);
        npc.Hp = Math.Clamp(npc.Hp - damage, 0, npc.Template.MaxHp);
        // Sin texto y solo al que pega (UserDanoNpc, SistemaCombate.bas:467). El comentario que estaba
        // aca decia que el original difundia con `SendData ToPCArea` y era falso: usa `WriteChat...`,
        // que escribe a UN usuario.
        //
        // Y VA SOBRE LA CABEZA DEL QUE PEGA, no de la criatura: el original ancla el numero en
        // `UserList(UserIndex).Char.CharIndex` (:467). Acá iba sobre el NPC, que además era incoherente con
        // nuestro propio FALLO —tres líneas más arriba— que ya usaba el CharIndex del jugador.
        session.Send(ServerMessages.DamageOverHead(p.CharIndex, damage));
        // APUÑALAR: golpe extra, y SÓLO si la criatura sobrevivió al principal (VB6 SistemaCombate.bas:474-482,
        // `If .Stats.MinHP > 0 Then ... DoApuñalar`). Va con el daño BASE, no con el ya mermado por la defensa.
        if (npc.Hp > 0 && PuedeApunalar(p)) DoApunalar(session, p, npc, null, danoBase);
        if (npc.Hp == 0) NpcKilledByPlayer(session, p, npc);
    }

    /// <summary>
    /// EL GOLPE QUE PARALIZA A LA CRIATURA (VB6 <c>GolpeParalizaNPC</c>, SistemaCombate.bas:2204, llamado desde
    /// la línea 930 entre el impacto y el daño). Hermana de <see cref="GolpeParalizaUsuario"/>, con dos
    /// diferencias del original: dura mucho menos (30 ticks = 1,2 s contra 150) y acá la rama del "orbe" SÍ está
    /// viva, porque no pide un campo que la data no tiene sino un ObjIndex concreto.
    ///
    /// Esa rama es una rareza que se porta igual de rara: prende con la <b>Flecha Paralizante (1085) equipada
    /// junto a un arma que NO sea de proyectil</b> —flecha con espada, sí— y entonces paraliza con un 5/35 fijo,
    /// sin mirar skill ni nivel. El original no exige arco para equipar munición (InvUsuario.bas:1118), así que
    /// la combinación es alcanzable.
    /// </summary>
    private void GolpeParalizaNpc(ClientSession session, Player p, Npc npc)
    {
        long now = _loop.Tick;
        if (npc.ParalyzedUntilTick > now) return;   // ya paralizada/inmovilizada → sale
        var arma = Equipment.EquippedOfCategory(p, _data, 1);
        int ammoSlot = EquippedSlotOfCategory(p, 5);
        bool orbe = arma is not null && arma.Proyectil == 0 && ammoSlot > 0 && p.Inventory[ammoSlot].ObjIndex == 1085;
        bool paraliza;
        if (!orbe && arma is null)          // a mano limpia: misma fórmula que desarmar contra un dado grande
        {
            int prob = Combat.ProbabilidadDesarmar(p.Skills[GameLimits.SkillWrestling], p.Level);
            paraliza = _rng.Next(1, Combat.TopeDadoGolpeParaliza(p.Class) + 1) <= prob;
        }
        else if (orbe) paraliza = _rng.Next(1, 36) <= 5;
        else return;                        // con arma y sin orbe el original no hace nada
        if (!paraliza) return;
        npc.ParalyzedUntilTick = now + 30;
        session.Send(ServerMessages.ConsoleMsg("Tu golpe ha paralizado a la criatura.", FuenteInfo));
        _world.BroadcastToObservers(npc.Pos, ServerMessages.PlayWave(17, npc.Pos.X, npc.Pos.Y));
        _world.BroadcastToObservers(npc.Pos, ServerMessages.CreateFX(npc.CharIndex, 8, 0));
    }

    /// <summary>
    /// Aprender haciendo: intenta subir la skill que el jugador acaba de usar (VB6 <c>SubirSkill</c>,
    /// Modulo_UsUaRiOs.bas:1185). Si sube, avisa con LocaleMsg 454 —con el sigilo '*' que el cliente
    /// resuelve al nombre de la habilidad— y regala los 10 de experiencia del original, que incluso
    /// pueden hacer subir de nivel.
    /// </summary>
    /// <summary>¿Puede apuñalar? (VB6 <c>PuedeApuñalar</c>, <c>Modulo_UsUaRiOs.bas:1175-1180</c>): necesita un
    /// arma con <c>Apuñala=1</c> equipada Y (skill Puñales ≥ 10 O ser Asesino). El asesino apuñala desde el
    /// primer día; el resto tiene que entrenar hasta 10 aunque tenga la daga en la mano.</summary>
    private bool PuedeApunalar(Player p)
    {
        var arma = Equipment.EquippedOfCategory(p, _data, 1);
        if (arma is null || arma.Apunala != 1) return false;
        return p.Skills[GameLimits.SkillApunalar] >= GameLimits.MinApunalar || p.Class == 4;
    }

    /// <summary>
    /// APUÑALAR POR LA ESPALDA (VB6 <c>DoApuñalar</c>, <c>Trabajo.bas:1753</c>). Un golpe extra que sale
    /// DESPUÉS del golpe normal y sólo si la víctima lo sobrevivió (<c>SistemaCombate.bas:474-482</c>).
    ///
    /// <para>Dos detalles que no son intercambiables y por eso van explícitos: contra un NPC el daño es
    /// <c>Int(daño*2)</c> —truncado— y contra un jugador es <c>Round(daño*1.4)</c> del asesino o
    /// <c>Round(daño*1.5)</c> del resto —redondeado—. Dos conversiones distintas en la misma función.</para>
    ///
    /// <para>Y el daño de entrada es el daño BASE, antes de restarle la defensa de la víctima: el original
    /// pasa <c>DañoBase</c>, no el daño ya aplicado.</para>
    /// </summary>
    private void DoApunalar(ClientSession session, Player p, Npc? npc, Player? victima, int danoBase)
    {
        if (_rng.Next(0, 101) >= Combat.SuerteApunalar(p.Class, p.Skills[GameLimits.SkillApunalar]))
        {
            session.Send(ServerMessages.LocaleMsg(79));   // "¡No has logrado apuñalar a tu enemigo!"
            return;
        }
        if (victima is not null)
        {
            int dano = (int)Math.Round(danoBase * (p.Class == 4 ? 1.4 : 1.5), MidpointRounding.ToEven);
            victima.Hp = Math.Max(0, victima.Hp - dano);
            session.Send(ServerMessages.ConsoleMsg($"Has apuñalado a {victima.Name} por {dano}.", FuenteCombate));
            victima.Connection?.Send(ServerMessages.ConsoleMsg($"Te ha apuñalado {p.Name} por {dano}.", FuenteCombate));
            victima.Connection?.Send(ServerMessages.UpdateHP((short)victima.Hp));
            // El apunalar del original manda SOLO texto (Trabajo.bas:1792): el flotante era agregado nuestro.
            if (victima.Hp == 0) OnKill(session, p, victima);
        }
        else if (npc is not null)
        {
            int dano = danoBase * 2;   // Int(daño*2): truncado, no redondeado
            npc.Hp = Math.Clamp(npc.Hp - dano, 0, npc.Template.MaxHp);
            // El goteo va DESPUÉS de restar la vida, como en magia y al revés que en el melee: el original
            // resta en Trabajo.bas:1799 y recién llama a CalcularDarExp en :1803. O sea que la puñalada que
            // MATA tampoco cobra en el goteo — su parte sale del pago del pool al morir.
            CalcularDarExp(session, p, npc, dano);
            session.Send(ServerMessages.ConsoleMsg($"Has apuñalado la criatura por {dano}.", FuenteCombate));
            // Idem: Trabajo.bas:1800 manda solo la linea de texto.
            if (npc.Hp == 0) NpcKilledByPlayer(session, p, npc);
        }
        RaiseSkill(session, p, GameLimits.SkillApunalar);
    }

    /// <summary>
    /// Gotea la parte de experiencia que corresponde por un golpe y la descuenta del pool de la criatura
    /// (VB6 <c>CalcularDarExp</c>, <c>SistemaCombate.bas:1992</c>).
    ///
    /// <para>Se llama <b>ANTES</b> de aplicar el daño, porque el recorte usa el HP que la criatura todavía
    /// tiene (<c>:470</c> contra <c>:472</c>).</para>
    ///
    /// <para>Las invocaciones y mascotas no reparten nada, por el mismo motivo que no dan loot: matar a la
    /// propia invocación sería una canilla infinita.</para>
    /// </summary>
    private void CalcularDarExp(ClientSession session, Player p, Npc npc, int damage)
    {
        if (npc.OwnerCharIndex != 0) return;
        long exp = Combat.ExpPorGolpe(damage, npc.Hp, npc.Template.MaxHp, npc.Template.GiveExp);
        if (exp <= 0) return;
        // El pool manda: nunca se reparte más de lo que le queda a la criatura.
        if (exp > npc.ExpCount) exp = npc.ExpCount;
        if (exp <= 0) return;
        npc.ExpCount -= exp;
        long dar = exp * (_admin?.ExpMult ?? 1);
        if (p.Donador) dar += dar / 2;   // VB6 :2029
        OtorgarExp(session, p, dar);
        RepartirExperienciaAlFamiliar(session, p, dar);
        // CUÁNTA experiencia ganó ESTE golpe (VB6 SistemaCombate.bas:2041, la línea que sigue al UpdateExp).
        // El tester lo pidió (2.19) y es del original: sin esto la barra de experiencia se movía sola y el
        // jugador no tenía forma de saber cuánto rindió cada golpe. El aviso NO está en `OtorgarExp` porque el
        // original lo manda desde los sitios de COMBATE y no desde el otorgamiento en sí.
        session?.Send(ServerMessages.LocaleMsg(140, dar.ToString()));
    }

    private void RaiseSkill(ClientSession? session, Player p, int skill)
    {
        if (!SkillProgression.TryRaise(p, skill, _rng)) return;
        session?.Send(ServerMessages.LocaleMsg(454, $"*{skill}%{p.Skills[skill]}"));
        OtorgarExp(session, p, SkillProgression.ExpPerSkillUp);
        session?.Send(ServerMessages.LocaleMsg(140, SkillProgression.ExpPerSkillUp.ToString()));
    }

    /// <summary>
    /// Otorga experiencia y avisa al cliente como lo hace el original. Devuelve los niveles que subió.
    ///
    /// La secuencia es la de <c>SistemaCombate.bas:2036</c>: <c>WriteUpdateExp</c> SIEMPRE, y después
    /// <c>CheckUserLevel</c>, que por cada nivel manda el <b>sonido</b> del nivel al área y el locale 186, y
    /// al terminar <c>WriteUpdateUserStatsForLevel</c> (Modulo_UsUaRiOs.bas:611) más —si hay puntos que
    /// repartir— <c>WriteLevelUp</c> y el locale 187.
    ///
    /// El port mandaba en su lugar el <c>UpdateUserStats</c> gordo (~26 bytes) desde cada sitio que da
    /// experiencia. Se centraliza acá para que los cuatro no puedan volver a divergir: el bug que esto
    /// previene no es visible —el cliente muestra lo mismo— pero son bytes de más en el camino más
    /// caliente del juego, y no es lo que el cliente VB6 original recibe.
    ///
    /// Y le faltaban TRES cosas del original, todas en el mismo camino: subir de nivel no <b>sonaba</b>
    /// (<c>PlayWave(SND_NIVEL)</c> al área), no <b>avisaba</b> (locales 186 "¡Has subido de nivel!" y 187
    /// "Has ganado N skillpoints") y el <c>LevelUp</c> llevaba el total de puntos donde el VB6 manda los
    /// ganados — el cliente original los <b>acumula</b>, así que con el total contaba de más.
    /// </summary>
    private int OtorgarExp(ClientSession? session, Player p, long exp)
    {
        int puntosAntes = p.FreeSkillPoints;
        bool eraNewbie = p.Level <= GameLimits.NewbieMaxLevel;   // VB6 WasNewbie, tomado ANTES de dar la exp
        var ganancias = new List<Game.Leveling.Ganancia>();
        int levels = _leveling.Grant(p, exp, ganancias);
        session?.Send(ServerMessages.UpdateExp((int)p.Exp));
        if (levels > 0)
        {
            // POR CADA nivel, dentro del `Do While` del original (Modulo_UsUaRiOs.bas:387-389): el SONIDO de
            // subir de nivel al ÁREA (SND_NIVEL = 6, incluido el propio jugador — el VB6 usa ToPCArea) y el
            // "¡Has subido de nivel!". Subir dos niveles de un saque suena y avisa dos veces.
            for (int i = 0; i < levels; i++)
            {
                _world.BroadcastToObservers(p.Pos, ServerMessages.PlayWave(SndNivel, (byte)p.Pos.X, (byte)p.Pos.Y));
                session?.Send(ServerMessages.LocaleMsg(186));
                // Y CUÁNTO ganó, uno por stat y sólo si es > 0 (Modulo_UsUaRiOs.bas:559-573). Subir de nivel
                // avisaba que subiste pero no de cuánto: el jugador veía moverse la barra sin número.
                var g = i < ganancias.Count ? ganancias[i] : default;
                if (g.Hp > 0) session?.Send(ServerMessages.LocaleMsg(197, g.Hp.ToString()));
                if (g.Stamina > 0) session?.Send(ServerMessages.LocaleMsg(198, g.Stamina.ToString()));
                if (g.Mana > 0) session?.Send(ServerMessages.LocaleMsg(199, g.Mana.ToString()));
                if (g.Golpe > 0) session?.Send(ServerMessages.LocaleMsg(200, g.Golpe.ToString()));
            }
            // DEJAR DE SER NEWBIE ESTANDO EN EL DUNGEON NEWBIE (VB6 Modulo_UsUaRiOs.bas:580-595, adentro del
            // mismo `Do While` que los avisos de arriba). El original te SACA del mapa de newbies: te fija la
            // ciudad-hogar según la facción —ciudadano a Nix, republicano a Illiandor, cualquier otro a
            // Rinkel—, te teletransporta ahí y recién entonces avisa con LocaleMsg 304. Sin esto, el que
            // subía a 16 adentro del dungeon se quedaba ahí y con el hogar viejo.
            if (session is not null && eraNewbie && p.Level > GameLimits.NewbieMaxLevel
                && string.Equals(_data.MapZone(p.Pos.Map).Trim(), "NEWBIE", StringComparison.OrdinalIgnoreCase))
            {
                p.HomeCity = p.IsCitizen ? Cities.Nix : p.IsRepublicano ? Cities.Illiandor : Cities.Rinkel;
                TeleportTo(session, p, Cities.SpawnTile(p.HomeCity));
                session.Send(ServerMessages.LocaleMsg(304));
            }
            // Y LA TERCERA REGLA DE LA CASA (2.2b): que el equipo de novato desaparezca al dejar de serlo. El
            // original NO tiene ningún `QuitarNewbieObj` — los 25 objetos `(Newbies)` se quedan para siempre—
            // así que también viene apagada. Va gateada por `eraNewbie`, o sea que corre UNA sola vez: en la
            // subida que te saca del rango de newbie, no en cada nivel de ahí en adelante.
            //
            // OJO que no depende del mapa, a diferencia del bloque de arriba: el que llega a 16 lejos del
            // dungeon también deja de ser newbie, y su equipo tiene que irse igual.
            if (session is not null && _ini.QuitarEquipoNovato && eraNewbie && p.Level > GameLimits.NewbieMaxLevel)
            {
                int sacados = 0;
                for (byte slot = 1; slot <= GameLimits.MaxInventorySlots; slot++)
                {
                    var inv = p.Inventory[slot];
                    if (inv.IsEmpty || _data.Obj(inv.ObjIndex) is not { IsNewbie: true }) continue;
                    // Por el MISMO embudo que tirar o vender: si estaba puesto hay que sacarle el bono y el
                    // gráfico antes de que el slot desaparezca (ver DesequiparSiSeVaEntero).
                    DesequiparSiSeVaEntero(session, p, slot, inv.Amount);
                    p.Inventory[slot] = default;
                    session.Send(ServerMessages.ChangeInventorySlot(slot, 0, 0, false, 0f, 0));
                    sacados++;
                }
                if (sacados > 0)
                    session.Send(ServerMessages.ConsoleMsg(
                        $"Dejaste de ser novato: se fueron {sacados} objeto(s) del equipo de novato.", FuenteInfo));
            }
            // LAS REGLAS DE LA CASA (informe del tester, 2.12 y 2.13). NINGUNA de las dos es del original —
            // `SubirNivel` reparte vida, maná, energía, golpe y skillpoints, y ni regala oro ni cura— así que
            // vienen APAGADAS en el ini: con la config por defecto esto no corre y el server se comporta como
            // el VB6. Están acá para que prenderlas cueste una línea del Server.ini y no escribir código.
            //
            // Van por nivel, dentro del mismo `if (levels > 0)`: subir dos de un saque paga dos veces, que es
            // lo coherente con los avisos de arriba, que también salen dos veces.
            if (_ini.OroAlSubirNivel > 0)
            {
                long premio = (long)_ini.OroAlSubirNivel * levels;
                // TOPADO EN MAXORO, como TODA entrada de oro del original (vender: Comercio.bas:169; robar:
                // Trabajo.bas:1591) — y sobre el TOTAL, no sobre lo que entra. La primera versión de esta línea
                // topaba contra `int.MaxValue` y el guard de `OroDelOriginalTests` la agarró: con un premio
                // grande y muchos niveles, el oro podía pasarse de los noventa millones que el original nunca
                // deja pasar.
                p.Gold = (int)Math.Min((long)p.Gold + premio, MaxOro);
                session?.Send(ServerMessages.UpdateGold(p.Gold));
                session?.Send(ServerMessages.LocaleMsg(29, premio.ToString()));   // "¡Has ganado #1 monedas de oro!"
            }
            if (_ini.CuraAlSubirNivel)
            {
                // Después del `Grant`, así que los máximos ya son los del nivel nuevo: curar antes dejaría el
                // HP en el tope VIEJO y la mitad de la gracia se perdería.
                p.Hp = p.MaxHp; p.Mana = p.MaxMana; p.Stamina = p.MaxStamina;
                session?.Send(ServerMessages.UpdateHP((short)p.Hp));
                session?.Send(ServerMessages.UpdateMana((short)p.Mana));
                session?.Send(ServerMessages.UpdateSta((short)p.Stamina));
            }
            session?.Send(ServerMessages.UpdateUserStatsForLevel(p));
            // El VB6 gatea con `If Pts > 0` y manda los puntos GANADOS en esta subida, no el total: el cliente
            // original acumula (`SkillPoints = SkillPoints + N`). Mandar el total lo hacía contar de más.
            int puntosGanados = p.FreeSkillPoints - puntosAntes;
            if (puntosGanados > 0)
            {
                session?.Send(ServerMessages.LevelUp((short)puntosGanados));
                session?.Send(ServerMessages.LocaleMsg(187, puntosGanados.ToString()));
            }
        }
        return levels;
    }

    /// <summary>Sonido de subir de nivel (VB6 <c>SND_NIVEL = 6</c>, Declares.bas:540).</summary>
    private const short SndNivel = 6;

    /// <summary>Sonido de beber de un pozo (VB6 <c>SND_BEBER = 135</c>, Declares.bas:555).</summary>
    private const short SndBeber = 135;

    /// <summary>"Lanzaste un hechizo y no hay a quién": el original manda <b>223</b> ("Sólo afecta usuarios")
    /// o <b>224</b> ("Sólo afecta NPCs") según a qué apunte el hechizo (modHechizos.bas:904 y :916), no un
    /// texto genérico. Acá salía un <c>ConsoleMsg</c> inventado en los cuatro sitios.
    ///
    /// El mapeo no es literal porque el ruteo tampoco lo es: el original mira <c>flags.TargetUser</c> /
    /// <c>TargetNPC</c> y este port resuelve por el TILE ENCARADO (divergencia ya documentada más abajo). El
    /// mensaje sí se puede elegir igual, con el mismo campo <c>Target</c> del hechizo: 2 = sólo NPCs.</summary>
    private static short SinObjetivo(Data.SpellInfo spell) => spell.Target == 2 ? (short)224 : (short)223;

    /// <summary>Los dos sonidos del casamiento (VB6 <c>snd_casamiento = 161</c> y <c>SND_SANAR = 55</c>,
    /// Declares.bas:557 y :551). El original manda la marcha a TODO el mapa y el sanar sólo al área.</summary>
    private const short SndCasamiento = 161, SndSanar = 55;

    /// <summary>Montar o desmontar (VB6 <c>DoEquita</c>, Trabajo.bas:2340). Está afuera del handler de equipar
    /// porque hay un SEGUNDO camino que desmonta y que no pasa por ahí: caminar a un tile donde no se puede
    /// andar a caballo (ver <see cref="DesmontarSiEntraBajoTecho"/>).
    ///
    /// Al montar, el cuerpo pasa a ser el <c>Ropaje</c> de la montura y el arma desaparece; al desmontar se
    /// restaura la apariencia desde el equipo.</summary>
    private void AlternarMontura(ClientSession session, Player p, byte slot, ObjInfo montura)
    {
        var inv = p.Inventory[slot];
        p.Mounted = !p.Mounted;
        inv.Equipped = p.Mounted;
        p.Inventory[slot] = inv;
        if (p.Mounted)
        {
            if (montura.Ropaje > 0) p.Body = (short)montura.Ropaje;
            p.WeaponAnim = 0;
        }
        else
        {
            RefreshAppearance(p, _data);   // restaura cuerpo vestido + arma/escudo/casco desde el equipo
        }
        session.Send(ServerMessages.ChangeInventorySlot(slot, inv.ObjIndex, inv.Amount, inv.Equipped, 0f, 1));
        session.Send(ServerMessages.MontateToggle()); // client toggles its own mounted state
        _world.BroadcastToObservers(p.Pos, ServerMessages.CharacterChange(p)); // observers see the mount appearance
        session.Send(ServerMessages.ConsoleMsg(p.Mounted ? $"Montas {montura.Name}." : $"Desmontas {montura.Name}.", FuenteInfo));
    }

    /// <summary>Entrar a caballo a un lugar donde no se puede andar a caballo te BAJA del animal (VB6
    /// <c>Modulo_UsUaRiOs.bas:718</c>, dentro del movimiento: <c>If .flags.Montando = 1 Then If Trigger &gt;= 20
    /// Or Zona = "DUNGEON" Then DoEquita</c>). Se llama después de mover, con la posición nueva ya aplicada —
    /// que es lo que hace el original, porque mira <c>.Pos</c> y no el destino.</summary>
    private void DesmontarSiEntraBajoTecho(ClientSession session, Player p)
    {
        if (!p.Mounted || !_data.NoSeMontaAca(p.Pos.Map, p.Pos.X, p.Pos.Y)) return;
        for (byte s = 1; s <= GameLimits.MaxInventorySlots; s++)
        {
            var inv = p.Inventory[s];
            if (inv.IsEmpty || !inv.Equipped) continue;
            var obj = _data.Obj(inv.ObjIndex);
            if (obj is not null && obj.IsMount) { AlternarMontura(session, p, s, obj); return; }
        }
        p.Mounted = false;   // por si el flag quedó sin su montura en el inventario: no dejarlo colgado
    }

    /// <summary>Muerte de un NPC a manos de un jugador (VB6 <c>MuereNpc</c>): lo saca del mundo, libera el
    /// slot de mascota de su dueño y —si no es invocación/mascota— otorga experiencia, nivel y loot.
    /// Compartido por el melee (<see cref="AttackNpc"/>) y por la magia de daño (<see cref="CastDamageOnNpc"/>)
    /// para que matar con espada o con hechizo dé exactamente la misma recompensa.</summary>
    private void NpcKilledByPlayer(ClientSession session, Player p, Npc npc)
    {
        _world.RemoveNpc(npc.CharIndex);
        _world.BroadcastToObservers(npc.Pos, ServerMessages.CharacterRemove(npc.CharIndex));
        // EL GRITO AL MORIR (VB6 MODULO_NPCs.bas:101), y va justo acá: el original lo emite apenas `QuitarNPC`
        // sacó la criatura del mundo —o sea después del CharacterRemove— y antes de repartir experiencia.
        //
        // Dos detalles del original que no se ven leyendo por arriba. Va SÓLO si la mató un jugador (`If
        // UserIndex > 0`): una criatura que mata a otra no suena. Y el área es la DEL JUGADOR (`ToPCArea,
        // UserIndex`) aunque las coordenadas del sonido sean las de la criatura — que muerta ya no está en el
        // mundo, así que su área no existiría.
        if (npc.Template.Snd3 > 0)
            _world.BroadcastToObservers(p.Pos, ServerMessages.PlayWave((short)npc.Template.Snd3, npc.Pos.X, npc.Pos.Y));
        p.NpcsKilled++;
        // El lugar de mascota de su dueño se libera solo: lo hace World.RemoveNpc, por donde pasa esta muerte, y
        // RemoveNpc (arriba) ya sacó a este NPC del mundo. No hay que tocar ningún contador aparte (cupo compartido).
        // Las invocaciones y mascotas (OwnerCharIndex != 0) NO otorgan exp/oro/loot: el VB6 HechizoInvocacion
        // pone GiveGLD=0 en cada invocado (modHechizos.bas:656) y son mascotas sin exp. Sin este gate, matar
        // la propia invocación (blanco indefenso, no contraataca al dueño) sería una canilla infinita de exp/oro/items.
        bool daRecompensa = npc.OwnerCharIndex == 0;
        // EL RESTO DEL POOL, no la experiencia entera (VB6 MODULO_NPCs.bas:129-144). Antes acá se pagaba
        // `GiveExp` completo al que remataba, así que la criatura repartía su experiencia DOS veces si alguien
        // más la había estado golpeando. Ahora el goteo por golpe ya cobró su parte y al matador le queda el
        // saldo — que si peleó solo es todo, igual que antes.
        if (daRecompensa && npc.ExpCount > 0)
        {
            long gainExp = npc.ExpCount * (_admin?.ExpMult ?? 1);   // "Evento Experiencia" del panel
            if (p.Donador) gainExp += gainExp / 2;   // VB6 SistemaCombate.bas:2029 — el donador gana +50% de experiencia
            npc.ExpCount = 0;                        // VB6 :142 — el pool se vacía, no se paga dos veces
            OtorgarExp(session, p, gainExp);
            RepartirExperienciaAlFamiliar(session, p, gainExp);
            session.Send(ServerMessages.LocaleMsg(140, gainExp.ToString()));   // VB6 MODULO_NPCs.bas:136
        }
        // "¡Has matado a #1!" con el NOMBRE de la criatura, y va SIEMPRE — el original lo manda afuera del
        // `If ExpCount > 0` (MODULO_NPCs.bas:141), así que rematar algo que ya no da experiencia igual avisa.
        // Acá salía un ConsoleMsg con texto nuestro que además metía la experiencia y el nivel adentro de la
        // frase; el original los manda por separado (140 la experiencia, 186/197-200 el nivel).
        session.Send(ServerMessages.LocaleMsg(184, npc.Name));
        if (daRecompensa) DropNpcLoot(session, p, npc);
    }

    /// <summary>
    /// Hechizo de daño sobre un NPC (VB6 <c>modHechizos.bas HechizoPropNPC</c>, rama <c>SubeHP = 2</c> "Reduce").
    /// Dos diferencias con el melee, fieles al original: el daño mágico <b>no resta la defensa del NPC</b>
    /// (sólo tiene piso en 0, <c>If daño &lt; 0 Then daño = 0</c>) y suma un bonus por nivel del caster
    /// (<c>daño + Porcentaje(daño, 3 * ELV)</c>). Igual que el melee, provoca al NPC aunque no lo mate
    /// (<c>NPCAtacado</c>) y la muerte pasa por el mismo camino de exp/oro/loot que la espada.
    /// </summary>
    /// <summary>
    /// Hechizo de ESTADO sobre una criatura (VB6 HechizoEstadoNPC, modHechizos.bas:1540-1600). Antes esta
    /// rama sólo miraba jugadores, así que paralizar o envenenar a un bicho devolvía "No hay objetivo".
    ///
    /// Diferencias con el estado sobre jugadores, todas del original:
    ///   - paralizar/inmovilizar PROVOCA a la criatura (PuedeAtacarNPC + NPCAtacado), igual que pegarle;
    ///   - RemoverParalisis sólo funciona sobre TU PROPIA mascota (<c>If .MaestroUser = UserIndex</c>);
    ///   - la inmunidad por NPC (.flags.AfectaParalisis → LocaleMsg 399) no se modela: ningún NPC de esta
    ///     data trae la clave, así que la rama es inalcanzable.
    /// El veneno sobre criaturas no está en HechizoEstadoNPC (el original sólo ramifica paralizar,
    /// inmovilizar y remover parálisis), así que envenenar a un bicho sigue sin objetivo válido.
    /// </summary>
    /// <summary>VB6 <c>HechizoCasteado</c> (modHechizos.bas:734/785/849): el maná, la stamina y
    /// <c>SubirSkill(magia)</c> son ATÓMICOS — se cobran SÓLO cuando el hechizo se lanzó de verdad sobre un
    /// objetivo válido, porque en el original los tres viven dentro del mismo <c>If HechizoCasteado</c>.
    ///
    /// El port los tenía partidos: la stamina y la skill se cobraban al ENTRAR a HandleCastSpell (antes de
    /// resolver el objetivo) y el maná sólo en las ramas de éxito. Como los hechizos de daño/estado retornan
    /// con "No hay objetivo" ANTES de tocar el maná, castear al aire entrenaba Magia gratis: subía la skill y
    /// daba exp pagando sólo stamina, que auto-regenera — una canilla de entrenamiento que el maná debía
    /// gatear. Llamar a esto en cada camino de éxito restaura la atomicidad del original.</summary>
    /// <summary>Avisa al cliente lo que COSTÓ el hechizo: stamina y maná, en ese orden.
    ///
    /// El original manda las DOS cosas y siempre juntas, en los tres puntos donde cobra
    /// (<c>modHechizos.bas:744-745</c>, <c>:795-796</c> en HandleHechizoUsuario y <c>:859-860</c> en
    /// HandleHechizoNPC). Acá se mandaba sólo <c>UpdateMana</c> en 10 de los 11 call sites: la cita apuntaba al
    /// <c>WriteUpdateMana</c> y se pasaba por alto el <c>WriteUpdateSta</c> de la línea inmediatamente anterior.
    /// Como <c>SpellCasted</c> sí descuenta <c>StaRequerido</c> —y hay 22 hechizos que cuestan 100 de energía,
    /// 8 que cuestan 300 y 7 que cuestan 250— la barra de energía del cliente quedaba congelada después de cada
    /// casteo, hasta el siguiente tick de regeneración. Antes no se notaba porque el <c>UpdateUserStats</c>
    /// gordo llevaba <c>MinSta</c> adentro.</summary>
    private static void EmitirCostoDelHechizo(ClientSession session, Player p)
    {
        session.Send(ServerMessages.UpdateSta((short)p.Stamina));
        session.Send(ServerMessages.UpdateMana((short)p.Mana));
    }

    private void SpellCasted(ClientSession session, Player p, Data.SpellInfo spell)
    {
        p.Mana -= spell.ManaCost;
        if (p.Mana < 0) p.Mana = 0;                                                            // VB6: If MinMAN < 0 Then MinMAN = 0
        if (spell.StaRequerido > 0) p.Stamina = Math.Max(0, p.Stamina - spell.StaRequerido);   // VB6: castear cansa
        RaiseSkill(session, p, GameLimits.SkillMagia);                                         // VB6 SubirSkill(magia)
    }

    private void CastStateOnNpc(ClientSession session, Player p, Npc npc, Data.SpellInfo spell)
    {
        // Paralizar una CRIATURA dura mucho menos que paralizar a una persona: el original usa
        // IntervaloParalizadoReducido (Admin.bas:58 = 37 vueltas ≈ 1,5 s) en HechizoEstadoNPC
        // (modHechizos.bas:1565), no IntervaloParalizado. El valor estaba en 250 con este mismo comentario:
        // la cita era correcta y el número no. Encaja con el golpe que paraliza criaturas (30 vueltas).
        const long ParalisisTicks = 37;

        if (spell.IsRemoveParalysis)
        {
            // Sólo la propia mascota: el VB6 gatea con `.MaestroUser = UserIndex` y si no, no hace nada.
            if (npc.OwnerCharIndex != p.CharIndex) { session.Send(ServerMessages.LocaleMsg(298)); return; }
            npc.ParalyzedUntilTick = 0;
            SpellCasted(session, p, spell);
            EmitSpellFx(p, npc.Pos, npc.CharIndex, spell);
            session.Send(ServerMessages.ConsoleMsg($"Lanzaste {spell.Name}: {npc.Name} quedó liberado de la parálisis.", 0));
            EmitirCostoDelHechizo(session, p);
            return;
        }

        if (!spell.IsParalyze) { session.Send(ServerMessages.LocaleMsg(298)); return; }

        // INMUNIDAD (VB6 HechizoEstadoNPC, modHechizos.bas:1548): la criatura con `Inmunidad = 1` no se
        // paraliza ni se inmoviliza — son los 66 bosses del install. El original avisa con LocaleMsg 399
        // ("¡La criatura es inmune a este hechizo!") y deja `HechizoCasteado = False`, así que NO cobra el
        // maná: por eso este gate va ANTES de `SpellCasted`. Sin él, congelar a un boss y matarlo sin que se
        // moviera era gratis.
        if (npc.Template.Inmunidad != 0) { session.Send(ServerMessages.LocaleMsg(399)); return; }

        // VB6 PuedeAtacarNPC: los NPCs de servicio no se tocan — el mismo gate que el daño.
        if (!npc.Attackable || npc.IsMerchant || npc.IsBanker || npc.IsReviver || npc.IsWorkbench || npc.IsTrainer || npc.FactionConvert != 0)
        { session.Send(ServerMessages.LocaleMsg(298)); return; }
        if (!ArmadaPuedeAtacarNpc(session, p, npc)) return;   // VB6 PuedeAtacarNPC:1752 — va después del 298

        npc.AttackedBy = p.Name;   // NPCAtacado: paralizarla la provoca igual que pegarle
        npc.Hostile = true;
        npc.ParalyzedUntilTick = _loop.Tick + ParalisisTicks;
        SpellCasted(session, p, spell);
        EmitSpellFx(p, npc.Pos, npc.CharIndex, spell);
        session.Send(ServerMessages.ConsoleMsg($"Lanzaste {spell.Name}: {npc.Name} quedó paralizado.", 0));
        EmitirCostoDelHechizo(session, p);
    }

    private void CastDamageOnNpc(ClientSession session, Player p, Npc npc, Data.SpellInfo spell, int amount)
    {
        // VB6 PuedeAtacarNPC: los NPCs de servicio no son atacables — mismo gate que AttackNpc.
        if (!npc.Attackable || npc.IsMerchant || npc.IsBanker || npc.IsReviver || npc.IsWorkbench || npc.IsTrainer || npc.FactionConvert != 0)
        { session.Send(ServerMessages.LocaleMsg(298)); return; }
        if (!ArmadaPuedeAtacarNpc(session, p, npc)) return;   // VB6 PuedeAtacarNPC:1752 — va después del 298
        npc.AttackedBy = p.Name;   // VB6 NPCAtacado: lo provoca y lo vuelve hostil para defenderse
        npc.Hostile = true;
        int damage = Combat.EfectoHechizoArea3Pct(amount, p.Level);   // VB6: daño + Porcentaje(daño, 3*ELV), piso 0
        SpellCasted(session, p, spell);
        npc.Hp = Math.Clamp(npc.Hp - damage, 0, npc.Template.MaxHp);
        // OJO: acá el goteo de experiencia va DESPUÉS de aplicar el daño, al revés que en el melee. No es un
        // descuido de este port, es del original: `HechizoPropNPC` resta la vida en modHechizos.bas:1688 y
        // recién llama a `CalcularDarExp` en :1692, mientras que `UserDañoNpc` lo llama ANTES (:470 contra :472).
        //
        // La asimetría tiene una consecuencia observable y por eso se porta igual: como el recorte usa el HP
        // que le QUEDA a la criatura, el hechizo que MATA no gotea nada —el HP ya es 0— y toda su experiencia
        // sale del pago del pool al morir. Un golpe de espada que mata sí cobra su parte en el goteo.
        // El número de daño flotando (VB6 :1690, entre la resta de vida y el goteo de exp): modo 2 y con el
        // charIndex del LANZADOR. Nukear una criatura no mostraba el número; pegarle con la espada sí.
        session.Send(ServerMessages.ChatOverHeadLocale(p.CharIndex, damage, 2));
        CalcularDarExp(session, p, npc, damage);
        EmitSpellFx(p, npc.Pos, npc.CharIndex, spell);
        session.Send(ServerMessages.LocaleMsg(497, $"^{spell.Index}%{npc.Name}%{damage}"));
        EmitirCostoDelHechizo(session, p);
        if (npc.Hp == 0) NpcKilledByPlayer(session, p, npc);
    }

    /// <summary>On NPC death: award gold to the killer and, per the RandomDrop chance, drop an item on
    /// the ground (ObjectCreate broadcast) at the NPC's tile or a free neighbour.</summary>
    private void DropNpcLoot(ClientSession session, Player p, Npc npc)
    {
        var t = npc.Template;
        if (t.GiveGld > 0)
        {
            int gainGld = t.GiveGld * (_admin?.GoldMult ?? 1);              // "Evento Oro" del panel
            // Si con esto te pasarias del tope, el original NO paga y avisa con el 378 (MODULO_NPCs.bas:904,
            // guard `OroLleno`). Aca se sumaba igual y el long se comia el desborde en silencio.
            if ((long)p.Gold + gainGld > int.MaxValue) { session.Send(ServerMessages.LocaleMsg(378)); return; }
            p.Gold += gainGld;
            session.Send(ServerMessages.LocaleMsg(29, gainGld.ToString()));
            session.Send(ServerMessages.UpdateGold(p.Gold));   // VB6 MODULO_NPCs.bas:910, no el UpdateUserStats gordo
        }
        // Botín: el VB6 (NPC_TIRAR_ITEMS, Modulo_InventANDobj.bas:72) recorre TODOS los ítems del inventario
        // del NPC y evalúa cada uno por separado — los NPCs multidrop (Lobo, Dragón Legendario…) sueltan varias
        // cosas. La regla de probabilidad es la del original, y es rara a propósito: sólo ProbTirar == 100 es
        // especial (dropea siempre); con cualquier otro valor tira una moneda al 50% e ignora el número.
        foreach (var d in t.Drops ?? (IReadOnlyList<Data.NpcDrop>)Array.Empty<Data.NpcDrop>())
        {
            if (d.ObjIndex <= 0 || d.Amount <= 0) continue;
            bool cae = d.ProbTirar >= 100 || _rng.NextDouble() < Math.Min(1.0, 0.5 * (_admin?.DropMult ?? 1));  // "Evento Drop" del panel
            if (!cae) continue;
            var pos = FindFreeGround(npc.Pos);
            if (pos is not { } drop) continue;
            _world.SetGround(drop, (short)d.ObjIndex, (short)d.Amount);
            _world.BroadcastToObservers(drop, ServerMessages.ObjectCreate(drop.X, drop.Y, (short)d.ObjIndex, (short)d.Amount));
            var name = _data.Obj(d.ObjIndex)?.Name ?? $"obj {d.ObjIndex}";
            session.Send(ServerMessages.ConsoleMsg($"{npc.Name} dejó caer {d.Amount}x {name}.", 0));
        }
    }

    /// <summary>Up to `count` tiles near `center` free of players and NPCs, scanning expanding rings.</summary>
    private IEnumerable<WorldPos> FreeTilesAround(WorldPos center, int count)
    {
        var result = new List<WorldPos>();
        for (int radius = 1; radius <= 3 && result.Count < count; radius++)
            for (int dx = -radius; dx <= radius && result.Count < count; dx++)
                for (int dy = -radius; dy <= radius && result.Count < count; dy++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != radius) continue; // only the ring's edge
                    int x = center.X + dx, y = center.Y + dy;
                    if (x is < 0 or > 255 || y is < 0 or > 255) continue;
                    var pos = new WorldPos(center.Map, (byte)x, (byte)y);
                    if (_world.PlayerAt(pos) is null && _world.NpcAt(pos) is null) result.Add(pos);
                }
        return result;
    }

    /// <summary>The NPC's tile if free of ground items, else the first free 4-neighbour, else null.</summary>
    private WorldPos? FindFreeGround(WorldPos at)
    {
        if (!_world.TryGetGround(at, out _)) return at;
        foreach (var (dx, dy) in new[] { (0, -1), (1, 0), (0, 1), (-1, 0) })
        {
            var n = new WorldPos(at.Map, (byte)(at.X + dx), (byte)(at.Y + dy));
            if (!_world.TryGetGround(n, out _)) return n;
        }
        return null;
    }

    /// <summary>GM commands (GMCommands packet, id 92): a subcommand byte (eGMCommands) + its args.
    /// Slice: ServerTime (/HORA), Where (/DONDE), WarpChar (/TELEP), Invisible (/INVISIBLE). Args are
    /// read synchronously from the stream (variable length) then the effect runs on the loop thread.</summary>
    private void HandleGMCommands(ClientSession session, ByteQueueReader r)
    {
        byte cmd = r.ReadByte(); // eGMCommands selector (Declares.bas)
        switch (cmd)
        {
            case 5: // ServerTime — /HORA
                _loop.Enqueue(() => GmServerTime(session));
                break;
            case 6: // Where — /DONDE <name>
            {
                string name = r.ReadAsciiString();
                _loop.Enqueue(() => GmWhere(session, name));
                break;
            }
            case 8: // WarpChar — /TELEP <name> <map> <x> <y>
            {
                string name = r.ReadAsciiString();
                short map = r.ReadInteger();
                byte x = r.ReadByte(), y = r.ReadByte();
                _loop.Enqueue(() => GmWarp(session, name, map, x, y));
                break;
            }
            case 11: // GoToChar — /IRA <name>: teleport the GM to that player
            {
                string name = r.ReadAsciiString();
                _loop.Enqueue(() => GmGoToChar(session, name));
                break;
            }
            case 12: // Invisible — /INVISIBLE
                _loop.Enqueue(() => GmInvisible(session));
                break;
            case 18: // KillNPC — /RMATA: kill the NPC the GM faces
                _loop.Enqueue(() => GmKillNpc(session));
                break;
            case 21: // RequestCharInfo — /INFO <name>: send the target's CharacterInfo sheet
            {
                string name = r.ReadAsciiString();
                _loop.Enqueue(() => GmCharInfo(session, name));
                break;
            }
            case 14: // RequestUserList — /LISTUSU: the list of online player names
                _loop.Enqueue(() =>
                {
                    if (!RequireGm(session, session.Player)) return;
                    session.Send(ServerMessages.UserNameList(_world.Players.Select(pl => pl.Name)));
                    session.Send(ServerMessages.ConsoleMsg($"Usuarios online: {_world.OnlineCount}.", FuenteInfo));
                });
                break;
            case 25: // ReviveChar — /REVIVIR <name>
            {
                string name = r.ReadAsciiString();
                _loop.Enqueue(() => GmRevive(session, name));
                break;
            }
            case 32: // SummonChar — /SUM <name>: bring that player to the GM
            {
                string name = r.ReadAsciiString();
                _loop.Enqueue(() => GmSummon(session, name));
                break;
            }
            case 20: // EditChar — /MOD <nick|YO> <opcion> <arg1> <arg2>
            {
                string nick = r.ReadAsciiString();
                byte opcion = r.ReadByte();
                string arg1 = r.ReadAsciiString();
                string arg2 = r.ReadAsciiString();
                _loop.Enqueue(() => GmEditChar(session, nick, opcion, arg1, arg2));
                break;
            }
            case 17: // Jail — /CARCEL <nick> <motivo> <minutos>
            {
                string nick = r.ReadAsciiString();
                string motivo = r.ReadAsciiString();
                byte minutos = r.ReadByte();
                _loop.Enqueue(() => GmJail(session, nick, motivo, minutos));
                break;
            }
            // --- Inspección de personajes: /INV (22), /BOV (23), /SKILLS (24) ---
            // Los tres tienen el MISMO wire (un solo ASCIIString con el nick) y el mismo gate, y responden
            // sólo con ConsoleMsg de FontIndex 3. Sin ellos no se puede auditar un dupe ni resolver una
            // disputa: es lo que un GM mira antes de sancionar.
            case 22: { string nick = r.ReadAsciiString(); _loop.Enqueue(() => GmInspect(session, nick, GmInspectKind.Inventory)); break; }
            case 23: { string nick = r.ReadAsciiString(); _loop.Enqueue(() => GmInspect(session, nick, GmInspectKind.Bank)); break; }
            case 24: { string nick = r.ReadAsciiString(); _loop.Enqueue(() => GmInspect(session, nick, GmInspectKind.Skills)); break; }
            // /ACC (60) y /RACC (61): spawnear un NPC del NPCs.dat al frente. En el VB6 son EQUIVALENTES —el
            // parámetro `respawn` que los distingue es código muerto (lo pisa `.Respawn=0`)— así que los dos
            // rutean al mismo handler. El arg es un Integer con el número de NPC del NPCs.dat.
            case 60: { short npcIndex = r.ReadInteger(); _loop.Enqueue(() => GmCreateNpc(session, npcIndex)); break; }
            case 61: { short npcIndex = r.ReadInteger(); _loop.Enqueue(() => GmCreateNpc(session, npcIndex)); break; }
            case 19: { string nick = r.ReadAsciiString(); string reason = r.ReadAsciiString(); _loop.Enqueue(() => GmWarn(session, nick, reason)); break; }  // /ADVERTENCIA
            // /BAN por PERSONAJE: string(nick) + byte(accion). accion 1 = banear, 0 = desbanear (los comentarios
            // del VB6 están invertidos respecto a las llamadas: Case 1 → BanCharacter, Case 0 → UnBanCharacter).
            case 30: { string nick = r.ReadAsciiString(); byte accion = r.ReadByte(); _loop.Enqueue(() => GmBan(session, nick, accion == 1)); break; }
            // --- Forense de IP y ejecución ---
            case 29: { string nick = r.ReadAsciiString(); _loop.Enqueue(() => GmExecute(session, nick)); break; }        // /EJECUTAR
            case 36: { string nick = r.ReadAsciiString(); _loop.Enqueue(() => GmNickToIp(session, nick)); break; }        // /NICK2IP
            case 37: { byte a = r.ReadByte(), b = r.ReadByte(), c = r.ReadByte(), d = r.ReadByte(); _loop.Enqueue(() => GmIpToNick(session, $"{a}.{b}.{c}.{d}")); break; }  // /IP2NICK
            case 53: // CreateItem — /CI <objIndex> <cantidad>
            {
                short objIndex = r.ReadInteger();
                short amount = r.ReadInteger();
                _loop.Enqueue(() => GmCreateItem(session, objIndex, amount));
                break;
            }
            case 86: // DARPUN — /DARPUN <nick>@<puntos>: acreditar créditos de donador
            {
                string nick = r.ReadAsciiString();
                long puntos = r.ReadLong();
                _loop.Enqueue(() => GmDarpun(session, nick, puntos));
                break;
            }
            case 88: // Donador — /DONADOR <nick>: togglear el flag donador de la cuenta
            {
                string nick = r.ReadAsciiString();
                _loop.Enqueue(() => GmDonador(session, nick));
                break;
            }

            // --- Tanda 1 de comandos GM (VB6 HandleGMCommands): los que no piden subsistemas nuevos ---
            case 1: // GMMessage — /GMSG <mensaje>: chat privado entre GMs
            {
                string msg = r.ReadAsciiString();
                _loop.Enqueue(() => GmChat(session, msg));
                break;
            }
            case 26: // OnlineGM — lista de GMs conectados
                _loop.Enqueue(() => GmOnlineGm(session));
                break;
            case 27: // OnlineMap — /ONLINEMAP <mapa>: usuarios en ese mapa
            {
                short map = r.ReadInteger();   // OJO: el VB6 lee el arg ANTES del gate — si no, se desincroniza el stream
                _loop.Enqueue(() => GmOnlineMap(session, map));
                break;
            }
            case 28: // Kick — /ECHAR <nick>: expulsa a un jugador del mundo
            {
                string nick = r.ReadAsciiString();
                _loop.Enqueue(() => GmKick(session, nick));
                break;
            }
            case 35: // ServerMessage — /RMSG <mensaje>: broadcast a todo el servidor
            {
                string msg = r.ReadAsciiString();
                _loop.Enqueue(() => GmServerMessage(session, msg));
                break;
            }
            case 54: // DestroyItems — /DEST: borra el ítem del suelo donde está parado el GM
                _loop.Enqueue(() => GmDestroyItem(session));
                break;
            case 55: // TileBlockedToggle — /BLOQ: togglea el bloqueo del tile del GM
                _loop.Enqueue(() => GmToggleBlocked(session));
                break;
            case 57: // KillAllNearbyNPCs — /MASSKILL: mata los NPCs de la ventana de pantalla
                _loop.Enqueue(() => GmMassKill(session));
                break;
            // --- Tanda 2 de comandos GM ---
            case 3: // GoNearby — /IRCERCA <nick>: teleporta al GM junto a un jugador
            {
                string nick = r.ReadAsciiString();
                _loop.Enqueue(() => GmGoNearby(session, nick));
                break;
            }
            case 7: // CreaturesInMap — /NENE <mapa>: inventario de criaturas de un mapa
            {
                short map = r.ReadInteger();   // el VB6 lee el arg antes del gate
                _loop.Enqueue(() => GmCreaturesInMap(session, map));
                break;
            }
            case 38: // GuildOnlineMembers — /ONCLAN <clan>: miembros de un clan conectados
            {
                string guild = r.ReadAsciiString();
                _loop.Enqueue(() => GmGuildOnline(session, guild));
                break;
            }
            case 43: // DestroyAllItemsInArea — /MASSDEST: borra los ítems del suelo de la pantalla
                _loop.Enqueue(() => GmMassDestroy(session));
                break;
            case 56: // KillNPCNoRespawn — /MATA: mata la criatura encarada, sin respawn
                _loop.Enqueue(() => GmKillNpcNoRespawn(session));
                break;
            case 59: // SystemMessage — /SMSG <mensaje>: cartel modal a todo el servidor
            {
                string msg = r.ReadAsciiString();
                _loop.Enqueue(() => GmSystemMessage(session, msg));
                break;
            }
            // --- Tanda 3: los que operan sobre el TARGET del click izquierdo (ver HandleLeftClick) ---
            case 31: // NPCFollow — /SEGUIR: la criatura clickeada persigue al GM (toggle)
                _loop.Enqueue(() => GmNpcFollow(session));
                break;
            // --- Tanda 7: eventos con multiplicador, apagado y clan por nombre ---
            case 89: // EventoOro — /ORO <multi> <minutos>
            {
                byte multi = r.ReadByte(), mins = r.ReadByte();
                _loop.Enqueue(() => GmEvento(session, oro: true, multi, mins));
                break;
            }
            case 90: // EventoExperiencia — /EXP <multi> <minutos>
            {
                byte multi = r.ReadByte(), mins = r.ReadByte();
                _loop.Enqueue(() => GmEvento(session, oro: false, multi, mins));
                break;
            }
            case 64: // TurnOffServer — /APAGAR
                _loop.Enqueue(() => GmTurnOffServer(session));
                break;
            case 50: // GuildBan — /BANCLAN <clan>: banea a todos los miembros (el clan NO se disuelve)
            {
                string clan = r.ReadAsciiString();
                _loop.Enqueue(() => GmGuildBan(session, clan));
                break;
            }
            // --- Tanda 6: apariencia del GM, clan por nombre y des-aturdir ---
            case 2:  // showName — /SHOWNAME: el GM oculta/muestra su propio nick
                _loop.Enqueue(() => GmShowName(session));
                break;
            case 62: // NavigateToggle — /NAVE: el GM camina sobre agua sin barco
                _loop.Enqueue(() => GmNavigateToggle(session));
                break;
            case 44: // MakeDumbNoMore — /NOESTUPIDO <nick>: le limpia el aturdimiento a otro
            {
                string nick = r.ReadAsciiString();
                _loop.Enqueue(() => GmMakeDumbNoMore(session, nick));
                break;
            }
            case 49: // GuildMemberList — /MIEMBROSCLAN <clan>: lista los miembros de CUALQUIER clan
            {
                string clan = r.ReadAsciiString();
                _loop.Enqueue(() => GmGuildMemberList(session, clan));
                break;
            }
            case 65: // RemoveCharFromGuild — /RAJARCLAN <nick>: expulsa a alguien del clan en que esté
            {
                string nick = r.ReadAsciiString();
                _loop.Enqueue(() => GmRemoveCharFromGuild(session, nick));
                break;
            }
            // --- Tanda 5: los que reusan rutinas que el port ya tiene (panel de admin / persistencia) ---
            case 4: // comment — /REM <texto>: deja un comentario en el log de GMs
            {
                string comentario = r.ReadAsciiString();   // se consume SIEMPRE, aunque el gate rebote
                _loop.Enqueue(() => GmComment(session, comentario));
                break;
            }
            case 13: // GMPanel — /PANELGM: le pide al cliente que abra el panel de GM
                _loop.Enqueue(() => GmPanel(session));
                break;
            case 63: // ServerOpenToUsersToggle — /HABILITAR: modo mantenimiento (sólo GMs) on/off
                _loop.Enqueue(() => GmServerOpenToggle(session));
                break;
            case 78: // SaveChars — /GRABAR: persiste todos los personajes online
                _loop.Enqueue(() => GmSaveChars(session));
                break;
            case 80: // KickAllChars — /ECHARTODOSPJS: expulsa a todos los no-GM
                _loop.Enqueue(() => GmKickAllChars(session));
                break;
            // --- Tanda 4: el centinela y el clima ---
            case 15: // Working — /TRABAJANDO: lista a quien está trabajando ahora
                _loop.Enqueue(() => GmWorking(session));
                break;
            case 16: // Hiding — /OCULTANDO: lista a quien se está ocultando ahora
                _loop.Enqueue(() => GmHiding(session));
                break;
            case 41: // RainToggle — /LLUVIA <clima>: fuerza el clima del mundo
            {
                byte queclima = r.ReadByte();   // el VB6 lo lee ANTES del gate de privilegios
                _loop.Enqueue(() => GmRainToggle(session, queclima));
                break;
            }
            case 33: // ResetNPCInventory — /RESETINV: repone en cero el stock del mercader clickeado
                _loop.Enqueue(() => GmResetNpcInventory(session));
                break;
            case 40: // TeleportDestroy — /DT: borra el teleport del tile clickeado
                _loop.Enqueue(() => GmTeleportDestroy(session));
                break;
            case 42: // TalkAsNPC — /TALKAS <mensaje>: habla por la boca de la criatura clickeada
            {
                string msg = r.ReadAsciiString();
                _loop.Enqueue(() => GmTalkAsNpc(session, msg));
                break;
            }
            case 39: // TeleportCreate — /CT <mapa> <x> <y>: crea un teleport al destino, arriba del GM
            {
                short mapa = r.ReadInteger(); byte tx = r.ReadByte(), ty = r.ReadByte();
                _loop.Enqueue(() => GmTeleportCreate(session, mapa, tx, ty));
                break;
            }
            case 51: // BanIP — /BANIP: su wire es CONDICIONAL, así que se lee acá y no por la tabla de layouts.
            {
                // VB6 HandleBanIP (Protocol.bas:9755): Boolean(porIP) → si true 4 bytes de IP, si no un ASCIIString
                // con el nick; y después SIEMPRE el motivo. La tabla de layouts fijos no puede expresar esto y
                // dejaba la sesión trabada (el parser reventaba sin drenar el buffer).
                bool porIp = r.ReadBoolean();
                string ipONick = porIp ? string.Join(".", r.ReadBytes(4).Select(b => b.ToString())) : r.ReadAsciiString();
                string motivo = r.ReadAsciiString();
                _loop.Enqueue(() => GmBanIp(session, porIp, ipONick, motivo));
                break;
            }
            case 48: // BannedIPReload — /BANIPRELOAD: graba la lista y la vuelve a leer
                _loop.Enqueue(() => GmBannedIpReload(session));
                break;
            case 81: // ReloadNPCs — /RELOADNPCS
                _loop.Enqueue(() => GmReload(session, GmReloadKind.Npcs));
                break;
            case 83: // ReloadSpells — /RELOADHECHIZOS
                _loop.Enqueue(() => GmReload(session, GmReloadKind.Spells));
                break;
            case 84: // ReloadObjects — /RELOADOBJ
                _loop.Enqueue(() => GmReload(session, GmReloadKind.Objects));
                break;
            case 87: // ResponderGM — la respuesta del GM a una consulta de /GM
            {
                string quien = r.ReadAsciiString(), mensaje = r.ReadAsciiString(), modo = r.ReadAsciiString();
                _loop.Enqueue(() => GmResponderGM(session, quien, mensaje, modo));
                break;
            }
            case 67: // ToggleCentinelActivated — /CENTINELAACTIVADO
                _loop.Enqueue(() => GmCentinelToggle(session));
                break;
            case 91: // CuentaRegresiva — Byte(segundos) + Byte(mapa; 0 = todo el server)
            {
                byte segs = r.ReadByte(), mapa = r.ReadByte();
                _loop.Enqueue(() => GmCuentaRegresiva(session, segs, mapa));
                break;
            }
            // MUERTOS EN EL ORIGINAL, con su case para que no queden en el cajón de "no implementado" — que
            // es una etiqueta distinta y manda a investigar de nuevo lo que ya tiene veredicto:
            //   /LIMPIAR (34): HandleCleanWorld (Protocol.bas:908) lee el id, chequea privilegios y NO HACE
            //                  NADA MÁS. El cuerpo termina ahí.
            //   /LASTIP  (58): HandleLastIP arma la lista de IPs pero TODAS sus líneas —el armado y el
            //                  WriteConsoleMsg— están COMENTADAS: el que lo tipea no recibe una sola letra.
            // Callar es lo fiel en los dos. Los argumentos igual se consumen (el `case` los lee) o el stream
            // se desincroniza, que es el defecto que este switch existe para no repetir.
            case 34:
                break;
            case 58:
                r.ReadAsciiString();
                break;

            // /APASS <personaje> <cuenta> — NO SE PORTA, y se le contesta el motivo en vez de un "todavía no".
            // El original (HandleAlterPassword, Protocol.bas:1004) COPIA la contraseña de otra cuenta con
            // `WriteVar(... "Password", Password)`: sólo funciona porque el .cnt guarda la contraseña en
            // claro. Acá se guarda un PBKDF2, que es irreversible por diseño, así que no hay nada que copiar.
            // Y el destino que escribe el VB6 es la cuenta del PROPIO GM (`.Account`), no la del personaje
            // que nombra el comando — o sea que además hace algo distinto de lo que su mensaje dice.
            case 66:
            {
                r.ReadAsciiString(); r.ReadAsciiString();
                _loop.Enqueue(() =>
                {
                    if (!RequireGm(session, session.Player)) return;
                    session.Send(ServerMessages.ConsoleMsg(
                        "/APASS no existe acá: las contraseñas se guardan cifradas y no se pueden copiar. " +
                        "Para dar acceso, que el jugador cree una cuenta nueva.", FuenteInfo));
                });
                break;
            }
            case 82: // ReloadServerIni — /RELOADSINI (VB6 HandleReloadServerIni: LogGM + LoadSini)
                _loop.Enqueue(() => GmRecargarIni(session));
                break;
            case 85: // SetIniVar — /SETINIVAR <seccion> <clave> <valor> (VB6 HandleSetIniVar: WriteVar al ini)
            {
                string sec = r.ReadAsciiString(), clave = r.ReadAsciiString(), valor = r.ReadAsciiString();
                _loop.Enqueue(() => GmEscribirIni(session, sec, clave, valor));
                break;
            }
            case 45: // SetTrigger — /TRIGGER <n>: cambia el trigger del tile donde está parado el GM
            {
                byte trig = r.ReadByte();
                _loop.Enqueue(() => GmSetTrigger(session, trig));
                break;
            }
            case 46: // AskTrigger — /TRIGGER (sin argumento): informa el trigger del tile
                _loop.Enqueue(() => GmAskTrigger(session));
                break;
            case 9:  // SOSShowList — /SHOW SOS
                _loop.Enqueue(() => GmShowSosList(session));
                break;
            case 10: // SOSRemove — SOSDONE <nick>: atender una consulta y sacarla de la cola
            {
                string quien = r.ReadAsciiString();
                _loop.Enqueue(() => GmSosRemove(session, quien));
                break;
            }
            case 79: // CleanSOS — /BORRAR SOS
                _loop.Enqueue(() => GmCleanSos(session));
                break;
            case 69: // SaveMap — /GUARDAMAPA
                _loop.Enqueue(() => GmSaveMap(session));
                break;
            case 70: // ChangeMapInfoPK — /MODMAPINFO PK <0|1>
            {
                bool pk = r.ReadBoolean();
                _loop.Enqueue(() => GmChangeMapInfo(session, MapInfoField.Pk, pk, ""));
                break;
            }
            case 71: // ChangeMapInfoBackup — /MODMAPINFO BACKUP <0|1>
            {
                bool bk = r.ReadBoolean();
                _loop.Enqueue(() => GmChangeMapInfo(session, MapInfoField.Backup, bk, ""));
                break;
            }
            case 72: // ChangeMapInfoRestricted — /MODMAPINFO RESTRINGIR <NEWBIE|NO|ARMADA|CAOS|FACCION>
            {
                string modo = r.ReadAsciiString();
                _loop.Enqueue(() => GmChangeMapInfo(session, MapInfoField.Restrict, false, modo));
                break;
            }
            case 73: // ChangeMapInfoNoMagic — /MODMAPINFO MAGIASINEFECTO <0|1>
            {
                bool f = r.ReadBoolean();
                _loop.Enqueue(() => GmChangeMapInfo(session, MapInfoField.NoMagic, f, ""));
                break;
            }
            case 74: // ChangeMapInfoNoInvi — /MODMAPINFO INVISINEFECTO <0|1>
            {
                bool f = r.ReadBoolean();
                _loop.Enqueue(() => GmChangeMapInfo(session, MapInfoField.NoInvi, f, ""));
                break;
            }
            case 75: // ChangeMapInfoNoResu — /MODMAPINFO RESUSINEFECTO <0|1>
            {
                bool f = r.ReadBoolean();
                _loop.Enqueue(() => GmChangeMapInfo(session, MapInfoField.NoResu, f, ""));
                break;
            }
            case 76: // ChangeMapInfoLand — /MODMAPINFO TERRENO <BOSQUE|NIEVE|DESIERTO|CIUDAD|DUNGEON|NADA>
            {
                string terreno = r.ReadAsciiString();
                _loop.Enqueue(() => GmChangeMapInfo(session, MapInfoField.Terrain, false, terreno));
                break;
            }
            case 77: // ChangeMapInfoZone — /MODMAPINFO ZONA <CAMPO|CIUDAD|DUNGEON|NEWBIE|...>
            {
                string zona = r.ReadAsciiString();
                _loop.Enqueue(() => GmChangeMapInfo(session, MapInfoField.Zone, false, zona));
                break;
            }
            case 47: // BannedIPList — /BANIPLIST
                _loop.Enqueue(() => GmBannedIpList(session));
                break;
            case 52: // UnbanIP — /UNBANIP <a.b.c.d> (cuatro Bytes, no un string)
            {
                string ip = string.Join(".", r.ReadBytes(4).Select(b => b.ToString()));
                _loop.Enqueue(() => GmUnbanIp(session, ip));
                break;
            }
            case 68: // ShowGuildMessages — /SHOWCMSG <clan>: el GM se engancha al chat de un clan ajeno
            {
                string clan = r.ReadAsciiString();
                _loop.Enqueue(() => GmShowGuildMessages(session, clan));
                break;
            }

            default:
                // Los subcomandos GM no llevan length-prefix: si no consumimos los argumentos de uno no
                // implementado, quedan bytes sueltos y se desincroniza TODO el stream siguiente. Antes se
                // cerraba la conexión por eso — pero era una REGRESIÓN: el VB6 no tiene Case Else y
                // simplemente ignora el comando, mientras que acá un GM que tipeaba /MOD, /CI o /CARCEL
                // quedaba EXPULSADO. Con la tabla de layouts consumimos los args y respondemos "no
                // implementado" sin cortar. Sólo cerramos si el selector es realmente desconocido.
                if (GmArgLayouts.TryGetValue(cmd, out var layout))
                {
                    foreach (char t in layout)
                    {
                        switch (t)
                        {
                            case 's': r.ReadAsciiString(); break;
                            case 'i': r.ReadInteger(); break;
                            case 'l': r.ReadLong(); break;
                            default: r.ReadByte(); break;   // 'b' = Byte/Boolean (1 byte en el wire)
                        }
                    }
                    Console.WriteLine($"[{session.RemoteEndpoint}] comando GM no implementado: {cmd}");
                    _loop.Enqueue(() =>
                    {
                        if (!RequireGm(session, session.Player)) return;
                        session.Send(ServerMessages.ConsoleMsg($"Ese comando de GM todavía no está implementado en este servidor (#{cmd}).", FuenteInfo));
                    });
                }
                else
                {
                    Console.WriteLine($"[{session.RemoteEndpoint}] selector de comando GM desconocido: {cmd}");
                    session.Close($"unknown GM command {cmd}");
                }
                break;
        }
    }

    /// <summary>
    /// Layout de argumentos de cada subcomando GM (<c>eGMCommands</c>), extraído de los <c>Handle*</c> del
    /// VB6 (Protocol.bas). Se usa para CONSUMIR los args de un comando que todavía no implementamos sin
    /// desincronizar el stream (los subcomandos no llevan length-prefix). Los implementados leen sus args en su
    /// propio <c>case</c>; esta tabla coincide con ellos, así que sirve de validación cruzada.
    ///
    /// Hoy quedan CINCO sin case y son los únicos que dependen de verdad de esta tabla: 34 (`/LIMPIAR`),
    /// 58 (`/LASTIP`) y 66 (`/APASS`), que son código muerto en el original, y 82 (`/RELOADSINI`) y
    /// 85 (`/SETINIVAR`), bloqueados por el `Server.ini` que este port no tiene (fase5 §4k).
    /// Claves: 's' = ASCIIString, 'i' = Integer (int16), 'l' = Long (int32), 'b' = Byte/Boolean (1 byte).
    /// </summary>
    private static readonly IReadOnlyDictionary<byte, string> GmArgLayouts = new Dictionary<byte, string>
    {
        [1] = "s",        [2] = "",         [3] = "s",        [4] = "s",        [5] = "",
        [6] = "s",        [7] = "i",        [8] = "sibb",     [9] = "",         [10] = "s",
        [11] = "s",       [12] = "",        [13] = "",        [14] = "",        [15] = "",
        [16] = "",        [17] = "ssb",     [18] = "",        [19] = "ss",      [20] = "sbss",
        [21] = "s",       [22] = "s",       [23] = "s",       [24] = "s",       [25] = "s",
        [26] = "",        [27] = "i",       [28] = "s",       [29] = "s",       [30] = "sb",
        [31] = "",        [32] = "s",       [33] = "",        [34] = "",        [35] = "s",
        [36] = "s",       [37] = "bbbb",    [38] = "s",       [39] = "ibb",     [40] = "",
        [41] = "b",       [42] = "s",       [43] = "",        [44] = "s",       [45] = "b",
        [46] = "",        [47] = "",        [48] = "",        [49] = "s",       [50] = "s",
        // 51 (BanIP) NO va en esta tabla: su wire es CONDICIONAL (Boolean; si es true 4 bytes de IP, si no un
        // ASCIIString con el nick; y después SIEMPRE el motivo). Un layout fijo no puede expresar eso y dejaba el
        // stream trabado —el parser tiraba NotEnoughData sin drenar el buffer y la sesión quedaba muerta—, así que
        // tiene su propio `case 51` que consume las dos ramas.
                          [52] = "bbbb",    [53] = "ii",      [54] = "",        [55] = "",
        [56] = "",        [57] = "",        [58] = "s",       [59] = "s",       [60] = "i",
        [61] = "i",       [62] = "",        [63] = "",        [64] = "",        [65] = "s",
        [66] = "ss",      [67] = "",        [68] = "s",       [69] = "",        [70] = "b",
        [71] = "b",       [72] = "s",       [73] = "b",       [74] = "b",       [75] = "b",
        [76] = "s",       [77] = "s",       [78] = "",        [79] = "",        [80] = "",
        [81] = "",        [82] = "",        [83] = "",        [84] = "",        [85] = "sss",
        [87] = "sss",     [89] = "bb",      [90] = "bb",      [91] = "bb",
        // 86 (DARPUN) y 88 (Donador) tienen case propio implementado. 89/90 (EventoOro/EventoExperiencia) faltaban
        // en la tabla: sin su layout el `default` no sabía cuántos bytes consumir y terminaba EXPULSANDO al GM que
        // los tipeara — la misma regresión que ya se había arreglado para /MOD, /CI y /CARCEL.
    };

    // Tiers de privilegio (VB6 PlayerType, Declares.bas:103): bitmask 8=Dios, 16=Admin, 32=RoleMaster. Los comandos
    // de economía del donador NO se gatean con el RequireGm genérico (cualquier GM > User): el VB6 los reserva a los
    // tiers altos — /DONADOR exige Dios (Protocol.bas:16116), /DARPUN exige Dios/Admin/RoleMaster (:16564, bloquea
    // User/Consejero/SemiDios). Sin esto, un SemiDios podría acuñar créditos y regalar donador permanente.
    // Máscaras de PlayerType (Declares.bas:103): User=1, Consejero=2, SemiDios=4, Dios=8, Admin=16, RoleMaster=32.
    private const int PrivUser = 1, PrivConsejero = 2, PrivSemiDios = 4;
    private const int PrivDios = 8, PrivAdmin = 16, PrivRoleMaster = 32;
    private static bool IsDios(Player? p) => p is not null && (p.Privileges & PrivDios) != 0;
    private static bool IsAltoGm(Player? p) => p is not null && (p.Privileges & (PrivDios | PrivAdmin | PrivRoleMaster)) != 0;
    /// <summary>SemiDios o superior (Admin|Dios|SemiDios) — el gate de /ECHAR, /RMSG, /ONLINEMAP y /ONLINEGM.
    /// Como en el VB6, quien no pasa el gate rebota EN SILENCIO (Exit Sub, sin ConsoleMsg).</summary>
    private static bool IsSemiDiosOrHigher(Player? p) => p is not null && (p.Privileges & (PrivSemiDios | PrivDios | PrivAdmin)) != 0;

    private static bool RequireGm(ClientSession session, Player? p)
    {
        // VB6: cada sub-handler GM arranca con "If .flags.Privilegios And PlayerType.User Then Exit Sub" — un
        // jugador común NO recibe ningún aviso, la petición se descarta en silencio. Replicamos ese silencio
        // (antes el slice mandaba un ConsoleMsg "No tienes privilegios de GM." que el VB6 nunca emite).
        return p is not null && p.IsGm;
    }

    private void GmServerTime(ClientSession session)
    {
        var p = session.Player;
        if (!RequireGm(session, p)) return;
        session.Send(ServerMessages.ConsoleMsg($"Hora del servidor: {DateTime.Now:HH:mm:ss dd/MM/yyyy}", FuenteInfo));
    }

    /// <summary>/DONDE (VB6 HandleWhere, Protocol.bas:6642): dónde está un jugador.
    ///
    /// <para>Faltaba la REGLA DE VISIBILIDAD, que no es un detalle de texto: el original sólo revela la
    /// posición si el objetivo es User, Consejero o SemiDiós — o si es Dios/Admin <b>y el que pregunta también
    /// lo es</b>. O sea que un SemiDiós no puede ubicar a un Dios. Acá se la mostrábamos a cualquier GM.</para>
    ///
    /// <para>Cuando la regla no deja, el original NO contesta nada: no hay rama negativa. Y el offline va por
    /// <c>LocaleMsg 75</c>, no por un texto nuestro.</para></summary>
    private void GmWhere(ClientSession session, string name)
    {
        var p = session.Player;
        if (!RequireGm(session, p)) return;
        var t = _world.PlayerByName(name);
        if (t is null) { session.Send(ServerMessages.LocaleMsg(75)); return; }
        bool objetivoVisible = (t.Privileges & (PrivUser | PrivConsejero | PrivSemiDios)) != 0
                               || ((t.Privileges & (PrivDios | PrivAdmin)) != 0
                                   && (p!.Privileges & (PrivDios | PrivAdmin)) != 0);
        if (!objetivoVisible) return;   // sin rama negativa: el original calla
        // El doble espacio tras "Ubicación" es del original (Protocol.bas:6673).
        session.Send(ServerMessages.ConsoleMsg(
            $"Ubicación  {name}: {t.Pos.Map}, {t.Pos.X}, {t.Pos.Y}.", FuenteInfo));
    }

    private void GmWarp(ClientSession session, string name, short map, byte x, byte y)
    {
        var p = session.Player;
        if (!RequireGm(session, p)) return;
        var t = string.IsNullOrWhiteSpace(name) ? p : _world.PlayerByName(name);
        if (t is null) { session.Send(ServerMessages.ConsoleMsg($"{name} no está online.", FuenteInfo)); return; }
        short destMap = map <= 0 ? t.Pos.Map : map;
        // Por `WarpOtroJugador` y no a mano: mover `Pos` de a pedacitos se olvidaba del `ChangeMap`, así que
        // teletransportar a alguien a OTRO mapa le dejaba el cliente dibujando el mapa viejo — con la gente
        // del nuevo apareciendo encima del escenario que ya no estaba. El helper ya existía (lo escribió la
        // cárcel, que tropezó con esto mismo) y hace la secuencia entera: limpia los diálogos de las dos
        // puntas, baja del barco al que quedó varado, manda el ChangeMap si cambió el mapa y refresca el área.
        WarpOtroJugador(t, new WorldPos(destMap, x, y));
        session.Send(ServerMessages.LocaleMsg(487, t.Name));
        if (t.CharIndex != p!.CharIndex)
            t.Connection?.Send(ServerMessages.ConsoleMsg($"Un GM te teletransportó a ({x},{y}).", FuenteInfo));
    }

    private void GmCharInfo(ClientSession session, string name)
    {
        var p = session.Player;
        if (!IsSemiDiosOrHigher(p)) return;   // VB6 Protocol.bas:8794: Admin/Dios/SemiDios, NO Consejero ni RoleMaster
        // /INFO también sobre un personaje OFFLINE (VB6 HandleRequestCharInfo lee su .chr): sólo lectura.
        var t = _world.PlayerByName(name) ?? (_db.CharacterExists(name) ? _db.LoadCharacter(name, 0, "") : null);
        if (t is null) { session.Send(ServerMessages.ConsoleMsg($"Usuario inexistente: {name}", FuenteInfo)); return; }
        session.Send(ServerMessages.CharacterInfo(t)); // structured sheet for the client's /INFO window
        session.Send(ServerMessages.ConsoleMsg($"Info de {t.Name}: clase {t.Class}, nivel {t.Level}, oro {t.Gold}, clan '{t.GuildName}'.", FuenteInfo));
    }

    private void GmGoToChar(ClientSession session, string name)
    {
        var p = session.Player;
        if (!RequireGm(session, p)) return;
        var t = _world.PlayerByName(name);
        if (t is null) { session.Send(ServerMessages.LocaleMsg(75)); return; }
        // Mismo helper que /TELEP: sin el ChangeMap, un GM que iba hacia alguien de otro mapa se quedaba
        // mirando el suyo. Y es el comando que más se usa, así que era el más visible de los tres.
        WarpOtroJugador(p!, new WorldPos(t.Pos.Map, t.Pos.X, t.Pos.Y));
        session.Send(ServerMessages.ConsoleMsg($"Te teletransportaste junto a {t.Name}.", FuenteInfo));
    }

    /// <summary>
    /// /MOD &lt;nick|YO&gt; &lt;opcion&gt; &lt;arg1&gt; &lt;arg2&gt; — EditChar (VB6 HandleEditChar, Protocol.bas:7532), el editor
    /// maestro de personajes. Porta las 13 opciones de <c>eEditOptions</c> sobre un jugador ONLINE; "YO"
    /// apunta al propio GM, igual que el original. Los valores llegan como texto y se parsean con la
    /// tolerancia del <c>val()</c> del VB6 (lo que no es número vale 0), y se respetan sus topes: el oro
    /// tiene tope de edición y la experiencia se limita a 20.000.000 por comando. También edita el .chr de un
    /// personaje OFFLINE (VB6 Protocol.bas:7538): si no está online se carga del store, se muta y se re-persiste.
    /// </summary>
    private void GmEditChar(ClientSession session, string nick, byte opcion, string arg1, string arg2)
    {
        const int MaxOroEdit = 5_000_000;      // VB6 MAX_ORO_EDIT
        const long MaxExpEdit = 20_000_000;    // VB6: If val(arg1) > 20000000 Then arg1 = 20000000
        var p = session.Player;
        // GATE POR TIER. El original tiene DOS caminos (Protocol.bas:7577): si sos RoleMaster, un
        // `Select Case` te da opciones distintas segun el segundo bit que lleves (RM+Consejero edita solo su
        // head/body/level, RM+SemiDios el head/body de cualquiera, RM+Dios ademas clase/skills/oro); y si no
        // sos RM, el `ElseIf` pide Admin o Dios y te da acceso TOTAL.
        //
        // La rama de RoleMaster es CODIGO MUERTO en esta data: la lista de RolesMasters del Server.ini esta
        // vacia y el rango no se alcanza nunca (ver GAPS-CODIGO-MUERTO.md §1). Asi que lo unico vivo es el
        // ElseIf, y eso es lo que se porta. Antes esto pedia solo "ser GM", o sea que un Consejero podia
        // editarle la clase, las skills y el oro a cualquiera.
        if (p is null || (p.Privileges & (PrivAdmin | PrivDios)) == 0) return;   // gate silencioso
        nick = nick.Replace('+', ' ');
        // "YO" = el propio GM; si no, el jugador online, o —VB6: "todos los comandos aunque el pj esté offline"—
        // su .chr cargado del store. La mutación corre igual sobre el Player offline y SaveCharacter lo persiste;
        // los Send a t.Connection (null si está offline) son no-ops, así que no hace falta ramificar.
        var t = string.Equals(nick, "YO", StringComparison.OrdinalIgnoreCase) ? p
                : (_world.PlayerByName(nick) ?? (_db.CharacterExists(nick) ? _db.LoadCharacter(nick, 0, "") : null));
        if (t is null) { session.Send(ServerMessages.LocaleMsg(75)); return; }
        // VB6 val(): toma el número inicial y lo que no parsea vale 0.
        long V(string s) => long.TryParse(s.Trim(), out var v) ? v : 0;
        string cambio;
        switch (opcion)
        {
            case 1:  t.Gold = (int)Math.Clamp(V(arg1), 0, MaxOroEdit); cambio = $"oro = {t.Gold}"; break;
            case 2:  { long xp = Math.Clamp(V(arg1), 0, MaxExpEdit); int lv = _leveling.Grant(t, xp); cambio = $"+{xp} exp" + (lv > 0 ? $" (+{lv} nivel/es)" : ""); break; }
            case 3:  t.Body = (short)V(arg1); cambio = $"body = {t.Body}"; break;
            // La cabeza VIVA también: es la que va al .chr (ver CharacterFile). Sin esto el cambio se veía
            // en pantalla y se perdía al guardar, para cualquiera que hubiera muerto alguna vez.
            case 4:  t.Head = t.OrigHead = (short)V(arg1); cambio = $"head = {t.Head}"; break;
            case 5:  t.UsersKilled = (int)V(arg1); cambio = $"ciudadanos matados = {t.UsersKilled}"; break;
            case 6:  t.NpcsKilled = (int)V(arg1); cambio = $"criminales/NPCs matados = {t.NpcsKilled}"; break;
            case 7:  t.Level = (byte)Math.Clamp(V(arg1), 1, 255); cambio = $"nivel = {t.Level}"; break;
            case 8:  t.Class = (byte)Math.Clamp(V(arg1), 1, 15); cambio = $"clase = {t.Class}"; break;
            // LAS TRES OPCIONES QUE EL ORIGINAL RESUELVE POR NOMBRE, no por número (Protocol.bas:7820, :7886
            // y :7898). El nombre viaja con `+` en lugar de los espacios y, si no resuelve, el VB6 contesta
            // LocaleMsg 414 y NO toca nada. El port las tomaba como índices y las clampeaba, así que `/MOD YO
            // 12 99` te dejaba la raza en 6 en vez de rechazar el comando, y el aviso no salía nunca.
            case 9:  { int sk = SkillPorNombre(arg1); if (sk == 0) { session.Send(ServerMessages.LocaleMsg(414)); return; }
                       t.Skills[sk] = (byte)Math.Clamp(V(arg2), 0, 100); cambio = $"skill {SkillNames[sk]} = {t.Skills[sk]}"; break; }
            case 10: t.FreeSkillPoints = (short)Math.Clamp(V(arg1), 0, short.MaxValue); cambio = $"skill points = {t.FreeSkillPoints}"; break;
            case 11: { byte sexo = GeneroPorNombre(arg1); if (sexo == 0) { session.Send(ServerMessages.LocaleMsg(414)); return; }
                       t.Gender = sexo; cambio = $"sexo = {t.Gender}"; break; }
            case 12: { byte raza = RazaPorNombre(arg1); if (raza == 0) { session.Send(ServerMessages.LocaleMsg(414)); return; }
                       t.Race = raza; cambio = $"raza = {t.Race}"; break; }
            case 13: t.Gold = (int)Math.Clamp(t.Gold + V(arg1), 0, MaxOroEdit); cambio = $"oro = {t.Gold}"; break;
            default: session.Send(ServerMessages.ConsoleMsg($"Opción de /MOD desconocida: {opcion}.", FuenteInfo)); return;
        }
        // Refrescar lo que el cambio haya tocado: stats siempre; apariencia sólo si cambió body/head/raza/sexo.
        t.Connection?.Send(ServerMessages.UpdateUserStats(t));
        // El re-broadcast de apariencia sólo tiene sentido si el char está ONLINE (en el mundo). Un char OFFLINE
        // cargado del store tiene CharIndex=0: broadcastear un CharacterChange(0) sería un paquete fantasma (el
        // VB6 offline sólo reescribe el .chr). Con CharIndex real (online) sí se difunde.
        if (opcion is 3 or 4 or 11 or 12 && t.CharIndex != 0)
            _world.BroadcastToObservers(t.Pos, ServerMessages.CharacterChange(t));
        _db.SaveCharacter(t);   // el VB6 escribe el .chr en el acto; acá persistimos para que sobreviva a un crash
        session.Send(ServerMessages.ConsoleMsg($"Charfile alterado: {t.Name} — {cambio}.", FuenteInfo));
        Console.WriteLine($"[{session.RemoteEndpoint}] /MOD {p!.Name} -> {t.Name}: {cambio}");
    }

    /// <summary>Índice de skill a partir de su NOMBRE, como lo resuelve /MOD (VB6 Protocol.bas:7822:
    /// <c>UCase$(Replace$(SkillsNames(i), " ", "+")) = UCase$(arg1)</c>). Los espacios viajan como <c>+</c>,
    /// así que "Tacticas de combate" se pide como <c>TACTICAS+DE+COMBATE</c>. Devuelve 0 si no resuelve —el
    /// caso que el original contesta con LocaleMsg 414—.</summary>
    private static int SkillPorNombre(string arg)
    {
        for (int i = 1; i <= GameLimits.NumSkills; i++)
            if (string.Equals(SkillNames[i].Replace(' ', '+'), arg, StringComparison.OrdinalIgnoreCase))
                return i;
        return 0;
    }

    /// <summary>Género a partir de su nombre (VB6 Protocol.bas:7886: sólo "MUJER" y "HOMBRE"; eGenero
    /// Hombre=1, Mujer=2). 0 = no resuelve.</summary>
    private static byte GeneroPorNombre(string arg) => arg.Trim().ToUpperInvariant() switch
    {
        "HOMBRE" => 1,
        "MUJER" => 2,
        _ => 0,
    };

    /// <summary>Raza a partir de su nombre (VB6 Protocol.bas:7898, el Select Case completo; eRaza Humano=1,
    /// Elfo=2, Drow=3, Gnomo=4, Enano=5, Orco=6). OJO con el orden: en el enum el GNOMO va antes que el
    /// ENANO, al revés de como los lista el Select. 0 = no resuelve.</summary>
    private static byte RazaPorNombre(string arg) => arg.Trim().ToUpperInvariant() switch
    {
        "HUMANO" => 1,
        "ELFO" => 2,
        "DROW" => 3,
        "GNOMO" => 4,
        "ENANO" => 5,
        "ORCO" => 6,
        _ => 0,
    };

    /// <summary>DARPUN (/DARPUN nick@puntos): un GM acredita créditos de donador a un jugador online — la vía por
    /// la que se acredita a quien donó dinero real. VB6 Protocol.bas:16539: anuncia el otorgamiento a todo el
    /// servidor. El crédito se gasta en la tienda [SHOP] (HandleRPremios).</summary>
    private void GmDarpun(ClientSession session, string nick, long puntos)
    {
        var p = session.Player;
        if (!IsAltoGm(p)) return;   // VB6 HandleDARPUN: sólo Dios/Admin/RoleMaster (bloquea SemiDios), silencio si no
        var t = _world.PlayerByName(nick.Replace('+', ' '));
        if (t is null) { session.Send(ServerMessages.LocaleMsg(75)); return; }   // usuario offline/inexistente
        if (puntos <= 0) { session.Send(ServerMessages.ConsoleMsg("La cantidad de créditos debe ser positiva.", FuenteInfo)); return; }
        t.CreditoDonador += puntos;
        _db.SaveCharacter(t);   // el VB6 escribe el .chr en el acto
        t.Connection?.Send(ServerMessages.ConsoleMsg($"Recibiste {puntos} créditos de donador. Total: {t.CreditoDonador}.", FuenteInfo));
        _world.Broadcast(ServerMessages.ConsoleMsg($"Servidor> {p!.Name} otorgó {puntos} créditos de donador a {t.Name}.", 0));   // VB6 :16583 (ToAll)
        Console.WriteLine($"[{session.RemoteEndpoint}] /DARPUN {p!.Name} -> {t.Name}: +{puntos} créditos");
    }

    /// <summary>Donador (/DONADOR nick): un GM (Dios) togglea el flag donador de la CUENTA del jugador. VB6
    /// Protocol.bas:16084: persiste en el .cnt (así todos los chars de la cuenta heredan el beneficio) y refresca
    /// el estado del char online. Avisa al GM (478/479) y al jugador (424/480).</summary>
    private void GmDonador(ClientSession session, string nick)
    {
        var p = session.Player;
        if (!IsDios(p)) return;   // VB6 HandleDonador: exige el tier Dios (bit 8), silencio si no
        var t = _world.PlayerByName(nick.Replace('+', ' '));
        if (t is null) { session.Send(ServerMessages.LocaleMsg(75)); return; }
        var acc = _db.LoadAccount(t.Account);
        if (acc is null) { session.Send(ServerMessages.ConsoleMsg($"No se encontró la cuenta de {t.Name}.", FuenteInfo)); return; }
        acc.Donador = !acc.Donador;   // toggle
        _db.SaveAccount(acc);         // persiste en el .cnt (VB6 :16127)
        t.Donador = acc.Donador;      // reflejar en el char online
        _world.BroadcastToObservers(t.Pos, ServerMessages.UpdateTagAndStatus(t));   // VB6 RefreshCharStatus
        if (acc.Donador)
        {
            session.Send(ServerMessages.LocaleMsg(478, t.Name));   // al GM: "Has hecho donador a la cuenta de #1"
            t.Connection?.Send(ServerMessages.LocaleMsg(424));     // al jugador: bienvenida al modo donador
        }
        else
        {
            session.Send(ServerMessages.LocaleMsg(479, t.Name));   // al GM: "Has quitado el donador a la cuenta de #1"
            t.Connection?.Send(ServerMessages.LocaleMsg(480));     // al jugador: "Ya no eres usuario donador"
        }
        Console.WriteLine($"[{session.RemoteEndpoint}] /DONADOR {p!.Name} -> {t.Name} (cuenta {acc.Name}): {(acc.Donador ? "ON" : "OFF")}");
    }

    /// <summary>
    /// /CARCEL &lt;nick&gt; &lt;motivo&gt; &lt;minutos&gt; — Jail (VB6 HandleJail, Protocol.bas:7289 + Encarcelar,
    /// Admin.bas:164). Gates del original, en orden: nick o motivo vacíos → LocaleMsg 411; el objetivo no está
    /// online → 75; el objetivo es GM → 298 (no se encarcela a un staff); más de 60 minutos → 412. Si pasa
    /// todo, teletransporta al preso a la cárcel y le avisa con LocaleMsg 145 (nombre del GM). La condena vive
    /// en Player.JailMinutesLeft, se persiste en el .chr y la descuenta el tick del mundo.
    /// </summary>
    /// <summary>Difunde un LocaleMsg a todos los GMs conectados (VB6 SendTarget.ToADMINS). Incluye al emisor.</summary>
    private void BroadcastToAdmins(byte[] packet)
    {
        foreach (var gm in _world.Players.Where(pl => pl.IsGm))
            gm.Connection?.Send(packet);
    }

    /// <summary>
    /// /BAN por PERSONAJE (VB6 HandleBanChar → BanCharacter/UnBanCharacter, Admin.bas:440/547). Distinto del
    /// baneo por cuenta del panel admin: marca el <c>flags.Ban</c> del .chr, así que ese personaje puntual no
    /// puede entrar aunque su cuenta esté limpia. Anota una pena y —si está conectado— lo desconecta en el acto.
    /// Difunde LocaleMsg 482 a los GMs. Sobre objetivos offline toca el .chr en disco (carga/guarda el Player).
    /// </summary>
    private void GmBan(ClientSession session, string nick, bool ban)
    {
        var p = session.Player;
        if (!IsSemiDiosOrHigher(p)) return;   // VB6 Protocol.bas:8617: Admin/Dios/SemiDios, NO Consejero ni RoleMaster
        nick = nick.Replace('+', ' ').Trim();
        if (string.IsNullOrWhiteSpace(nick)) return;

        var online = _world.PlayerByName(nick);
        var target = online ?? (_db.CharacterExists(nick) ? _db.LoadCharacter(nick, 0, "") : null);
        if (target is null) { session.Send(ServerMessages.LocaleMsg(75)); return; }   // no existe → "Usuario offline"
        // "No se banea a un staff": el rango del objetivo hay que sacarlo del Server.ini, no del `.chr`. Si está
        // OFFLINE, `target` sale del disco y —desde que los privilegios no se persisten— trae siempre
        // `Privilegios=1`, así que `target.IsGm` era false SIEMPRE y la protección no disparaba nunca. El VB6
        // resuelve el rango del offline con `UserDarPrivilegioLevel(UserName)`, o sea las listas del ini
        // (Admin.bas:459-464, BanCharacter), justamente porque el `.chr` no lo tiene.
        //
        // Sin esto, un Consejero podía banear al Dios del servidor mientras estaba desconectado: el `Banned=1`
        // queda escrito en el `.chr` y el gate de baneo corre ANTES de que se apliquen los privilegios del ini,
        // así que el dueño no podía volver a entrar nunca más sin editar la base a mano.
        if (ban && (target.IsGm || _ini.EsStaff(nick))) { session.Send(ServerMessages.LocaleMsg(484)); return; }
        // El estado PREVIO importa y no se miraba: banear a alguien ya baneado volvía a escribir la pena y a
        // confirmar como si fuera nuevo, y desbanear a alguien que no lo estaba confirmaba igual. El original
        // gatea las dos (Admin.bas:468 y :580) y son los únicos dos caminos que devuelven "no hice nada".
        if (ban && target.Banned) { session.Send(ServerMessages.LocaleMsg(390)); return; }
        if (!ban && !target.Banned) { session.Send(ServerMessages.LocaleMsg(486)); return; }

        target.Banned = ban;
        if (ban)
            target.Penalties.Add($"{p!.Name.ToLowerInvariant()}: BAN {DateTime.Now:dd/MM/yyyy HH:mm:ss}");
        _db.SaveCharacter(target);   // persistir el flag/pena en el acto

        // Aviso a los GMs (VB6: LocaleMsg 482 "#1 ha baneado a #2" / al desbanear no hay broadcast, sólo confirma).
        if (ban)
        {
            BroadcastToAdmins(ServerMessages.LocaleMsg(482, $"{p!.Name}%{nick}"));
            online?.Connection?.Close("baneado");   // VB6 CloseSocket(tuser): al baneado se lo echa del mundo
            // La confirmación al GM es el 481, con el nombre (Admin.bas:472). Su texto dice "está offline, pero
            // ha sido baneado" porque en el original esta rama es la del personaje offline; el fork la usa para
            // las dos y se porta igual. Acá salía un ConsoleMsg con texto nuestro.
            session.Send(ServerMessages.LocaleMsg(481, nick));
        }
        else
        {
            session.Send(ServerMessages.LocaleMsg(485, nick));   // "#1 ha sido desbaneado" (Admin.bas:577)
        }
        Console.WriteLine($"[{session.RemoteEndpoint}] /BAN {(ban ? "ban" : "unban")} {p!.Name} -> {nick}");
    }

    /// <summary>
    /// /ADVERTENCIA (VB6 HandleWarnUser, Protocol.bas:7439): asienta una advertencia en el registro de penas
    /// del personaje, para que quede el antecedente antes de un ban. A diferencia de /CARCEL, funciona sobre
    /// personajes OFFLINE (sólo necesita el .chr) y el advertido NO se entera de nada — es una anotación para
    /// el staff, no una notificación.
    ///
    /// El store de penas ya lo maneja bien el .NET: CharacterFile serializa <c>[PENAS]</c> desde
    /// <c>p.Penalties</c> en cada save y lo relee al cargar. OJO con la trampa que la investigación destapó:
    /// para un objetivo online hay que tocar SU Player en memoria y guardarlo —nunca escribir el .chr por
    /// afuera—, porque el próximo SaveCharacter reescribe la sección PENAS completa desde la lista y lo pisaría.
    /// </summary>
    private void GmWarn(ClientSession session, string nick, string reason)
    {
        var p = session.Player;
        if (!RequireGm(session, p)) return;
        nick = nick.Replace('+', ' ').Trim();
        if (string.IsNullOrWhiteSpace(nick) || string.IsNullOrWhiteSpace(reason))
        { session.Send(ServerMessages.LocaleMsg(413)); return; }   // "Utilice /advertencia nick@motivo"

        // Objetivo: el jugador conectado, o su .chr en disco (el original sólo chequea FileExist). Un nick
        // inexistente cae acá en null → silencio total, igual que el VB6 cuando el .chr no existe.
        var online = _world.PlayerByName(nick);
        var target = online ?? (_db.CharacterExists(nick) ? _db.LoadCharacter(nick, 0, "") : null);
        if (target is null) return;

        // No se advierte a un miembro del staff (VB6: LocaleMsg 298 si el objetivo figura en la tabla de GMs).
        // Con el objetivo OFFLINE el `.chr` no trae privilegios, así que hay que preguntarle al ini — mismo
        // motivo que en el /BAN de arriba.
        if (target.IsGm || _ini.EsStaff(nick)) { session.Send(ServerMessages.LocaleMsg(298)); return; }

        target.Penalties.Add($"{p!.Name.ToLowerInvariant()}: ADVERTENCIA por: {reason.ToLowerInvariant()} " +
                             $"{DateTime.Now:dd/MM/yyyy HH:mm:ss}");
        _db.SaveCharacter(target);   // en el acto, como /CARCEL: una sanción no se pierde si el server cae
        session.Send(ServerMessages.ConsoleMsg($"Has advertido a {nick.ToUpperInvariant()}.", 3));
        Console.WriteLine($"[{session.RemoteEndpoint}] /ADVERTENCIA {p.Name} -> {nick}");
    }

    /// <summary>
    /// ¿El lanzador puede ayudar (curar/beneficiar) al objetivo? (VB6 PuedeAyudar, modHechizos.bas:1910).
    /// Un renegado/criminal ayuda a cualquiera; la Armada y los ciudadanos imperiales sólo entre sí; la
    /// República y la Milicia sólo entre sí. El Caos y los GMs caen al permitir-por-defecto — un agujero del
    /// original (pueden curar hasta a un enemigo de facción) que se preserva a propósito.
    /// </summary>
    private static bool PuedeAyudar(Player caster, Player target)
    {
        if (caster.FactionStatus == Player.StatusCriminal) return true;   // esRene → siempre
        bool casterImperio = caster.IsArmada || caster.IsCitizen;
        if (casterImperio && !(target.IsArmada || target.IsCitizen)) return false;
        bool casterRepublica = caster.IsRepublicano || caster.IsMilicia;
        if (casterRepublica && !(target.IsRepublicano || target.IsMilicia)) return false;
        return true;   // Caos, GMs y cualquier status fuera de las facciones conocidas
    }

    /// <summary>IP de un jugador conectado, normalizada (VB6 <c>UserList(i).ip</c>). "" si no está conectado.</summary>
    private static string IpOfPlayer(Player p) => ConnectionGuard.NormalizeIp(p.Connection?.RemoteEndpoint ?? "");

    // --- Tanda 14: recargas en caliente ---

    private enum GmReloadKind { Npcs, Spells, Objects }

    /// <summary>Los tres <c>/RELOAD*</c> que tienen archivo equivalente en el port: <c>/RELOADNPCS</c>(81) →
    /// CargaNpcsDat, <c>/RELOADHECHIZOS</c>(83) → CargarHechizos y <c>/RELOADOBJ</c>(84) → LoadOBJData.
    /// Gate: sólo Admin o Dios, los tres.
    ///
    /// Emisión asimétrica, y es de la fuente: <b>sólo el de NPCs contesta</b> (<c>LocaleMsg(416)</c>); los de
    /// hechizos y objetos recargan en silencio. Acá se respeta esa asimetría.
    ///
    /// La recarga reemplaza el diccionario entero pero <b>no re-sincroniza lo que ya está vivo</b>: un NPC
    /// spawneado guarda la referencia a su plantilla vieja y un ítem en un inventario sigue con su índice. Es
    /// el mismo comportamiento del original, donde el array se pisa y los que ya nacieron tienen su copia.
    /// El <c>/RELOADSINI</c>(82) y <c>/SETINIVAR</c>(85) NO se portan: leen y escriben el Server.ini, que en
    /// este port no existe —su equivalente son los flags de arranque y el panel de admin, que ya ajusta los
    /// intervalos en caliente—. Ver fase5/GAPS-CODIGO-MUERTO.md.</summary>
    private void GmReload(ClientSession session, GmReloadKind kind)
    {
        var p = session.Player;
        if (p is null || (p.Privileges & (PrivUser | PrivConsejero | PrivSemiDios | PrivRoleMaster)) != 0) return;
        int n = kind switch
        {
            GmReloadKind.Npcs => _data.ReloadNpcs(),
            GmReloadKind.Spells => _data.ReloadSpells(),
            _ => _data.ReloadObjects(),
        };
        if (kind == GmReloadKind.Npcs) session.Send(ServerMessages.LocaleMsg(416));   // sólo éste contesta
        Console.WriteLine($"[{session.RemoteEndpoint}] /RELOAD{kind} {p.Name}: {n} entradas");
    }

    /// <summary>/BANIPRELOAD (VB6 HandleBannedIPReload, Protocol.bas:9598): <c>BanIpGuardar</c> +
    /// <c>BanIpCargar</c>, o sea graba la lista a disco y la vuelve a leer. No emite nada. Gate: Admin o Dios.
    ///
    /// Un triaje anterior lo daba por MUERTO leyendo "cuerpo vacío tras el gate", y está vivo: el cuerpo hace
    /// el round-trip completo. Lo que sí es cierto es que no manda un solo byte al cliente.</summary>
    private void GmBannedIpReload(ClientSession session)
    {
        var p = session.Player;
        if (p is null || (p.Privileges & (PrivUser | PrivConsejero | PrivSemiDios | PrivRoleMaster)) != 0) return;
        _admin?.PersistBannedIps?.Invoke();
        int n = _admin?.ReloadBannedIps() ?? -1;
        Console.WriteLine($"[{session.RemoteEndpoint}] /BANIPRELOAD {p.Name}: {n} IPs");
    }

    // --- Tanda 13: responder consultas, el toggle del centinela y la cuenta regresiva ---

    /// <summary>ResponderGM (VB6 HandleResponderGM, Protocol.bas:15448): la respuesta del GM a una consulta
    /// de /GM. Payload: tres ASCIIString — nick, mensaje y un "modo" que sólo se usa comparado con la cadena
    /// literal <c>"Usuario"</c>. Gate: Admin, SemiDios o Dios (acá el original SÍ deja al SemiDios).
    ///
    /// Tres ramas, y la elección no es por quién sos sino por DÓNDE está el destinatario:
    /// <list type="number">
    /// <item>offline → <c>LocaleMsg(75)</c> y la respuesta va por CORREO, avisando "Se le ha enviado por
    ///   correo la respuesta enviada." (o el aviso de casilla llena si no entra).</item>
    /// <item>online pero en un mapa <b>Pk</b> → <c>LocaleMsg(426)</c> y también por correo: el original no
    ///   quiere abrirle un cartel modal a alguien que puede estar en medio de una pelea.</item>
    /// <item>online en zona no-Pk → <c>SendMsgBox</c> con "{gm}: {mensaje}" y <c>LocaleMsg(251)</c> al GM.</item>
    /// </list>
    /// Si el tercer string no es exactamente "Usuario" no pasa nada: el <c>If</c> del original no tiene otra
    /// rama, así que el comando se descarta en silencio. Se porta igual.</summary>
    private void GmResponderGM(ClientSession session, string nick, string mensaje, string modo)
    {
        var p = session.Player;
        if (p is null || (p.Privileges & (PrivAdmin | PrivSemiDios | PrivDios)) == 0) return;   // gate silencioso
        if (modo != "Usuario") return;   // el original no tiene rama para otro valor
        string dest = nick.Replace('+', ' ').Trim();
        var t = _world.PlayerByName(dest);
        if (t is null)
        {
            session.Send(ServerMessages.LocaleMsg(75));                     // "Usuario offline"
            if (!_db.CharacterExists(dest)) return;                         // sin .chr no hay a quién escribirle
            _world.DeliverMail(dest, new MailEntry(p.Name, $"{p.Name}:{mensaje}", 0, 0));
            PersistMail();
            session.Send(ServerMessages.ConsoleMsg("Se le ha enviado por correo la respuesta enviada.", 3));
        }
        else if (_data.IsPkMap(t.Pos.Map))
        {
            session.Send(ServerMessages.LocaleMsg(426));
            _world.DeliverMail(dest, new MailEntry(p.Name, $"{p.Name}:{mensaje}", 0, 0));
            PersistMail();
        }
        else
        {
            t.Connection?.Send(ServerMessages.SendMsgBox($"{p.Name}: {mensaje}"));
            session.Send(ServerMessages.LocaleMsg(251));
        }
        // OJO: responder NO saca la consulta de la cola. Es tentador hacerlo, pero en el
        // original la única línea que cerraría el caso está COMENTADA (`'UserList(tIndex).flags.EnvioGM = 0`) y
        // no hay ninguna llamada a `Ayuda.Quitar` en todo el handler. Sacarla es justamente para lo que existe
        // SOSDONE, que el cliente manda cuando el GM marca la consulta como atendida.
        Console.WriteLine($"[{session.RemoteEndpoint}] ResponderGM {p.Name} -> {dest}");
    }

    /// <summary>/CENTINELAACTIVADO (VB6 HandleToggleCentinelActivated, Protocol.bas:10840): conmuta el
    /// <c>centinelaActivado</c> global y avisa a los ADMINS con "El centinela ha sido activado/desactivado."
    /// en FONTTYPE_SERVER (8). Gate: excluye User, Consejero y SemiDios — o sea Dios, Admin y RoleMaster.
    ///
    /// El flag <b>tampoco gatea nada en el original</b>, y eso no es una omisión del port: el subsistema
    /// anti-macro entero (modCentinela.bas) es CÓDIGO MUERTO en esta variante, porque sus dos únicos puntos de
    /// entrada están comentados en la fuente — <c>'Call modCentinela.PasarMinutoCentinela</c> (frmMain.frm:749)
    /// y <c>'Call modCentinela.CallUserAttention</c> (mMainLoop.bas:21)—. Sin ellos `RevisandoUserIndex` nunca
    /// sale de 0: el NPC 622/623 no se spawnea, nadie recibe el desafío de clave y nadie es encarcelado por
    /// macro. Por eso <c>CentinelReport</c> rebota en silencio también acá (en el VB6 sale por el
    /// <c>If CentinelaNPCIndex = 0 Then Exit Sub</c> de CentinelaSendClave). El toggle y su aviso son todo lo
    /// que el comando hace de verdad. Ver fase5/GAPS-CODIGO-MUERTO.md §4l.
    ///
    /// El original además resetea el estado del centinela y le quita el NPC: sin subsistema vivo, esas dos
    /// líneas no tienen nada que limpiar.</summary>
    private void GmCentinelToggle(ClientSession session)
    {
        var p = session.Player;
        if (p is null || (p.Privileges & (PrivUser | PrivConsejero | PrivSemiDios)) != 0) return;
        if (_admin is null) return;
        _admin.CentinelActive = !_admin.CentinelActive;
        BroadcastToAdmins(ServerMessages.ConsoleMsg(
            _admin.CentinelActive ? "El centinela ha sido activado." : "El centinela ha sido desactivado.", 8));
        Console.WriteLine($"[{session.RemoteEndpoint}] /CENTINELAACTIVADO {p.Name}: {(_admin.CentinelActive ? "ON" : "OFF")}");
    }

    /// <summary>CuentaRegresiva (VB6 HandleCuentaRegresiva, Protocol.bas:16704): arranca un conteo. Payload
    /// <c>Byte(segundos) + Byte(mapa)</c>; con mapa 0 el conteo va a TODO el server y con un mapa &gt; 0 sólo a
    /// ese mapa. El handler sólo setea las dos globales: el que emite es el reloj del server
    /// (General.bas:1991), que cada segundo manda <c>LocaleMsg(434, N-1)</c> y al llegar al final
    /// <c>LocaleMsg(435)</c>.
    ///
    /// <b>Divergencia deliberada de seguridad</b>: el original <b>no tiene gate de privilegios</b> acá —
    /// cualquier jugador podía lanzar una cuenta regresiva a todo el servidor, que es spam global gratis—.
    /// Acá se exige GM, en la misma línea que el resto del endurecimiento del port (ver fase5).</summary>
    private void GmCuentaRegresiva(ClientSession session, byte segundos, byte mapa)
    {
        var p = session.Player;
        if (!RequireGm(session, p)) return;   // el VB6 no gatea; acá sí (divergencia documentada)
        _world.CountdownRemaining = segundos;
        _world.CountdownMap = mapa;
        Console.WriteLine($"[{session.RemoteEndpoint}] CuentaRegresiva {p!.Name}: {segundos}s en {(mapa == 0 ? "todo el server" : $"mapa {mapa}")}");
    }

    // --- Tanda 12: el editor de triggers por tile ---

    /// <summary>/TRIGGER &lt;n&gt; (VB6 HandleSetTrigger, Protocol.bas:9488): le pone el trigger <c>n</c> al tile
    /// donde está parado el GM y le contesta el mismo texto que registra en el log. Gate: Admin o Dios.
    ///
    /// El <c>If tTrigger &gt;= 0</c> del original es siempre verdadero (viene de un Byte), así que no filtra
    /// nada: cualquier valor 0..255 entra, incluso los que no están en eTrigger. Se porta igual — el trigger
    /// se guarda tal cual y los gates simplemente no lo reconocen.
    ///
    /// OJO EL TEXTO: éste imprime "en mapa M X,Y" (sin espacio tras la coma) y el de consulta "X, Y" (con
    /// espacio). No es un descuido de lectura: son dos literales distintos en la fuente.</summary>
    /// <summary>/RELOADSINI (VB6 HandleReloadServerIni, Protocol.bas:1052 → <c>LogGM</c> + <c>LoadSini</c>):
    /// relee el Server.ini sin reiniciar.
    ///
    /// <para>DICE QUÉ NO TOMÓ EFECTO, y eso es la mitad del comando. Varios ajustes ya se consumieron al
    /// arrancar —los puertos están escuchando, el tope de conexiones vive en el <c>ConnectionGuard</c>, el
    /// archivo de log ya está abierto—, así que un "recargado" a secas sería mentira: el host cambiaría el
    /// puerto, vería el OK y se enteraría de que no pasó nada recién cuando un amigo no puede entrar.</para></summary>
    private void GmRecargarIni(ClientSession session)
    {
        var p = session.Player;
        if (!RequireGm(session, p)) return;
        if (!_ini.Recargar())
        {
            session.Send(ServerMessages.ConsoleMsg("No hay Server.ini de dónde releer (el server anda con los valores por defecto).", FuenteInfo));
            return;
        }
        session.Send(ServerMessages.ConsoleMsg($"Server.ini recargado: {_ini.CantidadStaff} de staff, exp x{_ini.Exp}, oro x{_ini.Oro}.", FuenteInfo));
        session.Send(ServerMessages.ConsoleMsg(
            "Piden reiniciar (ya estaban en uso): " + string.Join(", ", Data.ServerIni.PidenReiniciar), FuenteInfo));
        Console.WriteLine($"[{session.RemoteEndpoint}] /RELOADSINI {p!.Name}: Server.ini releído");
    }

    /// <summary>/SETINIVAR &lt;sección&gt; &lt;clave&gt; &lt;valor&gt; (VB6 HandleSetIniVar, Protocol.bas:1061 →
    /// <c>WriteVar</c> sobre el Server.ini + LogGM): escribe una clave del ini desde adentro del juego, y acá
    /// además la aplica en el acto (el original obliga a un /RELOADSINI aparte).
    ///
    /// <para>SÓLO ADMIN, y el original no lo pide. Es la única puerta del juego que ESCRIBE la config del
    /// host: con ella se pueden editar las listas de staff, o sea repartirse el rango. Dejarla en el mismo
    /// nivel que /LLUVIA convertía a cualquier SemiDiós en Admin, que es justo lo que el ini existe para
    /// decidir. Ver la nota de `estado-de-seguridad`: el rango se deriva del ini en CADA login.</para></summary>
    private void GmEscribirIni(ClientSession session, string seccion, string clave, string valor)
    {
        var p = session.Player;
        if (!RequireGm(session, p)) return;
        if (_ini.PrivilegioDe(p!.Name) != 16)   // PlayerType.Admin
        {
            session.Send(ServerMessages.ConsoleMsg("Sólo un Admin puede escribir el Server.ini.", FuenteInfo));
            return;
        }
        if (_ini.Ruta is null)
        {
            session.Send(ServerMessages.ConsoleMsg("No hay Server.ini en disco para escribir.", FuenteInfo));
            return;
        }
        try
        {
            var ini = Data.IniFile.Load(_ini.Ruta);
            ini.Set(seccion, clave, valor);
            ini.Save(_ini.Ruta);
            _ini.Recargar();
            session.Send(ServerMessages.ConsoleMsg($"Modificó {seccion} {clave} a {valor}.", FuenteInfo));
            Console.WriteLine($"[{session.RemoteEndpoint}] /SETINIVAR {p.Name}: [{seccion}] {clave}={valor}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            session.Send(ServerMessages.ConsoleMsg($"No se pudo escribir el Server.ini: {ex.Message}", FuenteInfo));
        }
    }

    private void GmSetTrigger(ClientSession session, byte trigger)
    {
        var p = session.Player;
        if (p is null || (p.Privileges & (PrivUser | PrivConsejero | PrivSemiDios | PrivRoleMaster)) != 0) return;
        _data.SetTileTrigger(p.Pos.Map, p.Pos.X, p.Pos.Y, trigger);
        string texto = $"Trigger {trigger} en mapa {p.Pos.Map} {p.Pos.X},{p.Pos.Y}";
        session.Send(ServerMessages.ConsoleMsg(texto, 3));
        Console.WriteLine($"[{session.RemoteEndpoint}] /TRIGGER {p.Name}: {texto}");
    }

    /// <summary>/TRIGGER sin argumento (VB6 HandleAskTrigger, Protocol.bas:9531): informa el trigger del tile
    /// donde está parado el GM. Gate: Admin o Dios. El valor sale de MapTriggers.json (los seis del enum
    /// eTrigger, no sólo los tres que gatean algo) más lo que haya puesto /TRIGGER en runtime.</summary>
    private void GmAskTrigger(ClientSession session)
    {
        var p = session.Player;
        if (p is null || (p.Privileges & (PrivUser | PrivConsejero | PrivSemiDios | PrivRoleMaster)) != 0) return;
        byte trigger = _data.TileTrigger(p.Pos.Map, p.Pos.X, p.Pos.Y);
        session.Send(ServerMessages.ConsoleMsg($"Trigger {trigger} en mapa {p.Pos.Map} {p.Pos.X}, {p.Pos.Y}", 3));
    }

    // --- Tanda 10: la cola de consultas a los GMs (VB6 `Ayuda`) ---

    /// <summary>/SHOW SOS (VB6 HandleSOSShowList, Protocol.bas:6944 → WriteShowSOSForm): le manda al GM la cola
    /// de consultas pendientes en un solo <c>ShowSOSForm</c>, con las entradas unidas por NUL. Gate: excluye
    /// sólo al usuario común (cualquier tier de staff la ve). Con la cola vacía manda el paquete IGUAL, con la
    /// cadena vacía, que es lo que hace el original.</summary>
    private void GmShowSosList(ClientSession session)
    {
        var p = session.Player;
        if (p is null || (p.Privileges & PrivUser) != 0) return;   // gate silencioso
        session.Send(ServerMessages.ShowSosForm(_world.HelpEntries));
    }

    /// <summary>SOSDONE &lt;nick&gt; (VB6 HandleSOSRemove, Protocol.bas:6967): saca esa consulta de la cola — es lo
    /// que manda el cliente cuando el GM atiende una. No emite NADA en ninguno de los dos casos.
    ///
    /// El gate del original está escrito <c>If Not .flags.Privilegios And PlayerType.User</c>, que en la
    /// precedencia bitwise de VB6 es <c>(Not Privilegios) And User</c>: para un GM da distinto de cero y para
    /// un usuario común da cero, así que funciona igual que un "sólo staff" — por accidente, pero funciona.
    ///
    /// En el VB6 vivo este comando es INERTE porque <c>Ayuda.Quitar</c> compara mal (busca el nombre a partir
    /// del carácter 10 del elemento, donde vive la hora). Acá quita de verdad; ver la nota de World._help.</summary>
    private void GmSosRemove(ClientSession session, string nick)
    {
        var p = session.Player;
        if (p is null || (p.Privileges & PrivUser) != 0) return;   // gate silencioso
        if (_world.HelpRemove(nick.Replace('+', ' ').Trim()))
            Console.WriteLine($"[{session.RemoteEndpoint}] SOSDONE {p.Name} -> {nick}");
    }

    /// <summary>/BORRAR SOS (VB6 HandleCleanSOS, Protocol.bas:10292 → Ayuda.Reset): vacía la cola entera.
    /// No le contesta nada al GM. Gate: sólo Admin o Dios.</summary>
    private void GmCleanSos(ClientSession session)
    {
        var p = session.Player;
        if (p is null || (p.Privileges & (PrivUser | PrivConsejero | PrivSemiDios | PrivRoleMaster)) != 0) return;
        int n = _world.HelpClear();
        Console.WriteLine($"[{session.RemoteEndpoint}] /BORRAR SOS {p.Name}: {n} consultas borradas");
    }

    // --- Tanda 9: el editor de MapInfo y el backup del mapa ---

    /// <summary>Las tres variantes de <c>/MODMAPINFO</c> que operan sobre el mapa donde ESTÁ PARADO el GM
    /// (VB6 HandleChangeMapInfoPK :10391, ...Backup :10341, ...Restricted :10434). Las tres tienen la misma
    /// forma: cambian un campo del <c>MapInfo</c> del mapa, lo escriben al mapa<i>N</i>.dat y confirman por
    /// consola con el valor que quedó. Gate: Admin o Dios (los tres, sin excluir RoleMaster).
    ///
    /// De los tres campos, <b>sólo Pk tiene consumidores</b>: es el que decide si morir cuesta el inventario
    /// (<c>IsPkMap</c> → TirarTodo). <c>BackUp</c> y <c>Restringir</c> en esta variante del VB6 <b>sólo se
    /// escriben</b> —FileIO.bas:1415/1417 al cargar el .dat y estos dos handlers; ningún otro sitio los lee,
    /// o sea que el gate de "mapa restringido a la Armada" que sugiere el nombre no existe—. Se portan igual
    /// para que el estado y la emisión coincidan con el original, pero no inventan un gate que la fuente no
    /// tiene. Ver fase5/GAPS-CODIGO-MUERTO.md §4k.
    ///
    /// Los valores válidos de RESTRINGIR son exactamente cinco ("NEWBIE", "NO", "ARMADA", "CAOS", "FACCION")
    /// y con cualquier otro el original <b>no hace ni contesta nada</b> (su rama de aviso está comentada).</summary>
    private void GmChangeMapInfo(ClientSession session, MapInfoField campo, bool flag, string texto)
    {
        var p = session.Player;
        if (p is null || (p.Privileges & (PrivAdmin | PrivDios)) == 0) return;   // gate silencioso
        short mapa = p.Pos.Map;
        switch (campo)
        {
            case MapInfoField.Pk:
                _data.SetMapSafe(mapa, !flag);
                // El original imprime el Boolean de VB6, que se formatea "True"/"False".
                session.Send(ServerMessages.ConsoleMsg($"Mapa {mapa} PK: {(flag ? "True" : "False")}", 3));
                break;
            case MapInfoField.Backup:
                _data.SetMapBackup(mapa, flag);
                // Acá imprime el BYTE (el original convierte el Boolean a 1/0 antes de guardarlo).
                session.Send(ServerMessages.ConsoleMsg($"Mapa {mapa} Backup: {(flag ? 1 : 0)}", 3));
                break;
            case MapInfoField.Restrict:
                string v = texto.Trim().ToUpperInvariant();
                if (v is not ("NEWBIE" or "NO" or "ARMADA" or "CAOS" or "FACCION")) return;   // silencio, como el VB6
                _data.SetMapRestrict(mapa, v);
                session.Send(ServerMessages.ConsoleMsg($"Mapa {mapa} Restringido: {v}", 3));
                break;
            case MapInfoField.NoMagic:
                _data.SetMapNoMagic(mapa, flag);
                session.Send(ServerMessages.ConsoleMsg($"Mapa {mapa} MagiaSinEfecto: {(flag ? 1 : 0)}", 3));
                break;
            case MapInfoField.NoInvi:
                _data.SetMapNoInvi(mapa, flag);
                session.Send(ServerMessages.ConsoleMsg($"Mapa {mapa} InviSinEfecto: {(flag ? 1 : 0)}", 3));
                break;
            case MapInfoField.NoResu:
                _data.SetMapNoResu(mapa, flag);
                session.Send(ServerMessages.ConsoleMsg($"Mapa {mapa} ResuSinEfecto: {(flag ? 1 : 0)}", 3));
                break;
            default:
                // TERRENO y ZONA validan contra LA MISMA lista de seis (sí, el original acepta "BOSQUE" como
                // zona y "CIUDAD" como terreno: es un copy-paste de la fuente, no un error de lectura), y con
                // cualquier otro valor no hacen ni contestan nada.
                string w = texto.Trim().ToUpperInvariant();
                if (w is not ("BOSQUE" or "NIEVE" or "DESIERTO" or "CIUDAD" or "CAMPO" or "DUNGEON")) return;
                if (campo == MapInfoField.Terrain)
                {
                    _data.SetMapTerrain(mapa, w);
                    session.Send(ServerMessages.ConsoleMsg($"Mapa {mapa} Terreno: {w}", 3));
                }
                else
                {
                    _data.SetMapZone(mapa, w);
                    session.Send(ServerMessages.ConsoleMsg($"Mapa {mapa} Zona: {w}", 3));
                }
                break;
        }
        Console.WriteLine($"[{session.RemoteEndpoint}] /MODMAPINFO {campo} {p.Name} -> mapa {mapa}");
    }

    private enum MapInfoField { Pk, Backup, Restrict, NoMagic, NoInvi, NoResu, Terrain, Zone }

    /// <summary>/GUARDAMAPA (VB6 HandleSaveMap, Protocol.bas:10754 → <c>GrabarMapa</c>): guarda el mapa donde
    /// está el GM en la carpeta de backup del server y contesta <c>LocaleMsg(417)</c>. Gate: sólo Admin o Dios.
    ///
    /// El original reescribe el .map/.inf binario del mapa. Este slice no tiene escritor de ese formato (lee
    /// .csm ya transcodeado a JSON), así que graba lo que de ese mapa <b>puede cambiar en runtime</b>: los
    /// ítems del suelo, los tile-exits (los que crea y destruye /CT) y los tiles que /BLOQ dejó bloqueados.
    /// Es una divergencia de FORMATO, no de intención: el comando sigue siendo "dejá en disco el mapa como
    /// está ahora". El archivo va a &lt;exe&gt;/worldbackup/mapa&lt;N&gt;.json, el equivalente del
    /// <c>App.Path\WorldBackUp\Mapa&lt;N&gt;</c> del original.</summary>
    private void GmSaveMap(ClientSession session)
    {
        var p = session.Player;
        if (p is null || (p.Privileges & (PrivUser | PrivConsejero | PrivSemiDios | PrivRoleMaster)) != 0) return;
        short mapa = p.Pos.Map;
        try
        {
            string dir = Path.Combine(AppContext.BaseDirectory, "worldbackup");
            Directory.CreateDirectory(dir);
            var objetos = _world.GroundOnMap(mapa)
                .Select(g => new { x = g.Pos.X, y = g.Pos.Y, objIndex = g.ObjIndex, amount = g.Amount })
                .OrderBy(o => o.y).ThenBy(o => o.x).ToArray();
            var salidas = _data.MapExits.Where(e => e.Key.Map == mapa)
                .Select(e => new { x = e.Key.X, y = e.Key.Y, destMap = e.Value.DestMap, destX = e.Value.DestX, destY = e.Value.DestY })
                .OrderBy(e => e.y).ThenBy(e => e.x).ToArray();
            var snapshot = new
            {
                mapa,
                pk = _data.IsPkMap(mapa),
                backup = _data.MapBackup(mapa),
                restringir = _data.MapRestrict(mapa),
                objetos,
                salidas,
            };
            File.WriteAllText(Path.Combine(dir, $"mapa{mapa}.json"),
                JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"[{session.RemoteEndpoint}] /GUARDAMAPA {p.Name} -> mapa {mapa} ({objetos.Length} objetos, {salidas.Length} salidas)");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[{session.RemoteEndpoint}] /GUARDAMAPA falló: {ex.Message}");
        }
        session.Send(ServerMessages.LocaleMsg(417));
    }

    // --- Tanda 8: los tres comandos de la lista de IPs baneadas y el espía del chat de clan ---

    /// <summary>/BANIP (VB6 HandleBanIP, Protocol.bas:9755): banea una IP. Dos formas en el MISMO paquete, según
    /// el Boolean que lo abre: por IP literal (cuatro Bytes) o por nick de alguien CONECTADO, del que se saca su
    /// IP; si el nick no está online no hay IP que banear y sale <c>LocaleMsg(75)</c>. Gate: Admin o Dios.
    ///
    /// La parte importante es que no se queda en la lista: además de agregar la IP, <b>banea a cada personaje
    /// conectado desde ella</b> (el <c>For loopc = 1 To LastUser</c> del original llamando a BanCharacter), así
    /// que el ban por IP arrastra las cuentas que estaban usándola. Y avisa a los GMs, no a todo el mundo.
    /// El motivo se lee del wire y el original lo IGNORA (sólo lo usa BanCharacter para su propia pena).</summary>
    private void GmBanIp(ClientSession session, bool porIp, string ipONick, string motivo)
    {
        var p = session.Player;
        if (p is null || (p.Privileges & (PrivAdmin | PrivDios)) == 0) return;   // gate silencioso
        if (_admin is null) return;
        string ip = porIp ? ConnectionGuard.NormalizeIp(ipONick) : "";
        if (!porIp)
        {
            var t = _world.PlayerByName(ipONick.Replace('+', ' ').Trim());
            if (t is null) { session.Send(ServerMessages.LocaleMsg(75)); return; }   // no online → no hay IP
            ip = IpOfPlayer(t);
        }
        if (string.IsNullOrWhiteSpace(ip)) { session.Send(ServerMessages.LocaleMsg(75)); return; }
        if (_admin.IsIpBanned(ip))
        { session.Send(ServerMessages.ConsoleMsg($"La IP {ip} ya se encuentra en la lista de bans.", 3)); return; }

        _admin.BanIp(ip);
        _admin.PersistBannedIps?.Invoke();
        BroadcastToAdmins(ServerMessages.ConsoleMsg($"{p.Name} baneó la IP {ip}", 1));   // ToADMINS, FONTTYPE_FIGHT
        foreach (var otro in _world.Players.Where(pl => IpOfPlayer(pl) == ip).ToArray())
        {
            if (otro.IsGm) continue;   // BanCharacter tampoco banea staff (LocaleMsg 484 en el port)
            otro.Banned = true;
            otro.Penalties.Add($"{p.Name.ToLowerInvariant()}: BAN POR IP {ip} {DateTime.Now:dd/MM/yyyy HH:mm:ss}");
            _db.SaveCharacter(otro);
            otro.Connection?.Close($"baneado por IP ({ip})");
        }
        Console.WriteLine($"[{session.RemoteEndpoint}] /BANIP {p.Name} -> {ip} (motivo: {motivo})");
    }

    /// <summary>/BANIPLIST (VB6 HandleBannedIPList, Protocol.bas:9562): un ÚNICO ConsoleMsg con las IPs baneadas
    /// separadas por ", " (font INFO). Con la lista vacía el original manda el ConsoleMsg <b>igual, con la cadena
    /// vacía</b> —no calla ni avisa "no hay ninguna"—, así que acá también. Gate: Admin o Dios.</summary>
    private void GmBannedIpList(ClientSession session)
    {
        var p = session.Player;
        if (p is null || (p.Privileges & (PrivUser | PrivConsejero | PrivSemiDios | PrivRoleMaster)) != 0) return;
        session.Send(ServerMessages.ConsoleMsg(string.Join(", ", _admin?.BannedIpList() ?? Array.Empty<string>()), 3));
    }

    /// <summary>/UNBANIP &lt;a.b.c.d&gt; (VB6 HandleUnbanIP, Protocol.bas:9826): saca la IP de la lista. La IP llega
    /// como CUATRO BYTES sueltos, no como string. Contesta si la quitó o si no estaba. Gate: Admin o Dios.
    /// A los personajes que ya quedaron baneados por esa IP <b>no los desbanea</b> — igual que el original, que
    /// sólo toca la lista de IPs.</summary>
    private void GmUnbanIp(ClientSession session, string ip)
    {
        var p = session.Player;
        if (p is null || (p.Privileges & (PrivUser | PrivConsejero | PrivSemiDios | PrivRoleMaster)) != 0) return;
        bool quitada = _admin?.UnbanIp(ip) == true;
        if (quitada) _admin?.PersistBannedIps?.Invoke();
        session.Send(ServerMessages.ConsoleMsg(quitada
            ? $"La IP \"{ip}\" se ha quitado de la lista de bans."
            : $"La IP \"{ip}\" NO se encuentra en la lista de bans.", 3));
        Console.WriteLine($"[{session.RemoteEndpoint}] /UNBANIP {p.Name} -> {ip} (quitada={quitada})");
    }

    /// <summary>/SHOWCMSG &lt;clan&gt; (VB6 HandleShowGuildMessages → modGuilds.GMEscuchaClan, modGuilds.bas:1328):
    /// engancha al GM al chat privado de un clan al que NO pertenece. Es un comando con estado: el GM escucha un
    /// solo clan a la vez (<c>UserList().EscucheClan</c>), y mandarlo con el nombre VACÍO lo desengancha.
    /// Los cuatro mensajes salen en FONTTYPE_GUILD (7):
    /// <list type="bullet">
    /// <item>sin nombre y escuchando algo → "Dejas de escuchar a : X"</item>
    /// <item>el mismo clan que ya escuchaba → "Conectado a : X" (idempotente, no se re-engancha)</item>
    /// <item>otro clan → primero "Dejas de escuchar a : {anterior}" y después "Conectado a : X"</item>
    /// <item>clan inexistente → "Error, el clan no existe."</item>
    /// </list>
    /// Gate: Admin o Dios y NO RoleMaster. El efecto real lo hace <c>HandleGuildMessage</c>, que ahora también
    /// le manda el GuildChat a los oyentes además de a los miembros.</summary>
    private void GmShowGuildMessages(ClientSession session, string clan)
    {
        var p = session.Player;
        if (p is null || (p.Privileges & PrivRoleMaster) != 0
            || (p.Privileges & (PrivAdmin | PrivDios)) == 0) return;   // gate silencioso
        string nombre = clan.Replace('+', ' ').Trim();
        if (nombre.Length == 0)
        {
            if (p.EscucheClan.Length == 0) return;   // VB6: sin nombre y sin nada que escuchar, no pasa nada
            session.Send(ServerMessages.ConsoleMsg($"Dejas de escuchar a : {p.EscucheClan}", 7));
            p.EscucheClan = "";
            return;
        }
        var g = _world.GuildByName(nombre);
        if (g is null) { session.Send(ServerMessages.ConsoleMsg("Error, el clan no existe.", 7)); return; }
        if (string.Equals(p.EscucheClan, g.Name, StringComparison.OrdinalIgnoreCase))
        { session.Send(ServerMessages.ConsoleMsg($"Conectado a : {nombre}", 7)); return; }
        if (p.EscucheClan.Length > 0)
            session.Send(ServerMessages.ConsoleMsg($"Dejas de escuchar a : {p.EscucheClan}", 7));
        p.EscucheClan = g.Name;
        session.Send(ServerMessages.ConsoleMsg($"Conectado a : {nombre}", 7));
        Console.WriteLine($"[{session.RemoteEndpoint}] /SHOWCMSG {p.Name} -> <{g.Name}>");
    }

    /// <summary>Nombres de los jugadores conectados desde esa IP (VB6: el loop <c>For loopc = 1 To LastUser</c>).</summary>
    private string PlayersFromIp(string ip) =>
        string.Join(", ", _world.Players.Where(pl => IpOfPlayer(pl) == ip).Select(pl => pl.Name));

    /// <summary>
    /// /EJECUTAR (VB6 HandleExecute, Protocol.bas:8557): mata al instante a un jugador ONLINE. Si el objetivo
    /// no está conectado, LocaleMsg(75). Al ejecutar corre UserDie (mismo cadáver que cualquier muerte) y
    /// difunde LocaleMsg(458) a TODOS con args "{victima}%{gm}" — un anuncio global. El original lo manda con
    /// Modo=5, que NINGÚN cliente renderiza (no hay case para ese modo), pero se emite igual; acá va como
    /// LocaleMsg normal para que el cliente web sí lo muestre.
    /// </summary>
    private void GmExecute(ClientSession session, string nick)
    {
        var p = session.Player;
        if (!RequireGm(session, p)) return;
        var t = _world.PlayerByName(nick.Replace('+', ' ').Trim());
        if (t is null || !t.IsAlive) { session.Send(ServerMessages.LocaleMsg(75)); return; }   // OnlineCheck falla → "Usuario offline"
        KillPlayer(_world, _data, t);
        _world.BroadcastToObservers(t.Pos, ServerMessages.CharacterChange(t));   // apariencia de cadáver al área
        _world.Broadcast(ServerMessages.LocaleMsg(458, $"{t.Name}%{p!.Name}"));  // anuncio global (VB6 ToAll)
        Console.WriteLine($"[{session.RemoteEndpoint}] /EJECUTAR {p.Name} -> {t.Name}");
    }

    /// <summary>
    /// /NICK2IP (VB6 HandleNickToIP, Protocol.bas:8899): la IP de un nick conectado + todos los personajes que
    /// comparten esa IP (para detectar multi-cuentas). Dos ConsoleMsg de FontIndex 3. Si el nick no está
    /// online, LocaleMsg(80).
    /// </summary>
    private void GmNickToIp(ClientSession session, string nick)
    {
        var p = session.Player;
        if (!IsSemiDiosOrHigher(p)) return;   // VB6 Protocol.bas:8899: Admin/Dios/SemiDios, NO Consejero ni RoleMaster
        var t = _world.PlayerByName(nick.Replace('+', ' ').Trim());
        if (t is null) { session.Send(ServerMessages.LocaleMsg(80)); return; }
        string ip = IpOfPlayer(t);
        session.Send(ServerMessages.ConsoleMsg($"El ip de {t.Name} es {ip}", 3));
        session.Send(ServerMessages.ConsoleMsg($"Los personajes con ip {ip} son: {PlayersFromIp(ip)}", 3));
        Console.WriteLine($"[{session.RemoteEndpoint}] /NICK2IP {p!.Name} -> {t.Name} ({ip})");
    }

    /// <summary>/IP2NICK (VB6 HandleIPToNick, Protocol.bas:8999): el reverso — todos los personajes conectados
    /// desde una IP dada. La IP llega como 4 bytes (a.b.c.d). Un ConsoleMsg de FontIndex 3.</summary>
    private void GmIpToNick(ClientSession session, string ip)
    {
        var p = session.Player;
        if (!IsSemiDiosOrHigher(p)) return;   // VB6 Protocol.bas:9001: Admin/Dios/SemiDios, NO Consejero ni RoleMaster
        session.Send(ServerMessages.ConsoleMsg($"Los personajes con ip {ip} son: {PlayersFromIp(ip)}", 3));
        Console.WriteLine($"[{session.RemoteEndpoint}] /IP2NICK {p!.Name} -> {ip}");
    }

    /// <summary>
    /// /ACC y /RACC (VB6 HandleCreateNPC / HandleCreateNPCWithRespawn, Protocol.bas:10964/11003 → SpawnNpc).
    /// Spawnea un NPC del NPCs.dat en el tile que el GM tiene enfrente. Los dos comandos hacen lo mismo: en
    /// el original el flag `respawn` es código muerto, así que el NPC siempre queda registrado para respawn.
    ///
    /// Sobre el gate: el original usa una máscara distinta a la de /INV (bloquea SemiDios, permite RoleMaster),
    /// pero esa asimetría es inalcanzable en este install —sólo el tier Dios existe, y Dios pasa TODAS las
    /// máscaras (ver fase5/GAPS-CODIGO-MUERTO.md)—, así que se usa el mismo RequireGm que el resto del toolset.
    /// </summary>
    private void GmCreateNpc(ClientSession session, short npcIndex)
    {
        var p = session.Player;
        if (!RequireGm(session, p)) return;
        var template = _data.Npc(npcIndex);
        if (npcIndex < 1 || template is null)
        { session.Send(ServerMessages.ConsoleMsg($"No existe el NPC {npcIndex}.", FuenteInfo)); return; }   // VB6: SpawnNpc devuelve 0 y calla; acá se avisa por consola
        if (_npcs is null) { session.Send(ServerMessages.ConsoleMsg("Spawn de NPCs no disponible.", FuenteInfo)); return; }

        var (tx, ty) = Step(p!.Pos.X, p.Pos.Y, p.Heading);
        var pos = new WorldPos(p.Pos.Map, tx, ty);
        int charIndex = _npcs.GmSpawn(template, pos);
        if (charIndex == 0) { session.Send(ServerMessages.ConsoleMsg("No se pudo invocar la criatura acá.", FuenteInfo)); return; }
        session.Send(ServerMessages.ConsoleMsg($"Invocaste a {template.Name}.", FuenteInfo));
        Console.WriteLine($"[{session.RemoteEndpoint}] /ACC {p.Name} -> NPC {npcIndex} ({template.Name})");
    }

    private enum GmInspectKind { Inventory, Bank, Skills }

    /// <summary>
    /// Nombres de las habilidades tal como los imprime /SKILLS (VB6 <c>SkillsNames</c>, General.bas:1746).
    /// El índice es el del enum eSkill, así que el ORDEN es el contrato. OJO: el 3 se imprime
    /// "Artes marciales", no "Wrestling" — el nombre interno y el visible no coinciden.
    /// </summary>
    private static readonly string[] SkillNames =
    {
        "", "Tacticas de combate", "Combate con armas", "Artes marciales", "Apuñalar", "Armas arrojadizas",
        "Armas de proyectiles", "Defensa con escudos", "Magia", "Resistencia magica", "Meditar", "Ocultarse",
        "Domar animales", "Suerte", "Robar", "Comercio", "Supervivencia", "Liderazgo", "Pesca", "Mineria",
        "Talar arboles", "Botanica", "Herreria", "Carpinteria", "Alquimia", "Sastreria", "Navegacion",
        "Equitacion",
    };

    /// <summary>
    /// /INV, /BOV y /SKILLS (VB6 HandleRequestCharInventory/Bank/Skills, Protocol.bas:8078/8143/8208 +
    /// SendUserInvTxt Modulo_UsUaRiOs.bas:982 y SendUserBovedaTxt modBanco.bas:343). Son la herramienta de
    /// auditoría del GM: sin esto no se puede revisar un dupe ni resolver una disputa por un ítem.
    ///
    /// Los tres responden SÓLO con ConsoleMsg de FontIndex 3, y si el que pregunta no es GM el original
    /// no contesta NADA — ni un error. Se respeta ese silencio: es lo que evita que un jugador cualquiera
    /// use el comando para sondear si un personaje existe.
    ///
    /// Cubre el personaje OFFLINE igual que el original (que lee el .chr crudo con GetVar): acá se carga del
    /// store con <c>LoadCharacter</c>, que es la misma foto. El comentario que decía "queda pendiente" quedó
    /// viejo cuando se agregó esa carga — la deuda ya no existe.
    /// </summary>
    private void GmInspect(ClientSession session, string nick, GmInspectKind kind)
    {
        var p = session.Player;
        if (!IsSemiDiosOrHigher(p)) return;   // VB6 :8078/8143/8208: los tres piden Admin/Dios/SemiDios,
                                              // no un GM cualquiera — un Consejero no le ve la bóveda a nadie
        const byte Font = FuenteInfo;   // los tres helpers del original contestan con FONTTYPE_INFO

        nick = nick.Replace('+', ' ').Trim();
        // VB6 HandleEditChar/RequestCharInfo/SKILLS (Protocol.bas:7538 "todos los comandos se pueden usar aunque el
        // pj este offline"): la inspección GM funciona sobre un personaje OFFLINE — se lee su .chr del store (mismo
        // patrón que GmBan). Como /INV/BOV/SKILLS son de sólo lectura, el Player cargado sirve igual que el online.
        var t = _world.PlayerByName(nick) ?? (_db.CharacterExists(nick) ? _db.LoadCharacter(nick, 0, "") : null);
        if (t is null) { session.Send(ServerMessages.ConsoleMsg($"Usuario inexistente: {nick}", Font)); return; }

        session.Send(ServerMessages.ConsoleMsg(t.Name, Font));
        switch (kind)
        {
            case GmInspectKind.Skills:
                for (int i = 1; i <= GameLimits.NumSkills; i++)
                    session.Send(ServerMessages.ConsoleMsg($"{SkillNames[i]} = {t.Skills[i]}", Font));
                break;

            default:
            {
                bool bank = kind == GmInspectKind.Bank;
                int count = 0;
                var lines = new List<string>();
                for (int slot = 1; slot <= GameLimits.MaxInventorySlots; slot++)
                {
                    var inv = bank ? t.BankInventory[slot] : t.Inventory[slot];
                    if (inv.ObjIndex <= 0 || inv.Amount <= 0) continue;
                    count++;
                    var name = _data.Obj(inv.ObjIndex)?.Name ?? $"obj {inv.ObjIndex}";
                    lines.Add($" Objeto {slot} {name} Cantidad:{inv.Amount}");
                }
                session.Send(ServerMessages.ConsoleMsg($"Tiene {count} objetos.", Font));
                foreach (var line in lines) session.Send(ServerMessages.ConsoleMsg(line, Font));
                break;
            }
        }
        Console.WriteLine($"[{session.RemoteEndpoint}] /{kind.ToString().ToUpperInvariant()} {p!.Name} -> {t.Name}");
    }

    private void GmJail(ClientSession session, string nick, string motivo, byte minutos)
    {
        var p = session.Player;
        if (!RequireGm(session, p)) return;
        if (string.IsNullOrWhiteSpace(nick) || string.IsNullOrWhiteSpace(motivo)) { session.Send(ServerMessages.LocaleMsg(411)); return; }
        var t = _world.PlayerByName(nick.Replace('+', ' '));
        if (t is null) { session.Send(ServerMessages.LocaleMsg(75)); return; }
        if (t.IsGm) { session.Send(ServerMessages.LocaleMsg(298)); return; }
        if (minutos > 60) { session.Send(ServerMessages.LocaleMsg(412)); return; }
        if (minutos < 1) minutos = 1;
        // Registro de la sanción ANTES de encarcelar, como el original (Protocol.bas:7355-7359). El formato es
        // literal —GM y motivo en minúsculas— porque es lo que después se lee para auditar una disputa.
        t.Penalties.Add($"{p!.Name.ToLowerInvariant()}: CARCEL {minutos}m, MOTIVO: {motivo.ToLowerInvariant()} " +
                        $"{DateTime.Now:dd/MM/yyyy HH:mm:ss}");
        _db.SaveCharacter(t);   // el VB6 escribe el .chr en el acto: una sanción no se pierde si el server cae

        t.JailMinutesLeft = minutos;
        WarpOtroJugador(t, Cities.Prision);   // VB6 Encarcelar (Admin.bas:175) llama WarpUserChar
        t.Connection?.Send(ServerMessages.LocaleMsg(145, p!.Name));   // "#1 te ha encarcelado"
        session.Send(ServerMessages.ConsoleMsg($"Encarcelaste a {t.Name} por {minutos} minuto(s): {motivo}.", FuenteInfo));
        Console.WriteLine($"[{session.RemoteEndpoint}] /CARCEL {p!.Name} -> {t.Name} ({minutos}m, motivo: {motivo})");
    }

    /// <summary>
    /// /CI &lt;objIndex&gt; &lt;cantidad&gt; — CreateItem (VB6 HandleCreateItem, Protocol.bas:9865): le mete el ítem
    /// al inventario del propio GM. El original valida que el índice exista en ObjData y que tenga nombre
    /// (los huecos del .dat), y avisa con LocaleMsg 446 ("Has creado #1 (#2)"). Si no entra en el inventario
    /// no dice nada, igual que el VB6 (MeterItemEnInventario devuelve False y el mensaje queda adentro del If).
    /// GATE ESTRICTO POR TIER: el original pide el bit <b>Dios</b> (<c>If .flags.Privilegios And
    /// PlayerType.Dios</c>, Protocol.bas:9885) y calla si no lo tenés — no el genérico "es GM".
    ///
    /// <para>Acá decía "alcanza con GM porque el slice todavía no modela los tiers", y esa nota envejeció:
    /// el <c>Server.ini</c> de este port SÍ tiene las cinco listas, así que un host que poblara
    /// <c>SemiDioses</c> o <c>Consejeros</c> le daba a un tier medio la capacidad de crear CUALQUIER objeto
    /// de la nada. En el install del original eso no se notaba porque sólo la lista de Dios está poblada
    /// (ver GAPS-CODIGO-MUERTO.md §1), pero el port no hereda esa limitación de la data. Es el mismo agujero
    /// que la revisión adversarial ya había encontrado en <c>/DONADOR</c> y <c>/DARPUN</c>.</para>
    /// </summary>
    private void GmCreateItem(ClientSession session, short objIndex, short amount)
    {
        var p = session.Player;
        if (!IsDios(p)) return;
        var obj = _data.Obj(objIndex);
        if (objIndex < 1 || obj is null || string.IsNullOrWhiteSpace(obj.Name))
        { session.Send(ServerMessages.ConsoleMsg($"No existe el objeto {objIndex}.", FuenteInfo)); return; }
        int qty = Math.Clamp((int)amount, 1, GameLimits.MaxInventoryObjs);
        int slot = FindSlotFor(p!, objIndex, qty);
        if (slot == 0) { session.Send(ServerMessages.LocaleMsg(25)); return; }   // inventario lleno
        var inv = p!.Inventory[slot];
        inv.ObjIndex = objIndex;
        inv.Amount = (short)Math.Min(GameLimits.MaxInventoryObjs, inv.Amount + qty);
        p.Inventory[slot] = inv;
        session.Send(ServerMessages.ChangeInventorySlot((byte)slot, inv.ObjIndex, inv.Amount, inv.Equipped, 0f, 1));
        session.Send(ServerMessages.LocaleMsg(446, $"{obj.Name}%{qty}"));
        Console.WriteLine($"[{session.RemoteEndpoint}] /CI {p.Name}: {qty}x {obj.Name} ({objIndex}) en slot {slot}");
    }

    private void GmSummon(ClientSession session, string name)
    {
        var p = session.Player;
        if (!IsSemiDiosOrHigher(p)) return;   // VB6 Protocol.bas:8320: Admin/Dios/SemiDios, NO Consejero ni RoleMaster
        var t = _world.PlayerByName(name);
        if (t is null) { session.Send(ServerMessages.LocaleMsg(75)); return; }
        // Y el tercero: traer a alguien de otro mapa sin ChangeMap lo dejaba viendo el mapa del que vino.
        WarpOtroJugador(t, new WorldPos(p!.Pos.Map, p.Pos.X, p.Pos.Y));
        t.Connection?.Send(ServerMessages.ConsoleMsg($"Un GM te ha transportado.", FuenteInfo));
        session.Send(ServerMessages.ConsoleMsg($"Transportaste a {t.Name} junto a ti.", FuenteInfo));
    }

    private void GmRevive(ClientSession session, string name)
    {
        var p = session.Player;
        if (!IsSemiDiosOrHigher(p)) return;   // VB6 Protocol.bas:8290: Admin/Dios/SemiDios, NO Consejero ni RoleMaster
        var t = _world.PlayerByName(name) ?? (string.IsNullOrWhiteSpace(name) ? p : null);
        if (t is null) { session.Send(ServerMessages.LocaleMsg(75)); return; }
        // `RevivePlayer` y no `Hp = MaxHp` a secas: acá se subían los puntos de vida y nada más, así que el
        // resucitado por un GM quedaba vivo pero DIBUJADO COMO CADÁVER para todos —incluido él— y seguía
        // incinerado si lo estaba. El helper restaura la cabeza viva, apaga el fuego, refresca la apariencia
        // y la DIFUNDE, que es la mitad que faltaba. Es el mismo que usa el sacerdote.
        RevivePlayer(_world, _data, t);
        t.Connection?.Send(ServerMessages.ConsoleMsg("Un GM te ha resucitado.", FuenteInfo));
        session.Send(ServerMessages.ConsoleMsg($"Resucitaste a {t.Name}.", FuenteInfo));
    }

    private void GmKillNpc(ClientSession session)
    {
        var p = session.Player;
        if (!RequireGm(session, p)) return;
        var (tx, ty) = Step(p!.Pos.X, p.Pos.Y, p.Heading);
        var npc = _world.NpcAt(new WorldPos(p.Pos.Map, tx, ty));
        if (npc is null) { session.Send(ServerMessages.ConsoleMsg("No hay una criatura al frente.", FuenteInfo)); return; }
        _world.RemoveNpc(npc.CharIndex);
        _world.BroadcastToObservers(npc.Pos, ServerMessages.CharacterRemove(npc.CharIndex));
        session.Send(ServerMessages.ConsoleMsg($"Eliminaste a {npc.Name}.", FuenteInfo));
    }

    /// <summary>/GMSG (VB6 Protocol.bas:6361): chat privado entre staff. Gate Consejero+ (o sea, no un User raso).
    /// El mensaje vacío no se difunde (el VB6 sólo loguea). Font 20 = FONTTYPE_fonttt.</summary>
    private void GmChat(ClientSession session, string message)
    {
        var p = session.Player;
        if (p is null || p.Privileges <= PrivUser) return;   // gate silencioso, como el original
        if (string.IsNullOrEmpty(message)) return;
        BroadcastToAdmins(ServerMessages.ConsoleMsg($"[Chat GM][{p.Name}] {message}", 20));
        Console.WriteLine($"[{session.RemoteEndpoint}] /GMSG {p.Name}: {message}");
    }

    /// <summary>OnlineGM (VB6 Protocol.bas:8388): lista el staff conectado. Un SemiDios NO ve a los Dioses/Admins;
    /// un Dios/Admin ve a todos. Sin nadie que listar responde LocaleMsg(410).</summary>
    private void GmOnlineGm(ClientSession session)
    {
        var p = session.Player;
        if (!IsSemiDiosOrHigher(p)) return;   // gate silencioso
        int visibles = PrivConsejero | PrivSemiDios;
        if ((p!.Privileges & (PrivDios | PrivAdmin)) != 0) visibles |= PrivDios | PrivAdmin;
        var nombres = _world.Players.Where(o => (o.Privileges & visibles) != 0).Select(o => o.Name).ToList();
        if (nombres.Count == 0) { session.Send(ServerMessages.LocaleMsg(410)); return; }
        session.Send(ServerMessages.ConsoleMsg(string.Join(", ", nombres) + ".", 3));
    }

    /// <summary>/ONLINEMAP (VB6 Protocol.bas:8435): usuarios en un mapa. Un SemiDios no ve a los Dioses/Admins.
    /// El original responde SIEMPRE, aunque la lista quede vacía (no tiene rama de rechazo).</summary>
    private void GmOnlineMap(ClientSession session, short map)
    {
        var p = session.Player;
        if (!IsSemiDiosOrHigher(p)) return;   // gate silencioso (el arg ya se consumió en el dispatch)
        int visibles = PrivUser | PrivConsejero | PrivSemiDios;
        if ((p!.Privileges & (PrivDios | PrivAdmin)) != 0) visibles |= PrivDios | PrivAdmin;
        var nombres = _world.Players.Where(o => o.Pos.Map == map && (o.Privileges & visibles) != 0)
                                    .Select(o => o.Name).ToList();
        session.Send(ServerMessages.ConsoleMsg("Usuarios en el mapa: " + string.Join(", ", nombres), 3));
    }

    /// <summary>/ECHAR (VB6 Protocol.bas:8482): expulsa a un jugador. Offline → LocaleMsg(75); si el objetivo tiene
    /// MÁS rango que quien lo echa → LocaleMsg(298). Al expulsar, el original anuncia a TODO el servidor.</summary>
    private void GmKick(ClientSession session, string nick)
    {
        var p = session.Player;
        if (!IsSemiDiosOrHigher(p)) return;   // gate silencioso
        nick = nick.Replace('+', ' ').Trim();
        var t = _world.PlayerByName(nick);
        if (t is null) { session.Send(ServerMessages.LocaleMsg(75)); return; }          // "Usuario offline"
        if (t.Privileges > p!.Privileges) { session.Send(ServerMessages.LocaleMsg(298)); return; }   // no se echa a un superior
        _world.Broadcast(ServerMessages.ConsoleMsg($"{p.Name} echó a {t.Name}.", 3));   // VB6: FONTTYPE_INFO
        t.Connection?.Close("expulsado por un GM");
        Console.WriteLine($"[{session.RemoteEndpoint}] /ECHAR {p.Name} -> {t.Name}");
    }

    /// <summary>/RMSG (VB6 Protocol.bas:8837): broadcast de staff a todo el servidor, con el formato "Nick&gt; msg".</summary>
    private void GmServerMessage(ClientSession session, string message)
    {
        var p = session.Player;
        if (!IsSemiDiosOrHigher(p)) return;   // gate silencioso
        if (string.IsNullOrEmpty(message)) return;
        _world.Broadcast(ServerMessages.ConsoleMsg($"{p!.Name}> {message}", 0));
        Console.WriteLine($"[{session.RemoteEndpoint}] /RMSG {p.Name}: {message}");
    }

    /// <summary>/DEST (VB6 Protocol.bas:9914): borra el ítem del suelo bajo el GM. Sin ítem → silencio. Un TELEPORT
    /// con salida asociada NO se borra acá (para eso está /DT), también en silencio.</summary>
    private void GmDestroyItem(ClientSession session)
    {
        var p = session.Player;
        if (!IsAltoGm(p)) return;   // Dios/Admin/RoleMaster, gate silencioso
        if (!_world.TryGetGround(p!.Pos, out var item)) return;
        var obj = _data.Obj(item.ObjIndex);
        if (obj is not null && obj.ObjType == 19 && _data.MapExits.ContainsKey((p.Pos.Map, p.Pos.X, p.Pos.Y))) return;  // otTeleport con salida: se borra con /DT
        _world.RemoveGround(p.Pos);
        _world.BroadcastToObservers(p.Pos, ServerMessages.ObjectDelete(p.Pos.X, p.Pos.Y));
        Console.WriteLine($"[{session.RemoteEndpoint}] /DEST {p.Name} en ({p.Pos.Map},{p.Pos.X},{p.Pos.Y})");
    }

    /// <summary>/BLOQ (VB6 Protocol.bas:9949): togglea el bloqueo del tile donde está el GM y avisa a TODO el mapa
    /// con BlockPosition (el original usa SendData(toMap), no sólo al área).</summary>
    private void GmToggleBlocked(ClientSession session)
    {
        var p = session.Player;
        if (!IsAltoGm(p)) return;   // gate silencioso
        bool blocked = !_data.IsBlockedTile(p!.Pos.Map, p.Pos.X, p.Pos.Y);
        _data.SetTileBlocked(p.Pos.Map, p.Pos.X, p.Pos.Y, blocked);
        _world.BroadcastToMap(p.Pos.Map, ServerMessages.BlockPosition(p.Pos.X, p.Pos.Y, blocked));
        Console.WriteLine($"[{session.RemoteEndpoint}] /BLOQ {p.Name} ({p.Pos.X},{p.Pos.Y}) -> {blocked}");
    }

    /// <summary>/MASSKILL (VB6 Protocol.bas:10009): mata todos los NPCs de la ventana de pantalla del GM
    /// (X±8, Y±6 — el tamaño de cámara de General.bas:1781), recortada al mapa. No responde nada al que lo tira:
    /// lo observable son los CharacterRemove de cada criatura.</summary>
    private void GmMassKill(ClientSession session)
    {
        var p = session.Player;
        if (!IsAltoGm(p)) return;   // gate silencioso
        var victimas = _world.NpcsOnMap(p!.Pos.Map)
                             .Where(n => Math.Abs(n.Pos.X - p.Pos.X) <= 8 && Math.Abs(n.Pos.Y - p.Pos.Y) <= 6)
                             .ToList();   // ToList: abajo se remueven, y NpcsOnMap devuelve la lista VIVA del índice
        foreach (var npc in victimas)
        {
            _npcs?.CancelRespawn(npc.CharIndex);   // VB6 QuitarNPC: tampoco respawnean las de /MASSKILL
            _world.RemoveNpc(npc.CharIndex);
            _world.BroadcastToObservers(npc.Pos, ServerMessages.CharacterRemove(npc.CharIndex));
        }
        Console.WriteLine($"[{session.RemoteEndpoint}] /MASSKILL {p!.Name}: {victimas.Count} criaturas");
    }

    /// <summary>/SMSG (VB6 Protocol.bas:11214): cartel modal a TODO el servidor. Gate Dios/Admin. A diferencia de
    /// /RMSG, el original NO chequea que el mensaje esté vacío.</summary>
    private void GmSystemMessage(ClientSession session, string message)
    {
        var p = session.Player;
        if (p is null || (p.Privileges & (PrivDios | PrivAdmin)) == 0) return;   // gate silencioso
        _world.Broadcast(ServerMessages.ShowMessageBox(message));
        Console.WriteLine($"[{session.RemoteEndpoint}] /SMSG {p.Name}: {message}");
    }

    /// <summary>/MASSDEST (VB6 Protocol.bas:9378): borra los ítems del suelo de la ventana de pantalla del GM,
    /// SALVO el decorado del mapa (puertas, carteles, árboles, yacimientos, teleports, muebles, fragua, yunque —
    /// VB6 ItemNoEsDeMapa). Cada borrado emite su ObjectDelete al área.</summary>
    private void GmMassDestroy(ClientSession session)
    {
        var p = session.Player;
        if (!IsAltoGm(p)) return;   // gate silencioso
        // GroundOnMap (no GroundInView): el VB6 NO distingue objetos "fijos" del mapa — su único filtro es
        // ItemNoEsDeMapa, por TIPO de objeto. Con GroundInView, que descarta _groundFijo, el comando no borraba
        // ni uno solo de los ~3.400 objetos colocados por el .csm que el original sí barre (barcas, sacos de oro,
        // comida...) y quedaba reducido a limpiar los drops de la sesión.
        var borrables = _world.GroundOnMap(p!.Pos.Map)
            .Where(g => Math.Abs(g.Pos.X - p.Pos.X) <= 8 && Math.Abs(g.Pos.Y - p.Pos.Y) <= 6
                     && (_data.Obj(g.ObjIndex)?.IsNotMapFurniture ?? false))
            .Select(g => g.Pos).ToList();
        foreach (var pos in borrables)
        {
            _world.RemoveGround(pos);
            _world.BroadcastToObservers(pos, ServerMessages.ObjectDelete(pos.X, pos.Y));
        }
        Console.WriteLine($"[{session.RemoteEndpoint}] /MASSDEST {p.Name}: {borrables.Count} objetos");
    }

    /// <summary>/MATA (VB6 Protocol.bas:9982): elimina la criatura SIN respawn. Opera sobre el NPC CLICKEADO
    /// (<c>flags.TargetNPC</c>) — antes acá se usaba el encarado, que era la aproximación de cuando el slice no
    /// modelaba el target de click. A diferencia de /RMATA, el VB6 no emite NINGÚN mensaje: lo observable es el
    /// CharacterRemove.</summary>
    private void GmKillNpcNoRespawn(ClientSession session)
    {
        var p = session.Player;
        if (!IsAltoGm(p)) return;   // gate silencioso
        var npc = TargetNpcDe(p!);
        if (npc is null) return;   // el VB6 sale en silencio si no hay target
        _npcs?.CancelRespawn(npc.CharIndex);   // "sin respawn" de verdad: si no, reaparecía a los 5 s
        _world.RemoveNpc(npc.CharIndex);
        _world.BroadcastToObservers(npc.Pos, ServerMessages.CharacterRemove(npc.CharIndex));
        Console.WriteLine($"[{session.RemoteEndpoint}] /MATA {p!.Name} -> {npc.Name}");
    }

    /// <summary>/IRCERCA (VB6 Protocol.bas:6450): teleporta al GM a un tile libre junto al jugador. Offline →
    /// LocaleMsg(75); sin ningún lugar libre en radio 2..5 → LocaleMsg(403). Un GM que no sea Dios/Admin no puede
    /// caerle encima a un Dios/Admin (rebota en silencio).</summary>
    private void GmGoNearby(ClientSession session, string nick)
    {
        var p = session.Player;
        if (p is null || (p.Privileges & (PrivConsejero | PrivSemiDios | PrivDios | PrivAdmin)) == 0) return;
        var t = _world.PlayerByName(nick.Replace('+', ' ').Trim());
        if (t is null) { session.Send(ServerMessages.LocaleMsg(75)); return; }
        if ((t.Privileges & (PrivDios | PrivAdmin)) != 0 && (p.Privileges & (PrivDios | PrivAdmin)) == 0) return;  // no se persigue a un superior
        // Barrido del original: radios crecientes 2..5, cuadrado completo, Y interno / X externo.
        for (int i = 2; i <= 5; i++)
            for (int x = t.Pos.X - i; x <= t.Pos.X + i; x++)
                for (int y = t.Pos.Y - i; y <= t.Pos.Y + i; y++)
                {
                    if (x < GameLimits.MinXBorder || x > GameLimits.MaxXBorder
                        || y < GameLimits.MinYBorder || y > GameLimits.MaxYBorder) continue;
                    var dest = new WorldPos(t.Pos.Map, (byte)x, (byte)y);
                    if (_world.PlayerAt(dest) is not null || _world.NpcAt(dest) is not null) continue;
                    // El VB6 llama LegalPos(..., PuedeAgua:=True, ...): acepta agua, así que NO se filtra por IsWater.
                    if (_data.IsBlockedTile(dest.Map, dest.X, dest.Y)) continue;
                    TeleportTo(session, p, dest);
                    // WarpUserChar(..., FX:=True): el original anuncia el salto al área con sonido + efecto,
                    // salvo que el GM esté en invisibilidad permanente (AdminInvisible).
                    if (!(p.Invisible && p.InvisibleUntilTick == 0))
                    {
                        _world.BroadcastToObservers(dest, ServerMessages.PlayWave(3, dest.X, dest.Y));      // SND_WARP
                        _world.BroadcastToObservers(dest, ServerMessages.CreateFX(p.CharIndex, 1, 0));      // FXWARP
                    }
                    Console.WriteLine($"[{session.RemoteEndpoint}] /IRCERCA {p.Name} -> {t.Name}");
                    return;
                }
        session.Send(ServerMessages.LocaleMsg(403));   // "¡Todos los lugares están ocupados!"
    }

    // --- Tanda 7: eventos, apagado, disolver clan ---

    /// <summary>/ORO y /EXP (VB6 handleEventoOro :16151 / handleEventoExperiencia :16192): TOGGLE de evento.
    /// Payload <c>Byte(multiplicador) + Byte(minutos)</c>. Al prender broadcastea "¡El evento 'X xN' ha dado
    /// comienzo! Duración: M minutos." y al apagar "¡El evento 'X xN' ha finalizado!" — ojo que el de apagado
    /// imprime <c>Temporal</c>, o sea el multiplicador que había AL ENTRAR, no el nuevo. Font INFO (3).
    ///
    /// Tres rarezas del original que se portan tal cual:
    /// <list type="number">
    /// <item>El multiplicador que manda el cliente <b>se ignora</b> salvo que el GM se llame "Shermie" o
    ///   "Mermas" (los dueños de ese server); para cualquier otro se fuerza a <b>2</b>. Está cableado en la
    ///   fuente, no en un .dat, así que es comportamiento y no data: acá se replica con los mismos dos nombres.</item>
    /// <item>El VB6 <b>no tiene timer</b> que apague el evento: los "N minutos" son TEXTO del anuncio y el
    ///   evento se apaga con un segundo /ORO o /EXP. Igual acá — un timer sería inventarlo.</item>
    /// <item>El original escribe el multiplicador <b>antes</b> del gate de privilegios, así que un no-Admin que
    ///   mande el paquete deja <c>multiplicadorExp = 2</c> seteado. Es inerte: el sitio que lo lee
    ///   (SistemaCombate.bas:2025) lo gatea con <c>ExpModificada</c>, que sólo se prende dentro del gate. Acá
    ///   la palanca es un solo campo (el multiplicador ES el flag), así que el gate va primero y no hay fuga.</item>
    /// </list>
    ///
    /// El de EXPERIENCIA tiene efecto real y el mecanismo ya estaba enganchado en el sitio equivalente exacto:
    /// <c>AdminConfig.ExpMult</c> multiplica la exp por matar un NPC, igual que <c>ExpaDar * multiplicadorExp</c>
    /// dentro de <c>CalcularDarExp</c> (la exp por frag PvP no se toca en ninguno de los dos).
    ///
    /// <b>Divergencia deliberada en /ORO</b>: en el VB6 <c>multiplicadorOro</c> es una <b>escritura muerta</b> —
    /// se declara (Declares.bas:50), se setea acá y <b>nadie lo lee jamás</b>, así que el evento de oro anuncia
    /// algo que no pasa. El port lo engancha a <c>AdminConfig.GoldMult</c>, que ya multiplica el oro que suelta
    /// la criatura: es el mismo criterio que se viene usando cuando el algoritmo está escrito y un bug lo deja
    /// sin efecto (ver fase5/GAPS-CODIGO-MUERTO.md §4k).</summary>
    private void GmEvento(ClientSession session, bool oro, byte multi, byte minutos)
    {
        var p = session.Player;
        if (p is null || (p.Privileges & (PrivAdmin | PrivDios)) == 0) return;   // gate silencioso
        if (_admin is null) return;
        // VB6: `If Name = "Shermie" Or Name = "Mermas" Then mult = <lo que mandó> Else mult = 2`.
        int pedido = p.Name is "Shermie" or "Mermas" ? Math.Max(1, (int)multi) : 2;
        int previo = oro ? _admin.GoldMult : _admin.ExpMult;   // el `Temporal` del original
        string nombre = oro ? "Oro" : "Experiencia";
        if (previo > 1)   // había evento en curso: este uso lo APAGA (y el texto usa el multiplicador VIEJO)
        {
            if (oro) _admin.GoldMult = 1; else _admin.ExpMult = 1;
            _world.Broadcast(ServerMessages.ConsoleMsg($"¡El evento '{nombre} x{previo}' ha finalizado!", 3));
        }
        else
        {
            if (oro) _admin.GoldMult = pedido; else _admin.ExpMult = pedido;
            _world.Broadcast(ServerMessages.ConsoleMsg(
                $"¡El evento '{nombre} x{pedido}' ha dado comienzo! Duración: {minutos} minutos.", 3));
        }
        Console.WriteLine($"[{session.RemoteEndpoint}] /{(oro ? "ORO" : "EXP")} {p.Name}: x{(oro ? _admin.GoldMult : _admin.ExpMult)}");
    }

    /// <summary>/APAGAR (VB6 HandleTurnOffServer, Protocol.bas:11106): avisa a TODOS con un ConsoleMsg en fuente
    /// de pelea (FONTTYPE_FIGHT = 1) y hace <c>Unload frmMain</c>. Gate: <c>Privilegios And (User Or Consejero Or
    /// SemiDios Or RoleMaster)</c> → Exit Sub, o sea sólo Admin o Dios, con rebote silencioso.
    ///
    /// Reusa el camino que ya tenía el port para el botón del panel de admin (AdminServer.cs, case "shutdown"):
    /// persiste todos los personajes y dispara el <c>cts.Cancel()</c>, con un delay corto para que el aviso
    /// alcance a salir por el socket antes de que se cierre el listener.</summary>
    private void GmTurnOffServer(ClientSession session)
    {
        var p = session.Player;
        if (p is null || (p.Privileges & (PrivUser | PrivConsejero | PrivSemiDios | PrivRoleMaster)) != 0) return;
        _world.Broadcast(ServerMessages.ConsoleMsg($"¡¡¡{p.Name} VA A APAGAR EL SERVIDOR!!!", 1));
        Console.WriteLine($"[{session.RemoteEndpoint}] /APAGAR {p.Name}");
        if (_shutdown is null) return;   // en tests no hay a quién apagar: el aviso ya salió
        foreach (var otro in _world.Players.ToArray()) _db.SaveCharacter(otro);
        var apagar = _shutdown;
        _ = Task.Run(async () => { await Task.Delay(800).ConfigureAwait(false); apagar(true); });
    }

    /// <summary>/BANCLAN &lt;clan&gt; (VB6 HandleGuildBan, Protocol.bas:9624): banea a TODOS los miembros del clan,
    /// online y offline. Gate: Admin o Dios y NO RoleMaster. Emisión fiel:
    /// <list type="bullet">
    /// <item>Clan inexistente → <c>ConsoleMsg</c> "No existe el clan: X" SÓLO al que lo pidió (font INFO).</item>
    /// <item>Si existe → un broadcast "{GM} baneó al clan {CLAN EN MAYÚSCULAS}" y después <b>un broadcast por
    ///   miembro</b>: <c>"   {miembro}&lt;{clan}&gt; ha sido expulsado del servidor."</c> (tres espacios de sangría
    ///   en el original), todos en FONTTYPE_FIGHT (1). Al que lo pidió no se le contesta nada aparte.</item>
    /// </list>
    /// <b>El clan NO se disuelve</b> y a nadie se le borra la membresía: el original sólo pone el flag de ban
    /// (más la pena en el .chr) y cierra el socket de los que están online. Queda un clan de baneados, que es
    /// exactamente lo que hace la fuente — no hay ningún <c>m_BorrarClan</c> en este handler.</summary>
    private void GmGuildBan(ClientSession session, string clan)
    {
        var p = session.Player;
        if (p is null || (p.Privileges & PrivRoleMaster) != 0
            || (p.Privileges & (PrivAdmin | PrivDios)) == 0) return;   // gate silencioso
        string nombre = clan.Replace("\\", "").Replace('+', ' ').Trim();
        var g = _world.GuildByName(nombre);
        if (g is null) { session.Send(ServerMessages.ConsoleMsg($"No existe el clan: {nombre}", 3)); return; }
        _world.Broadcast(ServerMessages.ConsoleMsg($"{p.Name} baneó al clan {g.Name.ToUpperInvariant()}", 1));
        foreach (var miembro in g.Members.ToArray())
        {
            _world.Broadcast(ServerMessages.ConsoleMsg($"   {miembro}<{g.Name}> ha sido expulsado del servidor.", 1));
            var online = _world.PlayerByName(miembro);
            if (online is not null)
            {
                online.Banned = true;
                _db.SaveCharacter(online);
                online.Connection?.Close($"clan <{g.Name}> baneado por un GM");
            }
            else if (_db.CharacterExists(miembro) && _db.LoadCharacter(miembro, 0, "") is { } off)
            {
                off.Banned = true;
                _db.SaveCharacter(off);
            }
        }
        Console.WriteLine($"[{session.RemoteEndpoint}] /BANCLAN {p.Name} -> <{g.Name}>, {g.Members.Count} miembros");
    }

    // --- Tanda 6: apariencia del GM, clan por nombre, des-aturdir ---

    /// <summary>/SHOWNAME (VB6 HandleShowName, Protocol.bas:6423): el GM conmuta su propio <c>showName</c> y
    /// <c>RefreshCharStatus</c> re-broadcastea <c>UpdateTagAndStatus</c> — con el flag en false manda nick VACÍO,
    /// status 0 y donador 0 (Modulo_UsUaRiOs.bas:222-227). El flag lo leen además <c>MakeUserChar</c> (el nick del
    /// CharacterCreate) y <c>LookatTile</c> (que NO emite CharMsgStatus sobre alguien con showName=false), y
    /// <c>DoAdminInvisible</c> lo baja/sube junto con la invisibilidad (Trabajo.bas:1208/1217).
    /// Gate: Dios/Admin/RoleMaster — el cuerpo entero vive dentro del `If`, así que el resto rebota en silencio.</summary>
    private void GmShowName(ClientSession session)
    {
        var p = session.Player;
        if (!IsAltoGm(p)) return;   // gate silencioso
        p!.ShowName = !p.ShowName;
        _world.BroadcastToObservers(p.Pos, ServerMessages.UpdateTagAndStatus(p));
        Console.WriteLine($"[{session.RemoteEndpoint}] /SHOWNAME {p.Name}: {(p.ShowName ? "visible" : "oculto")}");
    }

    /// <summary>/NAVE (VB6 HandleNavigateToggle, Protocol.bas:11042): conmuta el <c>Navegando</c> del propio GM
    /// —para cruzar agua sin barco— y le manda <c>NavigateToggle</c>. Gate: excluye usuario común y Consejero.
    /// OJO: no toca el <c>BoatSlot</c>, así que es navegar SIN barco; el gate de agua del Walk mira el flag.</summary>
    private void GmNavigateToggle(ClientSession session)
    {
        var p = session.Player;
        if (p is null || (p.Privileges & (PrivUser | PrivConsejero)) != 0) return;   // gate silencioso
        p.Navigating = !p.Navigating;
        session.Send(ServerMessages.NavigateToggle());
        Console.WriteLine($"[{session.RemoteEndpoint}] /NAVE {p.Name}: {(p.Navigating ? "ON" : "OFF")}");
    }

    /// <summary>/NOESTUPIDO &lt;nick&gt; (VB6 HandleMakeDumbNoMore, Protocol.bas:9422): le manda
    /// <c>DumbNoMore</c> al objetivo para apagarle el overlay de aturdimiento; offline → LocaleMsg(75) al GM.
    ///
    /// ALCANCE REAL, que sorprende: en este fork el handler <b>no limpia</b> <c>flags.Estupidez</c> del target
    /// (grep de Estupidez no da un solo hit en Protocol.bas — el flag sólo lo pone modHechizos.bas:1249/1279 y lo
    /// expira el timer de General.bas:965). O sea que el comando es COSMÉTICO: apaga el overlay del cliente y el
    /// servidor sigue considerándolo aturdido. Se porta así, con esa limitación, porque es lo que hace la fuente.
    /// Gate: Admin o Dios, O BIEN SemiDios y RoleMaster puestos SIMULTÁNEAMENTE (la comparación del original es
    /// por IGUALDAD contra la máscara, no `&lt;&gt; 0`).</summary>
    private void GmMakeDumbNoMore(ClientSession session, string nick)
    {
        var p = session.Player;
        if (p is null) return;
        bool ok = (p.Privileges & (PrivAdmin | PrivDios)) != 0
               || (p.Privileges & (PrivSemiDios | PrivRoleMaster)) == (PrivSemiDios | PrivRoleMaster);
        if (!ok) return;   // gate silencioso
        var t = _world.PlayerByName(nick.Replace('+', ' ').Trim());
        if (t is null) { session.Send(ServerMessages.LocaleMsg(75)); return; }
        t.Connection?.Send(ServerMessages.DumbNoMore());
        Console.WriteLine($"[{session.RemoteEndpoint}] /NOESTUPIDO {p.Name} -> {t.Name}");
    }

    /// <summary>/MIEMBROSCLAN &lt;clan&gt; (VB6 HandleGuildMemberList, Protocol.bas:6282): lista por consola los
    /// miembros de CUALQUIER clan (no del propio, a diferencia del /ONCLAN ya portado, que sólo ve los online).
    /// El original limpia las barras invertidas del nombre antes de buscar. Gate: Admin o Dios — y acá el VB6
    /// <b>no</b> excluye al RoleMaster, a diferencia de los otros comandos de clan.</summary>
    private void GmGuildMemberList(ClientSession session, string clan)
    {
        var p = session.Player;
        if (p is null || (p.Privileges & (PrivAdmin | PrivDios)) == 0) return;   // gate silencioso
        string nombre = clan.Replace("\\", "").Replace('+', ' ').Trim();
        var g = _world.GuildByName(nombre);
        if (g is null) { session.Send(ServerMessages.ConsoleMsg($"El clan <{nombre}> no existe.", FuenteInfo)); return; }
        session.Send(ServerMessages.ConsoleMsg($"Miembros de <{g.Name}>: {string.Join(", ", g.Members)}", FuenteInfo));
    }

    /// <summary>/RAJARCLAN &lt;nick&gt; (VB6 HandleRemoveCharFromGuild, Protocol.bas:11145 →
    /// <c>m_EcharMiembroDeClan</c>): expulsa a alguien del clan EN QUE ESTÉ. Ojo la diferencia con el kick del
    /// líder que ya existe (<c>HandleGuildKick</c>): ese opera sobre el clan DEL LLAMADOR, y éste sobre el clan
    /// DEL OBJETIVO, así que el clan se resuelve desde la víctima. Gate: Admin o Dios y NO RoleMaster.</summary>
    private void GmRemoveCharFromGuild(ClientSession session, string nick)
    {
        var p = session.Player;
        if (p is null || (p.Privileges & PrivRoleMaster) != 0
            || (p.Privileges & (PrivAdmin | PrivDios)) == 0) return;   // gate silencioso
        string nombre = nick.Replace('+', ' ').Trim();
        // El objetivo puede estar OFFLINE: entonces hay que barrer los clanes buscándolo por nombre.
        var t = _world.PlayerByName(nombre);
        var g = t is not null && !string.IsNullOrEmpty(t.GuildName)
            ? _world.GuildByName(t.GuildName)
            : _world.AllGuilds.FirstOrDefault(x => x.Members.Any(m => string.Equals(m, nombre, StringComparison.OrdinalIgnoreCase)));
        if (g is null) { session.Send(ServerMessages.ConsoleMsg($"{nombre} no pertenece a ningún clan.", FuenteClan)); return; }
        var miembro = g.Members.FirstOrDefault(m => string.Equals(m, nombre, StringComparison.OrdinalIgnoreCase));
        if (miembro is null) { session.Send(ServerMessages.ConsoleMsg($"{nombre} no pertenece a ningún clan.", FuenteClan)); return; }
        // AL LÍDER NO SE LO ECHA, ni siquiera un GM. Es la última línea de `m_PuedeSalirDeClan`
        // (modGuilds.bas:664): `m_PuedeSalirDeClan = UCase$(GetLeader) <> UCase$(Nombre)` — o sea que el
        // permiso se niega SIEMPRE que el expulsado sea el líder, sin mirar quién lo echa. Acá faltaba y el
        // comando decapitaba el clan: lo destapó el flow, que fundó el clan con el propio objetivo de líder.
        if (string.Equals(miembro, g.LeaderName, StringComparison.OrdinalIgnoreCase))
        { session.Send(ServerMessages.ConsoleMsg($"No puedes expulsar al líder del clan <{g.Name}>.", FuenteClan)); return; }
        g.Members.Remove(miembro);
        PersistGuilds();
        if (t is not null) { t.GuildName = ""; t.Connection?.Send(ServerMessages.ConsoleMsg($"Has sido expulsado del clan <{g.Name}>.", FuenteClan)); }
        session.Send(ServerMessages.ConsoleMsg($"Expulsaste a {miembro} del clan <{g.Name}>.", FuenteClan));
        Console.WriteLine($"[{session.RemoteEndpoint}] /RAJARCLAN {p.Name} -> {miembro} de <{g.Name}>");
    }

    // --- Tanda 5: comandos GM que reusan rutinas ya existentes del port ---

    /// <summary>/REM &lt;texto&gt; (VB6 HandleComment, Protocol.bas:6555): registra un comentario en el log de GMs y
    /// contesta <c>LocaleMsg(404)</c> ("¡Comentario guardado!"). El texto en sí NO viaja a nadie: el `LogGM` del
    /// original escribe a disco. Gate: <c>If Not Privilegios And PlayerType.User</c> — bitwise, o sea "cualquier
    /// tier de GM"; el usuario común rebota en silencio.</summary>
    private void GmComment(ClientSession session, string comentario)
    {
        var p = session.Player;
        if (p is null || (p.Privileges & PrivUser) != 0) return;   // gate silencioso
        Console.WriteLine($"[{session.RemoteEndpoint}] /REM {p.Name}: {comentario}");
        session.Send(ServerMessages.LocaleMsg(404));
    }

    /// <summary>/PANELGM (VB6 HandleGMPanel, Protocol.bas:7130): le dice al cliente que abra su panel de GM con
    /// <c>AbrirFormularios(8)</c>. No cambia nada del servidor. Gate: excluye sólo al usuario común.</summary>
    private void GmPanel(ClientSession session)
    {
        var p = session.Player;
        if (p is null || (p.Privileges & PrivUser) != 0) return;   // gate silencioso
        session.Send(ServerMessages.AbrirFormularios(8));
    }

    /// <summary>/HABILITAR (VB6 HandleServerOpenToUsersToggle, Protocol.bas:11074): conmuta el modo mantenimiento
    /// y avisa con <c>LocaleMsg(418)</c> al abrir o <c>419</c> al cerrar. OJO EL SENTIDO, que es fácil de invertir:
    /// el 418 ("habilitado para todos") sale cuando el servidor ESTABA cerrado y se abre.
    ///
    /// El flag y su enforcement ya existían: <c>AdminConfig.OnlyGms</c>, que el login mira para rechazar no-GMs
    /// y el panel de admin ya conmuta. Este comando es la misma palanca desde adentro del juego.
    /// Gate: sólo Admin o Dios (excluye Consejero, SemiDios y RoleMaster).</summary>
    private void GmServerOpenToggle(ClientSession session)
    {
        var p = session.Player;
        if (p is null || (p.Privileges & (PrivUser | PrivConsejero | PrivSemiDios | PrivRoleMaster)) != 0) return;
        if (_admin is null) return;
        _admin.OnlyGms = !_admin.OnlyGms;
        session.Send(ServerMessages.LocaleMsg(_admin.OnlyGms ? (short)419 : (short)418));
        Console.WriteLine($"[{session.RemoteEndpoint}] /HABILITAR {p.Name}: solo-GMs={(_admin.OnlyGms ? "ON" : "OFF")}");
    }

    /// <summary>/GRABAR (VB6 HandleSaveChars, Protocol.bas:10319 → GuardarUsuarios): persiste todos los personajes.
    /// El original NO le contesta al GM que lo pidió — sus mensajes son broadcasts del proceso de guardado, que
    /// este slice no emite. Gate: <c>EsGm</c> (GameLogic.bas:45), o sea cualquiera de los cuatro tiers de staff,
    /// que es exactamente el <c>IsGm</c> del port; rebote silencioso.</summary>
    private void GmSaveChars(ClientSession session)
    {
        var p = session.Player;
        if (p is null || !p.IsGm) return;   // gate silencioso
        // VB6 GuardarUsuarios (General.bas:1371): PAUSA el mundo, avisa "grabando", guarda, despausa y avisa
        // "grabados" — los cuatro broadcasts a TODOS. Acá estaba anotado como diferido ("sus mensajes son
        // broadcasts del proceso de guardado, que este slice no emite"); ya se pueden emitir.
        _world.Broadcast(ServerMessages.PauseToggle());
        _world.Broadcast(ServerMessages.LocaleMsg(180));    // "Grabando personajes..."
        int n = 0;
        foreach (var otro in _world.Players.ToArray()) { _db.SaveCharacter(otro); n++; }
        _world.Broadcast(ServerMessages.PauseToggle());     // el toggle vuelve: el mundo sigue
        _world.Broadcast(ServerMessages.LocaleMsg(436));    // "Personajes grabados"
        Console.WriteLine($"[{session.RemoteEndpoint}] /GRABAR {p.Name}: {n} personajes persistidos");
    }

    /// <summary>/ECHARTODOSPJS (VB6 HandleKickAllChars, Protocol.bas:10265 → EcharPjsNoPrivilegiados): cierra la
    /// conexión de todo usuario COMÚN y deja a los GMs. No emite nada al que lo pidió. Gate: sólo Admin o Dios.</summary>
    private void GmKickAllChars(ClientSession session)
    {
        var p = session.Player;
        if (p is null || (p.Privileges & (PrivUser | PrivConsejero | PrivSemiDios | PrivRoleMaster)) != 0) return;
        int n = 0;
        foreach (var otro in _world.Players.ToArray())
            if (!otro.IsGm) { otro.Connection?.Close("expulsion masiva de un GM"); n++; }
        Console.WriteLine($"[{session.RemoteEndpoint}] /ECHARTODOSPJS {p.Name}: {n} usuarios expulsados");
    }

    // --- Tanda 4: los dos comandos del CENTINELA y el del clima ---

    /// <summary>/TRABAJANDO (VB6 Protocol.bas:7215): lista por consola a los usuarios con
    /// <c>Counters.Trabajando &gt; 0</c>, separados por ", ". Si no hay ninguno → LocaleMsg(409). Gate: excluye al
    /// usuario común y al RoleMaster (o sea que un Consejero SÍ puede, a diferencia de casi todo lo demás).
    ///
    /// El original marca con " (*)" al que el centinela está revisando en ese momento
    /// (<c>modCentinela.Centinela.RevisandoUserIndex</c>); el centinela —el anti-macro que le pide al sospechoso
    /// tipear un código— no está en el slice, así que esa marca no se emite nunca. Es lo único que falta.</summary>
    private void GmWorking(ClientSession session)
    {
        var p = session.Player;
        if (p is null || (p.Privileges & (PrivUser | PrivRoleMaster)) != 0) return;   // gate silencioso
        var nombres = _world.Players.Where(o => o.Trabajando > 0).Select(o => o.Name).ToList();
        if (nombres.Count == 0) { session.Send(ServerMessages.LocaleMsg(409)); return; }
        session.Send(ServerMessages.ConsoleMsg($"Usuarios trabajando: {string.Join(", ", nombres)}", FuenteInfo));
    }

    /// <summary>/OCULTANDO (VB6 Protocol.bas:7258): igual que /TRABAJANDO pero con <c>Counters.Ocultando</c>, y
    /// LocaleMsg(410) cuando no hay nadie. Mismo gate (usuario común y RoleMaster afuera).</summary>
    private void GmHiding(ClientSession session)
    {
        var p = session.Player;
        if (p is null || (p.Privileges & (PrivUser | PrivRoleMaster)) != 0) return;   // gate silencioso
        var nombres = _world.Players.Where(o => o.Ocultando > 0).Select(o => o.Name).ToList();
        if (nombres.Count == 0) { session.Send(ServerMessages.LocaleMsg(410)); return; }
        session.Send(ServerMessages.ConsoleMsg($"Usuarios ocultandose: {string.Join(", ", nombres)}", FuenteInfo));
    }

    /// <summary>/LLUVIA &lt;clima&gt; (VB6 HandleRainToggle, Protocol.bas:9270): fuerza el clima del mundo y lo
    /// broadcastea a TODOS con RainToggle. El byte NO es un booleano: 0 = despejado, 1 = lluvia común,
    /// 2 = lluvia eléctrica, 3 = nieve, y cualquier otro valor cae en el <c>Else</c> que apaga (clima 0).
    ///
    /// Las cuatro ramas del original hacen <c>Lloviendo = Not Lloviendo</c> sobre el MISMO flag, así que pedir
    /// dos veces el mismo clima lo apaga — eso es lo que replica <see cref="Weather.Forzar"/>. Gate: excluye al
    /// usuario común y al Consejero (acá sí, al revés que los dos de arriba).</summary>
    private void GmRainToggle(ClientSession session, byte queclima)
    {
        var p = session.Player;
        if (p is null || (p.Privileges & (PrivUser | PrivConsejero)) != 0) return;   // gate silencioso
        byte efectivo = Weather.Forzar(queclima is 1 or 2 or 3 ? queclima : (byte)0);
        _world.Broadcast(ServerMessages.RainToggle(efectivo));
        Console.WriteLine($"[{session.RemoteEndpoint}] /LLUVIA {p.Name}: clima pedido={queclima} -> efectivo={efectivo}");
    }

    // --- Tanda 3: comandos GM que operan sobre el TARGET del click izquierdo (Player.TargetNpc / TargetMap,X,Y) ---

    /// <summary>La criatura clickeada, si sigue en el mundo. El target guarda un CharIndex y nada lo limpia
    /// cuando el NPC muere, así que la resolución puede fallar — y ahí el comando rebota, como el
    /// <c>TargetNPC = 0</c> del original.</summary>
    private Npc? TargetNpcDe(Player p) =>
        p.TargetNpc == 0 ? null : _world.NpcByIndex(p.TargetNpc);

    /// <summary>/TALKAS &lt;mensaje&gt; (VB6 HandleTalkAsNPC, Protocol.bas:9316): el mensaje sale por la boca de la
    /// criatura clickeada, en el área DE ELLA (no la del GM). Sin criatura clickeada → LocaleMsg(22)
    /// "Primero debes seleccionar un personaje". Sólo Dios/Admin/RoleMaster; el resto rebota en silencio.</summary>
    private void GmTalkAsNpc(ClientSession session, string message)
    {
        var p = session.Player;
        if (!IsAltoGm(p)) return;   // gate silencioso
        var npc = TargetNpcDe(p!);
        if (npc is null) { session.Send(ServerMessages.LocaleMsg(22)); return; }
        // modo 0 EXPLÍCITO: el original omite el 3er argumento de PrepareMessageChatOverHead, cuyo Optional es
        // 0 (Protocol.bas:14446) — mientras que el default del emisor de acá es 1, el modo del chat de jugador
        // (HandleTalk manda el TalkMode). Dejarlo implícito mandaba un byte distinto al del oráculo.
        _world.BroadcastToObservers(npc.Pos, ServerMessages.ChatOverHead(message, npc.CharIndex, 0));
        Console.WriteLine($"[{session.RemoteEndpoint}] /TALKAS {p!.Name} como {npc.Name}: {message}");
    }

    /// <summary>/RESETINV (VB6 HandleResetNPCInventory, Protocol.bas:8789 → ResetNpcInv): vacía el stock del
    /// mercader clickeado. El original NO lo repone: pone <c>NroItems = 0</c>, limpia los slots y apaga el
    /// <c>InvReSpawn</c>, o sea que deja al mercader sin nada para vender. No emite ningún paquete — lo
    /// observable es que la tienda abre vacía. Gate: SemiDios/Dios/Admin (el VB6 excluye también al RoleMaster).</summary>
    private void GmResetNpcInventory(ClientSession session)
    {
        var p = session.Player;
        if (p is null || (p.Privileges & (PrivUser | PrivConsejero | PrivRoleMaster)) != 0) return;   // gate silencioso
        var npc = TargetNpcDe(p);
        if (npc is null) return;              // el VB6 sale en silencio con TargetNPC = 0
        npc.Stock?.Clear();                   // sigue siendo mercader (Stock no-null), pero sin mercadería
        Console.WriteLine($"[{session.RemoteEndpoint}] /RESETINV {p.Name} -> {npc.Name}");
    }

    /// <summary>/CT &lt;mapa&gt; &lt;x&gt; &lt;y&gt; (VB6 HandleTeleportCreate, Protocol.bas:9134): planta un teleport
    /// (objeto 378) en el tile de ARRIBA del GM y le cuelga la salida al destino pedido. No usa el target: el
    /// origen es siempre <c>(Pos.X, Pos.Y - 1)</c>.
    ///
    /// Los cuatro rechazos del original, en su orden: destino inexistente o fuera de mapa → silencio; el tile de
    /// arriba ya ocupado por un objeto o por otra salida → silencio; el DESTINO con objeto o con salida →
    /// LocaleMsg(257). Los dos primeros callan y los dos últimos avisan; no es simetría, es lo que hace la
    /// fuente.</summary>
    private void GmTeleportCreate(ClientSession session, short mapa, byte x, byte y)
    {
        var p = session.Player;
        if (!IsAltoGm(p)) return;   // gate silencioso (Dios/Admin/RoleMaster: el VB6 excluye a SemiDios acá)
        Console.WriteLine($"[{session.RemoteEndpoint}] /CT {p!.Name} -> ({mapa},{x},{y})");
        // `InMapBounds` = el ÁREA JUGABLE (9..92 × 7..94), no el mapa entero. La diferencia no es cosmética: el
        // anillo de afuera es una trampa sin salida. Un teleport a (5,5) manda al jugador ahí, y desde ahí el
        // gate de Walk (que sí usa estos bordes) le rechaza los cuatro pasos con snap-back — y como la posición
        // se persiste al .chr, queda encerrado incluso reconectando. El original ni siquiera crea el teleport.
        if (!_data.MapExists(mapa)
            || x < GameLimits.MinXBorder || x > GameLimits.MaxXBorder
            || y < GameLimits.MinYBorder || y > GameLimits.MaxYBorder) return;
        if (p.Pos.Y <= 1) return;                                     // no hay tile "arriba" donde plantarlo
        var origen = new WorldPos(p.Pos.Map, p.Pos.X, (byte)(p.Pos.Y - 1));
        if (_world.TryGetGround(origen, out _)) return;               // ya hay un objeto ahí
        if (_data.MapExits.ContainsKey((origen.Map, origen.X, origen.Y))) return;   // ya hay una salida ahí
        if (_world.TryGetGround(new WorldPos(mapa, x, y), out _)
            || _data.MapExits.ContainsKey((mapa, x, y)))
        { session.Send(ServerMessages.LocaleMsg(257)); return; }

        _world.SetGround(origen, GameLimits.ObjTeleport, 1);
        _world.BroadcastToObservers(origen, ServerMessages.ObjectCreate(origen.X, origen.Y, GameLimits.ObjTeleport, 1));
        _data.SetMapExit(origen.Map, origen.X, origen.Y, mapa, x, y);
    }

    /// <summary>/DT (VB6 HandleTeleportDestroy, Protocol.bas:9205): borra el teleport del tile CLICKEADO — el
    /// único comando que usa <c>TargetMap/X/Y</c> y no el target de personaje. Exige que el tile tenga un objeto
    /// otTeleport Y una salida asociada; si es cualquier otra cosa, no hace nada.
    ///
    /// Detalle del original que parece de más y no lo es: además del teleport clickeado borra el objeto 378 que
    /// haya EN EL DESTINO. Es el teleport de vuelta del par que crea /CT; sin eso quedaría media puerta colgada
    /// apuntando a un tile ya limpio.</summary>
    private void GmTeleportDestroy(ClientSession session)
    {
        var p = session.Player;
        if (!IsAltoGm(p)) return;   // gate silencioso
        if (p!.TargetMap == 0 || p.TargetX < 1 || p.TargetX > 100 || p.TargetY < 1 || p.TargetY > 100) return;
        var pos = new WorldPos(p.TargetMap, p.TargetX, p.TargetY);
        if (!_world.TryGetGround(pos, out var obj) || obj.ObjIndex <= 0) return;
        if (_data.Obj(obj.ObjIndex)?.ObjType != 19) return;                              // no es otTeleport
        if (!_data.MapExits.TryGetValue((pos.Map, pos.X, pos.Y), out var dest)) return;   // sin salida asociada

        _world.RemoveGround(pos);
        _world.BroadcastToObservers(pos, ServerMessages.ObjectDelete(pos.X, pos.Y));
        var destPos = new WorldPos((short)dest.DestMap, (byte)dest.DestX, (byte)dest.DestY);
        if (_world.TryGetGround(destPos, out var otro) && otro.ObjIndex == GameLimits.ObjTeleport)
        {
            _world.RemoveGround(destPos);
            _world.BroadcastToObservers(destPos, ServerMessages.ObjectDelete(destPos.X, destPos.Y));
        }
        _data.RemoveMapExit(pos.Map, pos.X, pos.Y);
        Console.WriteLine($"[{session.RemoteEndpoint}] /DT {p.Name} en ({pos.Map},{pos.X},{pos.Y})");
    }

    /// <summary>/SEGUIR (VB6 HandleNPCFollow, Protocol.bas:8682 → DoFollow, MODULO_NPCs.bas:1123): TOGGLE que
    /// pone a la criatura clickeada a perseguir al GM. Prende <c>flags.Follow</c>, la vuelve no hostil, le anota
    /// al GM como <c>AttackedBy</c> y le cambia el modo de movimiento a <c>NPCDEFENSA</c> (<c>SeguirAgresor</c>);
    /// el segundo /SEGUIR le devuelve el movimiento y la hostilidad de su ficha. Además le saca la parálisis.
    ///
    /// En el original esto <b>casi nunca se nota</b>, y no por un bug de este port: la fase de ATAQUE del tick
    /// corre antes que la de movimiento y, para un NPC con <c>Hostile = 0</c> (que es como lo deja DoFollow),
    /// termina en <c>RestoreOldMovement</c> apenas no tiene al agresor adyacente — restaurándole el Movement
    /// antes de que <c>SeguirAgresor</c> llegue a correr. Y con un GM nunca lo tiene "adyacente" a los ojos de la
    /// IA, porque <see cref="Player.AdminPerseguible"/> es False para todo GM. Queda vivo el caso de la
    /// criatura CON dueño (mascota/invocación), a la que la fase de ataque no le corre. Se porta el mecanismo tal
    /// cual —no el resultado— así que el port reproduce las dos cosas por la misma razón que la fuente.</summary>
    private void GmNpcFollow(ClientSession session)
    {
        var p = session.Player;
        if (p is null || (p.Privileges & (PrivUser | PrivConsejero)) != 0) return;   // gate silencioso
        var npc = TargetNpcDe(p);
        if (npc is null) return;   // sin criatura clickeada el VB6 no hace nada (ni avisa)
        npc.ToggleFollow(p.Name);
        Console.WriteLine($"[{session.RemoteEndpoint}] /SEGUIR {p.Name} -> {npc.Name} ({(npc.Follow ? "on" : "off")})");
    }

    /// <summary>/NENE (VB6 Protocol.bas:6707): inventario de criaturas de un mapa, separando HOSTILES del resto y
    /// agrupando por nombre con sus coordenadas. Gate Consejero+.</summary>
    private void GmCreaturesInMap(ClientSession session, short map)
    {
        var p = session.Player;
        if (p is null || p.Privileges <= PrivUser) return;   // gate silencioso
        // VB6 MapaValido (General.bas:411): TODO el cuerpo del comando vive dentro de `If MapaValido(Map)`, así
        // que con un mapa inexistente el original no emite un solo byte (el cliente manda el InputBox crudo).
        if (!_data.MapExists(map)) return;
        var hostiles = new Dictionary<string, List<string>>();
        var otros = new Dictionary<string, List<string>>();
        foreach (var npc in _world.NpcsOnMap(map))
        {
            var destino = npc.Hostile ? hostiles : otros;
            // El VB6 agrupa por PREFIJO (`Left$(List(j), Len(Name)) = Name`), no por nombre exacto: dos especies
            // que comparten el comienzo del nombre caen en el mismo renglón. Se replica para no inventar otro criterio.
            var key = destino.Keys.FirstOrDefault(g => g.StartsWith(npc.Name, StringComparison.Ordinal)) ?? npc.Name;
            if (!destino.TryGetValue(key, out var coords)) destino[key] = coords = new List<string>();
            coords.Add($"({npc.Pos.X},{npc.Pos.Y})");
        }
        session.Send(ServerMessages.LocaleMsg(406));   // "Npcs Hostiles en mapa:"
        if (hostiles.Count == 0) session.Send(ServerMessages.LocaleMsg(405));
        else foreach (var (nombre, coords) in hostiles)
            session.Send(ServerMessages.ConsoleMsg($"{coords.Count} {nombre}: {string.Join(", ", coords)}", 3));
        session.Send(ServerMessages.LocaleMsg(407));   // "Otros NPCs:"
        if (otros.Count == 0) session.Send(ServerMessages.LocaleMsg(408));
        else foreach (var (nombre, coords) in otros)
            session.Send(ServerMessages.ConsoleMsg($"{coords.Count} {nombre}: {string.Join(", ", coords)}", 3));
    }

    /// <summary>/ONCLAN (VB6 Protocol.bas:9066): miembros conectados de un clan. Omite al propio emisor y, si quien
    /// pregunta no es Dios/Admin, también a los Dioses/Admins. Clan inexistente → silencio. Font 9 (GUILDMSG).</summary>
    private void GmGuildOnline(ClientSession session, string guildName)
    {
        var p = session.Player;
        if (p is null || (p.Privileges & PrivRoleMaster) != 0
            || (p.Privileges & (PrivSemiDios | PrivDios | PrivAdmin)) == 0) return;   // gate silencioso
        guildName = guildName.Replace('+', ' ').Trim();
        // El gate del VB6 es por EXISTENCIA del clan (GuildIndex > 0), no por "hay alguien online": un clan que
        // existe pero sin nadie conectado igual responde, con la lista vacía. Sólo el clan inexistente calla.
        if (_world.GuildByName(guildName) is null) return;
        // Sin filtro por rango: en el VB6 esa condición colapsa por precedencia y termina listando a todos los
        // miembros online salvo el propio emisor. Replicarlo tal cual en vez de inventar un filtro más estricto.
        var nombres = _world.GuildMembersOnline(guildName).Where(m => m.CharIndex != p.CharIndex).Select(m => m.Name);
        session.Send(ServerMessages.ConsoleMsg($"Clan {guildName.ToUpperInvariant()}: {string.Join(",", nombres)}", 9));
    }

    private void GmInvisible(ClientSession session)
    {
        var p = session.Player;
        if (!RequireGm(session, p)) return;
        p!.Invisible = !p.Invisible;
        p.InvisibleUntilTick = 0;   // /INVISIBLE es permanente: limpia cualquier timer de hechizo pendiente que destaparía al GM
        // `DoAdminInvisible` (Trabajo.bas:1208/1217) también mueve el showName: al ocultarse lo baja y al
        // mostrarse lo sube. Sin esto el GM invisible seguía mandando su nick en el UpdateTagAndStatus y en el
        // CharacterCreate de quien entrara a su área, o sea que se delataba solo.
        p.ShowName = !p.Invisible;
        session.Send(ServerMessages.ConsoleMsg(p.Invisible ? "Ahora eres invisible." : "Ya no eres invisible.", FuenteInfo));
        // Observers toggle the GM's visibility (SetInvisible keeps the char so other GMs still see it).
        _world.BroadcastToObservers(p.Pos, ServerMessages.SetInvisible(p.CharIndex, p.Invisible), p.CharIndex);
    }

    // --- Guilds (modGuilds slice: create, chat, leave, online) ---

    /// <summary>Persists the guild registry after a mutation, so clans survive restarts.</summary>
    private void PersistGuilds() => _clanesSucios = true;

    /// <summary>CreateNewGuild (id 28): desc, name, site, codex (4 ASCII strings). Slice: use name+desc,
    /// found the guild with the caller as leader.</summary>
    private void HandleCreateNewGuild(ClientSession session, ByteQueueReader r)
    {
        string desc = r.ReadAsciiString();
        string name = r.ReadAsciiString().Trim();
        _ = r.ReadAsciiString();          // site (unused in the slice)
        _ = r.ReadAsciiString();          // codex (unused in the slice)
        _loop.Enqueue(() =>
        {
            var p = session.Player;
            if (p is null) return;
            if (!string.IsNullOrEmpty(p.GuildName)) { session.Send(ServerMessages.ConsoleMsg("Ya perteneces a un clan.", FuenteClan)); return; }
            if (string.IsNullOrWhiteSpace(name)) { session.Send(ServerMessages.ConsoleMsg("Nombre de clan inválido.", FuenteClan)); return; }
            var guild = _world.CreateGuild(name, p.Name, desc);
            if (guild is null) { session.Send(ServerMessages.ConsoleMsg("Ya existe un clan con ese nombre.", FuenteClan)); return; }
            guild.Alignment = p.FoundingAlignment; // from the GuildFundation step (0 if founded directly)
            p.FoundingAlignment = 0;
            p.GuildName = guild.Name;
            PersistGuilds();
            session.Send(ServerMessages.ConsoleMsg($"Fundaste el clan <{guild.Name}> y eres su líder.", FuenteClan));
        });
    }

    /// <summary>GuildMessage (id 77): an ASCII string, broadcast to online guild members as GuildChat.</summary>
    private void HandleGuildMessage(ClientSession session, ByteQueueReader r)
    {
        string chat = r.ReadAsciiString();
        if (Frenado(session, ruidosa: true)) return;
        _loop.Enqueue(() =>
        {
            var p = session.Player;
            if (p is null || string.IsNullOrEmpty(chat)) return;
            if (string.IsNullOrEmpty(p.GuildName)) return;   // VB6 HandleGuildMessage: sin clan, silencio
            var line = $"{p.Name}> {chat}";
            foreach (var m in _world.GuildMembersOnline(p.GuildName))
                m.Connection?.Send(ServerMessages.GuildChat(line));
            // Y a los GMs enganchados con /SHOWCMSG a ESTE clan (VB6: los `ConectarGM` de la clase clan quedan en
            // la lista de destinatarios del chat, así que el GM lee las conversaciones sin ser miembro).
            foreach (var gm in _world.Players.Where(pl => pl.IsGm
                        && string.Equals(pl.EscucheClan, p.GuildName, StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(pl.Name, p.Name, StringComparison.OrdinalIgnoreCase)))
                gm.Connection?.Send(ServerMessages.GuildChat(line));
        });
    }

    /// <summary>GuildRequestMembership (id 60): guild name + application text. The applicant is QUEUED for the
    /// leader to accept (GuildAcceptNewMember) or reject (GuildRejectNewMember) — real AO does not auto-admit.</summary>
    private void HandleGuildJoin(ClientSession session, ByteQueueReader r)
    {
        string guildName = r.ReadAsciiString().Trim();
        string application = r.ReadAsciiString();   // el texto de la solicitud: lo lee el líder con ShowUserRequest
        _loop.Enqueue(() =>
        {
            var p = session.Player;
            if (p is null) return;
            if (!string.IsNullOrEmpty(p.GuildName)) { session.Send(ServerMessages.ConsoleMsg("Ya perteneces a un clan.", FuenteClan)); return; }
            var guild = _world.GuildByName(guildName);
            if (guild is null) { session.Send(ServerMessages.ConsoleMsg("Ese clan no existe.", FuenteClan)); return; }
            if (guild.JoinRequests.ContainsKey(p.Name)) { session.Send(ServerMessages.ConsoleMsg("Ya solicitaste unirte a ese clan; espera la decisión del líder.", FuenteClan)); return; }
            // El TEXTO de la solicitud se guarda: es el "Detalle" que el original mete en SOLICITUDES.dat
            // (NuevoAspirante(Nombre, Peticion)) y lo único que transporta ShowUserRequest cuando el líder
            // la mira. Antes se leía del wire y se tiraba, así que no había nada que mostrarle.
            guild.JoinRequests[p.Name] = application;
            PersistGuilds();
            // Protocol.bas:4838, tal cual: el original nombra al clan sin los <> y cierra con punto.
            session.Send(ServerMessages.ConsoleMsg(
                $"Tu solicitud ha sido enviada. Espera prontas noticias del líder de {guild.Name}.", FuenteClan));
            foreach (var m in _world.GuildMembersOnline(guild.Name))
                if (string.Equals(m.Name, guild.LeaderName, StringComparison.OrdinalIgnoreCase))
                    m.Connection?.Send(ServerMessages.ConsoleMsg($"{p.Name} solicitó unirse al clan.", FuenteClan));
        });
    }

    /// <summary>GuildAcceptNewMember (id 54): applicant name. The leader admits a queued aspirant.</summary>
    private void HandleGuildAcceptMember(ClientSession session, ByteQueueReader r)
    {
        string applicant = r.ReadAsciiString().Trim();
        _loop.Enqueue(() =>
        {
            var p = session.Player;
            if (p is null) return;
            var guild = string.IsNullOrEmpty(p.GuildName) ? null : _world.GuildByName(p.GuildName);
            if (guild is null || !string.Equals(guild.LeaderName, p.Name, StringComparison.OrdinalIgnoreCase))
            { session.Send(ServerMessages.ConsoleMsg("Solo el líder del clan puede aceptar solicitudes.", FuenteClan)); return; }
            if (!guild.JoinRequests.Remove(applicant)) { session.Send(ServerMessages.ConsoleMsg($"{applicant} no tiene una solicitud pendiente.", FuenteClan)); return; }
            var joiner = _world.PlayerByName(applicant);
            // VB6 a_AceptarAspirante: rechaza si el aspirante ya pertenece a otro clan (evita doble membresía).
            if (joiner is not null && !string.IsNullOrEmpty(joiner.GuildName))
            { session.Send(ServerMessages.ConsoleMsg($"{applicant} ya pertenece a un clan.", FuenteClan)); return; }
            guild.Members.Add(applicant);
            if (joiner is not null) joiner.GuildName = guild.Name;
            PersistGuilds();
            session.Send(ServerMessages.ConsoleMsg($"Aceptaste a {applicant} en el clan.", FuenteClan));
            joiner?.Connection?.Send(ServerMessages.ConsoleMsg($"Fuiste aceptado en el clan <{guild.Name}>.", FuenteClan));
            foreach (var m in _world.GuildMembersOnline(guild.Name))
                if (m.CharIndex != p.CharIndex && !string.Equals(m.Name, applicant, StringComparison.OrdinalIgnoreCase))
                    m.Connection?.Send(ServerMessages.ConsoleMsg($"{applicant} se unió al clan.", FuenteClan));
        });
    }

    /// <summary>GuildRejectNewMember (id 55): applicant name + reason. The leader declines a queued aspirant.</summary>
    private void HandleGuildRejectMember(ClientSession session, ByteQueueReader r)
    {
        string applicant = r.ReadAsciiString().Trim();
        string reason = r.ReadAsciiString().Trim();
        _loop.Enqueue(() =>
        {
            var p = session.Player;
            if (p is null) return;
            var guild = string.IsNullOrEmpty(p.GuildName) ? null : _world.GuildByName(p.GuildName);
            if (guild is null || !string.Equals(guild.LeaderName, p.Name, StringComparison.OrdinalIgnoreCase))
            { session.Send(ServerMessages.ConsoleMsg("Solo el líder del clan puede rechazar solicitudes.", FuenteClan)); return; }
            if (!guild.JoinRequests.Remove(applicant)) { session.Send(ServerMessages.ConsoleMsg($"{applicant} no tiene una solicitud pendiente.", FuenteClan)); return; }
            PersistGuilds();
            session.Send(ServerMessages.ConsoleMsg($"Rechazaste la solicitud de {applicant}.", FuenteClan));
            _world.PlayerByName(applicant)?.Connection?.Send(
                ServerMessages.ConsoleMsg($"Tu solicitud al clan <{guild.Name}> fue rechazada{(reason.Length > 0 ? $": {reason}" : ".")}", FuenteClan));
        });
    }

    /// <summary>GuildRequestJoinerInfo (id 49): applicant name. Sends the leader the aspirant's basic sheet.</summary>
    private void HandleGuildJoinerInfo(ClientSession session, ByteQueueReader r)
    {
        string applicant = r.ReadAsciiString().Trim();
        _loop.Enqueue(() =>
        {
            var p = session.Player;
            if (p is null) return;
            var guild = string.IsNullOrEmpty(p.GuildName) ? null : _world.GuildByName(p.GuildName);
            if (guild is null || !string.Equals(guild.LeaderName, p.Name, StringComparison.OrdinalIgnoreCase))
            { return; }  // VB6 no emite (Mensaje225 comentado)
            if (!guild.JoinRequests.TryGetValue(applicant, out var detalle)) { return; }
            session.Send(ServerMessages.ShowUserRequest(detalle));
        });
    }

    /// <summary>GuildDeclareWar (id 52): enemy guild name. The caller's guild leader declares war; both
    /// clans become enemies (attacks between their members don't incur the criminal flag).</summary>
    private void HandleGuildDeclareWar(ClientSession session, ByteQueueReader r)
    {
        string enemyName = r.ReadAsciiString().Trim();
        _loop.Enqueue(() =>
        {
            var p = session.Player;
            if (p is null) return;
            if (string.IsNullOrEmpty(p.GuildName)) { session.Send(ServerMessages.ConsoleMsg("No perteneces a ningún clan.", FuenteClan)); return; }
            var mine = _world.GuildByName(p.GuildName);
            if (mine is null || !string.Equals(mine.LeaderName, p.Name, StringComparison.OrdinalIgnoreCase))
            { session.Send(ServerMessages.ConsoleMsg("Solo el líder del clan puede declarar la guerra.", FuenteClan)); return; }
            var enemy = _world.GuildByName(enemyName);
            if (enemy is null) { session.Send(ServerMessages.ConsoleMsg("Ese clan no existe.", FuenteClan)); return; }
            if (string.Equals(enemy.Name, mine.Name, StringComparison.OrdinalIgnoreCase)) { session.Send(ServerMessages.ConsoleMsg("No puedes declararte la guerra a ti mismo.", FuenteClan)); return; }
            mine.Enemies.Add(enemy.Name);
            enemy.Enemies.Add(mine.Name);
            PersistGuilds();
            foreach (var m in _world.GuildMembersOnline(mine.Name).Concat(_world.GuildMembersOnline(enemy.Name)))
                m.Connection?.Send(ServerMessages.ConsoleMsg($"¡GUERRA! Los clanes <{mine.Name}> y <{enemy.Name}> están en guerra.", FuenteClan));
        });
    }

    /// <summary>Enqueues a simple ConsoleMsg acknowledgement (for packets with no state change).</summary>
    private const short Fogata = 63;   // Declares.bas: Public Const FOGATA As Integer = 63

    /// <summary>La fogata APAGADA (VB6 <c>FOGATA_APAG = 136</c>, Declares.bas:365). En obj.dat se llama
    /// "Ramitas": es el paso intermedio entre la leña y el fuego, y el que se enciende por doble-clic.</summary>
    private const short FogataApagada = 136;

    /// <summary>Leña (VB6 <c>Leña = 58</c>) y daga (<c>DAGA = 15</c>), Declares.bas:355 y :364.</summary>
    private const short Lenia = 58, Daga = 15;
    private const int Runa = 38;
    /// <summary>VB6 <c>eOBJType.otPasajes</c>: el pasaje del barco (viaje rápido entre puertos).</summary>
    private const int Pasaje = 36;
    /// <summary>VB6 <c>eOBJType.otAnillo</c> (18): el slot de HERRAMIENTA de este fork — piquete, hacha, caña,
    /// red, tijeras, costurero, olla y serrucho— además de los anillos mágicos que suben una skill.</summary>
    private const int Herramienta = 18;
    private const int MaxMascotasEntrenador = 7;   // VB6 MAXMASCOTASENTRENADOR (Declares.bas:218)         // otRuna (Declares.bas:615) — la Runa de teletransportacion

    /// <summary>Partir un montón de leña en fogatas apagadas (VB6 <c>TratarDeHacerFogata</c>, Trabajo.bas:1228).
    /// Pide TRES troncos —el mensaje del original dice "no mas ni menos de tres" pero el gate es <c>&lt; 3</c>,
    /// o sea que con seis salen dos fogatas— y la tirada sale de Supervivencia. No consume la daga ni energía.
    /// El resultado queda como nuevo objetivo del jugador, así se puede encender sin volver a clickear.</summary>
    private void TratarDeHacerFogata(ClientSession session, Player p)
    {
        var pos = new WorldPos(p.TargetObjMap, p.TargetObjX, p.TargetObjY);
        if (!_world.TryGetGround(pos, out var suelo) || suelo.ObjIndex != Lenia)
        { session.Send(ServerMessages.LocaleMsg(422)); return; }
        if (DistanciaVB6(pos, p.Pos) > 2) { session.Send(ServerMessages.LocaleMsg(8)); return; }
        if (DeadCheck(session, p)) return;                              // LocaleMsg 77
        if (suelo.Amount < 3) { session.Send(ServerMessages.LocaleMsg(208)); return; }
        int skill = p.Skills[GameLimits.SkillSupervivencia];
        int suerte = skill >= 35 ? 1 : skill >= 6 ? 2 : 3;
        if (_rng.Next(1, suerte + 1) != 1) return;                      // fallar la tirada no avisa nada
        short cuantas = (short)(suelo.Amount / 3);
        _world.SetGround(pos, FogataApagada, cuantas);
        _world.BroadcastToObservers(pos, ServerMessages.ObjectCreate(pos.X, pos.Y, FogataApagada, 0));
        session.Send(ServerMessages.ConsoleMsg($"Has hecho {cuantas} fogatas.", FuenteInfo));
        p.TargetObj = FogataApagada;
    }

    /// <summary>VB6 HayOBJarea(.Pos, FOGATA): una fogata encendida en la casilla del jugador o pegada a ella.</summary>
    private bool NearCampfire(Player p)
    {
        for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
            {
                int x = p.Pos.X + dx, y = p.Pos.Y + dy;
                if (x is < 0 or > 255 || y is < 0 or > 255) continue;
                if (_world.TryGetGround(new WorldPos(p.Pos.Map, (byte)x, (byte)y), out var g) && g.ObjIndex == Fogata)
                    return true;
            }
        return false;
    }

    /// <summary><c>ALINEACION_GUILD</c> (modGuilds.bas:36). La alineación del clan NO la elige el jugador: sale
    /// de su facción.</summary>
    private const byte AlinRepublicano = 1, AlinImperial = 2, AlinCaotico = 3, AlinRenegado = 4;

    /// <summary>La alineación que le toca a un personaje por su facción (VB6 HandleGuildFundate,
    /// Protocol.bas:6177-6188). Devuelve 0 si no cae en ninguna de las cuatro, que es el caso que el original
    /// manda a hablar con un GM.</summary>
    private static byte AlineacionPorFaccion(Player p) =>
        p.IsCitizen || p.IsArmada ? AlinImperial
        : p.IsRepublicano || p.IsMilicia ? AlinRepublicano
        : p.IsCaos ? AlinCaotico
        : p.FactionStatus == Player.StatusCriminal ? AlinRenegado
        : (byte)0;

    /// <summary>GuildFundate (id 89): paso 1 de la fundación.
    ///
    /// <para>Acá sólo se abría el formulario. En el original este paso <b>decide la alineación del clan a partir
    /// de la FACCIÓN</b> del que funda y la guarda; el paso 2 lee un byte del cliente cuyo <c>Select Case</c>
    /// está COMENTADO en el fuente, o sea que la elección del cliente no se usa. Nuestro port hacía lo
    /// contrario: se guardaba el byte que mandara el cliente, así que un renegado podía fundar un clan
    /// Imperial. Es la familia de "qué le creemos al cliente".</para></summary>
    private void HandleGuildFundate(ClientSession session)
    {
        _loop.Enqueue(() =>
        {
            var p = session.Player;
            if (p is null) return;
            if (!string.IsNullOrEmpty(p.GuildName)) return;   // VB6 HandleGuildFundate: HasFound -> Exit Sub, sin mensaje
            byte alin = AlineacionPorFaccion(p);
            if (alin == 0)
            { session.Send(ServerMessages.ConsoleMsg("Hay un error en su faccion, comuniquese con algun GameMaster", FuenteClan)); return; }
            p.FoundingAlignment = alin;
            session.Send(ServerMessages.AbrirFormularios(7));   // VB6: WriteAbrirFormularios(UserIndex, 7)
        });
    }

    /// <summary>GuildFundation (id 90): step 2 — records the chosen alignment for the next CreateNewGuild.
    /// VB6 HandleGuildFundation (Protocol.bas:6265): con PuedeFundarUnClan responde WriteAbrirFormularios(7)
    /// (abre el form de fundación), NO un texto; si ya fundó un clan (HasFound) hace LogCheating + Exit Sub.</summary>
    private void HandleGuildFundation(ClientSession session, ByteQueueReader r)
    {
        r.ReadByte();   // el byte de ClanType del cliente se LEE y se descarta: ver abajo
        _loop.Enqueue(() =>
        {
            var p = session.Player;
            if (p is null) return;
            if (!string.IsNullOrEmpty(p.GuildName)) return;   // VB6: HasFound -> LogCheating + Exit Sub (silencio)
            // La alineación ya quedó fijada por la FACCIÓN en el paso 1. El original lee este byte y su
            // `Select Case` está comentado entero (Protocol.bas:6256-6260), así que la elección del cliente no
            // se usa; acá se estaba guardando, que es dejar que el jugador elija la alineación de su clan.
            if (p.FoundingAlignment == 0) p.FoundingAlignment = AlineacionPorFaccion(p);
            session.Send(ServerMessages.AbrirFormularios(7));
        });
    }

    /// <summary>Catálogo de la tienda de donador (VB6 [SHOP] del Server.ini, FileIO.bas:1539). Cada premio es un
    /// (ObjIndex, precio en créditos). Estos son los 6 del install de CoverAO-2021; los índices 1638/1639 no existen
    /// en el Obj.dat (el VB6 revienta con subscript-out-of-range al listarlos — acá se manejan con un placeholder,
    /// sin crashear). Se deja como tabla en código porque en el VB6 es config de Server.ini, no data del Obj.dat.</summary>
    private static readonly (short ObjIndex, short Puntos)[] PrizeCatalog =
    {
        (1275, 20), (592, 20), (1638, 40), (1639, 40), (667, 80), (1265, 80),
    };

    /// <summary>PidePremios (id 87): el cliente pide la lista de premios de donador canjeables. VB6 WritePremios
    /// (Protocol.bas:16468): emite el paquete Premios con el catálogo + el crédito actual del jugador, o NADA si el
    /// catálogo está vacío. El nombre de cada premio sale del Obj.dat (placeholder si el índice no existe).</summary>
    private void HandlePidePremios(ClientSession session)
    {
        _loop.Enqueue(() =>
        {
            var p = session.Player;
            if (p is null || PrizeCatalog.Length == 0) return;   // sin premios -> no emite (VB6 CantPremios=0)
            var prizes = PrizeCatalog
                .Select(e => (Name: _data.Obj(e.ObjIndex)?.Name is { Length: > 0 } n ? n : $"Objeto #{e.ObjIndex}", e.Puntos))
                .ToList();
            session.Send(ServerMessages.Premios(prizes, (int)p.CreditoDonador));
        });
    }

    /// <summary>RPremios (id 88): index(int, 1-based) del premio a canjear. VB6 HandleRPremios (Protocol.bas:16506):
    /// premio comprado con CreditoDonador. Índice inválido / objeto inexistente -> silencio; crédito insuficiente ->
    /// LocaleMsg 423; inventario lleno -> LocaleMsg 328 (sin descontar). Sólo al meter el ítem con éxito se cobra.</summary>
    private void HandleRPremios(ClientSession session, ByteQueueReader r)
    {
        int index = r.ReadInteger();   // 1-based (VB6 PremiosInfo(index))
        _loop.Enqueue(() =>
        {
            var p = session.Player;
            if (p is null) return;
            if (index < 1 || index > PrizeCatalog.Length) return;   // fuera de rango -> silencio (el VB6 crashea; acá no)
            var (objIndex, puntos) = PrizeCatalog[index - 1];
            var obj = _data.Obj(objIndex);
            if (obj is null || string.IsNullOrWhiteSpace(obj.Name)) return;   // premio con objeto inexistente -> silencio
            if (puntos > p.CreditoDonador) { session.Send(ServerMessages.LocaleMsg(423)); return; }   // créditos insuficientes
            int slot = FindSlotFor(p, objIndex);
            if (slot == 0) { session.Send(ServerMessages.LocaleMsg(328)); return; }   // inventario lleno (sin descontar)
            var inv = p.Inventory[slot];
            inv.ObjIndex = objIndex;
            inv.Amount = (short)Math.Min(GameLimits.MaxInventoryObjs, inv.Amount + 1);
            p.Inventory[slot] = inv;
            p.CreditoDonador -= puntos;   // se cobra DESPUÉS de meter el ítem con éxito
            session.Send(ServerMessages.ChangeInventorySlot((byte)slot, inv.ObjIndex, inv.Amount, inv.Equipped, 0f, 1));
            session.Send(ServerMessages.ConsoleMsg($"Canjeaste {obj.Name} por {puntos} créditos. Te quedan {p.CreditoDonador}.", FuenteInfo));
        });
    }

    /// <summary>Reward (id 75): claim a faction reward — a gold bonus for enlisted players.</summary>
    private void HandleReward(ClientSession session)
    {
        _loop.Enqueue(() =>
        {
            var p = session.Player;
            if (p is null) return;
            // VB6 HandleReward (Protocol.bas:5424) exige un NPC de facciones CLICKEADO; sin él manda
            // LocaleMsg(22) y NO entrega nada. El slice regalaba 100 de oro a cualquiera: canilla de oro.
            // Se resolvía por la baldosa encarada, que no es lo que mira el original ni lo que manda el cliente.
            if (p.TargetNpc < 1) { session.Send(ServerMessages.LocaleMsg(22)); return; }   // VB6 :5436
            var npc = _world.NpcByIndex(p.TargetNpc);
            // Y OJO CON EL SILENCIO: el 22 es SÓLO el "no clickeaste a nadie". Con un target que no es de
            // facciones —o estando muerto— el original sale MUDO (:5442, un `Exit Sub` pelado). Acá el 22 salía
            // en los tres casos, así que el cliente veía un aviso donde el original no manda nada.
            if (npc is null || npc.FactionEnlist == 0 || !p.IsAlive) return;
            if (DistanciaVB6(p.Pos, npc.Pos) > 4) { session.Send(ServerMessages.LocaleMsg(8)); return; }   // VB6 :5444
            // El NPC te habla A VOS y no a la cuadra: las 32 frases de ModFacciones.bas salen por
            // `WriteChatOverHead(UserIndex, ...)`, que es al socket del jugador. Acá se emitían al área.
            void Say(string t) => session.Send(ServerMessages.ChatOverHead(t, npc.CharIndex));
            // El bando equivocado tampoco es un 22: el original lo contesta con el globo del NPC (:5455/:5467/:5478).
            if (npc.FactionEnlist != p.FactionStatus)
            {
                // El `Select Case` del original sólo contempla Armada, Milicia y Caos: cualquier otro bando cae
                // fuera y sale mudo, así que acá el default también calla en vez de inventar una frase.
                string? reproche = npc.FactionEnlist switch
                {
                    Player.StatusArmada => "¡¡No perteneces a las tropas reales!!",
                    Player.StatusMilicia => "¡¡No perteneces a la Milicia republicana!!",
                    Player.StatusCaos => "¡¡No perteneces a la legión oscura!!",
                    _ => null,
                };
                if (reproche is not null) Say(reproche);
                return;
            }

            if (p.FactionRank >= MaxFactionRank(p)) { Say("Ya no tengo trabajo para darte, has alcanzado el rango más alto aquí."); return; }
            int killsEnemigos = KillsForFaction(p);
            int req = MatadosParaRango(p);   // VB6 matadosArmada/Caos: kills acumulados para el próximo rango
            if (killsEnemigos < req) { Say($"Mata {req - killsEnemigos} enemigos más para recibir tu próxima recompensa."); return; }
            p.FactionRank++;
            // VB6 RecompensaArmadaReal:427 / RecompensaCaos:548 / RecompensaMilicia:497: al entrar en la jerarquía
            // superior de tu facción el líder entrega además su armadura (rango >= 6 en Armada/Caos, >= 4 en Milicia).
            if (p.FactionRank >= (p.IsMilicia ? MiliciaArmorRank : SecondHierarchyRank)) GrantFactionArmor(session, p);
            session.Send(ServerMessages.UpdateUserStats(p));
            Say($"¡Felicidades! Has ascendido a {FactionTitle(p)}.");
        });
    }

    /// <summary>Work (id 21): skill(byte). Selecting a gathering skill begins that work (like a click).</summary>
    private void HandleWork(ClientSession session, ByteQueueReader r)
    {
        byte skill = r.ReadByte();
        _loop.Enqueue(() =>
        {
            var p = session.Player;
            if (p is null) return;
            if (DeadCheck(session, p)) return;   // VB6 HandleWork: Protocol.bas:2311
            // Ocultarse (VB6 HandleWork → Case Ocultarse, Protocol.bas:2324): es una self-action, NO pide
            // objetivo. Gates: ya oculto → silencio; navegando → 56; montando → 67; mapa InviSinEfecto → 447.
            // Si pasa, DoOcultarse tira la suerte.
            if (skill == GameLimits.SkillOcultar)
            {
                if (p.Oculto) return;
                if (p.Navigating) { session.Send(ServerMessages.LocaleMsg(56)); return; }
                if (p.Mounted) { session.Send(ServerMessages.LocaleMsg(67)); return; }
                if (_data.MapNoInvi(p.Pos.Map)) { session.Send(ServerMessages.LocaleMsg(447)); return; }
                DoOcultarse(session, p);
                return;
            }
            // VB6 HandleWork: para las skills de trabajo (pesca 18, robar 14, talar 20, mineria 19,
            // FundirMetal 88, domar 12) responde con WriteWorkRequestTarget(skill) — un paquete propio que
            // le pide al cliente marcar el objetivo. Las demas skills no llegan por este handler.
            if (skill is 12 or 14 or 18 or 19 or 20 or 88)
                session.Send(ServerMessages.WorkRequestTarget(skill));
        });
    }

    /// <summary>Vida de "estar oculto" en ticks del game-loop (VB6 <c>IntervaloOculto = 500</c>, Server.ini;
    /// a 40 ms/tick ≈ 20 s). Al vencerse, el jugador se revela solo.</summary>
    public const long OcultoDurationTicks = 500;

    /// <summary>DoOcultarse (VB6 Trabajo.bas:78): tira la suerte del skill Ocultarse (polinomio fiel al original).
    /// Si acierta, queda oculto por <see cref="OcultoDurationTicks"/> — deja de verlo el aggro de NPCs y los demás
    /// jugadores (SetInvisible) — y sube la skill. Si falla, no pasa nada (el VB6 tampoco avisa).</summary>
    private void DoOcultarse(ClientSession session, Player p)
    {
        int skill = p.Skills[GameLimits.SkillOcultar];
        double suerte = (((0.000002 * skill - 0.0002) * skill + 0.0064) * skill + 0.1124) * 100;
        if (_rng.Next(1, 101) <= suerte)
        {
            p.Oculto = true;
            p.OcultoUntilTick = _loop.Tick + OcultoDurationTicks;
            var pkt = ServerMessages.SetInvisible(p.CharIndex, true);
            session.Send(pkt);                                    // el propio cliente se ve semitransparente (VB6 ToPCArea incluye al actor)
            _world.BroadcastToObservers(p.Pos, pkt, p.CharIndex); // los demás dejan de verlo
            RaiseSkill(session, p, GameLimits.SkillOcultar);
            p.Ocultando++;   // crédito del centinela (VB6 Trabajo.bas:103), lo lista /OCULTANDO
        }
    }

    /// <summary>DoRobar (VB6 Trabajo.bas:1471): hurtar ORO a otro jugador. Sólo renegados/criminales roban
    /// (esRene = Status renegado → StatusCriminal en este slice); hace falta energía ≥ 15 (se descuenta). La
    /// tirada usa la tabla de Suerte por skill Robar (a más skill, más chance de que RandomNumber(1,Suerte) < 3).
    /// Con éxito roba oro: el ladrón (clase 5) saca RandomNumber(ELV*50, ELV*100), el resto RandomNumber(1,100),
    /// capado al oro de la víctima; el ladrón RECIBE 5× lo quitado (UpdateGold a ambos) y sube la skill. El robo
    /// de ÍTEM (sólo ladrón, 50%) no se modela — ver fase5/GAPS-CODIGO-MUERTO.md.</summary>
    private void DoRobar(ClientSession session, Player p, Player victima)
    {
        if (p.FactionStatus != Player.StatusCriminal)
        { session.Send(ServerMessages.ConsoleMsg("Para robar debes ser renegado, escribe /RETIRAR para dejar tu ciudadania.", FuenteInfo)); return; }
        if (p.Stamina < 15) { session.Send(ServerMessages.ConsoleMsg("Estas muy cansado para robar.", FuenteInfo)); return; }
        p.Stamina -= 15;
        if (p.Stamina < 0) p.Stamina = 0;
        session.Send(ServerMessages.UpdateSta((short)p.Stamina));   // VB6 Trabajo.bas → WriteUpdateSta

        // Tabla de Suerte por skill (VB6): tope decreciente → a mayor skill, más chance de éxito (res < 3).
        int rs = p.Skills[GameLimits.SkillRobar];
        int suerte = rs <= 10 ? 35 : rs <= 20 ? 30 : rs <= 30 ? 28 : rs <= 40 ? 24 : rs <= 50 ? 22
                   : rs <= 60 ? 20 : rs <= 70 ? 18 : rs <= 80 ? 15 : rs <= 90 ? 10 : rs < 100 ? 7 : 5;
        if (_rng.Next(1, suerte + 1) < 3)   // éxito
        {
            // VB6 DoRobar: la mitad de las veces, y SÓLO si es Ladrón, se roba un ÍTEM en vez de oro.
            if (_rng.Next(1, 51) < 25 && p.Class == 5)
            {
                if (TieneObjetosRobables(victima)) RobarObjeto(session, p, victima);
                else session.Send(ServerMessages.ConsoleMsg($"{victima.Name} no tiene objetos.", FuenteInfo));
                RaiseSkill(session, p, GameLimits.SkillRobar);
                return;
            }
            if (victima.Gold > 0)
            {
                bool ladron = p.Class == 5;   // eClass.ladron
                int n = ladron ? _rng.Next(p.Level * 50, p.Level * 100 + 1) : _rng.Next(1, 101);
                if (n > victima.Gold) n = victima.Gold;
                victima.Gold -= n;
                n *= 5;                                                   // VB6: el ladrón recibe 5× lo quitado
                p.Gold = (int)Math.Min((long)p.Gold + n, MaxOro);         // VB6 Trabajo.bas:1591, sobre el TOTAL
                session.Send(ServerMessages.ConsoleMsg($"Le has robado {n} monedas de oro a {victima.Name}", FuenteInfo));
                session.Send(ServerMessages.UpdateGold(p.Gold));
                victima.Connection?.Send(ServerMessages.UpdateGold(victima.Gold));
            }
            else
            {
                session.Send(ServerMessages.ConsoleMsg($"{victima.Name} no tiene oro.", FuenteInfo));
            }
            RaiseSkill(session, p, GameLimits.SkillRobar);
        }
        else
        {
            session.Send(ServerMessages.ConsoleMsg("¡No has logrado robar nada!", FuenteInfo));
            victima.Connection?.Send(ServerMessages.ConsoleMsg($"¡{p.Name} ha intentado robarte!", FuenteInfo));
        }
    }

    /// <summary>VB6 <c>ObjEsRobable</c> (Trabajo.bas:1638): qué ítem se puede hurtar. No se roban llaves,
    /// monturas, barcos ni el equipo de NOVATO —los cuatro salen del mismo <c>If</c> de
    /// <c>TieneObjetosRobables</c> (InvUsuario.bas:52)—, ni nada que la víctima tenga EQUIPADO. Los gates
    /// faccionarios del original (<c>Real</c>/<c>Caos</c>/<c>Milicia</c>/<c>Shop</c>) no se replican porque son
    /// vacuos en este install: ningún objeto del obj.dat trae esos campos.
    ///
    /// El de novato SÍ vuelve a tener efecto: <see cref="Data.ObjInfo.IsNewbie"/> ya no depende de la clave
    /// muerta sino del nombre.</summary>
    private bool ObjEsRobable(Player victima, int slot)
    {
        var inv = victima.Inventory[slot];
        if (inv.ObjIndex <= 0 || inv.Amount <= 0 || inv.Equipped) return false;
        var o = _data.Obj(inv.ObjIndex);
        return o is not null && !o.IsKey && !o.IsMount && !o.IsBoat && !o.IsNewbie;
    }

    /// <summary>¿La víctima lleva algo hurtable? (VB6 TieneObjetosRobables).</summary>
    private bool TieneObjetosRobables(Player victima)
    {
        for (int i = 1; i <= GameLimits.MaxInventorySlots; i++)
            if (ObjEsRobable(victima, i)) return true;
        return false;
    }

    /// <summary>VB6 <c>RobarObjeto</c> (Trabajo.bas:1661): hurta un ítem del inventario de la víctima. Recorre los
    /// slots empezando por el principio o por el final (moneda al aire) y por cada candidato robable tira un 30%
    /// para quedárselo; se lleva entre el 5% y el 10% del stack, mínimo 1. Si no le entra en el inventario, cae al
    /// piso. Los mensajes distinguen al Ladrón ("Has robado") del resto ("Has hurtado").
    ///
    /// DIVERGENCIA DELIBERADA, del mismo tipo que la invocación (§4): en el VB6 esta función NUNCA roba nada. La
    /// línea <c>If ObjData(...).OBJType = otbebidas Or eOBJType.otUseOnce Then Exit Sub</c> se evalúa, por la
    /// precedencia de VB6, como <c>(tipo = otbebidas) Or (otUseOnce)</c>; y <c>otUseOnce</c> vale 1, o sea SIEMPRE
    /// verdadero, así que sale antes de mover el ítem. Todo el algoritmo está escrito —selección de slot,
    /// cantidades, mensajes— y lo desactiva ese único bug de precedencia, así que se porta salteando esa línea:
    /// es gameplay que el original perdió, no protocolo inventado.</summary>
    private void RobarObjeto(ClientSession session, Player p, Player victima)
    {
        var orden = _rng.Next(1, 13) < 6
            ? Enumerable.Range(1, GameLimits.MaxInventorySlots)
            : Enumerable.Range(1, GameLimits.MaxInventorySlots).Reverse();
        int slot = 0;
        foreach (int i in orden)
            if (ObjEsRobable(victima, i) && _rng.Next(1, 11) < 4) { slot = i; break; }
        if (slot == 0) return;   // ninguna tirada salió: el original tampoco avisa

        var vinv = victima.Inventory[slot];
        int amount = vinv.Amount;
        // `RandomNumber(ObjAmount * 0.05, ObjAmount * 0.1)` con los dos parámetros declarados `As Long`
        // (Matematicas.bas:71): los extremos se REDONDEAN al pasarlos, no se truncan. Con 30 unidades el
        // original roba entre 2 y 3, y truncando el piso quedaba en 1.
        int piso = (int)Matematicas.AEntero(amount * 0.05), techo = (int)Matematicas.AEntero(amount * 0.1);
        int num = Math.Max(1, _rng.Next(piso, techo + 1));
        if (num > amount) num = amount;
        short objIndex = vinv.ObjIndex;

        vinv.Amount -= (short)num;                     // sacárselo a la víctima
        if (vinv.Amount <= 0) vinv = default;
        victima.Inventory[slot] = vinv;
        victima.Connection?.Send(ServerMessages.ChangeInventorySlot((byte)slot, vinv.ObjIndex, vinv.Amount,
            vinv.Equipped, 0f, vinv.IsEmpty ? (byte)0 : (byte)1));

        int destino = FindSlotFor(p, objIndex, num);   // dárselo al ladrón; si no entra, al piso
        if (destino == 0)
        {
            // Buscar un tile LIBRE (VB6 TirarItemAlPiso → Tilelibre): escribir directo en p.Pos pisaría lo que ya
            // hubiera en el suelo — destruyendo el stack ajeno y, si el ladrón está en el vano de una puerta
            // abierta, la propia puerta (queda imposible de cerrar y el cliente la sigue dibujando).
            if (FreeGroundNear(_world, p.Pos) is { } spot)
            {
                _world.SetGround(spot, objIndex, (short)num);
                _world.BroadcastToObservers(spot, ServerMessages.ObjectCreate(spot.X, spot.Y, objIndex, (short)num));
            }
            // Sin ningún tile libre el ítem se pierde, igual que el `NuevaPos = 0` del original.
        }
        else
        {
            var ladronInv = p.Inventory[destino];
            ladronInv.ObjIndex = objIndex;
            ladronInv.Amount = (short)Math.Min(GameLimits.MaxInventoryObjs, ladronInv.Amount + num);
            p.Inventory[destino] = ladronInv;
            session.Send(ServerMessages.ChangeInventorySlot((byte)destino, ladronInv.ObjIndex, ladronInv.Amount,
                ladronInv.Equipped, 0f, 1));
        }
        string nombre = _data.Obj(objIndex)?.Name ?? "objeto";
        session.Send(ServerMessages.ConsoleMsg($"{(p.Class == 5 ? "Has robado" : "Has hurtado")} {num} {nombre}", 3));
    }

    /// <summary>Minerales crudos que hacen falta por lingote (VB6 MineralesParaLingote, Trabajo.bas:800):
    /// Hierro 14, Plata 20, Oro 35. Cualquier otro no es fundible (10000 = inalcanzable).</summary>
    private int MineralesParaLingote(int oreObjIndex) => oreObjIndex switch
    {
        GameLimits.OreIronObj => 14,
        GameLimits.OreSilverObj => 20,
        GameLimits.OreGoldObj => 35,
        _ => 10000,
    };

    /// <summary>DoFundir (VB6 FundirMineral + DoLingotes, Trabajo.bas:211/825): funde mineral crudo en lingotes.
    /// Gate de skill: el MinSkill del mineral debe ser ≤ Minería / ModFundicion(clase) (Guerrero ÷1, resto ÷3,
    /// Trabajo.bas:948). Cantidad por tanda: CantidadItems = max(1, (nivel-4)/5), acotada a lo que alcance el
    /// stock; consume MineralesParaLingote·CantidadItems minerales y entrega CantidadItems lingotes (LingoteIndex).
    ///
    /// El VB6 funde el mineral del TargetObjInvSlot (el que el cliente seleccionó); acá se funde el PRIMER mineral
    /// crudo del inventario — equivalente cuando el jugador lleva un solo tipo, divergencia acotada si lleva varios.
    /// Tampoco se modela el "trabajo continuo" (flag Lingoteando + DoTrabajar): un WorkLeftClick = una tanda, igual
    /// que la minería/tala/pesca de este slice.</summary>
    private void DoFundir(ClientSession session, Player p)
    {
        int slot = 0; ObjInfo? mineral = null;
        // PRIMERO, EL QUE ELIGIÓ: la ranura que dejó anotada al usar el mineral (VB6 flags.Lingoteando). Recién
        // si esa ranura ya no sirve —la gastó, la movió— se busca en el inventario, que es lo único que había
        // antes: con hierro y oro encima, elegía el server y no el jugador.
        if (p.Lingoteando >= 1 && p.Lingoteando <= GameLimits.MaxInventorySlots)
        {
            var elegido = p.Inventory[p.Lingoteando];
            var o = elegido.ObjIndex > 0 && elegido.Amount > 0 ? _data.Obj(elegido.ObjIndex) : null;
            if (o is not null && o.IsMineral) { slot = p.Lingoteando; mineral = o; }
        }
        for (int i = 1; slot == 0 && i <= GameLimits.MaxInventorySlots; i++)
        {
            var inv = p.Inventory[i];
            if (inv.ObjIndex <= 0 || inv.Amount <= 0) continue;
            var o = _data.Obj(inv.ObjIndex);
            if (o is not null && o.IsMineral) { slot = i; mineral = o; }
        }
        if (mineral is null) { session.Send(ServerMessages.ConsoleMsg("No tienes minerales para fundir.", FuenteInfo)); return; }

        int modFundicion = p.Class == 3 ? 1 : 3;   // eClass.Guerrero=3 funde con menos skill (÷1); el resto ÷3
        if (mineral.MinSkill > p.Skills[GameLimits.SkillMineria] / modFundicion)
        { session.Send(ServerMessages.LocaleMsg(194)); return; }   // "No tenés conocimientos de minería suficientes"

        int perLingote = MineralesParaLingote(mineral.Index);
        int cantidad = Math.Max(1, (p.Level - 4) / 5);             // VB6 CantidadItems = MaximoInt(1, (ELV-4)/5)
        var slotInv = p.Inventory[slot];
        while (cantidad > 0 && slotInv.Amount < perLingote * cantidad) cantidad--;
        if (cantidad <= 0 || slotInv.Amount < perLingote) { session.Send(ServerMessages.LocaleMsg(206)); return; }   // "No tenés suficientes materiales"

        // Buscar el destino del lingote ANTES de consumir el mineral: si no hay espacio, se avisa sin tocar el
        // stock (el VB6 tira el lingote al piso, pero acá preferimos no perder material silenciosamente).
        int lingSlot = FindSlotFor(p, (short)mineral.LingoteIndex, cantidad);
        if (lingSlot == 0) { session.Send(ServerMessages.ConsoleMsg("No tienes espacio para los lingotes.", FuenteInfo)); return; }

        slotInv.Amount -= (short)(perLingote * cantidad);          // consumir minerales
        if (slotInv.Amount < 1) slotInv = default;
        p.Inventory[slot] = slotInv;
        session.Send(ServerMessages.ChangeInventorySlot((byte)slot, slotInv.ObjIndex, slotInv.Amount, slotInv.Equipped, 0f, slotInv.IsEmpty ? (byte)0 : (byte)1));

        var ling = p.Inventory[lingSlot];
        ling.ObjIndex = (short)mineral.LingoteIndex;
        ling.Amount = (short)Math.Min(GameLimits.MaxInventoryObjs, ling.Amount + cantidad);
        p.Inventory[lingSlot] = ling;
        session.Send(ServerMessages.ChangeInventorySlot((byte)lingSlot, ling.ObjIndex, ling.Amount, ling.Equipped, 0f, 1));
        session.Send(ServerMessages.LocaleMsg(207, cantidad.ToString()));   // "¡Has obtenido #1 lingotes!"
        // OJO: el VB6 DoLingotes (Trabajo.bas:825-884) NO sube skill al fundir — la Minería sólo sube al MINAR
        // (DoMineria, ya replicado en el gather). Subirla también acá sería una segunda fuente por unidad de mineral,
        // la misma canilla de skill que se cerró en la magia (tarea #71). Por eso NO se llama RaiseSkill acá.
        // El CRÉDITO del centinela sí sube: DoLingotes/DoFundir lo hacen (Trabajo.bas:880/939) aunque no suban skill.
        p.Trabajando++;
    }

    /// <summary>Revela a un jugador oculto (VB6 Protocol.bas:1387/1719, SistemaCombate.bas:899): caminar, atacar o
    /// castear rompe el ocultamiento y lo vuelve visible (LocaleMsg 307). No toca la invisibilidad por hechizo/GM
    /// (si <c>Invisible</c> sigue activo, no se emite SetInvisible: el jugador continúa oculto a la vista).</summary>
    private void RevealHidden(Player p)
    {
        if (!p.Oculto) return;
        p.Oculto = false;
        p.OcultoUntilTick = 0;
        if (!p.Invisible)
        {
            var pkt = ServerMessages.SetInvisible(p.CharIndex, false);
            p.Connection?.Send(pkt);
            _world.BroadcastToObservers(p.Pos, pkt, p.CharIndex);
            p.Connection?.Send(ServerMessages.LocaleMsg(307));   // "Has vuelto a ser visible."
        }
    }

    /// <summary>Como <see cref="RevealHidden"/> pero además rompe la invisibilidad POR HECHIZO (no la permanente
    /// de GM, InvisibleUntilTick==0): atacar a una criatura revela AMBOS estados (VB6 UsuarioAtacaNpc,
    /// SistemaCombate.bas:898-904). Se usa en los golpes a NPC (melee y arco).</summary>
    private void RevealOnAttack(Player p)
    {
        RevealHidden(p);
        if (p.Invisible && p.InvisibleUntilTick != 0)   // invis por hechizo (la de GM es permanente, InvisibleUntilTick==0)
        {
            p.Invisible = false;
            p.InvisibleUntilTick = 0;
            var pkt = ServerMessages.SetInvisible(p.CharIndex, false);
            p.Connection?.Send(pkt);
            _world.BroadcastToObservers(p.Pos, pkt, p.CharIndex);
            p.Connection?.Send(ServerMessages.LocaleMsg(307));
        }
    }

    /// <summary>EquiparSkin (id 30): Byte(Equipo) + Byte(BackOrNext). VB6 HandleEquiparSkin (Protocol.bas:3176)
    /// llama EquipaSkin (General.bas:1481) — que a un NO-donador lo rechaza con WriteLocaleMsg(377) sin tocar el
    /// body — y LUEGO SIEMPRE ChangeUserCharTodo (3197), un re-broadcast de apariencia (CharacterChange) al
    /// área incluido el actor, haya cambiado el skin o no. El slice no modela donadores (todos caen en el gate
    /// 377) ni el ciclado de body-graphics del catálogo TieneSkin/CantidadSkin, pero SÍ replica el par
    /// observable: LocaleMsg(377) + CharacterChange con la apariencia actual (cierra el último DATA del barrido).</summary>
    private void HandleEquiparSkin(ClientSession session, ByteQueueReader r)
    {
        r.ReadByte(); r.ReadByte(); // Byte(Equipo) + Byte(BackOrNext)
        _loop.Enqueue(() =>
        {
            var p = session.Player;
            if (p is null) return;
            session.Send(ServerMessages.LocaleMsg(377));                                 // VB6 EquipaSkin: gate no-donador
            _world.BroadcastToObservers(p.Pos, ServerMessages.CharacterChange(p));       // VB6 ChangeUserCharTodo: re-broadcast de apariencia (incluye al actor)
        });
    }

    /// <summary>
    /// Packets_Correo (id 96): index byte + slot byte. Semántica del original
    /// (HandlePacketsCorreo, Protocol.bas:16002 → mod_Correos.bas):
    /// <list type="bullet">
    /// <item>1 = borrar la carta del slot y compactar el buzón (<c>ResetCorreos</c>). Slot 0 no hace nada.</item>
    /// <item>2 = marcarla leída (<c>Leida = 1</c>). Slot 0 no hace nada.</item>
    /// <item>3 = retirar el adjunto al inventario (<c>RetirarItemCorreo</c>); si no entra, va al piso.</item>
    /// <item>4 = nada (el original tiene el Case vacío).</item>
    /// </list>
    /// El buzón se abre haciendo doble clic en el NPC de correo, que responde con
    /// <see cref="ServerMessages.CorreoList"/> + AbrirFormularios(6) (Acciones.bas:617).
    ///
    /// Hasta acá el port usaba AUTO-ENTREGA (volcaba el buzón entero al inventario) porque no tenía el paquete
    /// <c>CorreoList</c> ni el flag Leída. Con el emisor portado ya no hace falta la simplificación: el
    /// jugador ve su bandeja, elige qué carta abrir y saca cada adjunto a mano, como en el original.
    /// </summary>
    private void HandlePacketsCorreo(ClientSession session, ByteQueueReader r)
    {
        byte sub = r.ReadByte();
        byte slot = r.ReadByte();
        _loop.Enqueue(() =>
        {
            var p = session.Player;
            if (p is null) return;
            var box = _world.PeekMail(p.Name);
            // Los tres subcomandos indexan por slot 1..N. El original sale sin hacer nada con slot 0 y no
            // valida el techo (leería basura del array fijo); acá se acota a lo que hay.
            if (slot < 1 || slot > box.Count) return;
            switch (sub)
            {
                case 1:   // ResetCorreos: borrar y compactar
                    box.RemoveAt(slot - 1);
                    _world.DropMailboxIfEmpty(p.Name);
                    PersistMail();
                    session.Send(ServerMessages.CorreoList(box));   // que la bandeja quede al día
                    break;
                case 2:   // marcar leída
                    box[slot - 1] = box[slot - 1] with { Leida = 1 };
                    PersistMail();
                    break;
                case 3: RetirarItemCorreo(session, p, box, slot - 1); break;
                // case 4: el original lo tiene vacío.
            }
        });
    }

    /// <summary>Saca el adjunto de una carta al inventario (VB6 <c>RetirarItemCorreo</c>, mod_Correos.bas).
    /// Si no entra en el inventario, el original lo TIRA AL PISO en vez de perderlo — la carta queda igual en
    /// la bandeja, sólo sin adjunto.</summary>
    private void RetirarItemCorreo(ClientSession session, Player p, List<MailEntry> box, int i)
    {
        var m = box[i];
        if (m.ObjIndex <= 0 || m.Amount <= 0) return;
        int slot = FindSlotFor(p, m.ObjIndex, m.Amount);
        if (slot == 0)
        {
            // VB6 TirarItemAlPiso → Tilelibre: hay que buscar un tile LIBRE, no escribir sobre p.Pos, que
            // pisaría lo que ya hubiera en el suelo (incluida una puerta abierta, que quedaría imposible de
            // cerrar). Sin ningún tile libre el ítem se pierde, igual que el `NuevaPos = 0` del original.
            if (FreeGroundNear(_world, p.Pos) is { } spot)
            {
                _world.SetGround(spot, m.ObjIndex, m.Amount);
                _world.BroadcastToObservers(spot, ServerMessages.ObjectCreate(spot.X, spot.Y, m.ObjIndex, m.Amount));
                session.Send(ServerMessages.ConsoleMsg("No te entra en el inventario: el adjunto quedó en el piso.", FuenteInfo));
            }
            else return;   // ni piso ni inventario: la carta conserva su adjunto para el próximo intento
        }
        else
        {
            var inv = p.Inventory[slot];
            inv.ObjIndex = m.ObjIndex;
            inv.Amount = (short)Math.Min(GameLimits.MaxInventoryObjs, inv.Amount + m.Amount);
            p.Inventory[slot] = inv;
            session.Send(ServerMessages.ChangeInventorySlot((byte)slot, inv.ObjIndex, inv.Amount, inv.Equipped, 0f, 1));
        }
        box[i] = m with { ObjIndex = 0, Amount = 0 };   // la carta se queda, sin adjunto
        PersistMail();
        session.Send(ServerMessages.CorreoList(box));
    }

    /// <summary>GuildAllianceDetails / GuildPeaceDetails → <c>r_VerPropuesta</c> (modGuilds.bas:1773): el LÍDER
    /// lee el texto de la propuesta que le mandó otro clan antes de aceptarla o rechazarla. Sus cuatro ramas son
    /// exactas y las tres de error van por <c>ConsoleMsg</c> en FONTTYPE_GUILD, no por LocaleMsg:
    /// sin clan → "No eres miembro de ningún clan."; no líder → "No eres el líder de tu clan."; sin propuesta de
    /// ese clan → "No existe la propuesta solicitada."; y con propuesta → el paquete <c>OfferDetails</c>.
    ///
    /// Antes acá se listaban las relaciones ya existentes en un ConsoleMsg inventado, y el <b>nombre del clan</b>
    /// que trae el paquete se leía y se tiraba — así que no había con qué buscar la propuesta.</summary>
    private void HandleGuildRelationDetails(ClientSession session, string kind, string otherGuild)
    {
        _loop.Enqueue(() =>
        {
            var p = session.Player;
            if (p is null) return;
            if (string.IsNullOrEmpty(p.GuildName)) { session.Send(ServerMessages.ConsoleMsg("No eres miembro de ningún clan.", 7)); return; }
            var g = _world.GuildByName(p.GuildName);
            if (g is null) return;
            if (!string.Equals(g.LeaderName, p.Name, StringComparison.OrdinalIgnoreCase))
            { session.Send(ServerMessages.ConsoleMsg("No eres el líder de tu clan.", 7)); return; }
            var offers = kind == "alianza" ? g.AllianceOffers : g.PeaceOffers;
            if (!offers.TryGetValue(otherGuild.Trim(), out var details))
            { session.Send(ServerMessages.ConsoleMsg("No existe la propuesta solicitada.", 7)); return; }
            session.Send(ServerMessages.OfferDetails(details));
        });
    }

    private void HandleGuildOfferList(ClientSession session, bool alliance)
    {
        _loop.Enqueue(() =>
        {
            var p = session.Player;
            if (p is null || string.IsNullOrEmpty(p.GuildName)) return;
            var g = _world.GuildByName(p.GuildName);
            if (g is null) return;
            var offers = alliance ? g.AllianceOffers.Keys : g.PeaceOffers.Keys;
            session.Send(alliance
                ? ServerMessages.AlianceProposalsList(offers)
                : ServerMessages.PeaceProposalsList(offers)); // structured proposal-list window
            session.Send(ServerMessages.ConsoleMsg(
                $"Ofertas de {(alliance ? "alianza" : "paz")} pendientes: {(offers.Count == 0 ? "ninguna" : string.Join(", ", offers))}", FuenteClan));
        });
    }

    /// <summary>RequestGuildLeaderInfo/GuildRequestDetails (id 10/61): reports the caller's clan details.</summary>
    /// <summary>RequestGuildLeaderInfo (10): the leader's management panel — all guilds, this guild's
    /// members, its news, and pending join requests.</summary>
    private void HandleGuildLeaderInfo(ClientSession session)
    {
        _loop.Enqueue(() =>
        {
            var p = session.Player;
            // VB6 modGuilds.bas:1254: si el jugador no tiene clan, responde con la LISTA de clanes.
            if (p is null) return;
            if (string.IsNullOrEmpty(p.GuildName)) { session.Send(ServerMessages.GuildList(_world.AllGuilds.Select(x => x.Name))); return; }
            var g = _world.GuildByName(p.GuildName);
            if (g is null) return;
            // VB6 modGuilds.bas:1258: al que NO es líder se le manda GuildMemberInfo (lista de clanes + los
            // miembros del suyo) y NO el panel de administración. Antes el port le daba el panel completo a
            // cualquier miembro, con las solicitudes de ingreso incluidas.
            if (!string.Equals(g.LeaderName, p.Name, StringComparison.OrdinalIgnoreCase))
            {
                session.Send(ServerMessages.GuildMemberInfo(_world.AllGuilds.Select(gg => gg.Name), g.Members));
                return;
            }
            session.Send(ServerMessages.GuildLeaderInfo(
                _world.AllGuilds.Select(gg => gg.Name), g.Members, g.News ?? "", g.JoinRequests.Keys));
        });
    }

    private void HandleGuildInfo(ClientSession session, string clanPedido = "")
    {
        _loop.Enqueue(() =>
        {
            var p = session.Player;
            if (p is null) return;
            // El clan PEDIDO; si el paquete vino sin nombre se cae al propio, que es el caso que atendía antes.
            string nombre = string.IsNullOrWhiteSpace(clanPedido) ? p.GuildName : clanPedido;
            if (string.IsNullOrEmpty(nombre)) return;   // VB6 GuildRequestDetails sin clan: silencio
            var g = _world.GuildByName(nombre);
            if (g is null) return;
            session.Send(ServerMessages.GuildDetails(g)); // structured clan info window
            session.Send(ServerMessages.ConsoleMsg(
                $"Clan <{g.Name}> — líder: {g.LeaderName}, miembros: {g.Members.Count}, aliados: {g.Allies.Count}, enemigos: {g.Enemies.Count}.", FuenteClan));
        });
    }

    /// <summary>VB6 <c>SendDetallesPersonaje</c> (modGuilds.bas:1978): el LÍDER de un clan pide la ficha de uno
    /// de sus miembros o aspirantes, POR NOMBRE. El port leía el nick del wire y lo tiraba: a cualquiera que
    /// preguntara le contestaba la lista de miembros de su propio clan, así que el paquete no servía para lo
    /// único que existe. Las tres negativas del original son por texto y no mandan ficha.</summary>
    private void HandleGuildMemberInfo(ClientSession session, string nick)
    {
        _loop.Enqueue(() =>
        {
            var p = session.Player;
            if (p is null) return;
            if (string.IsNullOrEmpty(p.GuildName)) { session.Send(ServerMessages.ConsoleMsg("No perteneces a ningún clan.", FuenteInfo)); return; }
            var g = _world.GuildByName(p.GuildName);
            if (g is null) return;
            if (!string.Equals(g.LeaderName, p.Name, StringComparison.OrdinalIgnoreCase))
            { session.Send(ServerMessages.ConsoleMsg("No eres el líder de tu clan.", FuenteInfo)); return; }
            nick = (nick ?? "").Replace("\\", "").Replace("/", "").Replace(".", "").Trim();   // VB6 saca \ / . del nick
            bool esDelClan = g.Members.Any(m => string.Equals(m, nick, StringComparison.OrdinalIgnoreCase))
                             || g.JoinRequests.Keys.Any(a => string.Equals(a, nick, StringComparison.OrdinalIgnoreCase));
            if (!esDelClan)
            { session.Send(ServerMessages.ConsoleMsg("El personaje no es ni aspirante ni miembro del clan.", FuenteInfo)); return; }
            // El original lee la ficha del .chr, así que contesta igual con el personaje DESCONECTADO. Pero
            // ESE LEER ES DISCO, y acá adentro estamos en el hilo del game loop: leer el .chr de un miembro
            // offline —peor con SQLite— frena el mundo entero por una ventana de clan. Si está conectado se
            // contesta en el acto; si no, la lectura se va a otro hilo y vuelve al loop sólo para emitir.
            var enElMundo = _world.PlayerByName(nick);
            if (enElMundo is not null) { session.Send(ServerMessages.CharacterInfo(enElMundo)); return; }
            string pedido = nick;
            _ = Task.Run(() =>
            {
                Player? cargado = null;
                try { if (_db.CharacterExists(pedido)) cargado = _db.LoadCharacter(pedido, 0, ""); }
                catch (Exception ex) { Console.Error.WriteLine($"[clan] ficha de {pedido}: {ex.Message}"); }
                // VB6: si el .chr no está, el original sólo lo deja en el log de errores y no emite nada.
                if (cargado is not null) _loop.Enqueue(() => session.Send(ServerMessages.CharacterInfo(cargado)));
            });
        });
    }

    /// <summary>Las noticias del clan al entrar al mundo — VB6 <c>modGuilds.SendGuildNews</c> (modGuilds.bas:560),
    /// llamado desde ConnectUser (TCP.bas:346). Sale del guild sin clan (<c>If GuildIndex = 0 Then Exit Sub</c>)
    /// y manda SÓLO el paquete estructurado, sin el ConsoleMsg que sí acompaña al pedido explícito. Va directo
    /// (no encolado): SpawnIntoWorld ya corre en el hilo del game loop.</summary>
    private void SendGuildNewsAlEntrar(ClientSession session, Player player)
    {
        if (string.IsNullOrEmpty(player.GuildName)) return;
        var g = _world.GuildByName(player.GuildName);
        if (g is null) return;
        session.Send(ServerMessages.GuildNews(g.News ?? "", g.Enemies, g.Allies));
    }

    private void HandleShowGuildNews(ClientSession session)
    {
        _loop.Enqueue(() =>
        {
            var p = session.Player;
            if (p is null || string.IsNullOrEmpty(p.GuildName)) return;
            var g = _world.GuildByName(p.GuildName);
            if (g is null) return;
            session.Send(ServerMessages.GuildNews(g.News ?? "", g.Enemies, g.Allies)); // structured news window
            session.Send(ServerMessages.ConsoleMsg($"Noticias de <{g.Name}>: {(string.IsNullOrEmpty(g.News) ? "sin novedades." : g.News)}", FuenteClan));
        });
    }

    /// <summary>
    /// Descripción + códex del clan (VB6 ChangeCodexAndDesc, modGuilds.bas:402). Sólo el líder, y NO responde
    /// ningún paquete — el cliente ya tiene el texto que acaba de mandar. Antes se leían los dos strings y se
    /// descartaban, así que editar la ficha del clan no hacía nada.
    /// </summary>
    private void HandleClanCodexUpdate(ClientSession session, string desc, string codex)
    {
        _loop.Enqueue(() =>
        {
            var g = RequireGuildLeader(session, session.Player);
            if (g is null) return;
            g.Description = desc;
            g.Codex = codex;
            PersistGuilds();
        });
    }

    /// <summary>Sitio web del clan (VB6 ActualizarWebSite, modGuilds.bas:383). Igual que el códex: sólo el
    /// líder, guarda y calla. Se ve después en GuildDetails.</summary>
    private void HandleGuildNewWebsite(ClientSession session, string url)
    {
        _loop.Enqueue(() =>
        {
            var g = RequireGuildLeader(session, session.Player);
            if (g is null) return;
            g.Url = url;
            PersistGuilds();
        });
    }

    private void HandleGuildUpdateNews(ClientSession session, ByteQueueReader r)
    {
        string news = r.ReadAsciiString();
        if (Frenado(session, ruidosa: true)) return;
        _loop.Enqueue(() =>
        {
            var g = RequireGuildLeader(session, session.Player);
            if (g is null) return;
            g.News = news;
            PersistGuilds();
            session.Send(ServerMessages.ConsoleMsg("Noticias del clan actualizadas.", FuenteClan));
        });
    }

    private void HandleGuildRejectAlliance(ClientSession session, ByteQueueReader r)
    {
        string name = r.ReadAsciiString().Trim();
        _loop.Enqueue(() =>
        {
            // VB6 HandleGuildRejectAlliance (Protocol.bas:3860): sin clan, r_RechazarPropuestaDeAlianza
            // devuelve 0 con errorStr "No eres miembro de ningún clan" y el handler lo emite como ConsoleMsg.
            var g = RequireGuildLeader(session, session.Player, announceNoGuild: true);
            if (g is null) return;
            g.AllianceOffers.Remove(name);
            session.Send(ServerMessages.ConsoleMsg($"Rechazaste la alianza con <{name}>.", FuenteClan));
        });
    }

    private void HandleGuildRejectPeace(ClientSession session, ByteQueueReader r)
    {
        string name = r.ReadAsciiString().Trim();
        _loop.Enqueue(() =>
        {
            // VB6 HandleGuildRejectPeace (Protocol.bas:3925): sin clan responde ConsoleMsg "No eres miembro de ningún clan".
            var g = RequireGuildLeader(session, session.Player, announceNoGuild: true);
            if (g is null) return;
            g.PeaceOffers.Remove(name);
            session.Send(ServerMessages.ConsoleMsg($"Rechazaste la paz con <{name}>.", FuenteClan));
        });
    }

    /// <summary>Resucitate (id 69) — VB6 HandleResucitate (Protocol.bas:5098). Revivir NO es gratis ni
    /// automático: hay que estar muerto y tener CLICKEADO a un sacerdote (<c>eNPCType.Revividor</c>) a diez
    /// baldosas o menos. Al revivir, el sacerdote lo anuncia sobre su cabeza con el locale 11.
    ///
    /// <para>Acá antes revivía el propio paquete, "como si siempre hubiera un sacerdote", con la nota de que el
    /// slice no modelaba el target ni al Revividor. Las dos cosas dejaron de ser ciertas —<c>Player.TargetNpc</c>
    /// llegó con <c>LookatTile</c> y <c>npc.IsReviver</c> con los roles de NPC— y mientras tanto el atajo dejaba
    /// la muerte sin costo: cualquier cadáver se levantaba solo, en cualquier lado. Es el gate que hace que
    /// morir signifique volver caminando hasta un templo.</para></summary>
    private void HandleResucitate(ClientSession session)
    {
        _loop.Enqueue(() =>
        {
            var p = session.Player;
            if (p is null) return;
            var npc = TargetNpcDe(p);
            // Sin nada clickeado avisa; con algo clickeado que no sirva CALLA. Son dos ramas distintas del
            // original (:5110 avisa, :5116 hace Exit Sub pelado) y se replican tal cual.
            if (npc is null) { session.Send(ServerMessages.LocaleMsg(22)); return; }
            if (!npc.IsReviver || p.IsAlive) return;
            if (DistanciaVB6(p.Pos, npc.Pos) > 10) { session.Send(ServerMessages.LocaleMsg(8)); return; }
            RevivePlayer(_world, _data, p);
            _world.BroadcastToObservers(p.Pos, ServerMessages.ChatOverHeadLocale(npc.CharIndex, 11, 1));
        });
    }

    /// <summary>RequestMiniStats (id 13): sends the real MiniStats packet (id 61) with the player's own
    /// kill counters + class/race/gender/faction, plus a human-readable console line for good measure.</summary>
    private void HandleRequestMiniStats(ClientSession session)
    {
        if (Frenado(session, ruidosa: false)) return;
        _loop.Enqueue(() =>
        {
            var p = session.Player;
            if (p is null) return;
            // VB6 HandleRequestMiniStats (Protocol.bas:1927) replies with WriteMiniStats and nothing else:
            // class/race/level/kills already ride in that packet, so the ConsoleMsg echo is spurious.
            session.Send(ServerMessages.MiniStats(p));
        });
    }

    /// <summary>MoveBank (id 39): Boolean(upwards) + Byte(slot). HandleMoveBank (Protocol.bas:3651) reads
    /// a direction flag + the slot and swaps it with its neighbour (up → slot-1, down → slot+1).</summary>
    private void HandleMoveBank(ClientSession session, ByteQueueReader r)
    {
        bool upwards = r.ReadBoolean();
        byte slot = r.ReadByte();
        _loop.Enqueue(() =>
        {
            var p = session.Player;
            if (p is null) return;
            int other = upwards ? slot - 1 : slot + 1;
            if (slot < 1 || slot > GameLimits.MaxBankSlots || other < 1 || other > GameLimits.MaxBankSlots) return;
            (p.BankInventory[slot], p.BankInventory[other]) = (p.BankInventory[other], p.BankInventory[slot]);
            session.Send(ServerMessages.ConsoleMsg("Reordenaste la bóveda.", FuenteInfo));
        });
    }

    /// <summary>Enlist (id 73): enlist in the faction army at a faction NPC (slice: message only).</summary>
    private void HandleEnlist(ClientSession session)
    {
        _loop.Enqueue(() =>
        {
            var p = session.Player;
            if (p is null) return;
            // Mismo contrato que HandleReward (Protocol.bas:5300): el enlistador es el que CLICKEASTE. El 22 es
            // sólo el "no clickeaste a nadie"; con un target que no es de facciones —o estando muerto— el
            // original sale mudo (:5306). Se resolvía por la baldosa encarada y contestaba 22 en los tres casos.
            if (p.TargetNpc < 1) { session.Send(ServerMessages.LocaleMsg(22)); return; }
            var npc = _world.NpcByIndex(p.TargetNpc);
            if (npc is null || npc.FactionEnlist == 0 || !p.IsAlive) return;
            if (DistanciaVB6(p.Pos, npc.Pos) > 4) { session.Send(ServerMessages.LocaleMsg(8)); return; }   // VB6 :5310

            // El NPC te habla A VOS: las frases de ModFacciones.bas salen por `WriteChatOverHead(UserIndex, ...)`,
            // que es al socket del jugador. Acá se emitían al área y las escuchaba media ciudad.
            void Say(string t) => session.Send(ServerMessages.ChatOverHead(t, npc.CharIndex));

            if (npc.FactionEnlist == Player.StatusArmada)   // Armada Real (VB6 EnlistarArmadaReal:301)
            {
                if (p.IsArmada) { Say("Ya perteneces a las tropas reales, ve a combatir enemigos."); return; }
                if (!p.IsCitizen) { Say("No aceptamos seguidores de facciones enemigas, ¡largo de aquí!"); return; }
                int kills = p.KillsEnemigosArmada;
                if (kills < 15) { Say($"Para unirte a nuestras fuerzas debes matar al menos 15 enemigos, sólo has matado {kills}."); return; }
                if (p.Level < 25) { Say("Para unirte a nuestras fuerzas debes ser al menos nivel 25."); return; }
                p.CambiarFaccion(Player.StatusArmada);
            }
            else if (npc.FactionEnlist == Player.StatusMilicia)   // Milicia Republicana (VB6 EnlistarMilicia:222)
            {
                if (p.IsMilicia) { Say("Ya perteneces a las tropas milicianas, ve a combatir enemigos."); return; }
                if (!p.IsRepublicano) { Say("No aceptamos seguidores de facciones enemigas, ¡largo de aquí!"); return; }
                int kills = p.KillsEnemigosMilicia;
                if (kills < 15) { Say($"Para unirte a nuestras fuerzas debes matar al menos 15 enemigos, sólo has matado {kills}."); return; }
                if (p.Level < 25) { Say("Para unirte a nuestras fuerzas debes ser al menos nivel 25."); return; }
                p.CambiarFaccion(Player.StatusMilicia);
            }
            else   // Legión del Caos (VB6 EnlistarCaos:120)
            {
                if (p.IsCaos) { Say("Ya perteneces a la horda del caos, tráeme más almas."); return; }
                if (!(p.FactionStatus == Player.StatusCriminal)) { Say("No aceptamos seguidores de facciones enemigas, ¡largo de aquí!"); return; }
                int kills = p.KillsEnemigosCaos;
                if (kills < 30) { Say($"Para unirte a nuestras fuerzas debes matar al menos 30 enemigos, sólo has matado {kills}."); return; }
                if (p.Level < 40) { Say("Para unirte a nuestras fuerzas debes ser al menos nivel 40."); return; }
                p.CambiarFaccion(Player.StatusCaos);
            }
            p.FactionRank = 1;
            // VB6 ModFacciones.bas:44/98/204: el cambio de bando se difunde al área con CharStatus (charIndex +
            // status), no re-mandando el CharacterChange entero — que además ni siquiera lleva el status.
            _world.BroadcastToObservers(p.Pos, ServerMessages.CharStatus(p.CharIndex, (byte)p.FactionStatus));
            session.Send(ServerMessages.UpdateUserStats(p));
            Say($"¡Bienvenido, {FactionTitle(p)}! Ve a combatir a nuestros enemigos.");
        });
    }

    /// <summary>Convertidor (VB6 EntrarImperial :64 / EntrarRepublica :11, vía Acciones.bas:225): le da la
    /// ciudadanía —imperial o republicana— a un RENEGADO. Exige: no ser ya de ese bando, venir de renegado (no se
    /// admite a nadie de una facción enemiga) y no pertenecer a ningún clan. Todo se avisa por ChatOverHead del NPC.</summary>
    private void DoConvertir(ClientSession session, Player p, Npc npc)
    {
        // Al socket del jugador y no al área: las 32 frases de ModFacciones.bas salen por
        // `WriteChatOverHead(UserIndex, ...)`. Ese archivo no tiene ni un solo ChatOverHead de área.
        void Say(string t) => session.Send(ServerMessages.ChatOverHead(t, npc.CharIndex));
        bool imperial = npc.FactionConvert == Player.StatusCiudadano;

        if (p.FactionStatus == npc.FactionConvert)
        {
            Say(imperial ? "¡No puedo hacerte un ciudadano imperial porque ya lo eres!."
                         : "¡No puedo hacerte republicano porque ya lo eres!.");
            return;
        }
        if (p.FactionStatus != Player.StatusCriminal)   // VB6: `.Faccion.Status <> 1` (sólo se convierte a un renegado)
        {
            Say("No aceptamos otros seguidores de facciones enemigas, ¡largo de aqui");
            return;
        }
        if (!string.IsNullOrEmpty(p.GuildName))
        {
            Say("¡Para realizar esta acción no debes pertenecer a ningún clan!.");
            return;
        }
        // LA CIUDADANÍA CUESTA (VB6 ModFacciones.bas:37-41 y :88-92, `Private Const PERDON As Long = 100000`).
        // Acá se daba GRATIS, que es un agujero de economía: entrar a una facción es lo que abre el ejército,
        // su armadura y su tabla de rangos, y el precio es justamente lo que hace que volverse renegado tenga
        // consecuencia. El gate del original es `Not .Stats.GLD > PERDON`: con exactamente 100.000 NO alcanza,
        // hacen falta 100.001. Se replica tal cual, rareza incluida.
        if (p.Gold <= PrecioDelPerdon)
        { session.Send(ServerMessages.LocaleMsg(383, PrecioDelPerdon.ToString())); return; }
        p.Gold -= PrecioDelPerdon;
        p.CambiarFaccion(npc.FactionConvert);
        // Y el hogar pasa a ser NIX en las dos ramas. No es un descuido de este porte: el original escribe
        // `.Hogar = cNix` también en EntrarRepublica (:42), así que el republicano recién convertido vuelve a
        // la capital imperial. Es raro, pero es lo que hace el juego que estamos copiando.
        p.HomeCity = Cities.Nix;
        // El comentario de acá ya decía "VB6 CharStatus" y se mandaba UpdateTagAndStatus: ahora va el paquete
        // que el original manda de verdad (ModFacciones.bas:44/98).
        _world.BroadcastToObservers(p.Pos, ServerMessages.CharStatus(p.CharIndex, (byte)p.FactionStatus));
        session.Send(ServerMessages.UpdateGold(p.Gold));   // VB6 WriteUpdateGold — el oro que acaba de pagar
        session.Send(ServerMessages.UpdateUserStats(p));
        Say(imperial ? "Bienvenido al Imperio." : "Bienvenido a la República.");
    }

    /// <summary>Lo que cobra el Convertidor por la ciudadanía (VB6 <c>PERDON</c>, ModFacciones.bas:10).</summary>
    private const int PrecioDelPerdon = 100_000;

    // (Acá vivía `NpcInFront`, el helper que resolvía el NPC por la baldosa ENCARADA. Lo usaban Enlist y
    // Reward, y su propio comentario ya admitía que el original usa `flags.TargetNPC`; HandleTrain, que el
    // comentario nombraba como parte del patrón, hacía rato que leía el target de verdad. Sin usuarios, se fue.)

    /// <summary>Título por rango, textual del VB6 (TituloReal :719, TituloCaos :692, TituloMilicia :745).</summary>
    private static string FactionTitle(Player p)
    {
        if (p.IsArmada) return p.FactionRank switch
        {
            <= 1 => "Legionario", 2 => "Soldado Real", 3 => "Teniente Real", 4 => "Comandante Real",
            5 => "General Real", 6 => "Elite Real", 7 => "Guardian del Bien", 8 => "Caballero Imperial",
            9 => "Justiciero", _ => "Guardia Imperial",
        };
        if (p.IsCaos) return p.FactionRank switch
        {
            <= 1 => "Miembro de las Hordas", 2 => "Guerrero del Caos", 3 => "Teniente del Caos",
            4 => "Comandante del Caos", 5 => "General del Caos", 6 => "Elite del Caos",
            7 => "Asolador de las Sombras", 8 => "Caballero Negro", 9 => "Emisario de las Sombras",
            _ => "Avatar del Apocalipsis",
        };
        if (p.IsMilicia) return p.FactionRank switch    // la Milicia topa en rango 7
        {
            <= 1 => "Milicia de Reserva", 2 => "Miliciano", 3 => "Miliciano Elite",
            4 => "Soldado de la República", 5 => "Soldado Raso", 6 => "Soldado Elite",
            _ => "Comandante de la República",
        };
        return "";
    }

    /// <summary>Kills enemigos ACUMULADOS necesarios para pasar del rango dado al siguiente. El VB6 usa DOS tablas
    /// distintas: matadosArmada (ModFacciones.bas:767) sube de a 5 → 20,25,30,…,60; matadosCaos (:791) sube de a
    /// 10 → 20,30,40,…,100. Ambos ejércitos topan en rango 10 (RecompensaArmadaReal:409 / RecompensaCaos:532).</summary>
    private static int MatadosParaRango(Player p) => p.IsCaos
        ? 20 + Math.Max(0, p.FactionRank - 1) * 10   // matadosCaos: +10 por rango
        : 20 + Math.Max(0, p.FactionRank - 1) * 5;   // matadosArmada: +5 por rango (lo usa también la Milicia)

    /// <summary>Rango máximo por facción: la Armada y el Caos topan en 10 (RecompensaArmadaReal:409 /
    /// RecompensaCaos:532); la Milicia, en 7 (RecompensaMilicia:480).</summary>
    private static int MaxFactionRank(Player p) => p.IsMilicia ? 7 : 10;

    /// <summary>Enemigos que le cuentan a la facción del jugador para enlistar/ascender.</summary>
    private static int KillsForFaction(Player p) =>
        p.IsArmada ? p.KillsEnemigosArmada : p.IsMilicia ? p.KillsEnemigosMilicia : p.KillsEnemigosCaos;

    /// <summary>Rango a partir del cual el VB6 considera "segunda jerarquía" y entrega la armadura faccionaria
    /// (Armada/Caos). La Milicia la entrega antes, desde el rango 4 (RecompensaMilicia:497).</summary>
    private const int SecondHierarchyRank = 6;
    private const int MiliciaArmorRank = 4;

    /// <summary>Desplazamiento del ítem por clase para la armadura faccionaria de 2ª jerarquía (VB6
    /// RecompensaArmadaReal:427 / RecompensaCaos:548): base 1566 (Armada) / 1522 (Caos), +2 por clase en el orden
    /// Clérigo, Mago, Guerrero, Asesino, Bardo, Druida, Gladiador, Paladín, Cazador, Mercenario.
    /// Las clases que el VB6 no lista (ladrón, trabajadores y **nigromante**) no reciben nada. Lo del nigromante es
    /// un BUG del original —el último Case repite `eClass.Bardo` en vez de `eClass.Nigromante`, así que su armadura
    /// (1586/1542) queda inalcanzable—, y se porta tal cual: manda la paridad.</summary>
    private static readonly IReadOnlyDictionary<byte, int> FactionArmorOffset = new Dictionary<byte, int>
    {
        [1] = 0,    // Clérigo
        [2] = 2,    // Mago
        [3] = 4,    // Guerrero
        [4] = 6,    // Asesino
        [6] = 8,    // Bardo
        [7] = 10,   // Druida
        [8] = 12,   // Gladiador
        [9] = 14,   // Paladín
        [10] = 16,  // Cazador
        [17] = 18,  // Mercenario
    };

    private const byte RaceGnomo = 4, RaceEnano = 5;   // Declares.bas eRaza (las razas "bajas" usan la variante +1)

    /// <summary>Clases mágicas y guerreras según los agrupa la MILICIA (VB6 RecompensaMilicia:499-503): a
    /// diferencia de Armada/Caos —que dan una armadura por clase— la República reparte sólo dos, la de los magos
    /// (clérigo, mago, bardo, druida, nigromante → 1592) y la de los combatientes (gladiador, guerrero, cazador,
    /// mercenario, paladín, asesino → 1590).</summary>
    private static readonly HashSet<byte> MiliciaMagicClasses = new() { 1, 2, 6, 7, 18 };
    private static readonly HashSet<byte> MiliciaFighterClasses = new() { 8, 3, 10, 17, 9, 4 };

    /// <summary>Entrega la armadura faccionaria (VB6): la del bando y la clase, con la variante "Bajos" (+1) si la
    /// raza es enano o gnomo. Si ya la tiene no le da otra; si no entra en el inventario, cae al piso (VB6
    /// TirarItemAlPiso).</summary>
    private void GrantFactionArmor(ClientSession session, Player p)
    {
        int bajos = p.Race is RaceGnomo or RaceEnano ? 1 : 0;
        short obj;
        if (p.IsMilicia)
        {
            if (MiliciaMagicClasses.Contains(p.Class)) obj = (short)(1592 + bajos);
            else if (MiliciaFighterClasses.Contains(p.Class)) obj = (short)(1590 + bajos);
            else return;                                                        // clase sin armadura de Milicia
        }
        else
        {
            if (!FactionArmorOffset.TryGetValue(p.Class, out var offset)) return;   // clase sin armadura faccionaria
            obj = (short)((p.IsArmada ? 1566 : 1522) + offset + bajos);
        }
        for (int i = 1; i <= GameLimits.MaxInventorySlots; i++)                 // VB6 TieneObjetos: ya la tiene → nada
            if (!p.Inventory[i].IsEmpty && p.Inventory[i].ObjIndex == obj) return;

        int slot = FindSlotFor(p, obj);
        if (slot == 0)                                                          // sin lugar → al piso
        {
            if (FindFreeGround(p.Pos) is { } spot)
            {
                _world.SetGround(spot, obj, 1);
                _world.BroadcastToObservers(spot, ServerMessages.ObjectCreate(spot.X, spot.Y, obj, 1));
            }
            return;
        }
        var inv = p.Inventory[slot];
        inv.ObjIndex = obj;
        inv.Amount = (short)Math.Min(GameLimits.MaxInventoryObjs, inv.Amount + 1);
        p.Inventory[slot] = inv;
        session.Send(ServerMessages.ChangeInventorySlot((byte)slot, inv.ObjIndex, inv.Amount, inv.Equipped, 0f, 1));
    }

    /// <summary>Cabeza al azar para las pociones de cambio de cara y de sexo. La tabla —que NO es la de crear
    /// personajes— vive en <see cref="Equipment.HeadRangeDePocion"/> con el porqué.</summary>
    private short CabezaAlAzar(byte race, byte gender)
    {
        var (lo, hi) = Equipment.HeadRangeDePocion(race, gender);
        return (short)_rng.Next(lo, hi + 1);       // RandomNumber del VB6 incluye los dos extremos
    }

    /// <summary>¿Los dos están peleando DENTRO de la arena? (VB6 <c>TriggerZonaPelea</c> = <c>TRIGGER6_PERMITE</c>,
    /// SistemaCombate.bas:2065: el permiso sale sólo si el trigger del atacante y el de la víctima son AMBOS
    /// ZONAPELEA; uno adentro y otro afuera da PROHIBE.) Un duelo de arena no tiene ninguna consecuencia del
    /// mundo real: no vuelve criminal a nadie, no cuenta frags y no traba las salidas del mapa.</summary>
    private bool DueloDeArena(Player a, Player b) =>
        _data.IsArena(a.Pos.Map, a.Pos.X, a.Pos.Y) && _data.IsArena(b.Pos.Map, b.Pos.X, b.Pos.Y);

    /// <summary>¿Este jugador puede agredir a este otro? (VB6 <c>PuedeAtacar</c>, SistemaCombate.bas:1504.)
    ///
    /// <para>Es LA compuerta del PvP y no estaba portada: el melee sólo miraba que la víctima no fuera un
    /// cadáver. O sea que se podía matar en el medio de Ullathorpe, a un compañero de clan y al propio cónyuge.
    /// El original la llama desde <c>UsuarioAtacaUsuario</c> (:1183) y desde las nueve ramas de daño de
    /// <c>modHechizos</c>, así que acá gatea las tres vías: golpe, disparo y hechizo ofensivo.</para>
    ///
    /// <para>El ORDEN es el del original, y no es cosmético: las reglas de clan y de facción van ANTES que la
    /// arena, así que dos ciudadanos siguen sin poder pelear aunque los dos estén parados en el trigger 6.</para>
    ///
    /// <para>Lo que NO se porta, porque en esta variante está comentado en la fuente: el chequeo de ZONASEGURA
    /// (:1660-1668). En el original, pelear parado en un tile seguro SE PUEDE; lo único que ZONASEGURA impide
    /// de verdad es invocar. Y la lista de mapas cableada del segundo <c>If</c> también está comentada: la
    /// protección de las ciudades sale del flag <c>Pk</c> del header del mapa, que da los mismos 82 mapas.</para>
    ///
    /// <para>Devuelve false y YA MANDÓ el rechazo que corresponda; el llamador sólo tiene que salir.</para></summary>
    private bool PuedeAtacar(ClientSession session, Player p, Player victima)
    {
        if (!p.IsAlive) { session.Send(ServerMessages.LocaleMsg(77)); return false; }
        // Mapa sin PvP: los 82 mapas con Pk=False del header (1 Ullathorpe, 34 Nix, 62 Lindos…).
        if (!_data.IsPkMap(p.Pos.Map)) { session.Send(ServerMessages.LocaleMsg(100)); return false; }
        // Al cónyuge no se le pega.
        if (!string.IsNullOrEmpty(p.SpouseName)
            && string.Equals(victima.Name, p.SpouseName, StringComparison.OrdinalIgnoreCase))
        { session.Send(ServerMessages.LocaleMsg(298)); return false; }
        if (!victima.IsAlive) { session.Send(ServerMessages.LocaleMsg(7)); return false; }
        // Compañeros de clan: el original sale MUDO acá (su WriteMensajes está comentado).
        if (!string.IsNullOrEmpty(victima.GuildName)
            && string.Equals(victima.GuildName, p.GuildName, StringComparison.OrdinalIgnoreCase)) return false;
        // Los ocho pares de facción que el original prohíbe (LocaleMsg 97). Ciudadano+Ciudadano está entre
        // ellos: en esta variante dos civiles NO se pueden pelear, hay que hacerse criminal o enlistarse en
        // bandos opuestos. Es el gate que más cambia el juego y es el que la fuente dice, textual.
        bool bloqueoDeFaccion =
               (victima.IsRepublicano && p.IsRepublicano) || (victima.IsMilicia && p.IsMilicia)
            || (victima.IsRepublicano && p.IsMilicia)     || (victima.IsMilicia && p.IsRepublicano)
            || (victima.IsCitizen && p.IsCitizen)         || (victima.IsCitizen && p.IsArmada)
            || (victima.IsArmada && p.IsCitizen)          || (victima.IsArmada && p.IsArmada);
        if (bloqueoDeFaccion) { session.Send(ServerMessages.LocaleMsg(97)); return false; }
        // Arena (TriggerZonaPelea): los dos adentro habilita; uno solo adentro prohíbe; ninguno de los dos
        // deja pasar al último gate, que es el que protege a los GMs fuera de la arena.
        bool yoEnArena = _data.IsArena(p.Pos.Map, p.Pos.X, p.Pos.Y);
        bool elEnArena = _data.IsArena(victima.Pos.Map, victima.Pos.X, victima.Pos.Y);
        if (yoEnArena || elEnArena) return yoEnArena == elEnArena;
        if (victima.IsGm) { session.Send(ServerMessages.LocaleMsg(101)); return false; }
        return true;
    }

    /// <summary>Atacar a un objetivo LEGAL (ciudadano/Armada) sin provocación te vuelve criminal y te expulsa del
    /// ejército — el "seguro" del AO clásico. Exceptúa a los enemigos de facción sancionados (Armada↔Caos), a los
    /// ya-criminales/Caos y a los clanes en guerra. Se aplica IDÉNTICO en melee y en hechizos de daño: antes vivía
    /// sólo en el melee, así que matar aliados con magia no penaba (canilla de PK "limpio").
    ///
    /// <para>Es el <c>UsuarioAtacadoPorUsuario</c> del original (SistemaCombate.bas:1442) y por eso hace también
    /// lo OTRO que ese Sub hace en toda agresión: trabar las salidas de mapa siete segundos a los dos. Va acá y
    /// no en cada call site justamente porque el original tiene un solo Sub — repartirlo era garantizar que
    /// alguna de las siete vías de agresión se quedara sin marcar.</para></summary>
    private void FlagCriminalIfLegalTarget(ClientSession session, Player p, Player target)
    {
        // La arena está exceptuada de TODO el Sub, no sólo de la parte criminal: es el primer `Exit Sub` del
        // original (:1451), antes que cualquier otra cosa.
        if (DueloDeArena(p, target)) return;
        // Anti-escape: ni el agredido ni el agresor pueden mapear por 7 segundos. Se marca ANTES de saber si el
        // golpe entró — el original llama a este Sub antes de `UsuarioImpacto` (SistemaCombate.bas:1193), así que
        // errarle también te traba.
        long trabaMapeo = _loop.Tick + TicksAntiMapeo;
        p.MapeoBloqueadoHastaTick = trabaMapeo;
        target.MapeoBloqueadoHastaTick = trabaMapeo;
        // Bandos enfrentados del VB6: Caos ↔ (Armada|Milicia), e Imperio ↔ República (Armada ↔ Milicia). Entre
        // ellos el combate está sancionado y no penaliza.
        bool pEjercito = p.IsArmada || p.IsMilicia, tEjercito = target.IsArmada || target.IsMilicia;
        bool esEnemigoDeFaccion = (pEjercito && target.IsCaos) || (p.IsCaos && tEjercito)
                               || (p.IsArmada && target.IsMilicia) || (p.IsMilicia && target.IsArmada);
        // "Legales" son los civiles con ciudadanía y sus ejércitos (imperiales y republicanos).
        bool objetivoLegal = target.IsCitizen || target.IsRepublicano || tEjercito;
        if (esEnemigoDeFaccion || p.IsCaos || !objetivoLegal || AtWar(p, target)) return;
        p.CambiarFaccion(Player.StatusCriminal);
        p.FactionRank = 0;   // atacar a un legal expulsa de la Armada
        session.Send(ServerMessages.ConsoleMsg("Has atacado a un aliado. Ahora eres CRIMINAL.", FuenteInfo));
        // VB6: el recolor del nick viaja en CharStatus (charIndex + status), no en el tag completo.
        _world.BroadcastToObservers(p.Pos, ServerMessages.CharStatus(p.CharIndex, (byte)p.FactionStatus)); // pasa a CRIMINAL
    }

    /// <summary>GuildOfferPeace (id 45): guild name + proposal text. The leader offers to end the war.
    /// VB6 HandleGuildOfferPeace (Protocol.bas:4051) reads TWO ASCIIStrings (guild, proposal).</summary>
    private void HandleGuildOfferPeace(ClientSession session, ByteQueueReader r)
    {
        string enemyName = r.ReadAsciiString().Trim();
        string proposal = r.ReadAsciiString();   // el texto que el líder enemigo leerá con OfferDetails
        _loop.Enqueue(() =>
        {
            var mine = RequireGuildLeader(session, session.Player, announceNoGuild: true);
            if (mine is null) return;
            var enemy = _world.GuildByName(enemyName);
            if (enemy is null || !mine.Enemies.Contains(enemy.Name)) { session.Send(ServerMessages.ConsoleMsg("No estás en guerra con ese clan.", FuenteClan)); return; }
            enemy.PeaceOffers[mine.Name] = proposal;
            session.Send(ServerMessages.ConsoleMsg($"Ofreciste la paz a <{enemy.Name}>.", FuenteClan));
            foreach (var m in _world.GuildMembersOnline(enemy.Name))
                m.Connection?.Send(ServerMessages.ConsoleMsg($"<{mine.Name}> ofrece la paz (el líder puede aceptarla).", FuenteClan));
        });
    }

    /// <summary>GuildAcceptPeace (id 41): offering guild name. Ends the war if peace was offered.</summary>
    private void HandleGuildAcceptPeace(ClientSession session, ByteQueueReader r)
    {
        string offeringName = r.ReadAsciiString().Trim();
        _loop.Enqueue(() =>
        {
            var mine = RequireGuildLeader(session, session.Player, announceNoGuild: true);
            if (mine is null) return;
            if (!mine.PeaceOffers.ContainsKey(offeringName)) { session.Send(ServerMessages.ConsoleMsg("Ese clan no te ofreció la paz.", FuenteClan)); return; }
            var other = _world.GuildByName(offeringName);
            if (other is null) return;
            mine.PeaceOffers.Remove(offeringName);
            mine.Enemies.Remove(other.Name);
            other.Enemies.Remove(mine.Name);
            PersistGuilds();
            foreach (var m in _world.GuildMembersOnline(mine.Name).Concat(_world.GuildMembersOnline(other.Name)))
                m.Connection?.Send(ServerMessages.ConsoleMsg($"¡PAZ! Los clanes <{mine.Name}> y <{other.Name}> firmaron la paz.", FuenteClan));
        });
    }

    /// <summary>
    /// Casamiento (id 104), portado de <c>PuedeCasarse</c> (Trabajo.bas:2510). Antes esto casaba de una:
    /// sin sacerdote, sin distancia, sin género y sin que el otro aceptara. El original es una PROPUESTA:
    ///
    /// <list type="number">
    /// <item>hay que estar los dos vivos, ser de géneros distintos y estar PEGADOS (distancia ≤ 1),</item>
    /// <item>hay que tener clickeado a un sacerdote (<c>Revividor</c>) a cinco tiles o menos,</item>
    /// <item>el primero manda la propuesta (<c>EjecutarAccion(3, nombre)</c> abre el cartel en el otro),</item>
    /// <item>y el casamiento se cierra cuando el OTRO responde con el mismo paquete.</item>
    /// </list>
    ///
    /// Ojo el <b>495</b>: el original lo usa para DOS cosas —"el objetivo está muy lejos" (:16, sin argumento)
    /// y "le has mandado una propuesta a #1" (:46, con el nombre)— y el texto del catálogo es el segundo.
    /// Se porta tal cual.
    /// </summary>
    private void HandleCasamiento(ClientSession session, string conQuien)
    {
        _loop.Enqueue(() =>
        {
            var p = session.Player;
            if (p is null) return;
            if (DeadCheck(session, p)) return;                                            // :1
            var target = _world.Players.FirstOrDefault(
                o => string.Equals(o.Name, conQuien, StringComparison.OrdinalIgnoreCase) && o.CharIndex != p.CharIndex);
            if (target is null) { session.Send(ServerMessages.LocaleMsg(75)); return; }   // no conectado
            if (!target.IsAlive) { session.Send(ServerMessages.LocaleMsg(7)); return; }   // :3
            if (target.CharIndex == p.CharIndex) { session.Send(ServerMessages.LocaleMsg(492)); return; }  // :7
            if (target.Gender == p.Gender) { session.Send(ServerMessages.LocaleMsg(117)); return; }        // :11
            if (DistanciaVB6(target.Pos, p.Pos) > 1) { session.Send(ServerMessages.LocaleMsg(495)); return; }  // :16
            if (!string.IsNullOrEmpty(p.SpouseName)) { session.Send(ServerMessages.LocaleMsg(116)); return; }  // :18
            if (!string.IsNullOrEmpty(target.SpouseName)) { session.Send(ServerMessages.LocaleMsg(120)); return; }  // :23

            if (string.Equals(target.CandidatoCasamiento, p.Name, StringComparison.OrdinalIgnoreCase))
            {
                // El otro ya había propuesto: se cierra. El anuncio va a TODO el servidor con
                // "mapa%novio%novia", y suenan dos cosas distintas — la marcha a todo el mapa y el sanar al área.
                p.SpouseName = target.Name; target.SpouseName = p.Name;
                p.CandidatoCasamiento = target.CandidatoCasamiento = "";
                string donde = _data.MapName(p.Pos.Map);
                _world.Broadcast(ServerMessages.LocaleMsg(236, $"{donde}%{p.Name}%{target.Name}"));
                _world.BroadcastToMap(p.Pos.Map, ServerMessages.PlayWave(SndCasamiento, p.Pos.X, p.Pos.Y));
                _world.BroadcastToObservers(p.Pos, ServerMessages.PlayWave(SndSanar, p.Pos.X, p.Pos.Y));
                return;
            }

            // Todavía no hay propuesta: hace falta el sacerdote clickeado y cerca.
            var sacerdote = p.TargetNpc > 0 ? _world.NpcByIndex(p.TargetNpc) : null;
            if (sacerdote is null) { session.Send(ServerMessages.LocaleMsg(493)); return; }                // :38
            if (!sacerdote.IsReviver) return;                                                            // :41 silencio
            if (DistanciaVB6(sacerdote.Pos, p.Pos) > 5) { session.Send(ServerMessages.LocaleMsg(494)); return; }  // :43
            session.Send(ServerMessages.LocaleMsg(495, target.Name));                                      // :46
            target.Connection?.Send(ServerMessages.EjecutarAccion(3, p.Name));                             // :47
            p.CandidatoCasamiento = target.Name;
        });
    }

    /// <summary>divorciar (id 105): dissolve the marriage.</summary>
    private void HandleDivorciar(ClientSession session)
    {
        _loop.Enqueue(() =>
        {
            var p = session.Player;
            if (p is null) return;
            // El original chequea MUERTO primero (Protocol.bas:16421) — un cadáver no se divorcia — y recién
            // después mira si estás casado. Los tres textos son los suyos.
            if (!p.IsAlive) { session.Send(ServerMessages.LocaleMsg(77)); return; }
            if (string.IsNullOrEmpty(p.SpouseName))
            { session.Send(ServerMessages.ConsoleMsg("No puedes divorciarte porque no estás casado.", FuenteInfo)); return; }
            string pareja = p.SpouseName;
            var spouse = _world.PlayerByName(pareja);
            if (spouse is not null && string.Equals(spouse.SpouseName, p.Name, StringComparison.OrdinalIgnoreCase))
            {
                spouse.SpouseName = "";
                spouse.Connection?.Send(ServerMessages.ConsoleMsg($"{p.Name} ha decidido divorciarse de ti.", FuenteInfo));
            }
            p.SpouseName = "";
            session.Send(ServerMessages.ConsoleMsg($"{pareja} ya no es más tu pareja.", FuenteInfo));
        });
    }

    /// <summary>Gamble (id 83) — el casino del original (VB6 HandleGamble, Protocol.bas:5890): se le apuesta oro
    /// a un NPC <b>Timbero</b> (<c>eNPCType</c> = 12) y se gana o se pierde lo apostado.
    ///
    /// <para>La probabilidad es <b>47%</b>, no la mitad: la banca gana. Y las respuestas van todas por la boca
    /// del NPC (<c>ChatOverHead</c> con SU CharIndex), no por consola — incluidos los rechazos, que es lo que
    /// hace que un timbero se sienta un personaje y no un formulario.</para>
    ///
    /// <para>Acá esto contestaba <c>LocaleMsg(22)</c> pasara lo que pasara: leía el monto y no apostaba nunca.
    /// El censo de handlers lo contaba como portado porque el <c>case</c> existía — un handler que existe no es
    /// un handler que hace algo.</para></summary>
    private void HandleGamble(ClientSession session, ByteQueueReader r)
    {
        int amount = r.ReadInteger();
        _loop.Enqueue(() =>
        {
            var p = session.Player;
            if (p is null) return;
            if (!p.IsAlive) { session.Send(ServerMessages.LocaleMsg(77)); return; }
            var npc = TargetNpcDe(p);
            if (npc is null) { session.Send(ServerMessages.LocaleMsg(22)); return; }
            if (DistanciaVB6(p.Pos, npc.Pos) > 10) { session.Send(ServerMessages.LocaleMsg(8)); return; }
            void Dice(string texto) => session.Send(ServerMessages.ChatOverHead(texto, npc.CharIndex, 0));
            if (npc.Template.NpcType != NpcTimbero) { Dice("No tengo ningún interés en apostar."); return; }
            if (amount < 1) { Dice("El mínimo de apuesta es 1 moneda."); return; }
            if (amount > MaxApuesta) { Dice($"El máximo de apuesta es {MaxApuesta} monedas."); return; }
            if (p.Gold < amount) { Dice("No tienes esa cantidad."); return; }

            if (_rng.Next(1, 101) <= ProbGanarApuesta)
            {
                // El original NO paga si con eso te pasarías del tope de oro, y en ese caso hace `Exit Sub`
                // ANTES de tocar los contadores: la jugada no cuenta y no se cobra nada.
                if ((long)p.Gold + amount > int.MaxValue)   // VB6 OroLleno (General.bas:1840): el tope ES 2147483647
                { session.Send(ServerMessages.ConsoleMsg("Tienes la cantidad máxima de oro que puedes tener. No has obtenido oro", FuenteInfo)); return; }
                p.Gold += amount;
                Dice($"¡Felicidades! Has ganado {amount} monedas de oro.");
            }
            else
            {
                p.Gold -= amount;
                Dice($"Lo siento, has perdido {amount} monedas de oro.");
            }
            session.Send(ServerMessages.UpdateGold(p.Gold));
        });
    }

    /// <summary>GuildOpenElections (id 59): the leader opens leadership elections (clears the ballot).</summary>
    private void HandleGuildOpenElections(ClientSession session)
    {
        _loop.Enqueue(() =>
        {
            var g = RequireGuildLeader(session, session.Player, announceNoGuild: true);
            if (g is null) return;
            g.ElectionsOpen = true;
            g.Votes.Clear();
            foreach (var m in _world.GuildMembersOnline(g.Name))
                m.Connection?.Send(ServerMessages.ConsoleMsg($"Se han abierto elecciones en el clan <{g.Name}>. Usa /VOTO <nombre>.", FuenteClan));
        });
    }

    /// <summary>GuildVote (id 82): candidate name. A member votes; the most-voted member becomes leader.</summary>
    private void HandleGuildVote(ClientSession session, ByteQueueReader r)
    {
        string candidate = r.ReadAsciiString().Trim();
        _loop.Enqueue(() =>
        {
            var p = session.Player;
            if (p is null) return;
            // El original NO inventa un mensaje por caso: `v_UsuarioVota` (modGuilds.bas:1005) devuelve el motivo
            // y el handler lo antepone siempre con el mismo prefijo (Protocol.bas:5820). Acá cada rechazo tenía
            // su propia redacción y sólo el primero llevaba el prefijo, así que el jugador no podía saber si el
            // voto contó o no. Los cuatro motivos son los suyos, palabra por palabra.
            void NoContabilizado(string motivo) =>
                session.Send(ServerMessages.ConsoleMsg("Voto NO contabilizado: " + motivo, FuenteClan));
            if (string.IsNullOrEmpty(p.GuildName)) { NoContabilizado("Tú no perteneces a ningún clan."); return; }
            var g = _world.GuildByName(p.GuildName);
            if (g is null) return;
            if (!g.ElectionsOpen) { NoContabilizado("No hay elecciones abiertas en tu clan."); return; }
            if (!g.Members.Contains(candidate)) { NoContabilizado($"{candidate} no pertenece al clan."); return; }
            if (g.Votes.ContainsKey(p.Name)) { NoContabilizado("Ya has votado, no puedes cambiar tu voto."); return; }
            g.Votes[p.Name] = candidate;
            session.Send(ServerMessages.ConsoleMsg("Voto contabilizado.", FuenteClan));

            // El umbral (mayoría absoluta de los miembros) vive en Guild.GanadorDeLaEleccion, donde se puede
            // testear: es una regla de una línea que si se afloja entrega el clan al primero que vota.
            var ganador = Guild.GanadorDeLaEleccion(g.Votes.Values, g.Members.Count);
            if (ganador is not null && !string.Equals(ganador, g.LeaderName, StringComparison.OrdinalIgnoreCase))
            {
                g.LeaderName = ganador;
                g.ElectionsOpen = false;   // elección resuelta
                PersistGuilds();
                foreach (var m in _world.GuildMembersOnline(g.Name))
                    m.Connection?.Send(ServerMessages.ConsoleMsg($"{ganador} es ahora el líder del clan <{g.Name}>.", FuenteClan));
            }
        });
    }

    /// <summary>Returns the caller's guild iff they are online and its leader; else null (with a message).
    ///
    /// La FUENTE viaja desde el llamador y no está fija acá: casi todas las operaciones de clan rechazan con
    /// <c>FONTTYPE_GUILD</c>, pero el <c>/CERRARCLAN</c> del original (HandleCloseGuild, Protocol.bas:16621)
    /// contesta con <c>FONTTYPE_INFO</c>. Con la fuente cableada en el helper, ocho acciones seguían saliendo
    /// del color equivocado aunque sus handlers ya estuvieran corregidos — el rechazo no lo emite el handler.</summary>
    private Guild? RequireGuildLeader(ClientSession session, Player? p, bool announceNoGuild = false,
                                      byte fuente = FuenteClan)
    {
        if (p is null) return null;
        if (string.IsNullOrEmpty(p.GuildName))
        {
            // VB6: la mayoria de las ops de clan callan sin pertenecer a uno, pero OpenElections/OfferPeace/
            // AcceptPeace SI responden "No perteneces a ningun clan." (v_AbrirElecciones / v_*Paz).
            if (announceNoGuild) session.Send(ServerMessages.ConsoleMsg("No perteneces a ningún clan.", fuente));
            return null;
        }
        var g = _world.GuildByName(p.GuildName);
        if (g is null || !string.Equals(g.LeaderName, p.Name, StringComparison.OrdinalIgnoreCase))
        { session.Send(ServerMessages.ConsoleMsg("Solo el líder del clan puede hacer eso.", fuente)); return null; }
        return g;
    }

    /// <summary>GuildOfferAlliance (id 46): guild name + proposal text. Records a pending alliance offer.
    /// VB6 HandleGuildOfferAlliance (Protocol.bas:4111) reads TWO ASCIIStrings (guild, proposal).</summary>
    private void HandleGuildOfferAlliance(ClientSession session, ByteQueueReader r)
    {
        string targetName = r.ReadAsciiString().Trim();
        string proposal = r.ReadAsciiString();   // el texto que el líder del otro clan leerá con OfferDetails
        _loop.Enqueue(() =>
        {
            var mine = RequireGuildLeader(session, session.Player, announceNoGuild: true);
            if (mine is null) return;
            var target = _world.GuildByName(targetName);
            if (target is null) { session.Send(ServerMessages.ConsoleMsg("Ese clan no existe.", FuenteClan)); return; }
            if (string.Equals(target.Name, mine.Name, StringComparison.OrdinalIgnoreCase)) return;
            target.AllianceOffers[mine.Name] = proposal;
            PersistGuilds();
            session.Send(ServerMessages.ConsoleMsg($"Ofreciste una alianza a <{target.Name}>.", FuenteClan));
            foreach (var m in _world.GuildMembersOnline(target.Name))
                m.Connection?.Send(ServerMessages.ConsoleMsg($"<{mine.Name}> te ofrece una alianza (el líder puede aceptarla).", FuenteClan));
        });
    }

    /// <summary>GuildAcceptAlliance (id 44): offering guild name. Forms the alliance if it was offered.</summary>
    private void HandleGuildAcceptAlliance(ClientSession session, ByteQueueReader r)
    {
        string offeringName = r.ReadAsciiString().Trim();
        _loop.Enqueue(() =>
        {
            var mine = RequireGuildLeader(session, session.Player, announceNoGuild: true);
            if (mine is null) return;
            if (!mine.AllianceOffers.ContainsKey(offeringName)) { session.Send(ServerMessages.ConsoleMsg("Ese clan no te ofreció una alianza.", FuenteClan)); return; }
            var other = _world.GuildByName(offeringName);
            if (other is null) { session.Send(ServerMessages.ConsoleMsg("Ese clan ya no existe.", FuenteClan)); return; }
            mine.AllianceOffers.Remove(offeringName);
            mine.Allies.Add(other.Name);
            other.Allies.Add(mine.Name);
            PersistGuilds();
            foreach (var m in _world.GuildMembersOnline(mine.Name).Concat(_world.GuildMembersOnline(other.Name)))
                m.Connection?.Send(ServerMessages.ConsoleMsg($"¡ALIANZA! Los clanes <{mine.Name}> y <{other.Name}> son aliados.", FuenteClan));
        });
    }

    /// <summary>True if both players are in guilds that have declared war on each other.</summary>
    private bool AtWar(Player a, Player b)
    {
        if (string.IsNullOrEmpty(a.GuildName) || string.IsNullOrEmpty(b.GuildName)) return false;
        var ga = _world.GuildByName(a.GuildName);
        return ga is not null && ga.Enemies.Contains(b.GuildName);
    }

    /// <summary>GuildLeave (id 64): the caller leaves their guild.</summary>
    private void HandleGuildLeave(ClientSession session)
    {
        _loop.Enqueue(() =>
        {
            var p = session.Player;
            if (p is null) return;
            if (string.IsNullOrEmpty(p.GuildName)) return;   // VB6 HandleGuildLeave: fuera de un clan, silencio
            var guild = _world.GuildByName(p.GuildName);
            guild?.Members.Remove(p.Name);
            PersistGuilds();
            foreach (var m in _world.GuildMembersOnline(p.GuildName))
                m.Connection?.Send(ServerMessages.ConsoleMsg($"{p.Name} deja el clan.", FuenteClan));
            session.Send(ServerMessages.ConsoleMsg($"Dejaste el clan <{p.GuildName}>.", FuenteClan));
            p.GuildName = "";
        });
    }

    /// <summary>GuildOnline (id 79): lists the caller's online guild members.</summary>
    private void HandleGuildOnline(ClientSession session)
    {
        if (Frenado(session, ruidosa: false)) return;
        _loop.Enqueue(() =>
        {
            var p = session.Player;
            if (p is null) return;
            if (string.IsNullOrEmpty(p.GuildName)) return;   // VB6 HandleGuildOnline: fuera de un clan, silencio
            var names = string.Join(", ", _world.GuildMembersOnline(p.GuildName).Select(m => m.Name));
            // El texto es el del original (VB6 Protocol.bas:5647); la fuente GUILDMSG ya coincidía.
            session.Send(ServerMessages.ConsoleMsg($"Compañeros de tu clan conectados: {names}", FuenteClanMsg));
        });
    }

    // --- Crafting (herrería/carpintería: build an item from materials + skill) ---

    /// <summary>CraftBlacksmith (id 23): Item, cant. Forge from ingots (obj.dat LingH/LingP/LingO). El VB6
    /// PuedeConstruirHerreria acepta armas + armaduras/cascos/escudos, así que el gate usa BlacksmithRecipes
    /// (ambos sets), no solo BlacksmithWeapons (antes un herrero no podía forjar armaduras/cascos/escudos).</summary>
    private void HandleCraftBlacksmith(ClientSession session, ByteQueueReader r)
    {
        short item = r.ReadInteger(); short cant = r.ReadInteger();
        _loop.Enqueue(() => DoCraft(session, item, cant, "forjar", "Forjaste", _data.BlacksmithRecipes,
            GameLimits.SkillHerreria, o => o.SkillBlacksmith, o => new[]
            {
                (GameLimits.IngotIronObj, o.IngotIron),
                (GameLimits.IngotSilverObj, o.IngotSilver),
                (GameLimits.IngotGoldObj, o.IngotGold),
            }, staminaCost: 50, craftSound: MartilloHerrero));   // VB6 HerreroConstruirItem (Trabajo.bas:669): 50 de energía, LocaleMsg 206
    }

    /// <summary>CraftCarpenter (id 24): Item, cant. Build from wood (obj.dat Madera).</summary>
    private void HandleCraftCarpenter(ClientSession session, ByteQueueReader r)
    {
        short item = r.ReadInteger(); short cant = r.ReadInteger();
        _loop.Enqueue(() => DoCraft(session, item, cant, "construir", "Construiste", _data.CarpenterItems,
            GameLimits.SkillCarpinteria, o => o.SkillCarpentry, o => new[] { (GameLimits.WoodObj, o.Wood) },
            staminaCost: 20));   // VB6 CarpinteroConstruirItem (Trabajo.bas:765): 20 de energía, LocaleMsg 206
    }

    /// <summary>CraftSastre (id 26): Item, cant. Sew from skins (obj.dat PielLobo/PielOsoPardo/PielOsoPolar).</summary>
    private void HandleCraftSastre(ClientSession session, ByteQueueReader r)
    {
        short item = r.ReadInteger(); short cant = r.ReadInteger();
        _loop.Enqueue(() => DoCraft(session, item, cant, "confeccionar", "Confeccionaste", _data.TailorItems,
            GameLimits.SkillSastreria, o => o.SkillTailoring, o => new[]
            {
                (GameLimits.SkinWolfObj, o.SkinWolf),
                (GameLimits.SkinBrownBearObj, o.SkinBrownBear),
                (GameLimits.SkinPolarBearObj, o.SkinPolarBear),
            }, requiredTool: 886,            // VB6 SastreConstruirItem: exige el Costurero (886)
               staminaCost: 50, tiredMsg: 93));   // Trabajo.bas:395: 50 de energía, y ES el único que manda el 93
    }

    /// <summary>Craftalquimia (id 25): Item, cant. Brew a potion from roots (obj.dat Raices).</summary>
    private void HandleCraftAlquimia(ClientSession session, ByteQueueReader r)
    {
        short item = r.ReadInteger(); short cant = r.ReadInteger();
        _loop.Enqueue(() => DoCraft(session, item, cant, "preparar", "Preparaste", _data.AlchemyItems,
            GameLimits.SkillAlquimia, o => o.SkillAlchemy, o => new[] { (GameLimits.RootObj, o.Roots) },
            requiredTool: 887,          // VB6 druidaConstruirItem: exige la Olla (887)
            staminaCost: 30));          // Trabajo.bas:2433: 30 de energía, LocaleMsg 206
    }

    /// <summary>Shared crafting: validate the recipe, skill, and materials, then consume the materials and
    /// add the produced item. `materials` maps the object to the per-unit amount required.</summary>
    /// <summary><paramref name="staminaCost"/> y <paramref name="tiredMsg"/>: la ENERGÍA que cobra cada oficio
    /// (VB6 Trabajo.bas — sastrería 50/LocaleMsg 93, herrería 50/206, carpintería 20/206, alquimia 30/206). Los
    /// tres últimos mandan el 206 ("no tenés suficientes materiales"), que es un mensaje EQUIVOCADO para una
    /// falta de energía; se porta igual porque es lo que manda la fuente. Cada uno tiene un `If Clase = X ...
    /// Else ...` cuyas dos ramas son IDÉNTICAS (mismo monto, mismo mensaje), así que acá va un solo gate.</summary>
    private void DoCraft(ClientSession session, short item, short cant, string verb, string pastTense,
        IReadOnlySet<int> recipes, int skillIndex, Func<ObjInfo, int> reqSkill, Func<ObjInfo, (int obj, int per)[]> materials,
        int requiredTool = 0, int staminaCost = 0, short tiredMsg = 206, short craftSound = LaburoCarpintero)
    {
        var p = session.Player;
        if (p is null) return;
        int count = Math.Clamp((int)cant, 1, 1000);
        // VB6: un objeto que no es receta de esta profesión (SkHerreria/etc = 0) -> Exit Sub SILENCIOSO, sin
        // avisar. El barrido en frío (item=1) lo confirma: el VB6 no emite nada mientras el .NET mandaba ConsoleMsg.
        if (!recipes.Contains(item)) return;
        var obj = _data.Obj(item);
        if (obj is null) return;
        // VB6 SastreConstruirItem (Trabajo.bas:390) / druidaConstruirItem (:2428): la fabricación exige la
        // herramienta EQUIPADA — el gate literal es `Invent.AnilloEqpObjIndex = COSTURERO` (u OLLA), o sea el
        // objeto que está puesto en el slot de anillo. Antes acá se pedía `CountItem(...) > 0`, que es "tenerla
        // en la mochila": se podía coser con el costurero guardado en el bolsillo. Ahora hay que ponérselo.
        // LOS RECHAZOS SON MUDOS. El original envuelve toda la fabricación en un solo
        // `If TieneMateriales And skill And PuedeConstruirX (And herramienta) Then` SIN `Else`
        // (Trabajo.bas:660/747), así que cada condición que no se cumple termina en silencio. Acá salían
        // cuatro paquetes que el VB6 no manda — y uno de ellos, el `LocaleMsg(206)` por falta de materiales,
        // encima con la semántica cambiada: en el original el 206 es EXCLUSIVAMENTE el aviso de falta de
        // ENERGÍA (:674/:682 y :774/:782), mal redactado en la fuente pero con un solo uso.
        if (requiredTool != 0 && Equipment.EquippedOfCategory(p, _data, 6)?.Index != requiredTool) return;
        if (p.Skills[skillIndex] < reqSkill(obj)) return;
        var needs = materials(obj).Where(m => m.per > 0).Select(m => (m.obj, need: m.per * count)).ToArray();
        foreach (var (mObj, need) in needs)
            if (CountItem(p, mObj) < need) return;
        int slot = FindSlotFor(p, item, count);
        if (slot == 0) return;
        // ENERGÍA. Va acá —validado todo, antes de consumir— porque es donde la cobra el original: dentro del
        // `If TieneMateriales And skill And PuedeConstruir And herramienta Then`, justo antes del `QuitarMateriales`.
        // Sin esto la fabricación no costaba NADA: se podía spamear el paquete y cada llamada subía la skill del
        // oficio y regalaba 10 de exp, con los materiales como único límite.
        if (staminaCost > 0)
        {
            if (p.Stamina < staminaCost) { session.Send(ServerMessages.LocaleMsg(tiredMsg)); return; }
            p.Stamina -= staminaCost;
            session.Send(ServerMessages.UpdateSta((short)p.Stamina));   // VB6 WriteUpdateSta
        }
        foreach (var (mObj, need) in needs) ConsumeItem(session, p, mObj, need);
        RaiseSkill(session, p, skillIndex);   // VB6: fabricar sube la skill del oficio (SubirSkill)
        var inv = p.Inventory[slot];
        inv.ObjIndex = item;
        inv.Amount = (short)Math.Min(GameLimits.MaxInventoryObjs, inv.Amount + count);
        p.Inventory[slot] = inv;
        session.Send(ServerMessages.ChangeInventorySlot((byte)slot, inv.ObjIndex, inv.Amount, inv.Equipped, 0f, 1));
        session.Send(ServerMessages.LocaleMsg(431));
        // EL SONIDO DEL OFICIO, al ÁREA (VB6 Trabajo.bas:425/707/789/2464): el martillo del herrero (41) o el
        // laburo del carpintero (42), que es el que usan también sastre y alquimista. Faltaba: fabricar era
        // completamente mudo para el que estaba al lado.
        _world.BroadcastToObservers(p.Pos, ServerMessages.PlayWave(craftSound, p.Pos.X, p.Pos.Y));
        p.Trabajando++;   // crédito del centinela (VB6 Trabajo.bas:427/708/792/2467), lo lista /TRABAJANDO
    }

    private const short MartilloHerrero = 41;    // Declares.bas:549
    private const short LaburoCarpintero = 42;   // Declares.bas:550 — lo usan carpintero, sastre y alquimista

    // --- Mail (correo): send a message + attached item; delivered to the recipient's mailbox ---

    /// <summary>Lo que cuesta mandar una carta (VB6 mod_Correos.bas:32-47, el 1750 está hardcodeado ahí).</summary>
    private const int CostoDelCorreo = 1750;

    /// <summary>Persists the mailboxes after a change, so pending mail survives restarts.</summary>
    private void PersistMail() => _correoSucio = true;

    // ── PERSISTENCIA DIFERIDA DE CLANES Y CORREO ────────────────────────────────────────────────────────
    // Las 22 llamadas a PersistGuilds/PersistMail escribían el archivo ENTERO en el acto, en el hilo del game
    // loop: serializar todos los clanes a JSON y bajarlo a disco. Cualquier acción de clan —pedir entrar,
    // declarar una guerra, tocar las noticias— lo disparaba, y son acciones que un cliente puede repetir a la
    // velocidad que quiera. Con un mundo poblado el costo crece con la cantidad de clanes, no con el cambio.
    //
    // Ahora se marca sucio y se escribe como mucho una vez cada IntervaloPersistenciaMs. El apagado y el
    // backup fuerzan el volcado, así que un cierre ordenado no pierde nada.
    //
    // 500 ms y no varios segundos, a propósito. Lo que hay que colapsar es la RÁFAGA: una sola acción de clan
    // dispara varias llamadas seguidas, todas dentro de la misma vuelta. Con medio segundo eso ya queda en una
    // escritura, y un cliente que spamea acciones pasa de una escritura por paquete a dos por segundo. Estirarlo
    // más no compra casi nada y sí cuesta: lo que se pierde ante una caída DURA (un kill, un corte de luz) es
    // exactamente este intervalo. El arnés lo hace explícito — reinicia el server a la fuerza 900 ms después de
    // fundar un clan y verifica que sobrevivió (persist_guild_flow / persist_mail_flow).
    private bool _clanesSucios, _correoSucio;
    private long _ultimaPersistenciaMs;
    private const long IntervaloPersistenciaMs = 500;

    /// <summary>El valor de <see cref="World.SueloCambios"/> que ya está en el disco. Arranca en -1 —y no en 0—
    /// para que un mundo cuyo suelo todavía no cambió igual se vuelque una vez: si no, un server que restaura
    /// el suelo y se apaga sin que nadie toque nada no reescribiría el archivo, y esa vuelta en falso sólo se
    /// nota cuando el archivo se borró a mano y nadie lo repone.</summary>
    private long _sueloGuardado = -1;

    /// <summary>Vuelca clanes y correo si quedaron sucios y ya pasó el intervalo. La llama el game loop.
    /// Con <paramref name="forzar"/> escribe sin esperar (apagado, backup).</summary>
    public void VolcarPersistenciaDiferida(bool forzar = false)
    {
        bool sueloSucio = _ini.PersistirSuelo && _world.SueloCambios != _sueloGuardado;
        if (!_clanesSucios && !_correoSucio && !sueloSucio && _world.RecordUsuarios == _recordGuardado) return;
        long ahora = Environment.TickCount64;
        if (!forzar && ahora - _ultimaPersistenciaMs < IntervaloPersistenciaMs) return;
        _ultimaPersistenciaMs = ahora;
        // Try por archivo: que falle el de clanes no puede dejar el correo sin escribir. Y la marca de sucio
        // se limpia sólo si la escritura salió bien, para que el próximo intento lo reintente.
        if (_clanesSucios)
        {
            try { _db.SaveGuilds(_world.AllGuilds); _clanesSucios = false; }
            catch (Exception ex) { Console.Error.WriteLine($"[persistencia] clanes: {ex.Message}"); }
        }
        if (_correoSucio)
        {
            try { _db.SaveMailboxes(_world.AllMailboxes); _correoSucio = false; }
            catch (Exception ex) { Console.Error.WriteLine($"[persistencia] correo: {ex.Message}"); }
        }
        // El record de conectados: se escribe sólo cuando SUBIÓ, que es un puñado de veces en la vida de un
        // servidor. Va por el mismo camino diferido para no meter un write a disco en el hilo del loop.
        if (_world.RecordUsuarios != _recordGuardado)
        {
            try { _db.SaveRecordUsuarios(_world.RecordUsuarios); _recordGuardado = _world.RecordUsuarios; }
            catch (Exception ex) { Console.Error.WriteLine($"[persistencia] record: {ex.Message}"); }
        }
        // EL SUELO (regla de la casa). El contador se toma ANTES de serializar y se guarda DESPUÉS: así lo que
        // queda anotado como "ya está en el disco" es exactamente lo que se escribió. Hoy todo esto corre en el
        // hilo del game loop y nada puede colarse en el medio, o sea que da lo mismo — pero si algún día el
        // volcado se saca de este hilo, esta forma sigue siendo correcta y la otra empieza a perder drops.
        if (sueloSucio)
        {
            long enEsteVolcado = _world.SueloCambios;
            // LAS PUERTAS NO VAN. El .csm las deja no-fijas porque su estado cambia en runtime, así que caen en
            // el mismo saco que un drop — pero `WorldPopulator` las repone en cada arranque, y restaurarlas
            // encima haría que una puerta abierta quedara abierta para siempre. Eso no lo pidió nadie y el
            // original no lo hace.
            try
            {
                _db.SaveSuelo(_world.SueloNoFijo().Where(t => _data.Obj(t.ObjIndex)?.IsDoor != true));
                _sueloGuardado = enEsteVolcado;
            }
            catch (Exception ex) { Console.Error.WriteLine($"[persistencia] suelo: {ex.Message}"); }
        }
    }

    /// <summary>Lo último que se persistió del record, para no reescribirlo en cada vuelta.</summary>
    private int _recordGuardado = -1;

    /// <summary>EnviarCorreo (id 97): recipient, message, ObjIndex, Amount. Consumes the attached item from
    /// the sender and drops it in the recipient's mailbox (delivered now if online, else at their login).</summary>
    private void HandleEnviarCorreo(ClientSession session, ByteQueueReader r)
    {
        string dest = r.ReadAsciiString();
        string message = r.ReadAsciiString();
        short objIndex = r.ReadInteger();
        short amount = r.ReadInteger();
        _loop.Enqueue(() =>
        {
            var p = session.Player;
            if (p is null) return;
            if (string.IsNullOrWhiteSpace(dest) || !_db.CharacterExists(dest))
            { session.Send(ServerMessages.ConsoleMsg($"No existe el personaje {dest}.", FuenteInfo)); return; }
            if (string.Equals(dest, p.Name, StringComparison.OrdinalIgnoreCase))
            { session.Send(ServerMessages.ConsoleMsg("No puedes enviarte correo a ti mismo.", FuenteInfo)); return; }
            // CASILLA LLENA (VB6 Max_Correos = 10, Declares.bas:1281; el gate en CantSendCorreo,
            // mod_Correos.bas:134). Sin tope, `DeliverMail` acumulaba sin límite — y como cada operación de
            // correo re-serializa el diccionario ENTERO de buzones a JSON dentro del game loop (hilo único),
            // un bucle de envíos a una víctima era un DoS remoto además de un buzón inutilizable: `slot` es un
            // byte, así que a partir de la carta 256 nadie podía volver a tocarlas.
            if (_world.PeekMail(dest).Count >= GameLimits.MaxCorreos)
            { session.Send(ServerMessages.ConsoleMsg($"{dest} no tiene más espacio en su casilla.", FuenteInfo)); return; }
            // EL CORREO CUESTA 1750 DE ORO (VB6 mod_Correos.bas:32-48). Es un sumidero de oro de la economía
            // —y de paso el limitador natural del spam— que acá no se cobraba: mandar cartas era gratis.
            // El original chequea ANTES de sacar el ítem y cobra DESPUÉS, con su UpdateGold.
            if (p.Gold < CostoDelCorreo) { session.Send(ServerMessages.LocaleMsg(26)); return; }   // "Oro insuficiente"

            short attachObj = 0, attachAmt = 0;
            if (objIndex > 0 && amount > 0)
            {
                if (CountItem(p, objIndex) < amount) { session.Send(ServerMessages.ConsoleMsg("No tienes ese ítem para adjuntar.", FuenteInfo)); return; }
                ConsumeItem(session, p, objIndex, amount);
                attachObj = objIndex; attachAmt = amount;
            }
            p.Gold -= CostoDelCorreo;                       // VB6 mod_Correos.bas:47
            session.Send(ServerMessages.UpdateGold(p.Gold)); // …y su WriteUpdateGold (:48)
            _world.DeliverMail(dest, new MailEntry(p.Name, message, attachObj, attachAmt));
            PersistMail();
            session.Send(ServerMessages.ConsoleMsg($"Enviaste correo a {dest}.", FuenteInfo));
            // If they're connected, notify them; they retrieve from the mailbox via Packets_Correo (or at login).
            var online = _world.PlayerByName(dest);
            online?.Connection?.Send(ServerMessages.ConsoleMsg($"Tienes correo nuevo de {p.Name}. Usa /RETIRAR para recibirlo.", FuenteInfo));
            // VB6 mod_Correos.bas:68: además del aviso por consola se prende el INDICADOR de correo pendiente
            // (el "tenés carta" del cliente). Se apaga cuando lo retira — es un flag, no un mensaje.
            online?.Connection?.Send(ServerMessages.MensajeSigno(true));
        });
    }

    /// <summary>DropDestroy (id 17): slot(Byte), amount(Long). Permanently destroys items from an inventory slot.
    /// Amount is a VB6 Long (4 bytes) — matches HandleDropDestroy (Protocol.bas:2103) / WriteDropDestroy client.</summary>
    private void HandleDropDestroy(ClientSession session, ByteQueueReader r)
    {
        byte slot = r.ReadByte();
        int amount = r.ReadLong();
        _loop.Enqueue(() =>
        {
            var p = session.Player;
            if (p is null || slot < 1 || slot > GameLimits.MaxInventorySlots) return;
            if (DeadCheck(session, p)) return;   // VB6 HandleDropDestroy: Protocol.bas:2161 (un muerto no destruye objetos)
            var inv = p.Inventory[slot];
            if (inv.IsEmpty) return;
            int destroy = Math.Clamp((int)amount, 1, inv.Amount);
            var name = _data.Obj(inv.ObjIndex)?.Name ?? "objeto";
            DesequiparSiSeVaEntero(session, p, slot, destroy);
            inv = p.Inventory[slot];
            inv.Amount -= (short)destroy;
            if (inv.Amount <= 0) inv = default;
            p.Inventory[slot] = inv;
            session.Send(ServerMessages.ChangeInventorySlot(slot, inv.ObjIndex, inv.Amount, inv.Equipped, 0f, (byte)(inv.IsEmpty ? 0 : 1)));
        });
    }

    /// <summary>Las mascotas del jugador — cupo COMPARTIDO entre domar e invocar (VB6 <c>NroMascotas</c>,
    /// <c>MAXMASCOTAS</c> = 3). Cuenta LUGARES OCUPADOS y no criaturas en el mundo: una mascota que espera afuera
    /// (domada en zona segura, o dejada atrás al entrar a una ciudad) sigue siendo tuya y sigue ocupando el suyo.
    /// Ver el bloque de mascotas de <see cref="Player"/>.</summary>
    private static int CuentaMascotas(Player p) => p.NroMascotas;

    /// <summary>Mascotas del jugador de una ficha dada (VB6 <c>PuedeDomarMascota</c> cuenta <c>MascotasType</c>).</summary>
    private static int CuentaMascotasDeTipo(Player p, short tipo) => p.MascotasDeTipo(tipo);

    /// <summary>
    /// DOMAR una criatura (VB6 <c>DoDomar</c>, Trabajo.bas:1058). El cupo (3) es COMPARTIDO con la invocación
    /// y no se doman más de 2 del mismo tipo; mercaderes, banqueros y entrenadores no se doman.
    ///
    /// <para>ESTO NO ES EL PAQUETE <c>Train</c>, y ahí estaba el mis-port: acá vivía en <c>HandleTrain</c>, o
    /// sea que para domar había que mandar el paquete que en el original le pide una criatura al ENTRENADOR.
    /// En el VB6 se doma clickeando a la criatura con la skill Domar (<c>HandleWorkLeftClick</c>,
    /// <c>Case eSkill.domar</c>, Protocol.bas:3015-3028), que es de donde se llama ahora.</para>
    /// </summary>
    private void DoDomar(ClientSession session, Player p, Npc npc)
    {
        {
            if (p.Skills[GameLimits.SkillDomar] <= 0) return;   // sin skill, silencio
            // Cupo compartido domar+invocar (VB6 DoDomar Trabajo.bas:1083 `If .NroMascotas < MAXMASCOTAS`).
            if (CuentaMascotas(p) >= GameLimits.MaxMascotas) { session.Send(ServerMessages.LocaleMsg(254)); return; }
            if (npc.IsMerchant || npc.IsBanker || npc.IsWorkbench || npc.IsTrainer || npc.OwnerCharIndex != 0) { session.Send(ServerMessages.LocaleMsg(300)); return; }
            // VB6 DoDomar (Trabajo.bas:1097-1105): la criatura tiene un costo `Domable` (NPCs.dat) y el jugador
            // puntos = Carisma * skill Domar. Sólo se doma si alcanza los puntos Y sale la tirada 1-en-5.
            // (Antes se domaba cualquier criatura, siempre: gate faltante del code-review.)
            if (npc.Template.Domable <= 0) { session.Send(ServerMessages.LocaleMsg(300)); return; }   // criatura no domable
            // No más de 2 mascotas del MISMO tipo (VB6 PuedeDomarMascota, Trabajo.bas:1162 → LocaleMsg 254).
            if (CuentaMascotasDeTipo(p, npc.Template.Index) >= GameLimits.MaxMascotasMismoTipo)
            { session.Send(ServerMessages.LocaleMsg(254)); return; }
            int puntosDomar = p.Carisma * p.Skills[GameLimits.SkillDomar];
            if (npc.Template.Domable > puntosDomar || _rng.Next(1, 6) != 1)
            { session.Send(ServerMessages.ConsoleMsg("No has logrado domar la criatura.", FuenteInfo)); return; }
            npc.OwnerCharIndex = p.CharIndex;
            p.OcuparSlotDeMascota(p.PrimerSlotLibre(), npc.CharIndex, npc.Template.Index);   // VB6 FreeMascotaIndex
            npc.Hostile = false;
            npc.OriginallyHostile = false;   // domada: deja de ser fiera para siempre, no sólo hasta el próximo RestoreOldMovement
            npc.AttackedBy = null;           // y se le perdona al domador el golpe que le dio para amansarla
            // VB6 MODULO_NPCs.bas:1158 (`.Movement = TipoAI.SigueAmo`): una mascota deja de andar sola y sigue a
            // su amo. El slice ya rutea por OwnerCharIndex antes del switch, pero se deja el TipoAI fiel.
            npc.Movement = 8;                // TipoAI.SigueAmo
            session.Send(ServerMessages.LocaleMsg(421));

            // LA CRIATURA DOMADA EN ZONA SEGURA ESPERA AFUERA (VB6 DoDomar, Trabajo.bas:1114-1124: `CanStay =
            // MapInfo(.Pos.Map).Pk`). No es que se pierda: se la saca del mundo y se le vuelve a poner el TYPE
            // que `RemoveNpc` acaba de borrar, con lo que el lugar queda ocupado pero VACÍO. De ahí la saca
            // WarpMascotas cuando el amo pisa un mapa inseguro. Este par —sacarla acá y traerla allá— es una
            // sola mecánica; portar la mitad deja mascotas que desaparecen para siempre al entrar a una ciudad.
            if (!_data.IsPkMap(p.Pos.Map)) DejarMascotaEsperandoAfuera(p, npc);
        }
    }

    /// <summary>Saca la criatura del mundo pero le conserva el lugar: la mascota sigue siendo del jugador y
    /// vuelve con <see cref="WarpMascotas"/>. Es el `QuitarNPC` + restaurar `MascotasType` que hacen tanto
    /// <c>DoDomar</c> (Trabajo.bas:1118-1123) como <c>WarpMascotas</c> (Modulo_UsUaRiOs.bas:1729-1741).</summary>
    private void DejarMascotaEsperandoAfuera(Player amo, Npc npc)
    {
        int slot = SlotDeMascota(amo, npc.CharIndex);
        if (slot == 0) return;
        short ficha = amo.MascotasType[slot];
        var donde = npc.Pos;
        _world.RemoveNpc(npc.CharIndex);              // esto le borra el lugar (World.RemoveNpc → QuitarMascota)
        _world.BroadcastToObservers(donde, ServerMessages.CharacterRemove(npc.CharIndex));
        amo.MascotasType[slot] = ficha;               // …y acá se lo devolvemos, vacío. El lugar FANTASMA.
        amo.NroMascotas++;
    }

    /// <summary>En qué lugar (1..3) está esa criatura, o 0.</summary>
    private static int SlotDeMascota(Player amo, short charIndexNpc)
    {
        for (int i = 1; i <= GameLimits.MaxMascotas; i++)
            if (amo.MascotasIndex[i] == charIndexNpc) return i;
        return 0;
    }

    /// <summary>
    /// LAS MASCOTAS VIAJAN CON EL AMO (VB6 <c>WarpMascotas</c>, Modulo_UsUaRiOs.bas:1695).
    ///
    /// <para>El original la llama en CADA warp —<c>WarpUserChar</c>, con <c>If .NroMascotas Then</c> y FUERA del
    /// bloque de cambio de mapa, o sea también en un salto dentro del mismo mapa— y siempre DESPUÉS de mover la
    /// posición, así que lo que mira es el destino. Hace dos cosas distintas según qué llevabas atrás:</para>
    /// <list type="bullet">
    ///   <item>la INVOCADA (la que tiene tiempo de vida) se MATA y no vuelve: el viaje te la cuesta;</item>
    ///   <item>la DOMADA se saca del mapa viejo —siempre— y se vuelve a poner al lado tuyo en el destino
    ///         conservándole la vida que traía, sólo si el destino es mapa inseguro.</item>
    /// </list>
    /// <para>Que se la saque SIEMPRE del mapa viejo es la mitad que más se nota: sin esto, cruzar de mapa te
    /// dejaba la mascota plantada donde estabas, viva, todavía tuya y ocupándote un lugar de los tres.</para>
    /// <para>Y si el destino es zona segura, la mascota no se pierde: queda en su lugar FANTASMA (con
    /// <c>MascotasType</c> puesto y <c>MascotasIndex</c> en 0) esperando afuera, y la próxima vez que pises un
    /// mapa inseguro esta misma función la trae de vuelta por la rama del <c>ficha != 0</c>.</para>
    /// </summary>
    private void WarpMascotas(Player amo)
    {
        if (amo.NroMascotas == 0) return;   // VB6 `If .NroMascotas Then Call WarpMascotas`
        bool puedenVenir = _data.IsPkMap(amo.Pos.Map);   // canWarp = MapInfo(.Pos.Map).Pk, ya con el destino puesto

        for (int i = 1; i <= GameLimits.MaxMascotas; i++)
        {
            short ficha = amo.MascotasType[i];
            short viva = amo.MascotasIndex[i];
            int vidaQueTraia = 0;

            var npc = viva != 0 ? _world.NpcByIndex(viva) : null;
            if (npc is not null)
            {
                bool esInvocada = npc.ExpireAtTick > 0;   // VB6 Contadores.TiempoExistencia > 0
                vidaQueTraia = npc.Hp;
                var donde = npc.Pos;
                _world.RemoveNpc(viva);                   // libera el lugar (World.RemoveNpc → QuitarMascota)
                _world.BroadcastToObservers(donde, ServerMessages.CharacterRemove(viva));

                if (esInvocada) { ficha = 0; continue; }  // muere y el lugar queda libre de verdad
                amo.MascotasType[i] = ficha;              // la domada conserva el lugar…
                amo.NroMascotas++;                        // …y el contador, que RemoveNpc acaba de bajar
                amo.MascotasIndex[i] = 0;
            }
            else if (viva != 0)
            {
                // El lugar apuntaba a una criatura que ya no está (murió sin pasar por acá). Se limpia.
                amo.MascotasIndex[i] = 0;
            }

            if (ficha == 0 || !puedenVenir) continue;

            var libre = TileLibreParaMascota(amo.Pos);
            // Sin lugar donde ponerla (agua, todo ocupado) se la deja esperando afuera y se sigue con las
            // otras, que es lo que hace el original cuando su SpawnNpc devuelve 0: no toca MascotasType, así
            // que la mascota no se pierde — vuelve en el próximo warp que encuentre lugar.
            if (libre is null) continue;
            var plantilla = _data.Npc(ficha);
            if (plantilla is null) continue;

            var traida = new Npc
            {
                CharIndex = _world.AllocateCharIndex(),
                Template = plantilla,
                // "Nos aseguramos de que conserve el hp, si estaba daniado" (Modulo_UsUaRiOs.bas:1766): el
                // original sólo lo pisa si el guardado era 0, o sea que una mascota herida cruza herida.
                // DIVERGENCIA CHICA Y A PROPÓSITO: allá la variable del hp se declara UNA vez para todo el
                // barrido de los tres lugares y no se limpia entre vueltas, así que con más de una mascota la
                // segunda puede heredar la vida de la primera. Acá se lee por vuelta. No se replica el
                // arrastre: es una variable sin inicializar, no una regla del juego.
                Hp = vidaQueTraia > 0 ? vidaQueTraia : plantilla.MaxHp,
                Pos = libre.Value,
                OwnerCharIndex = amo.CharIndex,
                Hostile = false,
                OriginallyHostile = false,
                Movement = GameLimits.TipoAiSigueAmo,   // VB6 FollowAmo
            };
            _world.AddNpc(traida);
            _world.BroadcastToObservers(traida.Pos, ServerMessages.CharacterCreate(traida));
            amo.MascotasIndex[i] = traida.CharIndex;     // el lugar ya estaba contado: acá sólo se le pone la criatura
        }
    }

    /// <summary>Un tile donde poner la mascota que llega, empezando por el del amo (VB6 <c>SpawnNpc</c> con la
    /// posición del amo busca el primer lugar legal alrededor).</summary>
    private WorldPos? TileLibreParaMascota(WorldPos junto)
    {
        foreach (var pos in FreeTilesAround(junto, 1)) return pos;
        return null;
    }

    /// <summary>
    /// Train (id 33) — EL ENTRENADOR (VB6 <c>HandleTrain</c>, Protocol.bas:3327-3375). Le pedís al NPC
    /// Entrenador que te spawnee una de SUS criaturas para practicar, por índice (el byte del paquete).
    ///
    /// <para>Los gates, en el orden del original: sin NPC clickeado sale en silencio; si el clickeado no es
    /// un Entrenador, silencio; si el entrenador ya tiene sus <c>MAXMASCOTASENTRENADOR</c> = 7 criaturas
    /// vivas, avisa con el <b>593</b>; y el índice tiene que caer dentro de su lista (fuera de rango es
    /// silencio, no un error).</para>
    ///
    /// <para>La criatura nace en la POSICIÓN DEL ENTRENADOR y queda colgada de él (<c>MaestroNpc</c>), no del
    /// jugador: no es una mascota tuya, no te sigue y no cuenta para tu cupo. Antes este paquete leía el
    /// índice y lo tiraba, y en su lugar domaba — ver <see cref="DoDomar"/>.</para>
    /// </summary>
    private void HandleTrain(ClientSession session, byte petIndex)
    {
        _loop.Enqueue(() =>
        {
            var p = session.Player;
            if (p is null || p.TargetNpc < 1) return;
            var entrenador = _world.NpcByIndex(p.TargetNpc);
            if (entrenador?.TrainerCreatures is not { Count: > 0 } criaturas) return;   // no es un Entrenador
            if (CriaturasDelEntrenador(entrenador) >= MaxMascotasEntrenador)
            { session.Send(ServerMessages.LocaleMsg(593, "", 1)); return; }
            if (petIndex < 1 || petIndex > criaturas.Count) return;                     // fuera de rango: silencio
            var indices = entrenador.Template.TrainerCreatureIndexes;
            if (indices is null || petIndex > indices.Count) return;
            short tpl = (short)indices[petIndex - 1];
            if (tpl <= 0) return;
            SpawnCriaturaDelEntrenador(entrenador, tpl);
        });
    }

    /// <summary>Crea la criatura EN la posición del entrenador y la deja colgada de él. Se reusa el mismo
    /// camino que la invocación de un hechizo (`NpcSystem.Spawn`), que ya resuelve tile libre y CharacterCreate.</summary>
    private void SpawnCriaturaDelEntrenador(Npc entrenador, short plantilla)
    {
        var tpl = _data.Npc(plantilla);
        if (tpl is null || _npcs is null) return;
        int ci = _npcs.GmSpawn(tpl, entrenador.Pos);
        var cria = _world.NpcByIndex((short)ci);
        if (cria is null) return;
        cria.MasterNpcCharIndex = entrenador.CharIndex;   // VB6 MaestroNpc: es del entrenador, no de un jugador
    }

    /// <summary>Cuántas criaturas vivas cuelgan de este entrenador (VB6 <c>Npclist(x).Mascotas</c>).</summary>
    private int CriaturasDelEntrenador(Npc entrenador) =>
        _world.Npcs.Count(n => n.IsAlive && n.MasterNpcCharIndex == entrenador.CharIndex);

    /// <summary>LeftClick (id 19): X, Y. VB6 <c>LookatTile</c> (GameLogic.bas:655). Hace DOS cosas: describe por
    /// consola lo que hay en el tile, y —lo que importa de verdad— deja anotado en el jugador QUÉ clickeó
    /// (<c>flags.Target*</c>). Ese target es el parámetro implícito de varios comandos GM: /SEGUIR, /RESETINV y
    /// /TALKAS operan sobre <see cref="Player.TargetNpc"/>, y /DT sobre el tile (<c>TargetMap,X,Y</c>).
    ///
    /// Las sondas del original no son "lo que hay en (X,Y)":
    /// <list type="bullet">
    /// <item><b>Personaje</b>: mira PRIMERO <c>(X, Y+1)</c> y sólo si ahí no hay nadie mira <c>(X, Y)</c>. Es
    /// deliberado — el sprite ocupa dos tiles de alto, así que clickearle la cabeza selecciona al que está un
    /// tile más abajo. Dentro de cada tile, el NPC pisa al usuario (se prueban en ese orden sobre la misma
    /// variable).</item>
    /// <item><b>Objeto</b>: una cadena <c>ElseIf</c> sobre (X,Y), (X+1,Y), (X+1,Y+1) y (X,Y+1) que se corta en el
    /// PRIMER tile con objeto; en el clickeado vale cualquier tipo, en los otros tres sólo puertas. O sea que si
    /// (X+1,Y) tiene un objeto que no es puerta, no se sigue probando: no hay target de objeto y listo.</item>
    /// </list>
    /// La respuesta son los paquetes dedicados <c>CharMsgStatus</c>/<c>CharMsgStatusNPC</c>
    /// (Protocol.bas:17082/17227): el server manda DATOS —vida, nivel, bando, estados— y la línea la arma el
    /// cliente. Antes acá salía un ConsoleMsg con la frase ya hecha, que es lo que hace un chat, no una ficha.</summary>
    private void HandleLeftClick(ClientSession session, ByteQueueReader r)
    {
        byte x = r.ReadByte(), y = r.ReadByte();
        _loop.Enqueue(() =>
        {
            var p = session.Player;
            if (p is null) return;
            LookatTile(session, p, x, y);
        });
    }

    /// <summary>VB6 <c>LookatTile</c> (GameLogic.bas:655): resuelve QUÉ hay en un tile y lo deja seleccionado en
    /// <c>Target*</c>. Es UN helper porque el original lo llama desde varios lados y no sólo desde el clic: el
    /// <c>Case eSkill.magia</c> del <c>WorkLeftClick</c> (Protocol.bas:2927), robar (:2961), domar, el arco… — todo
    /// lo que "apunta a un tile" pasa por acá y después lee <c>flags.TargetUser/TargetNPC</c>. Vivía inline en el
    /// clic, así que el casteo no podía usarlo.</summary>
    private void LookatTile(ClientSession session, Player p, byte x, byte y)
    {
        // Gate de rango: el RECTÁNGULO 8×6 de RANGO_VISION, no el bloque de área de World.InView (que llega a
        // ~17 tiles y convertiría el click en un scanner de nombres al doble del alcance del original — y en
        // un /MATA a esa distancia). Es lo primero que hace LookatTile, y rebota SIN tocar el target.
        if (Math.Abs(p.Pos.X - x) > RangoVisionX || Math.Abs(p.Pos.Y - y) > RangoVisionY) return;

        var pos = new WorldPos(p.Pos.Map, x, y);
        bool foundSomething = false;
        // OJO: `InMapBounds` (GameLogic.bas:225) NO son los límites del mapa sino los del área jugable —
        // MinXBorder..MaxXBorder = 9..92 y MinYBorder..MaxYBorder = 7..94 (General.bas:1781). Un click fuera
        // de eso NO es un no-op: cae en la rama Else, que DESELECCIONA (el bloque `If FoundSomething = 0`).
        if (x >= GameLimits.MinXBorder && x <= GameLimits.MaxXBorder
            && y >= GameLimits.MinYBorder && y <= GameLimits.MaxYBorder)
        {
            p.TargetMap = pos.Map; p.TargetX = x; p.TargetY = y;
            foundSomething = LookatTileObjeto(p, pos);

            // ¿Personaje? El tile de ABAJO primero (ver arriba), y el NPC pisando al usuario en cada uno.
            Player? user = null; Npc? npc = null;
            if (y < GameLimits.MaxYBorder)
            {
                var abajo = new WorldPos(pos.Map, x, (byte)(y + 1));
                user = _world.PlayerAt(abajo);
                npc = _world.NpcAt(abajo);
            }
            if (user is null && npc is null)
            {
                user = _world.PlayerAt(pos);
                npc = _world.NpcAt(pos);
            }

            if (npc is not null)
            {
                foundSomething = true;
                p.TargetNpc = npc.CharIndex; p.TargetNpcTipo = (byte)npc.Template.NpcType;
                p.TargetUser = 0; p.TargetObj = 0;   // el Case 2 SÍ borra el target de objeto; el Case 1 no
                session.Send(ServerMessages.CharMsgStatusNpc(npc, p, _loop.Tick));
                // "Si el NPC se corre mandamos el texto sino lo lee el cliente" (GameLogic.bas:792): a una
                // criatura que ya no está en su tile de spawn se le manda además su diálogo sobre la cabeza,
                // por índice de locale (el cliente lo resuelve del NPC, por eso viaja el Numero y no el texto).
                if (npc.Pos != npc.StartPos && npc.Template.Description.Length > 1)
                    session.Send(ServerMessages.ChatOverHeadLocale(npc.CharIndex, npc.Template.Index, 4));
            }
            else if (user is not null)
            {
                // Gate del Case 1: `AdminInvisible = 0 Or (Privilegios And PlayerType.User)`. En VB el `And`
                // liga MÁS FUERTE que el `Or`, así que para un clickeador sin privilegios (Privilegios tiene
                // el bit User=1) la segunda mitad ya es verdadera y el gate SIEMPRE pasa: al GM escondido lo
                // "targetea" cualquier jugador común, y sólo otro GM se queda sin poder seleccionarlo.
                // Y ojo AdminInvisible, que es un flag DISTINTO de Invisible (modHechizos.bas:456 los compara
                // por separado): la invisibilidad de GM acá es la permanente, InvisibleUntilTick == 0.
                bool adminInvisible = user.Invisible && user.IsGm && user.InvisibleUntilTick == 0;
                if (!adminInvisible || (p.Privileges & PrivUser) != 0)
                {
                    foundSomething = true;
                    p.TargetUser = user.CharIndex; p.TargetNpc = 0; p.TargetNpcTipo = 0;
                    // `If UserList(tI).showName Then WriteCharMsgStatus` (GameLogic.bas:772): a alguien que
                    // apagó su nick con /SHOWNAME el click lo TARGETEA igual pero no devuelve su ficha.
                    if (user.ShowName) session.Send(ServerMessages.CharMsgStatus(user, p, _loop.Tick));
                }
                // Si el gate NO pasa, el original deja FoundChar = 1: no corre ninguna limpieza y el target
                // anterior sobrevive. Por eso acá tampoco se toca nada.
            }
            else
            {
                // `If FoundChar = 0`: sin personaje se limpian NPC y usuario, pero NO el objeto.
                p.TargetNpc = 0; p.TargetNpcTipo = 0; p.TargetUser = 0;
            }
        }

        // `If FoundSomething = 0` — el único lugar que limpia. Corre tanto dentro del mapa como en la rama
        // de fuera de bounds, y deja intacto TargetMap/X/Y: eso es lo que sigue viendo /DT.
        if (!foundSomething) ClearTarget(p);
    }

    /// <summary>La cadena de sondas de OBJETO de <c>LookatTile</c>: deja <c>TargetObj*</c> en el tile que matchea
    /// y devuelve si encontró algo. NO limpia cuando no matchea — el original sólo pone esos campos en cero en el
    /// bloque final <c>If FoundSomething = 0</c>, así que un click que agarra un personaje conserva el objeto
    /// targeteado antes.</summary>
    private bool LookatTileObjeto(Player p, WorldPos pos)
    {
        Span<(int dx, int dy, bool soloPuertas)> sondas = stackalloc (int, int, bool)[]
            { (0, 0, false), (1, 0, true), (1, 1, true), (0, 1, true) };
        foreach (var (dx, dy, soloPuertas) in sondas)
        {
            int tx = pos.X + dx, ty = pos.Y + dy;
            if (tx < 1 || tx > 100 || ty < 1 || ty > 100) continue;
            var t = new WorldPos(pos.Map, (byte)tx, (byte)ty);
            if (!_world.TryGetGround(t, out var obj) || obj.ObjIndex <= 0) continue;
            // Primer tile CON objeto: acá se corta la cadena ElseIf, matchee o no el filtro de puertas.
            if (soloPuertas && _data.Obj(obj.ObjIndex)?.IsDoor != true) return false;
            p.TargetObj = obj.ObjIndex; p.TargetObjMap = t.Map; p.TargetObjX = t.X; p.TargetObjY = t.Y;
            return true;
        }
        return false;
    }

    /// <summary>Deselecciona (VB6: el bloque <c>If FoundSomething = 0</c>). Borra los SIETE campos de target pero
    /// no el tile clickeado (<c>TargetMap/X/Y</c>), que el original deja en pie.</summary>
    private static void ClearTarget(Player p)
    {
        p.TargetNpc = 0; p.TargetNpcTipo = 0; p.TargetUser = 0;
        p.TargetObj = 0; p.TargetObjMap = 0; p.TargetObjX = 0; p.TargetObjY = 0;
    }

    /// <summary>DoubleClick (id 20): X, Y. On an NPC, shows its dialogue (Desc from NPCs.dat); a merchant
    /// or banker opens its window. Slice: dialogue + commerce/bank.</summary>
    /// <summary>Doble-clic sobre un tile SIN NPC: puede haber un objeto del mundo. VB6 <c>Accion</c>
    /// (Acciones.bas:332-390) sondea CUATRO tiles y no sólo el clickeado — (X,Y), (X+1,Y), (X+1,Y+1) y (X,Y+1) —
    /// y en los tres extra sólo acepta puertas. Eso es lo que deja abrir una puerta clickeándole el dintel o la
    /// mitad de al lado. Del resto de los tipos accionables (Correo/Leña/Pozos) el slice no modela ninguno.</summary>
    private void TryAccionParaObjeto(ClientSession session, Player p, byte x, byte y)
    {
        Span<(int dx, int dy, bool soloPuertas)> sondas = stackalloc (int, int, bool)[]
            { (0, 0, false), (1, 0, true), (1, 1, true), (0, 1, true) };
        foreach (var (dx, dy, soloPuertas) in sondas)
        {
            int tx = x + dx, ty = y + dy;
            if (tx is < 1 or > 255 || ty is < 1 or > 255) continue;
            var pos = new WorldPos(p.Pos.Map, (byte)tx, (byte)ty);
            if (!_world.TryGetGround(pos, out var obj) || obj.ObjIndex <= 0) continue;
            var info = _data.Obj(obj.ObjIndex);
            if (info is null) return;                       // el VB6 entra al Select igual y no matchea ningún Case
            if (info.IsDoor) { AccionParaPuerta(session, p, pos, info); return; }
            if (info.ObjType == ObjTipoPozo) { AccionParaPozo(session, p, pos, info); return; }
            // ENCENDER UNA FOGATA APAGADA (VB6 AccionParaRamita, Acciones.bas:514, llamada desde :348 cuando el
            // tile tiene FOGATA_APAG = 136 "Ramitas"). Faltaba, y con ella faltaba DESCANSAR: `HandleRest` exige
            // una fogata ENCENDIDA (63) al lado, en el mundo hay CERO colocadas, y sin este camino no había
            // forma de crear una. Las 2 ramitas que sí están puestas eran leña que nadie podía prender.
            if (obj.ObjIndex == FogataApagada)
            {
                if (DistanciaVB6(pos, p.Pos) > 2) { session.Send(ServerMessages.LocaleMsg(8)); return; }
                // Ni en tile seguro ni en mapa sin Pk (Acciones.bas:543).
                if (_data.IsSafeZone(pos.Map, pos.X, pos.Y) || !_data.IsPkMap(pos.Map))
                { session.Send(ServerMessages.LocaleMsg(191)); return; }
                // La tirada sale de Supervivencia: 1 en 3 hasta 5, 1 en 2 hasta 10, segura de 10 en adelante.
                int skill = p.Skills[GameLimits.SkillSupervivencia];
                int suerte = skill >= 10 ? 1 : skill >= 6 ? 2 : 3;
                if (_rng.Next(1, suerte + 1) != 1) { session.Send(ServerMessages.LocaleMsg(8)); return; }
                // Y en CIUDAD no prende ni con la tirada ganada (Acciones.bas:560): el original manda el 171
                // recién acá, después de gastar la tirada.
                if (_data.MapZone(pos.Map).Equals("CIUDAD", StringComparison.OrdinalIgnoreCase))
                { session.Send(ServerMessages.LocaleMsg(171)); return; }
                _world.SetGround(pos, Fogata, 1);
                _world.BroadcastToObservers(pos, ServerMessages.ObjectCreate(pos.X, pos.Y, Fogata, 0));
                session.Send(ServerMessages.LocaleMsg(170));
                RaiseSkill(session, p, GameLimits.SkillSupervivencia);
                return;
            }
            // POZOS (otPozos = 40). VB6 AccionParaPozos (Acciones.bas:410): a más de 3 tiles no hace NADA
            // —ni avisa— y adentro ramifica por SubTipo: 1 llena el maná, 2 la sed. Los dos gates son "sólo si
            // falta": estando lleno no pasa nada y no se emite un byte. Faltaba entero, y hay 10 pozos
            // colocados en 7 mapas del mundo real.
            if (info.ObjType == 40)
            {
                if (DistanciaVB6(pos, p.Pos) > 3) return;
                if (info.SubType == 1 && p.Mana < p.MaxMana)
                {
                    p.Mana = p.MaxMana;
                    session.Send(ServerMessages.UpdateMana((short)p.Mana));
                    session.Send(ServerMessages.LocaleMsg(173));
                    _world.BroadcastToObservers(p.Pos, ServerMessages.PlayWave(SndBeber, p.Pos.X, p.Pos.Y));
                }
                else if (info.SubType == 2 && p.Thirst < p.MaxThirst)
                {
                    p.Thirst = p.MaxThirst;
                    session.Send(ServerMessages.UpdateSed((byte)p.Thirst));
                    session.Send(ServerMessages.LocaleMsg(175));
                    _world.BroadcastToObservers(p.Pos, ServerMessages.PlayWave(SndBeber, p.Pos.X, p.Pos.Y));
                }
                return;
            }
            // Buzón de correo (otCorreo = 48). VB6 AccionParaCorreo (Acciones.bas:592): a más de 3 tiles
            // LocaleMsg 8, muerto LocaleMsg 77, y si pasa manda la bandeja + abre el formulario 6 + apaga el
            // indicador de "tenés carta". Es el único camino por el que el original lista el buzón.
            if (info.ObjType == 48)
            {
                if (DistanciaVB6(pos, p.Pos) > 3) { session.Send(ServerMessages.LocaleMsg(8)); return; }
                if (!p.IsAlive) { session.Send(ServerMessages.LocaleMsg(77)); return; }
                session.Send(ServerMessages.CorreoList(_world.PeekMail(p.Name)));
                session.Send(ServerMessages.AbrirFormularios(6));
                session.Send(ServerMessages.MensajeSigno(false));
                return;
            }
            // YUNQUE + MARTILLO puesto → abre la herrería (VB6 `Accion`, Acciones.bas:146). El gate es el
            // martillo EQUIPADO (`If .Invent.AnilloEqpObjIndex = MARTILLO_HERRERO`), o sea el mismo slot de
            // herramienta de §4r: sin el martillo en la mano, el doble clic sobre el yunque no hace nada.
            if (info.IsAnvil)
            {
                if (Equipment.EquippedOfCategory(p, _data, 6)?.Index == 389) SendCraftList(session, "blacksmith", "Yunque");
                return;
            }
            // LA FRAGUA, clickeada de una: no funde: el orden es al revés (primero se elige el mineral, y el
            // clic acá lo funde). Es el primer reflejo de cualquiera —el tester la clickeó y no pasó nada— así
            // que en vez de quedarse muda dice qué falta. No cambia ninguna regla: sólo explica el rechazo.
            if (info.IsForge)
            {
                session.Send(ServerMessages.ConsoleMsg(
                    "Para fundir: usá el mineral en tu inventario y después hacé clic en la fragua.", FuenteInfo));
                return;
            }
            if (soloPuertas) return;                        // las 3 sondas extra sólo aceptan otPuertas
            return;                                         // objeto sin acción modelada: el VB6 tampoco hace nada
        }
    }

    /// <summary>Conmuta la CERRADURA de la puerta clickeada (destrabar / trabar con llave) y deja el target
    /// apuntando al objeto nuevo, como el original (<c>.flags.TargetObj = ...</c> después del swap: sin eso,
    /// usar la llave dos veces seguidas operaría sobre el estado viejo).
    ///
    /// NO emite NADA al mundo, y eso es deliberado: la rama de la llave del VB6 (InvUsuario.bas:1736-1755)
    /// sólo escribe <c>MapData(...).ObjInfo.ObjIndex</c> y manda el LocaleMsg al que usó la llave. Ni
    /// <c>ObjectCreate</c> ni <c>Bloquear</c> — a diferencia de <c>AccionParaPuerta</c>, que sí los manda
    /// porque ahí la puerta se ABRE y hay que mover el bloqueo. Acá los dos estados están cerrados
    /// (52 "cerrada" ⇄ 53 "cerrada con llave", las dos con <c>abierta=1</c>), así que no hay nada que
    /// actualizarle al cliente. Emitir de más es tan infiel como emitir de menos.</summary>
    private bool CambiarPuerta(Player p, int nuevoIndex)
    {
        if (nuevoIndex <= 0 || p.TargetObjMap == 0) return false;
        if (_data.Obj(nuevoIndex) is null) return false;
        _world.SetGround(new WorldPos(p.TargetObjMap, p.TargetObjX, p.TargetObjY), (short)nuevoIndex, 1);
        p.TargetObj = (short)nuevoIndex;
        return true;
    }

    /// <summary>VB6 <c>AccionParaPuerta</c> (Acciones.bas:449). Abrir/cerrar es conmutar el ObjIndex del tile
    /// entre <c>IndexAbierta</c> e <c>IndexCerrada</c> y, sobre todo, mover el <c>Blocked</c> de DOS tiles —
    /// (X,Y) y (X-1,Y)— porque la puerta ocupa dos. El estado sale del objeto QUE ESTÁ en el tile: al abrir queda
    /// el del par abierto (cuyo Cerrada=0) y el próximo doble-clic lo cierra. No hay timer que las vuelva a
    /// cerrar: el estado vive en RAM hasta reiniciar.</summary>
    /// <summary><c>otPozos</c> (Declares.bas:618). El <c>SND_BEBER</c> ya está arriba, con las pociones.</summary>
    private const int ObjTipoPozo = 40;

    /// <summary>Los POZOS del mundo (VB6 <c>AccionParaPozos</c>, Acciones.bas:404), que no estaban portados.
    ///
    /// <para>Hay DIEZ puestos en el mapa —3 "Pozo Mágico" y 7 "Pozo de Agua"— y hasta ahora el doble clic no
    /// hacía nada con ellos. El <c>Subtipo</c> los distingue: 1 llena el maná ENTERO, 2 llena la sed. La data
    /// ya lo traía; lo que faltaba era esta rama.</para>
    ///
    /// <para>Dos detalles que parecen de estilo y no lo son: la distancia es EUCLÍDEA (<c>Distance</c>,
    /// Matematicas.bas:55), que en radio 3 NO es lo mismo que la Manhattan de los otros gates —la diagonal
    /// (2,2) entra—; y si ya estás lleno el original NO emite NADA, ni aviso ni sonido. El rebote por lejanía
    /// sí avisa.</para></summary>
    private void AccionParaPozo(ClientSession session, Player p, WorldPos pos, Data.ObjInfo info)
    {
        int dx = p.Pos.X - pos.X, dy = p.Pos.Y - pos.Y;
        if (dx * dx + dy * dy > 9) { session.Send(ServerMessages.LocaleMsg(8)); return; }   // `Distance > 3`
        if (info.SubType == 1)          // pozo de maná
        {
            if (p.Mana >= p.MaxMana) return;
            p.Mana = p.MaxMana;
            session.Send(ServerMessages.UpdateMana((short)p.Mana));
            session.Send(ServerMessages.LocaleMsg(173));
        }
        else if (info.SubType == 2)     // pozo de agua
        {
            if (p.Thirst >= p.MaxThirst) return;
            p.Thirst = p.MaxThirst;
            session.Send(ServerMessages.UpdateSed(p.Thirst));
            session.Send(ServerMessages.LocaleMsg(175));
        }
        else return;                    // otro subtipo: el Select del original no matchea y no hace nada
        _world.BroadcastToObservers(p.Pos, ServerMessages.PlayWave(SndBeber, p.Pos.X, p.Pos.Y));
    }

    private void AccionParaPuerta(ClientSession session, Player p, WorldPos pos, Data.ObjInfo info)
    {
        // VB6: `If Not (Distance(...) > 2)`. Distance (Matematicas.bas:55) es EUCLÍDEA — ojo, NO es el Chebyshev
        // que usan los gates de NPC de acá al lado: con la euclídea, la diagonal de 2 (dist 2.83) NO alcanza.
        int dx = p.Pos.X - pos.X, dy = p.Pos.Y - pos.Y;
        if (dx * dx + dy * dy > 4) { session.Send(ServerMessages.LocaleMsg(8)); return; }   // "Estás muy lejos."
        // Una puerta con llave NUNCA se abre con doble-clic (sólo con el ítem otLlaves de su misma `clave`,
        // que el slice no modela). Ojo: hay puertas con `clave` pero `Llave=0` — el gate es Llave, no clave.
        if (info.Llave != 0) { session.Send(ServerMessages.LocaleMsg(172)); return; }       // "La puerta está cerrada con llave."

        bool abriendo = info.Closed == 1;   // `Closed` sale de la clave `abierta`, invertida (ver ObjInfo)
        int nuevo = abriendo ? info.IndexAbierta : info.IndexCerrada;
        if (nuevo <= 0) return;
        _world.SetGround(pos, (short)nuevo, 1);
        _world.BroadcastToObservers(pos, ServerMessages.ObjectCreate(pos.X, pos.Y, (short)nuevo, 0));
        // El bloqueo de los DOS tiles de la puerta ((X,Y) y (X-1,Y): ocupa dos). El .csm los trae en 1, así que
        // abrir = desbloquear. Cada cambio va con su `Bloquear` (General.bas:112) = un BlockPosition a TODO el
        // mapa, no sólo al área: el cliente mantiene su propio mapa de bloqueos. El VB6 INVIERTE el orden entre
        // abrir y cerrar —abrir (X,Y) y después (X-1,Y); cerrar (X-1,Y) y después (X,Y)— y se respeta.
        var celdas = abriendo
            ? new[] { (x: pos.X, y: pos.Y), (x: (byte)(pos.X - 1), y: pos.Y) }
            : new[] { (x: (byte)(pos.X - 1), y: pos.Y), (x: pos.X, y: pos.Y) };
        foreach (var (cx, cy) in celdas)
        {
            _data.SetTileBlocked(pos.Map, cx, cy, !abriendo);
            _world.BroadcastToMap(pos.Map, ServerMessages.BlockPosition(cx, cy, !abriendo));
        }
        _world.BroadcastToObservers(pos, ServerMessages.PlayWave(SndPuerta, pos.X, pos.Y));
    }

    private const short SndPuerta = 5;   // VB6 Declares.bas:539 SND_PUERTA
    private const short SndEscudo = 37;  // VB6 Declares.bas:548 SND_ESCUDO — el clang del rechazo con escudo

    /// <summary>VB6 <c>EsObjetoFijo</c> (Modulo_UsUaRiOs.bas:2277): tipos de objeto que el server NUNCA le manda
    /// al cliente — los dibuja de su propia copia del mapa. Los valores salen de <c>eOBJType</c>
    /// (Declares.bas:584-626): Carteles=8, Árboles=4, Yacimiento=22, Correo=48. (El original repite otArboles
    /// dos veces en el Or; es cosmético.)</summary>
    private static bool EsObjetoFijo(int objType) => objType is 8 or 4 or 22 or 48;

    /// <summary>VB6 <c>Distancia</c> (Matematicas.bas:44): <b>Manhattan</b> (|dx|+|dy|) más 100 por cada mapa
    /// de diferencia. No confundir con <c>Distance</c> (:55), que es EUCLÍDEA y es la que usan las puertas y
    /// el yunque: el original tiene las dos y las usa en lugares distintos, así que copiar la que estaba a
    /// mano cambia el alcance real de la acción.</summary>
    private static int DistanciaVB6(WorldPos a, WorldPos b) =>
        Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y) + Math.Abs(a.Map - b.Map) * 100;
    // VB6 AI_NPC.bas:50-51: la caja de vision del jugador. `Accion` la usa como gate de todo doble-clic.
    private const int RangoVisionX = 8;
    private const int RangoVisionY = 6;

    private void HandleDoubleClick(ClientSession session, ByteQueueReader r)
    {
        byte x = r.ReadByte(), y = r.ReadByte();
        _loop.Enqueue(() =>
        {
            var p = session.Player;
            if (p is null) return;
            // VB6 `Accion` (Acciones.bas:20), su PRIMER statement: si el tile clickeado está fuera de la caja de
            // visión sale MUDO, antes de mirar si hay NPC u objeto. Sin esto el .NET contestaba LocaleMsg(8)
            // "Estás muy lejos" a un doble-clic lejano donde el VB6 no manda nada — un paquete espurio, la misma
            // clase de gap que viene cerrando el barrido del oráculo.
            if (Math.Abs(p.Pos.X - x) > RangoVisionX || Math.Abs(p.Pos.Y - y) > RangoVisionY) return;
            var npc = _world.NpcAt(new WorldPos(p.Pos.Map, x, y));
            // TRABAJO CONTINUO (VB6 `Accion`, Acciones.bas:25): lo PRIMERO que hace el original es cortar la faena
            // en curso, sea cual sea el destino del clic. Después decide si hay una nueva que empezar.
            if (p.Laborando) { p.Laborando = false; session.Send(ServerMessages.LocaleMsg(391, "", 1, 12)); }
            if (npc is null)
            {
                if (IniciarFaena(session, p, x, y)) return;   // agua/yacimiento/árbol según la herramienta puesta
                TryAccionParaObjeto(session, p, x, y);        // si no, puede haber una PUERTA (o un yunque)
                return;
            }
            // EL `p.Familiar.Existe` NO ES DECORACIÓN: esta rama corre ANTES del DeadCheck y del gate de
            // distancia, así que sin él un jugador cualquiera —de las quince clases que no llevan familiar—
            // que le hacía doble clic a una Veterinaria (hay 27 en el mundo) recibía "no tenés ningún
            // familiar" en vez de caer por el camino de siempre. Era el único lugar donde el subsistema se le
            // notaba a quien no lo usa.
            if (npc.IsVeterinaria && p.Familiar.Existe) { AtenderVeterinaria(session, p, npc); return; }
            if (npc.IsReviver)                                              // sacerdote Revividor (VB6 Acciones.bas:304) — atiende ANTES del DeadCheck
            {
                // MANHATTAN, no Chebyshev: `Distancia` (Matematicas.bas:43) suma los dos ejes. Con Chebyshev un
                // sacerdote en (+10,+10) quedaba a 10 y para el original está a 20 (Acciones.bas:306). Y va
                // PRIMERO, antes de mirar si estás vivo: el original gatea la distancia y recién después parte.
                if (DistanciaVB6(p.Pos, npc.Pos) > 10)
                { session.Send(ServerMessages.LocaleMsg(8)); return; }      // VB6: distancia <= 10 (LocaleMsg 8 si lejos)
                if (!p.IsAlive)
                {
                    RevivePlayer(_world, _data, p);   // HP full + apariencia + LocaleMsg(90)
                    // La frase del sacerdote NO es texto libre: el original manda `ChatOverHeadLocale(charIndex,
                    // 11, 1)` (Acciones.bas:313), o sea el MODO 1, que el cliente resuelve como las palabras
                    // mágicas del hechizo 11 — Resucitar. Acá iba un `ChatOverHead` con "¡Levántate y anda!"
                    // escrito por nosotros: otro paquete y otro texto.
                    _world.BroadcastToObservers(npc.Pos, ServerMessages.ChatOverHeadLocale(npc.CharIndex, 11, 1));
                    return;
                }
                // VIVO: el sacerdote CURA al que llega herido (Acciones.bas:316-325). Faltaba entero — acá
                // salía un ConsoleMsg de "aún no necesitas mis servicios" que el original no manda, y el
                // jugador se quedaba sin la curación. A HP lleno el original calla, y por eso acá también.
                if (p.Hp >= p.MaxHp) return;
                p.PoisonedUntilTick = 0;                                    // `.flags.Envenenado = 0`
                p.Incinerado = false; p.IncineradoCounter = 0; p.IncineradoGolpes = 0;
                p.Hp = p.MaxHp;
                _world.BroadcastToObservers(p.Pos, ServerMessages.PlayWave(SndSanar, p.Pos.X, p.Pos.Y));
                _world.BroadcastToObservers(p.Pos, ServerMessages.EfectoCharParticula(p.CharIndex, 28, 100, false));
                _world.BroadcastToObservers(npc.Pos, ServerMessages.ChatOverHeadLocale(npc.CharIndex, 32, 1));  // palabras de Sanar
                session.Send(ServerMessages.UpdateHP((short)p.Hp));
                return;
            }
            if (DeadCheck(session, p)) return;   // un muerto sólo puede acudir al sacerdote (arriba); nada de comercio/banco/oficios
            // VB6 Sub Accion: interactuar con un NPC (comercio/banco/entrenador/mesa) exige estar a <= 3 tiles,
            // medidos con `Distancia` — MANHATTAN (Acciones.bas:174, :208, :220, :239, :279, :295). Acá estaba
            // en Chebyshev, o sea que un mercader en (+3,+3) contaba como 3 cuando para el original está a 6:
            // se comerciaba y se usaba el banco en diagonal desde el doble de lejos. El comentario de
            // `AccionParaPuerta`, treinta líneas más abajo, ya decía "ojo, NO es el Chebyshev que usan los
            // gates de NPC de acá al lado" — estaba anotado y faltaba sacar la conclusión.
            if (DistanciaVB6(p.Pos, npc.Pos) > 3)
            { session.Send(ServerMessages.LocaleMsg(8)); return; }
            if (npc.FactionConvert != 0) { DoConvertir(session, p, npc); return; }   // Convertidor: da la ciudadanía
            if (npc.WorkbenchTrade is not null) { SendCraftList(session, npc.WorkbenchTrade, npc.Name); return; }
            // Doble clic a un mercader → la tienda, salvo que no te atienda por tu facción (Acciones.bas:180-197,
            // el mismo bloque que el `CommerceStart`). Es el camino por el que el cliente web abre el comercio.
            if (npc.IsMerchant) { if (MercaderAtiende(session, p, npc)) OpenCommerce(session, p, npc); return; }
            if (npc.IsBanker) { OpenBank(session, p, npc); return; }        // double-click a banker → bank window
            // Sólo la lista: la rama Entrenador del original (Acciones.bas:271) manda `WriteTrainerCreatureList`
            // y nada más. Acá iba además un ConsoleMsg ("estas son las criaturas que puedo entrenar") que el VB6
            // no emite — el mismo patrón de mensaje espurio que el barrido del oráculo cerró 16 veces.
            if (npc.TrainerCreatures is not null) { session.Send(ServerMessages.TrainerCreatureList(npc.TrainerCreatures)); return; }
            // DESVIACIÓN DELIBERADA DEL SLICE (decidida, no olvidada). El original CALLA acá: su `Accion` rutea
            // por NPCtype y no tiene rama `Else`, así que un NPC sin rol no contesta nada al doble clic. Se
            // mantiene el diálogo porque sin él las criaturas quedan mudas.
            //
            // El mecanismo con el que el VB6 lo compensa está portado A MEDIAS, y la mitad que falta es la que
            // haría sobrar esta línea. Son dos caminos distintos y sólo uno pasa por la red:
            //   · el NPC que se CORRIÓ de su spawn: el server manda `ChatOverHeadLocale(..., 4)` desde
            //     `LookatTile` — eso ya sale de acá y desde el 2026-08-18 el cliente además lo DIBUJA;
            //   · el NPC quieto en su spawn: "lo lee el cliente" (GameLogic.bas:792). El original lo saca de
            //     `MapData(x, y).NPCIndex`, o sea de su propio archivo de mapa, y nuestro cliente no tiene los
            //     índices de NPC por tile.
            // Mientras esa segunda mitad no esté, sacar este ConsoleMsg dejaría mudo al NPC parado, que es el
            // caso normal. `npc_dialogue_flow` lo asserta.
            var desc = npc.Template.Description;
            session.Send(ServerMessages.ConsoleMsg(
                string.IsNullOrWhiteSpace(desc) ? $"{npc.Name} no tiene nada que decirte." : $"{npc.Name}: {desc}", FuenteInfo));
        });
    }

    /// <summary>Sends a workbench's craftable list (BlacksmithWeapons/CarpenterObjects/…) to the client.</summary>
    private void SendCraftList(ClientSession session, string trade, string npcName)
    {
        if (trade == "blacksmith")
        {
            // El VB6 manda UNA lista por tipo: armas, armaduras, cascos y escudos (paquetes 48/49/50/51).
            session.Send(ServerMessages.BlacksmithWeapons(CraftItems(_data.BlacksmithWeapons)));
            session.Send(ServerMessages.BlacksmithArmors(CraftItems(_data.BlacksmithArmors)));
            session.Send(ServerMessages.BlacksmithHelmets(CraftItems(_data.BlacksmithHelmets)));
            session.Send(ServerMessages.BlacksmithShields(CraftItems(_data.BlacksmithShields)));
            session.Send(ServerMessages.ConsoleMsg($"{npcName}: aquí puedes forjar armas, armaduras, cascos y escudos. Elige una receta.", FuenteInfo));
            return;
        }
        byte[]? pkt = trade switch
        {
            "carpenter" => ServerMessages.CarpenterObjects(CraftItems(_data.CarpenterItems)),
            "tailor" => ServerMessages.SastreObjects(CraftItems(_data.TailorItems)),
            "alchemy" => ServerMessages.AlquimiaObjects(CraftItems(_data.AlchemyItems)),
            _ => null,
        };
        if (pkt is null) return;
        session.Send(pkt);
        session.Send(ServerMessages.ConsoleMsg($"{npcName}: aquí puedes fabricar. Elige una receta.", FuenteInfo));
    }

    private List<Data.ObjInfo> CraftItems(IReadOnlySet<int> set)
    {
        var list = new List<Data.ObjInfo>();
        foreach (var i in set) { var o = _data.Obj((short)i); if (o is not null) list.Add(o); }
        return list;
    }

    // --- Misc player actions (change description / leave faction / transfer gold) ---

    /// <summary>ChangeDescription (id 81): sets the character's description (persisted in .chr [INIT] Desc).</summary>
    private void HandleChangeDescription(ClientSession session, ByteQueueReader r)
    {
        string desc = r.ReadAsciiString();
        _loop.Enqueue(() =>
        {
            var p = session.Player;
            if (p is null) return;
            if (DeadCheck(session, p)) return;   // VB6 HandleChangeDescription: muerto -> LocaleMsg 77
            // VB6 exige AsciiValidos (sólo a-z + espacio). Acá basta con rechazar los caracteres de control
            // (< 32, incluye CR/LF) para cerrar la inyección de INI en el .chr; IniFile.Set ya es la red final.
            if (desc.Any(c => c < ' ')) { session.Send(ServerMessages.LocaleMsg(392)); return; }
            p.Description = desc;
            session.Send(ServerMessages.LocaleMsg(111));
        });
    }

    /// <summary>RetirarFaccion (id 98): renunciar a tu status faccionario. VB6 HandleRetirarFaccion (Protocol.bas:15526):
    /// muerto -> Exit Sub; newbie (nivel &lt;= 14) -> LocaleMsg(425); "If esRene Or GuildIndex &gt; 0 Then Exit Sub"
    /// (ya-criminal o en un clan salen en silencio). Después un Select por status: Armada(5)->ciudadano(2);
    /// Caos(4)->renegado(1); Case Else (ciudadano puro 2/3)->renegado(1) + Hogar=cRinkel. En todos broadcastea
    /// CharStatus (recolor del nick), sin texto. Mapeo del slice (criminal=0≈esRene, ciudadano=1≈esCiuda): sólo la
    /// Armada vuelve a ciudadano; el Caos Y el ciudadano-puro terminan criminales — renunciar a la ciudadanía te
    /// vuelve renegado, igual que el VB6.</summary>
    private void HandleRetirarFaccion(ClientSession session)
    {
        _loop.Enqueue(() =>
        {
            var p = session.Player;
            if (p is null) return;
            if (!p.IsAlive) return;                                        // VB6 DeadCheck
            if (p.Level <= 14) { session.Send(ServerMessages.LocaleMsg(425, "15")); return; }  // VB6: newbie -> LocaleMsg(425, LimiteNewbie)
            // OJO: se decide sobre el status PERSISTIDO, no sobre el vivo. Bajo override de staff el vivo vale
            // 7/8/9 (el byte que pinta el nick con color de GM) y NINGUNA rama del Select matchea, así que
            // caía en el Case Else y `CambiarFaccion` escribía CRIMINAL dentro de `StatusCivilBajoStaff`,
            // encima de la facción real. En pantalla no pasaba nada —el CharStatus difunde el 9 vivo—, y el
            // día que el host lo sacaba del ini, el GM que había sido Armada Real volvía renegado.
            // `CambiarFaccion` escribe justo en ese campo, así que la decisión tiene que leerlo a él.
            byte real = p.FactionStatusPersistido;
            // VB6 (Protocol.bas:15545): ya-criminal (esRene) o miembro de un clan -> Exit Sub silencioso.
            if (real == Player.StatusCriminal || !string.IsNullOrEmpty(p.GuildName)) return;
            // VB6 Select Status: Milicia(6)→republicano(3); Armada(5)→ciudadano(2); Caos(4)→renegado(1);
            // Case Else (civil puro: ciudadano/republicano)→renegado(1) + Hogar=cRinkel.
            bool eraCivilPuro = real is Player.StatusCiudadano or Player.StatusRepublicano;
            p.CambiarFaccion(real == Player.StatusMilicia ? Player.StatusRepublicano
                             : real == Player.StatusArmada ? Player.StatusCiudadano
                             : Player.StatusCriminal);
            p.FactionRank = 0;
            if (eraCivilPuro) p.HomeCity = Cities.Rinkel;   // VB6 Case Else: Hogar = cRinkel al perder la ciudadanía
            // EL AVISO DE LA BAJA (LocaleMsg 384). Lo mandan las TRES ramas de expulsión —Real, Caos y
            // Milicia (ModFacciones.bas:616, :638 y :672)— y NINGUNA de las civiles: al ciudadano puro que
            // renuncia el original no le contesta nada. Va antes del CharStatus porque adentro del
            // `Expulsar*` el aviso es la primera línea.
            //
            // Lo que esas rutinas hacen además —desequipar la armadura/escudo faccionario y
            // `QuitarItemsFaccionarios`— es MUERTO POR DATA en este install: ningún objeto del obj.dat trae
            // las banderas Real/Caos/Milicia (el mismo motivo por el que el LocaleMsg 260 no se porta).
            if (!eraCivilPuro) session.Send(ServerMessages.LocaleMsg(384));
            // Y el CharStatus sale DOS VECES cuando la baja es de una facción: uno lo manda `Expulsar*`
            // (ModFacciones.bas:622) y el otro el propio HandleRetirarFaccion al salir del Select
            // (Protocol.bas:15570). Emitir de menos es tan infiel como emitir de más, y este es de los que
            // sólo caza el oráculo.
            var charStatus = ServerMessages.CharStatus(p.CharIndex, (byte)p.FactionStatus);
            if (!eraCivilPuro) _world.BroadcastToObservers(p.Pos, charStatus);
            _world.BroadcastToObservers(p.Pos, charStatus);
            // Acá había un `UpdateUserStats` que el original NO manda por este camino (el handler termina en
            // el CharStatus y el bloque de la meditación). Eran ~26 bytes de más en el cable.
        });
    }

    /// <summary><c>MAXORO</c> (Declares.bas:344) = noventa millones: el TECHO del oro en mano.
    ///
    /// <para>El original lo aplica sobre el TOTAL después de sumar, en los dos lugares donde entra oro de a
    /// montones —vender (Comercio.bas:169) y robar (Trabajo.bas:1591)—, y lo usa además como tope de lo que se
    /// puede transferir de una vez. Acá no existía: el robo tenía anotado "el VB6 capea a MAXORO, el robo es de
    /// a cientos, nunca llega", que razona sobre lo que ENTRA y no sobre lo que queda. Con noventa millones en
    /// la billetera el original te devuelve a noventa millones y este port te dejaba pasar.</para></summary>
    private const long MaxOro = 90_000_000;

    /// <summary>TransferGOLD (id 102): nombre(ASCIIString) y DESPUÉS monto(Long) — el orden importa.
    ///
    /// <para>Esto movía la BILLETERA entre dos jugadores CONECTADOS. El original (VB6 HandleTransferGOLD,
    /// Protocol.bas:15821) mueve la <b>bóveda</b>, y el destinatario <b>no tiene por qué estar conectado</b>: si
    /// <c>NameIndex</c> no lo encuentra pero el <c>.chr</c> existe, le acredita el saldo escribiendo el archivo.
    /// Eso es lo que el juego llama "tu cuenta Goliath", y era justamente para lo que sirve: mandarle plata a
    /// alguien que no está. Nuestro handler le contestaba "Destinatario inválido" a media función.</para>
    ///
    /// <para>El orden de las compuertas es parte del contrato, así que va tal cual: existe → no soy yo → monto
    /// en rango → me alcanza la bóveda. La tercera <b>no tiene rama negativa</b> en el original: un monto de 0 o
    /// por encima de <see cref="MaxOro"/> se descarta sin decir nada, y el silencio también es una respuesta.</para>
    ///
    /// <para>El <c>WriteUpdateGold</c> del original manda <c>Stats.GLD</c> —la billetera, que en esta operación
    /// no cambió—, no el saldo del banco. Se replica: el paquete sale igual y con el oro de la mano.</para>
    ///
    /// <para>La rama offline toca DISCO adentro del game loop, que es lo que en general no se hace acá. Va así
    /// a propósito: es un <c>.chr</c> (0,75 ms medido) y no se puede martillar, porque el techo de cadencia por
    /// sesión corre ANTES de despachar cualquier paquete (<c>ClientSession</c>). Diferirlo obligaría a llevar
    /// un saldo pendiente por nombre y a resolverlo si el tipo se conecta en el medio — más estado y más
    /// formas de perder oro que el milisegundo que ahorra.</para></summary>
    private void HandleTransferGold(ClientSession session, ByteQueueReader r)
    {
        string targetName = r.ReadAsciiString();
        int amount = r.ReadLong();
        _loop.Enqueue(() =>
        {
            var p = session.Player;
            if (p is null) return;
            var target = _world.PlayerByName(targetName);
            // `PersonajeExiste` del original es un FileExist sobre el .chr (Admin.bas:221): vale para el que está
            // conectado y para el que no.
            if (target is null && !_db.CharacterExists(targetName))
            { session.Send(ServerMessages.ConsoleMsg("El personaje no existe.", FuenteInfo)); return; }
            if (target is not null && target.CharIndex == p.CharIndex)
            { session.Send(ServerMessages.ConsoleMsg("Objetivo inválido.", FuenteInfo)); return; }
            if (amount <= 0 || amount > MaxOro) return;                        // sin rama negativa: calla
            if (p.Bank < amount)
            { session.Send(ServerMessages.ConsoleMsg("No tienes suficientes monedas de oro depositadas en tu cuenta.", FuenteInfo)); return; }

            // ACREDITAR PRIMERO, DESCONTAR DESPUÉS. Al revés —como lo hace el original— el oro se destruye si la
            // escritura del `.chr` falla: el `.chr` puede existir para el chequeo y no poder leerse un instante
            // después (el backup lo está copiando), y ahí la excepción sube con la bóveda ya descontada.
            if (target is null)
            {
                if (_db.AcreditarBovedaOffline(targetName, amount) is null)     // VB6: GetVar + WriteVar sobre el .chr
                { session.Send(ServerMessages.ConsoleMsg("El personaje no existe.", FuenteInfo)); return; }
                p.Bank -= amount;
            }
            else
            {
                p.Bank -= amount;
                target.Bank += amount;
                target.Connection?.Send(ServerMessages.UpdateGold(target.Gold));
                // El espacio que falta entre el nombre y el "ha" es del original (`"¡" & .Name & "ha transferido "`,
                // Protocol.bas:15847). Va tal cual: no es un typo nuestro.
                target.Connection?.Send(ServerMessages.ConsoleMsg(
                    $"¡{p.Name}ha transferido {amount} monedas de oro a tu cuenta Goliath!.", FuenteInfo));
                target.Connection?.Send(ServerMessages.ConsoleMsg(
                    "¡Has recibido un nuevo mensaje de Finanzas Goliath, ve a un correo local para leerlo.", FuenteInfo));
            }
            session.Send(ServerMessages.UpdateGold(p.Gold));                   // la billetera, como el original
            session.Send(ServerMessages.ConsoleMsg(
                $"¡Se ha realizado la transferencia correctamente! Tienes {p.Bank} monedas de oro en tu cuenta.", FuenteInfo));
            _world.BroadcastToObservers(p.Pos, ServerMessages.PlayWave(SndOro, p.Pos.X, p.Pos.Y));
        });
    }

    /// <summary>ModifySkills (id 32): 27 bytes of per-skill increments. Spends free skill points.</summary>
    private void HandleModifySkills(ClientSession session, ByteQueueReader r)
    {
        var inc = new byte[GameLimits.NumSkills + 1];
        for (int i = 1; i <= GameLimits.NumSkills; i++) inc[i] = r.ReadByte();
        _loop.Enqueue(() =>
        {
            var p = session.Player;
            if (p is null) return;
            int spent = 0;
            for (int i = 1; i <= GameLimits.NumSkills && p.FreeSkillPoints > 0; i++)
            {
                int add = Math.Min(inc[i], (int)p.FreeSkillPoints);
                add = Math.Min(add, 100 - p.Skills[i]); // cap skills at 100
                if (add <= 0) continue;
                p.Skills[i] = (byte)(p.Skills[i] + add);
                p.FreeSkillPoints -= (short)add; spent += add;
            }
            if (spent == 0) return;   // VB6: sin puntos asignados, no responde
            session.Send(ServerMessages.SendSkills(p.Skills));
            session.Send(ServerMessages.ConsoleMsg($"Asignaste {spent} punto(s) de skill. Restantes: {p.FreeSkillPoints}.", FuenteInfo));
        });
    }

    /// <summary>CloseGuild (id 107): the leader disbands the clan.</summary>
    private void HandleCloseGuild(ClientSession session)
    {
        _loop.Enqueue(() =>
        {
            var p = session.Player;
            if (p is null) return;
            // DOS GATES QUE FALTABAN, y van ANTES del de liderazgo (VB6 Protocol.bas:16632 y :16637): un
            // cadáver no cierra el clan, y en mapa PK tampoco. Sin ellos, el líder disolvía el clan muerto y
            // en medio de una pelea. Los dos rechazan con FONTTYPE_INFO, como el resto de este handler.
            if (!p.IsAlive) { session.Send(ServerMessages.ConsoleMsg("¡¡Estás muerto!!", FuenteInfo)); return; }
            if (_data.IsPkMap(p.Pos.Map))
            { session.Send(ServerMessages.ConsoleMsg("¡No puedes cerrar el clan en zona insegura!", FuenteInfo)); return; }
            // El /CERRARCLAN es la excepción: rechaza con FONTTYPE_INFO, no con la del clan.
            var g = RequireGuildLeader(session, session.Player, announceNoGuild: true, fuente: FuenteInfo);
            if (g is null) return;
            foreach (var member in _world.GuildMembersOnline(g.Name).ToList())
            {
                member.GuildName = "";
                member.Connection?.Send(ServerMessages.ConsoleMsg($"El clan <{g.Name}> ha sido disuelto.", FuenteInfo));
            }
            _world.RemoveGuild(g.Name);
            PersistGuilds();
        });
    }

    /// <summary>Denounce (id 86): text. Sends a report to every online GM.</summary>
    private void HandleDenounce(ClientSession session, ByteQueueReader r)
    {
        string text = r.ReadAsciiString();
        _loop.Enqueue(() =>
        {
            var p = session.Player;
            if (p is null || string.IsNullOrWhiteSpace(text)) return;
            int gms = 0;
            foreach (var gm in _world.Players.Where(pl => pl.IsGm))
            { gm.Connection?.Send(ServerMessages.ConsoleMsg($"[DENUNCIA de {p.Name}] {text}", FuenteClanMsg)); gms++; }
            session.Send(ServerMessages.LocaleMsg(85));   // VB6 HandleDenounce: WriteLocaleMsg(85) siempre (el broadcast a admins va aparte)
        });
    }

    /// <summary>
    /// SeleccionarHogar (id 103): fija a qué ciudad manda RegresarHogar. El byte es el id de ciudad (1..12).
    ///
    /// DIVERGENCIA DELIBERADA del stack .NET+web (ver fase5/GAPS-CODIGO-MUERTO.md). En el VB6 el byte NO es la
    /// ciudad sino un SUBCOMANDO (0 = pedirle el cambio al sacerdote Revividor cercano, 1 = confirmar), y la
    /// ciudad se DERIVA del mapa donde estás parado — y encima con dos bugs de precedencia que hacen que un
    /// ciudadano común no pueda elegir NINGUNA ciudad y que la rama República/Milicia sea inalcanzable. Este
    /// stack deja elegir la ciudad directo de un dropdown (el cliente web manda <c>SeleccionarHogar(cityId)</c>):
    /// más simple, sin los bugs, y el ciclo elegir-hogar → morir → RegresarHogar está verificado (home_flow).
    ///
    /// <para>Lo que SÍ se porta es el <b>gate del sacerdote</b>: hay que tener clickeado a un Revividor a cinco
    /// baldosas o menos (VB6 :16283-16297). No es parte de la divergencia —esa es sobre CÓMO se elige la ciudad,
    /// no sobre desde dónde— y sin él el hogar se cambiaba desde cualquier punto del mundo, lo que convertía a
    /// <c>RegresarHogar</c> en un teletransporte gratis a cualquier ciudad. Con el gate, mudarse cuesta el viaje
    /// hasta el templo de destino, que es la economía que el original quería.</para>
    /// </summary>
    private void HandleSelectHome(ClientSession session, ByteQueueReader r)
    {
        byte city = r.ReadByte();
        _loop.Enqueue(() =>
        {
            var p = session.Player;
            if (p is null) return;
            var npc = TargetNpcDe(p);
            if (npc is null || !npc.IsReviver)
            { session.Send(ServerMessages.ConsoleMsg("Primero tienes que seleccionar un sacerdote, haz click izquierdo sobre él.", FuenteInfo)); return; }
            if (DistanciaVB6(p.Pos, npc.Pos) > 5)
            { session.Send(ServerMessages.ConsoleMsg("Estás muy lejos del sacerdote.", FuenteInfo)); return; }
            if (city < 1 || city > 12) { session.Send(ServerMessages.ConsoleMsg("Ciudad invalida.", FuenteInfo)); return; }
            p.HomeCity = city;
            session.Send(ServerMessages.ConsoleMsg($"Elegiste la ciudad {city} como tu hogar.", FuenteInfo));
        });
    }

    // --- More player actions: uptime / home / reorder / kick / friends ---

    /// <summary>UpTime (id 76): reports how long the server has been running.</summary>
    private void HandleUpTime(ClientSession session)
    {
        _loop.Enqueue(() =>
        {
            var up = TimeSpan.FromMilliseconds(_loop.Tick * (long)GameLoop.TickMs);
            // El formato es el del original (VB6 HandleUpTime, Protocol.bas:5531): arma la frase de adentro
            // hacia afuera y omite los tramos que valen cero, con "día" en singular cuando es uno solo.
            string texto = $"{up.Seconds} segundos.";
            if (up.TotalMinutes >= 1) texto = $"{up.Minutes} minutos, " + texto;
            if (up.TotalHours >= 1) texto = $"{up.Hours} horas, " + texto;
            if (up.TotalDays >= 1) texto = $"{up.Days} {(up.Days == 1 ? "día" : "días")}, " + texto;
            session.Send(ServerMessages.ConsoleMsg($"Server Online: {texto}", FuenteInfo));
        });
    }

    /// <summary>RegresarHogar (id 99): teleport the player back to the spawn/home tile.</summary>
    private void HandleRegresarHogar(ClientSession session)
    {
        _loop.Enqueue(() =>
        {
            var p = session.Player;
            if (p is null || p.IsAlive) return;  // VB6 HandleRegresarHogar: solo con Muerto=1
            // Al tile Dead de la ciudad-hogar elegida (SeleccionarHogar -> p.HomeCity); Ullathorpe por defecto.
            WarpAlHogar(session, p, Cities.DeadTile(p.HomeCity));
        });
    }

    /// <summary>El viaje a la ciudad-hogar: lo comparten <c>RegresarHogar</c> y la RUNA, que en el original
    /// llaman los dos a <c>WarpUserChar(..., True)</c> con el mismo destino. No emite mensaje de llegada
    /// (el VB6 tampoco) y baja del barco si venía navegando, porque el hogar es tierra.</summary>
    private void WarpAlHogar(ClientSession session, Player p, WorldPos destino)
    {
        var from = p.Pos;
        p.Pos = destino;
        DisembarkIfStranded(p);
        if (p.Pos.Map != from.Map) session.Send(ServerMessages.ChangeMap(p.Pos.Map));
        session.Send(ServerMessages.PosUpdate(p.Pos.X, p.Pos.Y));
        RefreshAreaVisibility(_world, p, from, p.Pos);
        WarpMascotas(p);
        WarpFamiliar(p);
        _world.BroadcastToObservers(p.Pos, ServerMessages.CharacterMove(p.CharIndex, p.Pos.X, p.Pos.Y), p.CharIndex);
    }

    /// <summary>MoveSpell (id 38): upwards(byte), slot(byte). Swaps the spell with its neighbour.</summary>
    private void HandleMoveSpell(ClientSession session, ByteQueueReader r)
    {
        bool upwards = r.ReadByte() != 0;
        byte slot = r.ReadByte();
        _loop.Enqueue(() =>
        {
            var p = session.Player;
            if (p is null) return;
            int other = upwards ? slot - 1 : slot + 1;
            if (slot < 1 || slot > GameLimits.MaxUserSpells) return;
            // Ya esta en el borde: el original NO calla, avisa con el 10 (modHechizos.bas DesplazarHechizo,
            // :1872 arriba y :1890 abajo). Aca se salia en silencio y la tecla parecia no responder.
            if (other < 1 || other > GameLimits.MaxUserSpells) { session.Send(ServerMessages.LocaleMsg(10)); return; }
            if (p.Spells[slot] == 0) return;   // VB6: ranura vacia, nada que reordenar
            (p.Spells[slot], p.Spells[other]) = (p.Spells[other], p.Spells[slot]);
            session.Send(ServerMessages.ChangeSpellSlot(slot, p.Spells[slot]));
            session.Send(ServerMessages.ChangeSpellSlot((byte)other, p.Spells[other]));
        });
    }

    /// <summary>SwapObjects (id 95): slotA(byte), slotB(byte). Swaps two inventory slots.</summary>
    private void HandleSwapObjects(ClientSession session, ByteQueueReader r)
    {
        byte a = r.ReadByte(), b = r.ReadByte();
        _loop.Enqueue(() =>
        {
            var p = session.Player;
            if (p is null || a < 1 || a > GameLimits.MaxInventorySlots || b < 1 || b > GameLimits.MaxInventorySlots) return;
            (p.Inventory[a], p.Inventory[b]) = (p.Inventory[b], p.Inventory[a]);
            var ia = p.Inventory[a]; var ib = p.Inventory[b];
            session.Send(ServerMessages.ChangeInventorySlot(a, ia.ObjIndex, ia.Amount, ia.Equipped, 0f, (byte)(ia.IsEmpty ? 0 : 1)));
            session.Send(ServerMessages.ChangeInventorySlot(b, ib.ObjIndex, ib.Amount, ib.Equipped, 0f, (byte)(ib.IsEmpty ? 0 : 1)));
        });
    }

    /// <summary>GuildKickMember (id 56): the leader removes a member from the clan.</summary>
    private void HandleGuildKick(ClientSession session, ByteQueueReader r)
    {
        string name = r.ReadAsciiString().Trim();
        _loop.Enqueue(() =>
        {
            var g = RequireGuildLeader(session, session.Player);
            if (g is null) return;
            if (string.Equals(name, g.LeaderName, StringComparison.OrdinalIgnoreCase)) { session.Send(ServerMessages.ConsoleMsg("No puedes expulsarte a ti mismo.", FuenteClan)); return; }
            if (!g.Members.Remove(name)) { session.Send(ServerMessages.ConsoleMsg("Ese personaje no es del clan.", FuenteClan)); return; }
            PersistGuilds();
            var kicked = _world.PlayerByName(name);
            if (kicked is not null) { kicked.GuildName = ""; kicked.Connection?.Send(ServerMessages.ConsoleMsg($"Has sido expulsado del clan <{g.Name}>.", FuenteClan)); }
            session.Send(ServerMessages.ConsoleMsg($"Expulsaste a {name} del clan.", FuenteClan));
        });
    }

    /// <summary>DesconectarCuenta (VB6 HandleDesconectarCuenta, Protocol.bas:17539): un Dios o Admin le corta la
    /// sesión a OTRO personaje.
    ///
    /// <para>Acá el paquete leía el nombre y cerraba la sesión DEL QUE LO MANDABA, sin mirar privilegios: un GM
    /// que escribía <c>/desconectar Fulano</c> se echaba a sí mismo. El cliente ya documentaba la conducta buena
    /// ("echa la sesión de ese personaje de la cuenta"), así que el comando estaba roto de punta a punta.</para>
    ///
    /// <para>El gate del original es <c>Dios Or Admin</c> —no alcanza con ser SemiDiós ni RoleMaster— y, como
    /// tantos de este archivo, es SILENCIOSO: al que no tiene rango no le contesta nada.</para></summary>
    private void HandleDesconectarCuenta(ClientSession session, ByteQueueReader r)
    {
        string nombre = r.ReadAsciiString().Replace('+', ' ').Trim();   // VB6: el `+` viaja en lugar del espacio
        _loop.Enqueue(() =>
        {
            var p = session.Player;
            if (p is null || (p.Privileges & (PrivDios | PrivAdmin)) == 0) return;   // gate silencioso
            var otro = _world.PlayerByName(nombre);
            if (otro is null)
            { session.Send(ServerMessages.ConsoleMsg("Usuario offline o inexistente.", FuenteInfo)); return; }
            if (string.Equals(otro.Name, p.Name, StringComparison.OrdinalIgnoreCase))
            { session.Send(ServerMessages.ConsoleMsg("No puedes desconectarte a ti mismo.", FuenteInfo)); return; }
            otro.Connection?.Close("desconectado por un GM");
            session.Send(ServerMessages.ConsoleMsg($"Desconectaste a {otro.Name}", FuenteInfo));   // el original no le pone punto
        });
    }

    /// <summary><c>MAXAMIGOS</c> (Declares.bas:69): cinco amigos, y el arreglo del original es de ese tamaño fijo.</summary>
    private const int MaxAmigos = 5;

    /// <summary>Los cinco rechazos de <c>IntentarAgregarAmigo</c> (VB6 GameLogic.bas:1260), en su orden. Devuelve
    /// el motivo, o <c>null</c> si se puede agregar.</summary>
    private static string? MotivoParaNoAgregar(Player? yo, Player? otro)
    {
        if (otro is null || yo is null) return "Usuario offline.";
        if (ReferenceEquals(yo, otro)) return "No puedes agregarte a tu propia lista de amigos.";
        if (yo.Friends.Count >= MaxAmigos) return "La lista de amigos está llena.";
        if (otro.Friends.Count >= MaxAmigos) return "La lista de amigos del jugador está llena.";
        if (yo.Friends.Any(f => string.Equals(f, otro.Name, StringComparison.OrdinalIgnoreCase)))
            return $"{otro.Name} ya está en tu lista de amigos";   // el original no le pone punto final
        return null;
    }

    /// <summary>AddAmigos (VB6 HandleAddAmigo, Protocol.bas:16851): nombre(ASCIIString) y un byte de CASO.
    ///
    /// <para>La amistad del original es de a DOS: el caso 1 manda una solicitud y el caso 2 la confirma, y recién
    /// ahí cada uno entra en la lista del otro. Acá el byte de caso se leía y se tiraba, y el que pedía quedaba
    /// agregado sin que el otro se enterara — o sea que el <c>/FACCEPT</c> que el cliente ya sabe mandar no
    /// hacía nada distinto que el <c>/FADD</c>.</para>
    ///
    /// <para>La confirmación exige que el <see cref="Player.QuienAmigo"/> guardado coincida con quien confirmo;
    /// si no coincide es "Acción invalida", y si no hay ninguna solicitud pendiente el original NO contesta
    /// nada (su <c>If LenB(.QuienAmigo) >= 3</c> no tiene else).</para>
    ///
    /// <para>El aviso de éxito del original dice que el otro "está jugando en Mohurall (Argentina)" — el nombre
    /// del servidor de ese install, cableado en el fuente. Eso no se porta literal: se dice lo mismo sin el
    /// nombre de otro servidor.</para></summary>
    private void HandleAddFriend(ClientSession session, ByteQueueReader r)
    {
        string name = r.ReadAsciiString().Trim();
        byte caso = r.ReadByte();
        _loop.Enqueue(() =>
        {
            var p = session.Player;
            if (p is null || !Naming.IsValidName(name)) return;   // rechazo temprano (defensa en profundidad ante el store)
            var otro = _world.PlayerByName(name);                 // el original resuelve por NameIndex: sólo ONLINE
            string? motivo = MotivoParaNoAgregar(p, otro);
            if (motivo is not null) { session.Send(ServerMessages.ConsoleMsg(motivo, FuenteInfo)); return; }

            if (caso == 1)   // pedir
            {
                otro!.QuienAmigo = p.Name;
                session.Send(ServerMessages.ConsoleMsg(
                    $"{otro.Name} fue agregado a tu lista de amigos, espere confirmación.", FuenteInfo));
                otro.Connection?.Send(ServerMessages.ConsoleMsg(
                    $"{p.Name} te agregó a tu lista de amigos, para aceptarlo ingrese /FACCEPT {p.Name}.", FuenteInfo));
                return;
            }
            if (caso != 2) return;
            if (p.QuienAmigo.Length < 3) return;                  // sin solicitud pendiente el original calla
            if (!string.Equals(p.QuienAmigo, otro!.Name, StringComparison.OrdinalIgnoreCase))
            { session.Send(ServerMessages.ConsoleMsg("Acción invalida", FuenteInfo)); return; }

            p.Friends.Add(otro.Name);
            otro.Friends.Add(p.Name);
            p.QuienAmigo = "";
            session.Send(ServerMessages.ConsoleMsg($"{otro.Name} es ahora tu amigo.", FuenteInfo));
            otro.Connection?.Send(ServerMessages.ConsoleMsg($"{p.Name} es ahora tu amigo.", FuenteInfo));
        });
    }

    private void HandleDelFriend(ClientSession session, ByteQueueReader r)
    {
        string name = r.ReadAsciiString().Trim();
        _loop.Enqueue(() =>
        {
            var p = session.Player;
            if (p is null) return;
            // VB6 HandleDelAmigo: si el personaje no existe -> Exit Sub silencioso; tampoco confirma por texto.
            if (!_db.CharacterExists(name)) return;
            p.Friends.RemoveAll(f => string.Equals(f, name, StringComparison.OrdinalIgnoreCase));
        });
    }

    /// <summary>OnAmigos (VB6 HandleOnAmigo, Protocol.bas:16809): la lista de amigos.
    ///
    /// <para>Acá se listaban SÓLO los conectados y con nuestra redacción. El original lista a TODOS y dice de
    /// cada uno si está o no, y a los conectados les agrega EN QUÉ MAPA están — que es lo que hace útil al
    /// comando: se usa para encontrar al amigo, no para contarlo.</para>
    ///
    /// <para>Los separadores son asimétricos en el original y van tal cual: el offline cierra con <c>","</c> y
    /// el online con <c>", "</c>; el último de la lista cierra con punto en los dos casos.</para></summary>
    private void HandleFriendsOnline(ClientSession session)
    {
        _loop.Enqueue(() =>
        {
            var p = session.Player;
            if (p is null) return;
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < p.Friends.Count; i++)
            {
                bool ultimo = i == p.Friends.Count - 1;
                var amigo = _world.PlayerByName(p.Friends[i]);
                if (amigo is null) sb.Append(p.Friends[i]).Append("(Offline)").Append(ultimo ? "." : ",");
                else sb.Append(p.Friends[i]).Append("(Online)(").Append(_data.MapName(amigo.Pos.Map))
                       .Append(')').Append(ultimo ? "." : ", ");
            }
            session.Send(ServerMessages.ConsoleMsg(sb.Length > 0
                ? "Amigos conectados: " + sb : "Tu lista de amigos está vacía.", FuenteInfo));
        });
    }

    private void HandleFriendMessage(ClientSession session, ByteQueueReader r)
    {
        string msg = r.ReadAsciiString();
        _loop.Enqueue(() =>
        {
            var p = session.Player;
            if (p is null || string.IsNullOrWhiteSpace(msg)) return;
            int sent = 0;
            foreach (var f in p.Friends)
            {
                var friend = _world.PlayerByName(f);
                if (friend is not null) { friend.Connection?.Send(ServerMessages.ConsoleMsg($"[Amigo] {p.Name}> {msg}", FuenteAmigos)); sent++; }
            }
            session.Send(ServerMessages.ConsoleMsg(sent > 0 ? $"Mensaje enviado a {sent} amigo(s)." : "Ningún amigo en línea.", FuenteAmigos));
        });
    }

    /// <summary>Information (id 74) — no es "info del servidor": es <b>pedirle la misión al enlistador de tu
    /// bando</b> (VB6 HandleInformation, Protocol.bas:5339). Se le hace clic al NPC de facciones y te dice qué
    /// tenés que hacer para ascender, o te echa si no sos de los suyos.
    ///
    /// <para>Las tres misiones y sus topes salen tal cual del original: Armada y Caos ascienden hasta el rango
    /// <b>10</b>, la Milicia hasta el <b>7</b>; los dos ejércitos legales cazan criminales de a 5 y el Caos mata
    /// gente de a 10. Todo por la boca del NPC (<c>ChatOverHead</c>), como el resto de sus diálogos.</para>
    ///
    /// <para>Acá contestaba <c>LocaleMsg(22)</c> siempre, justificado con un "el slice no modela TargetNPC" que
    /// dejó de ser cierto cuando se portó <c>LookatTile</c>.</para></summary>
    private void HandleInformation(ClientSession session)
    {
        _loop.Enqueue(() =>
        {
            var p = session.Player;
            if (p is null) return;
            var npc = TargetNpcDe(p);
            if (npc is null) { session.Send(ServerMessages.LocaleMsg(22)); return; }
            // Igual que el original: si lo clickeado no es un enlistador —o si estás muerto— calla y sale.
            if (npc.FactionEnlist == 0 || !p.IsAlive) return;
            if (DistanciaVB6(p.Pos, npc.Pos) > 4) { session.Send(ServerMessages.LocaleMsg(8)); return; }
            void Dice(string texto) => session.Send(ServerMessages.ChatOverHead(texto, npc.CharIndex, 0));

            // (¿el jugador es de ese bando?, misión, cachetada si no es de los suyos)
            var (esDelBando, mision, rechazo) = npc.FactionEnlist switch
            {
                Player.StatusArmada  => (p.IsArmada, "¡¡Tu deber es combatir criminales, cada 5 criminales te daré una recompensa!!",
                                         "¡¡No perteneces a las tropas reales!!"),
                Player.StatusMilicia => (p.IsMilicia, "¡¡Tu deber es combatir criminales, cada 5 criminales te daré una recompensa!!",
                                         "¡¡No perteneces a la Milicia Real!!"),
                Player.StatusCaos    => (p.IsCaos, "¡¡Tu deber es eliminar a todo tipo de personas, cada 10 personas muertas te daré una recompensa!!",
                                         "¡¡No perteneces a las Fuerzas del Caos, largo de aquí!!"),
                _ => (false, "", ""),
            };
            int tope = TopeDeRango(npc.FactionEnlist);
            if (tope == 0) return;                        // enlistador de un bando que el original no contempla
            if (!esDelBando) { Dice(rechazo); return; }
            Dice(p.FactionRank >= tope ? "Ya no tengo trabajo para darte, has alcanzado el rango más alto aquí." : mision);
        });
    }

    // --- Info requests (skills / attributes / stats) ---

    /// <summary>RequestSkills (id 12): sends the player's 27 skill values (SendSkills).</summary>
    private void HandleRequestSkills(ClientSession session)
    {
        _loop.Enqueue(() =>
        {
            var p = session.Player;
            if (p is null) return;
            session.Send(ServerMessages.SendSkills(p.Skills));
        });
    }

    /// <summary>RequestAtributes (id 11): reports the 6 attributes (Fuerza/Agilidad/... incl. bonuses).</summary>
    private void HandleRequestAttributes(ClientSession session)
    {
        _loop.Enqueue(() =>
        {
            var p = session.Player;
            if (p is null) return;
            // Structured 'atributes' packet (5 bytes), matching the VB6 oracle — not a ConsoleMsg text line.
            session.Send(ServerMessages.Atributes(
                (byte)p.Strength, (byte)p.Agility, (byte)p.Attributes[3], (byte)p.Attributes[4], (byte)p.Attributes[5]));
        });
    }

    /// <summary>RequestStats (id 70): reports a summary of the player's stats.</summary>
    /// <summary>El <c>/EST</c>: la FICHA del personaje (VB6 <c>SendUserStatsTxt</c>, Modulo_UsUaRiOs.bas:897).
    ///
    /// Son DOCE líneas, no una. Acá se mandaba un solo <c>ConsoleMsg</c> resumido —"Nivel N, HP x/y, Maná
    /// a/b, Oro g, Clan c"— y encima con la fuente 0 en vez de la <c>FONTTYPE_INFO</c> del original, así que
    /// el jugador veía una línea del color equivocado donde debía ver su hoja entera: golpe con el arma
    /// puesta, defensa de cuerpo y de cabeza por separado, posición, y los cinco dados.
    ///
    /// Lo destapó el barrido del oráculo el 2026-08-08, recién después de afinarlo para que compare la CUENTA
    /// de paquetes y el FontIndex: hasta ahí, "el VB6 manda 9 ConsoleMsg y nosotros 1" daba OK porque los dos
    /// mandaban "un ConsoleMsg".</summary>
    private void HandleRequestStats(ClientSession session)
    {
        _loop.Enqueue(() =>
        {
            var p = session.Player;
            if (p is null) return;
            void Linea(string t) => session.Send(ServerMessages.ConsoleMsg(t, FuenteInfo));

            Linea($"Estadísticas de: {p.Name}");
            Linea($"Nivel: {p.Level}  EXP: {(long)p.Exp}/{p.Elu}");
            Linea($"Salud: {p.Hp}/{p.MaxHp}  Maná: {p.Mana}/{p.MaxMana}  Energía: {p.Stamina}/{p.MaxStamina}");

            // El golpe muestra ENTRE PARÉNTESIS el del arma, y sólo si hay una puesta.
            var arma = Equipment.EquippedOfCategory(p, _data, 1);
            Linea(arma is not null
                ? $"Menor Golpe/Mayor Golpe: {p.MinHit}/{p.MaxHit} ({arma.MinHit}/{arma.MaxHit})"
                : $"Menor Golpe/Mayor Golpe: {p.MinHit}/{p.MaxHit}");

            // Defensa de CUERPO: armadura + escudo si hay escudo, sólo armadura si no, y "0" si va desnudo.
            // El original imprime literalmente `0` —sin el par min/max— en esa última rama.
            var armadura = Equipment.EquippedOfCategory(p, _data, 2);
            var escudo = Equipment.EquippedOfCategory(p, _data, 4);
            if (armadura is null) Linea("(CUERPO) Mín Def/Máx Def: 0");
            else if (escudo is not null)
                Linea($"(CUERPO) Mín Def/Máx Def: {armadura.MinDef + escudo.MinDef}/{armadura.MaxDef + escudo.MaxDef}");
            else
                Linea($"(CUERPO) Mín Def/Máx Def: {armadura.MinDef}/{armadura.MaxDef}");

            var casco = Equipment.EquippedOfCategory(p, _data, 3);
            Linea(casco is not null
                ? $"(CABEZA) Mín Def/Máx Def: {casco.MinDef}/{casco.MaxDef}"
                : "(CABEZA) Mín Def/Máx Def: 0");

            // Clan y liderazgo: las dos líneas SÓLO si corresponde, como el original.
            if (!string.IsNullOrEmpty(p.GuildName))
            {
                Linea($"Clan: {p.GuildName}");
                if (string.Equals(_world.GuildByName(p.GuildName)?.LeaderName, p.Name, StringComparison.OrdinalIgnoreCase))
                    Linea("Status: Líder");
            }

            var enLinea = TimeSpan.FromMilliseconds(Environment.TickCount64 - session.ConectadoDesdeMs);
            Linea($"Logeado hace: {(int)enLinea.TotalHours}:{enLinea.Minutes}:{enLinea.Seconds}");
            Linea($"Oro: {p.Gold}  Posición: {p.Pos.X},{p.Pos.Y} en mapa {p.Pos.Map}");
            Linea($"Dados: {p.Attributes[GameLimits.AttrFuerza]}, {p.Attributes[GameLimits.AttrAgilidad]}, " +
                  $"{p.Attributes[GameLimits.AttrInteligencia]}, {p.Attributes[GameLimits.AttrCarisma]}, " +
                  $"{p.Attributes[GameLimits.AttrConstitucion]}");
        });
    }

    // --- Simple player toggles/queries (Online / Rest / safety toggles) ---

    /// <summary>Online (id 62): reports the number and names of connected players.</summary>
    private void HandleOnline(ClientSession session)
    {
        if (Frenado(session, ruidosa: false)) return;
        _loop.Enqueue(() =>
        {
            // Gate de sesión: era el ÚNICO handler de estado que no lo tenía. Un cliente SIN autenticar mandaba
            // 1 byte y recibía la lista completa de nicks conectados — fuga de datos y amplificación ~2500:1
            // (2,5 KB de salida por byte de entrada), más un string.Join O(n) por paquete en el game loop.
            if (session.Player is null) return;
            // DOS líneas, como el original (VB6 HandleOnline, Protocol.bas:4940): primero el conteo con el
            // récord, y la lista de nombres SÓLO si hay alguien. Acá iba una sola línea con otro formato y
            // con la fuente 0 en vez de FONTTYPE_INFO.
            session.Send(ServerMessages.ConsoleMsg(
                $"Número de usuarios: {_world.OnlineCount} - Record de usuarios: {_world.RecordUsuarios}", FuenteInfo));
            if (_world.OnlineCount > 0)
                session.Send(ServerMessages.ConsoleMsg(string.Join(", ", _world.Players.Select(pl => pl.Name)), FuenteInfo));
        });
    }

    /// <summary>Rest (id 65): toggles resting. While resting the player regenerates HP faster (tick).</summary>
    private void HandleRest(ClientSession session)
    {
        _loop.Enqueue(() =>
        {
            var p = session.Player;
            if (p is null || !p.IsAlive) return;
            // VB6 HandleRest (Protocol.bas:17174): RestOK solo si hay una fogata (HayOBJarea con FOGATA=63).
            // Si ya descansabas te levantas igual; si no, solo avisa que no hay fogata.
            if (NearCampfire(p))
            {
                p.Resting = !p.Resting;
                session.Send(ServerMessages.RestOK());
                // El original vosea y con tilde: `Te acomodás ... comenzás` (byte 0xE1 en Protocol.bas:17194).
                session.Send(ServerMessages.ConsoleMsg(p.Resting ? "Te acomodás junto a la fogata y comenzás a descansar." : "Te levantas.", FuenteInfo));
            }
            else if (p.Resting)
            {
                p.Resting = false;
                session.Send(ServerMessages.RestOK());
                session.Send(ServerMessages.ConsoleMsg("Te levantas.", FuenteInfo));
            }
            else session.Send(ServerMessages.ConsoleMsg("No hay ninguna fogata junto a la cual descansar.", FuenteInfo));
        });
    }

    /// <summary>ResuscitationSafeToggle (id 9): toggles the resurrection-safe flag.</summary>
    private void HandleResurrectionSafeToggle(ClientSession session)
    {
        _loop.Enqueue(() =>
        {
            var p = session.Player;
            if (p is null) return;
            p.ResurrectionSafe = !p.ResurrectionSafe;
            session.Send(ServerMessages.LocaleMsg(p.ResurrectionSafe ? (short)14 : (short)15));
        });
    }

    /// <summary>CombatModeToggle (id 8): toggles combat mode.</summary>
    private void HandleCombatModeToggle(ClientSession session)
    {
        _loop.Enqueue(() =>
        {
            var p = session.Player;
            if (p is null) return;
            p.CombatMode = !p.CombatMode;
            // LocaleMsg 12/13 ("Modo combate activado/desactivado") — client-localized, matching the VB6 oracle.
            session.Send(ServerMessages.LocaleMsg(p.CombatMode ? (short)12 : (short)13));
        });
    }

    // --- Gathering (WorkLeftClick: woodcutting/fishing/mining with the right tool + skill) ---

    /// <summary>WorkLeftClick (id 27): X, Y, Skill. For a gathering skill, if the player has the matching
    /// tool and skill, yields a random amount of the resource. Slice: no resource-tile check.</summary>
    private void HandleWorkLeftClick(ClientSession session, ByteQueueReader r)
    {
        byte tx = r.ReadByte(); byte ty = r.ReadByte();   // tile objetivo (VB6 LookatTile) — lo usa el arco
        byte skill = r.ReadByte();
        _loop.Enqueue(() =>
        {
            var p = session.Player;
            if (p is null || !p.IsAlive) return;
            // GATE DE ENTRADA del original (Protocol.bas:2643):
            //     If .flags.Muerto = 1 Or .flags.Descansar Or .flags.Meditando Or Not InMapBounds(...) Then Exit Sub
            // Acá sólo estaba el chequeo de muerto. Sin `Descansar` y `Meditando` quedaba la TERCERA pata del
            // exploit de la meditación: se podía meditar —cobrando el bono de maná del tick— y a la vez
            // disparar con el arco, robar o recolectar, porque este handler no cortaba ninguno de los dos
            // estados ni los miraba.
            if (p.Meditating || p.Resting) return;
            if (tx < 1 || tx > 100 || ty < 1 || ty > 100) return;   // InMapBounds
            // Fuera del rango de visión el original rechaza con snap-back y NO ejecuta la acción (:2646).
            if (Math.Abs(tx - p.Pos.X) > RangoVisionX || Math.Abs(ty - p.Pos.Y) > RangoVisionY)
            { session.Send(ServerMessages.PosUpdate(p.Pos.X, p.Pos.Y)); return; }
            // CancelExit (:2651): trabajar también cancela la cuenta regresiva del /SALIR.
            if (p.Saliendo)
            {
                p.Saliendo = false; p.SalirSegundos = 0;
                session.Send(ServerMessages.EjecutarAccion(1));
                session.Send(ServerMessages.LocaleMsg(365, "", 1));
            }
            // Combate a distancia con arco (VB6 HandleWorkLeftClick, Case eSkill.Proyectiles, Protocol.bas:2751).
            if (skill == GameLimits.SkillProyectiles) { RangedShot(session, p, tx, ty); return; }
            // MAGIA (`Case eSkill.magia`, :2918): el segundo paquete del casteo. `CastSpell` dejó la ranura elegida
            // y pidió el blanco; este clic lo lanza. Antes no había rama: el paquete caía a los oficios y contestaba
            // un ConsoleMsg de "no hay nada que trabajar" donde el original lanza el hechizo — así lo midió el
            // oráculo, y así es como el tester veía que "no deja tirar hechizos".
            if (skill == GameLimits.SkillMagia) { LanzarHechizoElegido(session, p, tx, ty); return; }
            // ARMAS ARROJADIZAS (VB6 HandleWorkLeftClick, `Case eSkill.ArmasArrojadizas`, Protocol.bas:2655).
            // De todo el case, lo ÚNICO alcanzable con esta data es el RECHAZO: el tiro exige un arma de
            // `SubTipo = 5` y en el obj.dat no hay ninguna, así que la rama que dispara es código muerto. El
            // rechazo no lo es —basta mandar el paquete con las manos vacías— y el port se lo comía en
            // silencio: la tecla no hacía nada y no había forma de saber por qué.
            //
            // Las tres ramas del `DummyInt` del original: sin arma equipada o sin unidades → LocaleMsg 125;
            // arma equipada que no es arrojadiza → sale MUDO. El `Desequipar` que el VB6 hace junto al 125 es
            // limpieza defensiva de un estado que este stack no alcanza (con `Amount` en 0 el slot ya queda
            // vacío), el mismo detalle que se omite en el arco por el mismo motivo.
            if (skill == GameLimits.SkillArmasArrojadizas)
            {
                int slotArma = EquippedSlotOfCategory(p, 1);
                var arrojadiza = slotArma == 0 ? null : _data.Obj(p.Inventory[slotArma].ObjIndex);
                if (arrojadiza is null || p.Inventory[slotArma].Amount < 1)
                    session.Send(ServerMessages.LocaleMsg(125));
                return;
            }
            // DOMAR (VB6 HandleWorkLeftClick, `Case eSkill.domar`, Protocol.bas:3015-3028). Se doma CLICKEANDO
            // a la criatura, no con el paquete `Train` —ese es el Entrenador—. Los tres gates del call site, en
            // su orden: la criatura tiene que ser domable (si no, 300), estar a <= 2 tiles en Manhattan (si no,
            // 8 "estas demasiado lejos") y NO estar ya peleando con alguien (si lo esta, 300). Lo que sigue
            // —cupo, tipo, puntos y tirada— lo resuelve DoDomar, igual que el original.
            if (skill == GameLimits.SkillDomar)
            {
                var objetivo = _world.NpcAt(new WorldPos(p.Pos.Map, tx, ty));
                if (objetivo is null || !objetivo.IsAlive) return;                       // sin criatura: silencio
                if (objetivo.Template.Domable <= 0) { session.Send(ServerMessages.LocaleMsg(300)); return; }
                if (Math.Abs(p.Pos.X - tx) + Math.Abs(p.Pos.Y - ty) > 2) { session.Send(ServerMessages.LocaleMsg(8)); return; }
                if (objetivo.AttackedBy is not null) { session.Send(ServerMessages.LocaleMsg(300)); return; }
                DoDomar(session, p, objetivo);
                return;
            }
            // Robar (VB6 HandleWorkLeftClick, Case eSkill.robar, Protocol.bas:2953): hurtar oro a otro jugador.
            // Gates del call site: mapa PK, víctima viva y distinta, a ≤2 tiles (Manhattan), ni la víctima ni el
            // ladrón parados en ZONASEGURA. Después DoRobar hace el resto (renegado, energía, tirada).
            if (skill == GameLimits.SkillRobar)
            {
                if (!_data.IsPkMap(p.Pos.Map)) { session.Send(ServerMessages.LocaleMsg(246, "", 1)); return; }   // VB6 WriteLocaleMsg(246, "", 1)
                var vpos = new WorldPos(p.Pos.Map, tx, ty);
                bool vis = Math.Abs(tx - p.Pos.X) <= RangoVisionX && Math.Abs(ty - p.Pos.Y) <= RangoVisionY;
                var victima = vis ? _world.PlayerAt(vpos) : null;
                // El VB6 envuelve todo el robo en `If Privilegios And PlayerType.User`: a un GM/admin no se le roba (silencio).
                if (victima is null || victima.CharIndex == p.CharIndex || !victima.IsAlive || victima.IsGm) return;
                if (Math.Abs(p.Pos.X - tx) + Math.Abs(p.Pos.Y - ty) > 2) { session.Send(ServerMessages.LocaleMsg(8)); return; }   // "Estas demasiado lejos"
                if (_data.IsSafeZone(victima.Pos.Map, victima.Pos.X, victima.Pos.Y)
                    || _data.IsSafeZone(p.Pos.Map, p.Pos.X, p.Pos.Y)) { session.Send(ServerMessages.LocaleMsg(310)); return; }   // zona segura
                // Cadencia de trabajo (VB6 IntervaloPermiteTrabajar): no se puede robar en ráfaga. Reusa la
                // cadencia de golpe (misma clase de anti-cheat), desactivada en fixtureMode como las demás.
                long nowWorkMs = Environment.TickCount64;
                long workMs = _admin?.AttackIntervalMs ?? _attackIntervalMs;
                if (workMs > 0 && nowWorkMs - p.LastWorkMs < workMs) return;
                p.LastWorkMs = nowWorkMs;
                DoRobar(session, p, victima);
                return;
            }
            // Fundición (VB6 FundirMetal=88 → FundirMineral/DoLingotes, Trabajo.bas:211/825): fundir mineral crudo
            // en lingotes. HandleWork ya pidió el objetivo con WorkRequestTarget(88); el WorkLeftClick lo ejecuta.
            if (skill == GameLimits.SkillFundir)
            {
                // LA FRAGUA ES OBLIGATORIA, y antes no lo era: se fundía parado en cualquier lado. El original
                // pretende que se funda EN la fragua —su rama de `Accion` la exige (Acciones.bas:116) y el
                // mensaje "Ahí no hay ninguna fragua" existe justamente para eso—, sólo que allá esa rama es
                // inalcanzable: pide el mineral EQUIPADO en la ranura de herramienta, y `EquiparInvItem` no
                // tiene caso para `otMinerales`. O sea que en el VB6 no hay forma de fundir, y por eso el
                // yunque queda decorativo: ningún NPC vende ni suelta lingotes.
                //
                // Acá se porta la INTENCIÓN: el mineral pide objetivo (como el original) y el clic sobre la
                // fragua funde. Es la interacción que el tester describe y la que el mundo tiene armada.
                var posFragua = new WorldPos(p.Pos.Map, tx, ty);
                var enFragua = _world.TryGetGround(posFragua, out var tileF) && tileF.ObjIndex > 0
                    ? _data.Obj(tileF.ObjIndex) : null;
                if (enFragua is null || !enFragua.IsForge)
                { session.Send(ServerMessages.ConsoleMsg("Ahí no hay ninguna fragua.", FuenteInfo)); return; }
                // Manhattan, como todas las acciones de herramienta del original (Acciones.bas:53).
                if (Math.Abs(p.Pos.X - tx) + Math.Abs(p.Pos.Y - ty) > 2)
                { session.Send(ServerMessages.ConsoleMsg("Estás demasiado lejos.", FuenteInfo)); return; }
                DoFundir(session, p);
                return;
            }
            // El original saca el mineral de `flags.TargetObj`, que le queda seteado del LeftClick previo. Acá
            // se resuelve del tile que trae ESTE paquete, para no depender de que el cliente haya mandado el
            // clic de selección antes: el resultado es el mismo y no hay estado de por medio.
            if (skill == GameLimits.SkillMineria
                && _world.TryGetGround(new WorldPos(p.Pos.Map, tx, ty), out var tile) && tile.ObjIndex > 0)
            { p.TargetObj = tile.ObjIndex; p.TargetObjMap = p.Pos.Map; p.TargetObjX = tx; p.TargetObjY = ty; }
            DoRecolectar(session, p, skill, avisarSinHerramienta: true);
        });
    }

    /// <summary>Intervalo del trabajo continuo: <c>IntervaloTrabajo = 700</c> del Server.ini, que el original
    /// consulta en <c>IntervaloPermiteTrabajar</c> (modNuevoTimer.bas:249) antes de cada vuelta.</summary>
    public const long IntervaloTrabajoMs = 700;

    /// <summary>Arranca una faena (VB6 <c>Accion</c>, Acciones.bas:32-110). El oficio lo elige la HERRAMIENTA
    /// EQUIPADA, no la skill que diga el cliente: caña o red → pescar, piquete → minar, hacha → talar. Cada uno
    /// valida su terreno y recién ahí prende el flag; a partir de ese momento el game-loop hace el trabajo solo.
    ///
    /// Devuelve true si tomó el clic (arrancó la faena o avisó por qué no), false si no había nada que hacer con
    /// la herramienta puesta — ahí el clic sigue su camino normal (puertas, yunque…).</summary>
    private bool IniciarFaena(ClientSession session, Player p, byte x, byte y)
    {
        var tool = Equipment.EquippedOfCategory(p, _data, 6);
        if (tool is null) return false;                      // sin herramienta puesta no hay faena que empezar
        var pos = new WorldPos(p.Pos.Map, x, y);
        bool hayObjeto = _world.TryGetGround(pos, out var enTile) && enTile.ObjIndex > 0;
        var info = hayObjeto ? _data.Obj(enTile.ObjIndex) : null;
        int lejos = Math.Abs(p.Pos.X - x) + Math.Abs(p.Pos.Y - y);   // el original usa distancia MANHATTAN, no Chebyshev

        switch (tool.Index)
        {
            case 138 or 881:   // RED_PESCA / CAÑA_PESCA
                if (!_data.IsWater(p.Pos.Map, x, y))
                { session.Send(ServerMessages.ConsoleMsg("No hay agua donde pescar. Busca un lago, rio o mar.", FuenteInfo)); return true; }
                break;
            case 187:          // PIQUETE_MINERO
                if (info is null || !info.IsDeposit)
                { session.Send(ServerMessages.ConsoleMsg("Ahí no hay ningún yacimiento.", FuenteInfo)); return true; }
                if (lejos > 2) { session.Send(ServerMessages.ConsoleMsg("Estás demasiado lejos.", FuenteInfo)); return true; }
                // El yacimiento queda SELECCIONADO, que es de donde el original saca el mineral en cada vuelta
                // (`ObjData(.flags.TargetObj).MineralIndex`, Trabajo.bas:2001). Sin esto la faena continua no
                // tiene forma de saber sobre qué está picando.
                p.TargetObj = (short)enTile.ObjIndex;
                p.TargetObjMap = pos.Map; p.TargetObjX = pos.X; p.TargetObjY = pos.Y;
                break;
            case 127 or 885:   // HACHA_LEÑADOR / TIJERAS — las dos trabajan sobre un ÁRBOL (leña y raíces)
                if (info is null || !info.IsTree) return false;      // el original no avisa nada si no hay árbol
                if (lejos > 2) { session.Send(ServerMessages.ConsoleMsg("Estas demasiado lejos.", FuenteInfo)); return true; }
                // Sólo en mapa Pk, con el mensaje de cada oficio: `If MapInfo(.Pos.Map).Pk = False Then ...`
                if (!_data.IsPkMap(p.Pos.Map))
                {
                    session.Send(ServerMessages.ConsoleMsg(tool.Index == 885
                        ? "No puedes juntar raices en zona segura." : "No puedes talar en zona segura.", FuenteInfo));
                    return true;
                }
                break;
            default:
                return false;   // martillo/serrucho/costurero/olla no abren faena: son de fabricar
        }
        p.Laborando = true;
        p.LastWorkMs = 0;   // que la primera vuelta salga ya, sin esperar el intervalo
        session.Send(ServerMessages.ConsoleMsg("Comienzas a trabajar...", 2));
        return true;
    }

    /// <summary>Una vuelta del trabajo continuo (VB6 <c>DoTrabajar</c>, Trabajo.bas:2472), llamada desde el
    /// game-loop mientras <see cref="Player.Laborando"/> esté prendido. Gatea por el intervalo de 700 ms, corta
    /// por falta de energía con su mensaje propio, y rutea por la herramienta que el jugador tenga puesta EN ESE
    /// MOMENTO — releerla cada vuelta es del original, así que sacarse la herramienta corta la producción sola.</summary>
    public void TickTrabajo(ClientSession? session, Player p, long nowMs)
    {
        if (session is null || !p.Laborando) return;
        if (nowMs - p.LastWorkMs < IntervaloTrabajoMs) return;
        p.LastWorkMs = nowMs;
        if (p.Stamina < 2)
        {
            session.Send(ServerMessages.ConsoleMsg("Dejas de trabajar debido a tu poca energía.", FuenteInfo));
            p.Laborando = false;
            return;
        }
        var herramienta = Equipment.EquippedOfCategory(p, _data, 6);
        int skill = herramienta is null ? 0 : SkillDeLaHerramienta(herramienta.Index);
        // Sin herramienta que matchee, el `Select Case` del original no entra a ningún Case y la vuelta pasa en
        // blanco — el flag queda prendido. Acá se corta, que es lo mismo desde afuera pero no deja al loop
        // trabajando de gusto sobre un jugador que ya no puede producir.
        if (skill == 0) { p.Laborando = false; return; }
        if (!DoRecolectar(session, p, skill, avisarSinHerramienta: false)) p.Laborando = false;
    }

    /// <summary>Los ids de <c>LocaleMsg</c> del acierto y del fallo, por oficio. El original NO usa texto
    /// literal acá: manda el mensaje catalogado (Trabajo.bas 1364/1370 pesca, 1888/1895 talar, 2014/2021
    /// minería). Botánica es la excepción y sí manda <c>WriteConsoleMsg</c> con el texto armado
    /// (Trabajo.bas:1940), así que va con 0 y se resuelve con <c>ConsoleMsg</c>.</summary>
    private static (short Ok, short Fallo) AvisoDelOficio(int skill) => skill switch
    {
        GameLimits.SkillPesca => (64, 59),
        GameLimits.SkillTalar => (65, 61),
        GameLimits.SkillMineria => (66, 62),
        _ => (0, 0),
    };

    private static (int Tool, int Resource, string Name) OficioDe(int skill) => skill switch
    {
        GameLimits.SkillTalar => (127, GameLimits.WoodObj, "Leña"),          // Hacha de Leñador → Leña
        // Pesca: el recurso es sólo el RESPALDO. El pez lo decide la HERRAMIENTA (ver DoRecolectar), porque la
        // caña y la red no sacan lo mismo.
        GameLimits.SkillPesca => (881, PezDeCana, "Pargo"),
        // Minería: el 192 es sólo el RESPALDO. El mineral de verdad sale del yacimiento que se está picando
        // (ver DoRecolectar); acá quedaba cableado y por eso los yacimientos de Oro y de Plata daban hierro.
        GameLimits.SkillMineria => (187, GameLimits.OreIronObj, "Hierro"),
        GameLimits.SkillBotanica => (885, 888, "Raíz"),                       // Tijeras → Raíz (el material de la alquimia)
        _ => (0, 0, ""),
    };

    // Las cuatro clases con oficio propio (secciones de NACIMIENTO.dat; la lista entera está en Leveling.cs).
    private const int ClaseDruida = 7, ClasePescador = 11, ClaseLenador = 13, ClaseMinero = 14;

    /// <summary>Los dos aparejos de pesca y lo que saca cada uno. La CAÑA da siempre Pargo (VB6
    /// <c>MiObj.ObjIndex = Pescado</c>, y <c>Pescado = 139</c> en Declares.bas:451); la RED saca uno al azar de
    /// los cuatro del enum <c>PECES_POSIBLES</c> (Trabajo.bas:1429-1432). Acá estaba cableado el 545 para las
    /// dos, así que tres de los cuatro peces del juego no salían nunca y la caña daba el pez de la red.</summary>
    private const int RedDePesca = 138, PezDeCana = 139;
    private static readonly int[] PecesDeRed = { 139, 544, 545, 546 };   // Pargo, Lisa, Pez Espada, Merluza

    /// <summary>El oficio lo declara la HERRAMIENTA equipada, no la skill que diga el cliente. Las dos de pesca
    /// dan el mismo oficio: con la red puesta este mapeo faltaba y <see cref="DoRecolectar"/> la rechazaba por
    /// "herramienta inadecuada", o sea que la red no pescaba nada ni con el clic ni con la faena continua.</summary>
    private static int SkillDeLaHerramienta(int tool) => tool switch
    {
        127 => GameLimits.SkillTalar,                    // Hacha de Leñador
        RedDePesca or 881 => GameLimits.SkillPesca,      // Red de Pesca / Caña de Pesca
        187 => GameLimits.SkillMineria,                  // Piquete de Minero
        885 => GameLimits.SkillBotanica,                 // Tijeras
        _ => 0,
    };

    /// <summary>El esfuerzo de UNA vuelta, por oficio y clase (VB6 <c>Esfuerzo*</c>, Declares.bas:311-321). La
    /// clase del oficio paga entre la mitad y un tercio que el resto, y ese descuento es el beneficio concreto
    /// de ser Pescador, Leñador, Minero o Druida: con un costo plano las cuatro no se distinguían de nadie.</summary>
    private static int EsfuerzoDelOficio(int skill, int clase) => skill switch
    {
        GameLimits.SkillPesca => clase == ClasePescador ? 1 : 3,
        GameLimits.SkillTalar => clase == ClaseLenador ? 2 : 4,
        GameLimits.SkillMineria => clase == ClaseMinero ? 2 : 5,
        GameLimits.SkillBotanica => clase == ClaseDruida ? 2 : 4,
        _ => 0,
    };

    /// <summary>La tirada del acierto, idéntica en los cuatro oficios (VB6 Trabajo.bas 1337/1860/1925/1992):
    /// <code>Suerte = Int(-0.00125 * Skill * Skill - 0.3 * Skill + 49)
    /// res    = RandomNumber(1, Suerte)</code>
    /// y acierta si <c>res</c> no pasa el umbral. La curva NO es lineal y esa es toda la gracia: con la skill en
    /// 0 acierta 6/49 (12%), en 50 son 6/30 (20%) y recién en 100 llega a 6/6 (100%) — el oficio rinde de verdad
    /// al final. Antes acá había un <c>skill%</c> pelado, que daba 50% donde el original da 20%.
    /// El <c>Int</c> de VB6 redondea hacia abajo, que es <see cref="Math.Floor(double)"/>.</summary>
    private static int SuerteDelOficio(int skill) => (int)Math.Floor(-0.00125 * skill * skill - 0.3 * skill + 49);

    /// <summary>UNA unidad de recolección (VB6 <c>DoPescar</c>/<c>DoTalar</c>/<c>DoMineria</c>). Lo llaman los dos
    /// caminos: el click puntual y —desde que existe el trabajo continuo— cada vuelta del loop.
    ///
    /// La herramienta se exige <b>EQUIPADA</b>, no en la mochila: el original la lee de
    /// <c>Invent.AnilloEqpObjIndex</c>. Devuelve false cuando el trabajo no puede seguir (sin herramienta, sin
    /// skill, sin energía), que es lo que el loop usa para cortar la faena.</summary>
    private bool DoRecolectar(ClientSession session, Player p, int skill, bool avisarSinHerramienta)
    {
        var (tool, resource, name) = OficioDe(skill);
        if (tool == 0) { if (avisarSinHerramienta) session.Send(ServerMessages.ConsoleMsg("No sabes hacer ese trabajo.", FuenteInfo)); return false; }
        if (p.Skills[skill] <= 0) { if (avisarSinHerramienta) session.Send(ServerMessages.ConsoleMsg("No tienes esa habilidad.", FuenteInfo)); return false; }
        // La herramienta se acepta por el OFICIO que declara, no por un índice único: la pesca tiene dos
        // aparejos y exigir sólo la caña dejaba la red sin hacer nada.
        var puesta = Equipment.EquippedOfCategory(p, _data, 6);
        if (puesta is null || SkillDeLaHerramienta(puesta.Index) != skill)
        { if (avisarSinHerramienta) session.Send(ServerMessages.ConsoleMsg("Necesitas la herramienta adecuada.", FuenteInfo)); return false; }
        // VB6 DoPescar/DoTalar/DoMineria: el trabajo CUESTA stamina (QuitarSta — sin energía no se trabaja) y
        // el éxito es una tirada según skill (res = RandomNumber(1,Suerte), sólo rinde a veces). Sin esto el
        // trabajo daba recurso garantizado sin costo → canilla de oro (los recursos se venden a NPCs).
        int esfuerzo = EsfuerzoDelOficio(skill, p.Class);
        if (p.Stamina < esfuerzo) { session.Send(ServerMessages.ConsoleMsg("Estas demasiado cansado para trabajar.", FuenteInfo)); return false; }
        p.Stamina -= esfuerzo;
        // El umbral del acierto: 6 en tala, botánica y pesca CON CAÑA; 5 en minería (Trabajo.bas:1996) y en
        // pesca CON RED, porque el original escribe `res < 6` ahí (:1425) y `res <= 6` en la caña (:1341).
        int suerte = SuerteDelOficio(p.Skills[skill]);
        int umbral = skill == GameLimits.SkillMineria || puesta.Index == RedDePesca ? 5 : 6;
        if (suerte <= 0 || _rng.Next(1, suerte + 1) > umbral)   // RandomNumber(1, Suerte); fallar igual gasta stamina
        {
            session.Send(ServerMessages.UpdateSta((short)p.Stamina));   // la tirada fallida igual gasta stamina
            var fallo = AvisoDelOficio(skill).Fallo;
            session.Send(fallo > 0 ? ServerMessages.LocaleMsg(fallo)
                                   : ServerMessages.ConsoleMsg("No has conseguido nada esta vez.", FuenteInfo));
            return true;   // fallar la tirada NO corta la faena: en el original se sigue intentando
        }
        // MINERÍA: el mineral lo declara el YACIMIENTO, no el oficio (VB6 `MiObj.ObjIndex =
        // ObjData(.flags.TargetObj).MineralIndex`, Trabajo.bas:2001). De los 12 yacimientos del install, 4 son
        // de Hierro, 4 de Oro y 4 de Plata; con el mineral cableado, los 8 de Oro y Plata daban hierro y toda
        // esa rama de la fundición era inalcanzable picando. Si el target se perdió, se cae al hierro, que es
        // lo que hacía antes.
        if (skill == GameLimits.SkillMineria)
        {
            var yacimiento = p.TargetObj > 0 ? _data.Obj(p.TargetObj) : null;
            if (yacimiento is { IsDeposit: true, MineralIndex: > 0 })
            { resource = yacimiento.MineralIndex; name = _data.Obj(resource)?.Name ?? name; }
        }
        // PESCA: el pez sale de la HERRAMIENTA, no del oficio (VB6 DoPescar vs DoPescarRed).
        if (skill == GameLimits.SkillPesca)
        {
            resource = puesta.Index == RedDePesca ? PecesDeRed[_rng.Next(PecesDeRed.Length)] : PezDeCana;
            name = _data.Obj(resource)?.Name ?? name;
        }
        // La cantidad del original: `MiObj.Amount = RandomNumber(10, 30)` en pesca, tala y minería. El bono por
        // clase que el VB6 calcula al lado (`CantidadItems`) es CÓDIGO MUERTO —las dos ramas del `If Clase`
        // asignan el mismo RandomNumber(10,30)—, así que no se porta. Botánica sí reparte por clase: el Druida
        // saca 1..6 y el resto 1..2 (Trabajo.bas:1934-1936). Acá había un 1..5 plano para los cuatro oficios,
        // o sea entre un cuarto y un sexto de lo que rinde el original.
        int amount = skill == GameLimits.SkillBotanica
            ? (p.Class == ClaseDruida ? _rng.Next(1, 7) : _rng.Next(1, 3))
            : _rng.Next(10, 31);
        int slot = FindSlotFor(p, (short)resource, amount);
        if (slot == 0)
        {
            // Mochila llena: el original NO corta el trabajo ni avisa, tira el montón al piso y sigue
            // (`If Not MeterItemEnInventario(...) Then Call TirarItemAlPiso(...)`). Cortar acá hacía que una
            // faena continua se apagara sola en cuanto se llenaba la mochila.
            if (FreeGroundNear(_world, p.Pos) is { } spot)
            {
                _world.SetGround(spot, (short)resource, (short)amount);
                _world.BroadcastToObservers(spot, ServerMessages.ObjectCreate(spot.X, spot.Y, (short)resource, (short)amount));
            }
        }
        else
        {
            var inv = p.Inventory[slot];
            inv.ObjIndex = (short)resource;
            inv.Amount = (short)Math.Min(GameLimits.MaxInventoryObjs, inv.Amount + amount);
            p.Inventory[slot] = inv;
            session.Send(ServerMessages.ChangeInventorySlot((byte)slot, inv.ObjIndex, inv.Amount, inv.Equipped, 0f, 1));
        }
        session.Send(ServerMessages.UpdateSta((short)p.Stamina));   // reflejar la stamina gastada
        var acierto = AvisoDelOficio(skill).Ok;
        session.Send(acierto > 0 ? ServerMessages.LocaleMsg(acierto)
                                 : ServerMessages.ConsoleMsg($"¡Has obtenido {name.ToLowerInvariant()}!({amount})", FuenteInfo));
        // El oficio mejora con el uso, igual que pegar o castear (VB6 DoPescar/DoTalar/DoMineria:
        // Trabajo.bas:1365/1890/2016 llaman SubirSkill con SU skill al conseguir el recurso).
        RaiseSkill(session, p, skill);
        p.Trabajando++;   // crédito del centinela (VB6 Trabajo.bas:1378/1902/2029), lo lista /TRABAJANDO
        return true;
    }

    private static int CountItem(Player p, int objIndex)
    {
        int total = 0;
        for (int i = 1; i <= GameLimits.MaxInventorySlots; i++)
            if (!p.Inventory[i].IsEmpty && p.Inventory[i].ObjIndex == objIndex) total += p.Inventory[i].Amount;
        return total;
    }

    private void ConsumeItem(ClientSession session, Player p, int objIndex, int amount)
    {
        for (int i = 1; i <= GameLimits.MaxInventorySlots && amount > 0; i++)
        {
            var inv = p.Inventory[i];
            if (inv.IsEmpty || inv.ObjIndex != objIndex) continue;
            int take = Math.Min(amount, inv.Amount);
            inv.Amount -= (short)take; amount -= take;
            if (inv.Amount <= 0) inv = default;
            p.Inventory[i] = inv;
            session.Send(ServerMessages.ChangeInventorySlot((byte)i, inv.ObjIndex, inv.Amount, inv.Equipped, 0f, (byte)(inv.IsEmpty ? 0 : 1)));
        }
    }

    /// <summary>After a character moves or teleports, send CharacterCreate/Remove diffs so each side sees
    /// the other exactly while within view range (ModAreas.CheckUpdateNeededUser). Only entities whose
    /// visibility flipped between <paramref name="from"/> and <paramref name="to"/> are touched — a step
    /// within the same 9×9 area sends nothing here; a teleport across areas refreshes the whole view.
    /// Static + World-based so both the dispatcher (walk/teleport) and NpcSystem (respawn) can call it.</summary>
    public static void RefreshAreaVisibility(World world, Player p, WorldPos from, WorldPos to)
    {
        foreach (var other in world.Players)
        {
            if (other.CharIndex == p.CharIndex) continue;
            bool was = World.InView(from, other.Pos), now = World.InView(to, other.Pos);
            if (now && !was)
            {
                p.Connection?.Send(ServerMessages.CharacterCreate(other));      // p now sees other
                // El CharacterCreate dibuja al char visible; si está oculto/invisible hay que re-aplicar el
                // SetInvisible o el observador que recién lo entra en vista lo vería (VB6 MakeUserChar + flag).
                if (other.Invisible || other.Oculto) p.Connection?.Send(ServerMessages.SetInvisible(other.CharIndex, true));
                other.Connection?.Send(ServerMessages.CharacterCreate(p));      // other now sees p
                if (p.Invisible || p.Oculto) other.Connection?.Send(ServerMessages.SetInvisible(p.CharIndex, true));
            }
            else if (was && !now)
            {
                p.Connection?.Send(ServerMessages.CharacterRemove(other.CharIndex)); // p loses sight of other
                other.Connection?.Send(ServerMessages.CharacterRemove(p.CharIndex));
            }
        }
        // SÓLO los NPCs del mapa donde está pasando algo. `InView` ya exige mismo mapa, así que los de los
        // otros 861 mapas daban was=false y now=false y no hacían nada — pero se recorrían igual, y esto corre
        // en CADA PASO de CADA jugador: 5404 comparaciones por paso contra las ≤60 de un mapa poblado.
        //
        // Ojo con el caso TELEPORT, que cruza de mapa: ahí `from` y `to` están en mapas distintos y hay que
        // recorrer los dos —el de origen para mandar los CharacterRemove y el de destino para los Create—, o el
        // que se teletransporta se lleva puestos a los NPCs del mapa que dejó, dibujados encima del nuevo.
        foreach (var npc in world.NpcsOnMap(to.Map))
        {
            bool was = World.InView(from, npc.Pos), now = World.InView(to, npc.Pos);
            if (now && !was) p.Connection?.Send(ServerMessages.CharacterCreate(npc));
            else if (was && !now) p.Connection?.Send(ServerMessages.CharacterRemove(npc.CharIndex));
        }
        if (from.Map != to.Map)
            foreach (var npc in world.NpcsOnMap(from.Map))
                if (World.InView(from, npc.Pos)) p.Connection?.Send(ServerMessages.CharacterRemove(npc.CharIndex));
    }

    /// <summary>Melee damage via the ported CalcularDano formula (SistemaCombate.bas:300-433). El original
    /// parte en dos por la condición <c>WeaponEqp &gt; 0 And NudiEqpSlot = 0</c>:
    ///
    /// <list type="bullet">
    /// <item>Con ARMA: su [MinHIT,MaxHIT] y el modificador de clase <c>DañoArmas</c>.</item>
    /// <item>Sin arma, o con NUDILLOS puestos: siempre <c>DañoWrestling</c>. Los nudillos aportan su propia
    /// tirada de daño y su MaxHIT, y encima de eso van el <c>+RandomNumber(1,3)</c> y el <c>+3</c> del máximo
    /// que el original le suma al puño ("hacemos que sea 'tipo' una daga"). A puño limpio esa suma ES todo el
    /// daño; con nudillos se acumula.</item>
    /// </list>
    ///
    /// Los dos últimos sumandos van DESPUÉS del modificador de clase, sobre el número ya redondeado: la
    /// montura (:421-427, cualquier objetivo) y el ítem mágico de <c>AumentaGolpe</c> (:429-433, <b>sólo contra
    /// NPC</b> — el Pendiente del Experto no pega más fuerte en PvP). Los especiales de arma (matadragones,
    /// arco-demonio) siguen sin modelarse.</summary>
    private int MeleeDamage(Player p, bool contraNpc)
    {
        long userHit = p.MaxHit >= p.MinHit ? _rng.Next(p.MinHit, p.MaxHit + 1) : p.MinHit;
        var nudillos = Equipment.EquippedOfCategory(p, _data, 7);
        var weapon = nudillos is null ? Equipment.EquippedOfCategory(p, _data, 1) : null;   // 1 = weapon
        long weaponRoll, weaponMax;
        double classMod;
        if (weapon is not null)
        {
            weaponRoll = weapon.MaxHit >= weapon.MinHit ? _rng.Next(weapon.MinHit, weapon.MaxHit + 1) : weapon.MinHit;
            weaponMax = weapon.MaxHit;
            classMod = _data.WeaponMod.TryGetValue(p.Class, out var wm) ? wm : 1.0;      // ModClase.DañoArmas
        }
        else
        {
            weaponRoll = nudillos is null ? 0
                       : nudillos.MaxHit >= nudillos.MinHit ? _rng.Next(nudillos.MinHit, nudillos.MaxHit + 1)
                       : nudillos.MinHit;
            weaponMax = nudillos?.MaxHit ?? 0;
            weaponRoll += _rng.Next(1, 4);                                                // RandomNumber(1,3)
            weaponMax += 3;
            classMod = _data.WrestlingMod.TryGetValue(p.Class, out var m) ? m : 0.4;     // ModClase.DañoWrestling
        }
        long dano = Combat.CalcularDano(weaponRoll, weaponMax, userHit, p.Strength, classMod);
        // LA MONTURA PEGA. El original le suma la tirada del animal a CUALQUIER golpe dado a caballo
        // (SistemaCombate.bas:421-427) y acá no se sumaba: montado se pegaba igual que a pie, aunque las 11
        // monturas de la data traen su [MinHIT,MaxHIT].
        if (p.Mounted && Equipment.MonturaEquipada(p, _data) is { } montura && montura.MaxHit >= montura.MinHit)
            dano += _rng.Next(montura.MinHit, montura.MaxHit + 1);
        if (contraNpc && Equipment.EquippedOfCategory(p, _data, 8) is { EfectoMagico: AumentaGolpe } pendiente)
            dano += pendiente.CuantoAumento;
        return Math.Max(1, (int)dano);
    }

    /// <summary>VB6 <c>eMagicType.AumentaGolpe</c> (Declares.bas): el ítem mágico que suma <c>CuantoAumento</c>
    /// al daño contra criaturas. En esta data lo trae uno solo, el Pendiente del Experto (+20).</summary>
    private const int AumentaGolpe = 18;

    /// <summary>VB6 <c>eMagicType.Silencio</c> (Declares.bas): el ítem mágico que hace CALLAR las palabras
    /// mágicas del portador. En esta data lo trae uno solo, el Amuleto del Silencio (obj 755).</summary>
    private const int Silencio = 16;

    /// <summary>Death handling shared by melee and spells: message, XP award + level-up, respawn.</summary>
    private void OnKill(ClientSession attackerSession, Player attacker, Player target)
    {
        attackerSession.Send(ServerMessages.ConsoleMsg($"Has matado a {target.Name}!", 0));
        target.Connection?.Send(ServerMessages.ConsoleMsg("Has muerto!", 0));
        attacker.UsersKilled++;
        // Contadores de facción por bando de la víctima (VB6 ContarMuerte, Modulo_UsUaRiOs.bas:1473). ANTES de contar
        // hay gates anti-farm que hacen Exit Sub: (1) nivel del atacante > víctima+10 → LocaleMsg(330) y no cuenta;
        // (2) víctima newbie (ELV<=15); (3) víctima desnuda (sin armadura); (4) los dos en la ARENA (:1493). La
        // víctima "legal" (ciudadano/Armada) suma a KillsLegales; la "criminal/caos" a KillsCriminales; el Caos
        // además a KillsCaos (para que el bando del Caos cuente todos menos Caos).
        //
        // El gate (4) estaba anotado acá como "no aplica: el slice no modela zonas de pelea" — cierto cuando se
        // escribió y falso desde que se portó ZONAPELEA. Sin él la arena es una fábrica de puntos de facción: dos
        // amigos se matan por turnos donde morir no cuesta nada y suben el rango del ejército gratis.
        bool farmeoDeNivel = attacker.Level > target.Level + 10;
        if (farmeoDeNivel) attackerSession.Send(ServerMessages.LocaleMsg(330));
        bool victimaDesnuda = Equipment.EquippedOfCategory(target, _data, 2) is null;
        if (!farmeoDeNivel && target.Level > GameLimits.NewbieMaxLevel && !victimaDesnuda && !DueloDeArena(attacker, target))
        {
            // VB6 ContarMuerte:1496-1503: un contador por bando de la víctima (cada ejército suma un subconjunto).
            if (target.IsCitizen) attacker.CiudadanosMatados++;
            else if (target.IsRepublicano) attacker.RepublicanosMatados++;
            else if (target.IsArmada) attacker.ArmadaMatados++;
            else if (target.IsMilicia) attacker.MilicianosMatados++;
            else if (target.IsCaos) attacker.CaosMatados++;
            else attacker.RenegadosMatados++;   // criminal = renegado (esRene)
        }
        // VB6 ActStats (Modulo_UsUaRiOs.bas:2353): la exp por frag es ELV_víctima * 2 (no *100), y si el atacante
        // supera a la víctima por más de 10 niveles NO recibe exp (mismo gate de nivel que ContarMuerte).
        long reward = farmeoDeNivel ? 0L : Math.Max(1, (int)target.Level) * 2L;
        int levels = reward > 0 ? OtorgarExp(attackerSession, attacker, reward) : 0;
        if (reward > 0)
            attackerSession.Send(ServerMessages.ConsoleMsg(levels > 0
                ? $"Ganaste {reward} de experiencia y subiste {levels} nivel(es)! Nivel {attacker.Level}."
                : $"Ganaste {reward} de experiencia.", 0));
        KillPlayer(_world, _data, target);   // VB6: el muerto queda como cadáver donde cayó (no respawn instantáneo)
    }

    /// <summary>El hechizo del portal está cableado por NÚMERO en el original (VB6 <c>HechizoCreateTelep</c>
    /// abre con <c>If HechizoIndex &lt;&gt; 53 Then Exit Sub</c>, modHechizos.bas:1948): no hay campo en
    /// Hechizos.dat que lo distinga. En la data es el 53, "Portal planar".</summary>
    private const short HechizoPortalLum = 53;
    /// <summary>El objeto que dibuja el portal en el piso (VB6 <c>ET.ObjIndex = 672</c>).</summary>
    private const short ObjPortalLum = 672;
    /// <summary>La partícula que marca el tile del portal, al abrirlo y al cerrarlo (VB6: 52).</summary>
    private const short ParticulaPortalLum = 52;
    /// <summary>El ciclo, en segundos (VB6 <c>Select Case .Counters.TimeTeleport</c>): a los 5 se abre, a los
    /// 15 se levanta. O sea que tarda 5 en aparecer y dura 10.</summary>
    private const int PortalSegundosParaAbrir = 5, PortalSegundosDeVida = 15;

    /// <summary>
    /// Tira el portal de Lum (VB6 <c>HechizoCreateTelep</c>, modHechizos.bas:1938). NO abre nada todavía:
    /// anota el tile y arranca el reloj. Los gates, en el orden del original:
    /// <list type="number">
    /// <item>prisión, Intermundia o mapa NO-Pk → LocaleMsg 448</item>
    /// <item>el tile no es legal → sale MUDO</item>
    /// <item>el tile tiene un objeto, tiene una salida o está bloqueado → LocaleMsg 257, el MISMO id para los tres</item>
    /// <item>ya hay un portal en curso → LocaleMsg 457 con los segundos que faltan</item>
    /// </list>
    /// El 457 es el cooldown y sale de <c>15 - TimeTeleport</c>: mientras el portal viva no se puede tirar otro.
    /// </summary>
    private void CrearPortalDeLum(ClientSession session, Player p, Data.SpellInfo spell)
    {
        if (p.Pos.Map == Cities.Prision.Map || p.Pos.Map == Cities.Intermundia.Map || !_data.IsPkMap(p.Pos.Map))
        { session.Send(ServerMessages.LocaleMsg(448)); return; }
        var destino = new WorldPos(p.Pos.Map, p.TargetX, p.TargetY);
        if (!PosLegal(destino, p.Navigating)) return;                                          // VB6 LegalPos: sin aviso
        if (_world.TryGetGround(destino, out _)) { session.Send(ServerMessages.LocaleMsg(257)); return; }
        if (_data.MapExits.ContainsKey((destino.Map, destino.X, destino.Y))) { session.Send(ServerMessages.LocaleMsg(257)); return; }
        if (_data.IsBlockedTile(destino.Map, destino.X, destino.Y)) { session.Send(ServerMessages.LocaleMsg(257)); return; }
        if (p.TimeTeleport != 0)
        {
            session.Send(ServerMessages.LocaleMsg(457,
                (PortalSegundosDeVida - p.TimeTeleport).ToString(System.Globalization.CultureInfo.InvariantCulture)));
            return;
        }
        p.PortalPos = destino;
        // Los dos paquetes del tile, en el orden del original (:1997-1998) y con sus coordenadas: el sonido
        // faltaba —abrir un portal era mudo— y va PRIMERO, gateado por `WAV <> 0` (a diferencia del camino de un
        // solo objetivo, donde no hay gate).
        if (spell.Wav != 0)
            _world.BroadcastToObservers(p.Pos, ServerMessages.PlayWave((short)spell.Wav, destino.X, destino.Y));
        if (spell.Particle != 0)
            _world.BroadcastToObservers(p.Pos, ServerMessages.EfectoTerrenoParticula((short)spell.Particle, destino.X, destino.Y, -1));
        p.CasteandoPortal = true;
        p.CreoTeleport = true;
        p.TimeTeleport = 0;
        p.PortalTickCounter = 0;
        // Las palabras mágicas van ANTES del 456 (:2006 contra :2008), y el portal también tiene su propia
        // llamada: no pasa por `InfoHechizo` — `HandleHechizoTerreno` lo rutea aparte por `Tipo = uCreateTelep`.
        DecirPalabrasMagicas(p, spell);
        _world.BroadcastToObservers(p.Pos, ServerMessages.LocaleMsg(456, $"{p.Name}%^{spell.Index}"));
        SpellCasted(session, p, spell);   // VB6 `b = True`: el maná se cobra recién con el hechizo aceptado
    }

    /// <summary>
    /// El paso de UN SEGUNDO del portal (VB6 <c>PasarSegundotelep</c>, General.bas:1855), que llama el loop
    /// del mundo. A los 5 s revalida el tile y planta el objeto; a los 15 s lo levanta.
    ///
    /// La revalidación no es paranoia del port: el comentario del autor dice "chequeamos si a la hora de
    /// crear el TP no pusieron cosas". En cinco segundos alguien puede tirar un ítem justo ahí.
    /// </summary>
    public static void PasoDelPortalDeLum(World world, GameData data, Player p)
    {
        if (!p.CreoTeleport) return;
        p.TimeTeleport++;
        if (p.TimeTeleport == PortalSegundosParaAbrir)
        {
            var t = p.PortalPos;
            bool cancela = p.Pos.Map == Cities.Prision.Map || p.Pos.Map == Cities.Intermundia.Map || !data.IsPkMap(p.Pos.Map);
            if (cancela) p.Connection?.Send(ServerMessages.LocaleMsg(448));
            // `If Not LegalPos(Mapa, X, Y) Then Cancelo = True` (:1884) y `If Not MapaValido(Mapa) Or Not
            // InMapBounds(...)` (:1903), los dos SILENCIOSOS. Faltaban los dos. El que se alcanza de verdad es
            // la OCUPACIÓN: en los cinco segundos que el portal tarda en abrir alguien se para en el tile, y
            // donde el original cancela nosotros lo abríamos igual, debajo de sus pies. El resto de LegalPos
            // —agua y los bordes del mapa— entra por el mismo `If`.
            if (t.Map <= 0 || t.X < GameLimits.MinXBorder || t.X > GameLimits.MaxXBorder
                || t.Y < GameLimits.MinYBorder || t.Y > GameLimits.MaxYBorder) cancela = true;
            if (world.PlayerAt(t) is not null || world.NpcAt(t) is not null) cancela = true;
            if (data.IsWater(t.Map, t.X, t.Y)) cancela = true;
            if (world.TryGetGround(t, out _)) { p.Connection?.Send(ServerMessages.LocaleMsg(257)); cancela = true; }
            if (data.MapExits.ContainsKey((t.Map, t.X, t.Y))) { p.Connection?.Send(ServerMessages.LocaleMsg(257)); cancela = true; }
            if (data.IsBlockedTile(t.Map, t.X, t.Y)) { p.Connection?.Send(ServerMessages.LocaleMsg(257)); cancela = true; }
            if (cancela)
            {
                world.BroadcastToObservers(p.Pos, ServerMessages.EfectoTerrenoParticula(ParticulaPortalLum, t.X, t.Y, 0));
                ControlarPortalLum(world, data, p, 0);
                return;
            }
            // "Si se va del mapa lo cancela" (el comentario es del original): sólo se abre si el que lo tiró
            // sigue en el mapa del tile. Y OJO que el VB6 acá NO limpia los contadores: deja el portal en
            // curso y lo levanta el caso de los 15 s, así que el cooldown corre igual.
            if (p.Pos.Map != t.Map) return;
            data.SetMapExit(t.Map, t.X, t.Y, Cities.Intermundia.Map, Cities.Intermundia.X, Cities.Intermundia.Y);
            world.SetGround(t, ObjPortalLum, 1);
            world.BroadcastToObservers(t, ServerMessages.ObjectCreate(t.X, t.Y, ObjPortalLum, 1));
            world.BroadcastToObservers(p.Pos, ServerMessages.EfectoTerrenoParticula(ParticulaPortalLum, t.X, t.Y, 0));
            p.CasteandoPortal = false;
        }
        else if (p.TimeTeleport >= PortalSegundosDeVida)
        {
            ControlarPortalLum(world, data, p, 1);
        }
    }

    /// <summary>
    /// VB6 <c>ControlarPortalLum</c> (General.bas:1948), con sus dos ramas excluyentes:
    /// <list type="bullet">
    /// <item>el portal YA está abierto (<c>CreoTeleport</c> y no <c>CasteandoPortal</c>) → se levanta el
    /// objeto y su salida, EN SILENCIO.</item>
    /// <item>todavía se está casteando, o se fuerza con <paramref name="modo"/> 0 → se CANCELA y avisa con
    /// LocaleMsg 356.</item>
    /// </list>
    /// Si no entra en ninguna —no hay portal y no se fuerza— el original no toca nada, y acá tampoco.
    /// La llaman el reloj, la muerte y la desconexión: un portal huérfano dejaría una salida a Intermundia
    /// escrita en el mapa para siempre.
    /// </summary>
    public static void ControlarPortalLum(World world, GameData data, Player p, byte modo)
    {
        var t = p.PortalPos;
        if (p.CreoTeleport && !p.CasteandoPortal)
        {
            if (world.TryGetGround(t, out var obj) && obj.ObjIndex == ObjPortalLum)
            {
                world.RemoveGround(t);
                world.BroadcastToObservers(t, ServerMessages.ObjectDelete(t.X, t.Y));
            }
            data.RemoveMapExit(t.Map, t.X, t.Y);
        }
        else if (p.CasteandoPortal || modo == 0)
        {
            p.Connection?.Send(ServerMessages.LocaleMsg(356));
            world.BroadcastToObservers(p.Pos, ServerMessages.EfectoTerrenoParticula(ParticulaPortalLum, t.X, t.Y, 0));
            p.CasteandoPortal = false;
        }
        else return;
        p.PortalPos = default;
        p.TimeTeleport = 0;
        p.CreoTeleport = false;
        p.PortalTickCounter = 0;
    }

    /// <summary>LAS PALABRAS MÁGICAS: el nombre del conjuro flotando sobre la cabeza del que lo lanza
    /// (VB6 <c>DecirPalabrasMagicas</c>, modHechizos.bas:277 → <c>ChatOverHeadLocale(charIndex, spellIndex, 1)</c>
    /// al área DEL LANZADOR, no del objetivo).
    ///
    /// El port no las decía NUNCA para un jugador: para las criaturas sí (<c>NpcSystem.EmitirFxDeHechizo</c>),
    /// pero el camino del jugador no tenía la línea. Y el barrido del oráculo no podía verlo: su única acción de
    /// hechizo castea un slot VACÍO, que muere mucho antes de llegar acá.
    ///
    /// El <b>AMULETO DEL SILENCIO</b> (obj 755, <c>EfectoMagico = Silencio = 16</c>) es lo único que las calla, y
    /// callarlas es su ÚNICO efecto en todo el juego (:283-287). Sin esta función el amuleto no tenía qué callar:
    /// era un ítem con descripción ("Hace tus hechizos silenciosos") y cero comportamiento.</summary>
    private void DecirPalabrasMagicas(Player caster, Data.SpellInfo spell)
    {
        if (Equipment.EquippedOfCategory(caster, _data, 8) is { EfectoMagico: Silencio }) return;
        _world.BroadcastToObservers(caster.Pos, ServerMessages.ChatOverHeadLocale(caster.CharIndex, spell.Index, 1));
    }

    /// <summary>El bloque de emisión de <c>InfoHechizo</c> (VB6 modHechizos.bas:1743-1749), que es por dónde pasa
    /// TODO hechizo de un solo objetivo: palabras mágicas primero y después los tres paquetes del objetivo.
    ///
    /// Se emitía uno solo de los cuatro, y con el gate equivocado:
    /// <list type="bullet">
    /// <item>El <c>CreateFX</c> salía cuando había FX <b>o</b> partícula, así que los <b>48</b> hechizos que son
    ///   sólo partícula mandaban un <c>CreateFX</c> con fx = 0 —que no dibuja nada— y su partícula no salía
    ///   nunca. El original los separa: el grh va con <c>If FXgrh &lt;&gt; 0</c> y la partícula con
    ///   <c>If Particle &lt;&gt; 0</c>, cada una en su línea.</item>
    /// <item>El <c>PlayWave</c> faltaba entero: la magia del jugador era MUDA. Y va <b>sin gate</b> — en
    ///   <c>InfoHechizo</c> no hay <c>If WAV &lt;&gt; 0</c>, a diferencia del camino de área (:471) y del de las
    ///   criaturas, que sí lo tienen. Son 4 hechizos con WAV = 0: el original les manda igual el paquete.</item>
    /// </list>
    ///
    /// OJO: el <c>EfectoTerrenoFX</c> NO es "el FX de un hechizo con Target 4". Su única emisión vive dentro de
    /// <c>HechizoTerrenoEstado</c> (modHechizos.bas:382), que es la rama de los hechizos DE ÁREA — la que recorre
    /// el 3×3 alrededor del tile casteado. Emitirlo por <c>Target == 4</c> metería el paquete en hechizos que en
    /// el original no lo mandan (la Bomba mágica es Target 4 y de área; la Tormenta de fuego también). Se emite
    /// en la rama de área, no acá. Ver <see cref="CastAreaSpell"/>.</summary>
    private void EmitSpellFx(Player caster, WorldPos pos, short charIndex, Data.SpellInfo spell)
    {
        DecirPalabrasMagicas(caster, spell);
        if (spell.FxGrh > 0)
            _world.BroadcastToObservers(pos, ServerMessages.CreateFX(charIndex, (short)spell.FxGrh, (short)spell.FxLoops));
        _world.BroadcastToObservers(pos, ServerMessages.PlayWave((short)spell.Wav, pos.X, pos.Y));
        if (spell.Particle > 0)
            _world.BroadcastToObservers(pos, ServerMessages.EfectoCharParticula(
                charIndex, (short)spell.Particle, spell.TimeParticula, false));
    }

    /// <summary>Hechizo de ÁREA: resuelve el hechizo sobre el 3×3 que rodea al tile casteado, no sobre un solo
    /// objetivo (VB6 <c>HechizoTerrenoEstado</c>, modHechizos.bas:382). Son 7 hechizos de la data: Bomba mágica,
    /// Tormenta de fuego, Tormenta ácida y Corriente eléctrica (daño), Círculo curativo y Pregonar la palabra de
    /// Dios (cura) y Tornado.
    ///
    /// TRES COSAS DEL ORIGINAL QUE PARECEN BUGS Y SE PORTAN IGUAL, porque son lo que corre:
    ///
    /// 1. <b>El bucle es 3×3 FIJO</b>, no dimensionado por <c>AreaEfecto</c> (:407 recorre <c>-1..+1</c> pelado).
    ///    Así que "cura en un diámetro de 5 tiles" del Círculo curativo miente: cura 3×3 como todos. El campo
    ///    <c>AreaEfecto</c> sólo actúa de segundo interruptor (<c>&lt;&gt; 0</c>).
    /// 2. <b><c>HechizoCasteado = True</c> está ANTES del bucle</b> (:406), no dentro. O sea: castear un hechizo
    ///    de área al vacío igual cobra maná, stamina y sube Magia. No es la canilla que se cerró en
    ///    <see cref="SpellCasted"/> — ahí el problema era cobrar sin cobrar el maná; acá el maná SÍ se paga, y
    ///    pagarlo es justamente lo que raciona el entrenamiento.
    /// 3. <b>Con <c>SubeHP = 0</c> el área no hace NADA</b>: el <c>Select Case</c> de :423 sólo tiene Case 1 y
    ///    Case 2. El Tornado (SubeHP 0, Paraliza 1) queda en puro efecto visual pese a que su descripción dice
    ///    "paraliza en un área de dos tiles" — la parálisis nunca se aplica porque no hay Case que la mire.
    ///
    /// El FX va sobre el TERRENO y una sola vez, después del bucle (:469-471): partícula, grh y sonido con las
    /// coordenadas del tile. Y NO hay CreateFX ni los mensajes 497/498 por víctima, porque <c>InfoHechizo</c>
    /// arranca con <c>If Hechizos(spellindex).HechizoDeArea Then Exit Sub</c> (:1741) — un hechizo de área nunca
    /// pasa por el camino de "avisar quién le pegó a quién".</summary>
    private void CastAreaSpell(ClientSession session, Player p, Data.SpellInfo spell, byte cx, byte cy)
    {
        // El CENTRO del 3×3 es el tile CLICKEADO (`PosCasteadaX = .flags.TargetX`, modHechizos.bas:397-399),
        // que LookatTile acaba de dejar. Antes era el tile encarado, porque el casteo no tenía clic.

        for (int x = cx - 1; x <= cx + 1; x++)
            for (int y = cy - 1; y <= cy + 1; y++)
            {
                // InMapBounds (GameLogic.bas:225): los límites del ÁREA JUGABLE, no del mapa.
                if (x < GameLimits.MinXBorder || x > GameLimits.MaxXBorder
                    || y < GameLimits.MinYBorder || y > GameLimits.MaxYBorder) continue;
                var tile = new WorldPos(p.Pos.Map, (byte)x, (byte)y);

                var target = _world.PlayerAt(tile);
                if (target is { IsAlive: true })
                {
                    // Case 1 cura: sólo si PuedeAyudar (gate de facción). Case 2 daño: a cualquiera MENOS a uno
                    // mismo — el original excluye al lanzador con `If Not TargetUser = UserIndex`, así que la
                    // Bomba mágica no se autodaña aunque el 3×3 incluya al mago si está pegado al centro.
                    if (spell.IsHeal) { if (PuedeAyudar(p, target)) AreaHealPlayer(session, p, target, spell); }
                    else if (spell.IsDamage && target.CharIndex != p.CharIndex) AreaDamagePlayer(session, p, target, spell);
                }

                // El NPC del tile se resuelve APARTE del jugador y en el mismo paso, no en un `else`: un tile con
                // criatura pisando a un jugador recibe las dos cosas (VB6 :431, fuera del `If TargetUser > 0`).
                var npc = _world.NpcAt(tile);
                if (npc is { IsAlive: true }) AreaSpellOnNpc(session, p, npc, spell);
            }

        // Los tres paquetes al terreno, una vez y en el orden del original (:469-471).
        if (spell.Particle > 0)
            _world.BroadcastToObservers(p.Pos, ServerMessages.EfectoTerrenoParticula(
                (short)spell.Particle, cx, cy, spell.TimeParticula));
        if (spell.FxGrh > 0)
            _world.BroadcastToObservers(p.Pos, ServerMessages.EfectoTerrenoFX(
                (short)spell.FxGrh, cx, (byte)Math.Min(byte.MaxValue, cy + 1), (short)spell.FxLoops));
        if (spell.Wav > 0)
            _world.BroadcastToObservers(p.Pos, ServerMessages.PlayWave((short)spell.Wav, cx, cy));
        // Y las PALABRAS MÁGICAS al final, después del FX del terreno (:472). Acá el hechizo de área tiene su
        // propia llamada porque no pasa por `InfoHechizo`: esa función arranca con
        // `If Hechizos(spellindex).HechizoDeArea Then Exit Sub` (:1741), o sea que un hechizo de área nunca
        // llega a la línea :1743.
        DecirPalabrasMagicas(p, spell);

        SpellCasted(session, p, spell);                      // el cobro va al final (VB6 `If HechizoCasteado`)
        EmitirCostoDelHechizo(session, p);
        session.Send(ServerMessages.UpdateSta((short)p.Stamina));     // maná Y stamina del lanzador (VB6 los manda por separado)
    }

    /// <summary>Una víctima de la cura de área (VB6 <c>HechizoTerrenoHP</c> con <c>Accion = True</c>,
    /// modHechizos.bas:492). La tirada de HP se hace POR OBJETIVO, no una vez para todos: el
    /// <c>RandomNumber(MinHP, MaxHP)</c> vive dentro de la función que el bucle llama por víctima.</summary>
    private void AreaHealPlayer(ClientSession session, Player p, Player target, Data.SpellInfo spell)
    {
        int healed = Combat.EfectoHechizoArea3Pct(_rng.Next(spell.MinHp, spell.MaxHp + 1), p.Level);
        target.Hp = Math.Min(target.MaxHp, target.Hp + healed);
        target.Connection?.Send(ServerMessages.UpdateHP((short)target.Hp));
        if (target.CharIndex != p.CharIndex)
        {
            session.Send(ServerMessages.LocaleMsg(445, $"{healed}%{target.Name}"));
            target.Connection?.Send(ServerMessages.LocaleMsg(32, $"{target.Name}%{healed}"));
        }
        else session.Send(ServerMessages.LocaleMsg(33, healed.ToString()));
        // El número flotante sale con el charIndex DEL LANZADOR y sólo a él (`WriteChatOverHeadLocale(UserIndex,
        // UserList(UserIndex).Char.CharIndex, ...)`): el mago ve el total curándose sobre su propia cabeza.
        session.Send(ServerMessages.ChatOverHeadLocale(p.CharIndex, healed, 3));
    }

    /// <summary>Una víctima del daño de área (VB6 <c>HechizoTerrenoHP</c> con <c>Accion = False</c>,
    /// modHechizos.bas:515). Esta es la fórmula completa, que el camino de un solo objetivo del port no tiene:
    /// bono del <b>2%</b> por nivel (no 3%, que es el de la cura y el de los NPCs), <b>+CuantoAumento</b> si el
    /// arma equipada es un báculo con <c>EfectoMagico = DañoMagico</c>, y <b>−ResistenciaMagica</b> de cada pieza
    /// de defensa que lleve la víctima. Piso en 0.
    ///
    /// Del original quedan afuera dos restas por ítems que este port no modela como equipables: el anillo y la
    /// montura. Y el veneno (<c>Envenena</c>) se aplica FUERA del gate de zona, o sea que envenena incluso donde
    /// el daño no entra — ninguno de los 4 hechizos de daño de área lo trae, así que la rama no tiene data.</summary>
    private void AreaDamagePlayer(ClientSession session, Player p, Player target, Data.SpellInfo spell)
    {
        var weapon = Equipment.EquippedOfCategory(p, _data, 1);
        int bonoBaculo = weapon is { EfectoMagico: 14 } ? weapon.CuantoAumento : 0;   // eMagicType.DañoMagico
        // Los CINCO slots que descuenta el original (casco, escudo, armadura, montura, anillo). Antes acá sólo
        // se miraban los tres de defensa, así que la montura Unicornio (RM +10) no protegía nada.
        int resistencia = Equipment.MagicResistance(target, _data);
        int damage = Combat.DanoHechizoOfensivo(_rng.Next(spell.MinHp, spell.MaxHp + 1), p.Level, bonoBaculo, resistencia);

        FlagCriminalIfLegalTarget(session, p, target);    // VB6 UsuarioAtacadoPorUsuario
        target.Hp = Math.Max(0, target.Hp - damage);
        target.Connection?.Send(ServerMessages.UpdateHP((short)target.Hp));
        session.Send(ServerMessages.ChatOverHeadLocale(p.CharIndex, damage, 2));          // el número, al lanzador
        target.Connection?.Send(ServerMessages.LocaleMsg(34, $"{p.Name}%{damage}"));      // "#1 te ha quitado..."
        if (target.Hp == 0) OnKill(session, p, target);
    }

    /// <summary>La criatura de un tile del área (VB6 <c>HechizoPropNPC</c>, modHechizos.bas:1639). Ojo la
    /// asimetría con los jugadores, que es de la fuente: acá el bono es del <b>3%</b> por nivel en las dos
    /// direcciones y no hay resistencia mágica que descontar. Curar sólo funciona sobre una criatura NO hostil
    /// (<c>If Npclist(npcindex).Hostile > 0 Then ... Exit Sub</c>), o sea las mascotas.</summary>
    /// <summary>NO SE PORTA ACÁ EL <c>Snd2</c> DE LA CRIATURA, y es deliberado. El original lo emite en el
    /// barrido de área de <c>HechizoTerrenoEstado</c> (modHechizos.bas:438), justo después de
    /// <c>HechizoPropNPC</c> y para CUALQUIER criatura que caiga en el área — mientras que este método sale
    /// temprano en tres casos (cura sobre hostil, hechizo sin daño, NPC de servicio) y sólo modela daño y cura.
    /// Meter el sonido en una estructura que no es la misma emitiría donde el original calla, que es la clase
    /// de error que sólo caza el oráculo… y que el oráculo NO cazaría acá, porque su barrido nunca lanza un
    /// hechizo de área sobre una criatura. Los otros cinco sitios del original sí están portados: el rugido al
    /// atacar, el quejido al recibirlo a melee (jugador→criatura y criatura→criatura) y el grito al morir.</summary>
    private void AreaSpellOnNpc(ClientSession session, Player p, Npc npc, Data.SpellInfo spell)
    {
        if (spell.IsHeal)
        {
            if (npc.Hostile) return;
            int healed = Combat.EfectoHechizoArea3Pct(_rng.Next(spell.MinHp, spell.MaxHp + 1), p.Level);
            npc.Hp = Math.Min(npc.Template.MaxHp, npc.Hp + healed);
            session.Send(ServerMessages.ChatOverHeadLocale(p.CharIndex, healed, 3));
            return;
        }
        if (!spell.IsDamage) return;   // SubeHP = 0 (Tornado): ningún Case lo agarra
        // PuedeAtacarNPC: los NPCs de servicio no se tocan. Silencio, no LocaleMsg(298): el original sale con
        // `HechizoCasteado = False` y sin avisar nada, porque en un área es normal barrer tiles no válidos.
        if (!npc.Attackable || npc.IsMerchant || npc.IsBanker || npc.IsReviver || npc.IsWorkbench
            || npc.IsTrainer || npc.FactionConvert != 0) return;
        npc.AttackedBy = p.Name;   // NPCAtacado
        npc.Hostile = true;
        var staff = Equipment.EquippedOfCategory(p, _data, 1);
        int damage = Combat.EfectoHechizoArea3Pct(_rng.Next(spell.MinHp, spell.MaxHp + 1), p.Level,
            staff is { EfectoMagico: 14 } ? staff.CuantoAumento : 0);
        npc.Hp = Math.Clamp(npc.Hp - damage, 0, npc.Template.MaxHp);
        session.Send(ServerMessages.ChatOverHeadLocale(p.CharIndex, damage, 2));
        if (npc.Hp == 0) NpcKilledByPlayer(session, p, npc);
    }

    /// <summary>Ceguera side-effect: any spell with Ceguera&gt;0 blinds its target for a while. Blind and
    /// BlindNoMore are distinct packets, so Blind is sent on the transition into blindness and BlindNoMore
    /// on expiry (reconciled in the Program.cs tick).</summary>
    private void ApplyBlind(Player target, Data.SpellInfo spell)
    {
        if (spell.Ceguera <= 0) return;
        target.BlindedUntilTick = _loop.Tick + 166; // IntervaloParalizado / 3 (modHechizos.bas:1309) ≈ 6,7 s
        if (!target.ClientBlind) { target.ClientBlind = true; target.Connection?.Send(ServerMessages.Blind()); }
    }

    /// <summary>Aplica sobre <paramref name="target"/> TODOS los efectos de estado que traiga el hechizo, con
    /// <c>If</c> INDEPENDIENTES y en el orden del original (<c>HechizoEstadoUsuario</c>, modHechizos.bas:
    /// Sanacion :1048 → Envenena :1096 → Incinera :1127 → CuraVeneno :1157 → Paraliza/Inmoviliza :1180 →
    /// RemoverParalisis :1213 → Ceguera :1290).
    ///
    /// POR QUÉ EXISTE. El VB6 rutea TODOS los hechizos con objetivo usuario al mismo Sub, que es una única
    /// secuencia de <c>If</c> sueltos donde <c>Select SubeHP</c> es el ÚLTIMO (:1412). Acá el despacho era una
    /// cadena <c>if / else if</c> por tipo, así que la primera bandera que matcheaba se comía todas las demás.
    /// Cinco hechizos del install quedaban a medias, y ninguno daba error:
    /// <list type="table">
    /// <item>32 Sanar — SubeHP=1 + RemoverParalisis + Sanacion → curaba y nada más (la incineración quedaba SIN antídoto)</item>
    /// <item>57 Tormenta ácida — SubeHP=2 + Envenena → no envenenaba</item>
    /// <item>69 Descarga Flamígera — SubeHP=2 + Incinera → no incineraba</item>
    /// <item>35 Lluvia de Mosquitos — Paraliza + Ceguera → no cegaba</item>
    /// <item>90 Prevención del mal — Invisibilidad + SubeAG → no daba agilidad (ver la rama IsInvisibility)</item>
    /// </list>
    /// El cobro de maná/stamina NO va acá: el original lo hace UNA vez en el caller (<c>HechizoCasteado</c>),
    /// así que cobrar por efecto cobraría de más.
    ///
    /// No se modela <c>Estupidez</c>/<c>RemoverEstupidez</c> — el port no tiene ese estado. Sanar trae
    /// <c>RemoverEstupidez=1</c> y esa bandera sigue sin efecto, igual que antes.</summary>
    private List<string> AplicarEfectosDeEstado(Player caster, Player target, Data.SpellInfo spell)
    {
        const long StateDurationTicks = 500;   // IntervaloParalizado (Server.ini), en vueltas de 40 ms
        long now = _loop.Tick;
        var efectos = new List<string>();
        if (spell.IsSanacion) { target.PoisonedUntilTick = 0; target.Incinerado = false; efectos.Add("sanado"); }
        // EL VENENO NO SE VA SOLO: en el original es un flag pelado, sin contador de duración. Lo apagan Curar
        // Veneno, la Sanación, el sacerdote (Acciones.bas:317) o la muerte.
        if (spell.IsPoison)
        {
            target.PoisonedUntilTick = long.MaxValue; target.PoisonCounter = 0;
            efectos.Add("envenenado");
        }
        // INCINERAR NO ES ENVENENAR (VB6 modHechizos.bas:1127). Tiene su propio flag, su propio contador y su
        // propio efecto: 45-65 de daño por golpe de fuego —contra el 1-5 del veneno— y se apaga solo a las 50
        // vueltas. Acá prendía el flag del VENENO, así que incinerar a alguien lo dejaba envenenado PARA
        // SIEMPRE y pegando diez veces menos. Y el original rechaza incinerar a quien ya está ardiendo
        // (LocaleMsg 326), que era otra rama que no existía.
        if (spell.IsIncinerate)
        {
            if (target.Incinerado) { caster.Connection?.Send(ServerMessages.LocaleMsg(326)); }   // ya está ardiendo
            else { target.Incinerado = true; target.IncineradoCounter = 0; efectos.Add("incinerado"); }
        }
        // Curar veneno a alguien que NO está envenenado: el original refuta con el 451 ("El objetivo no está
        // bajo el efecto de ese modificador", modHechizos.bas:1160) y NO castea. Acá se aplicaba igual y
        // salía "curado del veneno" sobre alguien sano.
        if (spell.IsCurePoison)
        {
            if (!target.IsPoisoned(now)) { caster.Connection?.Send(ServerMessages.LocaleMsg(451)); }
            else { target.PoisonedUntilTick = 0; efectos.Add("curado del veneno"); }
        }
        if (spell.IsParalyze || spell.IsImmobilize)
        {
            target.ParalyzedUntilTick = now + StateDurationTicks;
            efectos.Add(spell.IsImmobilize ? "inmovilizado" : "paralizado");
            // Quién lo paralizó: el efecto se suelta si el caster se muere o se aleja (VB6 EfectoParalisisUser,
            // General.bas:978 — ver el bloque del game-loop en Program.cs).
            target.ParalyzedByCharIndex = target.CharIndex == caster.CharIndex ? (short)0 : caster.CharIndex;
            // ParalizeOK es un TOGGLE del lado del cliente: sólo se manda en la transición.
            if (!target.ClientParalyzed) { target.ClientParalyzed = true; target.Connection?.Send(ServerMessages.ParalizeOK()); }
        }
        // Va DESPUÉS de paralizar, como en el original: un hechizo con las dos banderas termina liberando.
        if (spell.IsRemoveParalysis)
        {
            target.ParalyzedUntilTick = 0; target.ParalyzedByCharIndex = 0; efectos.Add("liberado de la parálisis");
            if (target.ClientParalyzed) { target.ClientParalyzed = false; target.Connection?.Send(ServerMessages.ParalizeOK()); }
        }
        if (spell.Ceguera > 0) { ApplyBlind(target, spell); efectos.Add("cegado"); }
        return efectos;
    }

    /// <summary>Aplica los buffs/debuffs de atributo del hechizo y emite SÓLO el atributo que tocó.
    ///
    /// El original son dos <c>Select Case</c> INDEPENDIENTES y en este orden: <c>SubeAgilidad</c>
    /// (modHechizos.bas:1322, emite <c>WriteUpdateDexterity</c>) y <c>SubeFuerza</c> (:1365, emite
    /// <c>WriteUpdateStrenght</c>). Acá se mandaban los DOS siempre, con una cita que leía esas dos líneas como
    /// si fueran un bloque único; el único lugar donde el VB6 manda ambos es la expiración de pociones
    /// (General.bas:1166), y ahí el propio autor lo comenta como excepción que no le gusta.</summary>
    private List<string> AplicarBuffsDeAtributo(Player target, Data.SpellInfo spell)
    {
        var parts = new List<string>();
        if (spell.SubeAgilidad > 0)
        {
            int roll = _rng.Next(spell.MinAgilidad, spell.MaxAgilidad + 1);
            int delta = spell.SubeAgilidad == 2 ? -roll : roll;
            long dur = spell.SubeAgilidad == 2 ? AttrDebuffDurationTicks : AttrBuffDurationTicks;
            target.ApplyAttrBonus(2, delta, _loop.Tick, dur);
            parts.Add($"{(delta >= 0 ? "+" : "")}{delta} agilidad");
            target.Connection?.Send(ServerMessages.UpdateDexterity(target.Agility));
        }
        if (spell.SubeFuerza > 0)
        {
            int roll = _rng.Next(spell.MinFuerza, spell.MaxFuerza + 1);
            int delta = spell.SubeFuerza == 2 ? -roll : roll;
            long dur = spell.SubeFuerza == 2 ? AttrDebuffDurationTicks : AttrBuffDurationTicks;
            target.ApplyAttrBonus(1, delta, _loop.Tick, dur);
            parts.Add($"{(delta >= 0 ? "+" : "")}{delta} fuerza");
            target.Connection?.Send(ServerMessages.UpdateStrenght(target.Strength));
        }
        return parts;
    }

    /// <summary>EL CASTEO SON DOS PAQUETES, como en el original (Protocol.bas:2142-2186). Este es el primero:
    /// <c>CastSpell(ranura)</c> NO lanza nada — deja la ranura elegida en <c>flags.Hechizo</c> y le pide al
    /// cliente que marque un blanco con <c>WorkRequestTarget(magia)</c>. El clic siguiente llega como
    /// <c>WorkLeftClick(x, y, magia)</c> y ESE es el que resuelve (<see cref="LanzarHechizoElegido"/>).
    ///
    /// La única excepción es la de la fuente: un hechizo <c>AutoLanzar</c> se tira sobre uno mismo sin pedir
    /// nada (:2138-2140), y son los 19 auto-buffs.
    ///
    /// Antes el port resolvía TODO acá, contra el tile ENCARADO. Funcionaba a medias — el tester lo reportó como
    /// "no deja tirar hechizos" (2.6) — y el barrido del oráculo lo midió como dos gaps: nunca se emitía
    /// <c>WorkRequestTarget</c>, y el rechazo del hechizo salía en el paquete equivocado. También cambia QUÉ se
    /// valida acá: el original en este paquete sólo mira que el jugador esté vivo y que la ranura tenga algo;
    /// la cadencia, el mapa sin magia y <c>PuedeLanzar</c> corren recién en el clic.</summary>
    private void HandleCastSpell(ClientSession session, ByteQueueReader r)
    {
        byte slot = r.ReadByte();
        _loop.Enqueue(() =>
        {
            var p = session.Player;
            if (p is null || !p.IsAlive) return;
            if (slot < 1 || slot > GameLimits.MaxUserSpells) { p.HechizoElegido = 0; return; }
            p.HechizoElegido = slot;
            var spell = _data.Spell(p.Spells[slot]);
            if (spell is null) return;   // `If uh <> 0`: ranura vacía -> silencio, pero la ranura queda elegida
            if (spell.EsAutoLanzado)
            {
                // `.flags.TargetUser = UserIndex : LanzarHechizo(...)` — sobre uno mismo, sin clic.
                LanzarHechizoElegido(session, p, p.Pos.X, p.Pos.Y);
                return;
            }
            session.Send(ServerMessages.WorkRequestTarget((byte)GameLimits.SkillMagia));
        });
    }

    /// <summary>El segundo paquete del casteo: el <c>Case eSkill.magia</c> del <c>WorkLeftClick</c>
    /// (Protocol.bas:2918-2951) y de ahí <c>LanzarHechizo</c> (modHechizos.bas:875). Recibe el TILE clickeado
    /// y resuelve el hechizo que <see cref="HandleCastSpell"/> dejó elegido. Los gates, en el orden de la fuente:
    /// mapa sin magia (385), <c>LookatTile</c> sobre el tile —que es lo que selecciona al objetivo y manda su
    /// ficha—, el rango de visión, la cadencia y recién ahí <c>PuedeLanzar</c> y el hechizo en sí.
    ///
    /// Se llama con el tile del clic (o el propio, para los <c>AutoLanzar</c>). Adentro, "el objetivo" es lo que
    /// haya en ese tile: el original lo lee de <c>flags.TargetUser/TargetNPC</c>, que <c>LookatTile</c> acaba de
    /// dejar, y este port lo lee del tile directamente — es lo mismo, sin pasar por el índice.</summary>
    private void LanzarHechizoElegido(ClientSession session, Player p, byte clickX, byte clickY)
    {
        {
            byte slot = p.HechizoElegido;
            p.HechizoElegido = 0;   // `.flags.Hechizo = 0` (:2950): se consume, haya salido o no
            if (slot < 1 || slot > GameLimits.MaxUserSpells) return;
            var spell = _data.Spell(p.Spells[slot]);
            if (spell is null) return;
            // MAPA SIN MAGIA (VB6 Protocol.bas:2921): antes que cualquier otro requisito, y ni un GM se salva. Lo
            // prende /MODMAPINFO MAGIASINEFECTO. Y ANTES del LookatTile: en un mapa sin magia el clic no
            // selecciona nada.
            if (_data.MapNoMagic(p.Pos.Map)) { session.Send(ServerMessages.LocaleMsg(385)); return; }
            // `Call LookatTile(UserIndex, .Pos.Map, X, Y)` (:2927): el clic del hechizo SELECCIONA, igual que un
            // clic normal — de ahí que el original mande el CharMsgStatus de quien esté en el tile.
            LookatTile(session, p, clickX, clickY);
            // Fuera del rango de visión: `LogCheating` y salir, sin avisar (:2930).
            if (Math.Abs(p.Pos.X - clickX) > RangoVisionX || Math.Abs(p.Pos.Y - clickY) > RangoVisionY) return;
            // CADENCIA DE CASTEO (:2936-2945). El port no tenía NINGUNA: un mago tiraba hechizos a velocidad de
            // red, con el `IntervaloLanzaHechizo=1000` del Server.ini ahí escrito y sin leer. La forma es la
            // misma del melee: un OR entre el atajo de castear-después-de-pegar y la cadencia normal. Rechazo
            // silencioso, como el VB6.
            long castMs = Environment.TickCount64;
            if (!CombatTiming.PermiteGolpeMagia(p, castMs, _intervaloGolpeMagia)
                && !CombatTiming.PermiteLanzarSpell(p, castMs, _intervaloCastear)) return;
            // EL TILE QUE RECIBE EL HECHIZO es el clickeado. Los `autolanzar` llegan acá con el tile propio.
            (byte, byte) TileObjetivo() => (clickX, clickY);
            // VB6 PuedeLanzar (modHechizos.bas:319), en su orden: un GM saltea todos los requisitos; si no,
            // primero la skill de magia mínima (221), después la stamina (93) y por último el maná (222).
            // Antes sólo se miraba el maná, así que un nivel 1 podía tirar el hechizo más caro del juego.
            if (!p.IsGm)
            {
                if (p.Skills[GameLimits.SkillMagia] < spell.MinSkill) { session.Send(ServerMessages.LocaleMsg(221)); return; }
                if (p.Stamina < spell.StaRequerido) { session.Send(ServerMessages.LocaleMsg(93)); return; }
                if (p.Mana < spell.ManaCost) { session.Send(ServerMessages.LocaleMsg(222)); return; }
                if (!HasRequiredRing(p, spell, out var ringMsg)) { session.Send(ServerMessages.LocaleMsg(ringMsg)); return; }
            }
            // El maná, la stamina y SubirSkill(magia) NO se cobran acá: son atómicos y van en SpellCasted(),
            // que cada rama llama SÓLO cuando el hechizo se lanzó sobre un objetivo válido (VB6 HechizoCasteado).
            // Cobrarlos antes de resolver el objetivo dejaba entrenar Magia gratis casteando al aire.
            // GATE DE MODO COMBATE (VB6 `SeguroCheck(UserIndex, 1)`, InvUsuario.bas:2490): sin el modo combate
            // prendido responde LocaleMsg(102) "debes estar en modo combate" y NO lanza. Es el mismo seguro que
            // ya gatea el ataque cuerpo a cuerpo, y existe para no dispararle sin querer a un aliado.
            //
            // OJO LA ASIMETRÍA, que es de la fuente y no un olvido: el gate está en `HandleHechizoTerreno`
            // (modHechizos.bas:701) y en `HandleHechizoUsuario` (:763), pero NO en `HandleHechizoNPC` (:813).
            // O sea: castearle a una CRIATURA no lo exige, castearle a un JUGADOR o al TERRENO sí. `LanzarHechizo`
            // (:875) elige el handler por `spell.Target` — 4 terreno, 1 usuarios, 2 NPC, 3 ambos (usuario primero
            // y si no hay, NPC) —, así que acá se replica esa misma decisión.
            //
            // Tampoco distingue el TIPO de hechizo: en el original hay que estar en modo combate hasta para
            // CURAR a un aliado, porque SeguroCheck corre antes de mirar qué hace el hechizo. Se porta así.
            // Y no exime al GM: SeguroCheck no mira privilegios (corre después de PuedeLanzar, que sí los mira).
            bool haySobreNpc;
            {
                var (sx, sy) = TileObjetivo();
                var tile = new WorldPos(p.Pos.Map, sx, sy);
                bool hayJugador = _world.PlayerAt(tile) is not null;
                haySobreNpc = spell.Target == 2 || (spell.Target == 3 && !hayJugador && _world.NpcAt(tile) is not null);
            }
            if (!haySobreNpc && !p.CombatMode) { session.Send(ServerMessages.LocaleMsg(102)); return; }

            RevealHidden(p);   // castear rompe el ocultamiento (VB6: lanzar un hechizo revela al oculto)
            // EL PORTAL DE LUM va por índice de hechizo, no por un flag del data: el original abre con
            // `If HechizoIndex <> 53 Then Exit Sub` (modHechizos.bas:1948). Es el único hechizo cableado por
            // número de todo el subsistema, y por eso no hay campo en Hechizos.dat que lo distinga.
            if (spell.Index == HechizoPortalLum) { CrearPortalDeLum(session, p, spell); return; }
            int amount = _rng.Next(spell.MinHp, spell.MaxHp + 1); // RandomNumber(MinHP, MaxHP)
            // HECHIZO DE ÁREA: va ANTES de las ramas por SubeHP porque el original lo rutea antes también
            // (`HandleHechizoTerreno` → `HechizoTerrenoEstado`, que resuelve el 3×3 entero y NO pasa por el
            // camino de un solo objetivo). Sin esto la Bomba mágica y la Tormenta de fuego le pegaban a UNA
            // sola víctima, y el Círculo curativo curaba a UNO.
            if (spell.IsArea && spell.IsTerrainState) { CastAreaSpell(session, p, spell, clickX, clickY); return; }
            if (spell.IsRevive)
            {
                // Hechizo Resucitar (VB6 modHechizos.bas:981): revive al jugador MUERTO al frente. El caster
                // ya está vivo (HandleCastSpell sale con !p.IsAlive). El slice omite los gates de arena/ModoCombate.
                var (tx, ty) = TileObjetivo();
                var target = _world.PlayerAt(new WorldPos(p.Pos.Map, tx, ty));
                if (target is null) { session.Send(ServerMessages.LocaleMsg(SinObjetivo(spell))); return; }
                if (target.IsAlive) { session.Send(ServerMessages.ConsoleMsg($"{target.Name} no está muerto.", 0)); return; }
                // MAPA SIN RESURRECCIÓN (VB6 modHechizos.bas:984): se mira el mapa del MUERTO, no el del caster,
                // y sale LocaleMsg(448) sin cobrar el maná. El original lo clava en la prisión al arrancar
                // (General.bas:298) para que a un preso no lo saquen resucitándolo desde afuera.
                if (_data.MapNoResu(target.Pos.Map)) { session.Send(ServerMessages.LocaleMsg(448)); return; }
                // EL MUERTO EN MODO COMBATE NO SE DEJA REVIVIR (VB6 modHechizos.bas:1000). En este fork el modo
                // combate hace de "seguro de resurrección": el 450 que le llega al muerto dice literalmente
                // "desactiva el seguro si quieres ser resucitado". Salen DOS mensajes, uno a cada uno, y el
                // hechizo NO se castea (`HechizoCasteado = False`), así que tampoco se cobra el maná.
                if (target.CombatMode)
                {
                    session.Send(ServerMessages.LocaleMsg(44));
                    target.Connection?.Send(ServerMessages.LocaleMsg(450, p.Name));
                    return;
                }
                SpellCasted(session, p, spell);
                EmitSpellFx(p, target.Pos, target.CharIndex, spell);
                RevivePlayer(_world, _data, target);                                       // HP full + apariencia + LocaleMsg(90) al target
                target.Connection?.Send(ServerMessages.LocaleMsg(532, p.Name));            // "#1 te ha resucitado"
                EmitirCostoDelHechizo(session, p);
            }
            else if (spell.IsHeal)
            {
                // VB6 SubeHP=1 (modHechizos.bas:1412): la curación puede ir sobre un ALIADO, no sólo sobre uno
                // mismo. El objetivo es el jugador del tile encarado (mismo patrón que la magia de daño); si no
                // hay nadie, o es uno mismo, se autocura.
                var (hx, hy) = TileObjetivo();
                var ally = _world.PlayerAt(new WorldPos(p.Pos.Map, hx, hy));
                var heal = ally is { IsAlive: true } && ally.CharIndex != p.CharIndex ? ally : p;

                // Gate de facción (VB6 PuedeAyudar, modHechizos.bas:1910): un renegado ayuda a cualquiera; la
                // Armada/ciudadanos sólo entre sí; República/Milicia sólo entre sí; el Caos y los GMs a
                // cualquiera (agujero del original, se preserva). En el rechazo: LocaleMsg 449 y NO se cura.
                if (heal != p && !PuedeAyudar(p, heal)) { session.Send(ServerMessages.LocaleMsg(449)); return; }

                // Bono +3% por nivel DEL LANZADOR (VB6: daño + Porcentaje(daño, 3 * ELV)).
                int healed = Combat.EfectoHechizoArea3Pct(amount, p.Level);
                SpellCasted(session, p, spell);
                heal.Hp = Math.Min(heal.MaxHp, heal.Hp + healed);
                EmitSpellFx(p, heal.Pos, heal.CharIndex, spell);
                // Un hechizo de curación puede traer ADEMÁS efectos de estado, y el original los aplica todos:
                // "Sanar" (32) es SubeHP=1 + RemoverParalisis + Sanacion. Acá sólo se llamaba a ApplyBlind, así
                // que Sanar curaba y dejaba al jugador paralizado y quemándose.
                AplicarEfectosDeEstado(p, heal, spell);
                heal.Connection?.Send(ServerMessages.UpdateHP((short)heal.Hp));   // VB6 modHechizos.bas:1018
                // El número CURADO flotando, en modo 3 (azul), y como en el resto del original con el charIndex
                // DEL LANZADOR y sólo a él (:1433, la línea siguiente al UpdateHP). Faltaba: el camino de área ya
                // lo mandaba y el de un solo objetivo no, así que curar a un aliado no mostraba cuánto.
                session.Send(ServerMessages.ChatOverHeadLocale(p.CharIndex, healed, 3));
                EmitirCostoDelHechizo(session, p);

                if (heal == p)
                    session.Send(ServerMessages.LocaleMsg(499, $"^{spell.Index}%{healed}"));   // autocura
                else
                {
                    session.Send(ServerMessages.LocaleMsg(497, $"^{spell.Index}%{heal.Name}%{healed}"));       // al lanzador
                    heal.Connection?.Send(ServerMessages.LocaleMsg(498, $"{p.Name}%^{spell.Index}%{healed}")); // al objetivo
                }
            }
            else if (spell.IsDamage)
            {
                var (tx, ty) = TileObjetivo();
                var spellPos = new WorldPos(p.Pos.Map, tx, ty);
                var target = _world.PlayerAt(spellPos);
                // PvE: si no hay jugador en el tile encarado, el hechizo va contra el NPC que esté ahí (VB6
                // HandleHechizoNPC -> HechizoPropNPC). Sin esta rama la magia ofensiva era sólo PvP y un mago
                // no podía cazar: nukear una criatura devolvía "No hay objetivo".
                if (target is null)
                {
                    var npcTarget = _world.NpcAt(spellPos);
                    if (npcTarget is not null && npcTarget.IsAlive) { CastDamageOnNpc(session, p, npcTarget, spell, amount); return; }
                }
                if (target is null || target.CharIndex == p.CharIndex) { session.Send(ServerMessages.LocaleMsg(SinObjetivo(spell))); return; }
                if (!PuedeAtacar(session, p, target)) return;    // VB6 modHechizos.bas:1109 — el mismo gate del melee
                FlagCriminalIfLegalTarget(session, p, target);   // lanzar daño a un legal pena igual que el melee (VB6: el "seguro" aplica a magia ofensiva)
                SpellCasted(session, p, spell);
                // MISMA fórmula que el área (VB6 `HechizoPropUsuario` Case 2, modHechizos.bas:1441): bono del 2%
                // por nivel, + el báculo de DañoMagico, − la resistencia mágica de los cinco slots equipados.
                // Acá antes se restaba `amount` pelado, así que la magia PvP ignoraba tanto el nivel del mago
                // como la armadura anti-magia de la víctima: un casco con ResistenciaMagica 20 no servía de nada.
                var staff = Equipment.EquippedOfCategory(p, _data, 1);
                amount = Combat.DanoHechizoOfensivo(amount, p.Level,
                    staff is { EfectoMagico: 14 } ? staff.CuantoAumento : 0,
                    Equipment.MagicResistance(target, _data));
                target.Hp = Math.Max(0, target.Hp - amount);
                // VB6 :366 `Call SubirSkill(targetIndex, eSkill.Resistencia)`: aguantar un hechizo entrena la
                // Resistencia MÁGICA de quien lo recibe. Es del camino de un solo objetivo nada más — el 3×3 del
                // área no la sube (`HechizoTerrenoHP` no llama a SubirSkill), y esa asimetría es de la fuente.
                RaiseSkill(target.Connection as ClientSession, target, GameLimits.SkillResistencia);
                EmitSpellFx(p, target.Pos, target.CharIndex, spell);
                // Un hechizo de daño puede traer ADEMÁS efectos de estado: "Tormenta ácida" (57) es SubeHP=2 +
                // Envenena, y "Descarga Flamígera" (69) SubeHP=2 + Incinera. Acá sólo se llamaba a ApplyBlind,
                // así que los dos pegaban pero nunca envenenaban ni incineraban.
                AplicarEfectosDeEstado(p, target, spell);
                session.Send(ServerMessages.LocaleMsg(497, $"^{spell.Index}%{target.Name}%{amount}"));
                target.Connection?.Send(ServerMessages.LocaleMsg(498, $"{p.Name}%^{spell.Index}%{amount}"));
                target.Connection?.Send(ServerMessages.UpdateHP((short)target.Hp));
                // El número de DAÑO flotando (:1494, modo 2, rojo). Mismo caso que la cura: el área lo mandaba y
                // el camino de un solo objetivo no. Va con el charIndex del LANZADOR y sólo a él, igual que en el
                // melee — el original dibuja el número sobre TU cabeza, no sobre la de la víctima.
                session.Send(ServerMessages.ChatOverHeadLocale(p.CharIndex, amount, 2));
                EmitirCostoDelHechizo(session, p);
                if (target.Hp == 0) OnKill(session, p, target);
            }
            else if (spell.IsInvoke)
            {
                // VB6 HechizoInvocacion (modHechizos.bas:609): "No permitimos se invoquen criaturas en zonas
                // seguras" — en un tile ZONASEGURA sale SIN invocar y SIN avisar (el WriteMensajes del original
                // está comentado), y como no llega a marcar HechizoCasteado tampoco se consume el maná.
                if (_data.IsSafeZone(p.Pos.Map, p.Pos.X, p.Pos.Y)) return;
                var template = _data.Npc(spell.InvokeNpc);
                if (template is null) { session.Send(ServerMessages.ConsoleMsg("No se puede invocar esa criatura.", 0)); return; }
                // DIVERGENCIA DELIBERADA, documentada en fase5/GAPS-CODIGO-MUERTO.md: en el VB6 la invocación
                // NO FUNCIONA. `HechizoInvocacion` (modHechizos.bas:597) es código muerto — su único call site
                // (:710) está COMENTADO — y la rama viva `Case uInvocacion` responde LocaleMsg(435) y nada más:
                // ni spawnea, ni cobra maná. Acá se mantiene funcional a propósito, porque es gameplay que el
                // original perdió al comentar la llamada. Consecuencia: el oráculo VB6 no puede validar este
                // camino, así que un barrido que lo compare lo verá como divergencia, no como gap.
                //
                // Cupo COMPARTIDO domar+invocar: MAXMASCOTAS = 3 (Declares.bas:444). Se cuenta por LUGARES
                // ocupados, así que una domada que espera afuera de una ciudad te sigue ocupando el suyo — igual
                // que en el original, y a diferencia de como estaba acá antes (conteo de NPCs vivos).
                int current = CuentaMascotas(p);
                if (current >= GameLimits.MaxMascotas) { session.Send(ServerMessages.ConsoleMsg("No puedes controlar más criaturas.", 0)); return; }
                SpellCasted(session, p, spell);
                int summoned = 0;
                foreach (var pos in FreeTilesAround(p.Pos, Math.Min((int)spell.InvokeCount, GameLimits.MaxMascotas - current)))
                {
                    var npc = new Npc
                    {
                        CharIndex = _world.AllocateCharIndex(),
                        Template = template,
                        Hp = template.MaxHp,
                        Pos = pos,
                        OwnerCharIndex = p.CharIndex, // won't attack its summoner
                        ExpireAtTick = _loop.Tick + SummonLifetimeTicks,  // VB6 TiempoExistencia = IntervaloInvocacion
                    };
                    _world.AddNpc(npc);
                    // La invocación ocupa un lugar igual que la domada: el cupo de 3 es UNO SOLO y en el
                    // original lo escriben los dos (VB6 HechizoInvocacion, modHechizos.bas:646-651).
                    p.OcuparSlotDeMascota(p.PrimerSlotLibre(), npc.CharIndex, npc.Template.Index);
                    _world.BroadcastToObservers(npc.Pos, ServerMessages.CharacterCreate(npc));
                    summoned++;
                }
                EmitSpellFx(p, p.Pos, p.CharIndex, spell);
                session.Send(ServerMessages.ConsoleMsg($"Lanzaste {spell.Name}: invocaste {summoned}x {template.Name}.", 0));
                EmitirCostoDelHechizo(session, p);
            }
            else if (spell.IsInvisibility)
            {
                // Invisibilidad (VB6 Case uEstado, Invisibilidad=1, modHechizos.bas): oculta al LANZADOR (Target 1).
                // Reusa el mismo estado que el /INVISIBLE de GM (p.Invisible + SetInvisible a observadores), pero con
                // vencimiento por tick — el game loop lo revierte cuando InvisibleUntilTick pasa. La IA de NPCs y el
                // filtrado de observadores ya respetan p.Invisible, así que quedar invisible corta el aggro.
                // YA INVISIBLE (VB6 modHechizos.bas:1078, y va ANTES del chequeo del mapa): LocaleMsg(68) y
                // `HechizoCasteado = False`, o sea que tampoco se cobra el maná. Faltaba: volver a lanzarla
                // estando invisible re-casteaba y cobraba, que es una fuga de maná chica pero real.
                if (p.Invisible) { session.Send(ServerMessages.LocaleMsg(68)); return; }
                // MAPA SIN INVISIBILIDAD (VB6 modHechizos.bas:1083): LocaleMsg(448) y el hechizo NO se casteó
                // —o sea que tampoco se cobra el maná, porque el original pone HechizoCasteado = False—.
                if (_data.MapNoInvi(p.Pos.Map)) { session.Send(ServerMessages.LocaleMsg(448)); return; }
                SpellCasted(session, p, spell);
                // No pisar la invisibilidad PERMANENTE de un GM: /INVISIBLE deja InvisibleUntilTick=0 (permanente),
                // y si el hechizo le fijara un vencimiento el game loop lo destaparía ~20 s después. Sólo fijamos el
                // timer si no está ya permanentemente invisible.
                if (!(p.Invisible && p.InvisibleUntilTick == 0))
                    p.InvisibleUntilTick = _loop.Tick + InvisibilityDurationTicks;
                p.Invisible = true;
                _world.BroadcastToObservers(p.Pos, ServerMessages.SetInvisible(p.CharIndex, true), p.CharIndex);
                EmitSpellFx(p, p.Pos, p.CharIndex, spell);
                // "Prevención del mal" (90) es Invisibilidad=1 + SubeAG=1: en el original los dos bloques son
                // `If` independientes del mismo Sub (:1074 y :1322), así que da invisibilidad Y +35 de agilidad.
                // Acá la rama de invisibilidad cortaba antes y el bono nunca se aplicaba.
                var buffs = AplicarBuffsDeAtributo(p, spell);
                var estados = AplicarEfectosDeEstado(p, p, spell);
                string extra = buffs.Concat(estados).ToList() is { Count: > 0 } todos ? " (" + string.Join(", ", todos) + ")" : "";
                session.Send(ServerMessages.ConsoleMsg($"Lanzaste {spell.Name}: te has vuelto invisible.{extra}", 0));
                EmitirCostoDelHechizo(session, p);
            }
            else if (spell.IsAttrBuff)
            {
                // Buffs/debuffs de atributo (VB6 HechizoPropUsuario): Fuerza/Debilidad, Celeridad/Torpeza, Poder
                // divino... Suben (SubeX=1) o bajan (SubeX=2) Fuerza/Agilidad en RandomNumber(Min,Max). Reusa
                // Player.ApplyAttrBonus (el camino de las pociones violeta), así que la aritmética del bono y su
                // expiración (ExpireEffects en el loop) ya están probadas.
                //
                // OJO con Target: en el VB6 `Target=1` (uUsuarios) significa "apunta a un usuario" — NO "sobre uno
                // mismo": los hechizos de daño también son Target=1 y van al enemigo encarado. Por eso se resuelve
                // por el TILE ENCARADO, no por Target. Un DEBUFF (SubeX=2) es una agresión: exige objetivo distinto
                // y marca criminal como el melee/daño (VB6 HechizoPropUsuario → UsuarioAtacadoPorUsuario). Un BUFF
                // (SubeX=1) va sobre el aliado encarado (gate PuedeAyudar) o, si no hay nadie, sobre uno mismo.
                var (bx, by) = TileObjetivo();
                bool esDebuff = spell.SubeFuerza == 2 || spell.SubeAgilidad == 2;
                Player? target = _world.PlayerAt(new WorldPos(p.Pos.Map, bx, by));
                if (esDebuff)
                {
                    if (target is null || target.CharIndex == p.CharIndex) { session.Send(ServerMessages.LocaleMsg(SinObjetivo(spell))); return; }
                    if (!PuedeAtacar(session, p, target)) return;    // debilitar es agredir: mismo gate (VB6 modHechizos.bas:1269)
                    FlagCriminalIfLegalTarget(session, p, target);   // debilitar a un legal pena igual que pegarle
                }
                else
                {
                    if (target is null) target = p;                  // buff sin objetivo → sobre uno mismo
                    if (target != p && !PuedeAyudar(p, target)) { session.Send(ServerMessages.LocaleMsg(449)); return; }
                }
                SpellCasted(session, p, spell);
                var parts = AplicarBuffsDeAtributo(target, spell);
                EmitSpellFx(p, target.Pos, target.CharIndex, spell);
                session.Send(ServerMessages.ConsoleMsg($"Lanzaste {spell.Name}: {target.Name} {string.Join(", ", parts)}.", 0));
                EmitirCostoDelHechizo(session, p);
            }
            else if (spell.IsParalyze || spell.IsRemoveParalysis || spell.IsPoison || spell.IsCurePoison
                     || spell.IsImmobilize || spell.IsIncinerate || spell.IsSanacion || spell.EsResucitaFamiliar)
            {
                // Hechizos de estado. OJO con Target (igual que en IsAttrBuff): `Target=1` = "apunta a un usuario",
                // NO "self". Los OFENSIVOS (paralizar/inmovilizar/envenenar/incinerar) van sobre el jugador —o la
                // criatura— ENCARADO y marcan criminal como cualquier agresión; los de SOPORTE (curar veneno /
                // remover parálisis) pueden ir sobre uno mismo si no hay nadie al frente.
                var (sx, sy) = TileObjetivo();
                bool ofensivo = spell.IsParalyze || spell.IsImmobilize || spell.IsPoison || spell.IsIncinerate;
                Player? target = _world.PlayerAt(new WorldPos(p.Pos.Map, sx, sy));
                // Si al frente hay una CRIATURA (y el hechizo es ofensivo), el estado va sobre ella (VB6
                // HandleHechizoNPC → Case uEstado → HechizoEstadoNPC, modHechizos.bas:834). Sin esto, paralizar o
                // envenenar a un bicho devolvía "No hay objetivo para el hechizo".
                if (target is null && ofensivo)
                {
                    var npcTarget = _world.NpcAt(new WorldPos(p.Pos.Map, sx, sy));
                    if (npcTarget is not null && npcTarget.IsAlive) { CastStateOnNpc(session, p, npcTarget, spell); return; }
                }
                if (target is null && (spell.IsCurePoison || spell.IsRemoveParalysis || spell.IsSanacion)) target = p;   // soporte → self
                // "Resucitar familiar o mascota" (#79) entra por acá. En el original NO tiene efecto —el
                // comentario de la fuente es literal: 'Solo damos la animacion xd'— pero la rama SÍ corre y da
                // el hechizo por lanzado (modHechizos.bas:1066), que es lo que emite las palabras mágicas, el
                // sonido y el aviso, y lo que cobra los 600 de maná y los 120 de stamina. Con `autolanzar=1`
                // el tile casteado es el propio, así que el `If UserIndex = targetIndex` del original se cumple
                // siempre; si aun así hubiera OTRO parado ahí, el original no lo daría por lanzado.
                if (spell.EsResucitaFamiliar && target?.CharIndex != p.CharIndex) return;
                // Y ACÁ el agregado nuestro: si el que lo tira tiene el familiar muerto, lo revive. Va después
                // del guard para no cambiar nada de lo que emite el original en el caso sin familiar.
                if (spell.EsResucitaFamiliar) RevivirFamiliarSiHay(session, p);
                if (target is null) { session.Send(ServerMessages.LocaleMsg(SinObjetivo(spell))); return; }
                // El gate va ANTES de consumir maná (VB6: los `Exit Sub` de PuedeAtacar están arriba de todo).
                if (ofensivo && target.CharIndex != p.CharIndex && !PuedeAtacar(session, p, target)) return;

                SpellCasted(session, p, spell);
                if (ofensivo && target.CharIndex != p.CharIndex) FlagCriminalIfLegalTarget(session, p, target);   // agredir a un legal → criminal
                // Los efectos se ACUMULAN y se aplican con `If` independientes, en el orden del original: ver
                // AplicarEfectosDeEstado. Antes esto era una cadena else-if acá adentro Y otra afuera, por tipo
                // de hechizo, y entre las dos dejaban cinco hechizos del install a medias.
                var efectos = AplicarEfectosDeEstado(p, target, spell);

                EmitSpellFx(p, target.Pos, target.CharIndex, spell);
                // El aviso al LANZADOR. El original (InfoHechizo, modHechizos.bas:1762-1765) tiene dos variantes
                // para un hechizo SIN daño: 452 "has lanzado #1 sobre #2" cuando el objetivo es otro, y 0 "has
                // lanzado #1 sobre ti" cuando es uno mismo. Acá salía un ConsoleMsg con texto nuestro, y el 453
                // que le llega al objetivo quedaba sin su par — el lanzador veía otra frase que el que la recibe.
                // El `^` y el `=` no son adorno: le dicen al cliente que resuelva el NOMBRE del hechizo por id.
                session.Send(target.CharIndex != p.CharIndex
                    ? ServerMessages.LocaleMsg(452, $"^{spell.Index}%{target.Name}")
                    : ServerMessages.LocaleMsg(0, $"={spell.Index}"));
                EmitirCostoDelHechizo(session, p);
                if (target.CharIndex != p.CharIndex)
                    target.Connection?.Send(ServerMessages.LocaleMsg(453, $"{p.Name}%^{spell.Index}"));
            }
            else if (spell.Index == HechizoLlamadoAlFamiliar)
            {
                // "Llamado al familiar": trae o retira. El subsistema entero es un agregado nuestro
                // (PacketDispatcher.Familiar.cs); el hechizo existe en el .dat del original y cae en el vacío
                // porque su `Tipo = 6` (uFamiliar) no tiene `Case` que lo atienda en ningún handler.
                LlamarORetirarFamiliar(session, p, spell);
            }
            else
            {
                // Hechizos sin efecto modelado: metamorfosis (#38-50), materializar comida/bebida (#55/#60),
                // arma mágica (#91), detectar invisibilidad (#62), desencantar (#22)... Necesitan subsistemas que
                // esta variante no tiene (o son código muerto en el VB6). Documentados en fase5/GAPS-CODIGO-MUERTO.md.
                //
                // NO se llama SpellCasted: el original tampoco cobra nada acá. En `HandleHechizoTerreno`
                // (modHechizos.bas:704-724) y `HandleHechizoUsuario` (:766-775) estas ramas tienen la llamada al
                // efecto COMENTADA, así que `HechizoCasteado` queda False y el bloque que sigue —el que hace
                // `SubirSkill(magia)` y descuenta maná y stamina (:727-748)— no corre.
                // Acá antes se llamaba SpellCasted "para que el gate de maná limite el spam", y era la canilla de
                // castear al aire entrando por la puerta de atrás: el maná no es un costo real (se recupera gratis
                // meditando, que no cuesta nada), es un delay — y la SKILL subía igual, por un hechizo que no hace
                // absolutamente nada.
                session.Send(ServerMessages.ConsoleMsg($"El hechizo {spell.Name} aun no esta implementado.", 0));
            }
            // Piso de maná (VB6 HechizoCasteado: If .Stats.MinMAN < 0 Then .Stats.MinMAN = 0). Un GM saltea el gate
            // de recursos, así que sin este clamp su maná quedaba negativo y se transmitía como short negativo.
            if (p.Mana < 0) p.Mana = 0;
        }
    }

    private void HandleChangeHeading(ClientSession session, ByteQueueReader r)
    {
        byte heading = r.ReadByte();
        _loop.Enqueue(() =>
        {
            var p = session.Player;
            if (p is null) return;
            // El original valida el rango y, fuera de él, NO emite nada ni toca el estado (Protocol.bas:3233:
            // `If heading > 0 And heading < 5 Then`). Acá se aceptaba cualquier byte, se guardaba y se
            // difundía.
            if (heading is < 1 or > 4) return;
            p.Heading = heading;
            // GIRAR manda `CharacterChangeSlot` (id 30) con el slot 3 = heading — cinco bytes—, no el
            // `CharacterChange` (id 29) entero de diecisiete (Protocol.bas:3237,
            // `PrepareMessageCharacterChangeSlot(.Char.CharIndex, .Char.heading, 3)`). No es sólo tamaño: son
            // dos IDS DISTINTOS con cuerpos de largo distinto, así que un decoder fiel al original se
            // desincroniza — el protocolo no lleva length-prefix. El emisor ya existía y se usaba al equipar;
            // lo que le faltaba era justamente el slot 3.
            //
            // VB6 usa SendData(ToPCArea), que INCLUYE al actor (no ToPCAreaButIndex): por eso no se excluye.
            _world.BroadcastToObservers(p.Pos, ServerMessages.CharacterChangeSlot(p.CharIndex, heading, 3));
        });
    }

    private void HandleMeditate(ClientSession session)
    {
        _loop.Enqueue(() =>
        {
            var p = session.Player;
            if (p is null || !p.IsAlive) return;
            p.Meditating = !p.Meditating;
            session.Send(ServerMessages.MeditateToggle());          // client toggles the meditation state
            short part = (short)ParticleToLevel(p);
            // Matches the VB6 oracle: ON = persistent aura particle (no text); OFF = clear it + LocaleMsg(123).
            if (p.Meditating)
            {
                _world.BroadcastToObservers(p.Pos, ServerMessages.EfectoCharParticula(p.CharIndex, part, -1f, false));
                // Acá había un RaiseSkill(SkillMeditar) con el comentario "VB6: meditar sube la skill Meditar".
                // Es falso: de los 20 `Call SubirSkill` del original NINGUNO es eSkill.Meditar — la skill sólo se
                // LEE, para calcular el maná que recupera (Trabajo.bas:2061) y el aguante al daño
                // (SistemaCombate.bas:595). Subirla acá era una canilla: apretar la tecla de meditar daba skill y
                // experiencia gratis, sin costo ni cadencia. Lo destapó el barrido del oráculo, y sólo a veces,
                // porque RaiseSkill es probabilístico.
            }
            else
            {
                _world.BroadcastToObservers(p.Pos, ServerMessages.EfectoCharParticula(p.CharIndex, part, 0f, true));
                session.Send(ServerMessages.LocaleMsg(123));        // "Dejas de meditar." (client-localized)
            }
            // Faster mana regen while meditating is applied by the regeneration tick (Program.cs).
        });
    }

    /// <summary>Meditation-aura particle id by level/faction (ParticleToLevel, Modulo_UsUaRiOs.bas:2286). Exact
    /// for levels 1-33/50/GM; for 34-49 the VB6 picks by its 6-value faction enum which the slice models as
    /// citizen/criminal only, so those two levels' particle is approximated (citizen→Ciudadano, criminal→Caos).</summary>
    private static int ParticleToLevel(Player p)
    {
        if (p.IsGm) return 128;
        int lvl = p.Level;
        if (lvl < 13) return 42;
        if (lvl < 25) return 81;
        if (lvl < 34) return 41;
        if (lvl < 44) return p.IsCitizen ? 40 : 37;
        if (lvl < 50) return p.IsCitizen ? 113 : 108;
        return lvl == 50 ? 107 : 0;
    }

    /// <summary>Segundos de la cuenta regresiva de salida (VB6 <c>IntervaloCerrarConexion</c>, Server.ini = 5).</summary>
    public const int QuitCountdownSeconds = 5;

    /// <summary>/SALIR (VB6 HandleQuit → <c>Cerrar_Usuario</c>, Modulo_UsUaRiOs.bas:1849). Antes esto era
    /// <c>session.Close("quit")</c>: cerrar el cliente era una salida INSTANTÁNEA, así que escapar de una pelea
    /// perdida salía gratis. El original no cierra de una — arranca una cuenta regresiva:
    /// <list type="number">
    /// <item>si ya hay una salida en curso, este segundo /SALIR la <b>CANCELA</b> (<c>CancelExit</c>).</item>
    /// <item>paralizado no se puede salir: <c>LocaleMsg(119)</c> y no pasa nada.</item>
    /// <item>arranca la salida (<c>EjecutarAccion(1)</c>) y, si estaba oculto o invisible, lo <b>REVELA</b> —
    ///   otra puerta de escape que el original cierra: no te vas invisible.</item>
    /// <item>y acá está el corazón: <b>si el mapa NO es Pk, o está muerto, o es GM, la salida es inmediata</b>
    ///   (Disconnect + cerrar). La espera existe SÓLO donde te pueden matar, que es de lo que protege.</item>
    /// </list></summary>
    private void HandleQuit(ClientSession session)
    {
        _loop.Enqueue(() =>
        {
            var p = session.Player;
            if (p is null) { session.Close("quit"); return; }

            if (p.Saliendo)   // VB6 Cerrar_Usuario:1855 — el segundo /SALIR cancela
            {
                p.Saliendo = false; p.SalirSegundos = 0;
                session.Send(ServerMessages.EjecutarAccion(1));
                session.Send(ServerMessages.LocaleMsg(365, "", 1));
                return;
            }
            if (p.IsParalyzed(_loop.Tick)) { session.Send(ServerMessages.LocaleMsg(119)); return; }

            session.Send(ServerMessages.EjecutarAccion(1));   // el cliente muestra el cartel de salida
            if (p.Invisible || p.Oculto)
            {
                p.Invisible = false; p.Oculto = false;
                p.InvisibleUntilTick = 0; p.OcultoUntilTick = 0;
                session.Send(ServerMessages.LocaleMsg(307));
                _world.BroadcastToObservers(p.Pos, ServerMessages.SetInvisible(p.CharIndex, false));
            }
            // Salida inmediata donde no hay riesgo: ciudad (mapa no-Pk), muerto o GM.
            if (!_data.IsPkMap(p.Pos.Map) || !p.IsAlive || p.IsGm)
            {
                session.Send(ServerMessages.Disconnect());
                session.Close("quit");
                return;
            }
            p.Saliendo = true;
            p.SalirSegundos = QuitCountdownSeconds;
            session.Send(ServerMessages.LocaleMsg(203, p.SalirSegundos.ToString()));
        });
    }

    private void HandleTalk(ClientSession session, ByteQueueReader r)
    {
        string chat = r.ReadAsciiString();
        byte talkMode = r.ReadByte();
        if (Frenado(session, ruidosa: true)) return;
        _loop.Enqueue(() =>
        {
            var p = session.Player;
            if (p is null) return;
            // Tope de largo del original (Protocol.bas:1380): 80 caracteres o mas y NO se dice nada, se avisa
            // con el 238. El gate va antes de todo, incluso de romper el ocultamiento.
            if (chat.Length >= 80) { session.Send(ServerMessages.LocaleMsg(238)); return; }
            Console.WriteLine($"[{session.RemoteEndpoint}] {p.Name}: {chat}");
            // HABLAR ROMPE EL OCULTAMIENTO (VB6 HandleTalk, Protocol.bas:1385-1391: `If .flags.Oculto > 0 Then
            // .flags.Oculto = 0 … SetInvisible(False) + WriteLocaleMsg(307)`). Éste es EL call site que la cita
            // del `RevealHidden` de HandleWalk señalaba — y estaba en el handler equivocado: el port revelaba
            // al caminar, que el original no hace, y NO revelaba al hablar, que sí. Quedó al descubierto al
            // sacar el gate inventado.
            RevealHidden(p);
            // Player chat renders OVER THE HEAD (ChatOverHead), not in the console — matches the VB6
            // oracle (HandleTalk -> ChatOverHead ToPCArea). Broadcast to the area incl. self.
            _world.BroadcastToObservers(p.Pos, ServerMessages.ChatOverHead(chat, p.CharIndex, talkMode));
            _world.Events.Publish(new PlayerTalked(p, chat));
        });
    }

    private void HandleWhisper(ClientSession session, ByteQueueReader r)
    {
        string targetName = r.ReadAsciiString();
        string chat = r.ReadAsciiString();
        if (Frenado(session, ruidosa: true)) return;
        _loop.Enqueue(() =>
        {
            var p = session.Player;
            if (p is null) return;
            var target = _world.PlayerByName(targetName);
            // El único rechazo del original es que no esté conectado. El gate de VIVO era invención nuestra: a
            // un muerto se le susurra sin problema.
            if (target is null) { session.Send(ServerMessages.LocaleMsg(75)); return; }
            // `If UserIndex <> targetUserIndex`: susurrarse a uno mismo no hace absolutamente nada, en silencio.
            if (target.CharIndex == p.CharIndex) return;
            // La MISMA línea para los dos, con FONTTYPE_INFOBOLD2. Acá salían dos frases inventadas ("te
            // susurra" / "le susurras") y con la fuente de info, o sea otro color.
            var linea = ServerMessages.ConsoleMsg($"[{p.Name}] {chat}", FuenteSusurro);
            if (World.InView(p.Pos, target.Pos))   // VB6 EstaPCarea
            {
                // El `If LenB(chat) <> 0` del original cubre SÓLO esta rama: un susurro vacío a alguien que está
                // lejos sí emite las dos líneas, y de cerca no emite nada. La asimetría es de la fuente.
                if (chat.Length == 0) return;
                // De cerca el susurro además se DIBUJA sobre la cabeza (modo 4) a los dos, y se le copia a los
                // administradores. El port no mandaba ningún ChatOverHead: un susurro no se veía en pantalla.
                BroadcastToAdmins(ServerMessages.ChatOverHead(chat, p.CharIndex, 4));
                session.Send(linea);
                target.Connection?.Send(linea);
                session.Send(ServerMessages.ChatOverHead(chat, p.CharIndex, 4));
                target.Connection?.Send(ServerMessages.ChatOverHead(chat, p.CharIndex, 4));
                return;
            }
            session.Send(linea);
            target.Connection?.Send(linea);
        });
    }

    private void HandlePing(ClientSession session, ByteQueueReader r)
    {
        int time = r.ReadLong();
        session.Send(ServerMessages.Pong(time)); // echo immediately; no game state involved
    }

    private void HandleRequestPositionUpdate(ClientSession session)
    {
        _loop.Enqueue(() =>
        {
            var p = session.Player;
            if (p is not null) session.Send(ServerMessages.PosUpdate(p.Pos.X, p.Pos.Y));
        });
    }

    /// <summary>VB6 <c>AllMascotasAtacanUser</c> (SistemaCombate.bas:1482): las mascotas de <paramref name="amo"/>
    /// salen a pelear contra <paramref name="victima"/>. El original las manda por <c>AttackedBy</c> +
    /// <c>NPCDEFENSA</c> + <c>Hostile = 1</c>, que es exactamente la rama de IA que el port ya tenía; lo único
    /// que faltaba era que alguien la encendiera.</summary>
    private void MascotasAtacanAlUsuario(Player victima, Player amo)
    {
        foreach (var mascota in _world.Npcs)
        {
            if (mascota.OwnerCharIndex != amo.CharIndex || !mascota.IsAlive) continue;
            mascota.AttackedBy = victima.Name;
            mascota.Movement = GameLimits.TipoAiNpcDefensa;
            mascota.Hostile = true;
        }
    }

    /// <summary>El precio que ve y paga ESTE comprador — VB6 <c>Descuento</c> (Comercio.bas:244):
    /// <c>Valor / (1 + Comerciar/100)</c>, o sea que con la skill al máximo se paga la mitad. Vale para el
    /// cobro (Comercio.bas:89) y para la vidriera (Comercio.bas:268): el original muestra el precio YA con el
    /// descuento, así que mostrar el de lista y cobrar otro no es una opción.</summary>
    private static long PrecioParaElComprador(Player? p, int precioDeLista)
    {
        if (p is null) return precioDeLista;
        double desc = 1 + p.Skills[GameLimits.SkillComerciar] / 100.0;
        return Math.Max(1, (long)(precioDeLista / desc + 0.5));   // VB6 CLng(... + 0.5)
    }

    /// <summary><c>SND_ORO2</c> (Declares.bas:558): el tintineo que el original tira al área al soltar oro.</summary>
    private const short SndOro = 172;

    /// <summary>VB6 <c>TirarOro</c> (InvUsuario.bas:154): el oro va al piso como un objeto <c>iORO</c>. El
    /// original tira montones de a <c>MAX_INVENTORY_OBJS</c> y busca un tile libre alrededor
    /// (<c>TirarItemAlPiso</c>); acá se tira UN montón —el tope de una tirada ya lo pone el gate de 100.000 del
    /// handler— en el tile del jugador o, si está ocupado, en uno pegado.</summary>
    private void TirarOro(ClientSession session, Player p, long cantidad)
    {
        if (cantidad <= 0 || cantidad > p.Gold) return;       // VB6: `If (Cantidad > 0) And (Cantidad <= .Stats.GLD)`
        short monton = (short)Math.Min(cantidad, GameLimits.MaxInventoryObjs);
        var donde = p.Pos;
        if (_world.TryGetGround(donde, out _))
        {
            donde = default;
            foreach (var (dx, dy) in new[] { (0, -1), (1, 0), (0, 1), (-1, 0) })
            {
                var t = new WorldPos(p.Pos.Map, (byte)(p.Pos.X + dx), (byte)(p.Pos.Y + dy));
                if (!_world.TryGetGround(t, out _) && PosLegal(t, p.Navigating)) { donde = t; break; }
            }
            if (donde.Map == 0) { session.Send(ServerMessages.LocaleMsg(262)); return; }
        }
        p.Gold -= monton;
        _world.SetGround(donde, (short)GameLimits.ObjOro, monton);
        _world.BroadcastToObservers(donde, ServerMessages.ObjectCreate(donde.X, donde.Y, (short)GameLimits.ObjOro, monton));
    }

    private void HandleDrop(ClientSession session, ByteQueueReader r)
    {
        byte slot = r.ReadByte();
        long amount = r.ReadLong();
        _loop.Enqueue(() =>
        {
            var p = session.Player;
            if (p is null || slot < 1) return;
            if (DeadCheck(session, p)) return;   // VB6 HandleDrop: un muerto no puede tirar objetos al suelo
            // EL ORO VIAJA POR UN SLOT QUE NO EXISTE, a propósito: `FLAGORO = MAX_INVENTORY_SLOTS + 1`
            // (Declares.bas:576). El port rechazaba todo slot > 25 antes de mirar nada, así que la rama del
            // oro era inalcanzable y no se podía tirar una moneda al piso. VB6 Protocol.bas:2025.
            if (slot == GameLimits.MaxInventorySlots + 1)
            {
                // VB6 Protocol.bas:2025-2031. El ORDEN y la INCONDICIONALIDAD son del original: primero corta
                // por el tope de 100.000, después tira, y el SONIDO al área y el UpdateGold al que tiró salen
                // IGUAL aunque no haya caído una moneda (TirarOro se guarda sus propios gates adentro).
                if (amount > 100_000) return;   // "Don't drop too much gold"
                TirarOro(session, p, amount);
                _world.BroadcastToObservers(p.Pos, ServerMessages.PlayWave(SndOro, p.Pos.X, p.Pos.Y));
                session.Send(ServerMessages.UpdateGold(p.Gold));
                return;
            }
            if (slot > GameLimits.MaxInventorySlots) return;
            var inv = p.Inventory[slot];
            if (inv.IsEmpty) return;
            // `Destruir=1` (obj.dat): el objeto NO se tira al piso. El original corta acá y en vez del DropObj
            // manda un ShowMessageBox (Protocol.bas:2057). Lo traen 5 ítems —los sacos de créditos, el de tiempo
            // de donador y el Anillo Dorado—, y son justamente los que uno no quiere perder por un click.
            if (_data.Obj(inv.ObjIndex) is { NoSeTira: true })
            { session.Send(ServerMessages.ShowMessageBox("", esPregunta: true, accion: 1)); return; }
            // UN NOVATO NO TIRA SU EQUIPO INICIAL (VB6 InvUsuario.bas:440: `If ItemNewbie(...) And Privilegios
            // And PlayerType.User` → LocaleMsg 260 y Exit Sub). El gate es sólo para el jugador común: un GM sí
            // puede. Estaba muerto porque la clave `Newbie` no existe en esta data; ver ObjInfo.IsNewbie.
            if (!p.IsGm && _data.Obj(inv.ObjIndex) is { IsNewbie: true })
            { session.Send(ServerMessages.LocaleMsg(260)); return; }
            if (_world.TryGetGround(p.Pos, out _)) { session.Send(ServerMessages.LocaleMsg(262)); return; }

            short drop = (short)Math.Clamp(amount, 1, inv.Amount);
            _world.SetGround(p.Pos, inv.ObjIndex, drop);
            _world.BroadcastToObservers(p.Pos, ServerMessages.ObjectCreate(p.Pos.X, p.Pos.Y, inv.ObjIndex, drop));

            DesequiparSiSeVaEntero(session, p, slot, drop);
            inv = p.Inventory[slot];   // el desequipado pudo tocar el slot: releerlo antes de descontar
            inv.Amount -= drop;
            if (inv.Amount <= 0) inv = default;
            p.Inventory[slot] = inv;
            session.Send(ServerMessages.ChangeInventorySlot(slot, inv.ObjIndex, inv.Amount, inv.Equipped, 0f, (byte)(inv.IsEmpty ? 0 : 1)));
            Console.WriteLine($"[{session.RemoteEndpoint}] {p.Name} tira {drop} en ({p.Pos.X},{p.Pos.Y})");
        });
    }

    private void HandlePickUp(ClientSession session)
    {
        _loop.Enqueue(() =>
        {
            var p = session.Player;
            if (p is null) return;
            if (DeadCheck(session, p)) return;   // VB6 HandlePickUp: Protocol.bas:1751
            if (!_world.TryGetGround(p.Pos, out var ground)) { session.Send(ServerMessages.LocaleMsg(261)); return; }  // VB6 GetObj
            // VB6 InvUsuario.bas:619 (`If ObjData(...).Agarrable <> 1 Then`): el decorado del mundo NO se levanta.
            // El sentido del dato está INVERTIDO — `Agarrable = 1` significa fijo. De los 176 objetos del mapa 1,
            // 175 son Agarrable=1 (árboles, puertas, carteles); sin este gate un `g` se lleva un árbol al bolso.
            // El VB6 no manda mensaje: simplemente no lo agarra.
            if (_data.Obj(ground.ObjIndex) is { IsPickable: false }) return;
            int slot = FindSlotFor(p, ground.ObjIndex, ground.Amount);
            if (slot == 0) { session.Send(ServerMessages.LocaleMsg(25)); return; }

            var inv = p.Inventory[slot];
            inv.ObjIndex = ground.ObjIndex;
            // El `Math.Min` queda de red de contención y ya no debería recortar nada: `FindSlotFor` recibe la
            // cantidad y sólo devuelve un slot donde entre (VB6 InvUsuario.bas:548). Antes apilaba a ciegas y
            // este recorte se comía el excedente sin avisar.
            inv.Amount = (short)Math.Min(GameLimits.MaxInventoryObjs, inv.Amount + ground.Amount);
            p.Inventory[slot] = inv;
            _world.RemoveGround(p.Pos);
            _world.BroadcastToObservers(p.Pos, ServerMessages.ObjectDelete(p.Pos.X, p.Pos.Y));
            session.Send(ServerMessages.ChangeInventorySlot((byte)slot, inv.ObjIndex, inv.Amount, inv.Equipped, 0f, 1));
            Console.WriteLine($"[{session.RemoteEndpoint}] {p.Name} agarra obj {ground.ObjIndex} en slot {slot}");
        });
    }

    private void HandleUseItem(ClientSession session, ByteQueueReader r)
    {
        byte slot = r.ReadByte();
        _loop.Enqueue(() =>
        {
            var p = session.Player;
            if (p is null || slot < 1 || slot > GameLimits.MaxInventorySlots) return;
            var inv = p.Inventory[slot];
            var obj = inv.IsEmpty ? null : _data.Obj(inv.ObjIndex);
            // El BARCO es la única excepción al gate de muerte: el VB6 (DoNavega) contempla embarcar/desembarcar
            // estando muerto — el cadáver navega como fragata fantasmal (body 87).
            if (obj is not null && obj.IsBoat) { DoNavega(session, p, slot, obj); return; }
            if (DeadCheck(session, p)) return;   // VB6 UseInvItem: DeadCheck bloquea comida/pociones/etc. estando muerto
            if (inv.IsEmpty) return;
            if (obj is null) { session.Send(ServerMessages.ConsoleMsg("No puedes usar ese objeto.", FuenteInfo)); return; }

            // Usar un ARCO equipado pone al cliente en modo targeting (VB6 UseInvItem/otWeapon, InvUsuario.bas:1476):
            // Proyectil=1 → WorkRequestTarget(Proyectiles). Gates: stamina > 0 (93), arco equipado (391) y modo
            // combate (102, SeguroCheck). Después el cliente clickea un tile y manda WorkLeftClick(X,Y,Proyectiles).
            if (obj.IsBow)
            {
                if (p.Stamina <= 0) { session.Send(ServerMessages.LocaleMsg(93)); return; }
                if (!inv.Equipped) { session.Send(ServerMessages.LocaleMsg(391)); return; }
                if (!p.CombatMode) { session.Send(ServerMessages.LocaleMsg(102)); return; }
                session.Send(ServerMessages.WorkRequestTarget(GameLimits.SkillProyectiles));
                return;
            }

            // ARMA CUERPO A CUERPO (el `Case Else` del Select por Proyectil, InvUsuario.bas:1498). Sólo hace UNA
            // cosa: la DAGA sobre un montón de LEÑA clickeado lo parte en fogatas apagadas. Con cualquier otro
            // objetivo el original manda el 422 ("Necesitas clickear sobre leña para hacer ramitas"), aunque el
            // arma sea una espada — es ruidoso, pero es lo que manda.
            if (obj.ObjType == 2)
            {
                if (p.TargetObj != Lenia) { session.Send(ServerMessages.LocaleMsg(422)); return; }
                if (inv.ObjIndex != Daga) return;                       // otra arma sobre leña: silencio
                if (!inv.Equipped) { session.Send(ServerMessages.LocaleMsg(391)); return; }
                TratarDeHacerFogata(session, p);
                return;
            }

            // LLAVE (otLlaves=9) sobre la puerta que se clickeó (VB6 InvUsuario.bas:1725-1766). Faltaba ENTERO:
            // la data trae 112 puertas con `llave=1` y 108 llaves, emparejadas por `clave`, y ninguna se podía
            // abrir — el doble-clic las rebota con el 172 y no había otro camino.
            //
            // Las cuatro combinaciones del original, y ojo con los nombres:
            //   * `Closed` sale de la clave `abierta` del .dat, que el VB6 carga en su campo `Cerrada`
            //     (FileIO.bas:981). abierta=1 ⇒ ESTÁ CERRADA. Leerlo al revés abre lo que hay que cerrar.
            //   * `Llave > 0` distingue "cerrada con llave" de "cerrada nomás": con llave la llave correcta la
            //     ABRE (pasa a IndexCerrada, que es el par sin trabar); sin llave, la llave correcta la TRABA
            //     (pasa a IndexCerradaLlave). Es un toggle de cerradura, no de puerta.
            if (obj.IsKey)
            {
                if (p.TargetObj <= 0) return;                       // sin objeto clickeado el VB6 no hace nada
                var puerta = _data.Obj(p.TargetObj);
                if (puerta is null || puerta.ObjType != 6) return;   // otPuertas
                // Puerta YA ABIERTA. El original manda el 476, cuyo texto es "¡Está cerrada!" — la frase no
                // corresponde a la rama, pero es lo que manda (InvUsuario.bas:1762) y se porta tal cual.
                if (puerta.Closed != 1) { session.Send(ServerMessages.LocaleMsg(476)); return; }
                bool coincide = puerta.Clave != 0 && puerta.Clave == obj.Clave;
                if (puerta.Llave > 0)
                {
                    if (!coincide) { session.Send(ServerMessages.LocaleMsg(473)); return; }         // "¡La llave no sirve!"
                    if (!CambiarPuerta(p, puerta.IndexCerrada)) return;
                    session.Send(ServerMessages.LocaleMsg(472));                                    // "¡Has abierto la puerta!"
                }
                else
                {
                    if (!coincide) { session.Send(ServerMessages.LocaleMsg(475)); return; }         // "¡La llave no sirve!"
                    if (!CambiarPuerta(p, puerta.IndexCerradaLlave)) return;
                    session.Send(ServerMessages.LocaleMsg(474));                                    // "¡Has cerrado con llave la puerta!"
                }
                return;
            }

            // RUNA (otRuna = 38, VB6 InvUsuario.bas:1356-1372). La data trae UNA: la "Runa de teletransportación".
            // Te devuelve a la ciudad-hogar, y las tres ramas del original son distintas entre sí:
            //   * muerto → al tile DEAD de la ciudad, el mismo donde te deja RegresarHogar
            //   * vivo, y estás parado en un tile de ARENA (Trigger 6) o en un mapa NO Pk → al tile normal
            //   * vivo en cualquier otro lado → NO viaja y avisa con el 468
            // OJO que el trigger 6 no es "zona segura" (esa es la 4): es el de las arenas. La condición se lee
            // tal cual está escrita en el VB6 (`Trigger = 6 Or MapInfo(...).Pk = False`), sin interpretarla.
            //
            // Acá la Runa no hacía NADA: caía al "No puedes usar ese objeto" del final.
            if (obj.ObjType == Runa)
            {
                if (!p.IsAlive) { WarpAlHogar(session, p, Cities.DeadTile(p.HomeCity)); return; }
                bool puedeVolver = _data.IsArena(p.Pos.Map, p.Pos.X, p.Pos.Y) || !_data.IsPkMap(p.Pos.Map);
                if (!puedeVolver) { session.Send(ServerMessages.LocaleMsg(468)); return; }
                WarpAlHogar(session, p, Cities.SpawnTile(p.HomeCity));
                return;
            }

            // EL MINERAL CRUDO PIDE OBJETIVO (VB6 `Case eOBJType.otMinerales` de UsarInvItem,
            // InvUsuario.bas:1830): usar un mineral no funde nada por sí solo, contesta
            // `WriteWorkRequestTarget(FundirMetal)` y se anota la RANURA en `flags.Lingoteando`. El clic que
            // viene después —sobre la fragua— es el que funde.
            //
            // Faltaba entero: acá el mineral caía en el "No puedes usar ese objeto" del final, y el tester lo
            // reportó como que la fragua no anda. Medido contra el server: `usar_mineral` devolvía ese texto.
            if (obj.IsMineral)
            {
                if (!p.IsAlive) { session.Send(ServerMessages.LocaleMsg(77)); return; }   // VB6 DeadCheck
                p.Lingoteando = slot;
                session.Send(ServerMessages.WorkRequestTarget(GameLimits.SkillFundir));
                return;
            }

            // LA HERRAMIENTA QUE ABRE SU TALLER (VB6 `Case eOBJType.otAnillo` de UsarInvItem, InvUsuario.bas:1515).
            // El tester lo reportó como "las herramientas caen en No puedes usar ese objeto" (2.33), y tenía
            // razón a medias: son TRES las que hacen algo, y lo que hacen es abrir su ventana de fabricación.
            //
            //   costurero (886) → sastrería     olla (887) → alquimia     serrucho (198) → carpintería
            //
            // Las otras herramientas del slot —piquete, hacha, caña, red, tijeras— caen en el `Select Case` sin
            // rama y el original NO contesta nada: se usan trabajando, no apretándoles la U. Y el yunque tampoco
            // está acá: la fragua se abre por el mundo, con doble clic, que es lo que el tester vio.
            //
            // EL GATE ES DOBLE Y NO ES REDUNDANTE. El original pide que el ítem del slot esté equipado (el `If
            // Equipped <> 0` de afuera) Y ADEMÁS que el ítem EQUIPADO en el slot de herramienta sea ese mismo
            // (`.Invent.AnilloEqpObjIndex = COSTURERO`); si no, LocaleMsg 391. Con un solo costurero en el
            // inventario da igual, pero con dos —uno puesto y otro no— la diferencia se ve.
            if (obj.ObjType == Herramienta)
            {
                if (!inv.Equipped) return;                                       // `If Equipped <> 0`: mudo
                string? taller = obj.Index switch
                {
                    886 => "tailor",       // COSTURERO   (Declares.bas:374)
                    887 => "alchemy",      // OLLA        (Declares.bas:372)
                    198 => "carpenter",    // SERRUCHO_CARPINTERO (Declares.bas:371)
                    _ => null,
                };
                if (taller is null) return;                                      // el resto del Select: silencio
                if (Equipment.EquippedOfCategory(p, _data, 6)?.Index != obj.Index)
                { session.Send(ServerMessages.LocaleMsg(391)); return; }
                SendCraftList(session, taller, obj.Name);
                return;
            }

            // EL PASAJE DEL BARCO (otPasajes = 36, VB6 InvUsuario.bas:1381). Es el viaje rápido del original y
            // caía en "No puedes usar ese objeto": el tester lo reportó (2.25). No abre ninguna ventana — se
            // clickea al PIRATA, se usa el pasaje, y te lleva.
            //
            // Los cinco gates del original, en su orden, y con dos mensajes distintos que conviene no mezclar:
            // el 22 ("primero hacé clic sobre el personaje") sale TANTO sin nadie clickeado COMO con alguien que
            // no es pirata, y el 30 ("el marino se muestra enojado…") sale cuando el pasaje no es de este puerto
            // o su destino no existe. La distancia es la Manhattan del original, no Chebyshev.
            if (obj.ObjType == Pasaje)
            {
                if (!p.IsAlive || p.Meditating) return;                                        // DeadCheck + meditando: mudo
                var pirata = TargetNpcDe(p);
                if (pirata is null || !pirata.IsTransportador) { session.Send(ServerMessages.LocaleMsg(22)); return; }
                if (DistanciaVB6(pirata.Pos, p.Pos) > 3) { session.Send(ServerMessages.LocaleMsg(8)); return; }
                if (p.Pos.Map != obj.PasajeDesdeMap) { session.Send(ServerMessages.LocaleMsg(30)); return; }
                if (!_data.MapExists(obj.PasajeHastaMap)) { session.Send(ServerMessages.LocaleMsg(30)); return; }

                WarpAlHogar(session, p, new WorldPos((short)obj.PasajeHastaMap,
                                                     (byte)obj.PasajeHastaX, (byte)obj.PasajeHastaY));
                session.Send(ServerMessages.LocaleMsg(31));                                    // "viajaste varios días, estás exhausto"
                // EL VIAJE TE DEJA VACÍO: hambre y sed a CERO, no al máximo (`MinAGU = 0 : MinHam = 0`). Y
                // devuelve fuerza y agilidad a su valor base, o sea que te corta los buffs — el original usa el
                // viaje como reset de estado, y las dos cosas son deliberadas.
                p.Thirst = 0; p.Hunger = 0;
                session.Send(ServerMessages.UpdateHungerAndThirst((byte)p.Thirst, (byte)p.Hunger));
                for (int i = 1; i < p.AttrBonus.Length; i++) { p.AttrBonus[i] = 0; p.AttrBonusExpiry[i] = 0; }
                session.Send(ServerMessages.UpdateStrenght(p.Strength));
                session.Send(ServerMessages.UpdateDexterity(p.Agility));
                inv.Amount -= 1;
                if (inv.Amount <= 0) inv = default;
                p.Inventory[slot] = inv;
                session.Send(ServerMessages.ChangeInventorySlot((byte)slot, inv.ObjIndex, inv.Amount, inv.Equipped, 0f, inv.IsEmpty ? (byte)0 : (byte)1));
                return;
            }

            // Pergamino (otPergaminos=24): un caster aprende su hechizo (VB6 InvUsuario.bas:1819 → AgregarHechizo,
            // modHechizos.bas:230). Sólo casters (MaxMana > 0, silencio si no). Si ya lo tiene → LocaleMsg 396; sin
            // slot libre → 395; si aprende → ocupa el primer slot vacío, lo refleja (ChangeSpellSlot) y consume el
            // pergamino. Es LA PALANCA de la magia: sin esto el spellset queda congelado en el inicial.
            if (obj.IsPergamino)
            {
                // Sin maná no se aprende, y el original NO calla: manda el 393 (InvUsuario.bas:1827).
                if (p.MaxMana <= 0) { session.Send(ServerMessages.LocaleMsg(393)); return; }
                short hIndex = (short)obj.HechizoIndex;
                if (hIndex <= 0) return;
                for (int i = 1; i <= GameLimits.MaxUserSpells; i++)
                    if (p.Spells[i] == hIndex) { session.Send(ServerMessages.LocaleMsg(396)); return; }   // ya lo tiene
                int freeSlot = 0;
                for (int i = 1; i <= GameLimits.MaxUserSpells; i++)
                    if (p.Spells[i] == 0) { freeSlot = i; break; }
                if (freeSlot == 0) { session.Send(ServerMessages.LocaleMsg(395)); return; }                // sin espacio
                p.Spells[freeSlot] = hIndex;
                session.Send(ServerMessages.ChangeSpellSlot((byte)freeSlot, hIndex));
                inv.Amount -= 1;
                if (inv.Amount <= 0) inv = default;
                p.Inventory[slot] = inv;
                session.Send(ServerMessages.ChangeInventorySlot((byte)slot, inv.ObjIndex, inv.Amount, inv.Equipped, 0f, inv.IsEmpty ? (byte)0 : (byte)1));
                return;
            }

            // Food/drink (otUseOnce/otbebidas): reset hunger/thirst, play the eat/drink sound, and send a
            // COMPACT slot update — byte-faithful to the VB6 oracle (InvUsuario.bas: UpdateHambre/Sed ->
            // PlayWave ToPCArea -> ChangeInventorySlotUser). Order matters.
            if (obj.IsFood || obj.IsDrink)
            {
                if (!p.IsAlive) return;
                short usedObj = inv.ObjIndex;
                short snd;
                byte[] hungerPkt;
                if (obj.IsFood)
                {
                    p.Hunger = (byte)Math.Min(p.MaxHunger, p.Hunger + obj.MinHam);
                    snd = (usedObj == 1 || usedObj == 2 || usedObj == 467) ? (short)82 : (short)7; // MORFAR_MANZANA / SOUND_COMIDA
                    hungerPkt = ServerMessages.UpdateHambre(p.Hunger);
                }
                else
                {
                    p.Thirst = (byte)Math.Min(p.MaxThirst, p.Thirst + obj.MinSed);
                    snd = 135; // SND_BEBER
                    hungerPkt = ServerMessages.UpdateSed(p.Thirst);
                }
                inv.Amount -= 1;
                if (inv.Amount <= 0) inv = default;
                p.Inventory[slot] = inv;
                session.Send(hungerPkt);
                session.Send(ServerMessages.PlayWave(snd, (byte)p.Pos.X, (byte)p.Pos.Y));
                // "Los admin invisibles solo producen sonidos a si mismos" — pero SÓLO en la BEBIDA
                // (InvUsuario.bas:1709). La COMIDA no lleva ese gate (:1444-1449) y suena al área igual estando
                // invisible: la asimetría es de la fuente y se porta tal cual. Acá no se gateaba ninguna de las
                // dos, así que un GM invisible se delataba tomando agua.
                if (!(obj.IsDrink && p.Invisible))
                    _world.BroadcastToObservers(p.Pos, ServerMessages.PlayWave(snd, (byte)p.Pos.X, (byte)p.Pos.Y), p.CharIndex);
                session.Send(ServerMessages.ChangeInventorySlotUser(slot, 3, inv.Amount));
                return;
            }
            // INSTRUMENTOS (otInstrumentos = 26, VB6 InvUsuario.bas:1851): suenan su Snd1 en el área y NO se
            // consumen. Un GM invisible lo escucha sólo él (`If .flags.AdminInvisible = 1`), que es la misma
            // asimetría que el original aplica en todo lo que hace ruido.
            if (obj.IsInstrument)
            {
                if (!p.IsAlive) { session.Send(ServerMessages.LocaleMsg(77)); return; }   // VB6 DeadCheck
                var wave = ServerMessages.PlayWave((short)obj.Snd1, p.Pos.X, p.Pos.Y);
                session.Send(wave);
                if (!p.Invisible) _world.BroadcastToObservers(p.Pos, wave, p.CharIndex);
                return;
            }
            if (!obj.IsPotion) { session.Send(ServerMessages.ConsoleMsg("No puedes usar ese objeto.", FuenteInfo)); return; }

            // CADENCIA PARA TOMAR (VB6 InvUsuario.bas:1559, `Case eOBJType.otPociones`): después de pegar hay
            // que esperar `IntervaloGolpeUsar` (400 ms en este install) antes de poder tomar una poción, y el
            // original avisa con el 469. Es lo que impide encadenar golpe + trago en el mismo instante.
            //
            // El reloj y su función ya estaban —`CombatTiming.PermiteGolpeUsar`, con el `actualizar: false` que
            // usa el original, y con tests propios verdes— y no los llamaba NADIE: `_intervaloGolpeUsar` se leía
            // del ini y no se usaba en ningún camino, así que el gate no existía en el juego.
            if (!Game.CombatTiming.PermiteGolpeUsar(p, Environment.TickCount64, _intervaloGolpeUsar))
            {
                session.Send(ServerMessages.LocaleMsg(469));
                return;
            }

            // Route the effect by SubTipo (InvUsuario.bas): 1=agility, 2=strength, 3=HP, 4=mana, 5=spell.
            const short SND_BEBER = 135;
            int lo = obj.MinModifier > 0 ? obj.MinModifier : obj.MaxModifier;
            int hi = obj.MaxModifier > 0 ? obj.MaxModifier : obj.MinModifier;
            int roll = hi > lo ? _rng.Next(lo, hi + 1) : Math.Max(1, hi);
            string effect;
            byte[]? avisoStat = null;   // el granular del stat que toque esta poción (lo elige cada case)
            switch (obj.SubType)
            {
                case 3: // red potion — restores HP
                    p.Hp = Math.Min(p.MaxHp, p.Hp + roll); effect = $"+{roll} HP -> {p.Hp}/{p.MaxHp}";
                    avisoStat = ServerMessages.UpdateHP((short)p.Hp); break;                 // VB6 InvUsuario.bas:1611
                case 4: // blue potion — restores mana by the AO formula (Porcentaje(MaxMan,4) + ELV\2 + 40/ELV)
                {
                    int restore = Potions.ManaRestore(p.MaxMana, p.Level);
                    p.Mana = Math.Min(p.MaxMana, p.Mana + restore); effect = $"+{restore} mana -> {p.Mana}/{p.MaxMana}";
                    avisoStat = ServerMessages.UpdateMana((short)p.Mana); break;             // VB6 InvUsuario.bas:1625
                }
                case 1: // yellow potion — temporary agility boost (attr 2)
                case 2: // green potion — temporary strength boost (attr 1)
                {
                    int attr = obj.SubType == 1 ? 2 : 1;
                    long durationTicks = Math.Max(1, obj.DurationSeconds) * (1000L / GameLoop.TickMs);
                    p.ApplyAttrBonus(attr, roll, _loop.Tick, durationTicks);
                    effect = $"+{roll} {(attr == 2 ? "agilidad" : "fuerza")} temporal";
                    // El VB6 manda SÓLO el atributo que tocó la poción (InvUsuario.bas:1576 agilidad / :1594
                    // fuerza), a diferencia de la EXPIRACIÓN, que manda los dos.
                    avisoStat = attr == 2 ? ServerMessages.UpdateDexterity(p.Agility)
                                          : ServerMessages.UpdateStrenght(p.Strength); break;
                }
                case 13: // cofre de créditos de donador (VB6 InvUsuario.bas:1683): suma CuantoAumento al crédito.
                {        // No es una bebida: se consume y avisa con LocaleMsg 470, sin sonido de beber ni UpdateUserStats.
                    if (obj.CuantoAumento <= 0) { session.Send(ServerMessages.ConsoleMsg("Ese objeto no tiene efecto.", FuenteInfo)); return; }
                    p.CreditoDonador += obj.CuantoAumento;
                    inv.Amount -= 1;
                    if (inv.Amount <= 0) inv = default;
                    p.Inventory[slot] = inv;
                    session.Send(ServerMessages.ChangeInventorySlot((byte)slot, inv.ObjIndex, inv.Amount, inv.Equipped, 0f, inv.IsEmpty ? (byte)0 : (byte)1));
                    session.Send(ServerMessages.LocaleMsg(470, $"{obj.CuantoAumento}%{p.CreditoDonador}"));   // "¡Has abierto cofre de #1 creditos, ahora tienes #2!"
                    return;
                }
                case 5: // poción que LANZA UN HECHIZO sobre quien la toma (VB6 InvUsuario.bas:1636)
                {
                    // El original pone TargetUser = uno mismo y llama HechizoEstadoUsuario. Si ese Sub no
                    // aplicó nada devuelve Casteo=False y hace `Exit Sub`: la poción NO se consume. Eso es lo
                    // que hace que la de Metamorfosis y las dos de Desencantar sigan sin efecto —ese Sub no
                    // lee esos campos— y es fiel, no una simplificación nuestra.
                    var hechizo = obj.LanzaHechizo > 0 ? _data.Spell(obj.LanzaHechizo) : null;
                    if (hechizo is null) { session.Send(ServerMessages.ConsoleMsg("Esa poción no tiene efecto.", FuenteInfo)); return; }
                    var aplicados = AplicarBuffsDeAtributo(p, hechizo);
                    aplicados.AddRange(AplicarEfectosDeEstado(p, p, hechizo));
                    // SubeHP va al final, como en el original (:1412), y con la misma fórmula que el hechizo.
                    if (hechizo.SubeHp == 1 && hechizo.MaxHp > 0)
                    {
                        int cura = Combat.EfectoHechizoArea3Pct(_rng.Next(hechizo.MinHp, hechizo.MaxHp + 1), p.Level);
                        p.Hp = Math.Min(p.MaxHp, p.Hp + cura);
                        aplicados.Add($"+{cura} HP -> {p.Hp}/{p.MaxHp}");
                        avisoStat = ServerMessages.UpdateHP((short)p.Hp);
                    }
                    if (hechizo.Invisibilidad > 0 && !p.Invisible)
                    {
                        p.InvisibleUntilTick = _loop.Tick + InvisibilityDurationTicks;
                        p.Invisible = true;
                        _world.BroadcastToObservers(p.Pos, ServerMessages.SetInvisible(p.CharIndex, true), p.CharIndex);
                        aplicados.Add("invisible");
                    }
                    if (aplicados.Count == 0)
                    {
                        // Casteo = False. Ni se consume ni se gasta: el frasco sigue en el inventario.
                        session.Send(ServerMessages.ConsoleMsg("Esa poción no te hace nada.", FuenteInfo));
                        return;
                    }
                    EmitSpellFx(p, p.Pos, p.CharIndex, hechizo);
                    effect = string.Join(", ", aplicados);
                    break;
                }
                case 6: // Scroll a Intermundia (VB6 InvUsuario.bas:1644) — objeto 498
                {
                    // El original NO avisa nada cuando la traba está puesta: si TiempoDeMapeo > 0 se saltea el
                    // warp y cae igual al consumo de abajo. O sea que el scroll SE GASTA sin teletransportar.
                    // Se porta tal cual, incluido el silencio: es la misma traba anti-escape que la baldosa de
                    // salida, y avisar acá sería mejorar el original a espaldas de quien conoce el bug.
                    if (p.MapeoBloqueadoHastaTick <= _loop.Tick)
                        TeleportTo(session, p, Cities.Intermundia);
                    effect = "Intermundia";
                    break;
                }
                case 7: // Poción mágica de cambio de cara (VB6 ChangeHead, InvUsuario.bas:1650) — objeto 504
                {
                    if (p.Navigating) { session.Send(ServerMessages.LocaleMsg(20)); return; }
                    if (p.Mounted) { session.Send(ServerMessages.LocaleMsg(21)); return; }
                    // Las DOS cabezas, como el original (:1934-1935). La `OrigChar.Head` no es decoración: es la
                    // cabeza VIVA, la que se restaura al revivir y la que se escribe en el .chr. Tocando sólo
                    // `Char.Head` la cara nueva duraba hasta el primer relogueo o la primera muerte.
                    p.Head = p.OrigHead = CabezaAlAzar(p.Race, p.Gender);
                    // El VB6 manda el GRANULAR (slot 2 = cabeza), no el CharacterChange entero: el que mira sólo
                    // cambia la cabeza del muñeco y le quedan el cuerpo y el arma que ya tenía dibujados.
                    _world.BroadcastToObservers(p.Pos, ServerMessages.CharacterChangeSlot(p.CharIndex, p.Head, 2));
                    _world.BroadcastToObservers(p.Pos, ServerMessages.CreateFX(p.CharIndex, 30, 0));
                    effect = $"cabeza {p.Head}";
                    break;
                }
                case 8: // Poción mágica de cambio de sexo (VB6 DarCuerpoNuevo, InvUsuario.bas:1665) — objeto 505
                {
                    if (p.Navigating) { session.Send(ServerMessages.LocaleMsg(20)); return; }
                    if (p.Mounted) { session.Send(ServerMessages.LocaleMsg(21)); return; }
                    // Desequipar TODO va primero y no es un detalle de higiene: el cuerpo nuevo es el DESNUDO de
                    // la raza en el género nuevo, así que dejar la armadura puesta pintaría un cuerpo vestido con
                    // el gráfico del otro sexo.
                    UnequipAll(p, _data);
                    // Las cinco auras se apagan, pero el original DIFUNDE una sola: `AuraToChar(charIndex, 0, 1)`
                    // por el arma (:1971). Las otras cuatro las pone en 0 en su estructura y no avisa a nadie —
                    // rareza de la fuente que se porta igual, porque el `CharacterCreate` de más abajo ya manda
                    // el estado real y quien llegue después las ve bien de todos modos.
                    p.WeaponAura = 0; p.BodyAura = 0; p.ShieldAura = 0; p.HelmetAura = 0; p.RingAura = 0;
                    _world.BroadcastToObservers(p.Pos, ServerMessages.AuraToChar(p.CharIndex, 0, 1));
                    p.Gender = p.Gender == 2 ? (byte)1 : (byte)2;   // eGenero: Hombre=1, Mujer=2
                    p.Head = p.OrigHead = CabezaAlAzar(p.Race, p.Gender);   // ídem: la cabeza VIVA también
                    p.Body = Equipment.NakedBody(p.Race, p.Gender);
                    _world.BroadcastToObservers(p.Pos, ServerMessages.CharacterChange(p));   // VB6 ChangeUserCharTodo
                    _world.BroadcastToObservers(p.Pos, ServerMessages.CreateFX(p.CharIndex, 30, 0));
                    effect = p.Gender == 2 ? "ahora es mujer" : "ahora es hombre";
                    break;
                }
                case 9: // Poción mágica de Nhareth (VB6 InvUsuario.bas:1678) — objeto 506
                {
                    // Partícula 23 PERMANENTE (el -1 del original), y sólo si no tiene ninguna puesta: con una
                    // encima el VB6 hace `Exit Sub` y la poción NI SIQUIERA SE GASTA. Por eso es `return` y no
                    // `break` — un `break` se la comería sin darle nada.
                    if (p.ParticulaFx != 0) return;
                    p.ParticulaFx = 23;
                    _world.BroadcastToObservers(p.Pos, ServerMessages.EfectoCharParticula(p.CharIndex, 23, -1, false));
                    effect = "aura de Nhareth";
                    break;
                }
                default:
                    // Los SubTipo 10, 11 y 12 del obj.dat (Scroll x2 Experiencia, x2 Oro y del Trueno) caen acá y
                    // está bien: son data sin código TAMBIÉN en el original —su Select Case salta del 9 al 13—,
                    // o sea ítems que el servidor de origen tampoco sabía usar. Lo que no se porta es lo muerto.
                    session.Send(ServerMessages.ConsoleMsg("Esa poción no tiene efecto.", FuenteInfo)); return;
            }

            inv.Amount -= 1;
            if (inv.Amount <= 0) inv = default;
            p.Inventory[slot] = inv;
            session.Send(ServerMessages.PlayWave(SND_BEBER, p.Pos.X, p.Pos.Y));      // drink sound (SND_BEBER)
            // Mismo gate que la bebida: los cuatro Case de poción del original lo traen (InvUsuario.bas:1578,
            // 1596, 1613 y 1628). Sin él, un GM invisible tomando una poción se escucha en toda el área.
            if (!p.Invisible)
                _world.BroadcastToObservers(p.Pos, ServerMessages.PlayWave(SND_BEBER, p.Pos.X, p.Pos.Y), p.CharIndex);
            // El original manda el granular del stat que tocó la poción, no el UpdateUserStats gordo: cada
            // Case de InvUsuario.bas:1567-1626 elige su propio Write (Dexterity / Strenght / HP / Mana).
            if (avisoStat is not null) session.Send(avisoStat);
            session.Send(ServerMessages.ChangeInventorySlot(slot, inv.ObjIndex, inv.Amount, inv.Equipped, 0f, (byte)(inv.IsEmpty ? 0 : 1)));
            Console.WriteLine($"[{session.RemoteEndpoint}] {p.Name} usa {obj.Name} ({effect})");
        });
    }

    private void HandleCommerceStart(ClientSession session)
    {
        _loop.Enqueue(() =>
        {
            var p = session.Player;
            if (p is null) return;
            if (DeadCheck(session, p)) return;   // VB6 HandleCommerceStart:5167 (un muerto no comercia — gatea la apertura directa, no sólo via DoubleClick)
            // "Ya estás comerciando" (Protocol.bas:5175). Faltaba: abrir la tienda dos veces reabría la
            // ventana en silencio en vez de avisar. Y mira la BÓVEDA también, porque el original tiene un solo
            // flag para las dos ventanas (ver Player.Comerciando).
            if (p.Comerciando) { session.Send(ServerMessages.LocaleMsg(387)); return; }
            // EL MERCADER ES EL QUE CLICKEASTE, no el que tenés enfrente. El original abre la tienda contra
            // `flags.TargetNPC` (Protocol.bas:5181) y por eso tolera hasta 3 baldosas; acá se resolvía por la
            // baldosa ENCARADA, que además de exigir estar pegado y mirándolo salteaba los tres rechazos de
            // abajo: sin un NPC targeteado el handler entero era inalcanzable. El cliente web ya manda el
            // LeftClick antes del DoubleClick, así que el target está puesto igual que en el original.
            if (p.TargetNpc < 1) return;                   // VB6: sin TargetNPC, silencio
            var npc = _world.NpcByIndex(p.TargetNpc);
            if (npc is null) return;
            if (!npc.IsMerchant) { NoTengoInteresEnComerciar(session, npc, conDescripcion: true); return; }
            if (DistanciaVB6(p.Pos, npc.Pos) > 3) { session.Send(ServerMessages.LocaleMsg(8)); return; }
            if (!MercaderAtiende(session, p, npc)) return;
            OpenCommerce(session, p, npc);
        });
    }

    /// <summary>"Lo siento, no tengo ningún interés en comerciar" — el <c>ChatOverHeadLocale(594, 4)</c> con el
    /// que el original contesta cuando le pedís comerciar a un NPC que no comercia (Protocol.bas:3427, :3541 y
    /// :5187). Faltaba entero: acá esos tres caminos salían mudos.
    ///
    /// <para><paramref name="conDescripcion"/> es la guarda <c>If LenB(Npclist(...).desc) &lt;&gt; 0</c> que el
    /// original pone SÓLO en el camino de <c>CommerceStart</c>; en el de comprar y el de vender manda el paquete
    /// sin mirar la descripción.</para>
    ///
    /// <para>Y OJO con lo que ve el jugador: el modo 4 resuelve el texto contra la tabla de NPCs
    /// (<c>General_Locale_NPCs(id, 1)</c>) y el cliente original sólo desvía al texto de GUI cuando el id es
    /// 592 — el 594 se va a buscar la descripción de la ficha 594, que es una "Gallina Salvaje" SIN descripción.
    /// O sea que en el original este aviso <b>no se ve</b>: la frase existe, está en <c>locale_gui_es.ind</c>
    /// línea 594, y el <c>Select Case</c> del cliente no la alcanza. Se porta el paquete, que es el contrato, y
    /// el cliente web replica el mismo camino en vez de "arreglarle" el bug.</para></summary>
    private static void NoTengoInteresEnComerciar(ClientSession session, Npc npc, bool conDescripcion)
    {
        if (conDescripcion && string.IsNullOrEmpty(npc.Template.Description)) return;
        session.Send(ServerMessages.ChatOverHeadLocale(npc.CharIndex, 594, 4));
    }

    /// <summary>El gate de FACCIÓN del comercio (VB6 Protocol.bas:5203-5220 y Acciones.bas:180-197): si no te
    /// atiende, manda el <c>ChatOverHeadLocale(592, 4)</c> —"¡Largo de aquí!, no tengo ningún interés en
    /// comerciar con enemigos"— y devuelve false. La regla y su porqué están en
    /// <see cref="Player.MercaderMeAtiende"/>; acá va la mitad que el original deja afuera del helper: sólo se
    /// gatea al jugador común (<c>If .flags.Privilegios And PlayerType.User</c>), el staff comercia donde sea.</summary>
    private bool MercaderAtiende(ClientSession session, Player p, Npc npc)
    {
        if ((p.Privileges & PrivUser) == 0) return true;              // GM: el original ni entra al bloque
        if (p.MercaderMeAtiende(_data.MapBattleMode(p.Pos.Map))) return true;
        session.Send(ServerMessages.ChatOverHeadLocale(npc.CharIndex, 592, 4));
        return false;
    }

    /// <summary>Opens a merchant's trade window: CommerceInit + one ChangeNpcInventorySlot per stock item.</summary>
    private void OpenCommerce(ClientSession session, Player p, Npc npc)
    {
        p.TradingWith = npc.CharIndex;
        // Y el mercader queda como TARGET, que en el original es la MISMA cosa: al VB6 sólo se le abre la
        // tienda haciéndole click al NPC (eso setea `flags.TargetNPC`), y después `HandleCommerceBuy` usa ese
        // campo tanto para el gate como para saber a quién comprarle (`Comercio(..., .flags.TargetNPC, ...)`).
        // Acá la tienda se abre por la baldosa que mirás, así que hay que sostener el invariante a mano: sin
        // esto el gate `TargetNPC < 1` —que es fiel y lo pide el oráculo— deja la compra muerta.
        p.TargetNpc = npc.CharIndex;
        p.TargetNpcTipo = (byte)npc.Template.NpcType;
        session.Send(ServerMessages.CommerceInit());
        for (byte i = 0; i < npc.Stock!.Count; i++)
        {
            var st = npc.Stock[i];
            session.Send(ServerMessages.ChangeNpcInventorySlot((byte)(i + 1), st.Amount, (int)PrecioParaElComprador(p, st.Price), st.ObjIndex));
        }
    }

    private void HandleCommerceEnd(ClientSession session)
    {
        _loop.Enqueue(() =>
        {
            var p = session.Player;
            if (p is null) return;
            p.TradingWith = 0;
            session.Send(ServerMessages.CommerceEnd());
        });
    }

    private void HandleCommerceBuy(ClientSession session, ByteQueueReader r)
    {
        byte slot = r.ReadByte();
        short amount = r.ReadInteger();
        _loop.Enqueue(() =>
        {
            var p = session.Player;
            if (p is null) return;
            // EL ORDEN DE LOS GATES IMPORTA, y el oráculo lo cazó: el original (Protocol.bas:3413-3434) mira
            // primero si estás MUERTO, después sale en SILENCIO si no hay NPC clickeado (`TargetNPC < 1`),
            // después le avisa al que le compra a quien no vende, y recién ahí dice "no estás comerciando" con
            // el 388. Poner el 388 primero emite donde el original calla —que es tan infiel como no emitir— y
            // el barrido en frío, donde nadie tiene target, lo destapa siempre.
            if (DeadCheck(session, p)) return;
            if (p.TargetNpc < 1) return;
            if (_world.NpcByIndex(p.TargetNpc) is { IsMerchant: false } mudo)
            { NoTengoInteresEnComerciar(session, mudo, conDescripcion: false); return; }
            if (p.TradingWith == 0) { session.Send(ServerMessages.LocaleMsg(388)); return; }
            var npc = _world.NpcByIndex(p.TradingWith);
            if (npc?.Stock is null || slot < 1 || slot > npc.Stock.Count) return;
            var st = npc.Stock[slot - 1];
            if (st.Amount < 1) return;   // slot agotado: sin esto Math.Clamp(_,1,0) lanza ArgumentException (min>max)
            int buy = Math.Clamp((int)amount, 1, st.Amount);
            long cost = PrecioParaElComprador(p, st.Price) * buy;
            if (p.Gold < cost) { session.Send(ServerMessages.LocaleMsg(26)); return; }
            int invSlot = FindSlotFor(p, st.ObjIndex, buy);
            if (invSlot == 0) { session.Send(ServerMessages.LocaleMsg(25)); return; }

            p.Gold -= (int)cost;
            var inv = p.Inventory[invSlot];
            inv.ObjIndex = st.ObjIndex;
            inv.Amount = (short)Math.Min(GameLimits.MaxInventoryObjs, inv.Amount + buy);
            p.Inventory[invSlot] = inv;
            DescontarStockDelMercader(npc, slot - 1, (short)buy);

            session.Send(ServerMessages.ChangeInventorySlot((byte)invSlot, inv.ObjIndex, inv.Amount, inv.Equipped, 0f, 1));
            session.Send(ServerMessages.ChangeNpcInventorySlot(slot, st.Amount, (int)PrecioParaElComprador(p, st.Price), st.ObjIndex));
            session.Send(ServerMessages.UpdateGold(p.Gold));
            session.Send(ServerMessages.ConsoleMsg($"Compraste {buy} x {_data.Obj(st.ObjIndex)?.Name ?? "objeto"} por {cost} oro.", FuenteInfo));
            // Regatear entrena: el VB6 sube Comerciar al final de `Sub Comercio` (Comercio.bas:192), que es la
            // MISMA rutina para comprar y para vender, asi que las dos operaciones entrenan.
            RaiseSkill(session, p, GameLimits.SkillComerciar);
        });
    }

    private void HandleCommerceSell(ClientSession session, ByteQueueReader r)
    {
        byte slot = r.ReadByte();
        short amount = r.ReadInteger();
        _loop.Enqueue(() =>
        {
            var p = session.Player;
            if (p is null) return;
            // La MISMA cadena de gates que la compra, y por el mismo motivo (VB6 Protocol.bas:3529-3545):
            // muerto -> 77, sin NPC clickeado -> silencio, NPC que no comercia -> el 594 sobre su cabeza.
            if (DeadCheck(session, p)) return;
            if (p.TargetNpc < 1) return;
            if (_world.NpcByIndex(p.TargetNpc) is { IsMerchant: false } mudo)
            { NoTengoInteresEnComerciar(session, mudo, conDescripcion: false); return; }
            if (p.TradingWith == 0 || slot < 1 || slot > GameLimits.MaxInventorySlots) return;
            var inv = p.Inventory[slot];
            if (inv.IsEmpty) return;

            // QUÉ ACEPTA COMPRARTE EL MERCADER (VB6 Comercio.bas:123). Rechaza con LocaleMsg 148 si el
            // `TipoItems` del NPC no coincide con el `ObjType` del objeto —salvo `otCualquiera`, que compra
            // de todo— o si le querés vender ORO. Lo traen 82 de los 291 NPCs: el herrero compra armas, el
            // sastre ropa, el de comidas comida. Sin este gate cualquier mercader compraba CUALQUIER cosa.
            //
            // Los otros cinco gates de esa misma cadena `ElseIf` están MUERTOS en este install y por eso no
            // se portan: `Newbie`, `Shop`, `Real`, `Caos` y `Milicia` aparecen CERO veces en el obj.dat
            // crudo, y los mercaderes especiales que los atienden ("SR", "SC", "SM") no existen en NPCs.dat.
            var mercader = _world.NpcByIndex(p.TradingWith);
            var aVender = _data.Obj(inv.ObjIndex);
            if (mercader is not null && aVender is not null
                && !mercader.Template.CompraObjeto(aVender.ObjType, inv.ObjIndex))
            {
                session.Send(ServerMessages.LocaleMsg(148));
                return;
            }

            int sell = Math.Clamp((int)amount, 1, inv.Amount);
            // VB6 `Precio = Fix(SalePrice(ObjIndex) * Cantidad)` (Comercio.bas:158) con SalePrice (Comercio.bas:306):
            //     objeto inválido -> 0 | ItemNewbie -> 0 | si no, Valor / REDUCTOR_PRECIOVENTA(=3)
            // Acá había `Math.Max(1, (Value ?? 3) / 3)`, que tenía CUATRO divergencias, y las cuatro pagaban de más:
            //   1. el PISO de 1: el original no tiene ninguno, paga 0 por todo lo que valga menos de 3;
            //   2. faltaba el gate ItemNewbie (inerte con esta data —ningún objeto trae la clave— pero es la fuente);
            //   3. el fallback `?? 3` inventaba valor 3 para un objeto que ni está en la data (el VB6 devuelve 0);
            //   4. dividía ANTES de multiplicar (entera), en vez de multiplicar y truncar al final como el `Fix`.
            // El PISO era una impresora de oro: el kit de novato trae ~1700 unidades de Valor 0 que el original
            // paga a 0 y el port pagaba a 1 c/u, o sea ~1700 monedas de la nada por personaje creado, repetible
            // sin matar nada ni esperar cadencia. El 4 además pagaba de menos en los ítems caros: con Valor 5 y
            // cantidad 2 el original da Fix(1,67*2)=3 y el port daba (5/3)*2=2.
            var vendido = _data.Obj(inv.ObjIndex);
            short objVendido = inv.ObjIndex;   // se guarda ANTES: abajo el slot se vacía y el índice se pierde
            double salePrice = vendido is null || vendido.IsNewbie ? 0.0 : vendido.Value / 3.0;
            int gain = (int)Math.Floor(salePrice * sell);   // Fix(): trunca (todo es positivo acá)

            // Vender lo que se lleva puesto lo DESEQUIPA, y va acá arriba porque el original saca el objeto
            // antes de calcular el precio (`QuitarUserInvItem`, Comercio.bas:154, contra el `Fix` de :158 y el
            // oro de :167). El orden de emisión es parte del contrato: si el CharacterChangeSlot saliera
            // después del UpdateGold, el oráculo lo vería como una secuencia distinta.
            DesequiparSiSeVaEntero(session, p, slot, sell);
            inv = p.Inventory[slot];
            p.Gold = (int)Math.Min((long)p.Gold + gain, MaxOro);   // VB6 Comercio.bas:169, sobre el TOTAL
            inv.Amount -= (short)sell;
            if (inv.Amount <= 0) inv = default;
            p.Inventory[slot] = inv;

            // LO VENDIDO VA AL STOCK DEL MERCADER (VB6 Comercio.bas:169-180: `SlotEnNPCInv` → suma → tope
            // MAX_INVENTORY_OBJS → `UpdateNpcInvToAll`). Acá el objeto se DESTRUÍA: salía del inventario del
            // jugador y no entraba a ningún lado, así que no se podía recomprar lo que uno mismo acababa de
            // vender — ni ver aparecer en la tienda lo que vendió otro.
            MeterEnStockDelMercader(session, mercader, objVendido, (short)sell);
            session.Send(ServerMessages.ChangeInventorySlot(slot, inv.ObjIndex, inv.Amount, inv.Equipped, 0f, (byte)(inv.IsEmpty ? 0 : 1)));
            session.Send(ServerMessages.UpdateGold(p.Gold));
            session.Send(ServerMessages.ConsoleMsg($"Vendiste {sell} objeto(s) por {gain} oro.", FuenteInfo));
            RaiseSkill(session, p, GameLimits.SkillComerciar);   // Comercio.bas:192 (misma Sub que la compra)
        });
    }

    private void HandleBankStart(ClientSession session)
    {
        _loop.Enqueue(() =>
        {
            var p = session.Player;
            if (p is null) return;
            if (DeadCheck(session, p)) return;   // VB6 HandleBankStart:5250 (un muerto no banquea — gatea la apertura directa, no sólo via DoubleClick)
            if (p.Comerciando) { session.Send(ServerMessages.LocaleMsg(387)); return; }   // VB6 :5257 — faltaba entero
            // EL BANQUERO ES EL QUE CLICKEASTE, igual que el mercader. El original abre la bóveda contra
            // `flags.TargetNPC` (Protocol.bas:5263) y por eso tolera hasta 3 baldosas; acá se resolvía por la
            // baldosa ENCARADA, que exigía estar pegado y mirándolo, y además hacía inalcanzables los dos
            // rechazos de abajo. El cliente web ya manda el LeftClick antes del DoubleClick.
            if (p.TargetNpc < 1) { session.Send(ServerMessages.LocaleMsg(22)); return; }   // VB6 :5277, la rama Else
            var npc = _world.NpcByIndex(p.TargetNpc);
            if (npc is null) return;
            if (DistanciaVB6(p.Pos, npc.Pos) > 3) { session.Send(ServerMessages.LocaleMsg(8)); return; }
            if (!npc.IsBanker) return;   // VB6: con un target que no es banquero no dice nada, ni siquiera el 22
            OpenBank(session, p, npc);
        });
    }

    /// <summary>Opens the vault for a player: BankInit (gold + item count) then one ChangeBankSlot per slot.
    /// Shared by HandleBankStart (BankStart packet) and the DoubleClick-on-banker path.</summary>
    private void OpenBank(ClientSession session, Player p, Npc banquero)
    {
        p.BankOpen = true;
        // SOSTENER EL INVARIANTE DEL ORIGINAL. El VB6 lee el banquero de `.flags.TargetNPC`, que le queda
        // seteado del clic con el que se abre la ventana; nuestro port la abre por la baldosa encarada y nunca
        // llenaba ese campo. Los handlers de oro lo necesitan para colgarle el globo de diálogo al banquero y
        // para medir la distancia, así que se setea acá — el mismo arreglo que se le hizo a OpenCommerce.
        p.TargetNpc = banquero.CharIndex;
        byte count = 0;
        for (int i = 1; i <= GameLimits.MaxBankSlots; i++) if (!p.BankInventory[i].IsEmpty) count++;
        session.Send(ServerMessages.BankInit(p.Bank, count));
        for (byte i = 1; i <= GameLimits.MaxBankSlots; i++)
        {
            var b = p.BankInventory[i];
            session.Send(ServerMessages.ChangeBankSlot(i, b.ObjIndex, b.Amount, _data.Obj(b.ObjIndex)?.Value ?? 0));
        }
    }

    private void HandleBankEnd(ClientSession session)
    {
        _loop.Enqueue(() => { var p = session.Player; if (p is null) return; p.BankOpen = false; session.Send(ServerMessages.BankEnd()); });
    }

    private void HandleBankDeposit(ClientSession session, ByteQueueReader r)
    {
        byte slot = r.ReadByte(); short amount = r.ReadInteger();
        _loop.Enqueue(() =>
        {
            var p = session.Player;
            if (p is null || !p.BankOpen || slot < 1 || slot > GameLimits.MaxInventorySlots) return;
            var inv = p.Inventory[slot];
            if (inv.IsEmpty) return;
            int move = Math.Clamp((int)amount, 1, inv.Amount);
            int bslot = FindBankSlotFor(p, inv.ObjIndex, move);
            if (bslot == 0) { session.Send(ServerMessages.LocaleMsg(149)); return; }
            // NO SE MUEVE MAS DE LO QUE ENTRA. El descuento del inventario y la suma a la boveda tienen que
            // ser el MISMO numero: si la ranura no da para todo, se lleva lo que cabe y el resto se queda
            // donde estaba. Antes el tope se aplicaba solo del lado que recibia, y la diferencia se perdia.
            move = Math.Min(move, GameLimits.MaxInventoryObjs - p.BankInventory[bslot].Amount);
            if (move <= 0) { session.Send(ServerMessages.LocaleMsg(149)); return; }
            short obj = inv.ObjIndex;
            DesequiparSiSeVaEntero(session, p, slot, move);   // guardar lo puesto en la bóveda también lo saca
            inv = p.Inventory[slot];
            inv.Amount -= (short)move; if (inv.Amount <= 0) inv = default; p.Inventory[slot] = inv;
            var bk = p.BankInventory[bslot]; bk.ObjIndex = obj; bk.Amount = (short)(bk.Amount + move); p.BankInventory[bslot] = bk;
            session.Send(ServerMessages.ChangeInventorySlot(slot, inv.ObjIndex, inv.Amount, inv.Equipped, 0f, (byte)(inv.IsEmpty ? 0 : 1)));
            session.Send(ServerMessages.ChangeBankSlot((byte)bslot, bk.ObjIndex, bk.Amount, _data.Obj(obj)?.Value ?? 0));
            session.Send(ServerMessages.BankOk());
        });
    }

    private void HandleBankExtract(ClientSession session, ByteQueueReader r)
    {
        byte slot = r.ReadByte(); short amount = r.ReadInteger();
        _loop.Enqueue(() =>
        {
            var p = session.Player;
            if (p is null || !p.BankOpen || slot < 1 || slot > GameLimits.MaxBankSlots) return;
            var bk = p.BankInventory[slot];
            if (bk.IsEmpty) return;
            int move = Math.Clamp((int)amount, 1, bk.Amount);
            int islot = FindSlotFor(p, bk.ObjIndex, move);
            if (islot == 0) { session.Send(ServerMessages.LocaleMsg(25)); return; }
            // Misma regla que el deposito, del otro lado: lo que sale de la boveda es lo que entra en la
            // mochila, ni una unidad mas.
            move = Math.Min(move, GameLimits.MaxInventoryObjs - p.Inventory[islot].Amount);
            if (move <= 0) { session.Send(ServerMessages.LocaleMsg(25)); return; }
            short obj = bk.ObjIndex;
            bk.Amount -= (short)move; if (bk.Amount <= 0) bk = default; p.BankInventory[slot] = bk;
            var inv = p.Inventory[islot]; inv.ObjIndex = obj; inv.Amount = (short)(inv.Amount + move); p.Inventory[islot] = inv;
            session.Send(ServerMessages.ChangeBankSlot(slot, bk.ObjIndex, bk.Amount, _data.Obj(obj)?.Value ?? 0));
            session.Send(ServerMessages.ChangeInventorySlot((byte)islot, inv.ObjIndex, inv.Amount, inv.Equipped, 0f, 1));
            session.Send(ServerMessages.BankOk());
        });
    }

    /// <summary>El banquero con el que se está operando (VB6 <c>Npclist(.flags.TargetNPC)</c>). Devuelve null si
    /// no hay ninguno targeteado o si el que hay no es banquero — las dos ramas en que el original sale sin
    /// hacer nada.</summary>
    private Npc? BanqueroDelTarget(Player p) =>
        _world.NpcByIndex(p.TargetNpc) is { IsBanker: true } b ? b : null;

    /// <summary>Depositar oro (VB6 <c>HandleBankDepositGold</c>, Protocol.bas:6027). Le faltaban los dos gates
    /// de entrada —MUERTO (77) y LEJOS (8, con la <c>Distancia</c> Manhattan del original)— así que con la
    /// bóveda abierta se podía caminar hasta la otra punta del mapa y seguir operando, y un cadáver también.
    ///
    /// Y la emisión no era la del original: acá salía un <c>ConsoleMsg</c> con texto nuestro. El VB6 cuelga el
    /// saldo en el <b>GLOBO DE DIÁLOGO DEL BANQUERO</b> (<c>WriteChatOverHead</c> sobre su CharIndex) y tira el
    /// tintineo del oro al área (:6078-6081). Además NO recorta: pedir más de lo que tenés RECHAZA con "No
    /// tenés esa cantidad."; con el <c>Clamp</c> que había, pedir de más depositaba todo.</summary>
    private void HandleBankDepositGold(ClientSession session, ByteQueueReader r)
    {
        int amount = r.ReadLong();
        _loop.Enqueue(() =>
        {
            var p = session.Player;
            if (p is null) return;
            if (!p.IsAlive) { session.Send(ServerMessages.LocaleMsg(77)); return; }
            if (!p.BankOpen) { session.Send(ServerMessages.LocaleMsg(22)); return; }   // VB6: sin banquero targeteado
            var banquero = BanqueroDelTarget(p);
            if (banquero is null) return;                                              // NPCtype <> Banquero: silencio
            if (DistanciaVB6(p.Pos, banquero.Pos) > 10) { session.Send(ServerMessages.LocaleMsg(8)); return; }
            if (amount <= 0 || amount > p.Gold)
            { session.Send(ServerMessages.ChatOverHead("No tenés esa cantidad.", banquero.CharIndex, 0)); return; }
            p.Gold -= amount; p.Bank += amount;
            session.Send(ServerMessages.ChatOverHead($"Tenés {p.Bank} monedas de oro en tu cuenta.", banquero.CharIndex, 0));
            _world.BroadcastToObservers(p.Pos, ServerMessages.PlayWave(SndOro, p.Pos.X, p.Pos.Y));
            session.Send(ServerMessages.UpdateGold(p.Gold));   // DENTRO del éxito, como el original (:6082)
        });
    }

    /// <summary>Retirar oro (VB6 <c>HandleBankExtractGold</c>, Protocol.bas:5955). Los mismos gates y la misma
    /// emisión que el depósito, con dos diferencias que están en la fuente y se portan tal cual: el rechazo dice
    /// <b>"No tienes esa cantidad."</b> (el depósito dice "No tenés") y el <c>WriteUpdateGold</c> va <b>FUERA</b>
    /// del If/Else (:6016), o sea que se manda aunque no se haya movido una moneda.
    ///
    /// El <c>OroLleno</c> del original no se porta: es un guardia roto —compara un <c>Long</c> contra
    /// 2.147.483.647— y ya está anotado como tal.</summary>
    private void HandleBankExtractGold(ClientSession session, ByteQueueReader r)
    {
        int amount = r.ReadLong();
        _loop.Enqueue(() =>
        {
            var p = session.Player;
            if (p is null) return;
            if (!p.IsAlive) { session.Send(ServerMessages.LocaleMsg(77)); return; }
            if (!p.BankOpen) { session.Send(ServerMessages.LocaleMsg(22)); return; }   // VB6: sin banquero targeteado
            var banquero = BanqueroDelTarget(p);
            if (banquero is null) return;                                              // NPCtype <> Banquero: silencio
            if (DistanciaVB6(p.Pos, banquero.Pos) > 10) { session.Send(ServerMessages.LocaleMsg(8)); return; }
            if (amount <= 0 || amount > p.Bank)
                session.Send(ServerMessages.ChatOverHead("No tienes esa cantidad.", banquero.CharIndex, 0));
            else
            {
                p.Bank -= amount; p.Gold += amount;
                session.Send(ServerMessages.ChatOverHead($"Tenés {p.Bank} monedas de oro en tu cuenta.", banquero.CharIndex, 0));
                _world.BroadcastToObservers(p.Pos, ServerMessages.PlayWave(SndOro, p.Pos.X, p.Pos.Y));
            }
            session.Send(ServerMessages.UpdateGold(p.Gold));   // FUERA del If/Else, como el original (:6016)
        });
    }

    /// <summary>Descuenta del inventario de un mercader y lo repone como el original
    /// (<c>QuitarNpcInvItem</c>, Modulo_InventANDobj.bas:202-263).
    ///
    /// Acá había un gate <c>if (Crucial == 0)</c> que dejaba el contador de los ítems crucial CLAVADO, con la
    /// lectura de que "todo el descuento está dentro de un <c>If ObjData(...).Crucial = 0</c>". Es media
    /// función: ese <c>If</c> tiene un <c>Else</c>, y el <c>Else</c> TAMBIÉN descuenta (:233). Lo que distingue
    /// a las dos ramas es qué pasa cuando el slot llega a cero:
    /// <list type="bullet">
    /// <item><b>Crucial = 1</b> — el slot se REPONE solo, releyendo la cantidad del NPCs.dat
    /// (<c>EncontrarCant</c>, :242), siempre que no quede ese mismo objeto en otro slot (<c>QuedanItems</c>).</item>
    /// <item><b>Crucial = 0</b> — el slot queda vacío; y sólo cuando se vacían TODOS (<c>NroItems = 0</c>) se
    /// recarga el inventario entero (<c>CargarInvent</c>, :225 y :253 — está en las DOS ramas).</item>
    /// </list>
    /// O sea: no es stock infinito, es <b>auto-restock</b>. Con el gate viejo, comprarle 2 pociones al
    /// alquimista dejaba el stock en 100 donde el original deja 98; y de yapa los objetos NO crucial se
    /// agotaban para siempre, que es exactamente el bug que el gate pretendía evitar, corrido de lugar.</summary>
    /// <summary>Mete lo que el jugador acaba de vender en el stock del mercader (VB6 Comercio.bas:169-180).
    /// Apila en un slot del mismo objeto si entra, si no usa uno libre, y acota al tope de la pila. El precio
    /// del slot nuevo sale del `Valor` del obj.dat, que es lo que el mercader cobra por lo que tiene.</summary>
    private void MeterEnStockDelMercader(ClientSession session, Npc? npc, short objIndex, short cantidad)
    {
        if (npc?.Stock is null || objIndex <= 0 || cantidad <= 0) return;
        int idx = npc.Stock.FindIndex(st => st.ObjIndex == objIndex && st.Amount + cantidad <= GameLimits.MaxInventoryObjs);
        if (idx < 0) idx = npc.Stock.FindIndex(st => st.ObjIndex == 0 || st.Amount <= 0);
        if (idx < 0)
        {
            if (npc.Stock.Count >= GameLimits.MaxInventorySlots) return;   // sin lugar: el original tampoco lo mete
            npc.Stock.Add(new MerchantSlot { ObjIndex = 0, Amount = 0, Price = 0 });
            idx = npc.Stock.Count - 1;
        }
        var slotStock = npc.Stock[idx];
        slotStock.ObjIndex = objIndex;
        slotStock.Amount = (short)Math.Min(GameLimits.MaxInventoryObjs, slotStock.Amount + cantidad);
        if (slotStock.Price <= 0) slotStock.Price = Math.Max(1, _data.Obj(objIndex)?.Value ?? 1);
        npc.Stock[idx] = slotStock;
        session.Send(ServerMessages.ChangeNpcInventorySlot((byte)(idx + 1), slotStock.Amount,
            (int)PrecioParaElComprador(session.Player, slotStock.Price), slotStock.ObjIndex));
    }

    private void DescontarStockDelMercader(Npc npc, int idx, short cantidad)
    {
        if (npc.Stock is null || idx < 0 || idx >= npc.Stock.Count) return;
        var st = npc.Stock[idx];
        short objIndex = st.ObjIndex;
        st.Amount -= cantidad;
        if (st.Amount > 0) { npc.Stock[idx] = st; return; }

        st.ObjIndex = 0; st.Amount = 0;
        npc.Stock[idx] = st;

        // Rama crucial: reponer ESE slot con la cantidad del .dat, si el objeto no quedó en ningún otro.
        if ((_data.Obj(objIndex)?.Crucial ?? 0) != 0 && !npc.Stock.Any(s => s.ObjIndex == objIndex))
        {
            var orig = npc.Template.StockItems?.FirstOrDefault(it => it.ObjIndex == objIndex);
            if (orig is { Amount: > 0 })
            {
                st.ObjIndex = objIndex;
                st.Amount = (short)Math.Clamp(orig.Value.Amount, 1, short.MaxValue);
                npc.Stock[idx] = st;
                return;
            }
        }
        // CargarInvent: con TODOS los slots vacíos se recarga el inventario entero desde el .dat. Va en las dos
        // ramas del original, así que también repone a los no-crucial una vez agotados.
        if (npc.Stock.All(s => s.ObjIndex == 0) && npc.Template.StockItems is { Count: > 0 } items)
            for (int i = 0; i < npc.Stock.Count && i < items.Count; i++)
                npc.Stock[i] = new MerchantSlot
                {
                    ObjIndex = (short)items[i].ObjIndex,
                    Amount = (short)Math.Clamp(items[i].Amount, 1, short.MaxValue),
                    Price = Math.Max(1, _data.Obj((short)items[i].ObjIndex)?.Value ?? 1),
                };
    }

    /// <summary>
    /// Ranura de la BÓVEDA donde entra <paramref name="cantidad"/> de ese objeto: primero una que ya lo tenga
    /// y en la que QUEPA, si no la primera vacía, y 0 si no hay ninguna.
    ///
    /// <para>El <c>cantidad</c> es el arreglo. Esta función devolvía la primera ranura con el mismo objeto sin
    /// mirar cuánto tenía, y los dos handlers cerraban la suma con <c>Math.Min(short.MaxValue, actual + move)</c>:
    /// con 30.000 flechas en la bóveda, depositar 5.000 descontaba las 5.000 del inventario y dejaba la bóveda
    /// en 32.767 — 2.233 flechas <b>destruidas en silencio</b>. Y de paso se pasaba del tope de 10.000 por
    /// ranura que el resto del juego sí respeta (<c>FindSlotFor</c> ya lo hacía; ésta era la única que no).</para>
    ///
    /// <para>Es <c>public</c> por la misma razón que las constantes de la apuesta: para que un test pueda
    /// afirmar sobre la regla sin levantar medio servidor.</para>
    /// </summary>
    public static int FindBankSlotFor(Player p, short objIndex, int cantidad = 1)
    {
        for (int i = 1; i <= GameLimits.MaxBankSlots; i++)
            if (!p.BankInventory[i].IsEmpty && p.BankInventory[i].ObjIndex == objIndex
                && p.BankInventory[i].Amount + cantidad <= GameLimits.MaxInventoryObjs) return i;   // apila si entra
        for (int i = 1; i <= GameLimits.MaxBankSlots; i++)
            if (p.BankInventory[i].IsEmpty) return i;
        return 0;
    }

    private void HandleEquipItem(ClientSession session, ByteQueueReader r)
    {
        byte slot = r.ReadByte();
        _loop.Enqueue(() =>
        {
            var p = session.Player;
            if (p is null || slot < 1 || slot > GameLimits.MaxInventorySlots) return;
            if (DeadCheck(session, p)) return;   // VB6 HandleEquipItem: Protocol.bas:3157
            var inv = p.Inventory[slot];
            if (inv.IsEmpty) return;
            // El `is null` corta ACÁ y no más abajo: `Equipment.Category(ObjInfo)` dereferencia su argumento,
            // así que un slot con un ObjIndex fuera del catálogo —un .chr viejo, un obj.dat cambiado— tiraba
            // NullReferenceException. No mataba al server (el Drain del game loop atrapa por acción), pero
            // equipar fallaba en silencio y dejaba un error en el log. Era el único warning del build (CS8604).
            var obj = _data.Obj(inv.ObjIndex);
            if (obj is null) return;
            if (obj.IsMount)
            {
                // No se monta bajo techo ni en un dungeon (VB6 DoEquita, Trabajo.bas:2345). El gate va sólo al
                // MONTAR: desmontar adentro tiene que poder hacerse, o el jugador que entra a caballo queda
                // trabado arriba del animal. El original ordena igual — el `If .flags.Montando = 0` envuelve
                // sólo la rama de subirse.
                if (!p.Mounted && _data.NoSeMontaAca(p.Pos.Map, p.Pos.X, p.Pos.Y))
                {
                    session.Send(ServerMessages.LocaleMsg(253));   // "No puedes montar aquí"
                    return;
                }
                AlternarMontura(session, p, slot, obj);
                return;
            }
            int cat = Equipment.Category(obj);
            if (cat == 0) return;   // VB6: objeto no equipable -> silencio

            if (inv.Equipped)
            {
                inv.Equipped = false;
                ClearEquipAnim(p, obj);
                AplicarBonusDeEquipo(p, obj, poniendose: false);
                p.Inventory[slot] = inv;
                // Sacarse la HERRAMIENTA corta la faena en el acto (VB6 `Desequipar`, InvUsuario.bas:767, justo
                // después de limpiar `AnilloEqpObjIndex`). El loop ya la cortaba solo, pero recién en la vuelta
                // siguiente —hasta 700 ms después— y sin avisar; y si se cambiaba de herramienta, seguía
                // trabajando con el oficio nuevo en vez de parar.
                if (cat == 6 && p.Laborando)
                {
                    p.Laborando = false;
                    session.Send(ServerMessages.LocaleMsg(391, "", 1));
                }
            }
            else
            {
                // Restricciones de equipamiento (VB6 EquiparInvItem, InvUsuario.bas:909). Nivel/clase/raza solo
                // se validan para un jugador normal (User), no para GMs (guard "Privilegios And User"). El género
                // (obj.Genero) y las facciones de ejército (obj.Faccion → 439) se difieren: el slice no modela
                // esos subsistemas (todo char es ciudadano Status 0/1, sin Real/Caos/Milicia).
                if (!p.IsGm)
                {
                    if (obj.MinLevel > 0 && p.Level < obj.MinLevel) { session.Send(ServerMessages.LocaleMsg(268, obj.MinLevel.ToString())); return; }  // nivel
                    // El nivel se pide para TODO (está antes del `Select Case`, InvUsuario.bas:925), pero la
                    // clase no: el `Case otItemsMagicos` es la única rama que NO llama a `ClasePuedeUsarItem`
                    // (:940 contra :982, :1028 y :1092). Un anillo o un orbe se lo pone cualquiera. En esta data
                    // ninguno de los 32 trae ClaseProhibida, así que hoy no cambia nada; el gate sigue la fuente
                    // y no el catálogo, porque un obj.dat editado sí lo notaría.
                    if (!obj.IsMagicItem && obj.ClassForbidden(p.Class)) { session.Send(ServerMessages.LocaleMsg(265)); return; }   // clase prohibida
                    if (obj.IsArmor && obj.RaceForbidden(p.Race)) { session.Send(ServerMessages.LocaleMsg(266)); return; } // raza prohibida (armadura)
                }
                // Conflicto de 2 manos (aplica a todos): arma de 2 manos + escudo equipado -> 263; escudo + arma de 2 manos -> 428.
                if (obj.IsWeapon && obj.TwoHanded == 1 && Equipment.EquippedOfCategory(p, _data, 4) is { TwoHanded: 0 })
                { session.Send(ServerMessages.LocaleMsg(263)); return; }
                if (obj.IsShield && obj.TwoHanded == 0 && Equipment.EquippedOfCategory(p, _data, 1) is { TwoHanded: 1 })
                { session.Send(ServerMessages.LocaleMsg(428)); return; }
                // Saca lo que ocupa el lugar: normalmente la MISMA categoría, y además la contraria en el par
                // arma/nudillos, que en el original se desequipan mutuamente (ver Equipment.LiberaCategoria).
                for (int i = 1; i <= GameLimits.MaxInventorySlots; i++)
                {
                    if (i == slot) continue;
                    var other = p.Inventory[i];
                    var oo = other.IsEmpty ? null : _data.Obj(other.ObjIndex);
                    if (other.Equipped && oo is not null && Equipment.LiberaCategoria(cat, Equipment.Category(oo)))
                    {
                        other.Equipped = false; p.Inventory[i] = other;
                        ClearEquipAnim(p, oo);
                        AplicarBonusDeEquipo(p, oo, poniendose: false);   // el que sale devuelve SU bonus
                        session.Send(ServerMessages.ChangeInventorySlot((byte)i, other.ObjIndex, other.Amount, false, 0f, 1));
                    }
                }
                inv.Equipped = true;
                SetEquipAnim(p, obj);
                AplicarBonusDeEquipo(p, obj, poniendose: true);
                p.Inventory[slot] = inv;
            }
            session.Send(ServerMessages.ChangeInventorySlot(slot, inv.ObjIndex, inv.Amount, inv.Equipped, 0f, 1));
            // VB6 EquiparInvItem gatea el re-broadcast SOLO para arma/armadura con "Montando=0 And Navegando=0"
            // (el arma va oculta y el body es la montura/barca hasta desmontar/desembarcar); casco y escudo,
            // visibles sobre la montura, sí re-broadcastean montado (InvUsuario.bas:1212/1262). Navegando el body
            // es la barca, así que arma/armadura no deben re-vestir el cuerpo real.
            // Los nudillos y los ítems mágicos tampoco se gatean: sus dos `Case` mandan sin preguntar por
            // montura ni barca (InvUsuario.bas:1008 y :960).
            if ((!p.Mounted && !p.Navigating) || obj.IsHelmet || obj.IsShield || obj.IsKnuckles || obj.IsMagicItem)
                BroadcastEquipChange(p, obj, inv.Equipped);
            // ACÁ NO VA NINGUNA LÍNEA DE CONSOLA. Salía un "Equipaste X." / "Desequipaste X." nuestro, y el
            // original no manda nada: `InvUsuario.bas` tiene UN SOLO `WriteConsoleMsg` en todo el archivo (:644,
            // el del oro que no entra), y ni `EquiparInvItem` ni `Desequipar` lo usan. Todo lo que el jugador
            // ve del cambio viaja por los granulares que ya se mandan arriba —el slot y el CharacterChangeSlot—,
            // que es justamente por qué el original usa esos paquetes. Es emisión DE MÁS, la clase que sólo caza
            // el oráculo y sólo si el barrido pasa por equipar.
        });
    }

    /// <summary>
    /// EL BONUS QUE DA UN ÍTEM EQUIPADO, de skill o de atributo. El original suma <c>CuantoAumento</c> al
    /// ponérselo y lo resta al sacárselo, en dos ramas del mismo <c>Select Case Obj.EfectoMagico</c>
    /// (InvUsuario.bas:967/973 al equipar, :741/747 al sacar): <c>ModificaAtributo</c> toca
    /// <c>QueAtributo</c> y <c>ModificaSkill</c> toca <c>QueSkill</c>. Son los anillos y pendientes que llevan
    /// el número en el nombre: Anillo del Guerrero (+20), Pendiente del Arquero (+30), Anillo de Agilidad
    /// (+5), Botas de Celeridad (+10). Sin esto el jugador se pone el ítem que promete +20 y no le cambia nada.
    ///
    /// Se llama en los TRES puntos donde cambia el estado equipado, y el tercero es el que se olvida: cuando
    /// uno se pone un anillo teniendo otro puesto, el que sale tiene que devolver SU bonus. Sin esa resta,
    /// cambiar de anillo diez veces deja diez bonus sumados — y como la skill se guarda en el <c>.chr</c>,
    /// queda para siempre.
    ///
    /// El clamp a 0..255 es una defensa NUESTRA, no del original: la skill vive en un byte y una resta sobre
    /// un personaje que ya venía con el ítem puesto de antes daría la vuelta a 241.
    /// </summary>
    private static void AplicarBonusDeEquipo(Player p, Data.ObjInfo o, bool poniendose)
    {
        const int ModificaAtributo = 2, ModificaSkill = 3;   // VB6 eMagicType
        if (o.CuantoAumento == 0) return;
        int delta = poniendose ? o.CuantoAumento : -o.CuantoAumento;

        // Las dos ramas son EL MISMO `Select Case` del original, una debajo de la otra. Acá vivía sólo la de
        // skill, así que los cuatro ítems de atributo —los únicos con EfectoMagico=2 en todo el install— no
        // hacían nada, aunque los cuatro llevan el número en el nombre.
        switch (o.EfectoMagico)
        {
            case ModificaSkill when o.QueSkill > 0 && o.QueSkill < p.Skills.Length:
                p.Skills[o.QueSkill] = (byte)Math.Clamp(p.Skills[o.QueSkill] + delta, 0, 255);
                break;
            // Sobre el atributo BASE, como el original (`.Stats.UserAtributos(...)`), y no sobre `AttrBonus`:
            // ese array es el de los buffs TEMPORALES, tiene vencimiento propio, y un bonus de equipo puesto ahí
            // se evaporaría solo con el ítem todavía puesto. Además la mitad del código lee el atributo base
            // directo (nivelar, guardar, el paquete de stats), así que el bonus no llegaría a esos caminos.
            case ModificaAtributo when o.QueAtributo > 0 && o.QueAtributo < p.Attributes.Length:
                p.Attributes[o.QueAtributo] =
                    (byte)Math.Clamp(p.Attributes[o.QueAtributo] + delta, 0, Game.GameLimits.MaxAttribute);
                break;
        }
    }

    /// <summary>Difunde al área lo que cambió al equipar o desequipar una pieza, como el original: un
    /// <c>CharacterChangeSlot</c> con SÓLO el slot afectado y, si el ítem trae aura, un <c>AuraToChar</c>
    /// (con aura 0 al desequipar). Antes acá se mandaba el <c>CharacterChange</c> completo — 17 bytes de
    /// payload y los nueve campos, para avisar que cambió uno.
    ///
    /// Los números salen de los call sites de <c>InvUsuario.bas</c>: slot 1 cuerpo, 4 arma, 5 escudo, 6 casco
    /// y <b>8 nudillos</b>; y el tipo de aura 1 arma, 2 cuerpo, 3 escudo, 4 casco. Los nudillos mandan el mismo
    /// <c>WeaponAnim</c> que el arma pero por el slot 8, y su aura es la del ARMA (<c>Arma_Aura</c>, tipo 1),
    /// no una propia: el original escribe <c>.Char.Arma_Aura</c> en las dos ramas (:1011 y :1074).</summary>
    private void BroadcastEquipChange(Player p, Data.ObjInfo o, bool equipped)
    {
        byte aura = equipped ? (byte)Math.Clamp(o.Aura, 0, 255) : (byte)0;
        (short valor, byte slot, byte tipoAura) = o switch
        {
            { IsShield: true } => (p.ShieldAnim, (byte)5, (byte)3),
            { IsHelmet: true } => (p.HelmetAnim, (byte)6, (byte)4),
            { IsWeapon: true } => (p.WeaponAnim, (byte)4, (byte)1),
            { IsKnuckles: true } => (p.WeaponAnim, (byte)8, (byte)1),
            { IsArmor: true } => (p.Body, (byte)1, (byte)2),
            _ => ((short)0, (byte)0, (byte)0),
        };
        if (slot == 0)
        {
            // Categoría sin slot propio en el original (ítems mágicos): sólo cambia el aura, y va con tipo 6
            // (Anillo_Aura, InvUsuario.bas:958-961). Si el ítem tampoco trae aura no hay nada que difundir.
            if (o.Aura > 0 || p.RingAura != 0)
            {
                p.RingAura = aura;
                _world.BroadcastToObservers(p.Pos, ServerMessages.AuraToChar(p.CharIndex, aura, 6));
            }
            return;
        }
        _world.BroadcastToObservers(p.Pos, ServerMessages.CharacterChangeSlot(p.CharIndex, valor, slot));
        // El aura se difunde sólo si hay algo que decir: el original lo gatea con `If Obj.Aura <> 0` al
        // equipar y con `If .Char.X_Aura <> 0` al desequipar (no manda un 0 sobre algo que ya era 0).
        byte previa = tipoAura switch { 1 => p.WeaponAura, 2 => p.BodyAura, 3 => p.ShieldAura, _ => p.HelmetAura };
        if (aura == 0 && previa == 0) return;
        switch (tipoAura)
        {
            case 1: p.WeaponAura = aura; break;
            case 2: p.BodyAura = aura; break;
            case 3: p.ShieldAura = aura; break;
            default: p.HelmetAura = aura; break;
        }
        _world.BroadcastToObservers(p.Pos, ServerMessages.AuraToChar(p.CharIndex, aura, tipoAura));
        // EL SONIDO DEL AURA. El original lo manda en los cuatro caminos de equipar, justo detrás del
        // AuraToChar y adentro del mismo `If Obj.Aura <> 0` (InvUsuario.bas:1014, 1077, 1181, 1270), así que
        // va acá y con las mismas dos condiciones: sólo al EQUIPAR —desequipar no suena— y sólo si el ítem
        // trae aura. Son los 10 ítems de tope del juego; sin esto, ponerse una Espada Mata Dragones era mudo.
        if (equipped && o.Aura > 0 && o.SndEspecial > 0)
            _world.BroadcastToObservers(p.Pos, ServerMessages.PlayWave((short)o.SndEspecial, p.Pos.X, p.Pos.Y));
    }

    /// <summary>
    /// SACAR UN ÍTEM DEL SLOT LO DESEQUIPA PRIMERO, si se va entero. Es la primera línea de
    /// <c>QuitarUserInvItem</c> (InvUsuario.bas:283): <c>If .Amount &lt;= Cantidad And .Equipped = 1 Then
    /// Desequipar</c>. Como esa función es el embudo por el que el original saca objetos del inventario, el
    /// gate corre en TODOS los caminos —tirar al piso, vender al mercader, destruir— sin que ninguno de ellos
    /// tenga que acordarse.
    ///
    /// <para>Acá no existía, y cada camino sacaba el objeto a mano dejando el estado colgado: tirar la
    /// armadura puesta te dejaba el cuerpo vestido con el gráfico de una armadura que ya no tenías, y vender
    /// el casco equipado te dejaba el casco dibujado en la cabeza hasta el próximo login. Con los ítems
    /// mágicos el agujero pasa de cosmético a permanente: el <c>CuantoAumento</c> del anillo se queda sumado
    /// a la skill y la skill se guarda en el <c>.chr</c>.</para>
    ///
    /// <para>La condición es <c>Amount &lt;= cantidad</c>, no <c>== </c>: sacar 5 de un stack de 3 también lo
    /// vacía. Y si sobra aunque sea una unidad el ítem sigue puesto, que es lo que hace el original.</para>
    /// </summary>
    private void DesequiparSiSeVaEntero(ClientSession session, Player p, byte slot, int cantidad)
    {
        var inv = p.Inventory[slot];
        if (!inv.Equipped || inv.Amount > cantidad) return;
        if (_data.Obj(inv.ObjIndex) is not { } o) return;
        if (o.IsMount)                       // VB6 Desequipar Case otMonturas -> DoEquita (te baja del animal)
        {
            if (p.Mounted) AlternarMontura(session, p, slot, o);
            return;
        }
        inv.Equipped = false;
        p.Inventory[slot] = inv;
        ClearEquipAnim(p, o);
        AplicarBonusDeEquipo(p, o, poniendose: false);
        if (o.IsRing && p.Laborando)         // sacarse la herramienta corta la faena (mismo gate que al desequipar)
        {
            p.Laborando = false;
            session.Send(ServerMessages.LocaleMsg(391, "", 1));
        }
        BroadcastEquipChange(p, o, equipped: false);
    }

    private static void SetEquipAnim(Player p, Data.ObjInfo o)
    {
        // VB6: casco y escudo siguen visibles SOBRE la montura, así que se actualizan y broadcastean aunque el
        // jugador esté montado (InvUsuario.bas:1212/1262). Sólo el arma (va oculta montado) y el body de la
        // armadura (el cuerpo es la montura) se gatean con Montando y se reflejan al desmontar (RefreshAppearance).
        if (o.IsShield) p.ShieldAnim = (short)o.Anim;
        else if (o.IsHelmet) p.HelmetAnim = (short)o.Anim;
        // Los NUDILLOS también escriben WeaponAnim, y sin el gate de montura: el original los pone y
        // broadcastea derecho (InvUsuario.bas:1005-1008), a diferencia del arma, que sí lo envuelve en
        // `Montando = 0 And Navegando = 0` (:1068). Es una inconsistencia de la fuente y se porta tal cual.
        else if (o.IsKnuckles) p.WeaponAnim = (short)o.Anim;
        else if (p.Mounted || p.Navigating) return;   // montado/navegando el arma va oculta y el body es la montura/barca
        else if (o.IsWeapon) p.WeaponAnim = (short)o.Anim;
        else if (o.IsArmor && o.Ropaje > 0) p.Body = (short)o.Ropaje;   // VB6 EquiparInvItem: .Char.body = Obj.Ropaje
    }

    private static void ClearEquipAnim(Player p, Data.ObjInfo o)
    {
        if (o.IsShield) p.ShieldAnim = 0;
        else if (o.IsHelmet) p.HelmetAnim = 0;
        else if (o.IsKnuckles) p.WeaponAnim = 0;      // VB6 Desequipar otNudillos: WeaponAnim = NingunArma, sin gate
        else if (p.Mounted || p.Navigating) return;
        else if (o.IsWeapon) p.WeaponAnim = 0;
        else if (o.IsArmor) p.Body = Equipment.NakedBody(p.Race, p.Gender);   // VB6 Desequipar: DarCuerpoDesnudo
    }

    /// <summary>Recalcula toda la apariencia (body + anims de arma/escudo/casco) desde el equipo actual del
    /// personaje. Se usa al entrar al mundo, al desmontar y al revivir, para reflejar lo que tiene equipado (el
    /// VB6 restaura estos campos desde el inventario en ConnectUser / al desmontar / en DarVida).</summary>
    public static void RefreshAppearance(Player p, GameData data)
    {
        p.Body = Equipment.BodyFor(p, data);
        // El arma y los nudillos escriben el MISMO campo, y el original resuelve el empate a favor del arma:
        // pone primero el de los nudillos y después lo pisa con el del arma si la hay (Modulo_UsUaRiOs.bas:93-101).
        p.WeaponAnim = (short)(Equipment.EquippedOfCategory(p, data, 1)?.Anim
                            ?? Equipment.EquippedOfCategory(p, data, 7)?.Anim ?? 0);
        p.ShieldAnim = (short)(Equipment.EquippedOfCategory(p, data, 4)?.Anim ?? 0);
        p.HelmetAnim = (short)(Equipment.EquippedOfCategory(p, data, 3)?.Anim ?? 0);
    }

    // Apariencia de cadáver (Declares.bas): iCuerpoMuerto = 8, iCabezaMuerto = 500.
    public const short CorpseBody = 8;
    public const short CorpseHead = 500;

    /// <summary>Vida de una invocación en ticks del game-loop antes de despawnearse sola (VB6
    /// <c>IntervaloInvocacion</c> = 1001, Server.ini; a 40 ms/tick ≈ 40 s).</summary>
    public const long SummonLifetimeTicks = 1001;

    // Duración de los efectos de hechizo temporales, en vueltas del game-loop (40 ms c/u). El VB6
    // (modHechizos.bas:1336 y :1353) fija `DuracionEfecto = 1200` a los buffs y `= 700` a los debuffs, y esos
    // números son VUELTAS —`DuracionPociones` les resta DeltaTick en cada pasada—, o sea 48 y 28 segundos.
    // Antes acá decían 30000 / 17500 porque se los había leído como SEGUNDOS: una poción de agilidad duraba
    // veinte minutos. Ver fase5/unidades-del-loop.md.
    public const long AttrBuffDurationTicks = 1200;
    public const long AttrDebuffDurationTicks = 700;
    public const long InvisibilityDurationTicks = 500;

    // El casino (VB6 HandleGamble, Protocol.bas:5890). El 47 es lo que hace que la banca gane: a la larga el
    // timbero se queda con el 6% de todo lo apostado. No es 50 y no es redondeo — está escrito así en la fuente.
    public const int NpcTimbero = 12;          // eNPCType.Timbero (Declares.bas:394)
    public const int MaxApuesta = 5000;
    public const int ProbGanarApuesta = 47;    // RandomNumber(1,100) <= 47

    /// <summary>Rango máximo que se puede alcanzar en cada ejército, o 0 si en ese bando no se piden misiones
    /// (VB6 HandleInformation, Protocol.bas:5374 / :5390 / :5405).
    ///
    /// <para>La Milicia corta en <b>7</b> y los otros dos en <b>10</b> — no es el mismo número, aunque las dos
    /// facciones legales compartan la misión. El 0 del default reproduce que el <c>Select Case</c> del original
    /// no tiene rama <c>Else</c>: ante un enlistador de otro bando el juego CALLA, no improvisa.</para></summary>
    public static int TopeDeRango(byte bando) => bando switch
    {
        Player.StatusArmada => 10,
        Player.StatusMilicia => 7,
        Player.StatusCaos => 10,
        _ => 0,
    };

    /// <summary>Traba de las salidas de mapa tras un ataque PvP (VB6 <c>TiempoDeMapeo = 7</c>).
    ///
    /// <para>Acá el 7 SÍ son segundos, al revés que los de arriba: este contador no lo baja el loop de 40 ms sino
    /// <c>PasarSegundo</c>, que cuelga del timer <c>Auditoria</c> de frmMain (Interval = 1000). Dos contadores
    /// del mismo <c>Counters</c> con unidades distintas según quién los decremente — por eso se cita el timer y
    /// no el número.</para></summary>
    public const long TicksAntiMapeo = 7 * (1000L / GameLoop.TickMs);   // 7 s → 175 vueltas

    // Gráficos de barco (Declares.bas iGraficos): iBarca = 84, iFragataFantasmal = 87 (muerto navegando).
    public const short BoatBody = 84;
    public const short GhostShipBody = 87;

    /// <summary>Embarcar / desembarcar — VB6 <c>DoNavega</c> (Trabajo.bas:115), disparado al usar un ítem
    /// <c>otBarcos</c>. Al EMBARCAR exige la skill de Navegación del barco (Barca 40 / Galera 55 / Galeón 75,
    /// LocaleMsg 477) y estar junto al agua (LocaleMsg 394); el cuerpo pasa a la barca —o a la fragata
    /// fantasmal si está muerto— y desmonta. Al DESEMBARCAR exige costa (LocaleMsg 430) y restaura la
    /// apariencia desde el equipo. Emite NavigateToggle + re-broadcast de apariencia.</summary>
    private void DoNavega(ClientSession session, Player p, byte slot, Data.ObjInfo boat)
    {
        if (!p.Navigating)
        {
            if (p.Skills[GameLimits.SkillNavegacion] < boat.MinSkill)
            { session.Send(ServerMessages.LocaleMsg(477, $"{boat.MinSkill}%*{GameLimits.SkillNavegacion}")); return; }
            if (!_data.WaterAdjacent(p.Pos.Map, p.Pos.X, p.Pos.Y))
            { session.Send(ServerMessages.LocaleMsg(394)); return; }   // "¡Debes aproximarte al agua para usar el barco!"
            p.Navigating = true;
            p.BoatSlot = slot;
            p.Body = p.IsAlive ? BoatBody : GhostShipBody;
            p.Head = 0;
            p.WeaponAnim = 0; p.ShieldAnim = 0; p.HelmetAnim = 0;
            if (p.Mounted) { p.Mounted = false; session.Send(ServerMessages.MontateToggle()); }
        }
        else
        {
            // Desembarcar exige costa: alguno de los 5 tiles (el propio + sus 4 vecinos) no debe ser agua.
            bool costa = !_data.IsWater(p.Pos.Map, p.Pos.X, p.Pos.Y)
                      || !_data.IsWater(p.Pos.Map, p.Pos.X + 1, p.Pos.Y) || !_data.IsWater(p.Pos.Map, p.Pos.X - 1, p.Pos.Y)
                      || !_data.IsWater(p.Pos.Map, p.Pos.X, p.Pos.Y + 1) || !_data.IsWater(p.Pos.Map, p.Pos.X, p.Pos.Y - 1);
            if (!costa) { session.Send(ServerMessages.LocaleMsg(430)); return; }   // "Debes estar cerca de una costa"
            p.Navigating = false;
            p.BoatSlot = 0;
            if (p.IsAlive) { p.Head = p.OrigHead; RefreshAppearance(p, _data); }
            else { p.Body = CorpseBody; p.Head = CorpseHead; }
        }
        session.Send(ServerMessages.NavigateToggle());
        _world.BroadcastToObservers(p.Pos, ServerMessages.CharacterChange(p));
    }

    /// <summary>Mata al jugador dejándolo como CADÁVER donde cayó — sin respawn ni teleport (VB6 UserDie,
    /// Modulo_UsUaRiOs.bas:1233-1444): body/head de muerto, anims a cero, limpia estados (parálisis/veneno/
    /// ceguera/meditación), desmonta, y re-broadcastea la apariencia. Estático para que también lo llame el
    /// NpcSystem (muerte por criatura).</summary>
    private const short AnilloEspectral = 1329, AnilloPenumbras = 1330;   // modHechizos.bas:4-5

    /// <summary>¿Tiene el ítem en el inventario? (VB6 TieneObjetos — cuenta el inventario, no el equipo).</summary>
    private static bool HasItem(Player p, short objIndex)
    {
        for (int i = 1; i <= GameLimits.MaxInventorySlots; i++)
            if (p.Inventory[i].ObjIndex == objIndex && p.Inventory[i].Amount > 0) return true;
        return false;
    }

    /// <summary>
    /// Requisito de ANILLO de los 7 hechizos caros (VB6 PuedeLanzar, modHechizos.bas:344-370). Es el último
    /// gate que le faltaba a PuedeLanzar: sin esto un nivel 1 tira Apocalipsis con sólo tener el maná.
    ///
    /// La rama <c>Anillo = 1</c> del original es más rara de lo que parece — pide el Espectral Y ADEMÁS que
    /// NO se tenga el de las Penumbras (<c>If Not TieneObjetos(ESPECTRAL) Or TieneObjetos(PENUMBRAS)</c>), o
    /// sea que llevar los dos encima INHABILITA los hechizos del primero. Se porta tal cual: manda la paridad.
    /// </summary>
    private static bool HasRequiredRing(Player p, Data.SpellInfo spell, out short localeMsg)
    {
        localeMsg = 0;
        switch (spell.Anillo)
        {
            case 1:
                if (!HasItem(p, AnilloEspectral) || HasItem(p, AnilloPenumbras)) { localeMsg = 440; return false; }
                return true;
            case 2:
                if (!HasItem(p, AnilloPenumbras)) { localeMsg = 441; return false; }
                return true;
            default:
                return true;
        }
    }

    /// <summary>
    /// Tira al suelo todo el inventario que "se cae" (VB6 TirarTodo → TirarTodosLosItems,
    /// InvUsuario.bas:2147/2191). Es lo que le pone RIESGO a morir: sin esto, un mapa PK no se distingue de
    /// una ciudad. Dos compuertas, las dos del original:
    ///   1. mapa Pk (MapInfo.json, derivado de battle_mode) — en ciudad no se pierde nada;
    ///   2. el tile no puede ser ZONASEGURA (el `MapData(...).Trigger = 6` del guard de TirarTodo).
    /// La arena queda cubierta por (1)+(2) más el gate de ZONAPELEA del caller.
    ///
    /// El original ramifica newbie (TirarTodosLosItems vs ...NoNewbies), pero en ESTA data ningún objeto trae
    /// la clave `Newbie`, así que las dos ramas sueltan exactamente el mismo conjunto y se implementa una sola.
    /// Tampoco se porta el caso especial del carro minero (CARROMINERO) ni el del pirata con galeón: los dos
    /// dependen de subsistemas que este slice no modela.
    /// </summary>
    /// <summary>REGLA DE LA CASA, no del original: los tipos que NUNCA se caen al morir, los haya marcado o no
    /// el <c>obj.dat</c>. Son los caros de reponer, donde perderlos de un mordisco arruina la partida:
    /// <list type="bullet">
    /// <item><b>otRuna</b> (38) — la Runa de teletransportación, 1 en esta data</item>
    /// <item><b>otMonturas</b> (44) — las 11 monturas, Huargos incluidos</item>
    /// <item><b>otBarcos</b> (31) — Barca, Galera y Galeón</item>
    /// <item><b>otMapa</b> (37) — va por si aparece; esta data no trae ninguno</item>
    /// </list>
    /// Va ADEMÁS del <c>NoSeCae</c> del original, que no se toca: los 5 que ya lo traen —los sacos de donador
    /// y el Anillo Dorado— se siguen respetando por su propio campo.</summary>
    private static bool NuncaSeCae(Data.ObjInfo o) => o.ObjType is 31 or 37 or 38 or 44;

    private static void DropInventoryOnDeath(World world, GameData data, Player p)
    {
        if (!data.IsPkMap(p.Pos.Map)) return;                       // MapInfo(map).Pk = False → no se cae nada
        if (data.IsSafeZone(p.Pos.Map, p.Pos.X, p.Pos.Y)) return;   // MapData(...).Trigger = ZONASEGURA

        for (int slot = 1; slot <= GameLimits.MaxInventorySlots; slot++)
        {
            var inv = p.Inventory[slot];
            if (inv.ObjIndex <= 0 || inv.Amount <= 0) continue;
            var info = data.Obj(inv.ObjIndex);
            if (info is null || !info.DropsOnDeath) continue;
            if (NuncaSeCae(info)) continue;   // la regla de la casa, arriba del NoSeCae del obj.dat
            // El equipo de NOVATO tampoco se cae. Es del original —ahí el `Newbie` del obj.dat lo protege de
            // que se lo roben y de tirarlo—, y acá vuelve a tener efecto porque `IsNewbie` se deriva del nombre
            // (ver ObjInfo.IsNewbie): sin esto el que recién empieza perdía el equipo inicial de un mordisco.
            if (info.IsNewbie) continue;

            var spot = FreeGroundNear(world, p.Pos);
            if (spot is not { } drop) continue;   // no hay tile libre alrededor: el ítem se queda (VB6 NuevaPos = 0)

            // Si estaba PUESTO, devolvé su bonus antes de que el ítem se vaya: el slot se limpia acá abajo y
            // después ya no hay de dónde sacar el EfectoMagico. Ver la nota de UnequipAll.
            if (inv.Equipped) AplicarBonusDeEquipo(p, info, poniendose: false);

            world.SetGround(drop, inv.ObjIndex, inv.Amount);
            world.BroadcastToObservers(drop, ServerMessages.ObjectCreate(drop.X, drop.Y, inv.ObjIndex, inv.Amount));
            p.Inventory[slot] = default;
            p.Connection?.Send(ServerMessages.ChangeInventorySlot((byte)slot, 0, 0, false, 0f, 0));   // slot vacío
        }
    }

    /// <summary>Desequipa TODO (VB6 UserDie:1343-1390: armadura, arma, nudillo, casco, herramienta/anillo,
    /// municiones, ítem mágico y escudo). Corre siempre, incluso en ciudad donde no se dropea nada.
    ///
    /// <para>DEVUELVE EL BONUS, y ahí se separa del original a propósito. El VB6 llama a <c>Desequipar</c> por
    /// cada slot, y esa función resta el <c>CuantoAumento</c> del ítem mágico… salvo que el ítem ya no esté:
    /// <c>TirarTodo</c> corre ANTES (UserDie:1332) y vacía el slot sin desequipar nada, así que el
    /// <c>Desequipar</c> de después se topa con <c>ObjIndex = 0</c> y sale por la puerta de atrás
    /// (InvUsuario.bas:684). Morir en mapa Pk con un Anillo del Guerrero (+20) puesto deja el +20 <b>para
    /// siempre</b>, y como la skill se guarda en el <c>.chr</c>, morir mil veces con anillos la lleva al tope.
    /// Es un agujero del original que corrompe estado persistido y que ningún jugador puede leer como "así era
    /// el juego": acá se cierra, y queda escrito que se cierra.</para></summary>
    private static void UnequipAll(Player p, GameData data)
    {
        for (int slot = 1; slot <= GameLimits.MaxInventorySlots; slot++)
        {
            var inv = p.Inventory[slot];
            if (!inv.Equipped) continue;
            if (data.Obj(inv.ObjIndex) is { } o) AplicarBonusDeEquipo(p, o, poniendose: false);
            inv.Equipped = false; p.Inventory[slot] = inv;
        }
        p.WeaponAnim = 0; p.ShieldAnim = 0; p.HelmetAnim = 0;
    }

    /// <summary>
    /// Primer tile sin ítem en el suelo alrededor de <paramref name="at"/>, en cuadrados crecientes
    /// (VB6 Tilelibre, Modulo_UsUaRiOs.bas:1511, que busca con <c>loopc</c> hasta 15).
    /// El radio importa: al morir se sueltan hasta 20 ítems de una y con un vecindario chico los últimos
    /// no encuentran dónde caer y se pierden — con el radio 1 que tenía esto antes sólo caían 5.
    /// </summary>
    private static WorldPos? FreeGroundNear(World world, WorldPos at)
    {
        if (!world.TryGetGround(at, out _)) return at;
        for (int radius = 1; radius <= 15; radius++)
            for (int dx = -radius; dx <= radius; dx++)
                for (int dy = -radius; dy <= radius; dy++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != radius) continue;   // sólo el borde del anillo
                    int x = at.X + dx, y = at.Y + dy;
                    if (x is < 1 or > 100 || y is < 1 or > 100) continue;
                    var n = new WorldPos(at.Map, (byte)x, (byte)y);
                    if (!world.TryGetGround(n, out _)) return n;
                }
        return null;
    }

    public static void KillPlayer(World world, GameData data, Player p)
    {
        // Morir corta la faena: si no, el cadáver seguiría pescando (el loop sólo mira el flag). El original
        // llega a lo mismo porque `DoTrabajar` cuelga de un usuario vivo.
        p.Laborando = false;
        // Y baja el portal de Lum (VB6 UserDie → ControlarPortalLum(UserIndex, 1), Modulo_UsUaRiOs.bas:1455).
        // No es cosmético: la salida a Intermundia se escribe en el MAPA, así que un portal huérfano queda
        // abierto para todos y para siempre.
        ControlarPortalLum(world, data, p, 1);
        p.Hp = 0;
        p.Deaths++;   // VB6 UserDie (Modulo_UsUaRiOs.bas:1260): justo después de `flags.Muerto = 1`
        p.Body = CorpseBody; p.Head = CorpseHead;
        p.WeaponAnim = 0; p.ShieldAnim = 0; p.HelmetAnim = 0;
        p.ParalyzedUntilTick = 0; p.ParalyzedByCharIndex = 0; p.PoisonedUntilTick = 0; p.BlindedUntilTick = 0; p.Meditating = false; p.Resting = false;  // VB6 UserDie limpia estados (parálisis/veneno/ceguera/meditación/descanso)
        p.Incinerado = false; p.IncineradoGolpes = 0;   // VB6 UserDie (Modulo_UsUaRiOs.bas:1256): morir apaga el fuego
        // Sincronizá el estado-cliente de parálisis/ceguera al morir (VB6 RemoveParalisis manda el toggle): el
        // reconciliador de Program.cs saltea a los muertos, así que sin esto el fantasma queda trabado / con
        // pantalla negra y arrastra el flag ~3s tras revivir. Condicional para no invertir el toggle ParalizeOK.
        if (p.ClientParalyzed) { p.ClientParalyzed = false; p.Connection?.Send(ServerMessages.ParalizeOK()); }
        if (p.ClientBlind) { p.ClientBlind = false; p.Connection?.Send(ServerMessages.BlindNoMore()); }
        p.TradingWith = 0; p.BankOpen = false;   // cierra comercio/banco al morir (un cadáver no comercia ni banquea)
        if (p.Mounted)   // VB6: desmonta al morir (Montando=0 + WriteMontateToggle) y libera el flag de la montura
        {
            p.Mounted = false;
            for (int i = 1; i <= GameLimits.MaxInventorySlots; i++)
            {
                var inv = p.Inventory[i];
                if (inv.Equipped && data.Obj(inv.ObjIndex)?.IsMount == true) { inv.Equipped = false; p.Inventory[i] = inv; }
            }
            p.Connection?.Send(ServerMessages.MontateToggle());
        }
        DropInventoryOnDeath(world, data, p);   // VB6 UserDie:1332 TirarTodo — ANTES de desequipar
        UnequipAll(p, data);                    // VB6 UserDie:1343-1390 — desequipa todo, siempre

        // Borrar el diálogo del muerto ANTES de cambiarle la apariencia (VB6 UserDie:1248): si dijo algo
        // justo antes de morir, la frase le quedaba flotando sobre el cadáver.
        world.BroadcastToObservers(p.Pos, ServerMessages.RemoveCharDialog(p.CharIndex));
        world.BroadcastToObservers(p.Pos, ServerMessages.CharacterChange(p));   // apariencia de cadáver al área (incl. actor)

        // Zona de pelea (arena): el VB6 (UserDie, Modulo_UsUaRiOs.bas:1450 `If TriggerZonaPelea = TRIGGER6_PERMITE
        // Then RevivirUsuario`) NO revive al instante — sólo marca `Resucitando=True`, y DoResucitar (mMainLoop:235)
        // levanta al jugador (DarVida) recién a los ~2500ms. Acá se marca Resucitando; NpcSystem programa el revive
        // tras el delay. Fuera de arena el muerto queda fantasma hasta un revive manual (sacerdote/hechizo/RegresarHogar).
        if (data.IsArena(p.Pos.Map, p.Pos.X, p.Pos.Y))
            p.Resucitando = true;
    }

    /// <summary>Revive al jugador muerto (VB6 DarVida, Modulo_UsUaRiOs.bas:60-119): HP full, restaura la cabeza
    /// viva y la apariencia desde el equipo (armadura Ropaje / cuerpo desnudo + anims), avisa con LocaleMsg(90)
    /// y re-broadcastea. No cambia la posición: revive donde está el cadáver.</summary>
    public static void RevivePlayer(World world, GameData data, Player p)
    {
        p.Hp = p.MaxHp;
        p.Head = p.OrigHead;
        // El sacerdote apaga el fuego además de revivir (VB6 Acciones.bas:319, `.flags.Incinerado = 0`).
        p.Incinerado = false; p.IncineradoGolpes = 0;
        RefreshAppearance(p, data);
        p.Connection?.Send(ServerMessages.UpdateUserStats(p));
        p.Connection?.Send(ServerMessages.LocaleMsg(90));   // "¡Has resucitado!"
        world.BroadcastToObservers(p.Pos, ServerMessages.CharacterChange(p));
    }

    /// <summary>VB6 DeadCheck (InvUsuario.bas:2444): si el jugador está muerto, avisa LocaleMsg(77) y devuelve
    /// true para que el handler aborte. El muerto (fantasma) sólo puede caminar; todo lo demás se bloquea.</summary>
    private static bool DeadCheck(ClientSession session, Player p)
    {
        if (p.IsAlive) return false;
        session.Send(ServerMessages.LocaleMsg(77));
        return true;
    }

    /// <summary>El slot donde meter <paramref name="cantidad"/> unidades de <paramref name="objIndex"/>
    /// (VB6 <c>MeterItemEnInventario</c>, InvUsuario.bas:548).
    ///
    /// <para>Apila SÓLO si el stack resultante entra en el tope. El original lo pide en la condición misma del
    /// primer bucle —<c>.Amount + MiObj.Amount &lt;= MAX_INVENTORY_OBJS</c>— así que un slot que se pasaría no
    /// se elige, y lo que sobra va a OTRO slot. Acá se apilaba sin mirar la capacidad y después se recortaba
    /// con un <c>Math.Min</c>: el excedente se destruía en silencio. No es sólo levantar del piso — este
    /// ayudante lo usan diez caminos (minar, talar, fabricar, fundir, comprar, robar, el premio del donador),
    /// y todos podían comerse ítems.</para>
    ///
    /// <para>La rama que TRUNCA sí existe en el original (el <c>Else</c> que fija el tope), pero es inalcanzable:
    /// para llegar ahí el bucle tuvo que caer en un slot VACÍO, y un slot vacío más una pila que ya viene
    /// acotada nunca se pasa. Por eso no se replica.</para></summary>
    /// <summary>La ranura del inventario donde entra <paramref name="cantidad"/> de ese objeto: apila si el
    /// montón no se pasa del tope del juego, y si no, una vacía. 0 = no hay lugar.
    ///
    /// <para><b>HAY QUE PASARLE LA CANTIDAD.</b> El default de 1 está para los llamadores que agregan una
    /// unidad, y seis lugares lo usaban para después sumar N: el buscador decía que sí porque entraba UNO, y la
    /// pila terminaba por encima de los 10.000 del original y clampeada en 32.767 — que es el paso previo a que
    /// se pierda lo que no entra, exactamente el defecto que tenía la bóveda con su gemelo
    /// <see cref="FindBankSlotFor"/>.</para></summary>
    public static int FindSlotFor(Player p, short objIndex, int cantidad = 1)
    {
        for (int i = 1; i <= GameLimits.MaxInventorySlots; i++)
            if (!p.Inventory[i].IsEmpty && p.Inventory[i].ObjIndex == objIndex
                && p.Inventory[i].Amount + cantidad <= GameLimits.MaxInventoryObjs) return i;   // apila si entra
        for (int i = 1; i <= GameLimits.MaxInventorySlots; i++)
            if (p.Inventory[i].IsEmpty) return i;                                          // first empty
        return 0;                                                                          // no room
    }

    /// <summary>Avisa a los amigos ONLINE que este jugador se va (VB6 <c>CloseUser</c>, TCP.bas:1632 →
    /// <c>ObtenerIndexAmigos(UserIndex, True)</c> → GameLogic.bas:1345-1351). A cada amigo que lo tenga en SU
    /// lista le llegan dos cosas: el <c>LocaleMsg(240, nombre)</c> y un <c>MostrarUbicacion</c> con
    /// <b>mapa/x/y en 0</b>, que es como el original codifica "se desconectó".
    ///
    /// El <c>numAmigo</c> es el slot que el que se va ocupa en la lista DEL QUE RECIBE (el VB6 lo resuelve con
    /// <c>BuscarSlotAmigoNameSlot</c>), 1-based — no el índice en la propia lista.
    ///
    /// La amistad del original NO es simétrica: se avisa sólo a quien te tenga agendado, aunque vos a él no.
    /// Por eso se recorre la lista del OTRO y no la propia.</summary>
    private void AvisarAmigosQueMeVoy(Player p)
    {
        foreach (var otro in _world.Players)
        {
            if (otro.CharIndex == p.CharIndex || otro.Connection is null) continue;
            int slot = otro.Friends.FindIndex(f => string.Equals(f, p.Name, StringComparison.OrdinalIgnoreCase));
            if (slot < 0) continue;
            otro.Connection.Send(ServerMessages.LocaleMsg(240, p.Name));
            otro.Connection.Send(ServerMessages.MostrarUbicacion(p.Name, (byte)(slot + 1), 0, 0, 0));
        }
    }

    /// <summary>La contracara: avisa a los amigos ONLINE que este jugador ENTRÓ (VB6
    /// <c>ObtenerIndexAmigos(Usuario, False)</c>, GameLogic.bas:1322-1345) con <c>LocaleMsg(239, nombre)</c>.
    ///
    /// Y acá la amistad SÍ tiene que ser MUTUA, al revés que en la despedida: el original exige las dos
    /// pertenencias —<c>BuscarSlotAmigoName(Usuario, otro)</c> y <c>BuscarSlotAmigoName(otro, Usuario)</c>—
    /// antes de mandar el aviso. No es simetría por prolijidad: el aviso viaja dentro del <c>If</c> anidado.
    ///
    /// El original recorre <c>For i = 1 To MAXAMIGOS</c> sobre la LISTA DE USUARIOS, que es un error suyo
    /// (debería ser MAXUSERS): sólo mira los primeros slots de conexión. Con la población de un servidor
    /// real la diferencia no se nota, y replicar el bug ataría el aviso al orden de conexión, así que se
    /// recorre a todos los conectados.</summary>
    private void AvisarAmigosQueLlegue(Player p)
    {
        foreach (var otro in _world.Players)
        {
            if (otro.CharIndex == p.CharIndex || otro.Connection is null) continue;
            bool loTengoAgendado = p.Friends.Any(f => string.Equals(f, otro.Name, StringComparison.OrdinalIgnoreCase));
            bool meTieneAgendado = otro.Friends.Any(f => string.Equals(f, p.Name, StringComparison.OrdinalIgnoreCase));
            if (loTengoAgendado && meTieneAgendado)
                otro.Connection.Send(ServerMessages.LocaleMsg(239, p.Name));
        }
    }

    public void OnDisconnect(ClientSession session, string reason)
    {
        var p = session.Player;
        if (p is null) return;
        _loop.Enqueue(() =>
        {
            p.Laborando = false;   // VB6 Cerrar_Usuario (Modulo_UsUaRiOs.bas:1882): desconectar corta la faena
            // La muerte AHORA PERSISTE entre sesiones (VB6 FileIO.bas:1626 guarda Muerto=1): si te desconectás
            // muerto, reconectás muerto. CharacterFile guarda la cabeza VIVA (OrigHead) para que la de cadáver
            // (500) no quede pegada, y SpawnIntoWorld re-aplica la apariencia de cadáver al entrar sin vida.
            // DESPAWNEAR PRIMERO Y GUARDAR DESPUÉS, y el orden es el arreglo: al revés, el `.chr` se escribía
            // con la vida que el familiar tenía al ser INVOCADO —porque es `RemoveNpc` quien le copia la vida
            // real de la criatura— y desconectarse y volver a entrar lo curaba gratis, salteando a la
            // Veterinaria, que es el único sumidero del diseño. El comentario que estaba acá decía que el
            // familiar "se guarda solo": era cierto salvo por el orden, que es justo lo que importaba.
            //
            // Y el motivo original de despawnear sigue en pie: una invocación huérfana, matada tras reconectar,
            // daría exp/oro/loot. (.ToList antes de remover.)
            foreach (var summon in _world.Npcs.Where(n => n.OwnerCharIndex == p.CharIndex).ToList())
            {
                _world.RemoveNpc(summon.CharIndex);
                _world.BroadcastToObservers(summon.Pos, ServerMessages.CharacterRemove(summon.CharIndex));
            }
            try { _db.SaveCharacter(p); }
            catch (Exception ex) { Console.Error.WriteLine($"[save] {p.Name}: {ex.Message}"); }
            // Y los lugares que quedaron ocupados sin criatura: la que esperaba afuera se pierde al desconectar,
            // igual que en el original (VB6 Cerrar_Usuario, Modulo_UsUaRiOs.bas:1428-1440, que barre los tres
            // lugares y termina con `.NroMascotas = 0`). Las mascotas no sobreviven a la sesión.
            for (int i = 1; i <= GameLimits.MaxMascotas; i++) { p.MascotasIndex[i] = 0; p.MascotasType[i] = 0; }
            p.NroMascotas = 0;
            // VB6 TCP.bas:1644 — al área, EXCLUYENDO al que se va (ya no está para verlo).
            _world.BroadcastToObservers(p.Pos, ServerMessages.RemoveCharDialog(p.CharIndex), p.CharIndex);
            ControlarPortalLum(_world, _data, p, 1);   // VB6 CloseUser, TCP.bas:1629 — el portal se va con él
            AvisarAmigosQueMeVoy(p);     // VB6 CloseUser → ObtenerIndexAmigos(_, True)
            _world.HelpRemove(p.Name);   // VB6 TCP.bas:1661 — al desconectar se le limpia la consulta a los GMs
            // El PAR del 489 de la entrada (VB6 TCP.bas:1684). Estaba media puerta puesta: a los GMs les
            // llegaba quién entraba y nunca quién se iba, así que la lista que se armaban mirando la consola
            // sólo crecía.
            BroadcastToAdmins(ServerMessages.LocaleMsg(490, p.Name));
            _world.Remove(p.CharIndex);
            _world.BroadcastToObservers(p.Pos, ServerMessages.CharacterRemove(p.CharIndex)); // tell observers it left view
            _world.Events.Publish(new PlayerDisconnected(p.CharIndex));
            // Soltar el Player de la sesión muerta. Sin esto quedaba colgado del ClientSession —con sus 20
            // slots de inventario, 40 de banco y los arrays de skills y hechizos— y encima cualquier paquete
            // que llegara DESPUÉS pasaba el gate `if (p is null) return` sobre un personaje ya guardado y
            // sacado del mundo, mutándolo sin efecto persistente pero con efecto real sobre el mundo vivo.
            p.Connection = null;
            session.Player = null;
            Console.WriteLine($"[{session.RemoteEndpoint}] guardado y desconectado: {p.Name}");
        });
    }

    private static (byte x, byte y) Step(byte x, byte y, byte heading) => heading switch
    {
        1 => (x, (byte)Math.Max(0, y - 1)),   // North
        2 => ((byte)Math.Min(255, x + 1), y), // East
        3 => (x, (byte)Math.Min(255, y + 1)), // South
        4 => ((byte)Math.Max(0, x - 1), y),   // West
        _ => (x, y),
    };
}
