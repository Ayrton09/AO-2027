using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;

namespace CoverAO.Server.Net;

/// <summary>
/// LAS FLECHAS DEL CAZADOR, DEL CARPINTERO (parte D del pedido del tester, 20/11: "Carpintero (para hacer los arcos y las flechas)";
/// el autor eligió hacerla el 22/11). Las tiendas venden sólo la Flecha Simple (el Carpintero NPC, el 44, en los mapas 1, 20 y otros
/// diez), que pega 1 a 2 y cuesta 40; las buenas las fabrica el Carpintero (<c>ObjCarpintero</c>: la Flecha pega 4 a 6 y pide una leña
/// y Carpintería 20, la +3 pega 10 a 12 y pide cuatro leñas y 50). Gonzo, el Cazador, nace con 1500 y cuando se le terminan pelea
/// con la Spatha: no compraba ni arco ni flechas.
///
/// <list type="bullet">
/// <item>EL CARPINTERO las fabrica para vender cuando tiene menos de <see cref="FlechasQueTieneParaVender"/>: la mejor que sabe, de a
/// <see cref="FlechasPorPaquete"/> por paquete (el servidor cobra la energía por paquete), y las publica en lotes de
/// <see cref="FlechasPorLote"/>. Valen 0 en el obj.dat, así que el precio sale de la leña (<see cref="ValorDeMercado"/>).</item>
/// <item>EL CAZADOR compra un lote en la subasta cuando tiene menos de <see cref="FlechasQueQuiereElCazador"/>, a lo sumo
/// <see cref="PorcentajeQuePaga"/> del valor, y un arco en la vitrina si no tiene ninguno (<see cref="LeFaltaElArco"/>). Con el arco
/// y menos de <see cref="FlechasParaComprarEnLaTienda"/>, las de la tienda, para no quedarse sin tirar.</item>
/// </list>
/// </summary>
public sealed partial class Bot
{
    public const int FlechasQueTieneParaVender = 400, FlechasPorPaquete = 50, FlechasPorLote = 200, FlechasQueQuiereElCazador = 300,
                     FlechasParaComprarEnLaTienda = 50;

    /// <summary>¿Es una flecha que fabrica el Carpintero?</summary>
    public static bool EsFlechaDeCarpintero(GameData datos, short obj) => datos.Obj(obj) is { IsAmmo: true } && datos.CarpenterItems.Contains(obj);

    /// <summary>El valor de una flecha por la leña que lleva, más la mitad, como la herrería; 0 si no es una.</summary>
    public static int ValorDeLaFlecha(GameData datos, short obj)
    {
        if (!EsFlechaDeCarpintero(datos, obj) || datos.Obj(obj) is not { } o) return 0;
        long costo = Oficios.Carpinteria.Materiales(o).Sum(m => (long)Math.Max(0, m.PorUnidad) * Math.Max(0, datos.Obj(m.Obj)?.Value ?? 0));
        return (int)Math.Min(int.MaxValue, costo + costo / 2);
    }

    private int FlechasQueTiene(SentidosDelBot s) => s.Suma(obj => _entorno.Datos.Obj(obj) is { IsAmmo: true } ? 1 : 0);

    /// <summary>¿El Carpintero fabrica flechas para vender? Con menos de <see cref="FlechasQueTieneParaVender"/> encima.</summary>
    private bool CarpinteroHaceFlechas(SentidosDelBot s) => _clase == ClaseCarpintero && FlechasQueTiene(s) < FlechasQueTieneParaVender;

    /// <summary>La mejor flecha que sabe hacer y para la que tiene leña, o null.</summary>
    private short? FlechaParaFabricar(Oficios.Fabricacion regla, int skill, Func<int, int> tiene) =>
        _entorno.Datos.CarpenterItems.Select(i => _entorno.Datos.Obj(i))
            .Where(o => o is { IsAmmo: true } && regla.Pide(o) <= skill && CuantasAlcanzan(_entorno.Datos, o.Index, regla, tiene) > 0)
            .OrderByDescending(o => regla.Pide(o!)).Select(o => (short?)o!.Index).FirstOrDefault();

    /// <summary>Cuántas pide en un paquete: de a una para aprender, y las flechas para vender de a <see cref="FlechasPorPaquete"/>.</summary>
    private short CantidadPorPaquete(SentidosDelBot s, short receta, Oficios.Fabricacion regla, Func<int, int> tiene) =>
        CarpinteroHaceFlechas(s) && EsFlechaDeCarpintero(_entorno.Datos, receta)
            ? (short)Math.Clamp(CuantasAlcanzan(_entorno.Datos, receta, regla, tiene), 1, FlechasPorPaquete) : (short)1;

    /// <summary>Lo que publica el Carpintero: sus flechas, de a lotes enteros.</summary>
    private bool EsFlechaParaVender(short obj) => _clase == ClaseCarpintero && EsFlechaDeCarpintero(_entorno.Datos, obj);

