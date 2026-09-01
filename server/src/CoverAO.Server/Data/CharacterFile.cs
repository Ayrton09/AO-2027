using System.Globalization;
using CoverAO.Server.Game;

namespace CoverAO.Server.Data;

/// <summary>
/// Reads/writes the character .chr file (INI) faithfully to the original SaveUser/LoadUser format
/// (FileIO.bas:1588 / TCP.bas:1832). Compound values use '-' delimiters: Position="map-x-y",
/// inventory Obj="objindex-amount-equipped", spells "H{i}". Round-trips the sections the server
/// models; other sections (FLAGS, FACCIONES, CORREO, ...) get sane defaults so the file is valid.
/// </summary>
public static class CharacterFile
{
    private static string F(int v) => v.ToString(CultureInfo.InvariantCulture);

    public static IniFile ToIni(Player p)
    {
        var ini = new IniFile();

        // El status VIVO puede ser el de GM (7/8/9) que le puso el Server.ini; al .chr va el civil de abajo.
        ini.Set("FACCIONES", "Status", F(p.FactionStatusPersistido));
        ini.Set("FACCIONES", "Rango", F(p.FactionRank));                 // rango en el ejército (VB6 Faccion.Rango)
        // Un contador por bando de la víctima (VB6 Faccion.*Matados): cada ejército suma un subconjunto distinto.
        ini.Set("FACCIONES", "CrimMatados", F(p.RenegadosMatados));
        ini.Set("FACCIONES", "CiudadanosMatados", F(p.CiudadanosMatados));
        ini.Set("FACCIONES", "RepublicanosMatados", F(p.RepublicanosMatados));
        ini.Set("FACCIONES", "ArmadaMatados", F(p.ArmadaMatados));
        ini.Set("FACCIONES", "MilicianosMatados", F(p.MilicianosMatados));
        ini.Set("FACCIONES", "CaosMatados", F(p.CaosMatados));
        ini.Set("FLAGS", "Privilegios", F(p.PrivilegiosPersistidos));   // el civil, no el que puso el Server.ini
        ini.Set("FLAGS", "Ban", F(p.Banned ? 1 : 0));       // VB6 flags.Ban — baneo por personaje (/BAN)
        ini.Set("FLAGS", "Muerto", F(p.IsAlive ? 0 : 1));   // VB6 FileIO.bas:1626 — la muerte persiste entre sesiones
        // VB6 FileIO.bas:1632 lo GUARDA y TCP.bas:1866 lo RELEE: quien desloguea navegando vuelve navegando.
        // Sin esto reaparecía parado en medio del mar y a pie — con el gate de agua (`IsWater != Navigating`)
        // rechazándole los cuatro vecinos, hasta que se le ocurriera volver a usar el barco del inventario.
        ini.Set("FLAGS", "Navegando", F(p.Navigating ? 1 : 0));
        // `Montando` está en el .chr una línea DEBAJO de `Navegando` en las dos puntas del VB6
        // (FileIO.bas:1633 lo escribe, TCP.bas:1867 lo relee) y se había quedado afuera con el otro.
        //
        // Faltando, el estado quedaba PARTIDO al reloguear: el cuerpo es el Ropaje de la montura y el ítem
        // sigue marcado como equipado —las dos cosas SÍ persisten— pero el flag volvía en false. O sea que
        // aparecías a caballo, a la vista de todos, y el server te trataba como si fueras a pie: podías
        // meterte a caballo bajo techo (`DesmontarSiEntraBajoTecho` no dispara sin el flag), el arma no se
        // ocultaba, y para bajarte había que tocar la montura dos veces.
        ini.Set("FLAGS", "Montando", F(p.Mounted ? 1 : 0));
        // `Envenenado` (FileIO.bas:1634 / TCP.bas:1868). En el VB6 el veneno NO caduca solo: el flag se apaga
        // únicamente al curarse —sacerdote (Acciones.bas:318), poción o el hechizo de curar veneno—, y el
        // nuestro funciona igual (`PoisonedUntilTick = long.MaxValue`). Sin persistirlo, desloguear y volver a
        // entrar te curaba gratis.
        //
        // El resto del bloque FLAGS del VB6 se miró y NO se porta, por motivo y no por olvido:
        //   · Paralizado / Escondido — el flag va con un contador (`Contadores.Paralisis`, `TiempoOculto`) que
        //     el .chr no guarda, así que reponerlo solo lo apaga el primer tick: el par no cierra.
        //   · Incinerado — nuestro incinerar comparte estado con el veneno, no hay campo separado que guardar.
        //   · Hambre / Sed / Desnudo — derivados del contador de comida/agua y del equipo, que ya persisten.
        ini.Set("FLAGS", "Envenenado", F(p.PoisonedUntilTick == long.MaxValue ? 1 : 0));
        // VB6 flags.MuertesUsuario (Modulo_UsUaRiOs.bas:1260, +1 en UserDie). La ficha del cliente lo muestra
        // como "Veces que moriste"; el .chr lo llama `Murio` (FileIO.bas:1638).
        ini.Set("FLAGS", "Murio", F(p.Deaths));
        // [MUERTES] (VB6 FileIO.bas:1774): las dos cuentas que el cliente muestra en la ficha. Vivian SOLO en
        // memoria — se emitian en MiniStats y se perdian al salir, asi que el jugador veia su historial en cero
        // cada vez que reconectaba.
        ini.Set("MUERTES", "UserMuertes", F(p.UsersKilled));
        ini.Set("MUERTES", "NpcsMuertes", F(p.NpcsKilled));
        // [CASAMIENTO] (VB6 FileIO.bas:1702). Mismo problema: uno se casaba y al reconectar estaba soltero.
        // `Casado` es redundante con tener pareja, pero se escribe igual porque es lo que el original guarda.
        ini.Set("CASAMIENTO", "Pareja", p.SpouseName);
        ini.Set("CASAMIENTO", "Casado", F(string.IsNullOrEmpty(p.SpouseName) ? 0 : 1));
        ini.Set("GUILD", "GuildName", p.GuildName);
        ini.Set("AMIGOS", "Cantidad", F(p.Friends.Count));
        for (int i = 0; i < p.Friends.Count; i++) ini.Set("AMIGOS", $"Amigo{i + 1}", p.Friends[i]);
        // Historial de sanciones (VB6 Protocol.bas:7355 / Admin.bas:479): clave `Cant` + `P1..Pn`.
        ini.Set("PENAS", "Cant", F(p.Penalties.Count));
        for (int i = 0; i < p.Penalties.Count; i++) ini.Set("PENAS", $"P{i + 1}", p.Penalties[i]);

        ini.Set("INIT", "Genero", F(p.Gender));
        ini.Set("INIT", "Raza", F(p.Race));
        ini.Set("INIT", "Hogar", F(p.Homeland));
        ini.Set("INIT", "Ciudad", F(p.HomeCity));   // ciudad-hogar elegida (SeleccionarHogar) — VB6 la persiste
        ini.Set("INIT", "Pena", F(p.JailMinutesLeft));   // condena pendiente (/CARCEL): sin persistirla, deslogueás y salís
        ini.Set("INIT", "Clase", F(p.Class));
        ini.Set("INIT", "Desc", p.Description);
        ini.Set("INIT", "Heading", F(p.Heading));
        // VB6 FileIO.bas:1719: `If .Char.Head = 0 Then Head = OrigChar.Head Else Head = .Char.Head` — o sea que
        // el original guarda la cabeza ACTUAL y sólo cae en la viva cuando la actual no vale. Acá la que no
        // vale es la de CADÁVER, así que el fallback se limita a estar muerto. Prefiriendo SIEMPRE OrigHead
        // —que queda cargada para siempre después de la primera muerte— un cambio de cabeza de `/MOD` se veía
        // en pantalla y no llegaba nunca al .chr.
        ini.Set("INIT", "Head", F(!p.IsAlive && p.OrigHead > 0 ? p.OrigHead : p.Head));
        ini.Set("INIT", "Body", F(p.Body));
        ini.Set("INIT", "Arma", F(p.WeaponAnim));
        ini.Set("INIT", "Escudo", F(p.ShieldAnim));
        ini.Set("INIT", "Casco", F(p.HelmetAnim));
        ini.Set("INIT", "UpTime", F((int)p.UpTimeSeconds));
        ini.Set("INIT", "Logged", "0");
        ini.Set("INIT", "Position", $"{p.Pos.Map}-{p.Pos.X}-{p.Pos.Y}");

        ini.Set("STATS", "GLD", F(p.Gold));
        ini.Set("STATS", "BANCO", p.Bank.ToString(CultureInfo.InvariantCulture));
        ini.Set("STATS", "MaxHP", F(p.MaxHp));
        ini.Set("STATS", "MinHP", F(p.Hp));
        ini.Set("STATS", "MaxSTA", F(p.MaxStamina));
        ini.Set("STATS", "MinSTA", F(p.Stamina));
        ini.Set("STATS", "MaxMAN", F(p.MaxMana));
        ini.Set("STATS", "MinMAN", F(p.Mana));
        ini.Set("STATS", "MaxHIT", F(p.MaxHit));
        ini.Set("STATS", "MinHIT", F(p.MinHit));
        ini.Set("STATS", "MaxAGU", F(p.MaxThirst));
        ini.Set("STATS", "MinAGU", F(p.Thirst));
        ini.Set("STATS", "MaxHAM", F(p.MaxHunger));
        ini.Set("STATS", "MinHAM", F(p.Hunger));
        ini.Set("STATS", "SkillPtsLibres", F(p.FreeSkillPoints));
        ini.Set("STATS", "EXP", p.Exp.ToString(CultureInfo.InvariantCulture));
        ini.Set("STATS", "ELV", F(p.Level));
        ini.Set("STATS", "ELU", p.Elu.ToString(CultureInfo.InvariantCulture));

        // Donador (VB6 FileIO.bas:1666): flag premium + créditos de canje. Persisten también en el .cnt de la cuenta.
        ini.Set("DONADOR", "Donador", F(p.Donador ? 1 : 0));
        ini.Set("DONADOR", "Puntos", p.CreditoDonador.ToString(CultureInfo.InvariantCulture));

        for (int i = 1; i <= 5; i++)
            ini.Set("ATRIBUTOS", "AT" + i, F(p.Attributes[i]));

        int itemCount = 0;
        for (int i = 1; i <= GameLimits.MaxInventorySlots; i++)
        {
            var s = p.Inventory[i];
            if (!s.IsEmpty) itemCount++;
            ini.Set("Inventory", "Obj" + i, $"{s.ObjIndex}-{s.Amount}-{(s.Equipped ? 1 : 0)}");
        }
        ini.Set("Inventory", "CantidadItems", F(itemCount));
        ini.Set("Inventory", "BarcoSlot", F(p.BoatSlot));   // VB6 FileIO.bas:1807 / TCP.bas:2053 — va con Navegando

        int bankCount = 0;
        for (int i = 1; i <= GameLimits.MaxBankSlots; i++)
        {
            var b = p.BankInventory[i];
            if (!b.IsEmpty) bankCount++;
            ini.Set("BancoInventory", "Obj" + i, $"{b.ObjIndex}-{b.Amount}"); // bank format: objindex-amount
        }
        ini.Set("BancoInventory", "CantidadItems", F(bankCount));

        for (int i = 1; i <= GameLimits.MaxUserSpells; i++)
            ini.Set("HECHIZOS", "H" + i, F(p.Spells[i]));

        // [FAMILIAR] -- seccion NUESTRA, el .chr del original no la tiene. Se escribe siempre, aunque el
        // personaje no lleve familiar, para que un archivo viejo y uno nuevo se lean igual: `Tipo=0` es "no
        // tiene". Lo que NO se guarda es si estaba llamado: el familiar entra al mundo cuando el jugador lo
        // llama, igual que las mascotas, que tampoco sobreviven a la sesion.
        var fam = p.Familiar;
        ini.Set("FAMILIAR", "Tipo", F(fam.Tipo));
        ini.Set("FAMILIAR", "Nombre", fam.Nombre);
        ini.Set("FAMILIAR", "Nivel", F(fam.Nivel));
        ini.Set("FAMILIAR", "Exp", F((int)fam.Exp));
        ini.Set("FAMILIAR", "ELU", F((int)fam.Elu));
        ini.Set("FAMILIAR", "MinHP", F((int)fam.Vida));
        ini.Set("FAMILIAR", "MaxHP", F((int)fam.VidaMaxima));
        ini.Set("FAMILIAR", "MinHIT", F((int)fam.GolpeMinimo));
        ini.Set("FAMILIAR", "MaxHIT", F((int)fam.GolpeMaximo));
        ini.Set("FAMILIAR", "Muerto", F(fam.Muerto ? 1 : 0));
        for (int i = 0; i < fam.Habilidades.Length; i++)
            ini.Set("FAMILIAR", "Hab" + (i + 1), F(fam.Habilidades[i]));

        for (int i = 1; i <= GameLimits.NumSkills; i++)
        {
            ini.Set("SKILLS", "SK" + i, F(p.Skills[i]));
            ini.Set("SKILLS", "ELUSK" + i, "0"); // skill exp not modeled yet; kept for format fidelity
            ini.Set("SKILLS", "EXPSK" + i, "0");
        }

        return ini;
    }

