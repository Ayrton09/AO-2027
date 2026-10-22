using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;

namespace CoverAO.Server.Net;

/// <summary>
/// VENDERLE A LA GENTE POR EL CENTRO DE SUBASTAS (decisión del autor, 22/10). El comercio entre usuarios del original es código
/// muerto (nadie llama <c>IniciarComercioConUsuario</c>), así que el canal con los jugadores es la subasta (<c>Game/Subastas.cs</c>).
///
/// <para>Lo que publica es lo que hoy le vende al mercader y vale mucho (<see cref="VaALaSubasta"/>): el equipo que dejó al
/// mejorar, que el mercader paga a un tercio. En un pueblo con Subastador no se lo vende al mercader (<see cref="GuardaParaLaSubasta"/>)
/// y al salir del mostrador lo publica (<see cref="PublicarEnLaSubastaAsync"/>) por debajo de lo que lo cobra el mercader
/// (<see cref="PrecioDeSubasta"/>), con compra directa. El oro de la venta y lo que vence sin ofertas van a la bóveda, como para
/// cualquiera. Si no llega al Subastador, por un rato vende como siempre.</para>
/// </summary>
public sealed partial class Bot
{
    /// <summary>Desde este valor del objeto se subasta en vez de vendérselo al mercader.</summary>
    public const int ValorParaSubastar = 1_000;

    /// <summary>Cuántas subastas suyas abiertas a la vez, como mucho.</summary>
    public const int SubastasPropias = 3;

    /// <summary>Cuánto dura cada una: el depósito es de 10 monedas por hora más el 1 % del precio final.</summary>
    public const byte HorasDeSubasta = 12;

    /// <summary>Si no pudo publicar, este rato le vende al mercader como siempre.</summary>
    public const int SinSubastaMs = 30 * 60_000;

    private const int RadioDelSubastador = 2;

    private long _sinSubastaHasta;

    /// <summary>¿Se subasta? Lo que vale, salvo lo que no se puede publicar (lo que no se tira y lo de novato).</summary>
    public static bool VaALaSubasta(ObjInfo? o) => o is { Value: >= ValorParaSubastar, NoSeTira: false, IsNewbie: false };

    /// <summary>
    /// EL PRECIO: compra directa al 60 % del valor y ofertas desde el 45 %. El mercader lo vende a casi el valor y lo compra a un
    /// tercio, así que la subasta le conviene al jugador que compra y al bot que vende.
    /// </summary>
    public static (int Inicial, int Final) PrecioDeSubasta(int valor) =>
        ((int)Matematicas.Porcentaje(valor, 45), (int)Matematicas.Porcentaje(valor, 60));

    /// <summary>¿Hay Subastador en ese mapa? Por lo que nace ahí (la ficha con <c>NpcType=16</c>). El punto es un struct: el
    /// <c>FirstOrDefault</c> a secas devolvía uno vacío y todo mapa tenía Subastador.</summary>
    private MapNpcSpawn? SubastadorDe(int mapa) =>
        _entorno.Datos.MapSpawns.TryGetValue(mapa, out var nacen)
            ? nacen.Where(s => EsSubastador((short)s.NpcIndex)).Select(s => (MapNpcSpawn?)s).FirstOrDefault() : null;

    private bool EsSubastador(short ficha) => _entorno.Datos.Npc(ficha)?.NpcType == 16;

    /// <summary>Las suyas en la última lista que vio.</summary>
    private int SusSubastas(SentidosDelBot sentidos) =>
        sentidos.Subastas.Count(f => string.Equals(f.Vendedor, Nombre, StringComparison.OrdinalIgnoreCase));

    /// <summary>¿Lo guarda para la subasta en vez de vendérselo al mercader? Si se subasta, hay Subastador en el pueblo, no le
    /// falló hace poco y no tiene ya todas las que se permite.</summary>
    private bool GuardaParaLaSubasta(SentidosDelBot sentidos, short obj) =>
        VaALaSubasta(_entorno.Datos.Obj(obj)) && Environment.TickCount64 >= _sinSubastaHasta
        && SubastadorDe(sentidos.Mapa) is not null && SusSubastas(sentidos) < SubastasPropias;

