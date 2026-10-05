using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// LA BARRA DE VIDA DE LOS JEFES (pedido del tester, 2026-09-27; no existe en el original). El server le cuenta la vida de
/// cada jefe al que lo está viendo: cuando cambia, y una vez por segundo aunque no cambie, que es lo que le da la barra al
/// que recién llega. Un jefe es una criatura hostil que da experiencia y tiene por lo menos <c>VidaMinimaDeJefe</c> de vida
/// máxima; los blancos de práctica (mucha vida, cero experiencia) no lo son. Con 0 en el ini no sale nada.
/// </summary>
public class LaBarraDeLosJefesTests
{
    private sealed class Conexion : IConnection
    {
        public readonly List<byte[]> Recibidos = new();
        public string RemoteEndpoint => "test";
        public long UltimaActividadMs => Environment.TickCount64;
        public void Send(byte[] packet) => Recibidos.Add(packet);
        public void Close(string reason) { }
    }

    private static ServerIni Ini(string texto = "") => ServerIni.Desde(IniFile.Parse("[INIT]\n" + texto), "test.ini");

    private static Npc Criatura(World world, string nombre, int vidaMaxima, long exp, byte x, bool hostil = true)
    {
        var ficha = new NpcInfo(Index: 1, Name: nombre, Body: 1, Head: 0, Heading: 3, MaxHp: vidaMaxima, MinHit: 1, MaxHit: 1,
            Def: 0, GiveExp: exp, GiveGld: 0, PoderEvasion: 0, PoderAtaque: 0, Hostile: hostil);
        var npc = new Npc
        {
            CharIndex = world.AllocateCharIndex(), Template = ficha, Hp = vidaMaxima, Pos = new WorldPos(1, x, 10),
            Hostile = hostil, OriginallyHostile = hostil, Heading = 3,
        };
        world.AddNpc(npc);
        return npc;
    }

    /// <summary>Un jugador mirando a un jefe, a una criatura común y a un blanco de práctica, todos a la vista.</summary>
    private static (World Mundo, Conexion Vista, Npc Jefe, Npc Comun, Npc Blanco) Escena()
    {
        var world = new World();
        var vista = new Conexion();
        var yo = new Player { CharIndex = world.AllocateCharIndex(), Name = "Aldo", Account = "aldo", Connection = vista };
        yo.Pos = new WorldPos(1, 10, 10);
        world.Add(yo);
        var jefe = Criatura(world, "Rey Dragón", 900_000, 2_200_000, 12);
        var comun = Criatura(world, "Lobo", 1_000, 50, 13);
        var blanco = Criatura(world, "Blanco de Combate", 100_000, 0, 14);
        return (world, vista, jefe, comun, blanco);
    }

    private static List<(short CharIndex, int Vida, int Maxima)> Vidas(Conexion c) => c.Recibidos
        .Where((p) => p[0] == (byte)ServerPacketId.VidaDelJefe)
        .Select((p) => (BitConverter.ToInt16(p, 1), BitConverter.ToInt32(p, 3), BitConverter.ToInt32(p, 7))).ToList();

    [Fact]
    public void El_paquete_lleva_el_charIndex_y_la_vida_en_Long()
    {
        var p = ServerMessages.VidaDelJefe(1234, 450_000, 900_000);
        Assert.Equal(11, p.Length);
        Assert.Equal(108, p[0]);
        Assert.Equal((short)1234, BitConverter.ToInt16(p, 1));
        Assert.Equal(450_000, BitConverter.ToInt32(p, 3));
        Assert.Equal(900_000, BitConverter.ToInt32(p, 7));
    }

    [Fact]
    public void Anuncia_la_vida_del_jefe_y_no_la_de_la_criatura_comun_ni_la_del_blanco()
    {
        var (mundo, vista, jefe, _, _) = Escena();
        new VidaDeLosJefes(mundo, Ini()).Tick(1);
        Assert.Equal(new[] { (jefe.CharIndex, 900_000, 900_000) }, Vidas(vista));
    }

