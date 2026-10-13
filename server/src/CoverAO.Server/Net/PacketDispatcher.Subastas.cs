using CoverAO.Protocol;
using CoverAO.Server.Game;

namespace CoverAO.Server.Net;

/// <summary>
/// EL CENTRO DE SUBASTAS, del lado de la red. <b>Es un agregado nuestro</b> (pedido del tester, 06/10): el CoverAO de 2021
/// trae al Subastador (NPCs.dat, ficha 164, <c>NpcType=16</c>, en quince lugares del mundo), los textos de la ventana y los
/// mensajes 318-321 del locale, pero ni una línea de código. Las reglas están en <see cref="CentroDeSubastas"/>; acá se
/// cobra, se saca de la mochila y se pone en la bóveda.
///
/// <para>Todo pedido exige estar a tiro (3 baldosas, como el banquero) del Subastador que se clickeó, vivo.</para>
/// </summary>
public sealed partial class PacketDispatcher
{
    /// <summary>El reloj de las subastas, en segundos Unix. Los tests lo cambian para vencerlas sin esperar.</summary>
    internal Func<long> RelojDeSubastas { get; set; } = () => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private long _subastasGuardadas;
    private long _ultimaRondaDeSubastasMs;

    /// <summary>El doble clic al Subastador: abre la ventana.</summary>
    private void AbrirSubastas(ClientSession session) => session.Send(ListaDeSubastas(abrir: true));

    /// <summary>La última lista armada: con el Centro lleno son unas 2000 filas, y se arma de nuevo sólo si cambió algo o pasó
    /// un segundo (el tiempo que queda de cada subasta va en segundos).</summary>
    private (bool abrir, long cambios, long ahora, byte[] lista)? _ultimaLista;

    private byte[] ListaDeSubastas(bool abrir)
    {
        long ahora = RelojDeSubastas();
        if (_ultimaLista is { } u && u.abrir == abrir && u.cambios == _world.Subastas.Cambios && u.ahora == ahora) return u.lista;
        var filas = _world.Subastas.Abiertas.Select(s => new ServerMessages.FilaDeSubasta(s.Id, s.Objeto, s.Cantidad,
            s.PrecioActual, s.PrecioFinal, s.Vendedor, s.Termina - ahora, s.Comprador ?? "")).ToList();
        var lista = ServerMessages.SubastasLista(abrir, filas);
        _ultimaLista = (abrir, _world.Subastas.Cambios, ahora, lista);
        return lista;
    }

    /// <summary>El Subastador clickeado, si está a tiro; si no, avisa y devuelve null.</summary>
    private Npc? SubastadorAMano(ClientSession session, Player p)
    {
        if (!p.IsAlive) { session.Send(ServerMessages.LocaleMsg(77)); return null; }
        if (_world.NpcByIndex(p.TargetNpc) is not { IsSubastador: true } n)
        { session.Send(ServerMessages.LocaleMsg(22)); return null; }
        if (n.Pos.Map != p.Pos.Map || DistanciaVB6(p.Pos, n.Pos) > 3) { session.Send(ServerMessages.LocaleMsg(8)); return null; }
        return n;
    }

    /// <summary>UNA LISTA POR SEGUNDO POR PERSONAJE: con el Centro lleno cada pedido son decenas de KB, y diez conexiones
    /// pidiéndola sin parar trababan el loop y la salida de todos (auditoría del 13/10). Lo que llega antes se descarta.</summary>
    private void HandleSubastasPedir(ClientSession session) => _loop.Enqueue(() =>
    {
        if (session.Player is not { } p) return;
        long ahoraMs = Environment.TickCount64;
        if (ahoraMs - p.UltimaListaDeSubastasMs < 1000) return;
        if (SubastadorAMano(session, p) is null) return;
        p.UltimaListaDeSubastasMs = ahoraMs;
        session.Send(ListaDeSubastas(abrir: false));
    });