    /// <summary>
    /// AL SALIR DEL MOSTRADOR, LO QUE GUARDÓ, A LA SUBASTA: camina hasta el Subastador, lo clickea (no justo debajo: ver
    /// <see cref="PonerElHogarAsync"/>), pide la lista para contar las suyas y publica de a una. Devuelve si publicó algo.
    /// </summary>
    private async Task<bool> PublicarEnLaSubastaAsync(TransporteDeBot transporte, SentidosDelBot sentidos, Task cerrada, CancellationToken ct)
    {
        if (sentidos.EnLaMochila(obj => EsBotin(sentidos, obj) && GuardaParaLaSubasta(sentidos, obj)) is null) return false;
        if (SubastadorDe(sentidos.Mapa) is not { } nace) return false;
        bool publico = false;
        try
        {
            if (!await IrHastaAsync(transporte, sentidos, sentidos.Mapa, (byte)nace.X, (byte)nace.Y, cerrada, ct, RadioDelSubastador)) return false;
            if (sentidos.MasCercano(EsSubastador) is not { } s
                || Math.Abs(s.Quien.X - sentidos.Posicion.X) + Math.Abs(s.Quien.Y - sentidos.Posicion.Y) > RadioDelSubastador + 1) return false;
            if (sentidos.Posicion == (s.Quien.X, (byte)(s.Quien.Y + 1))
                && await UnPasoHaciaAsync(transporte, sentidos, 1, 0, ct) != Paso.Dado) return false;
            if (!transporte.Mandar(Paquete(ClientPacketId.LeftClick, w => w.WriteByte(s.Quien.X).WriteByte(s.Quien.Y)))) return false;
            await Task.Delay(_rng.Next(500, 1_200), ct);
            int vistas = sentidos.VecesQueVioLasSubastas;
            if (!transporte.Mandar(Paquete(ClientPacketId.SubastasPedir, _ => { }))) return false;
            for (int espera = 0; espera < 12 && sentidos.VecesQueVioLasSubastas == vistas; espera++) await Task.Delay(250, ct);
            if (sentidos.VecesQueVioLasSubastas == vistas) return false;   // no atendió: lejos, o no era el Subastador

            for (int vez = 0; vez < SubastasPropias && SusSubastas(sentidos) < SubastasPropias; vez++)
            {
                if (sentidos.EnLaMochila(obj => EsBotin(sentidos, obj) && GuardaParaLaSubasta(sentidos, obj)) is not { } item) break;
                short obj = sentidos.QueHayEn(item.Ranura);
                var (inicial, final) = PrecioDeSubasta(_entorno.Datos.Obj(obj)!.Value * item.Cantidad);
                int antes = sentidos.CuantoTiene(o => o == obj);
                vistas = sentidos.VecesQueVioLasSubastas;
                transporte.Mandar(Paquete(ClientPacketId.SubastaPublicar, w => w.WriteByte(item.Ranura).WriteInteger(item.Cantidad)
                    .WriteLong(inicial).WriteLong(final).WriteByte(HorasDeSubasta)));
                for (int espera = 0; espera < 12 && sentidos.VecesQueVioLasSubastas == vistas; espera++) await Task.Delay(250, ct);
                if (sentidos.CuantoTiene(o => o == obj) >= antes) break;   // no se la tomó (el depósito, el Centro lleno)
                publico = true;
                Console.WriteLine($"[bot {Nombre}] subasta: {item.Cantidad} x obj {obj}, desde {inicial} (final {final})");
                await Task.Delay(_rng.Next(800, 2_000), ct);
            }
            return publico;
        }
        finally
        {
            // SIN PUBLICAR NADA, UN RATO VENDE COMO SIEMPRE: si no, el equipo viejo quedaría en la mochila para siempre.
            if (!publico) _sinSubastaHasta = Environment.TickCount64 + SinSubastaMs;
        }
    }
}