    /// <summary>¿Le falta el arco? El que tira con arco (<see cref="TiraConArco"/>) y no tiene ninguno que se pueda poner.</summary>
    private bool LeFaltaElArco(SentidosDelBot s) =>
        TiraConArco(_clase) && s.EnLaMochila(obj => _entorno.Datos.Obj(obj) is { IsBow: true } o && LoPuedeUsar(o, s.Nivel)) is null
        && !s.LlevaPuesto(obj => _entorno.Datos.Obj(obj) is { IsBow: true });

    /// <summary>¿Ese arco le sirve? Que lo pueda usar y pagar.</summary>
    private bool ArcoQueLeSirve(SentidosDelBot s, short obj, double plata) =>
        _entorno.Datos.Obj(obj) is { IsBow: true } o && LoPuedeUsar(o, s.Nivel) && o.Value > 0 && o.Value <= plata;

    private int FlechaPropia(short obj) => _entorno.Datos.Obj(obj) is { IsAmmo: true } o && LoPuedeUsar(o, (short)_miNivel) ? 1 : 0;

    /// <summary>En la vitrina, el arco que más pega de los que le sirven, y con el arco y casi sin flechas, las de la tienda. Devuelve
    /// si compró.</summary>
    private async Task<bool> ComprarElArcoAsync(TransporteDeBot transporte, SentidosDelBot sentidos, CancellationToken ct)
    {
        if (!sentidos.Comerciando || !TiraConArco(_clase)) return false;
        bool compro = false;
        if (LeFaltaElArco(sentidos))
        {
            double plata = PlataParaElEquipo(sentidos);
            if (sentidos.LoMejorDeLaVitrina(obj => ArcoQueLeSirve(sentidos, obj, plata) ? _entorno.Datos.Obj(obj)!.MaxHit : 0, plata)
                is not { } oferta) return false;
            int antes = sentidos.CuantoTiene(obj => obj == oferta.Obj);
            transporte.Mandar(Paquete(ClientPacketId.CommerceBuy, w => w.WriteByte(oferta.Ranura).WriteInteger(1)));
            await Task.Delay(_rng.Next(600, 1_400), ct);
            if (sentidos.CuantoTiene(obj => obj == oferta.Obj) <= antes) return false;
            compro = true;
        }
        // Y LAS FLECHAS EN LA MISMA VISITA: medido en la ronda 132 (22/11), Gonzo compró el Arco Compuesto y terminó las dos
        // corridas con la Spatha, sin una flecha.
        if (FlechasQueTiene(sentidos) < FlechasParaComprarEnLaTienda)
            compro |= await ReponerAsync(transporte, sentidos, FlechaPropia, FlechasQueQuiereElCazador, ct, ReservaPara(sentidos, comida: true, bebida: true));
        return compro;
    }

    /// <summary>
    /// ¿VUELVE A CASA CON LA RUNA POR EL ARCO, como por lo esencial, aunque esté en un mapa inseguro? El que tira con arco y no tiene
    /// ninguno: es su arma. Medido en las rondas 140 y 141 (23/11): Gonzo cazaba en el 191 (el Sendero del Este) y el único camino a
    /// Illiandor (el 194, con Carpintero) cruza el 193, donde nace algo que le queda grande; fracasó cuarenta veces y siguió con la Spatha.
    /// </summary>
    public static bool ElCazadorVuelveACasaPorElArco(bool tiraConArco, bool faltaElArco, bool vaPorElArco) =>
        tiraConArco && faltaElArco && vaPorElArco;

    /// <summary>¿Le sirve ese lote de flechas? Al que tira con arco y tiene pocas, de otro, y barato.</summary>
    public static bool LeConvienenLasFlechas(ServerMessages.FilaDeSubasta f, int valorUnitario, string yo, int plata, int tiene) =>
        tiene < FlechasQueQuiereElCazador && f.Cantidad > 0 && f.PrecioFinal > 0 && f.PrecioFinal <= plata && valorUnitario > 0
        && (long)f.PrecioFinal * 100 <= (long)valorUnitario * f.Cantidad * PorcentajeQuePaga
        && !string.Equals(f.Vendedor, yo, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// ¿EL CAZADOR VA A LA SUBASTA POR FLECHAS? Con el arco y menos de <see cref="FlechasQueQuiereElCazador"/>, aunque esté juntando
    /// para su meta o no busque mejoras: medido en la ronda 135 (22/11), Osmar publicó un lote de 200 en cada mundo y Gonzo, con el
    /// arco y una flecha o ninguna, no pasó nunca por el Subastador. Sólo lo miraba buscando mejoras de equipo.
    /// </summary>
    private bool CazadorBuscaFlechas(SentidosDelBot s) =>
        TiraConArco(_clase) && !LeFaltaElArco(s) && FlechasQueTiene(s) < FlechasQueQuiereElCazador && _acompana is null;

    private bool LeSirvenLasFlechas(SentidosDelBot s, ServerMessages.FilaDeSubasta f, int plata) =>
        TiraConArco(_clase) && EsFlechaDeCarpintero(_entorno.Datos, f.Objeto) && !_porRetirar.Contains(f.Objeto)
        && LeConvienenLasFlechas(f, ValorDeLaFlecha(_entorno.Datos, f.Objeto), Nombre, plata, FlechasQueTiene(s));
}
