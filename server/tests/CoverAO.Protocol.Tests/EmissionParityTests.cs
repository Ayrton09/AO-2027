using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// Golden byte-vectors for server→client emission. Until now the only permanent protocol guard was
/// <see cref="ProtocolParityTests"/>, which pins only the enum IDs — a field reorder or wrong type in a
/// builder passed every test silently (the audit's "emisión sin red de regresión" finding). These vectors
/// hand-encode the EXACT expected bytes (independent of the builder's own primitives), so any layout drift
/// in <see cref="ServerMessages"/> fails here. Format: WritePacketId=1 byte; Integer=int16 LE; Long=int32 LE;
/// ASCIIString=int16 LE char-count + cp1252 bytes; Byte=1 byte; lists NUL-joined with '\0'.
/// </summary>
public class EmissionParityTests
{
    // A minimal ObjInfo (only the fields a given list needs are set via named args).
    private static ObjInfo Obj(short index, int skinWolf = 0, int skinBrownBear = 0, int skinPolarBear = 0,
        int iron = 0, int silver = 0, int gold = 0, int wood = 0) =>
        new(index, "x", ObjType: 0, MaxModifier: 0, Value: 0, Anim: 0,
            IngotIron: iron, IngotSilver: silver, IngotGold: gold, Wood: wood,
            SkinWolf: skinWolf, SkinBrownBear: skinBrownBear, SkinPolarBear: skinPolarBear);

    [Fact]
    public void UpdateGold_is_id14_plus_int32_le()
    {
        // 1_000_000 = 0x000F4240 → LE 40 42 0F 00
        Assert.Equal(new byte[] { 14, 0x40, 0x42, 0x0F, 0x00 }, ServerMessages.UpdateGold(1_000_000));
    }

    [Fact]
    public void ConsoleMsg_is_id20_string_then_font_byte()
    {
        // "OK" → count 2 (02 00) + 'O'(79) 'K'(75), then font byte 5
        Assert.Equal(new byte[] { 20, 0x02, 0x00, 79, 75, 5 }, ServerMessages.ConsoleMsg("OK", 5));
    }

    [Fact]
    public void SastreObjects_emits_FIVE_ints_per_item() // regression guard for the 3→5 fix
    {
        // id 53, count 1, then per item: index, wolf, brown, polar, index — all int16 LE.
        // index 300 = 012C → 2C 01 ; wolf 7 ; brown 2 ; polar 0 ; index 300.
        var expected = new byte[]
        {
            53, 0x01, 0x00,
            0x2C, 0x01, 0x07, 0x00, 0x02, 0x00, 0x00, 0x00, 0x2C, 0x01,
        };
        Assert.Equal(expected, ServerMessages.SastreObjects(new[] { Obj(300, skinWolf: 7, skinBrownBear: 2, skinPolarBear: 0) }));
    }

    [Fact]
    public void CarpenterObjects_emits_THREE_ints_per_item()
    {
        // id 52, count 1, per item: index, wood, index. index 58 = 3A ; wood 4.
        var expected = new byte[] { 52, 0x01, 0x00, 0x3A, 0x00, 0x04, 0x00, 0x3A, 0x00 };
        Assert.Equal(expected, ServerMessages.CarpenterObjects(new[] { Obj(58, wood: 4) }));
    }

    [Fact]
    public void BlacksmithWeapons_emits_FIVE_ints_per_item()
    {
        // id 48, count 1, per item: index, iron, silver, gold, index. index 201 = C9 ; iron 15 ; silver 6 ; gold 2.
        var expected = new byte[]
        {
            48, 0x01, 0x00,
            0xC9, 0x00, 0x0F, 0x00, 0x06, 0x00, 0x02, 0x00, 0xC9, 0x00,
        };
        Assert.Equal(expected, ServerMessages.BlacksmithWeapons(new[] { Obj(201, iron: 15, silver: 6, gold: 2) }));
    }

    [Fact]
    public void TrainerCreatureList_is_one_NUL_joined_ascii_string()
    {
        // id 68, then ASCIIString "Lobo\0Oso": count 8 (08 00) + bytes.
        var expected = new byte[] { 68, 0x08, 0x00, 76, 111, 98, 111, 0, 79, 115, 111 };
        Assert.Equal(expected, ServerMessages.TrainerCreatureList(new[] { "Lobo", "Oso" }));
    }

    [Fact]
    public void LocaleMsg_no_args_matches_wire()
    {
        // id 83, Integer(22) LE, ASCIIString("") = len 0, mode 0, font 0. Backbone de la migracion a LocaleMsg.
        Assert.Equal(new byte[] { 83, 22, 0, 0, 0, 0, 0 }, ServerMessages.LocaleMsg(22));
    }

