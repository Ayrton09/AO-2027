using CoverAO.Protocol;
using CoverAO.Server.Game;

namespace CoverAO.Server.Net;

public sealed partial class Bot
{
    private long _proximoMandado;
    private bool _pagaPrimeroProvisiones;

    private long _proximoPrestamo;

    /// <summary>Cada cuánto vuelve a pedir: el servidor no le presta otro mientras deba, y el bot no lleva la cuenta.</summary>
    public const int EntrePedidosDePrestamoMs = 30 * 60_000, EntreIntentosDelPrestamoMs = 60_000;

    /// <summary>
    /// SIN NADA, AL BANQUERO A PEDIR PRESTADO (<see cref="PrestamoDelBanco"/>): va al banquero de un pueblo donde le venden y, si le
    /// presta, sale primero por la comida y la bebida. Medido en el server del tester (23/11): Elmo y Tulio, con 5 monedas y la bóveda
    /// vacía, iban y venían del banco a la taberna del Puerto de Rinkel sin poder comprar nada.
    /// </summary>
    private async Task<bool> PedirPrestamoAsync(TransporteDeBot transporte, SentidosDelBot sentidos, Task cerrada, CancellationToken ct)
    {
        if (Environment.TickCount64 < _proximoPrestamo || _acompana is not null || !sentidos.Vivo
            || !PrestamoDelBanco.LePresta(_entorno.Ini.PrestamoDelBanco, sentidos.Nivel, sentidos.Oro, Math.Max(0, sentidos.Banco), 0))
            return false;
        _proximoPrestamo = Environment.TickCount64 + EntrePedidosDePrestamoMs;
        bool MapaConBanquero(int mapa) => MandadoParaVestirse.EsPueblo(_entorno.Datos, mapa)
            && _entorno.Ini.MercaderLeVende(_entorno.Datos.MapBattleMode(mapa), _miBando) && !HayGuardiaQueLoAtaca(mapa)
            && _entorno.Datos.MapSpawns.TryGetValue(mapa, out var nacen) && nacen.Any(s => EsBanquero((short)s.NpcIndex));
        if (!MapaConBanquero(sentidos.Mapa))
        {
            // EL PUEBLO MISMO SE PISA AUNQUE NAZCAN GUARDIAS QUE PEGAN FUERTE, si no van por él (`MapaConBanquero`): medido en la ronda
            // 146 (23/11), desde el Puerto de Rinkel no encontraba Rinkel, al lado, y Elmo seguía en la taberna con 5 monedas.
            var pasa = PasaSinPeligro(sentidos, NingunMapa);
            bool Pisa(int m) => MapaConBanquero(m) || pasa(m);
            var pueblo = _entorno.Mapas.PuebloMasCercano(sentidos.Mapa, sentidos.Posicion, MapaConBanquero, id => EsBanquero((short)id), Pisa);
            // ENCERRADO POR UNA PUERTA, LA ABRE: ver `Navegante.PuebloMasCercanoAbriendo`.
            if (pueblo is null && await AbrirLaPuertaQueAsync(transporte, sentidos,
                    donde => _entorno.Mapas.PuebloMasCercanoAbriendo(sentidos.Mapa, sentidos.Posicion, donde, MapaConBanquero,
                                                                     id => EsBanquero((short)id), Pisa) is not null, cerrada, ct))
                pueblo = _entorno.Mapas.PuebloMasCercano(sentidos.Mapa, sentidos.Posicion, MapaConBanquero, id => EsBanquero((short)id), Pisa);
            if (pueblo is not { } destino)
            {
                // SIN BANQUERO AL QUE LLEGUE, A CASA CON LA RUNA, como el que vuelve a comprar (`VolverACasaAsync`). Medido en la
                // ronda 151 (23/11): Aldo, ciudadano de nivel 17 con 0 monedas, cazaba en el 839 y no encontraba banquero.
                if (await VolverACasaAsync(transporte, sentidos, id => EsBanquero((short)id), cerrada, ct))
                {
                    Console.WriteLine($"[bot {Nombre}] vuelve a casa a pedir prestado (queda en el {sentidos.Mapa})");
                    _proximoPrestamo = Environment.TickCount64 + EntreIntentosDelPrestamoMs;
                    return true;
                }
                Console.WriteLine($"[bot {Nombre}] no encuentra banquero desde el {sentidos.Mapa} para pedir prestado (bando {_miBando})");
                _proximoPrestamo = Environment.TickCount64 + EntreIntentosDelPrestamoMs;
                return false;
            }
            // SI EL VIAJE SE CORTA, VUELVE A PROBAR ENSEGUIDA: sin plata no tiene otra cosa que hacer.
            if (!await IrHastaAsync(transporte, sentidos, destino.Mapa, destino.X, destino.Y, cerrada, ct, soloALaMeta: true))
            {
                Console.WriteLine($"[bot {Nombre}] no llegó al banquero del {destino.Mapa} a pedir prestado (está en el {sentidos.Mapa})");
                _proximoPrestamo = Environment.TickCount64 + EntreIntentosDelPrestamoMs;
                return false;
            }
        }
        int antes = sentidos.Oro;
        if (!await AbrirElBancoAsync(transporte, sentidos, cerrada, ct))
        {
            Console.WriteLine($"[bot {Nombre}] no pudo abrir el banco del {sentidos.Mapa} a pedir prestado");
            _proximoPrestamo = Environment.TickCount64 + EntreIntentosDelPrestamoMs;
            return false;
        }
        for (int i = 0; i < 8 && sentidos.Oro <= antes && !cerrada.IsCompleted; i++) await Task.Delay(250, ct);
        if (sentidos.Oro <= antes)
        {
            // Debe el anterior, o el banquero le dio un mandado: el bot no ve la deuda.
            Console.WriteLine($"[bot {Nombre}] el banquero del {sentidos.Mapa} no le presta");
            return true;
        }
        Console.WriteLine($"[bot {Nombre}] pide prestado al banco: {sentidos.Oro - antes} monedas");
        _pagaPrimeroProvisiones = sentidos.ConHambre || sentidos.ConSed;
        _proximaCompra = 0;
        return true;
    }