    private void HandleSubastaPublicar(ClientSession session, ByteQueueReader r)
    {
        byte ranura = r.ReadByte();
        short cantidad = r.ReadInteger();
        long inicial = r.ReadLong(), final = r.ReadLong();
        byte horas = r.ReadByte();
        if ((!RanuraDeInventario(ranura) || !CentroDeSubastas.Duraciones.Contains(horas))
            && Imposible(session, $"publicó la ranura {ranura} por {horas} horas")) return;
        _loop.Enqueue(() =>
        {
            var p = session.Player;
            if (p is null || SubastadorAMano(session, p) is null) return;
            if (ranura < 1 || ranura > GameLimits.MaxInventorySlots) return;
            var inv = p.Inventory[ranura];
            if (inv.IsEmpty) return;
            if (BarcoEnUso(session, p, ranura)) return;
            if (cantidad < 1 || cantidad > inv.Amount)
            { session.Send(ServerMessages.ConsoleMsg("No tienes esa cantidad.", FuenteInfo)); return; }
            // Lo que no se tira tampoco se subasta: lo que se destruye al tirarlo y el equipo de novato.
            if (_data.Obj(inv.ObjIndex) is not { } obj || obj.NoSeTira || (!p.IsGm && obj.IsNewbie))
            { session.Send(ServerMessages.LocaleMsg(320)); return; }
            switch (_world.Subastas.PuedePublicar(p.Name, inicial, final, horas))
            {
                case ResultadoDeSubasta.Ok: break;
                case ResultadoDeSubasta.DemasiadasSubastas:
                    session.Send(ServerMessages.ConsoleMsg($"No podés tener más de {CentroDeSubastas.MaximoPorVendedor} subastas a la vez.", FuenteInfo));
                    return;
                case ResultadoDeSubasta.DuracionInvalida: return;
                case ResultadoDeSubasta.CentroLleno:
                    session.Send(ServerMessages.ConsoleMsg("El centro de subastas está lleno: probá más tarde.", FuenteInfo));
                    return;
                default:
                    session.Send(ServerMessages.ConsoleMsg("El precio final tiene que ser mayor que el inicial.", FuenteInfo));
                    return;
            }
            long deposito = CentroDeSubastas.Deposito(horas, inicial, final);
            if (p.Gold < deposito) { session.Send(ServerMessages.LocaleMsg(26)); return; }
            short objeto = inv.ObjIndex;
            QuitarDeLaRanura(session, p, ranura, cantidad);
            p.Gold -= (int)deposito;
            session.Send(ServerMessages.UpdateGold(p.Gold));
            var s = _world.Subastas.Publicar(p.Name, objeto, cantidad, inicial, final, horas, RelojDeSubastas());
            // EL PERSONAJE Y LAS SUBASTAS SE GUARDAN YA, y en ese orden (la cola de la base los escribe en orden): el objeto salía
            // de la mochila en memoria y entraba a la subasta, cada uno con su guardado a su tiempo, y una caída entre los dos lo
            // duplicaba. Así, lo peor de una caída en el medio es perderlo (auditoría del 12/10).
            _db.SaveCharacter(p);
            GuardarLasSubastas();
            _economia?.Anotar($"subasta {s.Id} de {p.Name}: {cantidad} x obj {objeto}, desde {inicial} (final {final}), {horas} h, deposito {deposito}");
            session.Send(ServerMessages.ConsoleMsg($"Publicaste la subasta. Pagaste {deposito} monedas de depósito.", FuenteInfo));
            session.Send(ListaDeSubastas(abrir: false));
        });
    }

