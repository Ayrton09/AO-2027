using CoverAO.Protocol;
using CoverAO.Server.Game;

namespace CoverAO.Server.Net;

/// <summary>
/// EL BANCO. Con la regla de la casa <c>OroQueSeCaeAlMorir</c> (el Server.ini que se distribuye la trae en 100000), el que muere en
/// un lugar inseguro con esa cantidad o menos encima pierde TODO el oro. Medido en las corridas del 01/10 al 06/10 (342): el que
/// muere así es el mid, que lleva encima 25 mil de mediana (el late lleva más de 100 mil y no pierde nada; el early muere en la
/// cuna, que es segura): 27 muertes, 20 mil de mediana cada una. Así que pasado el novato el bot hace lo que hace cualquier
/// jugador: al terminar las compras en un pueblo con banquero deja en la bóveda todo lo que no es de las compras
/// (<see cref="Bolsillo"/>), y cuando una compra necesita lo guardado, la va a hacer a un pueblo con banquero y retira antes del
/// mostrador.
///
/// <para>Las decisiones de compra miran TODA la plata (<see cref="PlataDe"/>); en el mostrador, sólo la de encima, que es la que
/// paga.</para>
/// </summary>
public sealed partial class Bot
{
    /// <summary>Con menos de esto de más encima no se va al banquero a depositar: el viaje por unas monedas no vale.</summary>
    private const int MinimoParaDepositar = 2_000;

    /// <summary>El tipo de NPC del banquero (VB6 <c>eNPCType.Banquero</c>).</summary>
    private const int NpcTypeBanquero = 4;

    /// <summary>A cuántas baldosas del banquero alcanza: el doble clic que abre el panel del oro atiende a tres (Manhattan), como
    /// el mercader.</summary>
    private const int RadioDelBanquero = 3;

    /// <summary>Los mapas donde no se pudo abrir el banco (el banquero detrás de una puerta, o sin camino): no se reintenta en cada
    /// viaje, y una compra que necesita lo guardado no va ahí.</summary>
    private readonly HashSet<int> _bancoSinCamino = new();

    /// <summary>Mientras compra en el mostrador, la plata es sólo la de encima.</summary>
    private bool _enElMostrador;

    /// <summary>La plata con la que decide: la de encima más la de la bóveda; en el mostrador, sólo la de encima.</summary>
    private int PlataDe(SentidosDelBot sentidos) => _enElMostrador ? sentidos.Oro : sentidos.Oro + LoQueSePuedeRetirar(sentidos);

    /// <summary>Lo de la bóveda con lo que se puede comprar: todo menos la reserva para vestirse (<see cref="LoQueRetira"/>); 0 si no lo
    /// sabe.</summary>
    private int LoQueSePuedeRetirar(SentidosDelBot sentidos) =>
        sentidos.Banco <= 0 ? 0
        : LoQueRetira(sentidos.Banco, LaRopaEsEsencial(sentidos.Nivel, TieneRopaPropia(sentidos)), ReservaParaVestirse(sentidos));

    /// <summary>La reserva para vestirse de este bot (<see cref="LoQueAlcanzaParaVestirse"/>); 0 si no hay ropa a la venta que le sirva.</summary>
    private int ReservaParaVestirse(SentidosDelBot sentidos)
    {
        int ropa = LaRopaMasBarata(sentidos);
        return ropa == int.MaxValue ? 0
            : LoQueAlcanzaParaVestirse(ropa, ReservaParaLoQueFalta(CostoPorPunto(esBebida: false), 0, ComidaMinima)
                                             + ReservaParaLoQueFalta(CostoPorPunto(esBebida: true), 0, BebidaMinima),
                                       CuestaLaHerramienta(sentidos));
    }

    /// <summary>
    /// LO QUE TIENE QUE QUEDAR EN LA BÓVEDA PARA VESTIRSE: la ropa más barata más lo que la compra del desnudo aparta antes de la ropa
    /// (<see cref="PlataQueSobra"/>, con la panza vacía: muerto en un lugar inseguro se le cae también la comida y la bebida), la
    /// herramienta si le falta y <see cref="MonedasDeApuro"/>. Con menos, la regla del desnudo no le da para la ropa y retira para comer.
    /// </summary>
    public static int LoQueAlcanzaParaVestirse(int laRopaMasBarata, int laPanzaDesdeCero, int laHerramienta) =>
        laRopaMasBarata + laPanzaDesdeCero + laHerramienta + MonedasDeApuro;

