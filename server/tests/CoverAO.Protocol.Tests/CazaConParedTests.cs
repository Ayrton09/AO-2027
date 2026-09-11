using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// LA PARED SE RODEA. La tienda de comidas de la cuna es un bloque de ocho filas: desde (52,67) un Murciélago que nace en (50,58)
/// queda a once baldosas en línea recta y a dieciséis por camino. Medido en `verdad1`: un bot quince minutos rebotando entre (49,67)
/// y (53,67), nivel 1 y seis criaturas en toda la corrida.
/// </summary>
public class CazaConParedTests
{
    private const int Cuna = 37;

    private static (Navegante Mapas, GameData Datos)? Mundo()
    {
        var datos = GameData.Load();
        return datos.MapExits.Count == 0 || !datos.MapSpawns.ContainsKey(Cuna) ? null : (new Navegante(datos), datos);
    }

    private static int Manhattan((byte X, byte Y) a, (byte X, byte Y) b) => Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y);

    private static Npc Criatura(short charIndex, short ficha, byte x, byte y) => new()
    {
        CharIndex = charIndex,
        Template = new NpcInfo(ficha, "Lobo", 0, 0, 0, MaxHp: 20, MinHit: 1, MaxHit: 2, Def: 0,
                               GiveExp: 5, GiveGld: 0, PoderEvasion: 0, PoderAtaque: 0),
        Pos = new WorldPos(1, x, y),
    };

    /// <summary>A LOS TUMBOS NO SE LLEGA Y RODEANDO SÍ, sobre el mapa de verdad.</summary>
    [Fact]
    public void Rodeando_llega_al_murcielago_de_atras_de_la_tienda()
    {
        if (Mundo() is not { } m) return;
        (byte X, byte Y) presa = (50, 58), arranque = (52, 67);

        // El paso codicioso va al norte, contra la pared de la tienda, y ahí se queda.
        var codicioso = Bot.PasoDeCaza(m.Mapas, Cuna, arranque, presa, choco: false, rodeando: false);
        Assert.NotNull(codicioso);
        Assert.True(m.Datos.IsBlockedTile(Cuna, codicioso!.Value.Paso.X, codicioso.Value.Paso.Y),
                    $"el paso codicioso dio ({codicioso.Value.Paso.X},{codicioso.Value.Paso.Y}), que no es pared");

        // Rodeando, cada paso sale del navegante, ninguno pisa pared, y termina al lado de la presa.
        var donde = arranque;
        bool rodeando = false;
        int pasos = 0;
        while (Manhattan(donde, presa) > 1)
        {
            var ir = Bot.PasoDeCaza(m.Mapas, Cuna, donde, presa, choco: true, rodeando: rodeando);
            Assert.True(ir is not null, $"se quedó sin camino en ({donde.X},{donde.Y})");
            rodeando = ir!.Value.Rodeando;
            donde = ir.Value.Paso;
            Assert.False(m.Datos.IsBlockedTile(Cuna, donde.X, donde.Y), $"pisó pared en ({donde.X},{donde.Y})");
            Assert.True(++pasos <= 40, "no llegó en cuarenta pasos");
        }
        Assert.True(pasos > Manhattan(arranque, presa), $"llegó en {pasos} pasos, sin rodear nada");
    }

    /// <summary>SIN CAMINO NO HAY PASO: el mercader de la cuna está adentro de la tienda, y a la tienda no se entra.</summary>
    [Fact]
    public void Sin_camino_no_devuelve_paso()
    {
        if (Mundo() is not { } m) return;
        Assert.Null(Bot.PasoDeCaza(m.Mapas, Cuna, ((byte)52, (byte)67), ((byte)52, (byte)65), choco: true, rodeando: true));
    }

    /// <summary>Y LA DESCARTADA NO SE VUELVE A ELEGIR: con la más cercana afuera, se elige la que sigue.</summary>
    [Fact]
    public void La_presa_descartada_no_se_vuelve_a_elegir()
    {
        var s = new SentidosDelBot();
        s.Recibir(ServerMessages.UserCharIndexInServer(500));
        s.Recibir(ServerMessages.CharacterCreate(new Player
        {
            CharIndex = 500, Name = "Aldo", Account = "cuenta_test", Pos = new WorldPos(1, 50, 60),
        }));
        s.Recibir(ServerMessages.CharacterCreate(Criatura(600, 501, 50, 58)));   // a dos
        s.Recibir(ServerMessages.CharacterCreate(Criatura(601, 501, 50, 55)));   // a cinco

        Assert.Equal((short)600, s.MasCercano(_ => true)!.Value.Quien.CharIndex);
        Assert.Equal((short)601, s.MasCercano(_ => true, salvo: 600)!.Value.Quien.CharIndex);
    }
}
