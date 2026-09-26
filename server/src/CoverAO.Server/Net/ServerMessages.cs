using CoverAO.Protocol;
using CoverAO.Server.Game;

namespace CoverAO.Server.Net;

/// <summary>
/// Builders for server-&gt;client (SC) packets. Each returns the exact bytes for one message,
/// with field layouts taken verbatim from fase0/spec/PROTOCOLO.md. Server-&gt;client is plaintext
/// (no XOR). Orchestration (which messages, in what order, at login) is validated separately via
/// the parity harness; these builders are the byte-accurate primitives that orchestration uses.
/// </summary>
public static class ServerMessages
{
    public static byte[] LoggedSuccessful() =>
        new ByteQueueWriter().WritePacketId(ServerPacketId.LoggedSuccessful).ToArray();

    // Lists are joined with SEPARATOR = vbNullChar ('\0') (Protocol.bas:40); the client Splits on it.
    private const char Sep = '\0';

    /// <summary>El FX de un hechizo casteado sobre el TERRENO (EfectoTerrenoFX, id 93). Layout:
    /// Integer(fx), Byte(x), Byte(y), Integer(loops) — PrepareMessageEfectoTerrenoFX. El original lo manda
    /// cuando el hechizo va a un tile y tiene FXgrh (modHechizos.bas:470) en vez del CreateFX, que necesita un
    /// charIndex: sin personaje al que colgarle la animación, el FX va a coordenadas.
    ///
    /// OJO EL <c>+1</c> EN Y: el original emite `PosCasteadaY + 1`, no la Y casteada. Es intencional —el sprite
    /// del FX se dibuja anclado por abajo, así que un tile más al sur lo centra sobre el objetivo—.</summary>
    public static byte[] EfectoTerrenoFX(short fx, byte x, byte y, short loops) =>
        new ByteQueueWriter().WritePacketId(ServerPacketId.EfectoTerrenoFX)
            .WriteInteger(fx).WriteByte(x).WriteByte(y).WriteInteger(loops).ToArray();

    /// <summary>La PARTÍCULA de un hechizo casteado sobre el TERRENO (EfectoTerrenoParticula, id 92). Layout:
    /// Integer(particula), Byte(x), Byte(y), Long(time) — PrepareMessageEfectoTerrenoParticula (Protocol.bas:15896).
    /// Es el hermano de <see cref="EfectoTerrenoFX"/> para los hechizos cuyo efecto visual es un emisor de
    /// partículas y no un grh animado (Corriente eléctrica 48, Círculo curativo 17, Pregonar 5).
    ///
    /// A diferencia del FX, éste NO lleva el <c>+1</c> en la Y: el original manda `PosCasteadaY` pelada
    /// (modHechizos.bas:469), porque una partícula se ancla al centro del tile y no por abajo.</summary>
    public static byte[] EfectoTerrenoParticula(short particula, byte x, byte y, int time) =>
        new ByteQueueWriter().WritePacketId(ServerPacketId.EfectoTerrenoParticula)
            .WriteInteger(particula).WriteByte(x).WriteByte(y).WriteLong(time).ToArray();

    /// <summary>El indicador de correo pendiente (MensajeSigno, id 95). Un Byte: 1 al recibir correo
    /// (mod_Correos.bas:68) y 0 cuando lo abre y lo lee (Acciones.bas:620). Es el "tenés carta" del cliente,
    /// independiente de cómo se entregue el correo.</summary>
    public static byte[] MensajeSigno(bool recibio) =>
        new ByteQueueWriter().WritePacketId(ServerPacketId.MensajeSigno).WriteByte(recibio ? (byte)1 : (byte)0).ToArray();

    /// <summary>El detalle de una propuesta de alianza o paz recibida (OfferDetails, id 70). Un ASCIIString con
    /// el texto que escribió el clan que ofreció — WriteOfferDetails (Protocol.bas:13704), que el líder pide con
    /// GuildAllianceDetails / GuildPeaceDetails antes de aceptar o rechazar.</summary>
    public static byte[] OfferDetails(string details) =>
        new ByteQueueWriter().WritePacketId(ServerPacketId.OfferDetails).WriteAsciiString(details).ToArray();

    /// <summary>Lo que ve un MIEMBRO COMÚN al abrir el panel de su clan (GuildMemberInfo, id 75): dos
    /// ASCIIString NUL-joined —la lista de todos los clanes y la de los miembros del suyo—
    /// (WriteGuildMemberInfo, Protocol.bas:13976). Es la contraparte del GuildLeaderInfo: el original manda
    /// ESTE al que no es líder, y el panel completo sólo al líder (modGuilds.bas:1258).</summary>
    public static byte[] GuildMemberInfo(IEnumerable<string> guildList, IEnumerable<string> memberList) =>
        new ByteQueueWriter().WritePacketId(ServerPacketId.GuildMemberInfo)
            .WriteAsciiString(string.Join(Sep, guildList))
            .WriteAsciiString(string.Join(Sep, memberList)).ToArray();

    /// <summary>El status (bando) de un personaje cambió (CharStatus, id 94). Layout: Integer(charIndex) +
    /// Byte(status) — PrepareMessageCharStatus (Protocol.bas:16247). El original lo difunde al área cada vez
    /// que alguien cambia de facción (ModFacciones.bas:44/98/204), para que el color del nick de los demás se
    /// actualice sin re-mandar el UpdateTagAndStatus entero.</summary>
    public static byte[] CharStatus(short charIndex, byte status) =>
        new ByteQueueWriter().WritePacketId(ServerPacketId.CharStatus)
            .WriteInteger(charIndex).WriteByte(status).ToArray();

    /// <summary>Borra el diálogo (el chat sobre la cabeza) de un personaje (RemoveCharDialog, id 3). Un solo
    /// Integer con el charIndex. El original lo difunde al área al MORIR (Modulo_UsUaRiOs.bas:1248), al warpear
    /// (:1594) y al desconectarse (TCP.bas:1644, ahí excluyendo al que se va) — sin esto, la última frase que
    /// dijo alguien le queda flotando encima del cadáver o del lugar donde estaba.</summary>
    public static byte[] RemoveCharDialog(short charIndex) =>
        new ByteQueueWriter().WritePacketId(ServerPacketId.RemoveCharDialog).WriteInteger(charIndex).ToArray();

    /// <summary>Borra TODOS los diálogos de la pantalla del que lo recibe (RemoveDialogs, id 2). Sólo el byte
    /// del id, sin payload. Es el hermano del <see cref="RemoveCharDialog"/> y va con él en el warp: el
    /// original manda primero el RemoveCharDialog AL ÁREA (para que los que quedan dejen de ver la frase del
    /// que se fue) y después éste AL QUE WARPEA (para que no se lleve puestos los diálogos del área vieja) —
    /// <c>WarpUserChar</c>, Modulo_UsUaRiOs.bas:1594-1595.
    ///
    /// Este emisor estuvo clasificado como CÓDIGO MUERTO por error: se lo buscó como "WriteRemoveDialogs" y en
    /// la fuente se llama <c>WriteRemoveAllDialogs</c>, así que el grep daba 0 llamadores.</summary>
    public static byte[] RemoveDialogs() =>
        new ByteQueueWriter().WritePacketId(ServerPacketId.RemoveDialogs).ToArray();

    /// <summary>Ubicación de un amigo (MostrarUbicacion, id 97): ASCIIString(nombre) + Byte(numAmigo) +
    /// Integer(mapa) + Byte(x) + Byte(y). El original lo manda al DESCONECTARSE alguien: cada amigo online
    /// suyo recibe <c>LocaleMsg(240, nombre)</c> y este paquete con <b>mapa/x/y en 0</b>, que es como se
    /// codifica "se fue" (<c>ObtenerIndexAmigos(_, True)</c> → GameLogic.bas:1351, desde <c>CloseUser</c>).
    /// El <c>numAmigo</c> es el SLOT que el que se va ocupa en la lista del que recibe, no en la propia.
    ///
    /// La rama de conexión del original (<c>Desconectar=False</c>) manda sólo el <c>LocaleMsg 239</c>: ahí el
    /// Writemostrarubicacion está COMENTADO, y además nadie llama a esa rama. Por eso acá tampoco va.</summary>
    public static byte[] MostrarUbicacion(string nombre, byte numAmigo, short mapa, byte x, byte y) =>
        new ByteQueueWriter().WritePacketId(ServerPacketId.MostrarUbicacion)
            .WriteAsciiString(nombre).WriteByte(numAmigo).WriteInteger(mapa).WriteByte(x).WriteByte(y).ToArray();

    /// <summary>Pausa/reanuda el mundo en el cliente (PauseToggle, id 38). Sin payload: es un TOGGLE, y por eso
    /// el original lo manda DOS veces —una antes y otra después— alrededor del guardado de personajes
    /// (GuardarUsuarios, General.bas:1373/1388), con un LocaleMsg en cada punta.</summary>
    public static byte[] PauseToggle() =>
        new ByteQueueWriter().WritePacketId(ServerPacketId.PauseToggle).ToArray();

    /// <summary>Avisa al cliente que la conexión se termina (Disconnect, id 6). Sin payload. El original lo
    /// manda justo antes de cerrar el socket cuando la salida es LIMPIA —el /SALIR, inmediato o al terminar su
    /// cuenta regresiva (Modulo_UsUaRiOs.bas:1887, General.bas:1345)—, no cuando echa a alguien.</summary>
    public static byte[] Disconnect() =>
        new ByteQueueWriter().WritePacketId(ServerPacketId.Disconnect).ToArray();

