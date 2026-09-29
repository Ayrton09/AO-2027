using CoverAO.Server.Data;
using CoverAO.Server.Game;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// LA FICHA NO CAMBIA NADA DE LO QUE SE GUARDA. Entre el <see cref="Player"/> y el <c>.chr</c> ahora está la
/// <see cref="FichaDePersonaje"/> (primer paso de la base: fase5/postgres/DISENO.md), y el <c>.chr</c> que sale tiene que ser
/// idéntico, byte a byte, al del código de antes (<see cref="CharacterFileViejo"/>); y el personaje que se lee, el mismo.
/// </summary>
public class FichaDePersonajeTests
{
    private static Player Completo()
    {
        var p = new Player
        {
            CharIndex = 1, Account = "cuenta", Name = "Completo", Gender = 2, Race = 3, Class = 7, Homeland = 2, HomeCity = 5, JailMinutesLeft = 12,
            Description = "una descripción con acentos", Heading = 1, Head = 22, OrigHead = 7, Body = 56,
            WeaponAnim = 3, ShieldAnim = 4, HelmetAnim = 5, UpTimeSeconds = 123_456, Pos = new WorldPos(34, 45, 67),
            Gold = 12_345, Bank = 9_876_543_210L, MaxHp = 300, Hp = 250, MaxStamina = 400, Stamina = 350,
            MaxMana = 500, Mana = 450, MaxHit = 30, MinHit = 20, MaxThirst = 100, Thirst = 50, MaxHunger = 100, Hunger = 60,
            FreeSkillPoints = 7, Exp = 12_345.5, Level = 33, Elu = 99_999,
            Banned = true, Navigating = true, Mounted = true, Deaths = 4, UsersKilled = 5, NpcsKilled = 600,
            FactionStatus = Player.StatusArmada, FactionRank = 3, RenegadosMatados = 11, CiudadanosMatados = 12,
            RepublicanosMatados = 13, ArmadaMatados = 14, MilicianosMatados = 15, CaosMatados = 16,
            SpouseName = "Pareja", GuildName = "ElClan", Donador = true, CreditoDonador = 77, BoatSlot = 25,
        };
        p.Envenenar(7);
        for (int i = 1; i <= 5; i++) p.Attributes[i] = (byte)(19 - i);
        p.Friends.AddRange(new[] { "Uno", "Dos" });
        p.Penalties.AddRange(new[] { "GM: BAN por algo 01/01/2026", "otra pena" });
        p.Inventory[1] = new InventorySlot { ObjIndex = 38, Amount = 5 };
        p.Inventory[3] = new InventorySlot { ObjIndex = 2, Amount = 1, Equipped = true };
        p.Inventory[25] = new InventorySlot { ObjIndex = 474, Amount = 1 };
        p.BankInventory[1] = new InventorySlot { ObjIndex = 100, Amount = 10 };
        p.BankInventory[40] = new InventorySlot { ObjIndex = 200, Amount = 20 };
        p.Spells[1] = 2;
        p.Spells[35] = 77;
        p.Skills[1] = 10;
        p.Skills[27] = 100;
        var fam = p.Familiar;
        fam.Tipo = 3; fam.Nombre = "Rex"; fam.Nivel = 9; fam.Exp = 1234; fam.Elu = 5000; fam.Vida = 40; fam.VidaMaxima = 90;
        fam.GolpeMinimo = 3; fam.GolpeMaximo = 8; fam.Muerto = true;
        fam.Habilidades[0] = 1; fam.Habilidades[1] = 2; fam.Habilidades[3] = 4;
        return p;
    }

    /// <summary>Muerto: la cabeza que se guarda es la viva, no la de cadáver.</summary>
    private static Player Muerto() => new() { CharIndex = 2, Account = "cuenta", Name = "Muerto", Hp = 0, Head = 500, OrigHead = 11, Body = 8 };

    private static IEnumerable<Player> Jugadores() => new[] { Completo(), Muerto(), new Player { CharIndex = 3, Account = "cuenta", Name = "Vacio" } };

    /// <summary>Salvo Paralizado e Incinerado, que el código de antes no guardaba y el original sí (FileIO.bas:1635-1636): con los
    /// dos en 0 son lo único nuevo, y se sacan antes de comparar.</summary>
    [Fact]
    public void El_chr_sale_identico_al_de_antes()
    {
        foreach (var p in Jugadores())
            Assert.Equal(CharacterFileViejo.ToIni(p).ToText(),
                System.Text.RegularExpressions.Regex.Replace(CharacterFile.ToIni(p).ToText(), @"^(Paralizado|Incinerado)=0\r?\n", "",
                    System.Text.RegularExpressions.RegexOptions.Multiline));
    }

