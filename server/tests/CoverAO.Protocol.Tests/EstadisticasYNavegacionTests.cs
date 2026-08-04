using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// Dos deudas que la auditoría de release encontró y que ningún test veía, por el mismo motivo en los dos
/// casos: <b>el campo estaba, el emisor estaba, y nadie los había conectado</b>.
///
/// 1. <b>MiniStats mandaba seis ceros cableados</b> (ciudadanos/renegados/republicanos/caos/armada/milicianos
///    matados y las muertes propias) aunque el <c>Player</c> lleva esos contadores y el <c>.chr</c> los
///    persiste. La ventana de estadísticas del cliente mostraba "Ciudadanos matados 0" para siempre.
///
/// 2. <b>Navegando no se persistía</b>. El VB6 lo guarda (FileIO.bas:1632) y lo RELEE (TCP.bas:1866), junto
///    con el BarcoSlot (FileIO.bas:1807 / TCP.bas:2053). Sin eso, desloguear en alta mar te devolvía al
///    juego parado en el agua y a pie: el gate de movimiento (<c>IsWater(destino) != Navigating</c>) le
///    rechaza los cuatro vecinos, así que quedaba plantado hasta que se le ocurriera reusar el barco.
///
/// El contador de muertes (<c>flags.MuertesUsuario</c>, +1 en UserDie) tampoco existía: se agregó con ellos.
/// </summary>
public class EstadisticasYNavegacionTests
{
    private static Player RoundTrip(Player p) =>
        CharacterFile.FromIni(IniFile.Parse(CharacterFile.ToIni(p).ToText()), p.CharIndex, p.Name, p.Account);

    private static Player NewChar() => new()
    {
        CharIndex = 1, Name = "Marino", Account = "cuenta", Class = 1, Level = 1,
        MaxHp = 100, Hp = 100, MaxStamina = 100, Stamina = 100,
    };

    // ---- MiniStats -------------------------------------------------------------------------------------

    /// <summary>Layout del VB6 <c>WriteMiniStats</c> (Protocol.bas:13373), en orden:
    /// Long ciudadanos · Long renegados · Long usuarios · Integer NPCs · Byte clase · Byte raza · Byte género ·
    /// Long muertes · Byte status · Long republicanos · Long caos · Long armada · Long milicianos.</summary>
    [Fact]
    public void MiniStats_lleva_los_contadores_reales_y_no_ceros()
    {
        var p = NewChar();
        p.CiudadanosMatados = 11; p.RenegadosMatados = 22; p.UsersKilled = 33; p.NpcsKilled = 44;
        p.Deaths = 55; p.RepublicanosMatados = 66; p.CaosMatados = 77;
        p.ArmadaMatados = 88; p.MilicianosMatados = 99;

        var r = new ByteQueueReader(ServerMessages.MiniStats(p));
        r.ReadByte();                                    // id del paquete
        Assert.Equal(11, r.ReadLong());                   // CiudadanosMatados
        Assert.Equal(22, r.ReadLong());                   // RenegadosMatados
        Assert.Equal(33, r.ReadLong());                   // UsuariosMatados
        Assert.Equal((short)44, r.ReadInteger());                // NPCsMuertos
        Assert.Equal(p.Class, r.ReadByte());
        Assert.Equal(p.Race, r.ReadByte());
        Assert.Equal(p.Gender, r.ReadByte());
        Assert.Equal(55, r.ReadLong());                   // MuertesUsuario — el que no existía
        Assert.Equal(p.FactionStatus, r.ReadByte());
        Assert.Equal(66, r.ReadLong());                   // RepublicanosMatados
        Assert.Equal(77, r.ReadLong());                   // CaosMatados
        Assert.Equal(88, r.ReadLong());                   // ArmadaMatados
        Assert.Equal(99, r.ReadLong());                   // MilicianosMatados
    }

    /// <summary>El contador de muertes tiene que sobrevivir el .chr: se muestra como "Veces que moriste".</summary>
    [Fact]
    public void Las_muertes_persisten_en_el_chr()
    {
        var p = NewChar();
        Assert.Equal(0, p.Deaths);

        p.Deaths = 7;
        Assert.Equal(7, RoundTrip(p).Deaths);
    }

    // ---- Navegación ------------------------------------------------------------------------------------

    [Fact]
    public void Navegando_y_el_slot_del_barco_persisten()
    {
        var p = NewChar();
        Assert.False(p.Navigating);
        Assert.Equal(0, (int)p.BoatSlot);

        p.Navigating = true;
        p.BoatSlot = 5;

        var loaded = RoundTrip(p);
        Assert.True(loaded.Navigating, "quien desloguea navegando tiene que volver navegando (VB6 TCP.bas:1866)");
        Assert.Equal(5, (int)loaded.BoatSlot);
    }