    [Fact]
    public void LocaleMsg_with_sigil_args_matches_wire()
    {
        // id 83, Integer(453) LE (0xC5 0x01), ASCIIString("A%^5") = len 4 + bytes, mode 0, font 0.
        // El "^" es el sigilo de hechizo que el cliente resuelve; los args van %-separados.
        var expected = new byte[] { 83, 197, 1, 4, 0, 65, 37, 94, 53, 0, 0 };
        Assert.Equal(expected, ServerMessages.LocaleMsg(453, "A%^5"));
    }

    [Fact]
    public void GuildList_nul_separated_matches_wire()
    {
        // id 36, ASCIIString con los nombres separados por NUL (VB6 SEPARATOR = vbNullChar), NUL final incluido.
        var expected = new byte[] { 36, 12, 0, 67, 108, 97, 110, 65, 0, 67, 108, 97, 110, 66, 0 };
        Assert.Equal(expected, ServerMessages.GuildList(new[] { "ClanA", "ClanB" }));
    }

    [Fact]
    public void Premios_matches_wire()
    {
        // id 89, Integer(count=1), Long(credito=0), luego por premio: ASCIIString(nombre) + Integer(puntos).
        var expected = new byte[] { 89, 1, 0, 0, 0, 0, 0, 7, 0, 110, 105, 118, 101, 108, 32, 49, 200, 0 };
        Assert.Equal(expected, ServerMessages.Premios(new (string, short)[] { ("nivel 1", 200) }, 0));
    }

    [Fact]
    public void WorkRequestTarget_matches_wire()
    {
        // id 43, Byte(skill). VB6 HandleWork responde esto para las skills de trabajo (talar=20).
        Assert.Equal(new byte[] { 43, 20 }, ServerMessages.WorkRequestTarget(20));
    }

    [Fact]
    public void UpdateHungerAndThirst_matches_wire()
    {
        // id 60, Byte(MinAGU=sed) primero, Byte(MinHam=hambre). Orden fiel al VB6.
        Assert.Equal(new byte[] { 60, 90, 80 }, ServerMessages.UpdateHungerAndThirst(90, 80));
    }

    // ---- CharacterCreate: los dos campos que el oráculo VB6 destapó como NO byte-fieles -----------------
    // El golden `docs/vb6-oracle-bootstrap.txt` los fija: un ciudadano viaja con Privileges=2 (línea 68,
    // ORACLEHERO) y un NPC viaja con el NÚMERO de su ficha como "nombre" (Name='43', '594', …).

    [Fact]
    public void CharacterCreate_de_un_jugador_manda_el_status_del_VB6_no_el_del_slice()
    {
        var p = new Player { Name = "X", Account = "cuenta", CharIndex = 1, FactionStatus = Player.StatusCiudadano };
        var bytes = ServerMessages.CharacterCreate(p);
        // Layout hasta el byte de Privileges: id + CharIndex,Body,Head (int16 ×3) + Heading,X,Y (byte ×3)
        // + Weapon,Shield,Helmet,FX,FXLoops (int16 ×5) + ASCIIString(nombre) = 1+6+3+10+2+1 = 23.
        Assert.Equal(2, bytes[23]);   // ciudadano imperial: el oráculo manda 2, NO 1
        Assert.Equal(Player.StatusCriminal, (byte)1);    // renegado = 1 (VB6 eFaccion), no 0
        Assert.Equal(Player.StatusCiudadano, (byte)2);
    }

    [Fact]
    public void CharacterCreate_de_un_npc_manda_su_numero_como_nombre_y_su_status()
    {
        var ficha = new NpcInfo(Index: 594, Name: "Gallina Salvaje", Body: 1, Head: 0, Heading: 3,
            MaxHp: 10, MinHit: 1, MaxHit: 2, Def: 0, GiveExp: 1, GiveGld: 0, PoderEvasion: 0, PoderAtaque: 0,
            Status: 4);
        var npc = new Npc { CharIndex = 7, Template = ficha, Hp = 10 };
        var bytes = ServerMessages.CharacterCreate(npc);
        // ASCIIString del nombre: arranca en el offset 20 (1+6+3+10) con el int16 LE de la cantidad de chars.
        Assert.Equal(3, bytes[20]);                       // "594" son 3 caracteres, no "Gallina Salvaje"
        Assert.Equal(new byte[] { 53, 57, 52 }, bytes[22..25]);   // '5','9','4'
        Assert.Equal(4, bytes[25]);                       // Privileges = flags.Status de la ficha (Caos)
    }

