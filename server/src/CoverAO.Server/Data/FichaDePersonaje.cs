using System.Collections.Immutable;
using CoverAO.Server.Game;

namespace CoverAO.Server.Data;

/// <summary>Una ranura de inventario o de bóveda tal como se guarda. En la bóveda <see cref="Equipado"/> no significa nada
/// y el <c>.chr</c> no lo escribe, pero se conserva lo que se leyó, como hacía el cargador.</summary>
public readonly record struct RanuraGuardada(short Objeto, short Cantidad, bool Equipado)
{
    public bool Vacia => Objeto == 0;
}

/// <summary>El familiar tal como se guarda. Un personaje sin familiar no tiene ficha de familiar (el <c>Tipo=0</c> del
/// <c>.chr</c>).</summary>
public sealed record FichaDeFamiliar(byte Tipo, string Nombre, byte Nivel, long Exp, long Elu, long Vida, long VidaMaxima,
                                     long GolpeMinimo, long GolpeMaximo, bool Muerto, ImmutableArray<int> Habilidades);

/// <summary>
/// TODO LO QUE SE GUARDA DE UN PERSONAJE, sin el resto del <see cref="Player"/>: lo que hoy es el <c>.chr</c>. Es lo que
/// viaja entre el juego y el almacén —archivos hoy; SQLite o Postgres después (fase5/postgres/DISENO.md)—. Se arma en el
/// hilo del mundo con el personaje vivo (<see cref="Desde"/>) y se vuelve a convertir en <see cref="Player"/> al entrar
/// (<see cref="CrearPlayer"/>).
///
/// <para><b>Inmutable a propósito.</b> Se arma en el loop y se escribe en otro hilo: un arreglo compartido con el
/// <see cref="Player"/> vivo cambiaría mientras se escribe, y el guardado saldría con el oro ya descontado y el objeto
/// todavía sin agregar. Por eso las ranuras son copias en <see cref="ImmutableArray{T}"/>.</para>
///
/// <para>Los arreglos siguen la convención del <see cref="Player"/>: <b>el índice 0 no se usa</b> y la ranura N está en
/// el índice N. Así la conversión no corre ningún índice, que es donde se esconden los errores de a uno.</para>
///
/// <para>Los valores son los <b>que se guardan</b>, no los vivos: el bando y el privilegio civiles (el de staff lo pone
/// el <c>Server.ini</c> en cada ingreso), la cabeza viva de un muerto, el veneno como su byte. Esas reglas viven en
/// <see cref="Desde"/>, en un solo lugar. Y el saneo de lo que se lee —un bando que no existe, un rango de más, la ranura
/// del barco fuera del inventario— vive en <see cref="CrearPlayer"/>, que es por donde pasa todo lo que viene de
/// cualquier almacén.</para>
/// </summary>
public sealed record FichaDePersonaje
{
    public required string Nombre { get; init; }

    // ── [INIT] ──────────────────────────────────────────────────────────────────────────────────────────────
    public byte Genero { get; init; }
    public byte Raza { get; init; }
    public byte Clase { get; init; }
    /// <summary>La tierra natal (<c>Hogar</c>).</summary>
    public byte TierraNatal { get; init; }
    /// <summary>La ciudad-hogar elegida (<c>Ciudad</c>).</summary>
    public byte CiudadHogar { get; init; }
    /// <summary>La condena pendiente de <c>/CARCEL</c> (<c>Pena</c>).</summary>
    public int MinutosDeCarcel { get; init; }
    public string Descripcion { get; init; } = "";
    public byte Rumbo { get; init; } = 3;
    /// <summary>La cabeza que se guarda: la viva si el personaje está muerto (ver <see cref="Desde"/>).</summary>
    public short Cabeza { get; init; }
    public short Cuerpo { get; init; }
    public short AnimArma { get; init; }
    public short AnimEscudo { get; init; }
    public short AnimCasco { get; init; }
    public long SegundosJugados { get; init; }
    public WorldPos Posicion { get; init; } = new(1, 50, 50);

    // ── [STATS] ─────────────────────────────────────────────────────────────────────────────────────────────
    public int Oro { get; init; }
    public long OroEnBanco { get; init; }
    public int Vida { get; init; } = 100;
    public int VidaMaxima { get; init; } = 100;
    public int Energia { get; init; } = 100;
    public int EnergiaMaxima { get; init; } = 100;
    public int Mana { get; init; }
    public int ManaMaximo { get; init; }
    public int GolpeMinimo { get; init; }
    public int GolpeMaximo { get; init; }
    public byte Sed { get; init; } = 100;
    public byte SedMaxima { get; init; } = 100;
    public byte Hambre { get; init; } = 100;
    public byte HambreMaxima { get; init; } = 100;
    public short SkillPtsLibres { get; init; }
    public double Exp { get; init; }
    public byte Nivel { get; init; } = 1;
    public long ExpParaSubir { get; init; }

