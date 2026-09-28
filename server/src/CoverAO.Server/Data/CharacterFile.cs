using System.Collections.Immutable;
using System.Globalization;
using CoverAO.Server.Game;

namespace CoverAO.Server.Data;

/// <summary>
/// Reads/writes the character .chr file (INI) faithfully to the original SaveUser/LoadUser format
/// (FileIO.bas:1588 / TCP.bas:1832). Compound values use '-' delimiters: Position="map-x-y",
/// inventory Obj="objindex-amount-equipped", spells "H{i}". Round-trips the sections the server
/// models; other sections (FLAGS, FACCIONES, CORREO, ...) get sane defaults so the file is valid.
///
/// <para>Trabaja con <see cref="FichaDePersonaje"/>: el <c>.chr</c> es una de las formas de guardar una ficha, la del
/// almacén de archivos y la de importar y exportar. Qué valor se guarda (el bando civil, la cabeza viva, el veneno) lo
/// decide <see cref="FichaDePersonaje.Desde"/>, y el saneo de lo leído <see cref="FichaDePersonaje.CrearPlayer"/>; acá
/// queda sólo el formato.</para>
/// </summary>
public static class CharacterFile
{
    private static string F(int v) => v.ToString(CultureInfo.InvariantCulture);

    public static IniFile ToIni(Player p) => ToIni(FichaDePersonaje.Desde(p));