    private void HandleSubastaOfertar(ClientSession session, ByteQueueReader r)
    {
        int id = r.ReadLong();
        long monto = r.ReadLong();
        if (id < 1 && Imposible(session, $"ofertó por la subasta {id}")) return;
        _loop.Enqueue(() =>
        {
            var p = session.Player;
            if (p is null || SubastadorAMano(session, p) is null) return;
            long ahora = RelojDeSubastas();
            if (!Responder(session, _world.Subastas.PuedeOfertar(id, p.Name, monto, ahora))) return;
            long costo = _world.Subastas.CostoDeLaOferta(id, monto);
            if (p.Gold < costo) { session.Send(ServerMessages.LocaleMsg(26)); return; }
            string? superado = _world.Subastas.Buscar(id)!.Comprador;
            p.Gold -= (int)costo;
            session.Send(ServerMessages.UpdateGold(p.Gold));
            bool cerrada = _world.Subastas.Ofertar(id, p.Name, monto);
            _economia?.Anotar($"subasta {id}: {p.Name} oferta {costo}{(cerrada ? " y la gana" : "")}");
            session.Send(ServerMessages.ConsoleMsg(cerrada
                ? "¡Ganaste la subasta! El objeto va a tu bóveda."
                : $"Ofertaste {costo} monedas.", FuenteInfo));
            if (superado is not null && !superado.Equals(p.Name, StringComparison.OrdinalIgnoreCase))
                _world.PlayerByName(superado)?.Connection?.Send(ServerMessages.ConsoleMsg(
                    "Superaron tu oferta en el Centro de Subastas. Tu oro vuelve a tu bóveda.", FuenteInfo));
            EntregarLoDeLasSubastasAlQueEsta();
            session.Send(ListaDeSubastas(abrir: false));
        });
    }

    private void HandleSubastaComprar(ClientSession session, ByteQueueReader r)
    {
        int id = r.ReadLong();
        if (id < 1 && Imposible(session, $"compró la subasta {id}")) return;
        _loop.Enqueue(() =>
        {
            var p = session.Player;
            if (p is null || SubastadorAMano(session, p) is null) return;
            if (!Responder(session, _world.Subastas.PuedeComprar(id, p.Name, RelojDeSubastas()))) return;
            var s = _world.Subastas.Buscar(id)!;
            if (p.Gold < s.PrecioFinal) { session.Send(ServerMessages.LocaleMsg(26)); return; }
            string? superado = s.Comprador;
            p.Gold -= (int)s.PrecioFinal;
            session.Send(ServerMessages.UpdateGold(p.Gold));
            _world.Subastas.Comprar(id, p.Name);
            _economia?.Anotar($"subasta {id}: {p.Name} compra directo por {s.PrecioFinal}");
            session.Send(ServerMessages.ConsoleMsg("Compraste la subasta. El objeto va a tu bóveda.", FuenteInfo));
            if (superado is not null && !superado.Equals(p.Name, StringComparison.OrdinalIgnoreCase))
                _world.PlayerByName(superado)?.Connection?.Send(ServerMessages.ConsoleMsg(
                    "Alguien compró directo la subasta en la que ofertaste. Tu oro vuelve a tu bóveda.", FuenteInfo));
            EntregarLoDeLasSubastasAlQueEsta();
            session.Send(ListaDeSubastas(abrir: false));
        });
    }

    private void HandleSubastaCancelar(ClientSession session, ByteQueueReader r)
    {
        int id = r.ReadLong();
        if (id < 1 && Imposible(session, $"canceló la subasta {id}")) return;
        _loop.Enqueue(() =>
        {
            var p = session.Player;
            if (p is null || SubastadorAMano(session, p) is null) return;
            if (!Responder(session, _world.Subastas.Cancelar(id, p.Name))) return;
            _economia?.Anotar($"subasta {id}: {p.Name} la cancela");
            session.Send(ServerMessages.ConsoleMsg("Cancelaste la subasta. El objeto vuelve a tu bóveda.", FuenteInfo));
            EntregarLoDeLasSubastasAlQueEsta();
            session.Send(ListaDeSubastas(abrir: false));
        });
    }

