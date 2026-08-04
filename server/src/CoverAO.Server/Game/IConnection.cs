namespace CoverAO.Server.Game;

/// <summary>
/// Abstraction the game layer uses to push bytes to a client, without depending on the TCP layer.
/// Implemented by the network session. Keeps Game independent of Net (the VB6 code fused them).
/// </summary>
public interface IConnection
{
    string RemoteEndpoint { get; }
    /// <summary><c>Environment.TickCount64</c> del último byte RECIBIDO del cliente, para el
    /// <c>IdleLimit</c> del Server.ini. Se mide sobre lo que ENTRA: lo que el server manda (clima,
    /// día/noche, movimiento de otros) le llega a todo el mundo y no es actividad del jugador.</summary>
    long UltimaActividadMs { get; }
    void Send(byte[] packet);
    void Close(string reason);
}
