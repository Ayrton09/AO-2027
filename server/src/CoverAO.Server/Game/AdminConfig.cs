using System.Collections.Concurrent;

namespace CoverAO.Server.Game;

/// <summary>
/// Estado de administración en caliente (lo muta el panel, lo leen los sistemas). Reúne las perillas que
/// el frmMain/frmServidor del server VB6 tenía sueltas: eventos de exp/oro/drop, autosave periódico, modo
/// "solo GMs" (mantenimiento), pausa del mundo, cadencia anti-cheat en vivo, mensajes automáticos
/// programados y baneo por IP. Campos simples (lecturas/escrituras atómicas en .NET); los comandos que
/// tocan estado de juego igual se encolan en el GameLoop desde el AdminServer.
/// </summary>
public sealed class AdminConfig
{
    public volatile bool Paused;      // congela el tick del mundo (NPCs/regen/clima/día-noche)
    public volatile bool OnlyGms;     // rechaza el login de no-GMs (mantenimiento)

    private int _exp = 1, _gold = 1, _drop = 1, _autosaveMin;
    /// <summary>Multiplicador de experiencia por matar NPC (1..50). "Evento Experiencia" del VB6.</summary>
    public int ExpMult { get => _exp; set => _exp = Math.Clamp(value, 1, 50); }
    /// <summary>Multiplicador de oro por matar NPC (1..50). "Evento Oro".</summary>
    public int GoldMult { get => _gold; set => _gold = Math.Clamp(value, 1, 50); }
    /// <summary>Multiplicador de probabilidad de drop (1..50). "Evento Drop".</summary>
    public int DropMult { get => _drop; set => _drop = Math.Clamp(value, 1, 50); }
    /// <summary>Autosave cada N minutos (0 = desactivado). "MinutosGuardarUsuarios" del VB6.</summary>
    public int AutosaveMinutes { get => _autosaveMin; set => _autosaveMin = Math.Clamp(value, 0, 240); }

    public long LastAutosaveTick;     // tick del último autosave (para el countdown)

    // --- Cadencia anti-cheat en vivo (VB6 IntervaloParaCaminar / IntervaloParaGolpe) ---
    // Editable desde el panel sin reiniciar. 0 = desactivado. El dispatcher los lee en cada paso/golpe.
    private long _walkMs, _attackMs;
    /// <summary>Intervalo mínimo entre pasos en ms (0 = sin límite). VB6 IntervaloParaCaminar.</summary>
    public long WalkIntervalMs { get => _walkMs; set => _walkMs = Math.Clamp(value, 0, 5_000); }
    /// <summary>Intervalo mínimo entre golpes en ms (0 = sin límite). VB6 IntervaloParaGolpe.</summary>
    public long AttackIntervalMs { get => _attackMs; set => _attackMs = Math.Clamp(value, 0, 20_000); }

    // --- Mensajes automáticos programados (frmMain del VB6: broadcast periódico rotativo) ---
    private volatile string[] _autoMsgs = Array.Empty<string>();
    private int _autoMsgMin;
    /// <summary>Cola de mensajes que se emiten en rotación. Vacía = ninguno.</summary>
    public string[] AutoMessages { get => _autoMsgs; set => _autoMsgs = value ?? Array.Empty<string>(); }
    /// <summary>Cada cuántos minutos se emite el próximo mensaje automático (0 = desactivado).</summary>
    public int AutoMsgMinutes { get => _autoMsgMin; set => _autoMsgMin = Math.Clamp(value, 0, 240); }
    public long LastAutoMsgTick;      // tick del último mensaje automático (para el countdown)
    public int AutoMsgIndex;          // índice rotativo dentro de AutoMessages

    // --- Baneo por IP (frmServidor del VB6: lista de IPs baneadas + "Unban All") ---
    // Set concurrente porque lo consulta el accept loop de cada transporte (hot path, fuera del GameLoop).
    private readonly ConcurrentDictionary<string, byte> _bannedIps = new(StringComparer.OrdinalIgnoreCase);
    public bool IsIpBanned(string ip) => _bannedIps.ContainsKey(ip);
    public bool BanIp(string ip) => !string.IsNullOrWhiteSpace(ip) && _bannedIps.TryAdd(ip.Trim(), 1);
    public bool UnbanIp(string ip) => _bannedIps.TryRemove(ip.Trim(), out _);
    public int ClearBannedIps() { int n = _bannedIps.Count; _bannedIps.Clear(); return n; }
    public string[] BannedIpList() => _bannedIps.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
    /// <summary>Carga inicial desde disco (no dispara persistencia; la hace el llamador).</summary>
    public void LoadBannedIps(IEnumerable<string> ips)
    { foreach (var ip in ips) if (!string.IsNullOrWhiteSpace(ip)) _bannedIps.TryAdd(ip.Trim(), 1); }

    /// <summary>¿El centinela (anti-macro) está activado? (VB6 <c>centinelaActivado</c>, Declares.bas). Lo
    /// conmuta el comando GM /CENTINELAACTIVADO y arranca con lo que diga <c>Centinela</c> en el Server.ini.
    ///
    /// <para>Desde el 2026-08-24 este flag GATEA el subsistema de verdad (ver
    /// <c>PacketDispatcher.Centinela.cs</c>). En el original no gatea nada porque modCentinela.bas está entero
    /// pero DESCONECTADO —sus dos puntos de entrada están comentados, frmMain.frm:749 y mMainLoop.bas:21—, y
    /// por eso acá el default es <b>apagado</b>: prendido es una regla de la casa, apagado es el install.</para>
    /// </summary>
    public bool CentinelActive { get; set; }

    /// <summary>Volcado de la lista a disco. Lo engancha Program.cs (data/banned-ips.txt) y lo llaman los dos
    /// caminos que mutan la lista: el panel de admin y los comandos GM /BANIP y /UNBANIP —el VB6 persiste en
    /// los dos casos, porque BanIpAgrega/BanIpQuita terminan siempre en BanIpGuardar (Admin.bas:302/348)—.
    /// Queda null si el proceso no tiene dónde escribir (tests), y entonces el ban vive sólo en memoria.</summary>
    public Action? PersistBannedIps { get; set; }

    /// <summary>Relectura de la lista desde disco (VB6 <c>BanIpCargar</c>), para el /BANIPRELOAD: vacía la
    /// lista en memoria y la repuebla con lo que haya en el archivo. Devuelve cuántas IPs quedaron, o -1 si el
    /// proceso no tiene de dónde leer. Lo engancha Program.cs junto con <see cref="PersistBannedIps"/>.</summary>
    public Func<int>? ReloadBannedIpsFrom { get; set; }

    /// <summary>Vacía y repuebla la lista desde disco. Devuelve la cantidad final (o -1 sin archivo).</summary>
    public int ReloadBannedIps()
    {
        if (ReloadBannedIpsFrom is null) return -1;
        _bannedIps.Clear();
        return ReloadBannedIpsFrom();
    }
}