    public static IniFile ToIni(FichaDePersonaje f)
    {
        var ini = new IniFile();

        // El status VIVO puede ser el de GM (7/8/9) que le puso el Server.ini; al .chr va el civil de abajo.
        ini.Set("FACCIONES", "Status", F(f.Bando));
        ini.Set("FACCIONES", "Rango", F(f.Rango));                 // rango en el ejército (VB6 Faccion.Rango)
        // Un contador por bando de la víctima (VB6 Faccion.*Matados): cada ejército suma un subconjunto distinto.
        ini.Set("FACCIONES", "CrimMatados", F(f.RenegadosMatados));
        ini.Set("FACCIONES", "CiudadanosMatados", F(f.CiudadanosMatados));
        ini.Set("FACCIONES", "RepublicanosMatados", F(f.RepublicanosMatados));
        ini.Set("FACCIONES", "ArmadaMatados", F(f.ArmadaMatados));
        ini.Set("FACCIONES", "MilicianosMatados", F(f.MilicianosMatados));
        ini.Set("FACCIONES", "CaosMatados", F(f.CaosMatados));
        ini.Set("FLAGS", "Privilegios", F(f.Privilegios));   // el civil, no el que puso el Server.ini
        ini.Set("FLAGS", "Ban", F(f.Baneado ? 1 : 0));       // VB6 flags.Ban — baneo por personaje (/BAN)
        ini.Set("FLAGS", "Muerto", F(f.Muerto ? 1 : 0));     // VB6 FileIO.bas:1626 — la muerte persiste entre sesiones
        // VB6 FileIO.bas:1632 lo GUARDA y TCP.bas:1866 lo RELEE: quien desloguea navegando vuelve navegando.
        // Sin esto reaparecía parado en medio del mar y a pie — con el gate de agua (`IsWater != Navigating`)
        // rechazándole los cuatro vecinos, hasta que se le ocurriera volver a usar el barco del inventario.
        ini.Set("FLAGS", "Navegando", F(f.Navegando ? 1 : 0));
        // `Montando` está en el .chr una línea DEBAJO de `Navegando` en las dos puntas del VB6
        // (FileIO.bas:1633 lo escribe, TCP.bas:1867 lo relee) y se había quedado afuera con el otro.
        //
        // Faltando, el estado quedaba PARTIDO al reloguear: el cuerpo es el Ropaje de la montura y el ítem
        // sigue marcado como equipado —las dos cosas SÍ persisten— pero el flag volvía en false. O sea que
        // aparecías a caballo, a la vista de todos, y el server te trataba como si fueras a pie: podías
        // meterte a caballo bajo techo (`DesmontarSiEntraBajoTecho` no dispara sin el flag), el arma no se
        // ocultaba, y para bajarte había que tocar la montura dos veces.
        ini.Set("FLAGS", "Montando", F(f.Montando ? 1 : 0));
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
        ini.Set("FLAGS", "Envenenado", F(f.Veneno));   // el Byte, no un 1
        // VB6 flags.MuertesUsuario (Modulo_UsUaRiOs.bas:1260, +1 en UserDie). La ficha del cliente lo muestra
        // como "Veces que moriste"; el .chr lo llama `Murio` (FileIO.bas:1638).
        ini.Set("FLAGS", "Murio", F(f.VecesMuerto));
        // [MUERTES] (VB6 FileIO.bas:1774): las dos cuentas que el cliente muestra en la ficha. Vivian SOLO en
        // memoria — se emitian en MiniStats y se perdian al salir, asi que el jugador veia su historial en cero
        // cada vez que reconectaba.
        ini.Set("MUERTES", "UserMuertes", F(f.UsuariosMatados));
        ini.Set("MUERTES", "NpcsMuertes", F(f.CriaturasMatadas));
        // [CASAMIENTO] (VB6 FileIO.bas:1702). Mismo problema: uno se casaba y al reconectar estaba soltero.
        // `Casado` es redundante con tener pareja, pero se escribe igual porque es lo que el original guarda.
        ini.Set("CASAMIENTO", "Pareja", f.Pareja);
        ini.Set("CASAMIENTO", "Casado", F(string.IsNullOrEmpty(f.Pareja) ? 0 : 1));
        ini.Set("GUILD", "GuildName", f.Clan);
        ini.Set("AMIGOS", "Cantidad", F(f.Amigos.Length));
        for (int i = 0; i < f.Amigos.Length; i++) ini.Set("AMIGOS", $"Amigo{i + 1}", f.Amigos[i]);
        // Historial de sanciones (VB6 Protocol.bas:7355 / Admin.bas:479): clave `Cant` + `P1..Pn`.
        ini.Set("PENAS", "Cant", F(f.Penas.Length));
        for (int i = 0; i < f.Penas.Length; i++) ini.Set("PENAS", $"P{i + 1}", f.Penas[i]);

        ini.Set("INIT", "Genero", F(f.Genero));
        ini.Set("INIT", "Raza", F(f.Raza));
        ini.Set("INIT", "Hogar", F(f.TierraNatal));
        ini.Set("INIT", "Ciudad", F(f.CiudadHogar));   // ciudad-hogar elegida (SeleccionarHogar) — VB6 la persiste
        ini.Set("INIT", "Pena", F(f.MinutosDeCarcel));   // condena pendiente (/CARCEL): sin persistirla, deslogueás y salís
        ini.Set("INIT", "Clase", F(f.Clase));
        ini.Set("INIT", "Desc", f.Descripcion);
        ini.Set("INIT", "Heading", F(f.Rumbo));
        // La cabeza que se guarda (la viva si está muerto) la decide FichaDePersonaje.Desde: VB6 FileIO.bas:1719.
        ini.Set("INIT", "Head", F(f.Cabeza));
        ini.Set("INIT", "Body", F(f.Cuerpo));
        ini.Set("INIT", "Arma", F(f.AnimArma));
        ini.Set("INIT", "Escudo", F(f.AnimEscudo));
        ini.Set("INIT", "Casco", F(f.AnimCasco));
        ini.Set("INIT", "UpTime", F((int)f.SegundosJugados));
        ini.Set("INIT", "Logged", "0");
        ini.Set("INIT", "Position", $"{f.Posicion.Map}-{f.Posicion.X}-{f.Posicion.Y}");

        ini.Set("STATS", "GLD", F(f.Oro));
        ini.Set("STATS", "BANCO", f.OroEnBanco.ToString(CultureInfo.InvariantCulture));
        ini.Set("STATS", "MaxHP", F(f.VidaMaxima));
        ini.Set("STATS", "MinHP", F(f.Vida));
        ini.Set("STATS", "MaxSTA", F(f.EnergiaMaxima));
        ini.Set("STATS", "MinSTA", F(f.Energia));
        ini.Set("STATS", "MaxMAN", F(f.ManaMaximo));
        ini.Set("STATS", "MinMAN", F(f.Mana));
        ini.Set("STATS", "MaxHIT", F(f.GolpeMaximo));
        ini.Set("STATS", "MinHIT", F(f.GolpeMinimo));
        ini.Set("STATS", "MaxAGU", F(f.SedMaxima));
        ini.Set("STATS", "MinAGU", F(f.Sed));
        ini.Set("STATS", "MaxHAM", F(f.HambreMaxima));
        ini.Set("STATS", "MinHAM", F(f.Hambre));
        ini.Set("STATS", "SkillPtsLibres", F(f.SkillPtsLibres));
        ini.Set("STATS", "EXP", f.Exp.ToString(CultureInfo.InvariantCulture));
        ini.Set("STATS", "ELV", F(f.Nivel));
        ini.Set("STATS", "ELU", f.ExpParaSubir.ToString(CultureInfo.InvariantCulture));

        // Donador (VB6 FileIO.bas:1666): flag premium + créditos de canje. Persisten también en el .cnt de la cuenta.
        ini.Set("DONADOR", "Donador", F(f.Donador ? 1 : 0));
        ini.Set("DONADOR", "Puntos", f.CreditosDonador.ToString(CultureInfo.InvariantCulture));

        for (int i = 1; i <= 5; i++)
            ini.Set("ATRIBUTOS", "AT" + i, F(i < f.Atributos.Length ? f.Atributos[i] : 0));

        int itemCount = 0;
        for (int i = 1; i <= GameLimits.MaxInventorySlots; i++)
        {
            var s = i < f.Inventario.Length ? f.Inventario[i] : default;
            if (!s.Vacia) itemCount++;
            ini.Set("Inventory", "Obj" + i, $"{s.Objeto}-{s.Cantidad}-{(s.Equipado ? 1 : 0)}");
        }
        ini.Set("Inventory", "CantidadItems", F(itemCount));
        ini.Set("Inventory", "BarcoSlot", F(f.RanuraDelBarco));   // VB6 FileIO.bas:1807 / TCP.bas:2053 — va con Navegando

        int bankCount = 0;
        for (int i = 1; i <= GameLimits.MaxBankSlots; i++)
        {
            var b = i < f.Boveda.Length ? f.Boveda[i] : default;
            if (!b.Vacia) bankCount++;
            ini.Set("BancoInventory", "Obj" + i, $"{b.Objeto}-{b.Cantidad}"); // bank format: objindex-amount
        }
        ini.Set("BancoInventory", "CantidadItems", F(bankCount));

        for (int i = 1; i <= GameLimits.MaxUserSpells; i++)
            ini.Set("HECHIZOS", "H" + i, F(i < f.Hechizos.Length ? f.Hechizos[i] : 0));

        // [FAMILIAR] -- seccion NUESTRA, el .chr del original no la tiene. Se escribe siempre, aunque el
        // personaje no lleve familiar, para que un archivo viejo y uno nuevo se lean igual: `Tipo=0` es "no
        // tiene". Lo que NO se guarda es si estaba llamado: el familiar entra al mundo cuando el jugador lo
        // llama, igual que las mascotas, que tampoco sobreviven a la sesion.
        var fam = f.Familiar;
        ini.Set("FAMILIAR", "Tipo", F(fam?.Tipo ?? 0));
        ini.Set("FAMILIAR", "Nombre", fam?.Nombre ?? "");
        ini.Set("FAMILIAR", "Nivel", F(fam?.Nivel ?? 0));
        ini.Set("FAMILIAR", "Exp", F((int)(fam?.Exp ?? 0)));
        ini.Set("FAMILIAR", "ELU", F((int)(fam?.Elu ?? 0)));
        ini.Set("FAMILIAR", "MinHP", F((int)(fam?.Vida ?? 0)));
        ini.Set("FAMILIAR", "MaxHP", F((int)(fam?.VidaMaxima ?? 0)));
        ini.Set("FAMILIAR", "MinHIT", F((int)(fam?.GolpeMinimo ?? 0)));
        ini.Set("FAMILIAR", "MaxHIT", F((int)(fam?.GolpeMaximo ?? 0)));
        ini.Set("FAMILIAR", "Muerto", F(fam is { Muerto: true } ? 1 : 0));
        for (int i = 0; i < Familiar.CantidadDeHabilidades; i++)
            ini.Set("FAMILIAR", "Hab" + (i + 1), F(fam is not null && i < fam.Habilidades.Length ? fam.Habilidades[i] : 0));

        for (int i = 1; i <= GameLimits.NumSkills; i++)
        {
            ini.Set("SKILLS", "SK" + i, F(i < f.Skills.Length ? f.Skills[i] : 0));
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
        => FichaDesdeIni(ini, name).CrearPlayer(charIndex, account);

    /// <summary>
    /// La ficha de un <c>.chr</c>, con los valores tal cual y los DEFAULTS del formato para las claves que falten —un
    /// archivo viejo, de antes de que existiera una sección, se lee igual—. El saneo (bandos que no existen, rangos de
    /// más) no va acá sino en <see cref="FichaDePersonaje.CrearPlayer"/>, por donde pasa lo de cualquier almacén.
    /// </summary>
    public static FichaDePersonaje FichaDesdeIni(IniFile ini, string nombre)
    {
        int friendCount = ini.GetInt("AMIGOS", "Cantidad");
        var amigos = ImmutableArray.CreateBuilder<string>();
        for (int i = 1; i <= friendCount; i++)
        {
            var amigo = ini.GetString("AMIGOS", $"Amigo{i}");
            if (!string.IsNullOrWhiteSpace(amigo)) amigos.Add(amigo);
        }
        int penaCount = ini.GetInt("PENAS", "Cant");
        var penas = ImmutableArray.CreateBuilder<string>();
        for (int i = 1; i <= penaCount; i++)
        {
            var pena = ini.GetString("PENAS", $"P{i}");
            if (!string.IsNullOrWhiteSpace(pena)) penas.Add(pena);
        }

        var atributos = new byte[6];
        for (int i = 1; i <= 5; i++) atributos[i] = ini.GetByte("ATRIBUTOS", "AT" + i);
        var inventario = new RanuraGuardada[GameLimits.MaxInventorySlots + 1];
        for (int i = 1; i <= GameLimits.MaxInventorySlots; i++) inventario[i] = ParseInventory(ini.GetString("Inventory", "Obj" + i));
        var boveda = new RanuraGuardada[GameLimits.MaxBankSlots + 1];
        for (int i = 1; i <= GameLimits.MaxBankSlots; i++) boveda[i] = ParseInventory(ini.GetString("BancoInventory", "Obj" + i));
        var hechizos = new short[GameLimits.MaxUserSpells + 1];
        for (int i = 1; i <= GameLimits.MaxUserSpells; i++) hechizos[i] = ini.GetShort("HECHIZOS", "H" + i);
        var skills = new byte[GameLimits.NumSkills + 1];
        for (int i = 1; i <= GameLimits.NumSkills; i++) skills[i] = ini.GetByte("SKILLS", "SK" + i);

        // El familiar. Un .chr de antes de que existiera la seccion devuelve vacio en todo y queda con Tipo=0,
        // que es exactamente "no eligio familiar": no hace falta migrar nada.
        FichaDeFamiliar? familiar = null;
        byte tipo = ini.GetByte("FAMILIAR", "Tipo", 0);
        if (tipo != 0)
        {
            var habilidades = new int[Familiar.CantidadDeHabilidades];
            for (int i = 0; i < habilidades.Length; i++) habilidades[i] = ini.GetInt("FAMILIAR", "Hab" + (i + 1));
            familiar = new FichaDeFamiliar(tipo, ini.GetString("FAMILIAR", "Nombre"), ini.GetByte("FAMILIAR", "Nivel", 1),
                ini.GetLong("FAMILIAR", "Exp"), ini.GetLong("FAMILIAR", "ELU"), ini.GetLong("FAMILIAR", "MinHP"),
                ini.GetLong("FAMILIAR", "MaxHP"), ini.GetLong("FAMILIAR", "MinHIT"), ini.GetLong("FAMILIAR", "MaxHIT"),
                ini.GetInt("FAMILIAR", "Muerto") != 0, ImmutableArray.Create(habilidades));
        }

        // VB6 TCP.bas:1868: el Byte tal cual (`CByte`); lo que no es positivo es "sano".
        int veneno = ini.GetInt("FLAGS", "Envenenado");
        byte tierraNatal = ini.GetByte("INIT", "Hogar");
        double exp = double.TryParse(ini.GetString("STATS", "EXP"), NumberStyles.Float, CultureInfo.InvariantCulture, out var e) ? e : 0;

        return new FichaDePersonaje
        {
            Nombre = nombre,
            Bando = ini.GetByte("FACCIONES", "Status", Player.StatusCiudadano),
            Rango = ini.GetByte("FACCIONES", "Rango"),
            RenegadosMatados = ini.GetInt("FACCIONES", "CrimMatados"),
            CiudadanosMatados = ini.GetInt("FACCIONES", "CiudadanosMatados"),
            RepublicanosMatados = ini.GetInt("FACCIONES", "RepublicanosMatados"),
            ArmadaMatados = ini.GetInt("FACCIONES", "ArmadaMatados"),
            MilicianosMatados = ini.GetInt("FACCIONES", "MilicianosMatados"),
            CaosMatados = ini.GetInt("FACCIONES", "CaosMatados"),
            Privilegios = ini.GetByte("FLAGS", "Privilegios", 1),
            Baneado = ini.GetInt("FLAGS", "Ban") == 1,
            Muerto = ini.GetInt("FLAGS", "Muerto") == 1,
            Navegando = ini.GetInt("FLAGS", "Navegando") == 1,   // VB6 TCP.bas:1866
            Montando = ini.GetInt("FLAGS", "Montando") == 1,     // VB6 TCP.bas:1867 (la línea de abajo)
            Veneno = veneno > 0 ? (byte)Math.Min(veneno, byte.MaxValue) : (byte)0,
            VecesMuerto = ini.GetInt("FLAGS", "Murio"),          // VB6 TCP.bas:1869 (.flags.MuertesUsuario)
            UsuariosMatados = ini.GetInt("MUERTES", "UserMuertes"),   // VB6 TCP.bas:2166
            CriaturasMatadas = ini.GetInt("MUERTES", "NpcsMuertes"),  // VB6 TCP.bas:2167
            Pareja = ini.GetString("CASAMIENTO", "Pareja"),
            Clan = ini.GetString("GUILD", "GuildName"),
            Amigos = amigos.ToImmutable(),
            Penas = penas.ToImmutable(),
            Genero = ini.GetByte("INIT", "Genero"),
            Raza = ini.GetByte("INIT", "Raza"),
            TierraNatal = tierraNatal,
            CiudadHogar = ini.GetByte("INIT", "Ciudad", tierraNatal),   // un .chr viejo no la trae: la tierra natal
            MinutosDeCarcel = ini.GetInt("INIT", "Pena"),
            Clase = ini.GetByte("INIT", "Clase"),
            Descripcion = ini.GetString("INIT", "Desc"),
            Rumbo = ini.GetByte("INIT", "Heading", 3),
            Cabeza = ini.GetShort("INIT", "Head"),
            Cuerpo = ini.GetShort("INIT", "Body"),
            AnimArma = ini.GetShort("INIT", "Arma"),
            AnimEscudo = ini.GetShort("INIT", "Escudo"),
            AnimCasco = ini.GetShort("INIT", "Casco"),
            SegundosJugados = ini.GetLong("INIT", "UpTime"),
            Posicion = ParsePosition(ini.GetString("INIT", "Position")),
            Oro = ini.GetInt("STATS", "GLD"),
            OroEnBanco = ini.GetLong("STATS", "BANCO"),
            VidaMaxima = ini.GetInt("STATS", "MaxHP", 100),
            Vida = ini.GetInt("STATS", "MinHP", 100),
            EnergiaMaxima = ini.GetInt("STATS", "MaxSTA", 100),
            Energia = ini.GetInt("STATS", "MinSTA", 100),
            ManaMaximo = ini.GetInt("STATS", "MaxMAN"),
            Mana = ini.GetInt("STATS", "MinMAN"),
            GolpeMaximo = ini.GetInt("STATS", "MaxHIT"),
            GolpeMinimo = ini.GetInt("STATS", "MinHIT"),
            SedMaxima = ini.GetByte("STATS", "MaxAGU", 100),
            Sed = ini.GetByte("STATS", "MinAGU", 100),
            HambreMaxima = ini.GetByte("STATS", "MaxHAM", 100),
            Hambre = ini.GetByte("STATS", "MinHAM", 100),
            SkillPtsLibres = ini.GetShort("STATS", "SkillPtsLibres"),
            Exp = exp,
            Nivel = ini.GetByte("STATS", "ELV", 1),
            ExpParaSubir = ini.GetLong("STATS", "ELU"),
            // Donador (VB6 TCP.bas:1859): flag premium + créditos. El .cnt de la cuenta puede forzar activo=1 al loguear
            // (PacketDispatcher.EntrarAlMundo): acá se lee lo del .chr como base.
            Donador = ini.GetInt("DONADOR", "Donador") == 1,
            CreditosDonador = ini.GetLong("DONADOR", "Puntos"),
            Atributos = ImmutableArray.Create(atributos),
            Inventario = ImmutableArray.Create(inventario),
            RanuraDelBarco = ini.GetByte("Inventory", "BarcoSlot"),   // VB6 TCP.bas:2053
            Boveda = ImmutableArray.Create(boveda),
            Hechizos = ImmutableArray.Create(hechizos),
            Skills = ImmutableArray.Create(skills),
            Familiar = familiar,
        };
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

    /// <summary>`objindex-amount-equipped` del inventario, o `objindex-amount` de la bóveda (el equipado queda en false).</summary>
    private static RanuraGuardada ParseInventory(string s)
    {
        var parts = s.Split('-');
        short obj = 0, amount = 0;
        bool equipped = false;
        if (parts.Length >= 1) short.TryParse(parts[0], out obj);
        if (parts.Length >= 2) short.TryParse(parts[1], out amount);
        if (parts.Length >= 3) equipped = parts[2] == "1";
        return new RanuraGuardada(obj, amount, equipped);
    }
}