    /// <summary>Cambio de UN slot de la apariencia de un personaje (CharacterChangeSlot, id 30). Layout:
    /// Integer(charIndex), Integer(valor), Byte(slot) — PrepareMessageCharacterChangeSlot (Protocol.bas:15002).
    /// El VB6 lo usa al equipar/desequipar en vez de re-mandar el CharacterChange entero: un paquete de 5 bytes
    /// de payload por cosa que cambió, en vez de los 17 del completo.
    ///
    /// Los números de slot salen de los call sites de InvUsuario.bas y NO son el enum de categorías del
    /// inventario: <b>1</b> = cuerpo (armadura), <b>4</b> = arma, <b>5</b> = escudo, <b>6</b> = casco,
    /// <b>8</b> = arma equipada como NUDILLOS (otNudillos manda el mismo WeaponAnim con otro slot).</summary>
    public static byte[] CharacterChangeSlot(short charIndex, short value, byte slot) =>
        new ByteQueueWriter().WritePacketId(ServerPacketId.CharacterChangeSlot)
            .WriteInteger(charIndex).WriteInteger(value).WriteByte(slot).ToArray();

    /// <summary>La pieza que cambió de un JUGADOR. La del GM con /INVISIBLE viaja en 0, como el resto de su
    /// apariencia (ver <see cref="CharacterChange(Player)"/>): equiparse no lo destapa. La dirección (slot 3) no es
    /// apariencia y va igual.</summary>
    public static byte[] CharacterChangeSlot(Player p, short value, byte slot) =>
        CharacterChangeSlot(p.CharIndex, slot == 3 ? value : Tapado(p, value), slot);

    /// <summary>El aura que rodea a un personaje por el equipo que lleva (AuraToChar, id 103). Layout:
    /// Integer(charIndex), Byte(aura), Byte(tipo) — PrepareMessageAuraToChar (Protocol.bas:17470).
    /// El tipo dice a QUÉ pieza corresponde: <b>1</b> arma, <b>2</b> cuerpo, <b>3</b> escudo, <b>4</b> casco,
    /// <b>6</b> anillo. Con <c>aura = 0</c> se apaga (es lo que manda al desequipar).</summary>
    public static byte[] AuraToChar(short charIndex, byte aura, byte tipo) =>
        new ByteQueueWriter().WritePacketId(ServerPacketId.AuraToChar)
            .WriteInteger(charIndex).WriteByte(aura).WriteByte(tipo).ToArray();

    /// <summary>La solicitud de un aspirante a clan (ShowUserRequest, id 85). Un solo ASCIIString con el
    /// "Detalle" que el aspirante escribió al postularse — WriteShowUserRequest (Protocol.bas:14152), que el
    /// líder pide con GuildRequestJoinerInfo para decidir si lo acepta.</summary>
    public static byte[] ShowUserRequest(string details) =>
        new ByteQueueWriter().WritePacketId(ServerPacketId.ShowUserRequest).WriteAsciiString(details).ToArray();

    /// <summary>Cartel de respuesta de un GM (SendMsgBox, id 56). Layout: ASCIIString(mensaje) + Byte(modo)
    /// — PrepareMessageSendMsgBox (Protocol.bas:15129). Lo usa <c>ResponderGM</c> para contestarle en pantalla
    /// a quien mandó una consulta con /GM. Distinto de ShowMessageBox (22), que lleva un Boolean de más.</summary>
    public static byte[] SendMsgBox(string message, byte modo = 0) =>
        new ByteQueueWriter().WritePacketId(ServerPacketId.SendMsgBox)
            .WriteAsciiString(message).WriteByte(modo).ToArray();

    /// <summary>Lista de consultas a los GMs (ShowSOSForm, id 84) — la respuesta a /SHOW SOS.
    /// Layout: un solo ASCIIString con las entradas <c>[NOMBRE]-HH:MM:SS-Tipo=</c> unidas por NUL
    /// (WriteShowSOSForm, Protocol.bas:14241). El original concatena "entrada + NUL" y después le saca el
    /// último carácter, que da exactamente un join por NUL — con la cola vacía, la cadena vacía.</summary>
    public static byte[] ShowSosForm(IEnumerable<string> entries) =>
        new ByteQueueWriter().WritePacketId(ServerPacketId.ShowSOSForm)
            .WriteAsciiString(string.Join(Sep, entries)).ToArray();

    /// <summary>Online user-name list (id 85), for the GM /LISTUSU command. Names joined by NUL.</summary>
    public static byte[] UserNameList(IEnumerable<string> names) =>
        new ByteQueueWriter().WritePacketId(ServerPacketId.UserNameList)
            .WriteAsciiString(string.Join(Sep, names)).ToArray();

    /// <summary>Guild news + enemy/ally lists (id 69 = guildNews). Layout: ASCIIString(news),
    /// ASCIIString(enemies), ASCIIString(allies) — enemy/ally names NUL-joined (WriteGuildNews, Protocol.bas:13659).</summary>
    public static byte[] GuildNews(string news, IEnumerable<string> enemies, IEnumerable<string> allies) =>
        new ByteQueueWriter().WritePacketId(ServerPacketId.guildNews)
            .WriteAsciiString(news)
            .WriteAsciiString(string.Join(Sep, enemies))
            .WriteAsciiString(string.Join(Sep, allies)).ToArray();

    /// <summary>Pending alliance/peace proposal lists (id 71/72) — one NUL-joined ASCIIString of the guild
    /// names that offered.</summary>
    public static byte[] AlianceProposalsList(IEnumerable<string> guilds) =>
        new ByteQueueWriter().WritePacketId(ServerPacketId.AlianceProposalsList).WriteAsciiString(string.Join(Sep, guilds)).ToArray();
    public static byte[] PeaceProposalsList(IEnumerable<string> guilds) =>
        new ByteQueueWriter().WritePacketId(ServerPacketId.PeaceProposalsList).WriteAsciiString(string.Join(Sep, guilds)).ToArray();

    /// <summary>The clan-leader management panel (id 74 = GuildLeaderInfo). Four NUL-joined ASCIIStrings: all guild
    /// names, this guild's members, its news, and pending join requests (WriteGuildLeaderInfo, Protocol.bas:13918).</summary>
    public static byte[] GuildLeaderInfo(IEnumerable<string> guildList, IEnumerable<string> memberList,
        string news, IEnumerable<string> joinRequests) =>
        new ByteQueueWriter().WritePacketId(ServerPacketId.GuildLeaderInfo)
            .WriteAsciiString(string.Join(Sep, guildList))
            .WriteAsciiString(string.Join(Sep, memberList))
            .WriteAsciiString(news)
            .WriteAsciiString(string.Join(Sep, joinRequests)).ToArray();

    /// <summary>A guild's detail sheet (id 76 = GuildDetails), for the clan info window. Layout from WriteGuildDetails
    /// (Protocol.bas:14072): name, founder, foundationDate, leader, URL, memberCount(int), electionsOpen(bool),
    /// alignment, enemiesCount(int), alliesCount(int), antifactionPoints, codex, guildDesc. Founder/date y
    /// antifaction no se modelan en el slice → vacíos/0; URL y códex SÍ (los setea el líder).</summary>
    public static byte[] GuildDetails(Guild g) =>
        new ByteQueueWriter().WritePacketId(ServerPacketId.GuildDetails)
            .WriteAsciiString(g.Name)
            .WriteAsciiString("").WriteAsciiString("")   // founder, foundationDate
            .WriteAsciiString(g.LeaderName)
            .WriteAsciiString(g.Url)
            .WriteInteger((short)g.Members.Count)
            .WriteBoolean(g.ElectionsOpen)
            .WriteAsciiString(Game.Guild.NombreDeAlineacion(g.Alignment))
            .WriteInteger((short)g.Enemies.Count)
            .WriteInteger((short)g.Allies.Count)
            .WriteAsciiString("0")                       // antifactionPoints
            .WriteAsciiString(g.Codex)
            .WriteAsciiString(g.Description).ToArray();

    /// <summary>Blacksmith weapon recipe list (id 48 = BlacksmithWeapons). Per item: objIndex, iron, silver, gold, objIndex —
    /// the client shows the name from the first index and stores the last for the craft request
    /// (HandleBlacksmithWeapons, Cliente/Protocol.bas:2754). Obj.Numero in the VB6 is the object index.</summary>
    public static byte[] BlacksmithWeapons(IReadOnlyList<Data.ObjInfo> items) => BlacksmithList(ServerPacketId.BlacksmithWeapons, items);
    public static byte[] BlacksmithArmors(IReadOnlyList<Data.ObjInfo> items) => BlacksmithList(ServerPacketId.BlacksmithArmors, items);

    /// <summary>Lista de CASCOS forjables (id 50) — mismo layout que las otras listas del herrero. El VB6 manda
    /// una lista por tipo; antes el slice metía cascos y escudos dentro de BlacksmithArmors.</summary>
    public static byte[] BlacksmithHelmets(IReadOnlyList<Data.ObjInfo> items) => BlacksmithList(ServerPacketId.BlacksmithHelmet, items);

    /// <summary>Lista de ESCUDOS forjables (id 51).</summary>
    public static byte[] BlacksmithShields(IReadOnlyList<Data.ObjInfo> items) => BlacksmithList(ServerPacketId.BlacksmithShield, items);

    private static byte[] BlacksmithList(ServerPacketId id, IReadOnlyList<Data.ObjInfo> items)
    {
        var w = new ByteQueueWriter().WritePacketId(id).WriteInteger((short)items.Count);
        foreach (var o in items)
            w.WriteInteger(o.Index).WriteInteger((short)o.IngotIron).WriteInteger((short)o.IngotSilver)
             .WriteInteger((short)o.IngotGold).WriteInteger(o.Index);
        return w.ToArray();
    }