    // ── [ATRIBUTOS] ─────────────────────────────────────────────────────────────────────────────────────────
    /// <summary>AT1..AT5 en los índices 1..5.</summary>
    public ImmutableArray<byte> Atributos { get; init; } = ImmutableArray.Create(new byte[6]);

    // ── [FLAGS] ─────────────────────────────────────────────────────────────────────────────────────────────
    /// <summary>El privilegio CIVIL. El de staff no se guarda nunca (memoria <c>estado-de-seguridad-no-persistir</c>).</summary>
    public byte Privilegios { get; init; } = 1;
    public bool Baneado { get; init; }
    public bool Muerto { get; init; }
    public bool Navegando { get; init; }
    public bool Montando { get; init; }
    /// <summary>0 = sano. Si no, el byte del veneno (<c>Envenenado</c>, VB6 TCP.bas:1868).</summary>
    public byte Veneno { get; init; }
    /// <summary>Las veces que murió (<c>Murio</c>).</summary>
    public int VecesMuerto { get; init; }

    // ── [FACCIONES] y [MUERTES] ─────────────────────────────────────────────────────────────────────────────
    /// <summary>El bando CIVIL (<c>Status</c>). El de staff no se guarda.</summary>
    public byte Bando { get; init; } = Player.StatusCiudadano;
    public byte Rango { get; init; }
    public int RenegadosMatados { get; init; }
    public int CiudadanosMatados { get; init; }
    public int RepublicanosMatados { get; init; }
    public int ArmadaMatados { get; init; }
    public int MilicianosMatados { get; init; }
    public int CaosMatados { get; init; }
    public int UsuariosMatados { get; init; }
    public int CriaturasMatadas { get; init; }

    // ── [CASAMIENTO], [GUILD], [DONADOR] ────────────────────────────────────────────────────────────────────
    public string Pareja { get; init; } = "";
    public string Clan { get; init; } = "";
    public bool Donador { get; init; }
    public long CreditosDonador { get; init; }

    // ── [Inventory], [BancoInventory], [HECHIZOS], [SKILLS] ─────────────────────────────────────────────────
    public ImmutableArray<RanuraGuardada> Inventario { get; init; } =
        ImmutableArray.Create(new RanuraGuardada[GameLimits.MaxInventorySlots + 1]);
    public byte RanuraDelBarco { get; init; }
    public ImmutableArray<RanuraGuardada> Boveda { get; init; } =
        ImmutableArray.Create(new RanuraGuardada[GameLimits.MaxBankSlots + 1]);
    public ImmutableArray<short> Hechizos { get; init; } = ImmutableArray.Create(new short[GameLimits.MaxUserSpells + 1]);
    public ImmutableArray<byte> Skills { get; init; } = ImmutableArray.Create(new byte[GameLimits.NumSkills + 1]);

    // ── [AMIGOS], [PENAS], [FAMILIAR] ───────────────────────────────────────────────────────────────────────
    public ImmutableArray<string> Amigos { get; init; } = ImmutableArray<string>.Empty;
    /// <summary>El historial de sanciones, cada una con el texto que muestra <c>/PENAS</c>.</summary>
    public ImmutableArray<string> Penas { get; init; } = ImmutableArray<string>.Empty;
    /// <summary>null = no eligió familiar.</summary>
    public FichaDeFamiliar? Familiar { get; init; }

