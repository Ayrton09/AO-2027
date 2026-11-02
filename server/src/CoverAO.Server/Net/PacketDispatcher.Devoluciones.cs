namespace CoverAO.Server.Net;

public sealed partial class PacketDispatcher
{
    // Una devolución puede esperar a un ingreso o a otro cambio del desconectado. Sigue siendo una
    // obligación aunque el callback que la originó haya terminado; no se descarta al cerrar la base.
    private sealed class DevolucionDeOro(string quien, int monto, string motivo)
    {
        public string Quien { get; } = quien;
        public int Monto { get; } = monto;
        public string Motivo { get; } = motivo;
        public bool Despachada;
    }

    // Sólo se toca desde el loop, o después de detenerlo durante el cierre.
    private readonly HashSet<DevolucionDeOro> _devolucionesPendientes = new();

    private void RegistrarDevolucion(string quien, int monto, string motivo)
    {
        var devolucion = new DevolucionDeOro(quien, monto, motivo);
        _devolucionesPendientes.Add(devolucion);
        IntentarDevolucion(devolucion);
    }

    private void IntentarDevolucion(DevolucionDeOro devolucion)
    {
        if (devolucion.Despachada) return;
        if (!Decidible(devolucion.Quien, () => IntentarDevolucion(devolucion))) return;
        DespacharDevolucion(devolucion, seguirRespuesta: true);
    }

    private void DespacharDevolucion(DevolucionDeOro devolucion, bool seguirRespuesta)
    {
        if (devolucion.Despachada) return;
        var vivo = _world.PlayerByName(devolucion.Quien);
        if (vivo is not null)
        {
            DarPorDespachada(devolucion);
            vivo.Bank += devolucion.Monto;
            _db.SaveCharacter(vivo);   // la devolución no espera al autosave, también si la cola ya se rindió
            vivo.Connection?.Send(ServerMessages.ConsoleMsg(devolucion.Motivo, FuenteInfo));
            return;
        }
        if (!_db.CharacterExists(devolucion.Quien))
        {
            DarPorDespachada(devolucion);
            AvisarDevolucionFallida(devolucion);
            return;
        }

        // No leer una ficha para cambiarle un número: esa lectura no tiene rescate y falla al rendir
        // la cola. La acreditación guarda su saldo previo y se rescata sin duplicarse al reiniciar.
        var credito = _db.AcreditarBovedaAsync(devolucion.Quien, devolucion.Monto);
        DarPorDespachada(devolucion);
        if (seguirRespuesta)
            CambiarDesconectado(devolucion.Quien, credito,
                saldo => { if (saldo is null) AvisarDevolucionFallida(devolucion); },
                () => AvisarDevolucionFallida(devolucion));
        else
            _ = credito.ContinueWith(t =>
            {
                if (!t.IsCompletedSuccessfully || t.Result is null) AvisarDevolucionFallida(devolucion);
            }, TaskScheduler.Default);
    }

    private void DarPorDespachada(DevolucionDeOro devolucion)
    {
        devolucion.Despachada = true;
        _devolucionesPendientes.Remove(devolucion);
    }

    private static void AvisarDevolucionFallida(DevolucionDeOro devolucion)
        => Console.Error.WriteLine($"[transferencia] no se pudieron devolver {devolucion.Monto} de oro a {devolucion.Quien}");

    /// <summary>Después de detener el loop y encolar las fichas finales y lo diferido, convierte las devoluciones que
    /// todavía esperaban una reserva en escrituras rescatables. Una transferencia apartada puede retener su Task para
    /// siempre: no se espera su callback. Ya no habrá ingresos ni fichas viejas posteriores que pisen estos créditos.</summary>
    public void DespacharDevolucionesPendientesAlApagar()
    {
        foreach (var devolucion in _devolucionesPendientes.ToArray())
            DespacharDevolucion(devolucion, seguirRespuesta: false);
    }
}