    [Fact]
    public void Cuando_la_vida_cambia_la_vuelve_a_anunciar_y_si_no_cambia_una_vez_por_segundo()
    {
        var (mundo, vista, jefe, _, _) = Escena();
        var barra = new VidaDeLosJefes(mundo, Ini());
        barra.Tick(1);
        barra.Tick(2);
        Assert.Single(Vidas(vista));                           // sin cambio no se repite en la vuelta siguiente
        jefe.Hp = 850_000;
        barra.Tick(3);
        Assert.Equal((jefe.CharIndex, 850_000, 900_000), Vidas(vista)[^1]);
        for (int vuelta = 4; vuelta < VidaDeLosJefes.VueltasDelRepaso; vuelta++) barra.Tick(vuelta);
        Assert.Equal(2, Vidas(vista).Count);
        barra.Tick(VidaDeLosJefes.VueltasDelRepaso);            // el repaso: la ve el que recién llega
        Assert.Equal(3, Vidas(vista).Count);
    }

    [Fact]
    public void El_que_no_lo_ve_no_recibe_nada()
    {
        var (mundo, vista, jefe, _, _) = Escena();
        jefe.Pos = new WorldPos(1, 90, 90);                   // otra área del mismo mapa
        new VidaDeLosJefes(mundo, Ini()).Tick(1);
        Assert.Empty(Vidas(vista));
    }

    [Fact]
    public void Con_cero_en_el_ini_no_hay_jefes_ni_paquete()
    {
        var (mundo, vista, _, _, _) = Escena();
        var barra = new VidaDeLosJefes(mundo, Ini("VidaMinimaDeJefe=0\n"));
        for (int vuelta = 1; vuelta <= VidaDeLosJefes.VueltasDelRepaso; vuelta++) barra.Tick(vuelta);
        Assert.Empty(vista.Recibidos);
    }

    [Fact]
    public void El_umbral_sale_del_ini()
    {
        var (mundo, vista, jefe, comun, _) = Escena();
        new VidaDeLosJefes(mundo, Ini("VidaMinimaDeJefe=1000\n")).Tick(1);
        Assert.Equal(new[] { jefe.CharIndex, comun.CharIndex }.OrderBy((c) => c), Vidas(vista).Select((v) => v.CharIndex).OrderBy((c) => c));
    }

    [Fact]
    public void Las_claves_vienen_prendidas_y_el_destino_de_los_portales_le_llega_al_cliente()
    {
        var porDefecto = Ini();
        Assert.Equal(50_000, porDefecto.VidaMinimaDeJefe);
        Assert.True(porDefecto.DestinoDeLosPortales);
        Assert.Equal(0, Ini("VidaMinimaDeJefe=-5\n").VidaMinimaDeJefe);
        Assert.False(Ini("DestinoDeLosPortales=0\n").DestinoDeLosPortales);
        Assert.Contains("VidaMinimaDeJefe", ServerIni.ClavesConocidas);
        Assert.Contains("DestinoDeLosPortales", ServerIni.ClavesConocidas);
        // LA LLUVIA BAJO TECHO viene APAGADA (no llueve adentro), y se prende desde el ini (el autor, 05/10).
        Assert.False(porDefecto.LluviaBajoTecho);
        Assert.True(Ini("LluviaBajoTecho=1\n").LluviaBajoTecho);
        Assert.Contains("LluviaBajoTecho", ServerIni.ClavesConocidas);

        var raiz = Path.Combine(Path.GetTempPath(), "coverao-portales-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(raiz);
        try
        {
            Assert.Contains("\"destinoPortales\":true", new StaticFileServer(7669, raiz, 7667).ConfigJson);
            var apagado = new StaticFileServer(7669, raiz, 7667, destinoDeLosPortales: false);
            Assert.Contains("\"destinoPortales\":false", apagado.ConfigJson);
            using var json = System.Text.Json.JsonDocument.Parse(apagado.ConfigJson);   // y sigue siendo JSON válido
            Assert.True(json.RootElement.GetProperty("alcanceHechizo").GetBoolean());
            Assert.False(json.RootElement.GetProperty("lluviaBajoTecho").GetBoolean());
            Assert.Contains("\"lluviaBajoTecho\":true", new StaticFileServer(7669, raiz, 7667, lluviaBajoTecho: true).ConfigJson);
        }
        finally { Directory.Delete(raiz, recursive: true); }
    }
}