    // Carpenter/Alchemy lists share the shape: count + per item (objIndex, one material, objIndex).
    // (Tailor differs — 5 ints/item — so it has its own builder below.)
    private static byte[] CraftList(ServerPacketId id, IReadOnlyList<Data.ObjInfo> items, Func<Data.ObjInfo, int> material)
    {
        var w = new ByteQueueWriter().WritePacketId(id).WriteInteger((short)items.Count);
        foreach (var o in items)
            w.WriteInteger(o.Index).WriteInteger((short)material(o)).WriteInteger(o.Index);
        return w.ToArray();
    }

    /// <summary>A trainer NPC's tameable-creature list (id 68). One NUL-joined ASCIIString
    /// (WriteTrainerCreatureList, Protocol.bas:13610).</summary>
    public static byte[] TrainerCreatureList(IEnumerable<string> creatures) =>
        new ByteQueueWriter().WritePacketId(ServerPacketId.TrainerCreatureList)
            .WriteAsciiString(string.Join(Sep, creatures)).ToArray();

    public static byte[] CarpenterObjects(IReadOnlyList<Data.ObjInfo> items) => CraftList(ServerPacketId.CarpenterObjects, items, o => o.Wood);
    public static byte[] AlquimiaObjects(IReadOnlyList<Data.ObjInfo> items) => CraftList(ServerPacketId.AlquimiaObjects, items, o => o.Roots);

    /// <summary>Tailor recipe list (id 53). Unlike carpentry/alchemy, WriteSastreObjects (Protocol.bas:13198)
    /// emits FIVE integers per item: objIndex, wolf-skin, brown-bear-skin, polar-bear-skin, objIndex.</summary>
    public static byte[] SastreObjects(IReadOnlyList<Data.ObjInfo> items)
    {
        var w = new ByteQueueWriter().WritePacketId(ServerPacketId.SastreObjects).WriteInteger((short)items.Count);
        foreach (var o in items)
            w.WriteInteger(o.Index).WriteInteger((short)o.SkinWolf).WriteInteger((short)o.SkinBrownBear)
             .WriteInteger((short)o.SkinPolarBear).WriteInteger(o.Index);
        return w.ToArray();
    }

    /// <summary>The account character-selection list. Layout from WriteAddPj (Protocol.bas:15311).</summary>
    public static byte[] AddPj(string accountName, IReadOnlyList<CharacterSummary> characters)
    {
        var w = new ByteQueueWriter()
            .WritePacketId(ServerPacketId.AddPJ)
            .WriteAsciiString(accountName)
            .WriteByte((byte)characters.Count);
        foreach (var c in characters)
            w.WriteAsciiString(c.Name)
             .WriteInteger(c.Head).WriteInteger(c.Body).WriteInteger(c.Helmet)
             .WriteInteger(c.Weapon).WriteInteger(c.Shield)
             .WriteByte(c.Level).WriteByte(c.Class)
             .WriteInteger(c.Map).WriteByte(c.Color).WriteBoolean(c.GameMaster);
        return w.ToArray();
    }

    /// <summary>Tells the client which form to open (1 = account/character-selection form).</summary>
    public static byte[] AbrirFormularios(byte formulario) =>
        new ByteQueueWriter().WritePacketId(ServerPacketId.AbrirFormularios).WriteByte(formulario).ToArray();

    /// <summary>One inventory slot. Layout from WriteChangeInventorySlot (Protocol.bas:12716).</summary>
    public static byte[] ChangeInventorySlot(byte slot, short objIndex, short amount, bool equipped, float salePrice, byte usable) =>
        new ByteQueueWriter()
            .WritePacketId(ServerPacketId.ChangeInventorySlot)
            .WriteByte(slot).WriteInteger(objIndex).WriteInteger(amount)
            .WriteBoolean(equipped).WriteSingle(salePrice).WriteByte(usable)
            .ToArray();

    /// <summary>One spell slot. Layout from WriteChangeSpellSlot (Protocol.bas:12803).</summary>
    public static byte[] ChangeSpellSlot(byte slot, short spellId) =>
        new ByteQueueWriter()
            .WritePacketId(ServerPacketId.ChangeSpellSlot)
            .WriteByte(slot).WriteInteger(spellId)
            .ToArray();

    /// <summary>The player's skills (SendSkills, id 67): 27 bytes (eSkill 1..27).</summary>
    public static byte[] SendSkills(byte[] skills)
    {
        var w = new ByteQueueWriter().WritePacketId(ServerPacketId.SendSkills);
        for (int i = 1; i <= GameLimits.NumSkills; i++) w.WriteByte(i < skills.Length ? skills[i] : (byte)0);
        return w.ToArray();
    }

    /// <summary>
    /// Texto localizado SOBRE LA CABEZA de un personaje (ChatOverHeadLocale, id 19). El campo
    /// <c>index</c> está sobrecargado según <c>modo</c>: con modo 1/4 es un id de locale, y con
    /// <b>modo 2 es el NÚMERO DE DAÑO</b> — es el "-N" flotante característico de AO.
    /// VB6 SistemaCombate.bas:590: <c>WriteChatOverHeadLocale(UserIndex, charIndex, daño, 2)</c>,
    /// que va junto al mensaje de texto, no en su lugar.
    /// </summary>
    public static byte[] ChatOverHeadLocale(short charIndex, int index, byte modo) =>
        new ByteQueueWriter().WritePacketId(ServerPacketId.ChatOverHeadLocale)
            .WriteInteger(charIndex).WriteLong(index).WriteByte(modo).ToArray();

    /// <summary>El número de daño flotante sobre la cabeza (modo 2 de ChatOverHeadLocale).</summary>
    public static byte[] DamageOverHead(short charIndex, int damage) => ChatOverHeadLocale(charIndex, damage, 2);

    /// <summary>
    /// El golpe de una CRIATURA, con la parte del cuerpo donde pegó — VB6 SistemaCombate.bas:588,
    /// <c>WriteLocaleMsg(UserIndex, lugar, daño, 2)</c>.
    ///
    /// <para>El modo 2 del <c>LocaleMsg</c> es un formato aparte: el <b>id ES la parte del cuerpo</b> (1..6,
    /// <c>PartesCuerpo</c>) y el argumento es el daño. El texto no viaja: lo arma el cliente pegando
    /// <c>locale_gui_es.ind</c> 497 ("La criatura") + 367..372 (la parte) + el número, en fuente 2.</para>
    ///
    /// <para>La zona ya se tiraba para decidir QUÉ absorbe el golpe —casco si es la cabeza, armadura y escudo
    /// si no (<see cref="Game.Combat.AplicarDefensa"/>)— pero se quedaba adentro del servidor: el jugador
    /// recibía un texto nuestro sin la parte del cuerpo, así que la mecánica estaba pero no se veía.</para>
    /// </summary>
    /// <param name="zona">1 cabeza, 2 pierna izq, 3 pierna der, 4 brazo der, 5 brazo izq, 6 torso.</param>
    public static byte[] GolpeDeCriatura(int zona, int dano) =>
        LocaleMsg((short)zona, dano.ToString(System.Globalization.CultureInfo.InvariantCulture), mode: 2);

    /// <summary>Guild chat line (GuildChat, id 21): a single ASCII string.</summary>
    public static byte[] GuildChat(string chat) =>
        new ByteQueueWriter().WritePacketId(ServerPacketId.GuildChat).WriteAsciiString(chat).ToArray();

    /// <summary>EjecutarAccion (id 106): Byte(Accion) + ASCIIString(Extra). VB6 WriteEjecutarAccion
    /// (Protocol.bas:17613). Accion=4 = una línea de novedades/actualización (SendUpdate en ConnectUser).</summary>
    public static byte[] EjecutarAccion(byte accion, string extra = "") =>
        new ByteQueueWriter().WritePacketId(ServerPacketId.EjecutarAccion).WriteByte(accion).WriteAsciiString(extra).ToArray();

    /// <summary>Play a sound effect. Layout from PrepareMessagePlayWave (Protocol.bas:14537): wave, X, Y.</summary>
    public static byte[] PlayWave(short wave, byte x, byte y) =>
        new ByteQueueWriter().WritePacketId(ServerPacketId.PlayWave)
            .WriteInteger(wave).WriteByte(x).WriteByte(y).ToArray();

    /// <summary>An item appears on a ground tile. Layout: X, Y, ObjIndex, Amount.</summary>
    public static byte[] ObjectCreate(byte x, byte y, short objIndex, short amount) =>
        new ByteQueueWriter().WritePacketId(ServerPacketId.ObjectCreate)
            .WriteByte(x).WriteByte(y).WriteInteger(objIndex).WriteInteger(amount).ToArray();

    /// <summary>An item is removed from a ground tile. Layout: X, Y.</summary>
    public static byte[] ObjectDelete(byte x, byte y) =>
        new ByteQueueWriter().WritePacketId(ServerPacketId.ObjectDelete)
            .WriteByte(x).WriteByte(y).ToArray();

    /// <summary>Fuerza al cliente a mover su personaje en una dirección (VB6 <c>WriteForceCharMove</c>). Lo usa
    /// <c>MoveNPCChar</c> cuando un NPC pisa el tile de un jugador MUERTO: no lo atraviesa, lo INTERCAMBIA de
    /// lugar — al cadáver lo empuja al tile del que venía el NPC, con el heading invertido.</summary>
    public static byte[] ForceCharMove(byte heading) =>
        new ByteQueueWriter().WritePacketId(ServerPacketId.ForceCharMove).WriteByte(heading).ToArray();