    // ---- Los dos emisores que estaban mal clasificados como código muerto -------------------------------

    [Fact]
    public void RemoveDialogs_es_solo_el_id()
    {
        // id 2, sin payload (VB6 WriteRemoveAllDialogs, Protocol.bas:11424). El nombre del emisor no coincide
        // con el del paquete, y por eso el grep lo daba por muerto.
        Assert.Equal(new byte[] { 2 }, ServerMessages.RemoveDialogs());
    }

    [Fact]
    public void MostrarUbicacion_matches_wire()
    {
        // id 97, ASCIIString(nombre) + Byte(numAmigo) + Integer(mapa) + Byte(x) + Byte(y).
        // "Ana" → 03 00 'A''n''a'; slot 2; mapa 0 → 00 00; x 0; y 0 (el "se desconectó" del original).
        var expected = new byte[] { 97, 0x03, 0x00, 65, 110, 97, 2, 0x00, 0x00, 0, 0 };
        Assert.Equal(expected, ServerMessages.MostrarUbicacion("Ana", 2, 0, 0, 0));
    }

    [Fact]
    public void Survival_decay_steps_by_10_and_floors_at_zero()
    {
        Assert.Equal(90, Survival.Decay(100));
        Assert.Equal(0, Survival.Decay(10));
        Assert.Equal(0, Survival.Decay(5));   // no baja de 0
        Assert.Equal(0, Survival.Decay(0));
    }
    // ---- Actualizaciones granulares de stats ----------------------------------------------------------
    // El original manda 3-5 bytes cuando cambia UN stat y reserva el UpdateUserStats gordo para cuando
    // cambia todo. Estos vectores fijan el layout: el riesgo real acá no es que falte el paquete, es que
    // tenga el TIPO equivocado — un Integer donde va un Long corre dos bytes y, como el protocolo no lleva
    // length-prefix, desincroniza el stream entero a partir de ahí.

    [Fact]
    public void UpdateSta_es_id11_mas_int16_le()
    {
        // WriteUpdateSta (Protocol.bas:11673): Byte(id) + Integer(MinSta). 300 = 0x012C -> LE 2C 01
        Assert.Equal(new byte[] { 11, 0x2C, 0x01 }, ServerMessages.UpdateSta(300));
    }

    [Fact]
    public void UpdateMana_es_id12_mas_int16_le()
    {
        Assert.Equal(new byte[] { 12, 0x2C, 0x01 }, ServerMessages.UpdateMana(300));
    }

    [Fact]
    public void UpdateHP_es_id13_mas_int16_le()
    {
        Assert.Equal(new byte[] { 13, 0x2C, 0x01 }, ServerMessages.UpdateHP(300));
    }

    [Fact]
    public void UpdateExp_es_id15_mas_int32_le()
    {
        // WriteUpdateExp manda un Long, no un Integer: la experiencia pasa los 32767 enseguida.
        Assert.Equal(new byte[] { 15, 0x40, 0x42, 0x0F, 0x00 }, ServerMessages.UpdateExp(1_000_000));
    }

    [Fact]
    public void Fuerza_y_agilidad_son_un_solo_byte()
    {
        // WriteUpdateStrenght (Protocol.bas:11855) / WriteUpdateDexterity (:11888). El typo "Strenght" es
        // del original y es parte del identificador del protocolo: no se corrige.
        Assert.Equal(new byte[] { 87, 18 }, ServerMessages.UpdateStrenght(18));
        Assert.Equal(new byte[] { 88, 21 }, ServerMessages.UpdateDexterity(21));
    }

