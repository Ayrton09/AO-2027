using CoverAO.Protocol;
using CoverAO.Server.Game;

namespace CoverAO.Server.Net;

/// <summary>
/// EL LADRÓN ROBA (pedido del tester, 20/11: "el ladrón busca robar entonces anda moviéndose por ahí sigilosamente... lo mejor que
/// puede robar es un ítem"). Los bots no robaban nunca. El servidor exige ser Renegado (<c>DoRobar</c>: "Para robar debes ser
/// renegado"), y Lira, el único Ladrón, era Republicana: decisión del autor (22/11), el Ladrón deja la ciudadanía y roba.
///
/// <para>Lo que hace un Ladrón: desde el nivel en que puede (<see cref="NivelParaHacerseRenegado"/>), renuncia a su facción
/// (<c>RetirarFaccion</c>, el <c>/RETIRAR</c>: de la Armada o la Milicia pasa a civil y de civil a Renegado). En un mapa inseguro,
/// con la vida y la energía para robar, se le arrima a una persona que le conviene (la regla de niveles del Clérigo,
/// <see cref="SeAnimaContra"/>) y prueba robarle <see cref="IntentosDeRobo"/> veces, a la cadencia del trabajo. Después
/// la deja <see cref="DejaALaRobadaMs"/>. El robo lo resuelve el servidor: oro, y la mitad de las veces un objeto; con la habilidad
/// baja sale uno de cada diecisiete intentos, y sube con el uso.</para>
/// </summary>
public sealed partial class Bot
{
    /// <summary>El servidor no deja renunciar antes (<c>HandleRetirarFaccion</c>: nivel 14 o menos, el aviso 425).</summary>
    public const int NivelParaHacerseRenegado = GameLimits.NewbieMaxLevel;

    /// <summary>Lo que cobra el servidor por intento (<c>DoRobar</c>), y un poco más para no quedarse sin con qué pelear.</summary>
    public const int EnergiaParaRobar = 15, EnergiaQueGuardaAlRobar = 30;

    public const int IntentosDeRobo = 4, DejaALaRobadaMs = 5 * 60_000;

    /// <summary>La distancia a la que el servidor deja robar (Manhattan, <c>HandleWorkLeftClick</c>).</summary>
    public const int DistanciaDeRobo = 2;

    private const int EntreRenunciasMs = 15_000;
    private long _proximaRenuncia;
    private readonly Dictionary<short, long> _robadaHasta = new();

    /// <summary>¿Renuncia a su facción? Un Ladrón vivo, del nivel en que se puede y que no es Renegado todavía.</summary>
    public static bool LadronRenuncia(byte clase, int nivel, byte bando) =>
        clase == GameLimits.ClaseLadron && nivel >= NivelParaHacerseRenegado
        && bando is Player.StatusCiudadano or Player.StatusRepublicano or Player.StatusArmada or Player.StatusMilicia;

    /// <summary>¿Sale a robar? Renegado, en un mapa inseguro, con la vida para pelear con gente y la energía.</summary>
    public static bool LadronSaleARobar(byte clase, byte bando, bool mapaInseguro, double salud, int energia) =>
        clase == GameLimits.ClaseLadron && bando == Player.StatusCriminal && mapaInseguro
        && salud >= SaludParaPelearConGente && energia >= EnergiaParaRobar + EnergiaQueGuardaAlRobar;

    /// <summary>Deja la ciudadanía, a lo sumo cada <see cref="EntreRenunciasMs"/> (de la Milicia son dos pasos). Devuelve si mandó.</summary>
    private bool HacerseRenegado(TransporteDeBot transporte, SentidosDelBot sentidos)
    {
        long ahora = Environment.TickCount64;
        if (!sentidos.Vivo || ahora < _proximaRenuncia || !LadronRenuncia(_clase, sentidos.Nivel, sentidos.MiEstado)) return false;
        _proximaRenuncia = ahora + EntreRenunciasMs;
        return transporte.Mandar(Paquete(ClientPacketId.RetirarFaccion, _ => { }));
    }

