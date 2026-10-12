namespace CoverAO.Server.Game;

/// <summary>Una subasta publicada. <see cref="Oferta"/> = 0 y <see cref="Comprador"/> = null mientras nadie ofertó;
/// <see cref="PrecioFinal"/> = 0 si no se puede comprar directo. <see cref="Termina"/> en segundos Unix (UTC): el reloj
/// de pared, porque la subasta sigue corriendo con el server apagado.</summary>
public sealed record Subasta(int Id, string Vendedor, short Objeto, short Cantidad, long PrecioInicial, long PrecioFinal,
    long Oferta, string? Comprador, long Termina)
{
    /// <summary>Lo que muestra la columna "Precio (Actual)": la oferta más alta, o el inicial si no hay.</summary>
    public long PrecioActual => Comprador is null ? PrecioInicial : Oferta;
}

/// <summary>Lo que el Centro le debe a un personaje: un objeto, oro, o las dos cosas. Va a su bóveda; mientras no se
/// pueda (desconectado, o la bóveda sin lugar para el objeto) queda esperando.</summary>
public sealed record EntregaDeSubasta(string Personaje, short Objeto, short Cantidad, long Oro);

/// <summary>Cómo terminó un pedido al Centro. Los que tienen texto del original van con su LocaleMsg (ver el dispatcher).</summary>
public enum ResultadoDeSubasta
{
    Ok,
    NoExiste,
    YaTermino,          // LocaleMsg 319
    EsTuya,             // LocaleMsg 318
    PrecioInvalido,
    OfertaBaja,
    SinCompraDirecta,
    NoEsTuya,
    TieneOfertas,
    DuracionInvalida,
    DemasiadasSubastas,
    CentroLleno,
}

/// <summary>
/// EL CENTRO DE SUBASTAS (pedido del tester, 06/10). En el CoverAO de 2021 sólo quedaron restos —el Subastador
/// (NPCs.dat, ficha 164, <c>NpcType=16</c>), los textos de la ventana y cuatro mensajes—, así que las reglas son de la
/// casa, decididas con el autor el 06/10:
/// <list type="bullet">
/// <item>Uno solo para todo el mundo: cualquier Subastador muestra la misma lista.</item>
/// <item>Duraciones de 3, 6, 12, 24 y 36 horas. Depósito al publicar: 10 monedas por hora más el 1 % del precio más alto
/// (el final si hay, si no el inicial); no se devuelve.</item>
/// <item>Ofertar retiene el oro de la oferta. Al que lo superan se le devuelve. Comprar directo paga el precio final y
/// cierra la subasta; una oferta que llega al final también.</item>
/// <item>Todo lo que se entrega —el objeto al comprador, el oro al vendedor, el objeto de vuelta si nadie compró, la
/// oferta superada— va a la BÓVEDA, con el personaje conectado o no (<see cref="Pendientes"/>).</item>
/// </list>
/// Esta clase sólo lleva las cuentas: sacar el objeto de la mochila, cobrar y poner en la bóveda lo hace el dispatcher.
/// </summary>
public sealed class CentroDeSubastas
{
    /// <summary>Las duraciones que se pueden elegir, en horas.</summary>
    public static readonly IReadOnlyList<int> Duraciones = new[] { 3, 6, 12, 24, 36 };

    /// <summary>Cuántas subastas abiertas puede tener un mismo personaje.</summary>
    public const int MaximoPorVendedor = 10;

    /// <summary>Las subastas abiertas en todo el centro, como mucho. Sin tope global, con varias cuentas la lista crecía sin
    /// fin, y se recorre entera cada pocos segundos y se manda entera a cada uno que la pide (auditoría del 12/10).</summary>
    public const int MaximoEnElCentro = 2000;

    /// <summary>El tope de cualquier precio: el oro que puede llevar un personaje.</summary>
    public const long PrecioMaximo = 90_000_000;

    private readonly List<Subasta> _subastas = new();
    private readonly List<EntregaDeSubasta> _pendientes = new();
    private int _ultimoId;

    /// <summary>Cambia con cada modificación: el volcado diferido compara contra lo último que guardó.</summary>
    public long Cambios { get; private set; }