    /// <summary>
    /// LO DE LAS COMPRAS, lo que se lleva encima: el doble de lo que cuesta llenar de cero las pociones, la comida, la bebida y los
    /// antídotos (el precio de lista más barato del mundo; en caja y en el mostrador que le toque sale más), más la herramienta si le
    /// falta. Con eso el viaje de compras siguiente se paga sin pasar por el banco.
    /// </summary>
    private int Bolsillo(SentidosDelBot sentidos) =>
        LoDeLasCompras(ReservaParaLasPociones(0, CuestaLaPocion),
                       ReservaParaLoQueFalta(CostoPorPunto(esBebida: false), 0, ComidaQueSeLleva(sentidos.Nivel))
                       + ReservaParaLoQueFalta(CostoPorPunto(esBebida: true), 0, BebidaQueSeLleva(sentidos.Nivel)),
                       AntidotosQueQuiere * CuestaElAntidoto, CuestaLaHerramienta(sentidos));

    /// <inheritdoc cref="Bolsillo"/>
    public static int LoDeLasCompras(int pociones, int comidaYBebida, int antidotos, int herramienta) =>
        2 * (pociones + comidaYBebida + antidotos) + herramienta + MonedasDeApuro;

    /// <summary>
    /// ¿PASA POR EL BANQUERO A DEPOSITAR? Pasado el novato (el novato muere en la cuna, que es segura, y compra donde no hay banco), si
    /// le sobran por lo menos <see cref="MinimoParaDepositar"/> por encima de <paramref name="bolsillo"/> y lo de encima SE CAERÍA al
    /// morir: con la regla apagada (<paramref name="tope"/> en 0) o con más que el tope encima no se cae nada, y bajarlo al bolsillo
    /// lo expone. Medido en la ronda 42 (06/10) depositando siempre: Milo, Petra, Nadia y Aldo, que llevaban 150 a 240 mil a salvo,
    /// quedaban en 8878 y volvían a juntar decenas de miles expuestos; el oro en riesgo al final bajó sólo un 6 %.
    /// Y si no sabe lo que tiene guardado (<paramref name="banco"/> en -1), pasa igual a mirar: en una vida anterior pudo haber
    /// dejado plata, y sin saberlo se cree pobre.
    /// </summary>
    public static bool ValeDepositar(int nivel, int oro, int banco, int bolsillo, int tope) =>
        nivel > GameLimits.NewbieMaxLevel
        && (banco < 0 || (tope > 0 && oro <= tope && oro - bolsillo >= MinimoParaDepositar));

    /// <summary>
    /// ¿ESTA COMPRA NECESITA LO DE LA BÓVEDA? Si tiene algo guardado y lo de encima no alcanza para lo de las compras, o si va sólo
    /// por gustos (equipo, pergaminos) y lo guardado es más que lo que le sobra encima: el gusto se elige con toda la plata, y con
    /// la mayor parte en el banco no se paga con lo de encima. Entonces sólo sirve un pueblo con banquero, y ahí retira antes de ir
    /// al mostrador. Con "sólo por gustos" a secas, en la ronda 42 Elmo fue al banquero a retirar 2928 con 34656 encima.
    /// </summary>
    public static bool NecesitaElBanco(int banco, int oro, int bolsillo, bool soloGustos) =>
        banco > 0 && (oro < bolsillo || (soloGustos && banco > oro - bolsillo));

