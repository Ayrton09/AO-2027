using CoverAO.Server.Data;
using CoverAO.Server.Game;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// El ÍNDICE POR TILE de los NPCs (World). No es una feature de juego: es lo que hace que
/// <see cref="World.NpcAt"/> sea un lookup y no un barrido de la población entera.
///
/// El barrido no era gratis por un camino que no se ve venir. Con el mundo real hay 5404 criaturas y 4704 de
/// ellas corren <c>NpcIrUsuarioCercano</c>, que cuando no encuentra a nadie tira un paso al azar con
/// probabilidad 1/11 (AI_NPC.bas:360, "a bailar"). O sea ~430 pasos por segundo con el servidor VACÍO, y cada
/// paso preguntaba <c>NpcAt</c> por el tile destino: 2,3 millones de comparaciones por segundo, creciendo con
/// el CUADRADO de la población.
///
/// Lo que se testea acá es lo único que el índice puede romper: quedar desincronizado del mundo. Un índice que
/// miente es peor que un barrido lento — deja NPCs fantasma bloqueando tiles vacíos, o tiles ocupados que los
/// demás pisan.
/// </summary>
public class WorldIndexTests
{
    private static NpcInfo Template() =>
        new(Index: 1, Name: "Rata", Body: 1, Head: 0, Heading: 3, MaxHp: 10, MinHit: 1, MaxHit: 1,
            Def: 0, GiveExp: 1, GiveGld: 1, PoderEvasion: 0, PoderAtaque: 1);

    private static Npc Nuevo(World w, byte x, byte y) =>
        new() { CharIndex = w.AllocateCharIndex(), Template = Template(), Hp = 10, Pos = new WorldPos(1, x, y) };

    [Fact]
    public void Se_encuentra_donde_esta_y_no_donde_no_esta()
    {
        var w = new World();
        var npc = Nuevo(w, 50, 50);
        w.AddNpc(npc);
        Assert.Same(npc, w.NpcAt(new WorldPos(1, 50, 50)));
        Assert.Null(w.NpcAt(new WorldPos(1, 51, 50)));
        Assert.Null(w.NpcAt(new WorldPos(2, 50, 50)));   // mismo tile, otro mapa
    }

    [Fact]
    public void Moverlo_libera_el_tile_viejo_y_ocupa_el_nuevo()
    {
        var w = new World();
        var npc = Nuevo(w, 50, 50);
        w.AddNpc(npc);
        w.MoveNpc(npc, new WorldPos(1, 51, 50));
        Assert.Null(w.NpcAt(new WorldPos(1, 50, 50)));   // si esto falla, el NPC bloquea un tile donde no está
        Assert.Same(npc, w.NpcAt(new WorldPos(1, 51, 50)));
        Assert.Equal(new WorldPos(1, 51, 50), npc.Pos);
    }

    [Fact]
    public void Sacarlo_del_mundo_libera_su_tile()
    {
        var w = new World();
        var npc = Nuevo(w, 50, 50);
        w.AddNpc(npc);
        w.RemoveNpc(npc.CharIndex);
        Assert.Null(w.NpcAt(new WorldPos(1, 50, 50)));
        Assert.False(w.HasNpc(npc.CharIndex));
    }

    [Fact]
    public void Dos_NPCs_en_el_mismo_tile_no_se_borran_entre_si()
    {
        // No es hipotético: SpawnNow devuelve al NPC a su punto de origen al respawnear SIN mirar si mientras
        // tanto se le paró otro encima. Con un índice de un solo NPC por tile, el segundo pisaba al primero y
        // al sacar a uno el tile quedaba "libre" con el otro todavía parado ahí.
        var w = new World();
        var a = Nuevo(w, 50, 50);
        var b = Nuevo(w, 50, 50);
        w.AddNpc(a); w.AddNpc(b);
        Assert.NotNull(w.NpcAt(new WorldPos(1, 50, 50)));

        w.RemoveNpc(a.CharIndex);
        Assert.Same(b, w.NpcAt(new WorldPos(1, 50, 50)));   // el que queda sigue estando

        w.RemoveNpc(b.CharIndex);
        Assert.Null(w.NpcAt(new WorldPos(1, 50, 50)));      // recién ahora el tile está libre
    }

    [Fact]
    public void Reusar_un_CharIndex_no_deja_un_NPC_fantasma_bloqueando_el_tile()
    {
        // AllocateCharIndex es un contador que se castea a short: con suficiente uptime da la vuelta y los
        // índices se repiten. Sin índice el choque pasaba desapercibido; con índice, pisar la entrada sin
        // desindexar al anterior lo dejaría marcando un tile ocupado para siempre, y sin estar en el mundo.
        var w = new World();
        var viejo = Nuevo(w, 50, 50);
        w.AddNpc(viejo);
        var nuevo = new Npc { CharIndex = viejo.CharIndex, Template = Template(), Hp = 10, Pos = new WorldPos(1, 60, 60) };
        w.AddNpc(nuevo);

        Assert.Null(w.NpcAt(new WorldPos(1, 50, 50)));      // el tile del viejo quedó libre
        Assert.Same(nuevo, w.NpcAt(new WorldPos(1, 60, 60)));
        Assert.Single(w.NpcsOnMap(1));
    }

    [Fact]
    public void Solo_las_invocaciones_entran_a_la_lista_de_vencimiento()
    {
        // El reloj recorre esta lista 25 veces por segundo. Si un NPC común entrara, volveríamos a filtrar los
        // 5404; si una invocación NO entrara, se quedaría en el mundo para siempre.
        var w = new World();
        var comun = Nuevo(w, 50, 50);
        var invocado = Nuevo(w, 51, 50);
        invocado.ExpireAtTick = 1000;
        w.AddNpc(comun); w.AddNpc(invocado);
        Assert.Single(w.NpcsConVencimiento);
        Assert.Same(invocado, w.NpcsConVencimiento[0]);

        w.RemoveNpc(invocado.CharIndex);
        Assert.Empty(w.NpcsConVencimiento);
    }

    [Fact]
    public void Los_objetos_del_suelo_se_consultan_por_mapa()
    {
        // GroundInView/GroundOnMap recorrían los ~30 mil objetos del mundo entero en cada login y cada teleport.
        var w = new World();
        w.SetGround(new WorldPos(1, 50, 50), 1, 1);
        w.SetGround(new WorldPos(1, 90, 90), 2, 1);      // mismo mapa, fuera del área de visión
        w.SetGround(new WorldPos(2, 50, 50), 3, 1);      // otro mapa

        Assert.Equal(2, w.GroundOnMap(1).Count());
        Assert.Single(w.GroundOnMap(2));
        Assert.Empty(w.GroundOnMap(3));

        var aLaVista = w.GroundInView(new WorldPos(1, 50, 50)).ToList();
        Assert.Single(aLaVista);
        Assert.Equal(1, aLaVista[0].ObjIndex);

        w.RemoveGround(new WorldPos(1, 50, 50));
        Assert.Single(w.GroundOnMap(1));
        Assert.Empty(w.GroundInView(new WorldPos(1, 50, 50)));
    }
}
