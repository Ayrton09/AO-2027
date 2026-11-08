using System.Text.Json;
using CoverAO.Server.Data;
using CoverAO.Server.Game;

namespace CoverAO.Server.Net;

internal enum RumboDeBot { Equipo, Faccion, Abastecer }

internal readonly record struct MetaDeBot(RumboDeBot Rumbo, int Objetivo, int Progreso, byte Faccion = 0,
                                           short Pieza = 0, bool Confirmada = false)
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
    private long _proximaBusquedaDePieza;

    private short? PiezaDeLaMeta => _metaDeNivel50 is { Rumbo: RumboDeBot.Equipo, Confirmada: false, Pieza: > 0 } meta
        ? meta.Pieza : null;

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
            byte? rango = _miBando == faccion ? s.RangoEn(faccion) : (byte)0;
            if (rango is null) return null;
            int siguiente = RangosDeFaccion.ParaRango(faccion, rango.Value + 1);
            if (siguiente != int.MaxValue) return new(RumboDeBot.Faccion, siguiente, Math.Min(siguiente, bajas), faccion);
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
        if (_metaDeNivel50 is not { } meta) return;
        int progreso = meta.Rumbo switch
        {
            RumboDeBot.Equipo => (int)Math.Min(meta.Objetivo, ((long)s.Oro + Math.Max(0, s.Banco)) / 5_000 * 5_000),
            RumboDeBot.Faccion when s.MisBajas is { } b => Math.Min(meta.Objetivo, BajasParaSuBando(_miBando, b)),
            _ => meta.Progreso,
        };
        var actual = meta with { Progreso = Math.Max(progreso, meta.Progreso) };
        if (actual.Rumbo == RumboDeBot.Equipo && !actual.Confirmada)
            actual = ActualizarPiezaDeLaMeta(s, actual);
        if (actual.Rumbo == RumboDeBot.Faccion && s.RangoEn(actual.Faccion) is { } rango)
        {
            int objetivo = RangosDeFaccion.Bajas(actual.Faccion).IndexOf(actual.Objetivo) + 1;
            actual = actual with { Confirmada = rango >= objetivo };
        }
        if (actual != meta) GuardarAvance(actual);
    }

    private MetaDeBot ActualizarPiezaDeLaMeta(SentidosDelBot s, MetaDeBot meta)
    {
        if (meta.Pieza > 0 && s.RanurasOcupadas().Any(r => s.QueHayEn(r) == meta.Pieza && s.EstaPuesto(r)))
            return meta with { Confirmada = true };
        var categorias = SuArcoYSusFlechas(s) is null ? QueSePone : QueSePoneConElArco;
        if (_entorno.Datos.Obj(meta.Pieza) is { } pieza && categorias.FirstOrDefault(f => f(pieza)) is { } categoria
            && LoPuedeUsar(pieza, s.Nivel) && !_noSeLoPone.Contains(meta.Pieza)
            && (MejoraLoQueTiene(s, meta.Pieza) || (MejorParaPoner(s, categoria) is { } r && s.QueHayEn(r) == meta.Pieza))) return meta;
        if (!s.Ubicado || !s.Vivo || _acompana is not null) return meta;
        if (Environment.TickCount64 < _proximaBusquedaDePieza) return meta with { Pieza = 0 };
        _proximaBusquedaDePieza = Environment.TickCount64 + 60_000;

        int presupuesto = Math.Max(meta.Objetivo, PlataParaElEquipo(s));
        bool Sirve(ObjInfo o) => !o.IsNewbie && o.Value > 0 && o.Value <= presupuesto && !_noSeLoPone.Contains(o.Index)
            && categorias.Any(f => f(o)) && MejoraLoQueTiene(s, o.Index);
        IEnumerable<ObjInfo> Piezas(int ficha) => _entorno.Datos.Npc(ficha)?.StockItems
            ?.Select(i => _entorno.Datos.Obj(i.ObjIndex)).OfType<ObjInfo>().Where(Sirve) ?? [];

        short Elegir(int ficha) => Piezas(ficha).OrderByDescending(ValorDeCombate).ThenBy(o => o.Value).FirstOrDefault()?.Index ?? 0;
        if (LeVendenAca(s.Mapa) && s.MasCercano(f => Piezas(f).Any()) is { } cerca)
            return meta with { Pieza = Elegir(cerca.Quien.FichaNpc) };
        if (_entorno.Mapas.DondeHay(s.Mapa, s.Posicion, f => Piezas(f).Any(), LeVendenAca, Pisable(s)) is { } lugar
            && _entorno.Datos.MapSpawns.TryGetValue(lugar.Mapa, out var nacen))
            return meta with { Pieza = Elegir(nacen.FirstOrDefault(n => n.X == lugar.X && n.Y == lugar.Y && Piezas(n.NpcIndex).Any()).NpcIndex) };
        return meta with { Pieza = 0 };
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

    private bool GuardaParaSuMeta => _metaDeNivel50 is { Rumbo: RumboDeBot.Equipo or RumboDeBot.Abastecer, Cumplida: false, Confirmada: false };
    private int PlataParaGustos(SentidosDelBot s) => GuardaParaSuMeta ? 0 : PlataParaElEquipo(s);

    private string? ContarSuMeta(SentidosDelBot s) => _metaDeNivel50 switch
    {
        { Rumbo: RumboDeBot.Equipo, Confirmada: true } m => $"ya me equipe: {NombreDeLaPieza(m.Pieza)}",
        { Rumbo: RumboDeBot.Equipo, Cumplida: true, Pieza: > 0 } m => $"ya junte el oro, voy por {NombreDeLaPieza(m.Pieza)}",
        { Rumbo: RumboDeBot.Equipo, Cumplida: true } => "ya junte el oro, ahora busco una mejora de equipo",
        { Rumbo: RumboDeBot.Equipo, Pieza: > 0 } m => $"junto oro para {NombreDeLaPieza(m.Pieza)}, llevo {m.Progreso} de {m.Objetivo}",
        { Rumbo: RumboDeBot.Equipo } m => $"junto oro para equipo, lleve {m.Progreso} de {m.Objetivo}",
        { Rumbo: RumboDeBot.Faccion, Confirmada: true } m =>
            $"ya me ascendieron a {PacketDispatcher.NombreDeRango(m.Faccion, s.RangoEn(m.Faccion)
                ?? (byte)(RangosDeFaccion.Bajas(m.Faccion).IndexOf(m.Objetivo) + 1))}",
        { Rumbo: RumboDeBot.Faccion, Cumplida: true } => "ya tengo las bajas, me falta reclamar el rango",
        { Rumbo: RumboDeBot.Faccion } m => $"busco mi proximo rango, llevo {m.Progreso} de {m.Objetivo} bajas",
        { Rumbo: RumboDeBot.Abastecer } m => $"llevo {m.Progreso} de {m.Objetivo} lotes publicados en subastas",
        _ => null,
    };

    private string NombreDeLaPieza(short pieza) => _entorno.Datos.Obj(pieza)?.Name ?? "mi equipo";
}
