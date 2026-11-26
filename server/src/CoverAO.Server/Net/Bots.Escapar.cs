using CoverAO.Protocol;
using CoverAO.Server.Game;

namespace CoverAO.Server.Net;

/// <summary>
/// ESCAPARSE DE UNA CRIATURA (pedido del tester, 25/11): "que los bots se oculten, se tiren invisibilidad o paralicen/inmovilicen a la
/// criatura, si necesitan escapar de una criatura que no pueden o no se animan a matar". Antes de alejarse (<see cref="AlejarseAsync"/>)
/// prueba, en este orden: dejarla quieta si no es inmune y la tiene a tiro de hechizo (<see cref="QuietarA"/>), ponerse invisible si
/// sabe el hechizo, u ocultarse si tiene la habilidad (<see cref="SkillParaOcultarse"/>) y no va montado. Después se aleja igual: quieta
/// o sin verlo, la criatura no lo sigue.
/// </summary>
public sealed partial class Bot
{
    /// <summary>Desde cuánto de Ocultarse prueba esconderse: con menos casi nunca sale.</summary>
    public const int SkillParaOcultarse = 20;

    /// <summary>Entre un recurso y el siguiente: la invisibilidad y el ocultarse duran más que esto, y el bot no sabe si le salieron.</summary>
    private const int EntreEscapesMs = 15_000;

    private long _proximoEscape;

    /// <summary>¿Prueba ocultarse? Con la habilidad y sin ir montado (montado el servidor no deja, VB6 LocaleMsg 67).</summary>
    public static bool PruebaOcultarse(int skillOcultar, bool montado) => skillOcultar >= SkillParaOcultarse && !montado;

    /// <summary>Lo que hace antes de alejarse de una criatura. Devuelve si hizo algo.</summary>
    private async Task<bool> EscaparDeLaCriaturaAsync(TransporteDeBot transporte, SentidosDelBot sentidos, SentidosDelBot.Visible de,
                                                      CancellationToken ct)
    {
        long ahora = Environment.TickCount64;
        if (!de.EsNpc || ahora < _proximoEscape || !sentidos.Vivo) return false;
        var (x, y) = sentidos.Posicion;
        bool inmune = _entorno.Datos.Npc(de.FichaNpc) is { Inmunidad: not 0 };
        if (!inmune && !SigueQuieta(_quieta, de.CharIndex, de.X, de.Y, ahora)
            && Math.Abs(de.X - x) <= AlcanceHechizoX && Math.Abs(de.Y - y) <= AlcanceHechizoY)
        {
            int manaAntes = sentidos.ManaEnPuntos;
            if (QuietarA(transporte, sentidos, de))
            {
                _proximoEscape = ahora + EntreEscapesMs;
                Console.WriteLine($"[bot {Nombre}] quieta a la criatura {de.FichaNpc} para escaparse en m{sentidos.Mapa}");
                await Task.Delay(_rng.Next(GolpeMinMs, GolpeMaxMs), ct);
                _quieta = (de.CharIndex, QuietaHasta(Environment.TickCount64, cobro: sentidos.ManaEnPuntos < manaAntes, esNpc: true), de.X, de.Y);
                return true;
            }
        }
        if (sentidos.UsaMana && ElegirClicDeHechizo(sentidos, x, y) is { } clic)
        {
            RevisarElUltimoCasteo(sentidos);
            if (ElMasBaratoQueSabe(sentidos, h => h.IsInvisibility) is { } ranura && LanzarSobre(transporte, sentidos, ranura, x, clic))
            {
                _proximoEscape = ahora + EntreEscapesMs;
                Console.WriteLine($"[bot {Nombre}] se pone invisible para escaparse de la criatura {de.FichaNpc} en m{sentidos.Mapa}");
                await Task.Delay(_rng.Next(GolpeMinMs, GolpeMaxMs), ct);
                return true;
            }
        }
        if (PruebaOcultarse(sentidos.Skill(GameLimits.SkillOcultar), VaMontado(sentidos))
            && transporte.Mandar(Paquete(ClientPacketId.Work, w => w.WriteByte(GameLimits.SkillOcultar))))
        {
            _proximoEscape = ahora + EntreEscapesMs;
            Console.WriteLine($"[bot {Nombre}] se oculta para escaparse de la criatura {de.FichaNpc} en m{sentidos.Mapa}");
            await Task.Delay(_rng.Next(PocionMinMs, PocionMaxMs), ct);
            return true;
        }
        return false;
    }
}