    /// <summary>
    /// ¿VA A MIRAR LO QUE TIENE EN LA BÓVEDA? Pasado el novato, sin plata encima para comprar nada (<see cref="MonedasDeApuro"/>),
    /// sin saber lo que guardó (<paramref name="banco"/> en -1: el servidor no lo manda al entrar) y con un banquero en el mapa.
    /// Sabiéndolo, la compra lo cuenta (<see cref="PlataDe"/>) y retira antes del mostrador (<see cref="NecesitaElBanco"/>).
    ///
    /// <para>Decisión del autor (17/10): el que revive sin nada, retira de la bóveda. Medido en `confm1` y `confm5` (16/10): Osmar,
    /// de nivel 22 y 23, murió en el 13 y revivió en Rinkel con 0 encima, sin pociones ni ropa, y pasó 1065 y 1052 segundos parado ahí, "nada
    /// que comprar", con 40.328 en la bóveda. En las corridas guardadas hasta la ronda 80, más de 300 de esas líneas son en Rinkel, que tiene dos
    /// banqueros.</para>
    /// </summary>
    public static bool MiraLaBoveda(int nivel, int oro, int banco, bool hayBanquero) =>
        nivel > GameLimits.NewbieMaxLevel && oro < MonedasDeApuro && banco < 0 && hayBanquero;

    private bool EsBanquero(short ficha) => _entorno.Datos.Npc(ficha) is { NpcType: NpcTypeBanquero };

    /// <summary>Dónde nace el banquero de ese mapa, o null si no tiene.</summary>
    private (byte X, byte Y)? BanqueroDelMapa(int mapa) =>
        _entorno.Datos.MapSpawns.TryGetValue(mapa, out var nacen)
        && nacen.FirstOrDefault(q => EsBanquero((short)q.NpcIndex)) is { NpcIndex: > 0 } b
            ? ((byte)b.X, (byte)b.Y) : null;

    private bool TieneBanquero(int mapa) => BanqueroDelMapa(mapa) is not null && !_bancoSinCamino.Contains(mapa);

    /// <summary>Va hasta el banquero del mapa y le abre el panel del oro (que dice lo que hay en la bóveda). False si no llegó o no
    /// atendió.</summary>
    private async Task<bool> AbrirElBancoAsync(TransporteDeBot transporte, SentidosDelBot sentidos, Task cerrada, CancellationToken ct)
    {
        if (BanqueroDelMapa(sentidos.Mapa) is not { } nace) return false;
        // Primero adonde nace (desde el mostrador casi nunca está a la vista); de cerca, adonde está. Y SI NO HAY CAMINO, LA
        // PUERTA, como con el mercader (`AbrirUnaPuertaAsync`): los dos banqueros de Rinkel están adentro del banco, y en la
        // ronda 42 Elmo salía "sin camino" desde el herrero.
        for (int intento = 0; intento < 2 && sentidos.MasCercano(EsBanquero) is not { Pasos: <= RadioDelBanquero }; intento++)
        {
            if (await IrHastaAsync(transporte, sentidos, sentidos.Mapa, nace.X, nace.Y, cerrada, ct, RadioDelBanquero)) break;
            if (!await AbrirUnaPuertaAsync(transporte, sentidos, ficha => EsBanquero((short)ficha), cerrada, ct)) break;
        }
        if (sentidos.MasCercano(EsBanquero) is not { } banquero) return false;
        if (banquero.Pasos > RadioDelBanquero
            && !await IrHastaAsync(transporte, sentidos, sentidos.Mapa, banquero.Quien.X, banquero.Quien.Y, cerrada, ct, RadioDelBanquero))
            return false;
        if (sentidos.Donde(banquero.Quien.CharIndex) is not { } donde) return false;
        int antes = sentidos.VecesQueVioElBanco;
        transporte.Mandar(Paquete(ClientPacketId.DoubleClick, w => w.WriteByte(donde.X).WriteByte(donde.Y)));
        for (int espera = 0; espera < 12 && sentidos.VecesQueVioElBanco == antes; espera++) await Task.Delay(250, ct);
        return sentidos.VecesQueVioElBanco != antes;
    }

    private async Task<bool> AbrirElBancoOAnotarloAsync(TransporteDeBot transporte, SentidosDelBot sentidos, Task cerrada, CancellationToken ct)
    {
        if (!sentidos.Vivo) return false;
        if (await AbrirElBancoAsync(transporte, sentidos, cerrada, ct)) return true;
        if (sentidos.Vivo) _bancoSinCamino.Add(sentidos.Mapa);
        Console.WriteLine($"[bot {Nombre}] no llega al banquero de m{sentidos.Mapa}");
        return false;
    }