    [Fact]
    public void UpdateUserStatsForLevel_manda_los_MAXIMOS_y_la_barra()
    {
        // WriteUpdateUserStatsForLevel (Protocol.bas:12649):
        //   Integer(MaxHP) Integer(MaxMAN) Integer(MaxSta) Integer(ELV) Long(ELU) Long(Exp)
        // OJO: van los MAXIMOS, no los actuales. Poner Hp donde va MaxHp compila igual y pasa desapercibido
        // en el cliente hasta que alguien sube de nivel con la vida a medias.
        var p = new Player { CharIndex = 1, Name = "Test", Account = "c",
                             MaxHp = 500, Hp = 3, MaxMana = 400, Mana = 2, MaxStamina = 300, Stamina = 1,
                             Level = 25, Elu = 70_000, Exp = 65_536 };
        Assert.Equal(new byte[]
        {
            42,
            0xF4, 0x01,             // MaxHP 500
            0x90, 0x01,             // MaxMAN 400
            0x2C, 0x01,             // MaxSta 300
            0x19, 0x00,             // ELV 25
            0x70, 0x11, 0x01, 0x00, // ELU 70000
            0x00, 0x00, 0x01, 0x00, // Exp 65536
        }, ServerMessages.UpdateUserStatsForLevel(p));
    }
    [Fact]
    public void CorreoList_manda_la_cantidad_antes_que_el_indice_del_objeto()
    {
        // WriteCorreoList (Protocol.bas:15953): Byte(n) y por carta
        //   ASCIIString(Emisor) ASCIIString(Carta) Byte(Leida) Integer(ObjetoCantidad) Integer(ObjetoIndex)
        // El orden de los dos ultimos es el que importa: va la CANTIDAD y despues el INDICE, al reves que en
        // el resto de los paquetes de item del protocolo. Invertirlos compila igual y el cliente muestra
        // "3 unidades del objeto 500" como "500 unidades del objeto 3".
        var cartas = new List<CoverAO.Server.Game.MailEntry>
        {
            new("Ana", "hola", ObjIndex: 500, Amount: 3, Leida: 1),
        };
        Assert.Equal(new byte[]
        {
            86,
            1,                       // una carta
            3, 0, (byte)'A', (byte)'n', (byte)'a',
            4, 0, (byte)'h', (byte)'o', (byte)'l', (byte)'a',
            1,                       // leida
            3, 0,                    // ObjetoCantidad = 3
            0xF4, 0x01,              // ObjetoIndex = 500
        }, ServerMessages.CorreoList(cartas));
    }

    [Fact]
    public void CorreoList_con_el_buzon_vacio_es_id_mas_cero()
    {
        Assert.Equal(new byte[] { 86, 0 }, ServerMessages.CorreoList(new List<CoverAO.Server.Game.MailEntry>()));
    }

    [Fact]
    public void Una_carta_persistida_sin_el_flag_Leida_sigue_cargando()
    {
        // El campo Leida se agrego despues: los buzones ya guardados en data/ no lo tienen. Si el record no
        // le diera default, System.Text.Json tiraria al deserializar y el server arrancaria con el correo
        // de todos vacio, sin avisar.
        var vieja = System.Text.Json.JsonSerializer.Deserialize<CoverAO.Server.Game.MailEntry>(
            """{"Sender":"Ana","Message":"hola","ObjIndex":500,"Amount":3}""");
        Assert.NotNull(vieja);
        Assert.Equal("Ana", vieja!.Sender);
        Assert.Equal(0, vieja.Leida);   // se asume NO leida
    }

    [Fact]
    public void CharacterInfo_la_ficha_del_personaje_matches_wire()
    {
        // ESTE EMISOR NO LO MIRA NADIE MAS. Sus dos call sites -el /INFO del GM y la ficha de miembro de clan-
        // quedan los dos FUERA del barrido del oraculo: el barrido manda cada accion EN FRIO y esas dos piden
        // estado (ser GM / ser lider de un clan). O sea que un campo corrido acá no lo caza ni el oraculo ni
        // ningun flow: el flow verifica que la ficha LLEGUE y de quien es, no sus bytes.
        //
        // Layout del original (VB6 WriteCharacterInfo, Protocol.bas:13843): nombre, raza, clase, genero,
        // nivel, oro, banco, 3 ASCIIString (pedidos / clan actual / clanes anteriores), 2 Boolean (Armada,
        // Caos) y 2 Long (ciudadanos y criminales matados).
        var p = new Player
        {
            CharIndex = 1, Name = "Ana", Account = "cuenta_test",
            Race = 2, Class = 3, Gender = 1, Level = 12,
            Gold = 258, Bank = 1, GuildName = "Clan",
        };
        var esperado = new byte[]
        {
            73,                       // id CharacterInfo
            3, 0, 65, 110, 97,        // ASCIIString "Ana": len 3 LE + bytes
            2, 3, 1,                  // raza, clase, genero
            12,                       // nivel
            2, 1, 0, 0,               // oro 258 = 0x0102 LE en 4 bytes
            1, 0, 0, 0,               // banco 1
            0, 0,                     // ASCIIString "" (pedidos)
            4, 0, 67, 108, 97, 110,   // ASCIIString "Clan" (clan actual)
            0, 0,                     // ASCIIString "" (clanes anteriores)
            0, 0,                     // Boolean Armada, Boolean Caos
            0, 0, 0, 0,               // Long ciudadanos matados
            0, 0, 0, 0,               // Long criminales matados
        };
        Assert.Equal(esperado, ServerMessages.CharacterInfo(p));
    }
}