    /// <summary>Un tile cambió de bloqueado (VB6 <c>PrepareMessageBlockPosition</c>, Protocol.bas:14750:
    /// WriteByte id, WriteByte X, WriteByte Y, WriteBoolean Blocked). Lo emite <c>Bloquear</c> (General.bas:112)
    /// cuando una puerta se abre o se cierra; con <c>toMap = True</c> va a TODOS los usuarios del mapa, no sólo a
    /// los del área — el cliente mantiene su propio mapa de bloqueos y necesita enterarse aunque esté lejos.</summary>
    public static byte[] BlockPosition(byte x, byte y, bool blocked) =>
        new ByteQueueWriter().WritePacketId(ServerPacketId.BlockPosition)
            .WriteByte(x).WriteByte(y).WriteBoolean(blocked).ToArray();

    public static byte[] CommerceInit() =>
        new ByteQueueWriter().WritePacketId(ServerPacketId.CommerceInit).ToArray();

    public static byte[] CommerceEnd() =>
        new ByteQueueWriter().WritePacketId(ServerPacketId.CommerceEnd).ToArray();

    public static byte[] UpdateGold(int gold) =>
        new ByteQueueWriter().WritePacketId(ServerPacketId.UpdateGold).WriteLong(gold).ToArray();

    /// <summary>One merchant stock slot. Layout: slot, Amount, price(single), ObjIndex.</summary>
    public static byte[] ChangeNpcInventorySlot(byte slot, short amount, float price, short objIndex) =>
        new ByteQueueWriter().WritePacketId(ServerPacketId.ChangeNPCInventorySlot)
            .WriteByte(slot).WriteInteger(amount).WriteSingle(price).WriteInteger(objIndex).ToArray();

    /// <summary>Abre una ventana del banco (VB6 WriteBankInit, Protocol.bas:11636). Layout: goliath(byte),
    /// oro de la bóveda(long), ítems de la bóveda(byte). El panel del oro (<paramref name="goliath"/>) trae los dos
    /// números; la bóveda de ítems los manda en CERO, como el original.</summary>
    public static byte[] BankInit(bool goliath, long bankGold = 0, byte bankItemCount = 0) =>
        new ByteQueueWriter().WritePacketId(ServerPacketId.BankInit)
            .WriteByte((byte)(goliath ? 1 : 0)).WriteLong(goliath ? (int)bankGold : 0)
            .WriteByte(goliath ? bankItemCount : (byte)0).ToArray();

    public static byte[] BankEnd() =>
        new ByteQueueWriter().WritePacketId(ServerPacketId.BankEnd).ToArray();

    public static byte[] BankOk() =>
        new ByteQueueWriter().WritePacketId(ServerPacketId.BankOK).ToArray();

    /// <summary>One bank vault slot. Layout: slot, ObjIndex, Amount, Valor(long).</summary>
    public static byte[] ChangeBankSlot(byte slot, short objIndex, short amount, long value) =>
        new ByteQueueWriter().WritePacketId(ServerPacketId.ChangeBankSlot)
            .WriteByte(slot).WriteInteger(objIndex).WriteInteger(amount).WriteLong((int)value).ToArray();

    /// <summary>Announces the per-session XOR key (the <c>redundance</c> byte).</summary>
    public static byte[] Logged(byte redundance) =>
        new ByteQueueWriter().WritePacketId(ServerPacketId.Logged).WriteByte(redundance).ToArray();

    public static byte[] ChangeMap(short map) =>
        new ByteQueueWriter().WritePacketId(ServerPacketId.ChangeMap).WriteInteger(map).ToArray();

    public static byte[] UserIndexInServer(short userIndex) =>
        new ByteQueueWriter().WritePacketId(ServerPacketId.UserIndexInServer).WriteInteger(userIndex).ToArray();

    public static byte[] UserCharIndexInServer(short charIndex) =>
        new ByteQueueWriter().WritePacketId(ServerPacketId.UserCharIndexInServer).WriteInteger(charIndex).ToArray();

    public static byte[] PosUpdate(byte x, byte y) =>
        new ByteQueueWriter().WritePacketId(ServerPacketId.PosUpdate).WriteByte(x).WriteByte(y).ToArray();

    /// <summary>Another character moved (broadcast to observers). Layout: CharIndex, X, Y.</summary>
    public static byte[] CharacterMove(short charIndex, byte x, byte y) =>
        new ByteQueueWriter().WritePacketId(ServerPacketId.CharacterMove)
            .WriteInteger(charIndex).WriteByte(x).WriteByte(y).ToArray();

    /// <summary>Se saca un personaje de la pantalla. Layout: CharIndex, Desvanecido(bool).
    ///
    /// <para><b>El booleano decide si el cliente hace el FANTASMA</b>: <c>HandleCharacterRemove</c>
    /// (Cliente/Codigo/Protocol.bas:1822) sólo llama a <c>CrearFantasma</c> si viene en <c>True</c>; con
    /// <c>False</c> el personaje desaparece de golpe. En el original vale <c>True</c> en todos lados salvo
    /// uno: <c>EraseNPCChar</c> lo manda en <c>True</c> (MODULO_NPCs.bas:672) y <c>EraseUserChar</c> lo
    /// recibe de quien lo llame — <c>True</c> al cambiar de mapa (Modulo_UsUaRiOs.bas:1597) y <c>False</c>
    /// al desconectar, después de guardar (TCP.bas:1650).</para>
    ///
    /// <para>El parámetro NO tiene default a propósito. Lo tenía —en <c>false</c>— y las 18 emisiones se
    /// escribieron sin pensarlo, así que el mundo entero mandaba lo contrario de lo que manda el original.
    /// El oráculo no puede verlo: compara QUÉ paquetes vuelven, no sus bytes.</para>
    ///
    /// <para>Los avisos de que algo salió de VISTA son nuestros: el original no los manda desde su sistema
    /// de áreas (no hay un solo <c>PrepareMessageCharacterRemove</c> en ModAreas.bas). Van en <c>true</c>,
    /// que es lo que el cliente ya venía dibujando.</para></summary>
    public static byte[] CharacterRemove(short charIndex, bool faded) =>
        new ByteQueueWriter().WritePacketId(ServerPacketId.CharacterRemove)
            .WriteInteger(charIndex).WriteBoolean(faded).ToArray();

    /// <summary>A character's nick tag / faction status changed (nick colour + donor flag). Layout id 82.</summary>
    /// <summary>VB6 <c>RefreshCharStatus</c> (Modulo_UsUaRiOs.bas:222-227): con <c>showName</c> en false NO manda
    /// el nick sino cadena VACÍA, y además status y donador en 0 — o sea que el personaje pierde el nombre Y el
    /// color del bando. Es lo que hace el <c>/SHOWNAME</c> de un GM. El layout no cambia, sólo los valores.</summary>
    /// <summary>
    /// EL TAG QUE VE EL RESTO: <c>"Nick &lt;Clan&gt;"</c>, no el nick pelado. El original lo arma así
    /// (Modulo_UsUaRiOs.bas:215-223: <c>ClanTag = " &lt;" &amp; GuildName &amp; "&gt;"</c>) y el cliente lo
    /// parte para dibujar el clan como segundo renglón debajo del nombre. Acá se mandaba sólo
    /// <c>p.Name</c>, así que el clan no aparecía sobre la cabeza de nadie — que es la señal con la que se
    /// reconoce a un aliado de un enemigo antes de que empiece la pelea.
    /// </summary>
    private static string TagDe(Player p) =>
        string.IsNullOrEmpty(p.GuildName) ? p.Name : $"{p.Name} <{p.GuildName}>";

    public static byte[] UpdateTagAndStatus(Player p) =>
        new ByteQueueWriter().WritePacketId(ServerPacketId.UpdateTagAndStatus)
            .WriteInteger(p.CharIndex)
            .WriteAsciiString(p.ShowName ? TagDe(p) : "")
            .WriteByte(p.ShowName ? p.FactionStatus : (byte)0)
            .WriteByte((byte)(p.ShowName && p.Donador ? 1 : 0)).ToArray();   // VB6 UpdateTagAndStatus field Donador (PROTOCOLO §82)

    /// <summary>A character changed appearance/heading. Layout from CharacterChange (id 29).
    ///
    /// <para>El GM con /INVISIBLE sale con cuerpo, cabeza y equipo en 0 (VB6 DoAdminInvisible, Trabajo.bas:1202-1206),
    /// que el cliente no dibuja. Se tapa acá y no en el jugador: su apariencia real sigue al día —se equipa, navega,
    /// se transforma— y vuelve entera cuando se hace visible. El original, en cambio, pierde el arma, el escudo y el
    /// casco al volver (no los restaura) y se destapa solo si se pone una armadura o si navega.</para></summary>
    public static byte[] CharacterChange(Player p) =>
        new ByteQueueWriter().WritePacketId(ServerPacketId.CharacterChange)
            .WriteInteger(p.CharIndex)
            .WriteInteger(Tapado(p, p.Body)).WriteInteger(Tapado(p, p.Head)).WriteByte(p.Heading)
            .WriteInteger(Tapado(p, p.WeaponAnim)).WriteInteger(Tapado(p, p.ShieldAnim)).WriteInteger(Tapado(p, p.HelmetAnim))
            .WriteInteger(0).WriteInteger(0)   // FX / FXLoops
            .ToArray();

    /// <summary>La parte de la apariencia que viaja: 0 si el GM está con /INVISIBLE.</summary>
    private static short Tapado(Player p, short valor) => p.AdminInvisible ? (short)0 : valor;

