/**
 * Server→client packet decoding for the web client (Fase 3). Reads the SC byte stream (server→client
 * is plaintext) into structured events the renderer/HUD can consume. Layouts match fase0/spec.
 * Not every SC packet is modelled yet — unknown ids stop the parse cleanly (no desync).
 */
import { ByteQueueReader, NotEnoughData } from '../codec/bytequeue.js';

export enum ServerPacketId {
  LoggedSuccessful = 0, Logged = 1, ConsoleMsg = 20, ChangeMap = 16, PosUpdate = 17,
  UserIndexInServer = 23, UserCharIndexInServer = 24, CharacterCreate = 25, CharacterRemove = 26,
  CharacterMove = 27, ForceCharMove = 28, CharacterChange = 29, ObjectCreate = 31, ObjectDelete = 32, BlockPosition = 33, ChangeInventorySlot = 44, ChangeSpellSlot = 46, UpdateUserStats = 41,
  PlayMidi = 34, PlayWave = 35, Pong = 81, UpdateTagAndStatus = 82, RainToggle = 39, CreateFX = 40,
  Dumb = 58, DumbNoMore = 66, AreaChanged = 37, LevelUp = 62, ChatOverHead = 18, Atributes = 47,
  MeditateToggle = 64, LocaleMsg = 83, EfectoCharParticula = 90,
  ChangeInventorySlotUser = 102, UpdateSed = 104, UpdateHambre = 105, UpdateHungerAndThirst = 60,
  // Actualizaciones de UN stat. El original las usa en los caminos calientes (cada golpe, cada vuelta de
  // regeneracion, cada tick de un oficio) y deja el UpdateUserStats gordo para cuando cambia todo junto.
  UpdateSta = 11, UpdateMana = 12, UpdateHP = 13, UpdateExp = 15,
  UpdateUserStatsForLevel = 42, UpdateStrenght = 87, UpdateDexterity = 88,
  BlacksmithWeapons = 48, BlacksmithArmors = 49, BlacksmithHelmet = 50, BlacksmithShield = 51,
  CarpenterObjects = 52, SastreObjects = 53,
  AlquimiaObjects = 54, TrainerCreatureList = 68, CommerceInit = 9, BankInit = 10, ChangeNPCInventorySlot = 59,
  ChangeBankSlot = 45, BankEnd = 8, GuildLeaderInfo = 74, UpdateGold = 14,
  GuildList = 36, Premios = 89, NavigateToggle = 4,
  MontateToggle = 5, CommerceEnd = 7, RestOK = 55, CharacterInfo = 73, ParalizeOK = 77, UserNameList = 85,
  ShowSOSForm = 84, SendMsgBox = 56, ShowUserRequest = 78,
  // Equipar/desequipar: el server manda un slot suelto y, si el item brilla, su aura.
  CharacterChangeSlot = 30, AuraToChar = 103,
  // Salida limpia, pausa del mundo y borrado del dialogo sobre la cabeza.
  Disconnect = 6, PauseToggle = 38, RemoveCharDialog = 3, RemoveDialogs = 2,
  // Ubicacion de un amigo. Llega al desconectarse alguien de tu lista, con mapa/x/y en 0.
  MostrarUbicacion = 97,
  // Clanes y facciones: propuesta recibida, panel del miembro comun, y el recolor del nick.
  OfferDetails = 70, GuildMemberInfo = 75, CharStatus = 94,
  // FX de un hechizo sobre el TERRENO (van por coordenadas, sin charIndex al que colgarse) y el indicador
  // de correo pendiente. Los dos EfectoTerreno* los emiten los hechizos de AREA, que resuelven el 3x3.
  EfectoTerrenoFX = 93, EfectoTerrenoParticula = 92, MensajeSigno = 95,
  AlianceProposalsList = 71, PeaceProposalsList = 72, GuildDetails = 76,
  AddPJ = 91, AbrirFormularios = 101, EjecutarAccion = 106, CorreoList = 86,
  // LA FICHA DEL FAMILIAR. AGREGADO NUESTRO: no existe en el original, arranca en 107 -- uno despues del
  // ultimo id del VB6-- por el mismo criterio que el `BorrarPersonaje = 114` del lado del cliente. El original
  // tenia el panel dibujado en frmEstadisticas y ningun paquete que lo alimentara.
  FamiliarInfo = 107,
  // El cuadro de dialogo del server. Es EL paquete de los rechazos de login (personaje ya conectado, otra
  // sesion de la misma cuenta): sin decodificarlo, el cliente quedaba en "logueando..." para siempre y ademas
  // se le desincronizaba el stream, porque no sabia cuantos bytes comerse.
  ShowMessageBox = 22,
  // Sin estos ids el stream se DESINCRONIZA: el protocolo no lleva length-prefix, asi que un paquete
  // que el decoder no sabe leer se come los bytes del siguiente.
  GuildChat = 21, Blind = 57, MiniStats = 61, SetInvisible = 63, BlindNoMore = 65, SendSkills = 67,
  WorkRequestTarget = 43,
  ChatOverHeadLocale = 19,
  // La respuesta al click izquierdo: el server NO manda una frase, manda los datos y el cliente arma la linea.
  CharMsgStatus = 99, CharMsgStatusNPC = 100,
  // Los dos ultimos emisores VIVOS que el decoder no modelaba, o sea que CORTABAN el frame: la respuesta a
  // ShowGuildNews (el cliente pedia las noticias del clan y no podia leerlas) y el ack de cada operacion de
  // boveda. El nombre del id va en minuscula porque asi lo bautiza el protocolo del fork (guildNews).
  guildNews = 69, BankOK = 80,
}