    public IReadOnlyList<Subasta> Abiertas => _subastas;
    public IReadOnlyList<EntregaDeSubasta> Pendientes => _pendientes;

    /// <summary>Lo que se cobra al publicar. El 1 % redondea al par, como todo porcentaje del juego (el cliente hace la misma
    /// cuenta para mostrarlo antes de publicar).</summary>
    public static long Deposito(int horas, long precioInicial, long precioFinal) =>
        10L * horas + Matematicas.Porcentaje(Math.Max(precioInicial, precioFinal), 1);

    /// <summary>Lo guardado, al arrancar.</summary>
    public void Cargar(IEnumerable<Subasta> subastas, IEnumerable<EntregaDeSubasta> pendientes)
    {
        _subastas.Clear(); _subastas.AddRange(subastas.OrderBy(s => s.Id));
        _pendientes.Clear(); _pendientes.AddRange(pendientes);
        _ultimoId = _subastas.Count == 0 ? 0 : _subastas.Max(s => s.Id);
    }

    public Subasta? Buscar(int id) => _subastas.FirstOrDefault(s => s.Id == id);

    /// <summary>Lo que se valida ANTES de sacarle el objeto y el depósito al vendedor.</summary>
    public ResultadoDeSubasta PuedePublicar(string vendedor, long precioInicial, long precioFinal, int horas)
    {
        if (!Duraciones.Contains(horas)) return ResultadoDeSubasta.DuracionInvalida;
        if (precioInicial < 1 || precioInicial > PrecioMaximo || precioFinal < 0 || precioFinal > PrecioMaximo
            || (precioFinal > 0 && precioFinal <= precioInicial))
            return ResultadoDeSubasta.PrecioInvalido;
        if (_subastas.Count(s => Mismo(s.Vendedor, vendedor)) >= MaximoPorVendedor) return ResultadoDeSubasta.DemasiadasSubastas;
        if (_subastas.Count >= MaximoEnElCentro) return ResultadoDeSubasta.CentroLleno;
        return ResultadoDeSubasta.Ok;
    }

    /// <summary>Publica. El que llama ya validó con <see cref="PuedePublicar"/> y ya sacó el objeto y el depósito.</summary>
    public Subasta Publicar(string vendedor, short objeto, short cantidad, long precioInicial, long precioFinal, int horas, long ahora)
    {
        var s = new Subasta(++_ultimoId, vendedor, objeto, cantidad, precioInicial, precioFinal, 0, null, ahora + horas * 3600L);
        _subastas.Add(s);
        Cambios++;
        return s;
    }

    /// <summary>Lo que se valida ANTES de cobrarle la oferta al que oferta.</summary>
    public ResultadoDeSubasta PuedeOfertar(int id, string quien, long monto, long ahora)
    {
        var s = Buscar(id);
        if (s is null) return ResultadoDeSubasta.NoExiste;
        if (s.Termina <= ahora) return ResultadoDeSubasta.YaTermino;
        if (Mismo(s.Vendedor, quien)) return ResultadoDeSubasta.EsTuya;
        if (monto > PrecioMaximo) return ResultadoDeSubasta.PrecioInvalido;
        // Sin ofertas alcanza con el inicial; con ofertas hay que superar la que hay. Llegar al final no hace falta
        // superarlo: con el final se compra.
        long minimo = s.Comprador is null ? s.PrecioInicial : s.Oferta + 1;
        if (s.PrecioFinal > 0) minimo = Math.Min(minimo, s.PrecioFinal);
        if (monto < minimo) return ResultadoDeSubasta.OfertaBaja;
        return ResultadoDeSubasta.Ok;
    }

    /// <summary>Lo que cuesta de verdad una oferta de <paramref name="monto"/>: si llega al precio final, el final.</summary>
    public long CostoDeLaOferta(int id, long monto) =>
        Buscar(id) is { PrecioFinal: > 0 } s && monto >= s.PrecioFinal ? s.PrecioFinal : monto;

