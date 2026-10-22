using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;

namespace CoverAO.Server.Net;

/// <summary>
/// COMERCIAR CON LA GENTE POR EL CENTRO DE SUBASTAS (decisión del autor, 22/10). El comercio entre usuarios del original es código
/// muerto (nadie llama <c>IniciarComercioConUsuario</c>), así que el canal con los jugadores es la subasta (<c>Game/Subastas.cs</c>).
///
/// <para>VENDE lo que hoy le vende al mercader y vale mucho (<see cref="VaALaSubasta"/>): el equipo que dejó al mejorar, que el
/// mercader paga a un tercio. En un pueblo con Subastador no se lo vende al mercader (<see cref="GuardaParaLaSubasta"/>) y al salir
/// del mostrador lo publica por debajo de lo que lo cobra el mercader (<see cref="PrecioDeSubasta"/>), con compra directa. Si no
/// llega al Subastador, por un rato vende como siempre. Lo que vence sin ofertas vuelve a la bóveda: lo retira y se lo vende al
/// mercader, sin volver a publicarlo.</para>
///
/// <para>COMPRA cada tanto (<see cref="MirarLaSubastaMs"/>) lo que lo mejora, si una persona lo vende con compra directa bastante
/// más barato que el mercader (<see cref="LeConvieneLaSubasta"/>); de otros bots no. Lo comprado llega a la bóveda, y lo retira
/// en el banquero (<see cref="RetirarLoDeLaSubastaAsync"/>); ponérselo es cosa de <see cref="Equiparse"/>.</para>
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

    /// <summary>Cada cuánto, como mucho, pasa a mirar qué hay para comprar.</summary>
    public const int MirarLaSubastaMs = 20 * 60_000;

    /// <summary>Con menos que esto para el equipo no pasa a mirar.</summary>
    public const int PlataParaMirarLaSubasta = 500;

    /// <summary>Paga como mucho este porcentaje del valor: el mercader lo cobra a casi el valor.</summary>
    public const int PorcentajeQuePaga = 80;

    private const int RadioDelSubastador = 2;

    private long _sinSubastaHasta, _proximaMiradaALaSubasta, _listaVistaEn = long.MinValue;

    /// <summary>Lo que el Centro le dejó en la bóveda (lo comprado, o lo propio que venció) y todavía no retiró.</summary>
    private readonly HashSet<short> _porRetirar = new();

    /// <summary>Lo suyo que venció sin ofertas: no se vuelve a publicar, se le vende al mercader.</summary>
    private readonly HashSet<short> _noSeVendio = new();

    /// <summary>Lo que publicó y lo que compró en esta vida: el aviso del Centro dice el nombre, y hay nombres repetidos.</summary>
    private readonly HashSet<short> _publicadas = new(), _comprados = new();

    /// <summary>
    /// ¿Se subasta? Lo que vale, salvo lo que no se puede publicar (lo que no se tira y lo de novato) y LOS PERGAMINOS: el que le
    /// sirve lo compró para aprenderlo y todavía no lo leyó al salir del mostrador. Medido en la ronda 97: Elmo compraba Remover
    /// parálisis a 8.334, lo publicaba a 6.500 y volvía a comprarlo, 51 publicaciones del mismo pergamino en tres corridas.
    /// </summary>
    public static bool VaALaSubasta(ObjInfo? o) => o is { Value: >= ValorParaSubastar, NoSeTira: false, IsNewbie: false, IsPergamino: false };

    /// <summary>
    /// EL PRECIO: compra directa al 65 % del valor y ofertas desde el 50 %. El mercader lo vende a casi el valor y lo compra a un
    /// tercio, así que la subasta le conviene al jugador que compra y al bot que vende: aun en el piso y descontado el depósito, al
    /// bot le queda más que lo que le paga el mercader (desde <see cref="ValorParaSubastar"/>).
    /// </summary>
    public static (int Inicial, int Final) PrecioDeSubasta(int valor) =>
        ((int)Matematicas.Porcentaje(valor, 50), (int)Matematicas.Porcentaje(valor, 65));

    /// <summary>
    /// ¿LE CONVIENE COMPRAR ESA? Con compra directa, de a uno, que le alcance lo de encima, a <see cref="PorcentajeQuePaga"/> del valor
    /// o menos, y de una persona: ni suya ni de otro bot (el comercio es con los jugadores).
    /// </summary>
    public static bool LeConvieneLaSubasta(ServerMessages.FilaDeSubasta f, int valor, string yo, int plata) =>
        f.PrecioFinal > 0 && f.Cantidad == 1 && f.PrecioFinal <= plata && f.PrecioFinal <= Matematicas.Porcentaje(valor, PorcentajeQuePaga)
        && !string.Equals(f.Vendedor, yo, StringComparison.OrdinalIgnoreCase) && !EsUnBot(f.Vendedor);

    private static bool EsUnBot(string nombre) =>
        PobladoDeBots.Nombres.Concat(PobladoDeBots.Suplentes).Contains(nombre, StringComparer.OrdinalIgnoreCase);

    /// <summary>¿Hay Subastador en ese mapa? Por lo que nace ahí (la ficha con <c>NpcType=16</c>). El punto es un struct: el
    /// <c>FirstOrDefault</c> a secas devolvía uno vacío y todo mapa tenía Subastador.</summary>
    private MapNpcSpawn? SubastadorDe(int mapa) =>
        _entorno.Datos.MapSpawns.TryGetValue(mapa, out var nacen)
            ? nacen.Where(s => EsSubastador((short)s.NpcIndex)).Select(s => (MapNpcSpawn?)s).FirstOrDefault() : null;

    private bool EsSubastador(short ficha) => _entorno.Datos.Npc(ficha)?.NpcType == 16;

    /// <summary>Las suyas en la última lista que vio.</summary>
    private int SusSubastas(SentidosDelBot sentidos) =>
        sentidos.Subastas.Count(f => string.Equals(f.Vendedor, Nombre, StringComparison.OrdinalIgnoreCase));

    /// <summary>Las suyas, si la lista es de hace poco; vieja, no cuenta (pueden haber vencido o haberse vendido): se vuelve a mirar.
    /// Sin esto, tres publicadas eran el tope de la sesión entera.</summary>
    private int SusSubastasConocidas(SentidosDelBot sentidos) =>
        Environment.TickCount64 - _listaVistaEn < MirarLaSubastaMs ? SusSubastas(sentidos) : 0;

    /// <summary>¿Lo guarda para la subasta en vez de vendérselo al mercader? Si se subasta, no venció ya una vez, hay Subastador en el
    /// pueblo, no le falló hace poco y no tiene ya todas las que se permite.</summary>
    private bool GuardaParaLaSubasta(SentidosDelBot sentidos, short obj) =>
        VaALaSubasta(_entorno.Datos.Obj(obj)) && !_noSeVendio.Contains(obj) && Environment.TickCount64 >= _sinSubastaHasta
        && SubastadorDe(sentidos.Mapa) is not null && SusSubastasConocidas(sentidos) < SubastasPropias;

    /// <summary>Lo que puede pagar en la subasta: lo de encima (el Centro no cobra de la bóveda), sin tocar lo de las compras.</summary>
    private int PlataParaLaSubasta(SentidosDelBot sentidos) => Math.Min(sentidos.Oro, PlataParaElEquipo(sentidos));

    /// <summary>¿Eso lo mejora? El equipo, como en la vitrina (<see cref="MejoraLoQueTiene"/>); el pergamino, si le sirve.</summary>
    private bool LoMejora(SentidosDelBot sentidos, short obj) =>
        !_porRetirar.Contains(obj) && (MejoraLoQueTiene(sentidos, obj) || (sentidos.UsaMana && LeSirveElPergamino(sentidos, obj)));

    /// <summary>
    /// AL SALIR DEL MOSTRADOR, EL SUBASTADOR: si guardó algo para publicar, o si toca mirar qué hay. Camina hasta él, lo clickea (no
    /// justo debajo: ver <see cref="PonerElHogarAsync"/>), pide la lista, compra lo que le conviene y publica de a una. Y si compró,
    /// al banquero a retirarlo. Devuelve si publicó o compró algo.
    /// </summary>
    private async Task<bool> PasarPorLaSubastaAsync(TransporteDeBot transporte, SentidosDelBot sentidos, Task cerrada, CancellationToken ct)
    {
        AnotarLoQueDejoLaSubasta(sentidos);
        bool hizoAlgo = await IrAlSubastadorAsync(transporte, sentidos, cerrada, ct);
        await RetirarLoDeLaSubastaAsync(transporte, sentidos, cerrada, ct);
        return hizoAlgo;
    }

    /// <summary>
    /// LO QUE EL CENTRO LE DEJÓ EN LA BÓVEDA, por el aviso (que dice el nombre): lo comprado ya está para retirar desde la compra; lo
    /// demás es suyo que venció sin ofertas, que se retira y no se vuelve a publicar. El nombre se resuelve primero entre lo que
    /// publicó o compró, porque hay objetos distintos con el mismo nombre (los gorros de mago); entre los de afuera, sólo si es uno.
    /// </summary>
    private void AnotarLoQueDejoLaSubasta(SentidosDelBot sentidos)
    {
        foreach (string nombre in sentidos.TomarLoQueDejoLaSubasta())
        {
            var conEseNombre = _entorno.Datos.Objects.Values
                .Where(o => VaALaSubasta(o) && string.Equals(o.Name, nombre, StringComparison.OrdinalIgnoreCase)).Select(o => o.Index).ToList();
            if (conEseNombre.FirstOrDefault(_comprados.Contains) is var comprado and > 0) { _comprados.Remove(comprado); continue; }
            short suyo = conEseNombre.FirstOrDefault(_publicadas.Contains);
            if (suyo == 0 && conEseNombre.Count == 1) suyo = conEseNombre[0];
            if (suyo == 0) continue;
            _publicadas.Remove(suyo);
            _porRetirar.Add(suyo);
            _noSeVendio.Add(suyo);
        }
    }

    private async Task<bool> IrAlSubastadorAsync(TransporteDeBot transporte, SentidosDelBot sentidos, Task cerrada, CancellationToken ct)
    {
        bool vende = sentidos.EnLaMochila(obj => EsBotin(sentidos, obj) && GuardaParaLaSubasta(sentidos, obj)) is not null;
        bool mira = Environment.TickCount64 >= _proximaMiradaALaSubasta && PlataParaLaSubasta(sentidos) >= PlataParaMirarLaSubasta;
        if ((!vende && !mira) || SubastadorDe(sentidos.Mapa) is not { } nace) return false;
        if (mira) _proximaMiradaALaSubasta = Environment.TickCount64 + MirarLaSubastaMs;
        bool publico = false, compro = false;
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
            if (!await EsperarLaListaAsync(sentidos, vistas, ct)) return false;   // no atendió: lejos, o no era el Subastador
            _listaVistaEn = Environment.TickCount64;

            if (mira) compro = await ComprarEnLaSubastaAsync(transporte, sentidos, ct);
            if (vende) publico = await PublicarEnLaSubastaAsync(transporte, sentidos, ct);
        }
        finally
        {
            // SIN PUBLICAR NADA, UN RATO VENDE COMO SIEMPRE: si no, el equipo viejo quedaría en la mochila para siempre.
            if (vende && !publico) _sinSubastaHasta = Environment.TickCount64 + SinSubastaMs;
        }
        return publico || compro;
    }

    private static async Task<bool> EsperarLaListaAsync(SentidosDelBot sentidos, int vistas, CancellationToken ct)
    {
        for (int espera = 0; espera < 12 && sentidos.VecesQueVioLasSubastas == vistas; espera++) await Task.Delay(250, ct);
        return sentidos.VecesQueVioLasSubastas != vistas;
    }

    /// <summary>La más barata de las que le convienen y lo mejoran, con compra directa. Una por visita.</summary>
    private async Task<bool> ComprarEnLaSubastaAsync(TransporteDeBot transporte, SentidosDelBot sentidos, CancellationToken ct)
    {
        int plata = PlataParaLaSubasta(sentidos);
        var elegida = sentidos.Subastas
            .Where(f => _entorno.Datos.Obj(f.Objeto) is { } o && LeConvieneLaSubasta(f, o.Value, Nombre, plata) && LoMejora(sentidos, f.Objeto))
            .OrderBy(f => f.PrecioFinal).Select(f => (ServerMessages.FilaDeSubasta?)f).FirstOrDefault();
        if (elegida is not { } f) return false;
        int antes = sentidos.Oro;
        transporte.Mandar(Paquete(ClientPacketId.SubastaComprar, w => w.WriteLong(f.Id)));
        for (int espera = 0; espera < 12 && sentidos.Oro == antes; espera++) await Task.Delay(250, ct);
        if (sentidos.Oro != antes - f.PrecioFinal) return false;   // se la ganaron, o venció
        _porRetirar.Add(f.Objeto);
        _comprados.Add(f.Objeto);
        Console.WriteLine($"[bot {Nombre}] compra en la subasta {f.Id}: obj {f.Objeto} de {f.Vendedor} por {f.PrecioFinal}");
        await Task.Delay(_rng.Next(800, 2_000), ct);
        return true;
    }

    /// <summary>Publica de a una lo que guardó, hasta <see cref="SubastasPropias"/>, con la lista ya a la vista.</summary>
    private async Task<bool> PublicarEnLaSubastaAsync(TransporteDeBot transporte, SentidosDelBot sentidos, CancellationToken ct)
    {
        bool publico = false;
        for (int vez = 0; vez < SubastasPropias && SusSubastas(sentidos) < SubastasPropias; vez++)
        {
            if (sentidos.EnLaMochila(obj => EsBotin(sentidos, obj) && GuardaParaLaSubasta(sentidos, obj)) is not { } item) break;
            short obj = sentidos.QueHayEn(item.Ranura);
            var (inicial, final) = PrecioDeSubasta(_entorno.Datos.Obj(obj)!.Value * item.Cantidad);
            int antes = sentidos.CuantoTiene(o => o == obj);
            int vistas = sentidos.VecesQueVioLasSubastas;
            transporte.Mandar(Paquete(ClientPacketId.SubastaPublicar, w => w.WriteByte(item.Ranura).WriteInteger(item.Cantidad)
                .WriteLong(inicial).WriteLong(final).WriteByte(HorasDeSubasta)));
            await EsperarLaListaAsync(sentidos, vistas, ct);
            if (sentidos.CuantoTiene(o => o == obj) >= antes) break;   // no se la tomó (el depósito, el Centro lleno)
            publico = true;
            _publicadas.Add(obj);
            Console.WriteLine($"[bot {Nombre}] subasta: {item.Cantidad} x obj {obj}, desde {inicial} (final {final})");
            await Task.Delay(_rng.Next(800, 2_000), ct);
        }
        return publico;
    }

    /// <summary>
    /// LO QUE DEJÓ EL CENTRO, DE LA BÓVEDA A LA MOCHILA: el banquero del pueblo, la bóveda de ítems (<c>BankStart</c>), retirar y cerrar. Lo que
    /// no está en la bóveda o no entra en la mochila se da por perdido para esta vida: no vuelve a intentarlo en cada visita.
    /// </summary>
    private async Task RetirarLoDeLaSubastaAsync(TransporteDeBot transporte, SentidosDelBot sentidos, Task cerrada, CancellationToken ct)
    {
        if (_porRetirar.Count == 0 || !TieneBanquero(sentidos.Mapa)) return;
        if (!await AbrirElBancoOAnotarloAsync(transporte, sentidos, cerrada, ct)) return;
        int vistas = sentidos.VecesQueVioLaBoveda;
        transporte.Mandar(Paquete(ClientPacketId.BankStart, _ => { }));
        for (int espera = 0; espera < 12 && sentidos.VecesQueVioLaBoveda == vistas; espera++) await Task.Delay(250, ct);
        if (sentidos.VecesQueVioLaBoveda != vistas)
            foreach (short obj in _porRetirar.ToList())
            {
                _porRetirar.Remove(obj);
                if (sentidos.EnLaBoveda(obj) is not { } b) continue;
                int antes = sentidos.CuantoTiene(o => o == obj);
                transporte.Mandar(Paquete(ClientPacketId.BankExtractItem, w => w.WriteByte(b.Ranura).WriteInteger(1)));
                for (int espera = 0; espera < 12 && sentidos.CuantoTiene(o => o == obj) == antes; espera++) await Task.Delay(250, ct);
                Console.WriteLine($"[bot {Nombre}] retira de la bóveda obj {obj}" + (sentidos.CuantoTiene(o => o == obj) > antes ? "" : ": no entró"));
                await Task.Delay(_rng.Next(400, 900), ct);
            }
        transporte.Mandar(Paquete(ClientPacketId.BankEnd, _ => { }));
        await Task.Delay(_rng.Next(400, 900), ct);
    }
}
