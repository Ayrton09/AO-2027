using CoverAO.Protocol;
using CoverAO.Server.Game;

namespace CoverAO.Server.Net;

internal sealed record CuentaParaEspectar(string Nombre, string[] Personajes, int Generacion);

public sealed partial class PacketDispatcher
{
    // La cámara es de la sesión. No ocupa una baldosa, no cambia el personaje del GM
    // y no reenvía los paquetes privados de la conexión observada.
    private sealed class VistaEspectador(Player objetivo)
    {
        public Player Objetivo { get; set; } = objetivo;
        public Player? Gm { get; init; }
        public CuentaParaEspectar? Cuenta { get; init; }
        public string? Staff { get; init; }
        public long PermisoHasta { get; set; }
        public bool Renovando { get; set; }
        public byte[]? UltimaEscena { get; set; }
        public byte[]? UltimoPanel { get; set; }
    }

    private readonly Dictionary<ClientSession, VistaEspectador> _espectadores = new();
    private const int CadaCuantosTicksEspectador = 3;
    private const int VigenciaDelPermiso = 5000 / GameLoop.TickMs;
    private const int MargenParaRenovar = 1000 / GameLoop.TickMs;

    private static bool PuedeEspectar(int rango, Player objetivo) =>
        rango > PrivUser && !objetivo.SinConexion
        && objetivo.Connection is ClientSession { Cerrada: false } && !objetivo.AdminInvisible
        && ((objetivo.Privileges & (PrivDios | PrivAdmin)) == 0 || (rango & (PrivDios | PrivAdmin)) != 0);

    private CharacterSummary[] RosterConStaffActual(List<CharacterSummary> roster) =>
        roster.Select(p => p with { GameMaster = _ini.EsStaff(p.Name) }).ToArray();

    private void EmpezarAEspectar(ClientSession sesion, string nombre)
    {
        if (sesion.Cerrada) return;
        if (sesion.Player is { } gm)
        {
            if (!gm.IsGm) return;
            var observado = _world.PlayerByName(nombre);
            if (observado is null || ReferenceEquals(gm, observado) || !PuedeEspectar(gm.Privileges, observado))
            { AvisarQueNoPuedeEspectar(sesion); return; }
            var vista = new VistaEspectador(observado) { Gm = gm };
            _espectadores[sesion] = vista;
            EnviarEscena(sesion, vista);
            return;
        }
        if (sesion.CuentaParaEspectar is not { } cuenta || sesion.ComprobandoEspectador) return;
        if (!CuentaDeEspectadorVigente(sesion, cuenta)) { AvisarQueNoPuedeEspectar(sesion); return; }

        var objetivo = _world.PlayerByName(nombre);
        if (objetivo is null) { AvisarQueNoPuedeEspectar(sesion); return; }
        var candidatos = cuenta.Personajes.Where(p => PuedeEspectar(_ini.PrivilegioDe(p), objetivo)).ToArray();
        if (candidatos.Length == 0) { AvisarQueNoPuedeEspectar(sesion); return; }
        int revision = ++sesion.RevisionDeEspectador;
        sesion.ComprobandoEspectador = true;
        Responder(_db.LeerStaffParaEspectarAsync(cuenta.Nombre, candidatos), staff =>
        {
            sesion.ComprobandoEspectador = false;
            if (sesion.Cerrada || sesion.RevisionDeEspectador != revision || sesion.Player is not null) return;
            if (!CuentaDeEspectadorVigente(sesion, cuenta) || staff is null
                || !PuedeEspectar(_ini.PrivilegioDe(staff), objetivo) || !SigueEnElMundo(objetivo))
            { AvisarQueNoPuedeEspectar(sesion); return; }

            var vista = new VistaEspectador(objetivo)
            {
                Cuenta = cuenta, Staff = staff, PermisoHasta = _loop.Tick + VigenciaDelPermiso
            };
            _espectadores[sesion] = vista;
            sesion.CambiarEspectadorSinPersonaje(true);
            Console.WriteLine($"[espectador] cuenta={cuenta.Nombre} staff={staff} objetivo={objetivo.Name}");
            EnviarEscena(sesion, vista);
        }, alFallar: () =>
        {
            sesion.ComprobandoEspectador = false;
            if (!sesion.Cerrada && sesion.RevisionDeEspectador == revision) AvisarQueNoPuedeEspectar(sesion);
        });
    }

    private static void AvisarQueNoPuedeEspectar(ClientSession sesion)
    {
        const string mensaje = "No podés observar a ese personaje.";
        sesion.Send(sesion.Player is null && !sesion.EspectaSinPersonaje
            ? ServerMessages.ShowMessageBox(mensaje)
            : ServerMessages.ConsoleMsg(mensaje, FuenteInfo));
    }

    private bool CuentaDeEspectadorVigente(ClientSession sesion, CuentaParaEspectar cuenta) =>
        ReferenceEquals(sesion.CuentaParaEspectar, cuenta) &&
        GeneracionDeClave(cuenta.Nombre) == cuenta.Generacion &&
        _admin?.IsIpBanned(ConnectionGuard.NormalizeIp(sesion.RemoteEndpoint)) != true;

    private bool SigueEnElMundo(Player personaje) =>
        _world.TryGet(personaje.CharIndex, out var actual) && ReferenceEquals(actual, personaje);

    private int RangoDeEspectador(ClientSession sesion, VistaEspectador vista, long tick)
    {
        if (sesion.Cerrada || !ReferenceEquals(sesion.Player, vista.Gm)
            || (vista.Cuenta is { } cuenta && (!CuentaDeEspectadorVigente(sesion, cuenta) || tick >= vista.PermisoHasta)))
            return PrivUser;
        return vista.Gm?.Privileges ?? _ini.PrivilegioDe(vista.Staff);
    }