    public static void Save(Player p, string path) => ToIni(p).Save(path);

    /// <summary>Reads just the fields the account character list (AddPJ) needs, without a full load.</summary>
    public static CharacterSummary LoadSummary(string path, string name) => SummaryFromIni(IniFile.Load(path), name);

    public static CharacterSummary SummaryFromIni(IniFile ini, string name)
    {
        var pos = ParsePosition(ini.GetString("INIT", "Position"));
        return new CharacterSummary(
            Name: name,
            Head: ini.GetShort("INIT", "Head"),
            Body: ini.GetShort("INIT", "Body"),
            Helmet: ini.GetShort("INIT", "Casco"),
            Weapon: ini.GetShort("INIT", "Arma"),
            Shield: ini.GetShort("INIT", "Escudo"),
            Level: ini.GetByte("STATS", "ELV", 1),
            Class: ini.GetByte("INIT", "Clase"),
            Map: pos.Map,
            Color: 0,
            GameMaster: false);
    }

    /// <summary>Lo que la tabla de posiciones necesita de un personaje. Todos los campos salen del
    /// <c>.chr</c> y son del original: nivel y experiencia de <c>[STATS]</c>, los dos contadores de
    /// <c>[MUERTES]</c> (VB6 TCP.bas:2166-2167) y las veces que murió de <c>[FLAGS] Murio</c>.</summary>
    public readonly record struct FilaDeRanking(string Nombre, int Nivel, double Exp, byte Clase,
                                                string Clan, int Usuarios, int Criaturas, int Muertes);