    /// <summary>Una criatura que GIRA (VB6 ChangeNPCChar, MODULO_NPCs.bas:624): su cuerpo y su cabeza, la dirección
    /// nueva y el efecto en 0. El original manda también el arma, el escudo y el casco en 0, que en SU cliente quiere
    /// decir "no cambia"; este cliente toma el 0 como "nada", así que va el equipo de la ficha, que se ve igual.</summary>
    public static byte[] CharacterChange(Npc n) =>
        new ByteQueueWriter().WritePacketId(ServerPacketId.CharacterChange)
            .WriteInteger(n.CharIndex)
            .WriteInteger(n.Template.Body).WriteInteger(n.Template.Head).WriteByte(n.Heading)
            .WriteInteger((short)n.Template.WeaponAnim).WriteInteger((short)n.Template.ShieldAnim)
            .WriteInteger((short)n.Template.HelmetAnim)
            .WriteInteger(0).WriteInteger(0)   // FX / FXLoops
            .ToArray();

    public static byte[] Pong(int time) =>
        new ByteQueueWriter().WritePacketId(ServerPacketId.Pong).WriteLong(time).ToArray();

    // Client visual toggles — ID-only (the client flips its own state on receipt).
    public static byte[] RestOK() => new ByteQueueWriter().WritePacketId(ServerPacketId.RestOK).ToArray();
    /// <summary>Weather toggle (id 46): clima byte (1 = rain, 0 = clear). Layout from WriteRainToggle (Protocol.bas:14698).</summary>
    public static byte[] RainToggle(byte clima) => new ByteQueueWriter().WritePacketId(ServerPacketId.RainToggle).WriteByte(clima).ToArray();
    public static byte[] MeditateToggle() => new ByteQueueWriter().WritePacketId(ServerPacketId.MeditateToggle).ToArray();
    public static byte[] MontateToggle() => new ByteQueueWriter().WritePacketId(ServerPacketId.MontateToggle).ToArray();

    /// <summary>Toggles the client's "navigating" state (id 4) — VB6 WriteNavigateToggle (Protocol.bas:11472):
    /// sólo el byte del id, sin payload. Se emite al embarcar y al desembarcar.</summary>
    public static byte[] NavigateToggle() => new ByteQueueWriter().WritePacketId(ServerPacketId.NavigateToggle).ToArray();

    /// <summary>Make a character (in)visible for observers (id 63). Layout: Integer(CharIndex), Boolean(Invisible)
    /// — WriteSetInvisible (Protocol.bas:14411). Unlike CharacterRemove it keeps the char so GMs still see it.</summary>
    public static byte[] SetInvisible(short charIndex, bool invisible) =>
        new ByteQueueWriter().WritePacketId(ServerPacketId.SetInvisible)
            .WriteInteger(charIndex).WriteBoolean(invisible).ToArray();

    // Combat-state toggles/clears — all ID-only packets (no fields), per fase0/spec + the VB6 enum.
    public static byte[] ParalizeOK() => new ByteQueueWriter().WritePacketId(ServerPacketId.ParalizeOK).ToArray();
    public static byte[] Blind() => new ByteQueueWriter().WritePacketId(ServerPacketId.Blind).ToArray();
    /// <summary>NADIE LO MANDA, y no es un olvido: la estupidez no se modela porque la clave <c>Estupidez</c>
    /// no existe en ninguno de los 113 hechizos del <c>Hechizos.dat</c> (ver el veredicto en NpcSystem, donde
    /// se descartó el <c>WriteDumb</c> del original por la misma razón). Se deja el constructor porque el
    /// cliente web SÍ decodifica su par <c>DumbNoMore</c>, y el día que aparezca data que lo use, esto es lo
    /// único que hay que conectar. Si alguien lo cablea, que sea con un hechizo que lo pida de verdad.</summary>
    public static byte[] Dumb() => new ByteQueueWriter().WritePacketId(ServerPacketId.Dumb).ToArray();
    public static byte[] BlindNoMore() => new ByteQueueWriter().WritePacketId(ServerPacketId.BlindNoMore).ToArray();
    public static byte[] DumbNoMore() => new ByteQueueWriter().WritePacketId(ServerPacketId.DumbNoMore).ToArray();

    /// <summary>Tells the client the current visible area (id 37). Layout: Byte(X), Byte(Y) — WriteAreaChanged
    /// (Protocol.bas:12514). Sent on world-entry and on area crossings.</summary>
    public static byte[] AreaChanged(byte x, byte y) =>
        new ByteQueueWriter().WritePacketId(ServerPacketId.AreaChanged).WriteByte(x).WriteByte(y).ToArray();

    /// <summary>Level-up notification (id 62). Layout: Integer(skillPoints) — PrepareMessageLevelUp (Protocol.bas:13426).</summary>
    public static byte[] LevelUp(short skillPoints) =>
        new ByteQueueWriter().WritePacketId(ServerPacketId.LevelUp).WriteInteger(skillPoints).ToArray();

    /// <summary>A visual FX over a character (id 40). Layout: CharIndex, FX, FXLoops (Protocol.bas:14511).</summary>
    public static byte[] CreateFX(short charIndex, short fx, short fxLoops) =>
        new ByteQueueWriter().WritePacketId(ServerPacketId.CreateFX)
            .WriteInteger(charIndex).WriteInteger(fx).WriteInteger(fxLoops).ToArray();

    /// <summary>A character's full info sheet (id 73 = CharacterInfo), for the GM /INFO command. Layout from WriteCharacterInfo
    /// (Protocol.bas:13843): name, race/class/gender/level, gold, bank, 3×ASCIIString (petitions/current/prev
    /// guild), 2×Boolean (RoyalArmy/CaosLegion), 2×Long (citizens/criminals killed). Petitions/army/faction-kill
    /// data isn't modeled in the slice → empty/0 (byte-accurate layout regardless).</summary>
    public static byte[] CharacterInfo(Player p) =>
        new ByteQueueWriter()
            .WritePacketId(ServerPacketId.CharacterInfo)
            .WriteAsciiString(p.Name)
            .WriteByte(p.Race).WriteByte(p.Class).WriteByte(p.Gender)
            .WriteByte(p.Level)
            .WriteLong((int)p.Gold).WriteLong((int)p.Bank)
            .WriteAsciiString("").WriteAsciiString(p.GuildName).WriteAsciiString("")
            .WriteBoolean(false).WriteBoolean(false)
            .WriteLong(0).WriteLong(0)
            .ToArray();

    /// <summary>Ficha de estadísticas (id 61) — VB6 <c>WriteMiniStats</c> (Protocol.bas:13373). Layout, en
    /// orden: 3×Long ciudadanos/renegados/usuarios matados + Integer NPCs muertos + 3×Byte clase/raza/género +
    /// Long muertes propias + Byte status de facción + 4×Long republicanos/caos/armada/milicianos matados. Los seis
    /// contadores de bando y el de muertes propias iban CABLEADOS EN 0 aunque el personaje los llevaba y el
    /// .chr los persistía: la ventana de estadísticas del cliente mostraba "Ciudadanos matados 0" para
    /// siempre. El emisor existía y la data estaba; lo que faltaba era conectarlos.</summary>
    public static byte[] MiniStats(Player p) =>
        new ByteQueueWriter()
            .WritePacketId(ServerPacketId.MiniStats)
            .WriteLong(p.CiudadanosMatados)
            .WriteLong(p.RenegadosMatados)
            .WriteLong(p.UsersKilled)          // UsuariosMatados
            .WriteInteger((short)p.NpcsKilled) // NPCsMuertos
            .WriteByte(p.Class).WriteByte(p.Race).WriteByte(p.Gender)
            .WriteLong(p.Deaths)               // MuertesUsuario (veces que murió)
            .WriteByte(p.FactionStatus)
            .WriteLong(p.RepublicanosMatados).WriteLong(p.CaosMatados)
            .WriteLong(p.ArmadaMatados).WriteLong(p.MilicianosMatados)
            .ToArray();

    public static byte[] ConsoleMsg(string text, byte fontIndex = 0) =>
        new ByteQueueWriter().WritePacketId(ServerPacketId.ConsoleMsg).WriteAsciiString(text).WriteByte(fontIndex).ToArray();

    /// <summary>La puñalada a una criatura (VB6 Trabajo.bas:1800), que viaja sólo como texto. Está acá y no adentro del handler
    /// porque los bots la leen para saber cuánto le sacaron (<see cref="SentidosDelBot.TomarLoQuePego"/>).</summary>
    public static string ApunalasteLaCriatura(int dano) => ComienzoDeLaPunalada + dano + ".";

    public const string ComienzoDeLaPunalada = "Has apuñalado la criatura por ";

    /// <summary>Cartel modal en el cliente (id 22). Layout: ASCIIString(mensaje) + Boolean(esPregunta) +
    /// Byte(accion) — PrepareMessageShowMessageBox (Protocol.bas:14596). Lo usa /SMSG para avisarle a todo el
    /// servidor, y con <c>("", true, 1)</c> el rechazo a tirar al piso un objeto marcado <c>Destruir</c>
    /// (Protocol.bas:2058), donde el texto lo pone el cliente según la acción.</summary>
    public static byte[] ShowMessageBox(string text, bool esPregunta = false, byte accion = 0) =>
        new ByteQueueWriter().WritePacketId(ServerPacketId.ShowMessageBox)
            .WriteAsciiString(text).WriteBoolean(esPregunta).WriteByte(accion).ToArray();

