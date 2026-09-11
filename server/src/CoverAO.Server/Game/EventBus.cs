using System.Collections.Concurrent;

namespace CoverAO.Server.Game;

/// <summary>Domain events — the decoupling seam between rules and side effects (I/O, broadcast).</summary>
public interface IGameEvent { }

public sealed record PlayerLoggedIn(Player Player) : IGameEvent;
public sealed record PlayerMoved(Player Player, WorldPos From, WorldPos To) : IGameEvent;
public sealed record PlayerTalked(Player Player, string Message) : IGameEvent;
public sealed record PlayerDisconnected(short CharIndex) : IGameEvent;
/// <summary>Murió un jugador. <c>Cause</c> dice qué lo mató ("lo mato Lobo (npc 501)", "el veneno"): ni el cadáver
/// ni el <c>.chr</c> lo guardan, así que es el único lugar donde queda.</summary>
public sealed record PlayerDied(Player Player, string Cause) : IGameEvent;

/// <summary>
/// Minimal synchronous publish/subscribe bus. Handlers publish events instead of reaching
/// directly into networking or other subsystems — the opposite of the VB6 pattern where game
/// logic emitted packets inline (e.g. Trabajo.bas: 146 inline Write* calls).
/// </summary>
public sealed class EventBus
{
    private readonly ConcurrentDictionary<Type, List<Action<IGameEvent>>> _subscribers = new();

    public void Subscribe<T>(Action<T> handler) where T : IGameEvent
    {
        var list = _subscribers.GetOrAdd(typeof(T), _ => new List<Action<IGameEvent>>());
        lock (list) list.Add(e => handler((T)e));
    }

    public void Publish(IGameEvent e)
    {
        if (_subscribers.TryGetValue(e.GetType(), out var list))
        {
            Action<IGameEvent>[] snapshot;
            lock (list) snapshot = list.ToArray();
            foreach (var h in snapshot) h(e);
        }
    }
}