    /// <summary>Oferta (ya validada con <see cref="PuedeOfertar"/> y ya cobrada por <see cref="CostoDeLaOferta"/>). Al
    /// superado se le devuelve lo suyo; si la oferta llega al precio final, la subasta se cierra ahí. Devuelve true si se
    /// cerró.</summary>
    public bool Ofertar(int id, string quien, long monto)
    {
        var s = Buscar(id)!;
        long costo = CostoDeLaOferta(id, monto);
        if (s.Comprador is not null) Deber(s.Comprador, 0, 0, s.Oferta);
        var nueva = s with { Oferta = costo, Comprador = quien };
        Cambios++;
        if (s.PrecioFinal > 0 && costo >= s.PrecioFinal) { Cerrar(nueva); return true; }
        _subastas[_subastas.IndexOf(s)] = nueva;
        return false;
    }

    /// <summary>Lo que se valida ANTES de cobrar la compra directa (que cuesta <see cref="Subasta.PrecioFinal"/>).</summary>
    public ResultadoDeSubasta PuedeComprar(int id, string quien, long ahora)
    {
        var s = Buscar(id);
        if (s is null) return ResultadoDeSubasta.NoExiste;
        if (s.Termina <= ahora) return ResultadoDeSubasta.YaTermino;
        if (Mismo(s.Vendedor, quien)) return ResultadoDeSubasta.EsTuya;
        if (s.PrecioFinal <= 0) return ResultadoDeSubasta.SinCompraDirecta;
        return ResultadoDeSubasta.Ok;
    }

    /// <summary>La compra directa, ya cobrada: es una oferta por el precio final.</summary>
    public void Comprar(int id, string quien) => Ofertar(id, quien, Buscar(id)!.PrecioFinal);

    /// <summary>El vendedor la retira, sólo si nadie ofertó todavía. El objeto vuelve a su bóveda; el depósito no.</summary>
    public ResultadoDeSubasta Cancelar(int id, string quien)
    {
        var s = Buscar(id);
        if (s is null) return ResultadoDeSubasta.NoExiste;
        if (!Mismo(s.Vendedor, quien)) return ResultadoDeSubasta.NoEsTuya;
        if (s.Comprador is not null) return ResultadoDeSubasta.TieneOfertas;
        _subastas.Remove(s);
        Deber(s.Vendedor, s.Objeto, s.Cantidad, 0);
        Cambios++;
        return ResultadoDeSubasta.Ok;
    }

    /// <summary>Cierra las que vencieron. Devuelve cuántas.</summary>
    public int Vencer(long ahora)
    {
        var vencidas = _subastas.Where(s => s.Termina <= ahora).ToList();
        foreach (var s in vencidas) Cerrar(s);
        if (vencidas.Count > 0) Cambios++;
        return vencidas.Count;
    }

    /// <summary>Saca de <see cref="Pendientes"/> lo de <paramref name="personaje"/> que <paramref name="entregar"/> pudo
    /// entregar (devuelve false si no pudo, por ejemplo con la bóveda llena: queda para la próxima).</summary>
    public int Entregar(string personaje, Func<EntregaDeSubasta, bool> entregar)
    {
        int hechas = 0;
        for (int i = 0; i < _pendientes.Count; i++)
        {
            var e = _pendientes[i];
            if (!Mismo(e.Personaje, personaje) || !entregar(e)) continue;
            _pendientes.RemoveAt(i--);
            hechas++;
        }
        if (hechas > 0) Cambios++;
        return hechas;
    }

    private void Cerrar(Subasta s)
    {
        int i = _subastas.FindIndex(x => x.Id == s.Id);
        if (i >= 0) _subastas.RemoveAt(i);
        if (s.Comprador is null) { Deber(s.Vendedor, s.Objeto, s.Cantidad, 0); return; }
        Deber(s.Comprador, s.Objeto, s.Cantidad, 0);
        Deber(s.Vendedor, 0, 0, s.Oferta);
    }

    private void Deber(string personaje, short objeto, short cantidad, long oro)
    {
        if (objeto == 0 && oro <= 0) return;
        _pendientes.Add(new EntregaDeSubasta(personaje, objeto, cantidad, oro));
    }

    private static bool Mismo(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