export interface ServerEvent { id: number; name: string; fields: Record<string, unknown>; }

const NAMES: Record<number, string> = Object.fromEntries(
  Object.entries(ServerPacketId).filter(([, v]) => typeof v === 'number').map(([k, v]) => [v, k]),
);

/** Reads one packet's fields; returns null for an unmodelled id (caller stops). */
function readOne(r: ByteQueueReader, id: number): Record<string, unknown> | null {
  switch (id) {
    case ServerPacketId.LoggedSuccessful: return {};
    case ServerPacketId.Logged: return { redundance: r.readByte() };
    case ServerPacketId.ConsoleMsg: return { chat: r.readAsciiString(), font: r.readByte() };
    case ServerPacketId.ChangeMap: return { map: r.readInteger() };
    case ServerPacketId.PosUpdate: return { x: r.readByte(), y: r.readByte() };
    case ServerPacketId.UserIndexInServer: return { userIndex: r.readInteger() };
    case ServerPacketId.UserCharIndexInServer: return { charIndex: r.readInteger() };
    case ServerPacketId.CharacterRemove: return { charIndex: r.readInteger(), faded: r.readBoolean() };
    case ServerPacketId.CharacterMove: return { charIndex: r.readInteger(), x: r.readByte(), y: r.readByte() };
    case ServerPacketId.CharacterChange: return { charIndex: r.readInteger(), body: r.readInteger(), head: r.readInteger(), heading: r.readByte(),
      weapon: r.readInteger(), shield: r.readInteger(), helmet: r.readInteger(), fx: r.readInteger(), fxLoops: r.readInteger() };
    case ServerPacketId.ChangeSpellSlot: return { slot: r.readByte(), spell: r.readInteger() };
    case ServerPacketId.ObjectCreate: return { x: r.readByte(), y: r.readByte(), objIndex: r.readInteger(), amount: r.readInteger() };
    // El server te FUERZA a moverte en una direccion: pasa cuando un NPC pisa el tile de tu cadaver y te
    // intercambia de lugar (VB6 MoveNPCChar). Sin este case el stream se desincroniza (no hay length-prefix).
    case ServerPacketId.ForceCharMove: return { heading: r.readByte() };
    case ServerPacketId.ObjectDelete: return { x: r.readByte(), y: r.readByte() };
    // Un tile cambio de bloqueado: lo manda el server al abrir/cerrar una puerta (VB6 Bloquear), a TODO el mapa.
    // Sin este case el stream se DESINCRONIZA: el protocolo no tiene length-prefix, asi que un id desconocido
    // deja los 3 bytes del payload en la cola y todo lo que sigue se lee corrido.
    case ServerPacketId.BlockPosition: return { x: r.readByte(), y: r.readByte(), blocked: r.readBoolean() };
    case ServerPacketId.PlayMidi: return { midi: r.readByte(), loops: r.readInteger() };
    case ServerPacketId.PlayWave: return { wave: r.readInteger(), x: r.readByte(), y: r.readByte() };
    case ServerPacketId.RainToggle: return { clima: r.readByte() };
    case ServerPacketId.CreateFX: return { charIndex: r.readInteger(), fx: r.readInteger(), fxLoops: r.readInteger() };
    // El par Dumb/DumbNoMore. El EFECTO no se modela (ningún hechizo de esta data trae `Estupidez = 1`, ver la
    // nota del handler en index.html), pero el id igual tiene que decodificarse: sin `case`, `readOne` devuelve
    // null y el bucle CORTA EL FRAME y tira todo lo que venga detrás — la contracara del protocolo sin
    // length-prefix. Los dos son cuerpo vacío, así que salen gratis y el par queda simétrico.
    case ServerPacketId.Dumb: return {};
    case ServerPacketId.DumbNoMore: return {};
    // `index` esta sobrecargado: con modo 2 ES el numero de dano (el "-N" flotante de AO);
    // con modo 1/4 es un id de locale.
    case ServerPacketId.ChatOverHeadLocale:
      return { charIndex: r.readInteger(), index: r.readLong(), modo: r.readByte() };
    // El server pide que el cliente elija un objetivo para una skill de trabajo/arco (id 43): un byte de skill.
    case ServerPacketId.WorkRequestTarget: return { skill: r.readByte() };
    // Toggles/acks sin payload (sólo el id): montar, cerrar comercio NPC, descansar en fogata, parálisis.
    case ServerPacketId.MontateToggle: return {};
    case ServerPacketId.CommerceEnd: return {};
    case ServerPacketId.RestOK: return {};
    case ServerPacketId.ParalizeOK: return {};
    // Lista de nombres online (/LISTUSU): un ASCIIString con los nombres separados por NUL.
    case ServerPacketId.UserNameList: return { names: r.readAsciiString() };
    // FX de un hechizo casteado a un TILE: sin charIndex al que colgarlo, va con coordenadas.
    case ServerPacketId.EfectoTerrenoFX: return { fx: r.readInteger(), x: r.readByte(), y: r.readByte(), loops: r.readInteger() };
    // Idem con una particula en vez de un grh animado. OJO: el `time` es un Long (4 bytes), no un Integer —
    // leerlo corto desincroniza todo el stream que venga detras.
    case ServerPacketId.EfectoTerrenoParticula:
      return { particula: r.readInteger(), x: r.readByte(), y: r.readByte(), time: r.readLong() };
    // 1 = tenes correo pendiente, 0 = ya lo leiste.
    case ServerPacketId.MensajeSigno: return { recibio: r.readByte() };
    // El texto de una propuesta de alianza/paz que otro clan nos mando (lo pide el lider).
    case ServerPacketId.OfferDetails: return { details: r.readAsciiString() };
    // Noticias del clan + enemigos + aliados (WriteGuildNews, Protocol.bas:13659). Los dos ultimos vienen
    // NUL-joined en un solo string, no como listas.
    case ServerPacketId.guildNews:
      return { news: r.readAsciiString(), enemies: r.readAsciiString(), allies: r.readAsciiString() };
    // Ack sin payload de cada operacion de boveda. NO es decorativo: HandleBankOK repinta la lista de la
    // ventana desde el inventario del cliente y le restaura la seleccion. Aca el equivalente es re-renderizar.
    case ServerPacketId.BankOK: return {};
    // Lo que ve un MIEMBRO COMUN del clan: lista de clanes + miembros del suyo, separados por NUL.
    case ServerPacketId.GuildMemberInfo: return { guildList: r.readAsciiString(), memberList: r.readAsciiString() };
    // Cambio de bando de un personaje: solo el charIndex y el status nuevo (recolor del nick).
    case ServerPacketId.CharStatus: return { charIndex: r.readInteger(), status: r.readByte() };
    // El server avisa que la conexion se termina (salida limpia) antes de cerrar el socket.
    case ServerPacketId.Disconnect: return {};
    // TOGGLE de pausa del mundo: el server lo manda dos veces alrededor del guardado de personajes.
    case ServerPacketId.PauseToggle: return {};
    // Borra el chat que quedo flotando sobre la cabeza de un personaje (al morir, al warpear, al salir).
    case ServerPacketId.RemoveCharDialog: return { charIndex: r.readInteger() };
    // Borra TODOS los dialogos de la pantalla. Sin payload. Llega al warpear, junto al RemoveCharDialog que
    // va al area vieja (VB6 WarpUserChar). Hay que decodificarlo SI O SI: sin length-prefix, un id que el
    // decoder no conoce desincroniza el stream entero.
    case ServerPacketId.RemoveDialogs: return {};
    // Un amigo cambio de ubicacion. Hoy el server solo lo emite al DESCONECTARSE alguien, con mapa/x/y en 0.
    case ServerPacketId.MostrarUbicacion:
      return { nombre: r.readAsciiString(), numAmigo: r.readByte(), mapa: r.readInteger(), x: r.readByte(), y: r.readByte() };
    // Un solo slot de la apariencia (equipar/desequipar): 1 cuerpo, 4 arma, 5 escudo, 6 casco, 8 nudillos.
    case ServerPacketId.CharacterChangeSlot: return { charIndex: r.readInteger(), value: r.readInteger(), slot: r.readByte() };
    // El aura del equipo: tipo 1 arma, 2 cuerpo, 3 escudo, 4 casco, 6 anillo; aura 0 la apaga.
    case ServerPacketId.AuraToChar: return { charIndex: r.readInteger(), aura: r.readByte(), tipo: r.readByte() };
    // La solicitud de un aspirante a clan, que el lider pide con GuildRequestJoinerInfo.
    case ServerPacketId.ShowUserRequest: return { details: r.readAsciiString() };
    // Cartel con la respuesta de un GM a una consulta de /GM (ResponderGM -> WriteSendMsgBox).
    case ServerPacketId.SendMsgBox: return { message: r.readAsciiString(), modo: r.readByte() };
    // Cola de consultas a los GMs (/SHOW SOS): un ASCIIString con entradas "[NOMBRE]-HH:MM:SS-Tipo="
    // separadas por NUL. Va aca aunque el HUD todavia no la muestre: sin el decode, un GM que tipeara
    // el comando desde el cliente web desincronizaba el stream entero.
    case ServerPacketId.ShowSOSForm: return { sosList: r.readAsciiString() };
    // Propuestas de alianza/paz pendientes de un clan: nombres de clan separados por NUL.
    case ServerPacketId.AlianceProposalsList: return { guilds: r.readAsciiString() };
    case ServerPacketId.PeaceProposalsList: return { guilds: r.readAsciiString() };
    // Ficha de detalle de un clan (WriteGuildDetails): nombre, fundador/fecha, líder, URL, nº miembros, elecciones
    // abiertas, alineación, nº enemigos/aliados, puntos antifacción, códex y descripción.
    case ServerPacketId.GuildDetails: return {
      name: r.readAsciiString(), founder: r.readAsciiString(), foundationDate: r.readAsciiString(),
      leader: r.readAsciiString(), url: r.readAsciiString(), memberCount: r.readInteger(),
      electionsOpen: r.readBoolean(), alignment: r.readAsciiString(),
      enemies: r.readInteger(), allies: r.readInteger(),
      antifaction: r.readAsciiString(), codex: r.readAsciiString(), desc: r.readAsciiString(),
    };
    // Ficha de personaje (/INFO): nombre, raza/clase/género/nivel, oro/banco, 3 strings (petición/clan actual/prev),
    // 2 bools (Armada/Caos) y 2 longs (ciudadanos/criminales matados). Mismo layout que WriteCharacterInfo.
    case ServerPacketId.CharacterInfo: return {
      name: r.readAsciiString(), race: r.readByte(), class: r.readByte(), gender: r.readByte(), level: r.readByte(),
      gold: r.readLong(), bank: r.readLong(),
      pending: r.readAsciiString(), guild: r.readAsciiString(), prevGuild: r.readAsciiString(),
      royalArmy: r.readBoolean(), caosLegion: r.readBoolean(), citizensKilled: r.readLong(), criminalsKilled: r.readLong(),
    };
    // Respuesta al click izquierdo sobre un JUGADOR (VB6 WriteCharMsgStatus). `status` es el codigo con el que
    // se pinta el nombre: 10..13 = rangos de GM, 1..7 = bandos. `nivel` viene en 255 cuando el objetivo esta
    // mas de 5 niveles por encima: el original lo ESCONDE, no es un nivel real.
    case ServerPacketId.CharMsgStatus: return {
      charIndex: r.readInteger(), status: r.readByte(), porcVida: r.readLong(),
      estados: r.readInteger(), estadosEx: r.readByte(), clase: r.readByte(), nivel: r.readInteger(),
      raza: r.readByte(), donador: r.readByte(), rangoFaccion: r.readByte(),
      pareja: r.readAsciiString(), desc: r.readAsciiString(),
    };
    // Idem sobre una CRIATURA. Ojo: lo primero NO es el charIndex sino el NUMERO de su ficha. Y la vida viene
    // cruda (vidaExacta=1) para un GM o alguien con Supervivencia>=60; para el resto, en porcentaje.
    case ServerPacketId.CharMsgStatusNPC: return {
      numero: r.readInteger(), status: r.readByte(), vidaExacta: r.readByte(), vida: r.readLong(),
      estadosEx: r.readByte(), nivel: r.readInteger(), esMascota: r.readByte(), tieneDueno: r.readByte(),
    };
    case ServerPacketId.Blind: return {};
    case ServerPacketId.BlindNoMore: return {};
    case ServerPacketId.GuildChat: return { chat: r.readAsciiString() };
    case ServerPacketId.SetInvisible: return { charIndex: r.readInteger(), invisible: r.readBoolean() };
    case ServerPacketId.MiniStats: return {
      ciudadanosMatados: r.readLong(), renegadosMatados: r.readLong(), usuariosMatados: r.readLong(),
      npcsMuertos: r.readInteger(), clase: r.readByte(), raza: r.readByte(), genero: r.readByte(),
      muertesUsuario: r.readLong(), status: r.readByte(), republicanosMatados: r.readLong(),
      caosMatados: r.readLong(), armadaMatados: r.readLong(), milicianosMatados: r.readLong(),
    };
    // 27 bytes crudos: un valor por skill (eSkill 1..27), en orden de enum.
    case ServerPacketId.SendSkills: {
      const skills: number[] = [];
      for (let i = 0; i < 27; i++) skills.push(r.readByte());
      return { skills };
    }
    case ServerPacketId.NavigateToggle: return {};   // sin payload — se emite al embarcar/desembarcar (DoNavega)
    case ServerPacketId.AreaChanged: return { x: r.readByte(), y: r.readByte() };
    case ServerPacketId.LevelUp: return { skillPoints: r.readInteger() };
    case ServerPacketId.ChatOverHead: return { chat: r.readAsciiString(), charIndex: r.readInteger(), mode: r.readByte() };
    case ServerPacketId.Atributes: return { fuerza: r.readByte(), agilidad: r.readByte(), inteligencia: r.readByte(), carisma: r.readByte(), constitucion: r.readByte() };
    case ServerPacketId.MeditateToggle: return {};
    case ServerPacketId.LocaleMsg: return { msgId: r.readInteger(), args: r.readAsciiString(), mode: r.readByte(), font: r.readByte() };
    case ServerPacketId.EfectoCharParticula: return { charIndex: r.readInteger(), particle: r.readInteger(), time: r.readSingle(), remove: r.readBoolean() };
    case ServerPacketId.ChangeInventorySlotUser: return { slot: r.readByte(), accion: r.readByte(), valor: r.readInteger() };
    case ServerPacketId.UpdateHambre: return { minHam: r.readByte() };
    case ServerPacketId.UpdateSed: return { minAgu: r.readByte() };
    case ServerPacketId.UpdateHungerAndThirst: return { minAgu: r.readByte(), minHam: r.readByte() };
    case ServerPacketId.BlacksmithWeapons:
    case ServerPacketId.BlacksmithHelmet:      // el herrero manda una lista por tipo (armas/armaduras/cascos/escudos)
    case ServerPacketId.BlacksmithShield:
    case ServerPacketId.BlacksmithArmors: {
      const count = r.readInteger(); const items: Record<string, number>[] = [];
      for (let i = 0; i < count; i++) items.push({ objIndex: r.readInteger(), iron: r.readInteger(), silver: r.readInteger(), gold: r.readInteger(), obj2: r.readInteger() });
      return { count, items };
    }
    case ServerPacketId.CarpenterObjects:
    case ServerPacketId.AlquimiaObjects: {
      const count = r.readInteger(); const items: Record<string, number>[] = [];
      for (let i = 0; i < count; i++) items.push({ objIndex: r.readInteger(), material: r.readInteger(), obj2: r.readInteger() });
      return { count, items };
    }
    case ServerPacketId.SastreObjects: { // WriteSastreObjects: 5 ints/item (objIndex + 3 skins + objIndex)
      const count = r.readInteger(); const items: Record<string, number>[] = [];
      for (let i = 0; i < count; i++) items.push({ objIndex: r.readInteger(), skinWolf: r.readInteger(), skinBrownBear: r.readInteger(), skinPolarBear: r.readInteger(), obj2: r.readInteger() });
      return { count, items };
    }
    case ServerPacketId.TrainerCreatureList: return { creatures: r.readAsciiString() };
    case ServerPacketId.CommerceInit: return {};
    // goliath = 1: el panel del oro, con el oro de la bóveda y cuántos ítems tiene; 0: la bóveda de ítems, con los dos en cero.
    case ServerPacketId.BankInit: { const goliath = r.readByte(); const gold = r.readLong(); const count = r.readByte(); return { goliath, gold, count }; }
    case ServerPacketId.BankEnd: return {};
    case ServerPacketId.ChangeBankSlot: return { slot: r.readByte(), objIndex: r.readInteger(), amount: r.readInteger(), value: r.readLong() };
    case ServerPacketId.GuildLeaderInfo: return { guilds: r.readAsciiString(), members: r.readAsciiString(), news: r.readAsciiString(), requests: r.readAsciiString() };
    // WriteGuildList: a single string with every guild name, each followed by a NUL (VB6 SEPARATOR).
    case ServerPacketId.GuildList: return { guilds: r.readAsciiString() };
    case ServerPacketId.Premios: {
      const count = r.readInteger(); const credit = r.readLong();
      const prizes: Record<string, unknown>[] = [];
      for (let i = 0; i < count; i++) prizes.push({ name: r.readAsciiString(), points: r.readInteger() });
      return { count, credit, prizes };
    }
    // Lista de personajes de la cuenta (flujo real AO: ConnectAccount -> AddPJ). Layout de WriteAddPj:
    // account + count + por char: name, head/body/helmet/weapon/shield (int16), level/class (byte), map (int16), color (byte), gm (bool).
    case ServerPacketId.AddPJ: {
      const account = r.readAsciiString(); const count = r.readByte();
      const chars: Record<string, unknown>[] = [];
      for (let i = 0; i < count; i++) chars.push({
        name: r.readAsciiString(), head: r.readInteger(), body: r.readInteger(), helmet: r.readInteger(),
        weapon: r.readInteger(), shield: r.readInteger(), level: r.readByte(), clase: r.readByte(),
        map: r.readInteger(), color: r.readByte(), gm: r.readBoolean(),
      });
      return { account, count, chars };
    }
    // Le dice al cliente qué formulario abrir (1 = selección de personaje). Tiene 1 byte de payload: sin
    // este case el stream se DESINCRONIZA (no hay length-prefix).
    case ServerPacketId.AbrirFormularios: return { form: r.readByte() };
    // ShowMessageBox (VB6 PrepareMessageShowMessageBox, Protocol.bas:14596): texto + si es pregunta + acción.
    case ServerPacketId.ShowMessageBox:
      return { message: r.readAsciiString(), esPregunta: r.readBoolean(), accion: r.readByte() };
    // EjecutarAccion (VB6): Byte(Accion) + ASCIIString(Extra). Al login llega con Accion=4 (línea de novedades,
    // SendUpdate). Sin este case el stream se DESINCRONIZA (protocolo sin length-prefix).
    case ServerPacketId.EjecutarAccion: return { accion: r.readByte(), extra: r.readAsciiString() };
    // La ficha del familiar. `estado` trae los dos bits juntos: 1 = esta puesto en el mundo, 2 = muerto.
    case ServerPacketId.FamiliarInfo: {
      const tipo = r.readByte(), nombre = r.readAsciiString(), nivel = r.readByte();
      const exp = r.readLong(), elu = r.readLong();
      const minHp = r.readLong(), maxHp = r.readLong();
      const minHit = r.readLong(), maxHit = r.readLong();
      const estado = r.readByte();
      const habilidades = [r.readInteger(), r.readInteger(), r.readInteger(), r.readInteger()];
      return { tipo, nombre, nivel, exp, elu, minHp, maxHp, minHit, maxHit,
               invocado: (estado & 1) !== 0, muerto: (estado & 2) !== 0, habilidades };
    }
    case ServerPacketId.UpdateGold: return { gold: r.readLong() };
    case ServerPacketId.ChangeNPCInventorySlot: return { slot: r.readByte(), amount: r.readInteger(), price: r.readSingle(), objIndex: r.readInteger() };
    case ServerPacketId.Pong: return { time: r.readLong() };
    case ServerPacketId.UpdateTagAndStatus:
      return { charIndex: r.readInteger(), tag: r.readAsciiString(), status: r.readByte(), donador: r.readByte() };
    case ServerPacketId.ChangeInventorySlot:
      return { slot: r.readByte(), objIndex: r.readInteger(), amount: r.readInteger(),
               equipped: r.readBoolean(), price: r.readSingle(), usable: r.readByte() };
    case ServerPacketId.CharacterCreate: {
      const f: Record<string, unknown> = {
        charIndex: r.readInteger(), body: r.readInteger(), head: r.readInteger(), heading: r.readByte(),
        x: r.readByte(), y: r.readByte(), weapon: r.readInteger(), shield: r.readInteger(),
        helmet: r.readInteger(), fx: r.readInteger(), fxLoops: r.readInteger(), name: r.readAsciiString(),
      };
      // Los 9 bytes finales: [0]=privileges (== Faccion.Status, color de nick), [1]=donador, [2]=particula, 6 auras.
      f.status = r.readByte();            // status de facción (0 criminal, 1 ciudadano, 3+ ejército) — decide el color
      f.donador = r.readByte();           // insignia donador
      for (let i = 0; i < 7; i++) r.readByte(); // particula + 6 auras (aún sin usar en el cliente)
      return f;
    }
    // La bandeja de correo entera. OJO el orden de los dos ultimos campos: va la CANTIDAD y despues el
    // INDICE del objeto, al reves que en el resto de los paquetes de item del protocolo.
    case ServerPacketId.CorreoList: {
      const n = r.readByte();
      const cartas: Array<Record<string, unknown>> = [];
      for (let i = 0; i < n; i++) {
        cartas.push({
          emisor: r.readAsciiString(), carta: r.readAsciiString(),
          leida: r.readByte(), cantidad: r.readInteger(), objIndex: r.readInteger(),
        });
      }
      return { cartas };
    }
    // Un stat suelto. El HUD aplica el delta sobre lo que ya tiene: el server no reenvia el maximo.
    case ServerPacketId.UpdateSta: return { minSta: r.readInteger() };
    case ServerPacketId.UpdateMana: return { minMan: r.readInteger() };
    case ServerPacketId.UpdateHP: return { minHp: r.readInteger() };
    case ServerPacketId.UpdateExp: return { exp: r.readLong() };
    case ServerPacketId.UpdateStrenght: return { fuerza: r.readByte() };
    case ServerPacketId.UpdateDexterity: return { agilidad: r.readByte() };
    // Al subir de nivel viajan los MAXIMOS y la barra de experiencia, no los actuales.
    case ServerPacketId.UpdateUserStatsForLevel: return {
      maxHp: r.readInteger(), maxMan: r.readInteger(), maxSta: r.readInteger(),
      level: r.readInteger(), elu: r.readLong(), exp: r.readLong(),
    };
    case ServerPacketId.UpdateUserStats: {
      const f: Record<string, unknown> = {
        maxHp: r.readInteger(), minHp: r.readInteger(), maxMan: r.readInteger(), minMan: r.readInteger(),
        maxSta: r.readInteger(), minSta: r.readInteger(), gold: r.readLong(), level: r.readInteger(),
        elu: r.readLong(), exp: r.readLong(),
      };
      for (let i = 0; i < 6; i++) r.readByte(); // agi/str/ham/agu bytes
      return f;
    }
    default: return null; // unmodelled — stop parsing here
  }
}

export function decodeServerStream(data: Uint8Array): ServerEvent[] {
  const events: ServerEvent[] = [];
  const r = new ByteQueueReader(data);
  while (!r.atEnd) {
    const before = r.remaining;   // garantía de progreso (defensa en profundidad contra loops del decoder)
    let id: number;
    try { id = r.readByte(); } catch (e) { if (e instanceof NotEnoughData) break; throw e; }
    let fields: Record<string, unknown> | null;
    try { fields = readOne(r, id); }
    catch (e) { if (e instanceof NotEnoughData) break; throw e; }
    if (fields === null) { events.push({ id, name: NAMES[id] ?? `?${id}`, fields: {} }); break; }
    events.push({ id, name: NAMES[id] ?? `?${id}`, fields });
    if (r.remaining >= before) break;   // un paquete que no consumió bytes cortaría el stream en loop infinito
  }
  return events;
}