    private async Task<bool> HacerElMandadoAsync(TransporteDeBot transporte, SentidosDelBot sentidos, Task cerrada, CancellationToken ct)
    {
        if (Environment.TickCount64 < _proximoMandado || _acompana is not null
            || !MandadoParaVestirse.Necesita(sentidos.Nivel, sentidos.EnergiaEnPuntos, sentidos.Oro, Math.Max(0, sentidos.Banco))
            || TieneRopaPropia(sentidos)) return false;

        _proximoMandado = Environment.TickCount64 + 90_000;
        bool EsSuSastre(int id) => MandadoParaVestirse.VendeRopaBasica(_entorno.Datos, _entorno.Datos.Npc(id),
            sentidos.Nivel, _clase, _raza, _genero);
        bool MapaConMandado(int mapa) => MandadoParaVestirse.EsPueblo(_entorno.Datos, mapa)
            && _entorno.Ini.MercaderLeVende(_entorno.Datos.MapBattleMode(mapa), _miBando)
            && _entorno.Datos.MapSpawns.TryGetValue(mapa, out var nacen)
            && nacen.Any(s => EsBanquero((short)s.NpcIndex)) && nacen.Any(s => EsSuSastre(s.NpcIndex));

        if (!MapaConMandado(sentidos.Mapa))
        {
            var pasa = PasaSinPeligro(sentidos, NingunMapa);
            if (_entorno.Mapas.PuebloMasCercano(sentidos.Mapa, sentidos.Posicion, MapaConMandado,
                    id => EsBanquero((short)id),
                    m => MapaConMandado(m) || pasa(m)) is not { } pueblo) return false;
            // La entrada identifica el lado del mapa que llega al banco. Aceptar una llegada a tres baldosas
            // hace entrar por (10,93) de Rinkel, aislada de (12,93), que sí da al pueblo.
            if (!await IrHastaAsync(transporte, sentidos, pueblo.Mapa, pueblo.X, pueblo.Y, cerrada, ct, soloALaMeta: true))
                return false;
        }

        int antes = sentidos.MandadosRecibidos;
        if (!await AbrirElBancoAsync(transporte, sentidos, cerrada, ct) || sentidos.MandadosRecibidos == antes) return false;

        // Entre el banquero y este punto pudo cambiar de mapa (una muerte, un portal): sin sastre suyo acá, no hay mandado.
        short mapaDelEncargo = sentidos.Mapa;
        if (!MapaConMandado(mapaDelEncargo)) return false;
        var sastre = _entorno.Datos.MapSpawns[mapaDelEncargo].First(n => EsSuSastre(n.NpcIndex));
        if (!await IrAbriendoAsync(transporte, sentidos, mapaDelEncargo, (byte)sastre.X, (byte)sastre.Y,
                cerrada, ct, RadioDelMostrador, EsSuSastre)
            || sentidos.MasCercano(f => EsSuSastre(f)) is not { Pasos: <= RadioDelMostrador } mostrador) return false;

        int oroAntes = sentidos.Oro;
        transporte.Mandar(Paquete(ClientPacketId.DoubleClick, w => w.WriteByte(mostrador.Quien.X).WriteByte(mostrador.Quien.Y)));
        for (int i = 0; i < 12 && !sentidos.Comerciando && !cerrada.IsCompleted; i++) await Task.Delay(250, ct);
        if (!sentidos.Comerciando) return false;

        bool cobro = sentidos.Oro >= oroAntes + MandadoParaVestirse.Paga;
        if (cobro)
        {
            _proximoMandado = Environment.TickCount64 + MandadoParaVestirse.EsperaEntrePagosSeg * 1_000;
            _pagaPrimeroProvisiones = sentidos.ConHambre || sentidos.ConSed;
            _ropaAlAlcanceHasta = 0;
            _proximaCompra = 0;
            await ComprarLaRopaDelCaminoAsync(transporte, sentidos, ct);
        }
        transporte.Mandar(Paquete(ClientPacketId.CommerceEnd, _ => { }));
        return cobro;
    }
}
