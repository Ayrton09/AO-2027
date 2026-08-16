/**
 * Server→client packet decoding for the web client (Fase 3). Reads the SC byte stream (server→client
 * is plaintext) into structured events the renderer/HUD can consume. Layouts match fase0/spec.
 * Not every SC packet is modelled yet — unknown ids stop the parse cleanly (no desync).
 */
import { ByteQueueReader, NotEnoughData } from '../codec/bytequeue.js';
export var ServerPacketId;
(function (ServerPacketId) {
    ServerPacketId[ServerPacketId["LoggedSuccessful"] = 0] = "LoggedSuccessful";
    ServerPacketId[ServerPacketId["Logged"] = 1] = "Logged";
    ServerPacketId[ServerPacketId["ConsoleMsg"] = 20] = "ConsoleMsg";
    ServerPacketId[ServerPacketId["ChangeMap"] = 16] = "ChangeMap";
    ServerPacketId[ServerPacketId["PosUpdate"] = 17] = "PosUpdate";
    ServerPacketId[ServerPacketId["UserIndexInServer"] = 23] = "UserIndexInServer";
    ServerPacketId[ServerPacketId["UserCharIndexInServer"] = 24] = "UserCharIndexInServer";
    ServerPacketId[ServerPacketId["CharacterCreate"] = 25] = "CharacterCreate";
    ServerPacketId[ServerPacketId["CharacterRemove"] = 26] = "CharacterRemove";
    ServerPacketId[ServerPacketId["CharacterMove"] = 27] = "CharacterMove";
    ServerPacketId[ServerPacketId["ForceCharMove"] = 28] = "ForceCharMove";
    ServerPacketId[ServerPacketId["CharacterChange"] = 29] = "CharacterChange";
    ServerPacketId[ServerPacketId["ObjectCreate"] = 31] = "ObjectCreate";
    ServerPacketId[ServerPacketId["ObjectDelete"] = 32] = "ObjectDelete";
    ServerPacketId[ServerPacketId["BlockPosition"] = 33] = "BlockPosition";
    ServerPacketId[ServerPacketId["ChangeInventorySlot"] = 44] = "ChangeInventorySlot";
    ServerPacketId[ServerPacketId["ChangeSpellSlot"] = 46] = "ChangeSpellSlot";
    ServerPacketId[ServerPacketId["UpdateUserStats"] = 41] = "UpdateUserStats";
    ServerPacketId[ServerPacketId["PlayMidi"] = 34] = "PlayMidi";
    ServerPacketId[ServerPacketId["PlayWave"] = 35] = "PlayWave";
    ServerPacketId[ServerPacketId["Pong"] = 81] = "Pong";
    ServerPacketId[ServerPacketId["UpdateTagAndStatus"] = 82] = "UpdateTagAndStatus";
    ServerPacketId[ServerPacketId["RainToggle"] = 39] = "RainToggle";
    ServerPacketId[ServerPacketId["CreateFX"] = 40] = "CreateFX";
    ServerPacketId[ServerPacketId["Dumb"] = 58] = "Dumb";
    ServerPacketId[ServerPacketId["DumbNoMore"] = 66] = "DumbNoMore";
    ServerPacketId[ServerPacketId["AreaChanged"] = 37] = "AreaChanged";
    ServerPacketId[ServerPacketId["LevelUp"] = 62] = "LevelUp";
    ServerPacketId[ServerPacketId["ChatOverHead"] = 18] = "ChatOverHead";
    ServerPacketId[ServerPacketId["Atributes"] = 47] = "Atributes";
    ServerPacketId[ServerPacketId["MeditateToggle"] = 64] = "MeditateToggle";
    ServerPacketId[ServerPacketId["LocaleMsg"] = 83] = "LocaleMsg";
    ServerPacketId[ServerPacketId["EfectoCharParticula"] = 90] = "EfectoCharParticula";
    ServerPacketId[ServerPacketId["ChangeInventorySlotUser"] = 102] = "ChangeInventorySlotUser";
    ServerPacketId[ServerPacketId["UpdateSed"] = 104] = "UpdateSed";
    ServerPacketId[ServerPacketId["UpdateHambre"] = 105] = "UpdateHambre";
    ServerPacketId[ServerPacketId["UpdateHungerAndThirst"] = 60] = "UpdateHungerAndThirst";
    // Actualizaciones de UN stat. El original las usa en los caminos calientes (cada golpe, cada vuelta de
    // regeneracion, cada tick de un oficio) y deja el UpdateUserStats gordo para cuando cambia todo junto.
    ServerPacketId[ServerPacketId["UpdateSta"] = 11] = "UpdateSta";
    ServerPacketId[ServerPacketId["UpdateMana"] = 12] = "UpdateMana";
    ServerPacketId[ServerPacketId["UpdateHP"] = 13] = "UpdateHP";
    ServerPacketId[ServerPacketId["UpdateExp"] = 15] = "UpdateExp";
    ServerPacketId[ServerPacketId["UpdateUserStatsForLevel"] = 42] = "UpdateUserStatsForLevel";
    ServerPacketId[ServerPacketId["UpdateStrenght"] = 87] = "UpdateStrenght";
    ServerPacketId[ServerPacketId["UpdateDexterity"] = 88] = "UpdateDexterity";
    ServerPacketId[ServerPacketId["BlacksmithWeapons"] = 48] = "BlacksmithWeapons";
    ServerPacketId[ServerPacketId["BlacksmithArmors"] = 49] = "BlacksmithArmors";
    ServerPacketId[ServerPacketId["BlacksmithHelmet"] = 50] = "BlacksmithHelmet";
    ServerPacketId[ServerPacketId["BlacksmithShield"] = 51] = "BlacksmithShield";
    ServerPacketId[ServerPacketId["CarpenterObjects"] = 52] = "CarpenterObjects";
    ServerPacketId[ServerPacketId["SastreObjects"] = 53] = "SastreObjects";
    ServerPacketId[ServerPacketId["AlquimiaObjects"] = 54] = "AlquimiaObjects";
    ServerPacketId[ServerPacketId["TrainerCreatureList"] = 68] = "TrainerCreatureList";
    ServerPacketId[ServerPacketId["CommerceInit"] = 9] = "CommerceInit";
    ServerPacketId[ServerPacketId["BankInit"] = 10] = "BankInit";
    ServerPacketId[ServerPacketId["ChangeNPCInventorySlot"] = 59] = "ChangeNPCInventorySlot";
    ServerPacketId[ServerPacketId["ChangeBankSlot"] = 45] = "ChangeBankSlot";
    ServerPacketId[ServerPacketId["BankEnd"] = 8] = "BankEnd";
    ServerPacketId[ServerPacketId["GuildLeaderInfo"] = 74] = "GuildLeaderInfo";
    ServerPacketId[ServerPacketId["UpdateGold"] = 14] = "UpdateGold";
    ServerPacketId[ServerPacketId["GuildList"] = 36] = "GuildList";
    ServerPacketId[ServerPacketId["Premios"] = 89] = "Premios";
    ServerPacketId[ServerPacketId["NavigateToggle"] = 4] = "NavigateToggle";
    ServerPacketId[ServerPacketId["MontateToggle"] = 5] = "MontateToggle";
    ServerPacketId[ServerPacketId["CommerceEnd"] = 7] = "CommerceEnd";
    ServerPacketId[ServerPacketId["RestOK"] = 55] = "RestOK";
    ServerPacketId[ServerPacketId["CharacterInfo"] = 73] = "CharacterInfo";
    ServerPacketId[ServerPacketId["ParalizeOK"] = 77] = "ParalizeOK";
    ServerPacketId[ServerPacketId["UserNameList"] = 85] = "UserNameList";
    ServerPacketId[ServerPacketId["ShowSOSForm"] = 84] = "ShowSOSForm";
    ServerPacketId[ServerPacketId["SendMsgBox"] = 56] = "SendMsgBox";
    ServerPacketId[ServerPacketId["ShowUserRequest"] = 78] = "ShowUserRequest";
    // Equipar/desequipar: el server manda un slot suelto y, si el item brilla, su aura.
    ServerPacketId[ServerPacketId["CharacterChangeSlot"] = 30] = "CharacterChangeSlot";
    ServerPacketId[ServerPacketId["AuraToChar"] = 103] = "AuraToChar";
    // Salida limpia, pausa del mundo y borrado del dialogo sobre la cabeza.
    ServerPacketId[ServerPacketId["Disconnect"] = 6] = "Disconnect";
    ServerPacketId[ServerPacketId["PauseToggle"] = 38] = "PauseToggle";
    ServerPacketId[ServerPacketId["RemoveCharDialog"] = 3] = "RemoveCharDialog";
    ServerPacketId[ServerPacketId["RemoveDialogs"] = 2] = "RemoveDialogs";
    // Ubicacion de un amigo. Llega al desconectarse alguien de tu lista, con mapa/x/y en 0.
    ServerPacketId[ServerPacketId["MostrarUbicacion"] = 97] = "MostrarUbicacion";
    // Clanes y facciones: propuesta recibida, panel del miembro comun, y el recolor del nick.
    ServerPacketId[ServerPacketId["OfferDetails"] = 70] = "OfferDetails";
    ServerPacketId[ServerPacketId["GuildMemberInfo"] = 75] = "GuildMemberInfo";
    ServerPacketId[ServerPacketId["CharStatus"] = 94] = "CharStatus";
    // FX de un hechizo sobre el TERRENO (van por coordenadas, sin charIndex al que colgarse) y el indicador
    // de correo pendiente. Los dos EfectoTerreno* los emiten los hechizos de AREA, que resuelven el 3x3.
    ServerPacketId[ServerPacketId["EfectoTerrenoFX"] = 93] = "EfectoTerrenoFX";
    ServerPacketId[ServerPacketId["EfectoTerrenoParticula"] = 92] = "EfectoTerrenoParticula";
    ServerPacketId[ServerPacketId["MensajeSigno"] = 95] = "MensajeSigno";
    ServerPacketId[ServerPacketId["AlianceProposalsList"] = 71] = "AlianceProposalsList";
    ServerPacketId[ServerPacketId["PeaceProposalsList"] = 72] = "PeaceProposalsList";
    ServerPacketId[ServerPacketId["GuildDetails"] = 76] = "GuildDetails";
    ServerPacketId[ServerPacketId["AddPJ"] = 91] = "AddPJ";
    ServerPacketId[ServerPacketId["AbrirFormularios"] = 101] = "AbrirFormularios";
    ServerPacketId[ServerPacketId["EjecutarAccion"] = 106] = "EjecutarAccion";
    ServerPacketId[ServerPacketId["CorreoList"] = 86] = "CorreoList";
    // El cuadro de dialogo del server. Es EL paquete de los rechazos de login (personaje ya conectado, otra
    // sesion de la misma cuenta): sin decodificarlo, el cliente quedaba en "logueando..." para siempre y ademas
    // se le desincronizaba el stream, porque no sabia cuantos bytes comerse.
    ServerPacketId[ServerPacketId["ShowMessageBox"] = 22] = "ShowMessageBox";
    // Sin estos ids el stream se DESINCRONIZA: el protocolo no lleva length-prefix, asi que un paquete
    // que el decoder no sabe leer se come los bytes del siguiente.
    ServerPacketId[ServerPacketId["GuildChat"] = 21] = "GuildChat";
    ServerPacketId[ServerPacketId["Blind"] = 57] = "Blind";
    ServerPacketId[ServerPacketId["MiniStats"] = 61] = "MiniStats";
    ServerPacketId[ServerPacketId["SetInvisible"] = 63] = "SetInvisible";
    ServerPacketId[ServerPacketId["BlindNoMore"] = 65] = "BlindNoMore";
    ServerPacketId[ServerPacketId["SendSkills"] = 67] = "SendSkills";
    ServerPacketId[ServerPacketId["WorkRequestTarget"] = 43] = "WorkRequestTarget";
    ServerPacketId[ServerPacketId["ChatOverHeadLocale"] = 19] = "ChatOverHeadLocale";
    // La respuesta al click izquierdo: el server NO manda una frase, manda los datos y el cliente arma la linea.
    ServerPacketId[ServerPacketId["CharMsgStatus"] = 99] = "CharMsgStatus";
    ServerPacketId[ServerPacketId["CharMsgStatusNPC"] = 100] = "CharMsgStatusNPC";
    // Los dos ultimos emisores VIVOS que el decoder no modelaba, o sea que CORTABAN el frame: la respuesta a
    // ShowGuildNews (el cliente pedia las noticias del clan y no podia leerlas) y el ack de cada operacion de
    // boveda. El nombre del id va en minuscula porque asi lo bautiza el protocolo del fork (guildNews).
    ServerPacketId[ServerPacketId["guildNews"] = 69] = "guildNews";
    ServerPacketId[ServerPacketId["BankOK"] = 80] = "BankOK";
})(ServerPacketId || (ServerPacketId = {}));
const NAMES = Object.fromEntries(Object.entries(ServerPacketId).filter(([, v]) => typeof v === 'number').map(([k, v]) => [v, k]));
/** Reads one packet's fields; returns null for an unmodelled id (caller stops). */
function readOne(r, id) {
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
            const skills = [];
            for (let i = 0; i < 27; i++)
                skills.push(r.readByte());
            return { skills };
        }
        case ServerPacketId.NavigateToggle: return {}; // sin payload — se emite al embarcar/desembarcar (DoNavega)
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
        case ServerPacketId.BlacksmithHelmet: // el herrero manda una lista por tipo (armas/armaduras/cascos/escudos)
        case ServerPacketId.BlacksmithShield:
        case ServerPacketId.BlacksmithArmors: {
            const count = r.readInteger();
            const items = [];
            for (let i = 0; i < count; i++)
                items.push({ objIndex: r.readInteger(), iron: r.readInteger(), silver: r.readInteger(), gold: r.readInteger(), obj2: r.readInteger() });
            return { count, items };
        }
        case ServerPacketId.CarpenterObjects:
        case ServerPacketId.AlquimiaObjects: {
            const count = r.readInteger();
            const items = [];
            for (let i = 0; i < count; i++)
                items.push({ objIndex: r.readInteger(), material: r.readInteger(), obj2: r.readInteger() });
            return { count, items };
        }
        case ServerPacketId.SastreObjects: { // WriteSastreObjects: 5 ints/item (objIndex + 3 skins + objIndex)
            const count = r.readInteger();
            const items = [];
            for (let i = 0; i < count; i++)
                items.push({ objIndex: r.readInteger(), skinWolf: r.readInteger(), skinBrownBear: r.readInteger(), skinPolarBear: r.readInteger(), obj2: r.readInteger() });
            return { count, items };
        }
        case ServerPacketId.TrainerCreatureList: return { creatures: r.readAsciiString() };
        case ServerPacketId.CommerceInit: return {};
        case ServerPacketId.BankInit: {
            r.readByte();
            const gold = r.readLong();
            const count = r.readByte();
            return { gold, count };
        } // byte(legacy)+long(gold)+byte(count)
        case ServerPacketId.BankEnd: return {};
        case ServerPacketId.ChangeBankSlot: return { slot: r.readByte(), objIndex: r.readInteger(), amount: r.readInteger(), value: r.readLong() };
        case ServerPacketId.GuildLeaderInfo: return { guilds: r.readAsciiString(), members: r.readAsciiString(), news: r.readAsciiString(), requests: r.readAsciiString() };
        // WriteGuildList: a single string with every guild name, each followed by a NUL (VB6 SEPARATOR).
        case ServerPacketId.GuildList: return { guilds: r.readAsciiString() };
        case ServerPacketId.Premios: {
            const count = r.readInteger();
            const credit = r.readLong();
            const prizes = [];
            for (let i = 0; i < count; i++)
                prizes.push({ name: r.readAsciiString(), points: r.readInteger() });
            return { count, credit, prizes };
        }
        // Lista de personajes de la cuenta (flujo real AO: ConnectAccount -> AddPJ). Layout de WriteAddPj:
        // account + count + por char: name, head/body/helmet/weapon/shield (int16), level/class (byte), map (int16), color (byte), gm (bool).
        case ServerPacketId.AddPJ: {
            const account = r.readAsciiString();
            const count = r.readByte();
            const chars = [];
            for (let i = 0; i < count; i++)
                chars.push({
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
        case ServerPacketId.UpdateGold: return { gold: r.readLong() };
        case ServerPacketId.ChangeNPCInventorySlot: return { slot: r.readByte(), amount: r.readInteger(), price: r.readSingle(), objIndex: r.readInteger() };
        case ServerPacketId.Pong: return { time: r.readLong() };
        case ServerPacketId.UpdateTagAndStatus:
            return { charIndex: r.readInteger(), tag: r.readAsciiString(), status: r.readByte(), donador: r.readByte() };
        case ServerPacketId.ChangeInventorySlot:
            return { slot: r.readByte(), objIndex: r.readInteger(), amount: r.readInteger(),
                equipped: r.readBoolean(), price: r.readSingle(), usable: r.readByte() };
        case ServerPacketId.CharacterCreate: {
            const f = {
                charIndex: r.readInteger(), body: r.readInteger(), head: r.readInteger(), heading: r.readByte(),
                x: r.readByte(), y: r.readByte(), weapon: r.readInteger(), shield: r.readInteger(),
                helmet: r.readInteger(), fx: r.readInteger(), fxLoops: r.readInteger(), name: r.readAsciiString(),
            };
            // Los 9 bytes finales: [0]=privileges (== Faccion.Status, color de nick), [1]=donador, [2]=particula, 6 auras.
            f.status = r.readByte(); // status de facción (0 criminal, 1 ciudadano, 3+ ejército) — decide el color
            f.donador = r.readByte(); // insignia donador
            for (let i = 0; i < 7; i++)
                r.readByte(); // particula + 6 auras (aún sin usar en el cliente)
            return f;
        }
        // La bandeja de correo entera. OJO el orden de los dos ultimos campos: va la CANTIDAD y despues el
        // INDICE del objeto, al reves que en el resto de los paquetes de item del protocolo.
        case ServerPacketId.CorreoList: {
            const n = r.readByte();
            const cartas = [];
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
            const f = {
                maxHp: r.readInteger(), minHp: r.readInteger(), maxMan: r.readInteger(), minMan: r.readInteger(),
                maxSta: r.readInteger(), minSta: r.readInteger(), gold: r.readLong(), level: r.readInteger(),
                elu: r.readLong(), exp: r.readLong(),
            };
            for (let i = 0; i < 6; i++)
                r.readByte(); // agi/str/ham/agu bytes
            return f;
        }
        default: return null; // unmodelled — stop parsing here
    }
}
export function decodeServerStream(data) {
    const events = [];
    const r = new ByteQueueReader(data);
    while (!r.atEnd) {
        const before = r.remaining; // garantía de progreso (defensa en profundidad contra loops del decoder)
        let id;
        try {
            id = r.readByte();
        }
        catch (e) {
            if (e instanceof NotEnoughData)
                break;
            throw e;
        }
        let fields;
        try {
            fields = readOne(r, id);
        }
        catch (e) {
            if (e instanceof NotEnoughData)
                break;
            throw e;
        }
        if (fields === null) {
            events.push({ id, name: NAMES[id] ?? `?${id}`, fields: {} });
            break;
        }
        events.push({ id, name: NAMES[id] ?? `?${id}`, fields });
        if (r.remaining >= before)
            break; // un paquete que no consumió bytes cortaría el stream en loop infinito
    }
    return events;
}