    /// <summary>Chat shown over a character's head (id 18). Layout: ASCIIString(chat), Integer(charIndex),
    /// Byte(mode) — PrepareMessageChatOverHead (Protocol.bas). The original client renders player chat
    /// this way (not in the console), so this is how HandleTalk broadcasts, matching the VB6 oracle.</summary>
    public static byte[] ChatOverHead(string chat, short charIndex, byte mode = 1) =>
        new ByteQueueWriter().WritePacketId(ServerPacketId.ChatOverHead)
            .WriteAsciiString(chat).WriteInteger(charIndex).WriteByte(mode).ToArray();

    // ── La respuesta al click izquierdo (VB6 LookatTile → WriteCharMsgStatus / WriteCharMsgStatusNPC) ─────────
    // Son los DOS paquetes con los que el original describe lo clickeado. No mandan texto: mandan datos, y el
    // cliente arma la línea. Por eso el nivel, la vida y los estados viajan como números y no como una frase.

    /// <summary>Estados del personaje, bitfield de <c>Generate_Char_Stat</c> (TCP.bas:1757, enum <c>lStat</c>).
    /// Los que el slice no modela (Incinerado, Trabajando, Combatiendo/TiempoDeMapeo, Inactivo/IdleCount,
    /// Saliendo) quedan en 0 — como en un personaje que no está en ninguno de esos estados.</summary>
    [Flags]
    public enum CharStat
    {
        Incinerado = 0x1, Envenenado = 0x2, Comerciando = 0x4, Trabajando = 0x8, Combatiendo = 0x10,
        Ciego = 0x20, Inactivo = 0x40, Resucitando = 0x80, Saliendo = 0x100,
    }

    /// <summary>Bitfield de <c>Generate_Char_StatEx</c> (TCP.bas:1801, enum <c>lStatEx</c>). El género va acá.</summary>
    [Flags]
    public enum CharStatEx { Paralizado = 0x1, Inmovilizado = 0x2, Hombre = 0x4, Mujer = 0x8 }

    private static short CharStatDe(Player p, long nowTick)
    {
        int s = 0;
        if (p.IsPoisoned(nowTick)) s |= (int)CharStat.Envenenado;
        if (p.TradingWith != 0) s |= (int)CharStat.Comerciando;
        if (p.IsBlinded(nowTick)) s |= (int)CharStat.Ciego;
        if (p.Resucitando) s |= (int)CharStat.Resucitando;
        return (short)s;
    }

    private static byte CharStatExDe(Player p, long nowTick)
    {
        int s = 0;
        // Las dos marcas del original por separado (Generate_Char_StatEx, TCP.bas:1790-1797), que pueden ir juntas.
        if (p.IsParalyzed(nowTick))
        {
            if (p.MarcaParalizado || !p.MarcaInmovilizado) s |= (int)CharStatEx.Paralizado;
            if (p.MarcaInmovilizado) s |= (int)CharStatEx.Inmovilizado;
        }
        s |= p.Gender == 1 ? (int)CharStatEx.Hombre : (int)CharStatEx.Mujer;
        return (byte)s;
    }

    /// <summary>El "código de estado" con el que el cliente pinta el nombre (Protocol.bas:17094): manda el RANGO
    /// si es GM y, si no, el bando. Ojo que el <c>Select Case</c> del original es sobre el valor ENTERO de
    /// Privilegios, no sobre bits: un Admin (16) no tiene <c>Case</c> propio y cae en el reparto por facción.
    /// La numeración de facción tampoco es identidad: 1/2/3 pasan derecho pero 4→5, 5→6, 6→7. (Éste es un byte
    /// DISTINTO del "Privileges" de CharacterCreate, que sí manda el Faccion.Status crudo.)</summary>
    private static byte CodigoDeEstado(Player p) => p.Privileges switch
    {
        2 => 10,    // Consejero
        32 => 11,   // RoleMaster
        4 => 12,    // SemiDios
        8 => 13,    // Dios
        _ => p.FactionStatus switch
        {
            Player.StatusCriminal => 1,      // VB6 Faccion.Status 1 = Renegado
            Player.StatusCiudadano => 2,
            Player.StatusRepublicano => 3,
            Player.StatusCaos => 5,
            Player.StatusArmada => 6,
            Player.StatusMilicia => 7,
            _ => 1,
        },
    };

    /// <summary>Porcentaje de vida tal como lo calcula el original: <c>CLng(((MinHP/100)/(MaxHP/100))*100)</c>.
    /// El <c>CLng</c> de VB redondea al PAR (banker's rounding), no hacia arriba.</summary>
    private static int PorcentajeVida(int hp, int maxHp) =>
        maxHp <= 0 ? 0 : (int)Math.Round(hp * 100.0 / maxHp, MidpointRounding.ToEven);

    /// <summary>CharMsgStatus (id 99) — la respuesta al click sobre un JUGADOR (VB6 WriteCharMsgStatus,
    /// Protocol.bas:17082). <paramref name="quienMira"/> decide si el nivel viaja o se oculta con un 255.</summary>
    public static byte[] CharMsgStatus(Player t, Player quienMira, long nowTick) =>
        new ByteQueueWriter().WritePacketId(ServerPacketId.CharMsgStatus)
            .WriteInteger(t.CharIndex)
            .WriteByte(CodigoDeEstado(t))
            .WriteLong(PorcentajeVida(t.Hp, t.MaxHp))
            .WriteInteger(CharStatDe(t, nowTick))
            .WriteByte(CharStatExDe(t, nowTick))
            .WriteByte(t.Class)
            // `ELV >= tELV+5 Or ELV >= tELV Or ELV+5 >= tELV`: las tres se subsumen en la última, así que el
            // nivel se esconde sólo ante alguien MÁS de 5 niveles por encima.
            .WriteInteger(quienMira.Level + 5 >= t.Level ? t.Level : (short)255)
            .WriteByte(t.Race)
            .WriteByte((byte)(t.Donador ? 1 : 0))
            .WriteByte(t.FactionRank)
            .WriteAsciiString(t.SpouseName)
            .WriteAsciiString(t.Description)
            .ToArray();

    /// <summary>CharMsgStatusNPC (id 100) — la respuesta al click sobre una CRIATURA (VB6
    /// WriteCharMsgStatusNPC, Protocol.bas:17227). Dos rarezas del original: lo primero que viaja no es el
    /// charIndex sino el <b>Numero</b> (el índice de su ficha), y la vida se manda CRUDA —no en porcentaje— a
    /// quien sea GM o tenga Supervivencia ≥ 60, con un byte que avisa cuál de las dos cosas es.</summary>
    public static byte[] CharMsgStatusNpc(Npc npc, Player quienMira, long nowTick)
    {
        bool vidaExacta = quienMira.IsGm || quienMira.Skills[GameLimits.SkillSupervivencia] >= 60;
        // Las dos marcas por separado, que pueden estar juntas (VB6 Generate_Char_StatExNpcs, TCP.bas:1819).
        int estados = (npc.ParalyzedUntilTick > nowTick ? (int)CharStatEx.Paralizado : 0)
                    | (npc.InmovilizadaHastaTick > nowTick ? (int)CharStatEx.Inmovilizado : 0);
        return new ByteQueueWriter().WritePacketId(ServerPacketId.CharMsgStatusNPC)
            .WriteInteger(npc.Template.Index)
            .WriteByte((byte)(npc.Template.Status is >= 0 and <= 4 ? npc.Template.Status : 1))
            .WriteByte((byte)(vidaExacta ? 1 : 0))
            .WriteLong(vidaExacta ? npc.Hp : PorcentajeVida(npc.Hp, npc.Template.MaxHp))
            .WriteByte((byte)estados)
            // Mismo patrón que el del jugador pero con margen 10. En esta data ningún NPC trae la clave "Nivel",
            // así que todos valen 100 por el default del original y el nivel se esconde para casi cualquiera.
            .WriteInteger(quienMira.Level + 10 >= npc.Template.Nivel ? (short)npc.Template.Nivel : (short)255)
            .WriteByte((byte)(npc.OwnerCharIndex != 0 ? 1 : 0))   // MaestroUser: es mascota de alguien
            // Y el segundo byte es el OTRO dueño, el de la pertenencia temporal por haberle pegado
            // (`Npclist().Owner`). Acá iba repetido el del amo, con la nota "el slice no separa amo de dueño":
            // ahora sí los separa, y éste es el único lugar por el que ese estado sale al cliente.
            .WriteByte((byte)(npc.ApropiadoPor != 0 ? 1 : 0))
            .ToArray();
    }

    /// <summary>Character attributes (id 47). Layout: 5 Bytes Fuerza/Agilidad/Inteligencia/Carisma/
    /// Constitucion — WriteAtributos (Protocol.bas:12846). The client renders these in the stats panel.</summary>
    public static byte[] Atributes(byte fuerza, byte agilidad, byte inteligencia, byte carisma, byte constitucion) =>
        new ByteQueueWriter().WritePacketId(ServerPacketId.atributes)
            .WriteByte(fuerza).WriteByte(agilidad).WriteByte(inteligencia).WriteByte(carisma).WriteByte(constitucion).ToArray();

    /// <summary>Localized message by id (id 83). Layout: Integer(id), ASCIIString(args), Byte(mode), Byte(font)
    /// — PrepareMessageLocaleMsg (Protocol.bas:17327). The client resolves the id to text via locale_smg_es.ind.
    /// The VB6 uses this (not literal ConsoleMsg) for systemic notices (combat gates, mode toggles).</summary>
    public static byte[] LocaleMsg(short id, string args = "", byte mode = 0, byte font = 0) =>
        new ByteQueueWriter().WritePacketId(ServerPacketId.LocaleMsg)
            .WriteInteger(id).WriteAsciiString(args).WriteByte(mode).WriteByte(font).ToArray();