    /// <summary>Lee sólo esos campos, sin construir el <see cref="Player"/> entero. Vive en ESTE archivo, que
    /// es el que escribe esas claves, para que renombrar una las cambie de los dos lados a la vez.</summary>
    public static FilaDeRanking RankingFromIni(IniFile ini, string name) => new(
        Nombre: name,
        Nivel: ini.GetByte("STATS", "ELV", 1),
        Exp: double.TryParse(ini.GetString("STATS", "EXP"), NumberStyles.Float, CultureInfo.InvariantCulture, out var xp) ? xp : 0,
        Clase: ini.GetByte("INIT", "Clase"),
        Clan: ini.GetString("GUILD", "GuildName"),
        Usuarios: Math.Max(0, ini.GetInt("MUERTES", "UserMuertes")),
        Criaturas: Math.Max(0, ini.GetInt("MUERTES", "NpcsMuertes")),
        Muertes: Math.Max(0, ini.GetInt("FLAGS", "Murio")));

    public static Player Load(string path, short charIndex, string name, string account)
        => FromIni(IniFile.Load(path), charIndex, name, account);

    public static Player FromIni(IniFile ini, short charIndex, string name, string account)
    {
        var p = new Player { CharIndex = charIndex, Name = name, Account = account };

        // Saneo defensivo: un .chr manipulado podría inyectar un status fuera de {0,1,3,4,5,6} (dejaría al char
        // ni-legal-ni-criminal, invisible a la sanción de PvP) o un rango > 10. Se acotan al cargar.
        byte statusRaw = ini.GetByte("FACCIONES", "Status", Player.StatusCiudadano);
        p.FactionStatus = statusRaw is Player.StatusCriminal or Player.StatusCiudadano or Player.StatusRepublicano
            or Player.StatusCaos or Player.StatusArmada or Player.StatusMilicia ? statusRaw : Player.StatusCiudadano;
        p.FactionRank = Math.Min((byte)10, ini.GetByte("FACCIONES", "Rango"));
        p.RenegadosMatados = Math.Max(0, ini.GetInt("FACCIONES", "CrimMatados"));
        p.CiudadanosMatados = Math.Max(0, ini.GetInt("FACCIONES", "CiudadanosMatados"));
        p.RepublicanosMatados = Math.Max(0, ini.GetInt("FACCIONES", "RepublicanosMatados"));
        p.ArmadaMatados = Math.Max(0, ini.GetInt("FACCIONES", "ArmadaMatados"));
        p.MilicianosMatados = Math.Max(0, ini.GetInt("FACCIONES", "MilicianosMatados"));
        p.CaosMatados = Math.Max(0, ini.GetInt("FACCIONES", "CaosMatados"));
        p.Privileges = ini.GetByte("FLAGS", "Privilegios", 1);
        p.Banned = ini.GetInt("FLAGS", "Ban") == 1;
        p.Navigating = ini.GetInt("FLAGS", "Navegando") == 1;   // VB6 TCP.bas:1866
        p.Mounted = ini.GetInt("FLAGS", "Montando") == 1;       // VB6 TCP.bas:1867 (la línea de abajo)
        p.PoisonedUntilTick = ini.GetInt("FLAGS", "Envenenado") == 1 ? long.MaxValue : 0;   // VB6 TCP.bas:1868
        p.Deaths = Math.Max(0, ini.GetInt("FLAGS", "Murio"));   // VB6 TCP.bas:1869 (.flags.MuertesUsuario)
        p.UsersKilled = Math.Max(0, ini.GetInt("MUERTES", "UserMuertes"));   // VB6 TCP.bas:2166
        p.NpcsKilled = Math.Max(0, ini.GetInt("MUERTES", "NpcsMuertes"));    // VB6 TCP.bas:2167
        p.SpouseName = ini.GetString("CASAMIENTO", "Pareja");
        p.GuildName = ini.GetString("GUILD", "GuildName");
        int friendCount = ini.GetInt("AMIGOS", "Cantidad");
        for (int i = 1; i <= friendCount; i++)
        {
            var f = ini.GetString("AMIGOS", $"Amigo{i}");
            if (!string.IsNullOrWhiteSpace(f)) p.Friends.Add(f);
        }

        int penaCount = ini.GetInt("PENAS", "Cant");
        for (int i = 1; i <= penaCount; i++)
        {
            var pena = ini.GetString("PENAS", $"P{i}");
            if (!string.IsNullOrWhiteSpace(pena)) p.Penalties.Add(pena);
        }

        p.Gender = ini.GetByte("INIT", "Genero");
        p.Race = ini.GetByte("INIT", "Raza");
        p.Homeland = ini.GetByte("INIT", "Hogar");
        p.HomeCity = ini.GetByte("INIT", "Ciudad", p.Homeland);   // fallback a la tierra natal si el .chr es viejo
        p.JailMinutesLeft = ini.GetInt("INIT", "Pena");           // condena pendiente (/CARCEL)
        p.Class = ini.GetByte("INIT", "Clase");
        p.Description = ini.GetString("INIT", "Desc");
        p.Heading = ini.GetByte("INIT", "Heading", 3);
        p.Head = ini.GetShort("INIT", "Head");
        p.Body = ini.GetShort("INIT", "Body");
        p.WeaponAnim = ini.GetShort("INIT", "Arma");
        p.ShieldAnim = ini.GetShort("INIT", "Escudo");
        p.HelmetAnim = ini.GetShort("INIT", "Casco");
        p.UpTimeSeconds = ini.GetLong("INIT", "UpTime");
        p.Pos = ParsePosition(ini.GetString("INIT", "Position"));

        p.Gold = ini.GetInt("STATS", "GLD");
        p.Bank = ini.GetLong("STATS", "BANCO");
        p.MaxHp = ini.GetInt("STATS", "MaxHP", 100);
        p.Hp = ini.GetInt("STATS", "MinHP", 100);
        p.MaxStamina = ini.GetInt("STATS", "MaxSTA", 100);
        p.Stamina = ini.GetInt("STATS", "MinSTA", 100);
        p.MaxMana = ini.GetInt("STATS", "MaxMAN");
        p.Mana = ini.GetInt("STATS", "MinMAN");
        p.MaxHit = ini.GetInt("STATS", "MaxHIT");
        p.MinHit = ini.GetInt("STATS", "MinHIT");
        p.MaxThirst = ini.GetByte("STATS", "MaxAGU", 100);
        p.Thirst = ini.GetByte("STATS", "MinAGU", 100);
        p.MaxHunger = ini.GetByte("STATS", "MaxHAM", 100);
        p.Hunger = ini.GetByte("STATS", "MinHAM", 100);
        p.FreeSkillPoints = ini.GetShort("STATS", "SkillPtsLibres");
        p.Exp = double.TryParse(ini.GetString("STATS", "EXP"), NumberStyles.Float, CultureInfo.InvariantCulture, out var exp) ? exp : 0;
        p.Level = ini.GetByte("STATS", "ELV", 1);
        p.Elu = ini.GetLong("STATS", "ELU");

        // Donador (VB6 TCP.bas:1859): flag premium + créditos. El .cnt de la cuenta puede forzar activo=1 al loguear
        // (Persistence.LoadAccount) — acá se lee lo del .chr como base.
        p.Donador = ini.GetInt("DONADOR", "Donador") == 1;
        p.CreditoDonador = ini.GetLong("DONADOR", "Puntos");

        for (int i = 1; i <= 5; i++)
            p.Attributes[i] = ini.GetByte("ATRIBUTOS", "AT" + i);

        for (int i = 1; i <= GameLimits.MaxInventorySlots; i++)
            p.Inventory[i] = ParseInventory(ini.GetString("Inventory", "Obj" + i));
        p.BoatSlot = ini.GetByte("Inventory", "BarcoSlot");   // VB6 TCP.bas:2053
        if (p.BoatSlot > GameLimits.MaxInventorySlots) p.BoatSlot = 0;   // .chr manoseado: un slot fuera de rango indexaría al vacío

        for (int i = 1; i <= GameLimits.MaxBankSlots; i++)
            p.BankInventory[i] = ParseInventory(ini.GetString("BancoInventory", "Obj" + i)); // 2-part ok (equipped defaults false)

        // El familiar. Un .chr de antes de que existiera la seccion devuelve vacio en todo y queda con Tipo=0,
        // que es exactamente "no eligio familiar": no hace falta migrar nada.
        var fam = p.Familiar;
        fam.Tipo = ini.GetByte("FAMILIAR", "Tipo", 0);
        if (fam.Tipo != 0)
        {
            fam.Nombre = ini.GetString("FAMILIAR", "Nombre");
            fam.Nivel = ini.GetByte("FAMILIAR", "Nivel", 1);
            fam.Exp = ini.GetLong("FAMILIAR", "Exp");
            fam.Elu = ini.GetLong("FAMILIAR", "ELU");
            fam.Vida = ini.GetLong("FAMILIAR", "MinHP");
            fam.VidaMaxima = ini.GetLong("FAMILIAR", "MaxHP");
            fam.GolpeMinimo = ini.GetLong("FAMILIAR", "MinHIT");
            fam.GolpeMaximo = ini.GetLong("FAMILIAR", "MaxHIT");
            fam.Muerto = ini.GetInt("FAMILIAR", "Muerto") != 0;
            for (int i = 0; i < fam.Habilidades.Length; i++)
                fam.Habilidades[i] = ini.GetInt("FAMILIAR", "Hab" + (i + 1));
        }

        for (int i = 1; i <= GameLimits.MaxUserSpells; i++)
            p.Spells[i] = ini.GetShort("HECHIZOS", "H" + i);

        for (int i = 1; i <= GameLimits.NumSkills; i++)
            p.Skills[i] = ini.GetByte("SKILLS", "SK" + i);

        return p;
    }

    private static WorldPos ParsePosition(string s)
    {
        var parts = s.Split('-');
        if (parts.Length < 3) return new WorldPos(1, 50, 50);
        short map = short.TryParse(parts[0], out var m) ? m : (short)1;
        byte x = byte.TryParse(parts[1], out var xx) ? xx : (byte)50;
        byte y = byte.TryParse(parts[2], out var yy) ? yy : (byte)50;
        return new WorldPos(map, x, y);
    }

    private static InventorySlot ParseInventory(string s)
    {
        var parts = s.Split('-');
        var slot = new InventorySlot();
        if (parts.Length >= 1) short.TryParse(parts[0], out slot.ObjIndex);
        if (parts.Length >= 2) short.TryParse(parts[1], out slot.Amount);
        if (parts.Length >= 3) slot.Equipped = parts[2] == "1";
        return slot;
    }
}