    /// <summary>Mueve oro entre lo de encima y la bóveda (positivo deposita, negativo retira) con el panel ya abierto, y espera a
    /// que el oro de encima lo confirme.</summary>
    private async Task<bool> MoverAlBancoAsync(TransporteDeBot transporte, SentidosDelBot sentidos, int monto, CancellationToken ct)
    {
        if (monto == 0) return false;
        int antes = sentidos.Oro;
        transporte.Mandar(Paquete(monto > 0 ? ClientPacketId.BankDepositGold : ClientPacketId.BankExtractGold,
                                  w => w.WriteLong(Math.Abs(monto))));
        for (int espera = 0; espera < 12 && sentidos.Oro == antes; espera++) await Task.Delay(250, ct);
        if (sentidos.Oro != antes - monto) return false;
        sentidos.MovioAlBanco(monto);
        Console.WriteLine($"[bot {Nombre}] {(monto > 0 ? "deposita" : "retira")} {Math.Abs(monto)} en m{sentidos.Mapa} "
                          + $"(encima {sentidos.Oro}, banco {sentidos.Banco})");
        await Task.Delay(_rng.Next(400, 900), ct);
        return true;
    }

    /// <summary>Al terminar las compras en un pueblo con banquero: deja en la bóveda lo que no es de las compras.</summary>
    private async Task DepositarLoQueSobraAsync(TransporteDeBot transporte, SentidosDelBot sentidos, Task cerrada, CancellationToken ct)
    {
        int tope = _entorno.Ini.OroQueSeCaeAlMorir;
        if (!ValeDepositar(sentidos.Nivel, sentidos.Oro, sentidos.Banco, Bolsillo(sentidos), tope) || !TieneBanquero(sentidos.Mapa)) return;
        if (!await AbrirElBancoOAnotarloAsync(transporte, sentidos, cerrada, ct)) return;
        // Ya con el saldo a la vista (si era la visita para mirarlo): la misma cuenta, ahora sabiendo el banco.
        if (ValeDepositar(sentidos.Nivel, sentidos.Oro, Math.Max(0, sentidos.Banco), Bolsillo(sentidos), tope))
            await MoverAlBancoAsync(transporte, sentidos, sentidos.Oro - Bolsillo(sentidos), ct);
    }

    /// <summary>Antes del mostrador, cuando la compra necesita lo guardado: retira todo menos la reserva para vestirse
    /// (<see cref="LoQueRetira"/>); lo que sobre vuelve a la bóveda al terminar, con <see cref="DepositarLoQueSobraAsync"/>.</summary>
    private async Task RetirarParaComprarAsync(TransporteDeBot transporte, SentidosDelBot sentidos, Task cerrada, CancellationToken ct)
    {
        if (!await AbrirElBancoOAnotarloAsync(transporte, sentidos, cerrada, ct)) return;
        if (LoQueSePuedeRetirar(sentidos) is > 0 and var retira) await MoverAlBancoAsync(transporte, sentidos, -retira, ct);
    }

    /// <summary>
    /// CUÁNTO RETIRA PARA COMPRAR: lo que pasa de <paramref name="reserva"/> (<see cref="LoQueAlcanzaParaVestirse"/>), que queda en la
    /// bóveda para la próxima vez que muera en un lugar inseguro, desnudo o no. Y DESNUDO, si lo que pasa no le alcanza para vestirse
    /// (menos que otra reserva), todo: es para eso.
    /// Muerto ahí se le cae la ropa, y desnudo no recupera energía (`Survival.RecuperaEnergia`, VB6 <c>RecStamina</c>): sin energía no
    /// pega, así que sin plata para vestirse no puede ni cazar algo flojo para juntarla.
    ///
    /// <para>Decisión del autor (17/10): el que revive sin nada retira de la bóveda; si no tiene, caza algo flojo. Medido en `medio1`
    /// (17/10): Elmo, nivel 24, murió en el 13, revivió desnudo, retiró los 12.728 que tenía y los gastó en pociones, ropa, arma y
    /// comida; volvió a morir y pasó los últimos quince minutos desnudo en el 794, con 0 encima y 0 en la bóveda, entre Murciélagos a
    /// los que no podía pegar.</para>
    /// </summary>
    public static int LoQueRetira(int banco, bool desnudo, int reserva) =>
        banco <= 0 ? 0 : desnudo ? (banco - reserva >= reserva ? banco - reserva : banco) : Math.Max(0, banco - reserva);
}
