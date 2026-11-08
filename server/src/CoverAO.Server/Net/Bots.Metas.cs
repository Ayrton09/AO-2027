using System.Text.Json;
using CoverAO.Server.Data;
using CoverAO.Server.Game;

namespace CoverAO.Server.Net;

internal enum RumboDeBot { Equipo, Faccion, Abastecer }

internal readonly record struct MetaDeBot(RumboDeBot Rumbo, int Objetivo, int Progreso, byte Faccion = 0)
{
    public bool Cumplida => Progreso >= Objetivo;
}

/// <summary>Una meta por personaje de bot. Los avances se guardan por hitos, no en cada vuelta del juego.</summary>
internal sealed class MetasDeBots
{
    private readonly string _ruta;
    private readonly object _candado = new();
    private readonly Dictionary<string, MetaDeBot> _metas = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<long> _reloj;
    private bool _noPisar;
    private volatile bool _lecturaPendiente;
    private long _reintentoEn = long.MaxValue;

    public MetasDeBots(string dataRoot, Func<long>? reloj = null)
    {
        _ruta = Path.Combine(dataRoot, "bots-metas.json");
        _reloj = reloj ?? (() => Environment.TickCount64);
        Cargar();
    }

    public bool Listo => !_lecturaPendiente;

    private void Cargar()
    {
        try
        {
            var guardadas = JsonSerializer.Deserialize<Dictionary<string, MetaDeBot>>(File.ReadAllText(_ruta));
            if (guardadas is not null)
                foreach (var (nombre, meta) in guardadas)
                    if (Enum.IsDefined(meta.Rumbo) && meta.Objetivo > 0 && meta.Progreso >= 0 && meta.Progreso <= meta.Objetivo
                        && (meta.Rumbo != RumboDeBot.Faccion || RangosDeFaccion.Bajas(meta.Faccion).Contains(meta.Objetivo)))
                        _metas[nombre] = meta;
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        catch (JsonException ex)
        {
            _noPisar = true;
            Console.Error.WriteLine($"[bots] no se pudo leer bots-metas.json: {ex.Message}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _lecturaPendiente = true;
            Volatile.Write(ref _reintentoEn, _reloj() + 30_000);
            Console.Error.WriteLine($"[bots] no se pudo leer bots-metas.json: {ex.Message}");
            return;
        }
        _lecturaPendiente = false;
        Volatile.Write(ref _reintentoEn, long.MaxValue);
    }

    public MetaDeBot? Leer(string nombre)
    {
        lock (_candado) return _metas.TryGetValue(nombre, out var meta) ? meta : null;
    }

    public void Guardar(string nombre, MetaDeBot meta)
    {
        lock (_candado)
        {
            if (_lecturaPendiente) return;
            if (_metas.TryGetValue(nombre, out var anterior) && anterior == meta) return;
            _metas[nombre] = meta;
            if (!_noPisar && (_reintentoEn == long.MaxValue || _reloj() >= _reintentoEn)) Escribir();
        }
    }

    public void ReintentarPendiente()
    {
        if (_reloj() < Volatile.Read(ref _reintentoEn)) return;
        lock (_candado)
        {
            if (_reloj() < _reintentoEn) return;
            if (_lecturaPendiente) Cargar();
            else Escribir();
        }
    }

    private void Escribir()
    {
        try
        {
            EscrituraAtomica.Escribir(_ruta, JsonSerializer.Serialize(_metas), System.Text.Encoding.UTF8);
            Volatile.Write(ref _reintentoEn, long.MaxValue);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Volatile.Write(ref _reintentoEn, _reloj() + 30_000);
            Console.Error.WriteLine($"[bots] no se pudo guardar bots-metas.json: {ex.Message}");
        }
    }
}

public sealed partial class Bot
{
    private MetaDeBot? _metaDeNivel50;
    private int _lotesPendientesDeLaMeta;

    private byte FaccionDeLaMeta() => _miBando switch
    {
        Player.StatusArmada or Player.StatusMilicia or Player.StatusCaos => _miBando,
        Player.StatusCiudadano when !Agresivo => Player.StatusArmada,
        Player.StatusRepublicano when !Agresivo => Player.StatusMilicia,
        Player.StatusCriminal when Agresivo => Player.StatusCaos,
        _ => 0,
    };

    private MetaDeBot? ElegirMetaDeNivel50(SentidosDelBot s)
    {
        if (EsFabricanteMetalurgico) return new(RumboDeBot.Abastecer, 6, 0);
        byte faccion = FaccionDeLaMeta();
        if (faccion != 0 && (_propio & 1) == 0)
        {
            if (s.MisBajas is not { } b) return null;
            int bajas = BajasParaSuBando(_miBando, b);
            foreach (int siguiente in RangosDeFaccion.Bajas(faccion))
                if (siguiente > bajas) return new(RumboDeBot.Faccion, siguiente, bajas, faccion);
        }
        return new(RumboDeBot.Equipo, 50_000, 0);
    }

    internal void ActualizarMetaDeNivel50(SentidosDelBot s)
    {
        if (s.Nivel < Leveling.MaxLevel || _entorno.Metas is not { } metas) return;
        metas.ReintentarPendiente();
        if (!metas.Listo) return;
        if (_metaDeNivel50 is null)
        {
            _metaDeNivel50 = metas.Leer(Nombre) ?? ElegirMetaDeNivel50(s);
            if (_metaDeNivel50 is { } nueva) metas.Guardar(Nombre, nueva);
        }
        AplicarLotesPendientes();
        if (_metaDeNivel50 is { Rumbo: RumboDeBot.Faccion } faccional && FaccionDeLaMeta() != faccional.Faccion)
        {
            if (ElegirMetaDeNivel50(s) is { } nueva) GuardarAvance(nueva);
            return;
        }
        if (_metaDeNivel50 is not { Cumplida: false } meta) return;
        int progreso = meta.Rumbo switch
        {
            RumboDeBot.Equipo => (int)Math.Min(meta.Objetivo, ((long)s.Oro + Math.Max(0, s.Banco)) / 5_000 * 5_000),
            RumboDeBot.Faccion when s.MisBajas is { } b => Math.Min(meta.Objetivo, BajasParaSuBando(_miBando, b)),
            _ => meta.Progreso,
        };
        if (progreso > meta.Progreso) GuardarAvance(meta with { Progreso = progreso });
    }

    private void GuardarAvance(MetaDeBot meta)
    {
        _metaDeNivel50 = meta;
        _entorno.Metas?.Guardar(Nombre, meta);
    }

    internal void AnotarLotePublicado(SentidosDelBot s)
    {
        if (s.Nivel < Leveling.MaxLevel || _entorno.Metas is null) return;
        _lotesPendientesDeLaMeta++;
        AplicarLotesPendientes();
    }

    private void AplicarLotesPendientes()
    {
        if (_lotesPendientesDeLaMeta == 0 || _metaDeNivel50 is not { } meta) return;
        int lotes = _lotesPendientesDeLaMeta;
        _lotesPendientesDeLaMeta = 0;
        if (meta.Rumbo == RumboDeBot.Abastecer && !meta.Cumplida)
            GuardarAvance(meta with { Progreso = (int)Math.Min(meta.Objetivo, (long)meta.Progreso + lotes) });
    }

    private bool GuardaParaSuMeta => _metaDeNivel50 is { Rumbo: RumboDeBot.Equipo or RumboDeBot.Abastecer, Cumplida: false };
    private int PlataParaGustos(SentidosDelBot s) => GuardaParaSuMeta ? 0 : PlataParaElEquipo(s);

    private string? ContarSuMeta() => _metaDeNivel50 switch
    {
        { Rumbo: RumboDeBot.Equipo, Cumplida: true } => "ya junte el oro, ahora busco una mejora de equipo",
        { Rumbo: RumboDeBot.Equipo } m => $"junto oro para equipo, lleve {m.Progreso} de {m.Objetivo}",
        { Rumbo: RumboDeBot.Faccion, Cumplida: true } => "ya alcance la meta de bajas para mi rango",
        { Rumbo: RumboDeBot.Faccion } m => $"busco mi proximo rango, llevo {m.Progreso} de {m.Objetivo} bajas",
        { Rumbo: RumboDeBot.Abastecer } m => $"llevo {m.Progreso} de {m.Objetivo} lotes publicados en subastas",
        _ => null,
    };
}