    /// <summary>
    /// La ficha de un personaje VIVO. Se llama en el hilo del mundo: lee unos cien campos del <see cref="Player"/>, y
    /// hacerlo desde otro hilo devuelve un personaje partido al medio.
    /// </summary>
    public static FichaDePersonaje Desde(Player p)
    {
        var inventario = new RanuraGuardada[GameLimits.MaxInventorySlots + 1];
        for (int i = 1; i <= GameLimits.MaxInventorySlots; i++)
        {
            var s = p.Inventory[i];
            inventario[i] = new RanuraGuardada(s.ObjIndex, s.Amount, s.Equipped);
        }
        var boveda = new RanuraGuardada[GameLimits.MaxBankSlots + 1];
        for (int i = 1; i <= GameLimits.MaxBankSlots; i++)
        {
            var b = p.BankInventory[i];
            boveda[i] = new RanuraGuardada(b.ObjIndex, b.Amount, b.Equipped);   // en la bóveda no significa nada, pero viaja igual
        }
        var fam = p.Familiar;
        return new FichaDePersonaje
        {
            Nombre = p.Name,
            Genero = p.Gender,
            Raza = p.Race,
            Clase = p.Class,
            TierraNatal = p.Homeland,
            CiudadHogar = p.HomeCity,
            MinutosDeCarcel = p.JailMinutesLeft,
            Descripcion = p.Description,
            Rumbo = p.Heading,
            // VB6 FileIO.bas:1719: el original guarda la cabeza ACTUAL y cae en la viva sólo cuando la actual no vale. Acá
            // la que no vale es la de CADÁVER, así que el reemplazo se limita a estar muerto.
            Cabeza = !p.IsAlive && p.OrigHead > 0 ? p.OrigHead : p.Head,
            Cuerpo = p.Body,
            AnimArma = p.WeaponAnim,
            AnimEscudo = p.ShieldAnim,
            AnimCasco = p.HelmetAnim,
            SegundosJugados = p.UpTimeSeconds,
            Posicion = p.Pos,
            Oro = p.Gold,
            OroEnBanco = p.Bank,
            Vida = p.Hp,
            VidaMaxima = p.MaxHp,
            Energia = p.Stamina,
            EnergiaMaxima = p.MaxStamina,
            Mana = p.Mana,
            ManaMaximo = p.MaxMana,
            GolpeMinimo = p.MinHit,
            GolpeMaximo = p.MaxHit,
            Sed = p.Thirst,
            SedMaxima = p.MaxThirst,
            Hambre = p.Hunger,
            HambreMaxima = p.MaxHunger,
            SkillPtsLibres = p.FreeSkillPoints,
            Exp = p.Exp,
            Nivel = p.Level,
            ExpParaSubir = p.Elu,
            Atributos = ImmutableArray.Create(p.Attributes),
            Privilegios = p.PrivilegiosPersistidos,
            Baneado = p.Banned,
            Muerto = !p.IsAlive,
            Navegando = p.Navigating,
            Montando = p.Mounted,
            Veneno = p.PoisonedUntilTick == long.MaxValue ? p.ValorDelVeneno : (byte)0,
            VecesMuerto = p.Deaths,
            Bando = p.FactionStatusPersistido,
            Rango = p.FactionRank,
            RenegadosMatados = p.RenegadosMatados,
            CiudadanosMatados = p.CiudadanosMatados,
            RepublicanosMatados = p.RepublicanosMatados,
            ArmadaMatados = p.ArmadaMatados,
            MilicianosMatados = p.MilicianosMatados,
            CaosMatados = p.CaosMatados,
            UsuariosMatados = p.UsersKilled,
            CriaturasMatadas = p.NpcsKilled,
            Pareja = p.SpouseName,
            Clan = p.GuildName,
            Donador = p.Donador,
            CreditosDonador = p.CreditoDonador,
            Inventario = ImmutableArray.Create(inventario),
            RanuraDelBarco = p.BoatSlot,
            Boveda = ImmutableArray.Create(boveda),
            Hechizos = ImmutableArray.Create(p.Spells),
            Skills = ImmutableArray.Create(p.Skills),
            Amigos = p.Friends.ToImmutableArray(),
            Penas = p.Penalties.ToImmutableArray(),
            Familiar = fam.Tipo == 0 ? null : new FichaDeFamiliar(fam.Tipo, fam.Nombre, fam.Nivel, fam.Exp, fam.Elu, fam.Vida,
                fam.VidaMaxima, fam.GolpeMinimo, fam.GolpeMaximo, fam.Muerto, ImmutableArray.Create(fam.Habilidades)),
        };
    }