    /// <summary>El aviso de un pedido que no se pudo. false si no se pudo.</summary>
    private static bool Responder(ClientSession session, ResultadoDeSubasta r)
    {
        byte[]? aviso = r switch
        {
            ResultadoDeSubasta.Ok => null,
            ResultadoDeSubasta.EsTuya => ServerMessages.LocaleMsg(318),
            ResultadoDeSubasta.NoExiste or ResultadoDeSubasta.YaTermino => ServerMessages.LocaleMsg(319),
            ResultadoDeSubasta.OfertaBaja => ServerMessages.ConsoleMsg("Tu oferta tiene que superar el precio actual.", FuenteInfo),
            ResultadoDeSubasta.SinCompraDirecta => ServerMessages.ConsoleMsg("Esa subasta no tiene precio final.", FuenteInfo),
            ResultadoDeSubasta.NoEsTuya => ServerMessages.ConsoleMsg("Esa subasta no es tuya.", FuenteInfo),
            ResultadoDeSubasta.TieneOfertas => ServerMessages.ConsoleMsg("No podés cancelar una subasta que ya tiene ofertas.", FuenteInfo),
            _ => ServerMessages.ConsoleMsg("No se pudo.", FuenteInfo),
        };
        if (aviso is not null) session.Send(aviso);
        return aviso is null;
    }

    /// <summary>Pone en la bóveda de <paramref name="p"/> lo que el Centro le debe y entra. Lo demás (la bóveda sin lugar)
    /// queda para la próxima.</summary>
    internal void EntregarLoDeLasSubastas(Player p)
    {
        _world.Subastas.Entregar(p.Name, e =>
        {
            if (e.Objeto != 0)
            {
                int bslot = FindBankSlotFor(p, e.Objeto, e.Cantidad);
                if (bslot == 0) return false;
                var bk = p.BankInventory[bslot];
                bk.ObjIndex = e.Objeto; bk.Amount = (short)(bk.Amount + e.Cantidad);
                p.BankInventory[bslot] = bk;
                if (p.BankOpen)
                    p.Connection?.Send(ServerMessages.ChangeBankSlot((byte)bslot, bk.ObjIndex, bk.Amount, _data.Obj(bk.ObjIndex)?.Value ?? 0));
                string nombre = _data.Obj(e.Objeto)?.Name ?? $"objeto {e.Objeto}";
                p.Connection?.Send(ServerMessages.ConsoleMsg($"El Centro de Subastas dejó en tu bóveda: {e.Cantidad} {nombre}.", FuenteInfo));
            }
            if (e.Oro > 0)
            {
                p.Bank += e.Oro;
                p.Connection?.Send(ServerMessages.ConsoleMsg($"El Centro de Subastas dejó en tu bóveda {e.Oro} monedas de oro.", FuenteInfo));
            }
            return true;
        });
    }

    /// <summary>A cada uno de los que el Centro les debe algo y están conectados.</summary>
    private void EntregarLoDeLasSubastasAlQueEsta()
    {
        foreach (var nombre in _world.Subastas.Pendientes.Select(e => e.Personaje).Distinct(StringComparer.OrdinalIgnoreCase).ToList())
            if (_world.PlayerByName(nombre) is { } p) EntregarLoDeLasSubastas(p);
    }

    /// <summary>Desde el loop: cierra las vencidas y entrega lo que se pueda. Una vez cada pocos segundos alcanza.</summary>
    public void SubastasAlDia()
    {
        long ahoraMs = Environment.TickCount64;
        if (ahoraMs - _ultimaRondaDeSubastasMs < 5_000) return;
        _ultimaRondaDeSubastasMs = ahoraMs;
        _world.Subastas.Vencer(RelojDeSubastas());
        if (_world.Subastas.Pendientes.Count > 0) EntregarLoDeLasSubastasAlQueEsta();
    }

    private bool SubastasSucias => _world.Subastas.Cambios != _subastasGuardadas;

    private void GuardarLasSubastas()
    {
        long enEsteVolcado = _world.Subastas.Cambios;
        try { _db.SaveSubastas(_world.Subastas); _subastasGuardadas = enEsteVolcado; }
        catch (Exception ex) { Console.Error.WriteLine($"[persistencia] subastas: {ex.Message}"); }
    }
}
