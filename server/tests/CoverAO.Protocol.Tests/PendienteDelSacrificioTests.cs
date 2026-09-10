using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// EL PENDIENTE DEL SACRIFICIO — una regla de la casa, no una omisión del port.
///
/// <para>El efecto es el <c>EfectoMagico = 15</c> del <c>obj.dat</c>, que en el enum del original se llama
/// <c>Sacrificio</c> (Declares.bas:872) y que el servidor VB6 <b>declara y no lee en ningún lado</b>: es data
/// muerta, como el <c>desencantar</c> del Hechizos.dat. O sea que hacerlo funcionar es AGREGAR algo, y se
/// agrega por decisión del autor a pedido del tester.</para>
///
/// <para>Las tres cargas no son un invento: la propia data las sostiene. El único que sueltan las criaturas
/// es el 3/3 —Dragón Legendario, Rey Dragón, Gran Dragón Negro, Golem de oro, Grinch—, y el 2/3 y el 1/3 no
/// los da NADIE, así que sólo pueden existir como estados gastados de uno entero.</para>
/// </summary>
public class PendienteDelSacrificioTests
{
    private const short Entero = 1081, Medio = 1498, Ultimo = 1499;

    /// <summary>Objetos que SÍ se caen: los de novato están protegidos por la regla del original.</summary>
    private const short PocionComun = 38, ArmaComun = 15;

    /// <summary>LA PREMISA, y va primera: si la data dejara de traer el efecto, todo lo de abajo pasaría por
    /// los motivos equivocados. Se salta sola si no está la carpeta del juego.</summary>
    [Fact]
    public void Los_tres_pendientes_traen_el_efecto_del_sacrificio()
    {
        var datos = GameData.Load();
        if (datos.ItemCount == 0) return;

        foreach (short idx in new[] { Entero, Medio, Ultimo })
        {
            var o = datos.Obj(idx);
            Assert.True(o is not null, $"el objeto {idx} no está en la data");
            Assert.Equal(15, o!.EfectoMagico);
        }
    }

    /// <summary>Y que el nombre diga lo que la mecánica supone: 3/3 → 2/3 → 1/3. Si alguna vez la data
    /// cambiara el orden, la cadena de gasto estaría al revés y nadie se enteraría.</summary>
    [Fact]
    public void La_cadena_va_de_entero_a_gastado()
    {
        var datos = GameData.Load();
        if (datos.ItemCount == 0) return;

        Assert.Contains("3/3", datos.Obj(Entero)!.Name);
        Assert.Contains("2/3", datos.Obj(Medio)!.Name);
        Assert.Contains("1/3", datos.Obj(Ultimo)!.Name);
    }

    /// <summary>El efecto se declara POR OBJETO y no por una lista de números: el que decide es el
    /// <c>EfectoMagico</c>, así que si mañana la data se lo pone a otro pendiente, funciona igual.</summary>
    [Fact]
    public void Ningun_otro_objeto_del_juego_trae_ese_efecto_por_accidente()
    {
        var datos = GameData.Load();
        if (datos.ItemCount == 0) return;

        var conEfecto = datos.Objects.Values.Where(o => o.EfectoMagico == 15).Select(o => o.Name).ToList();
        Assert.Equal(3, conEfecto.Count);
        Assert.All(conEfecto, n => Assert.Contains("Sacrificio", n));
    }

    // ── Lo que hace de verdad al morir ────────────────────────────────────────────────────────────────

    private static Player Probador(short pendiente)
    {
        var p = new Player { CharIndex = 1, Name = "Probador", Account = "cuenta_test", Pos = new WorldPos(15, 50, 50) };
        // OJO CON LOS ÍTEMS DE LA PRUEBA: los de novato NO se caen nunca (regla del original), así que con el
        // kit de arranque el control daba verde por el motivo equivocado — no se caía nada ni con pendiente ni
        // sin él. Se usan objetos comunes.
        p.Inventory[1] = new InventorySlot { ObjIndex = PocionComun, Amount = 100 };
        p.Inventory[2] = new InventorySlot { ObjIndex = ArmaComun, Amount = 1, Equipped = true };
        if (pendiente > 0) p.Inventory[3] = new InventorySlot { ObjIndex = pendiente, Amount = 1, Equipped = true };
        return p;
    }

    /// <summary>EL CONTROL, y va antes que el caso: sin el pendiente, morir en un mapa Pk SÍ te cuesta las
    /// cosas. Sin esta mitad, el test de abajo daría verde aunque el drop estuviera roto por otro motivo.</summary>
    [Fact]
    public void Sin_el_pendiente_morir_cuesta_el_inventario()
    {
        var datos = GameData.Load();
        if (datos.ItemCount == 0) return;
        Assert.True(datos.IsPkMap(15), "el mapa de la prueba tiene que ser Pk o no se cae nada por otra razón");

        var p = Probador(0);
        PacketDispatcher.KillPlayer(new World(), datos, p);

        Assert.True(p.Inventory[1].IsEmpty, "las pociones tenían que caerse");
    }

    [Fact]
    public void Con_el_pendiente_puesto_no_se_cae_nada_y_se_gasta_una_carga()
    {
        var datos = GameData.Load();
        if (datos.ItemCount == 0) return;

        var p = Probador(Entero);
        PacketDispatcher.KillPlayer(new World(), datos, p);

        Assert.Equal(PocionComun, p.Inventory[1].ObjIndex);   // las pociones se quedan
        Assert.Equal(ArmaComun, p.Inventory[2].ObjIndex);     // el arma también
        Assert.Equal(Medio, p.Inventory[3].ObjIndex);        // y el pendiente gastó una carga
    }

    /// <summary>La cadena entera: 3/3 → 2/3 → 1/3 → se acabó. Al gastar la última el objeto desaparece, y de
    /// ahí en más morir vuelve a costar las cosas.</summary>
    [Fact]
    public void La_ultima_carga_consume_el_pendiente()
    {
        var datos = GameData.Load();
        if (datos.ItemCount == 0) return;

        var p = Probador(Medio);
        PacketDispatcher.KillPlayer(new World(), datos, p);
        Assert.Equal(Ultimo, p.Inventory[3].ObjIndex);

        p = Probador(Ultimo);
        PacketDispatcher.KillPlayer(new World(), datos, p);
        Assert.True(p.Inventory[3].IsEmpty, "la última carga tenía que consumir el pendiente");
        Assert.Equal(PocionComun, p.Inventory[1].ObjIndex);   // pero esa muerte todavía protegió
    }

    /// <summary>Y las auras se apagan al morir. El original lo hace dentro de <c>UserDie</c>
    /// (Modulo_UsUaRiOs.bas:1347-1388) y acá no se hacía: el objeto que brillaba se caía y el brillo le
    /// quedaba pegado al personaje, porque el <c>CharacterChange</c> que se manda después no lleva auras.
    /// Lo reportó el tester.</summary>
    [Fact]
    public void Morir_apaga_las_auras()
    {
        var datos = GameData.Load();
        if (datos.ItemCount == 0) return;

        var p = Probador(0);
        p.RingAura = 12; p.WeaponAura = 3; p.BodyAura = 4; p.HelmetAura = 5; p.ShieldAura = 6;

        PacketDispatcher.KillPlayer(new World(), datos, p);

        Assert.Equal(0, p.RingAura);
        Assert.Equal(0, p.WeaponAura);
        Assert.Equal(0, p.BodyAura);
        Assert.Equal(0, p.HelmetAura);
        Assert.Equal(0, p.ShieldAura);
    }
}
