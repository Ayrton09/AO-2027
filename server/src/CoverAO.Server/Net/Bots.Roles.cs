using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;

namespace CoverAO.Server.Net;

public sealed partial class Bot
{
    private const int IntervaloDeFichaDeCompania = 3_000;
    private const int IntervaloDeAvisoDeCompania = 30_000;
    private const int DistanciaConArco = 3;
    private long _proximaFichaDeCompania;
    private long _proximoAvisoDeMana;
    private long _proximoAvisoDeRetirada;

    private void AvisarACompanero(TransporteDeBot transporte, string texto, ref long proximo)
    {
        long ahora = Environment.TickCount64;
        if (_acompana is not { } a || ahora < proximo) return;
        if (transporte.Mandar(Paquete(ClientPacketId.Whisper, w => w.WriteAsciiString(a.Quien).WriteAsciiString(texto))))
            proximo = ahora + IntervaloDeAvisoDeCompania;
    }

    private bool CurarACompanero(TransporteDeBot t, SentidosDelBot s, SentidosDelBot.Visible persona)
    {
        if (_clase != GameLimits.ClaseClerigo || persona.Muerto || !s.UsaMana || s.Salud < TomaPocion
            || !PacketDispatcher.PuedeAyudar(s.MiEstado, persona.Estado)
            || ElegirClicDeHechizo(s, persona.X, persona.Y) is not { } clic) return false;

        static bool EsCura(SpellInfo h) => h is { IsHeal: true, MaxHp: > 0, Target: 1 or 3 };
        RevisarElUltimoCasteo(s);
        int? costoDeCura = s.Hechizos().Select(x => _entorno.Datos.Spell(x.Hechizo))
            .Where(h => h is not null && EsCura(h) && !_noLoPuedeLanzar.Contains(h.Index))
            .Select(h => (int?)h!.ManaCost).Min();
        if (costoDeCura is not { } reserva) return false;
        if (s.VidaDeLaCompanera(persona.CharIndex, IntervaloDeFichaDeCompania) is not { } vida)
        {
            long ahora = Environment.TickCount64;
            if (ahora >= _proximaFichaDeCompania
                && t.Mandar(Paquete(ClientPacketId.LeftClick, w => w.WriteByte(persona.X).WriteByte(clic))))
                _proximaFichaDeCompania = ahora + IntervaloDeFichaDeCompania;
            return false;
        }
        if (vida is <= 0 or > 60) return false;
        if (ElMasBaratoQueSabe(s, EsCura, reserva) is not { } ranura)
        {
            if (s.ManaEnPuntos < reserva * 2)
                AvisarACompanero(t, "no me queda mana para curarte", ref _proximoAvisoDeMana);
            return false;
        }
        if (!LanzarSobre(t, s, ranura, persona.X, clic)) return false;
        // Una ficha vieja no autoriza gastar dos curas seguidas.
        s.OlvidarVidaDeLaCompanera();
        return true;
    }

    private async Task<bool> TomarDistanciaConArcoAsync(TransporteDeBot t, SentidosDelBot s, SentidosDelBot.Visible presa,
                                                       CancellationToken ct, Func<bool> cortaSi)
    {
        if (_pasoTrabado || _acompana is not { } a || s.PersonaLlamada(a.Quien) is not { } persona) return false;
        var desde = s.Posicion;
        if (Math.Abs(presa.X - desde.X) + Math.Abs(presa.Y - desde.Y) >= DistanciaConArco) return false;
        var evitar = s.BaldosasOcupadas().ToHashSet();
        var peligros = PeligrosDelViaje(s);
        for (byte rumbo = 1; rumbo <= 4; rumbo++)
        {
            var (dx, dy) = SentidosDelBot.Paso(rumbo);
            var vecino = (X: (byte)(desde.X + dx), Y: (byte)(desde.Y + dy));
            if (Math.Abs(persona.X - vecino.X) + Math.Abs(persona.Y - vecino.Y) > RadioDeAyuda
                || Math.Abs(presa.X - vecino.X) > AlcanceArcoX || Math.Abs(presa.Y - vecino.Y) > AlcanceArcoY
                || LoArrimaAlPeligro(desde, vecino, peligros)) evitar.Add(vecino);
        }
        var camino = _entorno.Mapas.LejosDe(s.Mapa, desde, (presa.X, presa.Y), 1, evitar);
        if (camino is null) return false;
        var paso = camino[0];
        await UnPasoHaciaAsync(t, s, paso.X - desde.X, paso.Y - desde.Y, ct, cortaSi: cortaSi);
        return s.Posicion != desde;
    }
}
