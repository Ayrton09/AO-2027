using CoverAO.Protocol;
using CoverAO.Server.Game;

namespace CoverAO.Server.Net;

public sealed partial class Bot
{
    private long _proximoMandado;
    private bool _pagaPrimeroProvisiones;

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
            if (_entorno.Mapas.MapaMasCercano(sentidos.Mapa, sentidos.Posicion, MapaConMandado,
                    PasaSinPeligro(sentidos, NingunMapa)) is not { } pueblo) return false;
            if (!await IrHastaAsync(transporte, sentidos, pueblo.Mapa, pueblo.X, pueblo.Y, cerrada, ct)) return false;
        }

        int antes = sentidos.MandadosRecibidos;
        if (!await AbrirElBancoAsync(transporte, sentidos, cerrada, ct) || sentidos.MandadosRecibidos == antes) return false;

        short mapaDelEncargo = sentidos.Mapa;
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