    /// <summary>SE GUARDA LA PARÁLISIS, NO LA INMOVILIZACIÓN (el original guarda sólo <c>flags.Paralizado</c>): la que el cliente
    /// ve, y no la que ya venció. Y la pendiente de uno que todavía no entró al mundo no se pierde en un guardado.</summary>
    [Fact]
    public void Se_guarda_la_paralisis_y_no_la_inmovilizacion()
    {
        var paralizado = new Player { CharIndex = 1, Account = "cuenta", Name = "P", ClientParalyzed = true, MarcaParalizado = true };
        var inmovilizado = new Player { CharIndex = 1, Account = "cuenta", Name = "I", ClientParalyzed = true, MarcaParalizado = false, MarcaInmovilizado = true };
        var vencido = new Player { CharIndex = 1, Account = "cuenta", Name = "V", ClientParalyzed = false, MarcaParalizado = true };
        var pendiente = new Player { CharIndex = 1, Account = "cuenta", Name = "E", ParalizadoAlEntrar = true };

        Assert.True(FichaDePersonaje.Desde(paralizado).Paralizado);
        Assert.False(FichaDePersonaje.Desde(inmovilizado).Paralizado);
        Assert.False(FichaDePersonaje.Desde(vencido).Paralizado);
        Assert.True(FichaDePersonaje.Desde(pendiente).Paralizado);
        Assert.True(FichaDePersonaje.Desde(FichaDePersonaje.Desde(paralizado).CrearPlayer(1, "cuenta")).Paralizado);
    }

    [Fact]
    public void Paralizado_e_incinerado_van_y_vuelven_por_el_chr()
    {
        var ficha = FichasDePrueba.Llena("Quemado") with { Paralizado = true, Incinerado = true };
        var leida = CharacterFile.FichaDesdeIni(IniFile.Parse(CharacterFile.ToIni(ficha).ToText()), "Quemado");
        Assert.True(leida.Paralizado);
        Assert.True(leida.Incinerado);
    }

    /// <summary>Archivos RAROS, que es donde vive el saneo: un bando que no existe, un rango de más, contadores negativos,
    /// la ranura del barco fuera del inventario, un veneno de más de un byte, una ranura de bóveda con el tercer campo, y
    /// secciones enteras que faltan (un <c>.chr</c> viejo, o vacío).</summary>
    private static IEnumerable<string> Textos()
    {
        foreach (var p in Jugadores()) yield return CharacterFileViejo.ToIni(p).ToText();
        yield return "[FACCIONES]\nStatus=2\nRango=15\nCrimMatados=-5\n[FLAGS]\nEnvenenado=300\nMurio=-2\nBan=2\n" +
                     "[INIT]\nHogar=4\nPosition=basura\n[Inventory]\nBarcoSlot=40\nObj1=5-6-1\n[BancoInventory]\nObj1=5-6-1\n" +
                     "[AMIGOS]\nCantidad=3\nAmigo1=Uno\nAmigo2=\nAmigo3=Tres\n[FAMILIAR]\nTipo=2\nNombre=Sin nivel\n";
        yield return "";
    }

    [Fact]
    public void El_personaje_leido_es_el_mismo_de_antes()
    {
        foreach (var texto in Textos())
        {
            var viejo = CharacterFileViejo.FromIni(IniFile.Parse(texto), 3, "Leido", "cuenta");
            var nuevo = CharacterFile.FromIni(IniFile.Parse(texto), 3, "Leido", "cuenta");
            // Todo lo que se guarda, comparado por el formato de antes...
            Assert.Equal(CharacterFileViejo.ToIni(viejo).ToText(), CharacterFileViejo.ToIni(nuevo).ToText());
            // ...y lo que el .chr no muestra tal cual.
            Assert.Equal(viejo.PoisonedUntilTick, nuevo.PoisonedUntilTick);
            Assert.Equal(viejo.ValorDelVeneno, nuevo.ValorDelVeneno);
            Assert.Equal(viejo.IsAlive, nuevo.IsAlive);
            Assert.Equal(viejo.OrigHead, nuevo.OrigHead);
            Assert.Equal(viejo.CharIndex, nuevo.CharIndex);
            Assert.Equal(viejo.Account, nuevo.Account);
            for (int i = 1; i <= GameLimits.MaxBankSlots; i++)
                Assert.Equal(viejo.BankInventory[i], nuevo.BankInventory[i]);
            Assert.Equal(viejo.Familiar.Nivel, nuevo.Familiar.Nivel);
        }
    }

    [Fact]
    public void La_ficha_va_y_vuelve_sin_perder_nada()
    {
        foreach (var p in Jugadores())
        {
            var ficha = FichaDePersonaje.Desde(p);
            var otra = FichaDePersonaje.Desde(ficha.CrearPlayer(1, "cuenta"));
            Assert.Equal(CharacterFile.ToIni(ficha).ToText(), CharacterFile.ToIni(otra).ToText());
        }
    }

    /// <summary>La ficha es una FOTO: lo que le pase al personaje vivo después de armarla no la toca. Es lo que permite
    /// armarla en el hilo del mundo y escribirla en otro.</summary>
    [Fact]
    public void La_ficha_no_cambia_si_cambia_el_personaje()
    {
        var p = Completo();
        var ficha = FichaDePersonaje.Desde(p);
        string antes = CharacterFile.ToIni(ficha).ToText();

        p.Inventory[1] = new InventorySlot { ObjIndex = 999, Amount = 9 };
        p.Spells[1] = 99;
        p.Skills[1] = 1;
        p.Attributes[1] = 1;
        p.Friends.Add("Nuevo");
        p.Familiar.Habilidades[0] = 9;
        p.Gold = 0;

        Assert.Equal(antes, CharacterFile.ToIni(ficha).ToText());
    }
}