    [Fact]
    public void Desembarcar_tambien_persiste()
    {
        var p = NewChar();
        p.Navigating = true; p.BoatSlot = 5;
        p = RoundTrip(p);
        Assert.True(p.Navigating);

        p.Navigating = false; p.BoatSlot = 0;
        var loaded = RoundTrip(p);
        Assert.False(loaded.Navigating);
        Assert.Equal(0, (int)loaded.BoatSlot);
    }

    // ---- Montura ---------------------------------------------------------------------------------------

    /// <summary><c>Montando</c> vive en el .chr una línea DEBAJO de <c>Navegando</c> en las dos puntas del VB6
    /// (FileIO.bas:1633 lo escribe, TCP.bas:1867 lo relee) y se había quedado afuera junto con él.
    ///
    /// El estado quedaba PARTIDO al reloguear, y es lo que lo hace un bug y no un detalle: el cuerpo pasa a
    /// ser el <c>Ropaje</c> de la montura y el ítem queda marcado como equipado —las dos cosas SÍ persisten—
    /// mientras el flag volvía en <c>false</c>. Aparecías a caballo a la vista de todos y el server te trataba
    /// como si fueras a pie.</summary>
    [Fact]
    public void Montando_persiste_en_el_chr()
    {
        var p = NewChar();
        Assert.False(p.Mounted);

        p.Mounted = true;
        Assert.True(RoundTrip(p).Mounted, "quien desloguea a caballo vuelve a caballo (VB6 TCP.bas:1867)");

        p.Mounted = false;
        Assert.False(RoundTrip(p).Mounted);
    }

    /// <summary>El cuerpo de la montura y el flag tienen que volver JUNTOS: es su desacuerdo el que rompía.</summary>
    [Fact]
    public void El_cuerpo_de_la_montura_y_el_flag_vuelven_juntos()
    {
        var p = NewChar();
        p.Mounted = true;
        p.Body = 700;              // el Ropaje de la montura, que AlternarMontura deja puesto

        var loaded = RoundTrip(p);
        Assert.True(loaded.Mounted);
        Assert.Equal(700, loaded.Body);
    }

    // ---- Veneno ----------------------------------------------------------------------------------------

    /// <summary>El veneno no caduca solo —ni en el VB6 ni acá—: se apaga al curarse. Sin persistirlo,
    /// desloguear y volver a entrar era una cura gratis (VB6 FileIO.bas:1634 / TCP.bas:1868).</summary>
    [Fact]
    public void El_veneno_sobrevive_al_relogueo()
    {
        var p = NewChar();
        Assert.False(p.IsPoisoned(1));

        p.PoisonedUntilTick = long.MaxValue;
        var envenenado = RoundTrip(p);
        Assert.True(envenenado.IsPoisoned(long.MaxValue - 1), "el veneno no se cura desconectando");

        p.PoisonedUntilTick = 0;                       // curado antes de salir
        Assert.False(RoundTrip(p).IsPoisoned(1));
    }

    /// <summary>Un .chr anterior a estos campos se carga en el estado seguro: a pie y sin muertes.</summary>
    [Fact]
    public void Un_chr_viejo_se_carga_a_pie_y_sin_muertes()
    {
        var text = CharacterFile.ToIni(NewChar()).ToText()
            .Replace("Navegando=0", "").Replace("Montando=0", "").Replace("Envenenado=0", "")
            .Replace("Murio=0", "").Replace("BarcoSlot=0", "");
        var loaded = CharacterFile.FromIni(IniFile.Parse(text), 1, "Marino", "cuenta");

        Assert.False(loaded.Navigating);
        Assert.False(loaded.Mounted);
        Assert.False(loaded.IsPoisoned(1));
        Assert.Equal(0, loaded.Deaths);
        Assert.Equal(0, (int)loaded.BoatSlot);
    }

    /// <summary>Un BarcoSlot fuera de rango (un .chr manoseado a mano) se descarta en vez de indexar al vacío.</summary>
    [Fact]
    public void Un_barco_slot_fuera_de_rango_se_descarta()
    {
        var p = NewChar();
        p.Navigating = true; p.BoatSlot = 5;
        var text = CharacterFile.ToIni(p).ToText().Replace("BarcoSlot=5", "BarcoSlot=250");
        var loaded = CharacterFile.FromIni(IniFile.Parse(text), 1, "Marino", "cuenta");

        Assert.Equal(0, (int)loaded.BoatSlot);
    }
}