    private void NavegarEspectador(ClientSession sesion, bool siguiente)
    {
        if (!_espectadores.TryGetValue(sesion, out var vista)) return;
        int rango = RangoDeEspectador(sesion, vista, _loop.Tick);
        if (rango <= PrivUser) { DejarDeEspectar(sesion); return; }

        var porNombre = StringComparer.OrdinalIgnoreCase;
        var opciones = _world.Players.Where(p => !ReferenceEquals(p, vista.Gm) && !ReferenceEquals(p, vista.Objetivo)
                && PuedeEspectar(rango, p))
            .OrderBy(p => p.Name, porNombre)
            .ToArray();
        if (opciones.Length == 0) return;
        string actual = vista.Objetivo.Name;
        var elegido = siguiente
            ? opciones.FirstOrDefault(p => porNombre.Compare(p.Name, actual) > 0) ?? opciones[0]
            : opciones.LastOrDefault(p => porNombre.Compare(p.Name, actual) < 0) ?? opciones[^1];

        // Mantiene el permiso ya comprobado. Una búsqueda por nombre que todavía estaba en la base
        // no puede ganarle a este cambio y mover la cámara unos instantes más tarde.
        sesion.RevisionDeEspectador++;
        vista.Objetivo = elegido;
        EnviarEscena(sesion, vista);
    }

    private void DejarDeEspectar(ClientSession sesion)
    {
        sesion.RevisionDeEspectador++;
        if (sesion.EspectaSinPersonaje) sesion.CambiarEspectadorSinPersonaje(false);
        if (!_espectadores.Remove(sesion) || sesion.Cerrada) return;
        sesion.Send(new ByteQueueWriter().WritePacketId(ServerPacketId.EscenaEspectador).WriteInteger(0).ToArray());
    }

    internal void ActualizarEspectadores(long tick)
    {
        if (_espectadores.Count == 0 || tick % CadaCuantosTicksEspectador != 0) return;
        foreach (var (sesion, vista) in _espectadores.ToArray())
        {
            if (!SigueEnElMundo(vista.Objetivo) || !PuedeEspectar(RangoDeEspectador(sesion, vista, tick), vista.Objetivo))
            {
                DejarDeEspectar(sesion);
                continue;
            }
            if (vista.Cuenta is { } activa && !vista.Renovando && tick >= vista.PermisoHasta - MargenParaRenovar)
                RenovarEspectador(sesion, vista, activa);
            EnviarEscena(sesion, vista);
        }
    }

    private void RenovarEspectador(ClientSession sesion, VistaEspectador vista, CuentaParaEspectar cuenta)
    {
        // La cola de la base vuelve a comprobar baneos y pertenencia sin frenar el mundo.
        // Si no hay respuesta, al vencer el permiso se deja de mandar la vista.
        vista.Renovando = true;
        Responder(_db.LeerStaffParaEspectarAsync(cuenta.Nombre, [vista.Staff!]), staff =>
        {
            if (_espectadores.GetValueOrDefault(sesion) != vista) return;
            if (staff is null || _loop.Tick >= vista.PermisoHasta) { DejarDeEspectar(sesion); return; }
            vista.PermisoHasta = _loop.Tick + VigenciaDelPermiso;
            vista.Renovando = false;
        }, alFallar: () =>
        {
            if (_espectadores.GetValueOrDefault(sesion) == vista) DejarDeEspectar(sesion);
        });
    }

    private void EnviarEscena(ClientSession sesion, VistaEspectador vista)
    {
        var objetivo = vista.Objetivo;
        var pos = objetivo.Pos;
        var escena = new ByteQueueWriter().WritePacketId(ServerPacketId.EscenaEspectador)
            .WriteInteger(objetivo.CharIndex).WriteInteger(pos.Map).WriteAsciiString(objetivo.Name);

        // Reusa el formato visual ya usado al entrar a un área. El índice 0 termina la lista
        // de personajes; el índice de objeto 0 termina la del suelo.
        void Personaje(byte[] paquete) => escena.WriteBytes(paquete.AsSpan(1));
        Personaje(ServerMessages.CharacterCreate(objetivo));
        foreach (var otro in _world.Observers(pos))
            if (!ReferenceEquals(otro, objetivo) && !otro.AdminInvisible && !otro.Invisible && !otro.Oculto)
                Personaje(ServerMessages.CharacterCreate(otro));
        foreach (var npc in _world.NpcsOnMap(pos.Map))
            if (World.InView(pos, npc.Pos)) Personaje(ServerMessages.CharacterCreate(npc));
        escena.WriteInteger(0);

        foreach (var (donde, objeto, cantidad) in _world.GroundInView(pos))
        {
            if (objeto <= 0 || (_data.Obj(objeto) is { } info && EsObjetoFijo(info.ObjType))) continue;
            escena.WriteInteger(objeto).WriteByte(donde.X).WriteByte(donde.Y).WriteInteger(cantidad);
        }
        byte[] paquete = escena.WriteInteger(0).ToArray();
        if (!paquete.AsSpan().SequenceEqual(vista.UltimaEscena))
        {
            vista.UltimaEscena = paquete;
            sesion.Send(paquete);
        }

        // Se actualiza aunque el personaje y la cámara estén quietos, y no viaja con cada paso.
        byte[] panel = ServerMessages.PanelEspectador(objetivo);
        if (panel.AsSpan().SequenceEqual(vista.UltimoPanel)) return;
        vista.UltimoPanel = panel;
        sesion.Send(panel);
    }
}