    /// <summary>
    /// El <see cref="Player"/> de esta ficha, listo para entrar al mundo. Acá va el SANEO de lo que se lee, venga del
    /// almacén que venga: un archivo manoseado o una fila tocada a mano no pueden dejar un personaje ni-legal-ni-criminal
    /// ni una ranura que indexe al vacío.
    /// </summary>
    public Player CrearPlayer(short charIndex, string cuenta)
    {
        var p = new Player { CharIndex = charIndex, Name = Nombre, Account = cuenta };

        // Un bando fuera de {0,1,3,4,5,6} dejaría al personaje invisible a la sanción del PvP; un rango de más, fuera de
        // las tablas de la facción.
        p.FactionStatus = Bando is Player.StatusCriminal or Player.StatusCiudadano or Player.StatusRepublicano
            or Player.StatusCaos or Player.StatusArmada or Player.StatusMilicia ? Bando : Player.StatusCiudadano;
        p.FactionRank = Math.Min((byte)10, Rango);
        p.RenegadosMatados = Math.Max(0, RenegadosMatados);
        p.CiudadanosMatados = Math.Max(0, CiudadanosMatados);
        p.RepublicanosMatados = Math.Max(0, RepublicanosMatados);
        p.ArmadaMatados = Math.Max(0, ArmadaMatados);
        p.MilicianosMatados = Math.Max(0, MilicianosMatados);
        p.CaosMatados = Math.Max(0, CaosMatados);
        p.Privileges = Privilegios;
        p.Banned = Baneado;
        p.Navigating = Navegando;
        p.Mounted = Montando;
        // VB6 TCP.bas:1868: el byte tal cual, y envenena cualquier valor que no sea 0 (mMainLoop.bas:168).
        if (Veneno > 0) p.Envenenar(Veneno);
        p.Deaths = Math.Max(0, VecesMuerto);
        p.UsersKilled = Math.Max(0, UsuariosMatados);
        p.NpcsKilled = Math.Max(0, CriaturasMatadas);
        p.SpouseName = Pareja;
        p.GuildName = Clan;
        foreach (var amigo in Amigos)
            if (!string.IsNullOrWhiteSpace(amigo)) p.Friends.Add(amigo);
        foreach (var pena in Penas)
            if (!string.IsNullOrWhiteSpace(pena)) p.Penalties.Add(pena);

        p.Gender = Genero;
        p.Race = Raza;
        p.Homeland = TierraNatal;
        p.HomeCity = CiudadHogar;
        p.JailMinutesLeft = MinutosDeCarcel;
        p.Class = Clase;
        p.Description = Descripcion;
        p.Heading = Rumbo;
        p.Head = Cabeza;
        p.Body = Cuerpo;
        p.WeaponAnim = AnimArma;
        p.ShieldAnim = AnimEscudo;
        p.HelmetAnim = AnimCasco;
        p.UpTimeSeconds = SegundosJugados;
        p.Pos = Posicion;

        p.Gold = Oro;
        p.Bank = OroEnBanco;
        p.MaxHp = VidaMaxima;
        p.Hp = Vida;
        p.MaxStamina = EnergiaMaxima;
        p.Stamina = Energia;
        p.MaxMana = ManaMaximo;
        p.Mana = Mana;
        p.MaxHit = GolpeMaximo;
        p.MinHit = GolpeMinimo;
        p.MaxThirst = SedMaxima;
        p.Thirst = Sed;
        p.MaxHunger = HambreMaxima;
        p.Hunger = Hambre;
        p.FreeSkillPoints = SkillPtsLibres;
        p.Exp = Exp;
        p.Level = Nivel;
        p.Elu = ExpParaSubir;
        p.Donador = Donador;
        p.CreditoDonador = CreditosDonador;

        for (int i = 1; i <= 5 && i < Atributos.Length; i++) p.Attributes[i] = Atributos[i];
        for (int i = 1; i <= GameLimits.MaxInventorySlots && i < Inventario.Length; i++)
        {
            var s = Inventario[i];
            p.Inventory[i] = new InventorySlot { ObjIndex = s.Objeto, Amount = s.Cantidad, Equipped = s.Equipado };
        }
        // Una ranura del barco fuera del inventario indexaría al vacío.
        p.BoatSlot = RanuraDelBarco > GameLimits.MaxInventorySlots ? (byte)0 : RanuraDelBarco;
        for (int i = 1; i <= GameLimits.MaxBankSlots && i < Boveda.Length; i++)
        {
            var b = Boveda[i];
            p.BankInventory[i] = new InventorySlot { ObjIndex = b.Objeto, Amount = b.Cantidad, Equipped = b.Equipado };
        }

        if (Familiar is { } f)
        {
            var fam = p.Familiar;
            fam.Tipo = f.Tipo;
            fam.Nombre = f.Nombre;
            fam.Nivel = f.Nivel;
            fam.Exp = f.Exp;
            fam.Elu = f.Elu;
            fam.Vida = f.Vida;
            fam.VidaMaxima = f.VidaMaxima;
            fam.GolpeMinimo = f.GolpeMinimo;
            fam.GolpeMaximo = f.GolpeMaximo;
            fam.Muerto = f.Muerto;
            for (int i = 0; i < fam.Habilidades.Length && i < f.Habilidades.Length; i++) fam.Habilidades[i] = f.Habilidades[i];
        }

        for (int i = 1; i <= GameLimits.MaxUserSpells && i < Hechizos.Length; i++) p.Spells[i] = Hechizos[i];
        for (int i = 1; i <= GameLimits.NumSkills && i < Skills.Length; i++) p.Skills[i] = Skills[i];
        return p;
    }
}
