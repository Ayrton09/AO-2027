using CoverAO.Server.Game;

namespace CoverAO.Server.Net;

public sealed partial class PacketDispatcher
{
    private void EncargarMandado(ClientSession session, Player p)
    {
        if (!PuedeHacerMandado(p, DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            || !_ini.MercaderLeVende(_data.MapBattleMode(p.Pos.Map), p.FactionStatus)
            || !_data.MapSpawns.TryGetValue(p.Pos.Map, out var nacen)
            || !nacen.Any(s => MandadoParaVestirse.VendeRopaBasica(_data, _data.Npc(s.NpcIndex), p.Level, p.Class, p.Race, p.Gender))) return;

        session.MandadoPendiente = (p.Pos.Map, Environment.TickCount64 + 5 * 60_000);
        session.Send(ServerMessages.ConsoleMsg(MandadoParaVestirse.Encargo, FuenteInfo));
    }

    private void RecibirMandado(ClientSession session, Player p, Npc sastre)
    {
        if (session.MandadoPendiente is not { } encargo || encargo.Mapa != p.Pos.Map
            || Environment.TickCount64 >= encargo.Vence
            || !MandadoParaVestirse.VendeRopaBasica(_data, sastre.Template, p.Level, p.Class, p.Race, p.Gender)) return;

        long ahora = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (!PuedeHacerMandado(p, ahora)) return;

        session.MandadoPendiente = null;
        p.UltimoMandado = ahora;
        p.Gold += MandadoParaVestirse.Paga;
        // El pago y su fecha viajan en la misma ficha y se guardan juntos.
        _db.SaveCharacter(p);
        session.Send(ServerMessages.UpdateGold(p.Gold));
        session.Send(ServerMessages.ConsoleMsg($"{sastre.Name}: Gracias por el mandado. Acá tenés {MandadoParaVestirse.Paga} monedas para ropa y comida.", FuenteInfo));
        _economia?.Anotar($"mandado {p.Name}: cobra {MandadoParaVestirse.Paga} oro en m{p.Pos.Map}");
    }

    /// <summary>EL PRÉSTAMO DEL BANCO, al hacerle doble clic al banquero: ver <see cref="Game.PrestamoDelBanco"/>.</summary>
    private void Prestar(ClientSession session, Player p, Npc banquero)
    {
        int monto = _ini.PrestamoDelBanco;
        if (!p.IsAlive || !Game.PrestamoDelBanco.LePresta(monto, p.Level, p.Gold, p.Bank, p.DeudaConElBanco)
            || p.Gold + (long)monto > MaxOro) return;
        p.Gold += monto;
        p.DeudaConElBanco = monto;
        // El oro y la deuda viajan en la misma ficha y se guardan juntos.
        _db.SaveCharacter(p);
        session.Send(ServerMessages.UpdateGold(p.Gold));
        session.Send(ServerMessages.ConsoleMsg($"{banquero.Name}: Te presto {monto} monedas para que comas. Lo próximo que deposites "
                                               + "paga primero el préstamo.", FuenteInfo));
        _economia?.Anotar($"prestamo {p.Name}: recibe {monto} oro en m{p.Pos.Map}");
    }

    private bool PuedeHacerMandado(Player p, long ahora) =>
        MandadoParaVestirse.Necesita(p.Level, p.Stamina, p.Gold, p.Bank)
        && MandadoParaVestirse.EsPueblo(_data, p.Pos.Map)
        && MandadoParaVestirse.PuedeVolverACobrar(p.UltimoMandado, ahora)
        && !p.Inventory.Concat(p.BankInventory).Any(i => !i.IsEmpty
            && MandadoParaVestirse.PuedeVestir(_data.Obj(i.ObjIndex), p.Level, p.Class, p.Race, p.Gender));
}