    /// <summary>
    /// SE LE ARRIMA A UNA PERSONA Y LE ROBA. Devuelve si se ocupó de esto (miró, caminó o robó); false si no había a quién.
    /// </summary>
    private async Task<bool> RobarAsync(TransporteDeBot transporte, SentidosDelBot sentidos, Task cerrada, CancellationToken ct)
    {
        if (!LadronSaleARobar(_clase, sentidos.MiEstado, _entorno.Datos.IsPkMap(sentidos.Mapa), sentidos.Salud, sentidos.EnergiaEnPuntos)
            || sentidos.HaceCuantoLeBajoLaVida <= LePeganHaceMs) return false;
        long ahora = Environment.TickCount64;
        var (x, y) = sentidos.Posicion;
        if (sentidos.PersonaMasCercana(v => !v.Muerto && !(_robadaHasta.TryGetValue(v.CharIndex, out long hasta) && ahora < hasta)
                                            && !_entorno.Datos.IsSafeZone(sentidos.Mapa, v.X, v.Y)
                                            && SeLoBusca(sentidos.NivelDe(v.CharIndex, NivelVistoMs), v.X - x, v.Y - y,
                                                         SeAnimaConElEscondido(sentidos, v.CharIndex))
                                            && SeAnimaConEsta(sentidos, v.CharIndex),
                                       GenteDejada()) is not { Pasos: <= RadioDeCaza } blanco) return false;
        short quien = blanco.Quien.CharIndex;
        // PRIMERO LA MIRA, como antes de pelear: la ficha dice si le conviene. Si ya la miró hace poco y no llegó, la deja.
        if (sentidos.FichaDe(quien, NivelVistoMs) is null)
        {
            if (_miradoEn.TryGetValue(quien, out long mirada) && ahora - mirada <= MirarDeNuevoMs)
            {
                _robadaHasta[quien] = ahora + MirarDeNuevoMs;
                return false;
            }
            MirarA(transporte, sentidos, quien);
            await Task.Delay(_rng.Next(600, 1_200), ct);
            return true;
        }
        int intentos = 0, pasos = 0;
        bool rodeando = false;
        while (!ct.IsCancellationRequested && !cerrada.IsCompleted && sentidos.Vivo && intentos < IntentosDeRobo)
        {
            if (sentidos.Donde(quien) is not { Muerto: false } v || sentidos.HaceCuantoLeBajoLaVida <= LePeganHaceMs
                || sentidos.EnergiaEnPuntos < EnergiaParaRobar + EnergiaQueGuardaAlRobar) break;
            var donde = sentidos.Posicion;
            int distancia = Math.Abs(v.X - donde.X) + Math.Abs(v.Y - donde.Y);
            if (distancia <= DistanciaDeRobo)
            {
                if (!transporte.Mandar(Paquete(ClientPacketId.WorkLeftClick,
                        w => w.WriteByte(v.X).WriteByte(v.Y).WriteByte((byte)GameLimits.SkillRobar)))) return false;
                intentos++;
                await Task.Delay(_rng.Next(_entorno.Ini.IntervaloTrabajo + 300, _entorno.Ini.IntervaloTrabajo + 900), ct);
                continue;
            }
            if (++pasos > RadioDeCaza * 2
                || PasoDeCaza(_entorno.Mapas, sentidos.Mapa, donde, (v.X, v.Y), _pasoTrabado, rodeando, sentidos.BaldosasOcupadas())
                    is not { } paso) break;
            rodeando = paso.Rodeando;
            if (await UnPasoHaciaAsync(transporte, sentidos, paso.Paso.X - donde.X, paso.Paso.Y - donde.Y, ct) != Paso.Dado) break;
        }
        _robadaHasta[quien] = Environment.TickCount64 + DejaALaRobadaMs;
        return true;
    }
}
