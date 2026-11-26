using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;

namespace CoverAO.Server.Net;

/// <summary>
/// LO QUE NO USA, A LA BÓVEDA. Pedido del tester (25/11): que los bots no pierdan todo al morir. Al terminar las compras en un pueblo
/// con banquero, pasado el novato, deja en la bóveda lo que se le caería al morir y no lleva puesto: armas, armaduras, cascos, escudos,
/// nudillos, anillos y objetos mágicos, y pieles (salvo el Sastre, que las trabaja); y de las pociones, la mitad de las rojas si pasa de
/// <see cref="RojasQueGuarda"/> y la mitad de las azules si pasa de <see cref="AzulesQueGuarda"/>. Lo que va a la subasta, las
/// herramientas de su oficio y el arco del que tira con arco quedan en la mochila. El oro ya lo deposita
/// <see cref="DepositarLoQueSobraAsync"/>.
/// </summary>
public sealed partial class Bot
{
    /// <summary>Desde cuántas rojas guarda la mitad.</summary>
    public const int RojasQueGuarda = 1_000;
    /// <summary>Desde cuántas azules guarda la mitad.</summary>
    public const int AzulesQueGuarda = 2_000;

    /// <summary>
    /// Qué guarda de la mochila: ranura y cantidad. <paramref name="queda"/> dice lo que se queda en la mochila aunque sea de lo que se
    /// guarda (lo de la subasta, la herramienta del oficio).
    /// </summary>
    public static List<(byte Ranura, short Obj, int Cantidad)> LoQueGuarda(GameData datos,
        IReadOnlyList<(byte Ranura, short Obj, short Cantidad, bool Puesto)> mochila, bool trabajaPieles, bool tiraConArco,
        Func<short, bool> queda, Func<short, bool>? delOficio = null)
    {
        var guarda = new List<(byte, short, int)>();
        foreach (var (ranura, obj, cantidad, puesto) in mochila)
        {
            // LO DEL OFICIO, aunque sea de lo que queda en la mochila; de lo puesto deja uno (el piquete de repuesto se apila sobre el
            // que usa, y el servidor desequipa sólo si se va la ranura entera).
            if (delOficio?.Invoke(obj) == true && cantidad - (puesto ? 1 : 0) is var deLasQueSobran and > 0
                && datos.Obj(obj) is { DropsOnDeath: true, IsNewbie: false })
            {
                guarda.Add((ranura, obj, deLasQueSobran));
                continue;
            }
            if (puesto || cantidad <= 0 || datos.Obj(obj) is not { DropsOnDeath: true, IsNewbie: false } o || o.IsPotion || queda(obj)) continue;
            bool equipo = o.IsWeapon || o.ObjType == 3 || o.IsKnuckles || o.IsMagicItem || o.ObjType == 47;
            bool piel = o.ObjType == 30 && !trabajaPieles;
            if (o.IsWeapon && tiraConArco && o.Proyectil > 0) continue;
            if (equipo || piel) guarda.Add((ranura, obj, cantidad));
        }
        MitadDe(3, RojasQueGuarda);
        MitadDe(4, AzulesQueGuarda);
        return guarda;

        // De las pociones de ese tipo, la mitad si pasan del tope, sacada de las ranuras en orden.
        void MitadDe(int subTipo, int tope)
        {
            var ranuras = mochila.Where(r => r.Cantidad > 0 && datos.Obj(r.Obj) is { IsPotion: true, DropsOnDeath: true } p
                                             && p.SubType == subTipo && !queda(r.Obj)).ToList();
            int total = ranuras.Sum(r => r.Cantidad);
            if (total <= tope) return;
            int falta = total / 2;
            foreach (var r in ranuras)
            {
                if (falta <= 0) break;
                int va = Math.Min(falta, r.Cantidad);
                guarda.Add((r.Ranura, r.Obj, va));
                falta -= va;
            }
        }
    }

    /// <summary>Lo que se queda en la mochila aunque sea de lo que se guarda: lo que va a la subasta, lo que produce el oficio para venderlo
    /// y las herramientas del oficio.</summary>
    private bool QuedaEnLaMochila(SentidosDelBot sentidos, short obj) =>
        EsHerramientaDeSuOficio(obj) || ParaPublicar(sentidos, obj) || (EsProduccionMetalurgica(obj) && !GuardaLoDelOficio(sentidos, obj))
        || _comprados.Contains(obj);

    /// <summary>Al terminar las compras en un pueblo con banquero: abre la bóveda y deja <see cref="LoQueGuarda"/>.</summary>
    private async Task GuardarLoQueNoUsaAsync(TransporteDeBot transporte, SentidosDelBot sentidos, Task cerrada, CancellationToken ct)
    {
        if (sentidos.Nivel <= GameLimits.NewbieMaxLevel || !sentidos.Vivo || _acompana is not null || !TieneBanquero(sentidos.Mapa)
            || cerrada.IsCompleted || ct.IsCancellationRequested) return;
        List<(byte Ranura, short Obj, int Cantidad)> Que() => LoQueGuarda(_entorno.Datos, sentidos.Mochila(), _clase == ClaseSastre,
            TiraConArco(_clase), obj => QuedaEnLaMochila(sentidos, obj), obj => GuardaLoDelOficio(sentidos, obj));
        if (Que().Count == 0) return;
        int mapa = sentidos.Mapa;
        bool SigueEnElBanco() => sentidos.Vivo && !cerrada.IsCompleted && !ct.IsCancellationRequested && sentidos.Mapa == mapa;
        if (!await AbrirElBancoAsync(transporte, sentidos, cerrada, ct, () => _acompana is not null) || !SigueEnElBanco()) return;
        bool pidioAbrir = false;
        try
        {
            int vistas = sentidos.VecesQueVioLaBoveda;
            if (!(pidioAbrir = transporte.Mandar(Paquete(ClientPacketId.BankStart, _ => { })))) return;
            for (int espera = 0; espera < 12 && sentidos.VecesQueVioLaBoveda == vistas && SigueEnElBanco(); espera++) await Task.Delay(250, ct);
            if (!SigueEnElBanco() || sentidos.VecesQueVioLaBoveda == vistas) return;
            // La lista se arma UNA vez: depositar no corre las otras ranuras (`HandleBankDeposit`), y rearmarla después de guardar la
            // mitad de las rojas volvería a ver más del tope y guardaría la mitad de la mitad.
            foreach (var (ranura, obj, cantidad) in Que())
            {
                if (!SigueEnElBanco() || sentidos.QueHayEn(ranura) != obj) continue;
                int antes = sentidos.CuantoTiene(o => o == obj);
                if (!transporte.Mandar(Paquete(ClientPacketId.BankDeposit, w => w.WriteByte(ranura).WriteInteger((short)Math.Min(cantidad, short.MaxValue))))) return;
                for (int espera = 0; espera < 12 && sentidos.CuantoTiene(o => o == obj) == antes && SigueEnElBanco(); espera++) await Task.Delay(250, ct);
                if (sentidos.CuantoTiene(o => o == obj) >= antes) break;   // la bóveda llena o no atendió: lo que queda, otra vez
                Console.WriteLine($"[bot {Nombre}] guarda en la bóveda {antes - sentidos.CuantoTiene(o => o == obj)} de obj {obj} en m{mapa}");
                await Task.Delay(_rng.Next(300, 700), ct);
            }
        }
        finally
        {
            if (pidioAbrir && !cerrada.IsCompleted) transporte.Mandar(Paquete(ClientPacketId.BankEnd, _ => { }));
        }
    }
}