    /// <summary>A persistent/one-shot particle FX over a character (id 90). Layout: Integer(charIndex),
    /// Integer(particle), Single(time), Boolean(remove) — PrepareMessageEfectoCharParticula (Protocol.bas:15150).
    /// Meditation aura: time=-1/remove=false to add (persistent), time=0/remove=true to clear.</summary>
    public static byte[] EfectoCharParticula(short charIndex, short particle, float time, bool remove) =>
        new ByteQueueWriter().WritePacketId(ServerPacketId.EfectoCharParticula)
            .WriteInteger(charIndex).WriteInteger(particle).WriteSingle(time).WriteBoolean(remove).ToArray();

    /// <summary>Compact (delta) update of one of the player's own inventory slots (id 102). Layout:
    /// Byte(slot), Byte(accion), Integer(valor) — writeChangeInventorySlotUser (Protocol.bas:17447).
    /// accion 1=equip, 2=unequip, 3=set amount (valor; 0 empties the slot). The full slot was sent earlier
    /// via ChangeInventorySlot, so this only carries the delta.</summary>
    public static byte[] ChangeInventorySlotUser(byte slot, byte accion, short valor) =>
        new ByteQueueWriter().WritePacketId(ServerPacketId.ChangeInventorySlotUser)
            .WriteByte(slot).WriteByte(accion).WriteInteger(valor).ToArray();

    /// <summary>Current hunger (id 105). Layout: Byte(minHam) — WriteUpdateHambre (Protocol.bas:17514).</summary>
    public static byte[] UpdateHambre(byte minHam) =>
        new ByteQueueWriter().WritePacketId(ServerPacketId.UpdateHambre).WriteByte(minHam).ToArray();

    /// <summary>Current thirst (id 104). Layout: Byte(minAgu) — WriteUpdateSed (Protocol.bas:17492).</summary>
    public static byte[] UpdateSed(byte minAgu) =>
        new ByteQueueWriter().WritePacketId(ServerPacketId.UpdateSed).WriteByte(minAgu).ToArray();

    /// <summary>LA FICHA DEL FAMILIAR (id 107). <b>Paquete nuestro</b>, no del original — ver
    /// <see cref="ServerPacketId.FamiliarInfo"/>. Se manda al entrar al mundo, al elegir familiar, al llamarlo
    /// o retirarlo, cuando sube de nivel y cuando muere o revive: o sea, cada vez que algo de lo que el panel
    /// muestra pudo cambiar. Con <c>tipo = 0</c> le dice al cliente que este personaje no lleva familiar, que
    /// es lo que le pasa a quince de las dieciocho clases.</summary>
    public static byte[] FamiliarInfo(Game.Familiar f)
    {
        // Un byte para los dos estados en vez de dos bytes: el panel los usa juntos y nunca por separado.
        byte estado = (byte)((f.Invocado ? 1 : 0) | (f.Muerto ? 2 : 0));
        var w = new ByteQueueWriter().WritePacketId(ServerPacketId.FamiliarInfo)
            .WriteByte(f.Tipo)
            .WriteAsciiString(f.Nombre)
            .WriteByte(f.Nivel)
            // El `Long` del protocolo son 4 bytes con signo: en el modelo se llevan en 64 bits para que la
            // experiencia no se desborde al sumar, y se recortan acá, que es donde entran al cable.
            .WriteLong((int)f.Exp).WriteLong((int)f.Elu)
            .WriteLong((int)f.Vida).WriteLong((int)f.VidaMaxima)
            .WriteLong((int)f.GolpeMinimo).WriteLong((int)f.GolpeMaximo)
            .WriteByte(estado)
            // Las cuatro ranuras van ESCRITAS UNA POR UNA y no con un bucle, aunque el bucle diga lo mismo: el
            // guard que compara este emisor contra el decoder del cliente lee el codigo, y un `foreach` le
            // aparece como "un largo variable" contra los cuatro campos fijos del otro lado. El paquete no
            // tiene length-prefix, asi que ese guard es lo unico que ata las dos puntas.
            .WriteInteger((short)f.Habilidades[0])
            .WriteInteger((short)f.Habilidades[1])
            .WriteInteger((short)f.Habilidades[2])
            .WriteInteger((short)f.Habilidades[3]);
        return w.ToArray();
    }

    /// <summary>
    /// El buzón entero (id 86). Layout: Byte(cantidad) y por cada carta
    /// ASCIIString(Emisor), ASCIIString(Carta), Byte(Leida), Integer(ObjetoCantidad), Integer(ObjetoIndex)
    /// — WriteCorreoList (Protocol.bas:15953), único call site Acciones.bas:617 (abrir el buzón en el NPC).
    ///
    /// OJO con el orden de los dos últimos: va la CANTIDAD y después el ÍNDICE del objeto, al revés de como
    /// se lee en todos los demás paquetes de ítem del protocolo. Invertirlos compila igual y el cliente
    /// muestra "3 unidades del objeto 500" como "500 unidades del objeto 3".
    /// </summary>
    public static byte[] CorreoList(IReadOnlyList<Game.MailEntry> cartas)
    {
        var w = new ByteQueueWriter().WritePacketId(ServerPacketId.CorreoList)
            .WriteByte((byte)Math.Min(byte.MaxValue, cartas.Count));
        foreach (var c in cartas.Take(byte.MaxValue))
            w.WriteAsciiString(c.Sender).WriteAsciiString(c.Message)
             .WriteByte(c.Leida).WriteInteger(c.Amount).WriteInteger(c.ObjIndex);
        return w.ToArray();
    }

    // ---- Actualizaciones GRANULARES de stats -------------------------------------------------------------
    //
    // El original manda un paquete de 3-5 bytes cuando cambia UN stat, y reserva el UpdateUserStats gordo
    // (~26 bytes) para cuando cambian muchos a la vez. El port mandaba el gordo siempre: el cliente muestra lo
    // mismo, pero no es byte-fiel y son ~8x bytes en el camino más caliente que hay (cada golpe recibido, cada
    // vuelta de regeneración, cada tick de un oficio).
    //
    // El barrido del oráculo no lo detectó porque ninguno de estos se dispara "en frío": todos necesitan estado
    // (estar trabajando, recibir daño, matar algo, subir de nivel, tomar una poción). Salió del conteo de
    // emisores, no de la comparación por acción — dos ejes que se cubren los agujeros mutuamente.

    /// <summary>Stamina actual (id 11). Layout: Integer(MinSta) — WriteUpdateSta (Protocol.bas:11673).</summary>
    public static byte[] UpdateSta(short minSta) =>
        new ByteQueueWriter().WritePacketId(ServerPacketId.UpdateSta).WriteInteger(minSta).ToArray();

    /// <summary>Maná actual (id 12). Layout: Integer(MinMAN) — WriteUpdateMana (Protocol.bas:11706).</summary>
    public static byte[] UpdateMana(short minMan) =>
        new ByteQueueWriter().WritePacketId(ServerPacketId.UpdateMana).WriteInteger(minMan).ToArray();

    /// <summary>HP actual (id 13). Layout: Integer(MinHP) — WriteUpdateHP (Protocol.bas:11739).</summary>
    public static byte[] UpdateHP(short minHp) =>
        new ByteQueueWriter().WritePacketId(ServerPacketId.UpdateHP).WriteInteger(minHp).ToArray();

    /// <summary>Experiencia acumulada (id 15). Layout: Long(Exp) — WriteUpdateExp (Protocol.bas:11793).</summary>
    public static byte[] UpdateExp(int exp) =>
        new ByteQueueWriter().WritePacketId(ServerPacketId.UpdateExp).WriteLong(exp).ToArray();

    /// <summary>Fuerza (id 87). Layout: Byte — WriteUpdateStrenght (Protocol.bas:11855). El nombre lleva el
    /// typo del original ("Strenght"): es el identificador del protocolo, no se corrige.</summary>
    public static byte[] UpdateStrenght(byte fuerza) =>
        new ByteQueueWriter().WritePacketId(ServerPacketId.UpdateStrenght).WriteByte(fuerza).ToArray();

    /// <summary>Agilidad (id 88). Layout: Byte — WriteUpdateDexterity (Protocol.bas:11888).</summary>
    public static byte[] UpdateDexterity(byte agilidad) =>
        new ByteQueueWriter().WritePacketId(ServerPacketId.UpdateDexterity).WriteByte(agilidad).ToArray();

    /// <summary>Los máximos y la barra de experiencia al subir de nivel (id 42). Layout:
    /// Integer(MaxHP), Integer(MaxMAN), Integer(MaxSta), Integer(ELV), Long(ELU), Long(Exp) —
    /// WriteUpdateUserStatsForLevel (Protocol.bas:12649), único call site Modulo_UsUaRiOs.bas:611 (SubirNivel).
    /// OJO con el orden: manda los MÁXIMOS y el nivel, no los actuales.</summary>
    public static byte[] UpdateUserStatsForLevel(Player p) =>
        new ByteQueueWriter()
            .WritePacketId(ServerPacketId.UpdateUserStatsForLevel)
            .WriteInteger((short)p.MaxHp).WriteInteger((short)p.MaxMana).WriteInteger((short)p.MaxStamina)
            .WriteInteger(p.Level)
            .WriteLong((int)p.Elu).WriteLong((int)p.Exp)
            .ToArray();

    /// <summary>
    /// La foto completa de los stats (id 41). El original la reserva para cuando cambia TODO junto: sólo tiene
    /// 7 call sites (login en TCP.bas ×2, comercio entre usuarios ×4, y uno en Modulo_UsUaRiOs), contra los
    /// granulares de acá arriba que usa en todos los caminos calientes.
    ///
    /// El port llegó a tener 34 call sites de éste. Quedan 8, y cada uno por un motivo:
    /// <list type="bullet">
    /// <item>SpawnIntoWorld — el original también manda el gordo al entrar (TCP.bas).</item>
    /// <item>GmEditChar (/MOD) — puede tocar cualquier stat, así que "cambió todo" es literal.</item>
    /// <item>GmRevive / RevivePlayer / HandleReward / HandleEnlist / DoConvertir / HandleRetirarFaccion —
    /// el VB6 <b>no manda ningún paquete de stats</b> en esos caminos (RevivirUsuario sólo emite el sonido y
    /// la partícula; ModFacciones no emite stats). Mandar el gordo es un <i>gap inverso</i>: de más, no de
    /// menos, y el cliente web lo necesita para repintar la barra. Cambiarlo a granulares sería inventar un
    /// layout que la fuente no tiene, así que se deja y se documenta.</item>
    /// </list>
    /// El único caso donde el original SÍ granulariza el revivir es el hechizo Resucitar
    /// (modHechizos.bas:1015 → UpdateMana + UpdateSta + UpdateHungerAndThirst), no el sacerdote.
    /// </summary>
    public static byte[] UpdateUserStats(Player p) =>
        new ByteQueueWriter()
            .WritePacketId(ServerPacketId.UpdateUserStats)
            .WriteInteger((short)p.MaxHp).WriteInteger((short)p.Hp)
            .WriteInteger((short)p.MaxMana).WriteInteger((short)p.Mana)
            .WriteInteger((short)p.MaxStamina).WriteInteger((short)p.Stamina)
            .WriteLong(p.Gold)
            .WriteInteger(p.Level)
            .WriteLong((int)p.Elu).WriteLong((int)p.Exp)
            .WriteByte(p.Agility).WriteByte(p.Strength)
            .WriteByte(p.Hunger).WriteByte(p.MaxHunger)
            .WriteByte(p.Thirst).WriteByte(p.MaxThirst)
            .ToArray();

    /// <summary>The 21-field character spawn packet. Field order &amp; the FXLoops=999 spawn constant
    /// verified byte-identical to VB6 PrepareMessageCharacterCreate (Protocol.bas:14887) and the
    /// player call site MakeUserChar (Modulo_UsUaRiOs.bas:332). The "Privileges" byte is the VB6
    /// <c>.Faccion.Status</c> (nick colour).
    ///
    /// <para>Los siete últimos bytes —partícula y las cinco auras— salían FIJOS EN 0 con la nota "systems not
    /// yet ported", que dejó de ser cierta cuando se portaron las auras del equipo. El síntoma no era que el
    /// brillo faltara siempre: <c>AuraToChar</c> se lo manda a los que están mirando en el momento de equipar,
    /// así que el aura se veía… hasta que alguien salía del área y volvía a entrar. Quien llegaba después veía
    /// al personaje apagado. Se manda el estado real, que es lo que hace el call site del original.</para></summary>
    public static byte[] CharacterCreate(Player p) =>
        new ByteQueueWriter()
            .WritePacketId(ServerPacketId.CharacterCreate)
            .WriteInteger(p.CharIndex)
            .WriteInteger(Tapado(p, p.Body))       // el GM con /INVISIBLE, en 0 (ver CharacterChange)
            .WriteInteger(Tapado(p, p.Head))
            .WriteByte(p.Heading)
            .WriteByte(p.Pos.X).WriteByte(p.Pos.Y)
            .WriteInteger(Tapado(p, p.WeaponAnim))
            .WriteInteger(Tapado(p, p.ShieldAnim))
            .WriteInteger(Tapado(p, p.HelmetAnim))
            .WriteInteger(0)     // FX (0 = sin efecto activo al aparecer, == .Char.FX en spawn normal)
            .WriteInteger(999)   // FXLoops: constante de spawn de jugador del VB6 (MakeUserChar:332)
            // Nick VACÍO si tiene showName en false (VB6 MakeUserChar, Modulo_UsUaRiOs.bas:309): así el GM que
            // usó /SHOWNAME aparece sin nombre también para quien entra a su área DESPUÉS del toggle.
            .WriteAsciiString(p.ShowName ? TagDe(p) : "")
            .WriteByte(p.FactionStatus)  // byte "Privileges" del VB6 == .Faccion.Status (color de nombre)
            .WriteByte((byte)(p.Donador ? 1 : 0))   // Donador (VB6 MakeUserChar:332 → .Char.FX... = Donador.activo)
            .WriteByte(p.ParticulaFx)
            .WriteByte(p.WeaponAura)
            .WriteByte(p.BodyAura)
            .WriteByte(p.ShieldAura)
            .WriteByte(p.HelmetAura)
            .WriteByte(0)              // Otra_Aura: el VB6 tiene el campo pero NADA se lo asigna nunca
            .WriteByte(p.RingAura)
            .ToArray();

    /// <summary>CharacterCreate for an NPC (same 21-field layout; no weapons/auras).</summary>
    public static byte[] CharacterCreate(Npc n) =>
        new ByteQueueWriter()
            .WritePacketId(ServerPacketId.CharacterCreate)
            .WriteInteger(n.CharIndex)
            .WriteInteger(n.Template.Body)
            .WriteInteger(n.Template.Head)
            .WriteByte(n.Heading)
            .WriteByte(n.Pos.X).WriteByte(n.Pos.Y)
            // El equipo que se le VE puesto, del NPCs.dat (VB6 MakeNPCChar, MODULO_NPCs.bas:590). Acá iban tres
            // ceros: 57 de los 291 NPCs salían desarmados —los Guerreros sin espada ni escudo, los Hechiceros
            // sin casco— aunque su ficha declara los tres.
            .WriteInteger((short)n.Template.WeaponAnim)
            .WriteInteger((short)n.Template.ShieldAnim)
            .WriteInteger((short)n.Template.HelmetAnim)
            .WriteInteger(0).WriteInteger(0)                 // FX / FXLoops
            // El NOMBRE de un NPC no es su nombre: es su NÚMERO de ficha (VB6 MakeNPCChar, MODULO_NPCs.bas:590,
            // manda `Npclist(i).Numero`). El cliente lo resuelve contra su tabla de locale — y de paso usa
            // `IsNumeric(Name)` para saber que el personaje ES un NPC (Protocol.bas:1733), que es de lo que
            // dependen el remapeo del color del nick y la regla de "el nombre sólo se ve bajo el puntero".
            // Mandar el nombre ya resuelto, como se hacía acá, le sacaba al cliente las dos cosas.
            .WriteAsciiString(n.Template.Index.ToString(System.Globalization.CultureInfo.InvariantCulture))
            // "Privileges" de un NPC = su `flags.Status` (VB6 MakeNPCChar), que sale de la clave Status del
            // NPCs.dat y es lo que le da COLOR AL NOMBRE. Acá iba un 0 fijo, así que los 48 NPCs alineados
            // perdían su color y salían todos con el gris del renegado.
            .WriteByte((byte)n.Template.Status)
            .WriteByte(0).WriteByte(0)                       // Donador / ParticulaFx
            .WriteByte(0).WriteByte(0).WriteByte(0).WriteByte(0).WriteByte(0).WriteByte(0) // 6 auras
            .ToArray();

    /// <summary>WriteGuildList (id 36): todos los nombres de clan, separados por NUL
    /// (VB6 Protocol.bas:40 -> Const SEPARATOR = vbNullChar, y agrega uno al final de cada nombre).</summary>
    public static byte[] GuildList(IEnumerable<string> guildNames)
    {
        string tmp = "";
        foreach (var n in guildNames) tmp += n + "\0";
        return new ByteQueueWriter().WritePacketId(ServerPacketId.guildList).WriteAsciiString(tmp).ToArray();
    }

    /// <summary>WritePremios (id 89): Integer(cantidad) + Long(credito) + por premio: ASCIIString(nombre) +
    /// Integer(puntos). El VB6 no emite el paquete cuando la cantidad es 0.</summary>
    public static byte[] Premios(IReadOnlyList<(string Name, short Points)> prizes, int donorCredit)
    {
        var w = new ByteQueueWriter().WritePacketId(ServerPacketId.Premios)
            .WriteInteger((short)prizes.Count).WriteLong(donorCredit);
        foreach (var (name, points) in prizes) w.WriteAsciiString(name).WriteInteger(points);
        return w.ToArray();
    }
    /// <summary>WorkRequestTarget (id 43): pide al cliente que elija el objetivo de una skill de
    /// trabajo (VB6 HandleWork -> WriteWorkRequestTarget para pesca/robar/talar/mineria/fundir/domar).</summary>
    public static byte[] WorkRequestTarget(byte skill) =>
        new ByteQueueWriter().WritePacketId(ServerPacketId.WorkRequestTarget).WriteByte(skill).ToArray();

    /// <summary>UpdateHungerAndThirst (id 60): Byte(MinAGU=sed) + Byte(MinHam=hambre). VB6
    /// WriteUpdateHungerAndThirst (Protocol.bas:13347): la sed va primero.</summary>
    public static byte[] UpdateHungerAndThirst(byte minAgu, byte minHam) =>
        new ByteQueueWriter().WritePacketId(ServerPacketId.UpdateHungerAndThirst)
            .WriteByte(minAgu).WriteByte(minHam).ToArray();
}
