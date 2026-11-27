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
    ///
    /// <para>LO MEJOR QUE TIENE PARA PONERSE (<paramref name="loMejor"/>) TAMPOCO, aunque todavía no lo lleve puesto: de eso, y de lo
    /// puesto, deja uno y guarda los repetidos. Medido en la base del tester (27/11): al salir de la tienda el bot guardaba lo que
    /// acababa de comprar antes de ponérselo, y en el viaje siguiente lo volvía a comprar. Gonzo tenía 23 Spathas y 23 Escudos de
    /// Hierro +2 en la bóveda; Ciro, 66 Escudos de Hierro +2 apilados en el que llevaba puesto.</para>
    /// </summary>
    public static List<(byte Ranura, short Obj, int Cantidad)> LoQueGuarda(GameData datos,
        IReadOnlyList<(byte Ranura, short Obj, short Cantidad, bool Puesto)> mochila, bool trabajaPieles, bool tiraConArco,
        Func<short, bool> queda, Func<short, bool>? delOficio = null, Func<short, bool>? loMejor = null)
    {
        var guarda = new List<(byte, short, int)>();
        // De lo mejor queda uno: el puesto si hay, y si no, el de la primera ranura.
        var yaQueda = new HashSet<short>(mochila.Where(r => r.Puesto).Select(r => r.Obj));
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
            if (cantidad <= 0 || datos.Obj(obj) is not { DropsOnDeath: true, IsNewbie: false } o || o.IsPotion || queda(obj)) continue;
            bool equipo = o.IsWeapon || o.ObjType == 3 || o.IsKnuckles || o.IsMagicItem || o.ObjType == 47;
            if (puesto || loMejor?.Invoke(obj) == true)
            {
                // Del puesto y de lo mejor, los repetidos: el servidor desequipa sólo si se va la ranura entera.
                int sobran = puesto || !yaQueda.Add(obj) ? cantidad - (puesto ? 1 : 0) : cantidad - 1;
                if (equipo && sobran > 0) guarda.Add((ranura, obj, sobran));
                continue;
            }
            // LAS PIELES DEL SASTRE NO: se publican en lotes para él (ver `EsPielParaVender`).
            bool piel = o.ObjType == 30 && !trabajaPieles && !EsPielDeSastre(obj);
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
        || EsCosturaParaElMercader(obj) || _comprados.Contains(obj);

    /// <summary>Lo que se pone el que tira con arco: el arco en vez del arma de mano.</summary>
    private static readonly Func<ObjInfo, bool>[] QueSePoneElArquero = [o => o.IsArmor, o => o.IsShield, o => o.IsHelmet, o => o.IsBow];

    /// <summary>Las clases de equipo que se pone este bot (<see cref="QueSePone"/>, o <see cref="QueSePoneElArquero"/>).</summary>
    private Func<ObjInfo, bool>[] SuEquipo => TiraConArco(_clase) ? QueSePoneElArquero : QueSePone;

    /// <summary>¿Es lo mejor que tiene de alguna clase de equipo que se pone, y se lo puede poner?</summary>
    private bool EsLoMejorParaPonerse(SentidosDelBot s, short obj) =>
        _entorno.Datos.Obj(obj) is { } o && LoPuedeUsar(o, s.Nivel) && !_noSeLoPone.Contains(obj)
        && SuEquipo.Any(es => es(o) && EsLoMejorQueTieneDeEsaClase(ValorDeCombate(o), LoMejorQueTiene(s, es)));

    /// <summary>
    /// LO QUE SACA DE LA BÓVEDA PARA PONERSE: de cada clase de equipo, lo que más vale de lo que se puede poner, si vale más que lo mejor
    /// que tiene encima. Una ranura por objeto.
    /// </summary>
    public static List<(byte Ranura, short Obj)> EquipoParaRetirar(GameData datos, IEnumerable<(byte Ranura, short Obj, short Cantidad)> boveda,
        IReadOnlyList<Func<ObjInfo, bool>> clases, Func<ObjInfo, bool> sePuedePoner, Func<Func<ObjInfo, bool>, int> loMejorQueTiene)
    {
        var enLaBoveda = boveda.Where(b => b.Cantidad > 0).Select(b => (b.Ranura, O: datos.Obj(b.Obj))).Where(b => b.O is not null).ToList();
        var retira = new List<(byte, short)>();
        foreach (var es in clases)
        {
            var mejor = enLaBoveda.Where(b => es(b.O!) && sePuedePoner(b.O!) && ValorDeCombate(b.O!) > 0)
                .OrderByDescending(b => ValorDeCombate(b.O!)).FirstOrDefault();
            if (mejor.O is { } m && ValorDeCombate(m) > loMejorQueTiene(es) && !retira.Any(r => r.Item2 == m.Index))
                retira.Add((mejor.Ranura, m.Index));
        }
        return retira;
    }

    private List<(byte Ranura, short Obj)> EquipoParaRetirar(SentidosDelBot s) =>
        EquipoParaRetirar(_entorno.Datos, s.Boveda(), SuEquipo, o => LoPuedeUsar(o, s.Nivel) && !_noSeLoPone.Contains(o.Index),
                          es => LoMejorQueTiene(s, es));

    private long _proximaMiradaDeLaBoveda;
    private const int EsperaDeLaBovedaMs = 90_000;

    /// <summary>
    /// SACAR EL EQUIPO DE LA BÓVEDA. Decisión del autor (17/10): el que revive sin nada retira de la bóveda lo que haga falta. Se hacía
    /// sólo con el oro. Medido en la base del tester (27/11): unos veinte bots desnudos, con 0 a 4 monedas y la bóveda llena de armas y
    /// armaduras (Gonzo, Fito, Karim, Chela, Paula, Mateo). En un mapa con banquero, si nunca miró la bóveda en esta sesión o si lo que
    /// vio ahí lo mejora, la abre y saca una pieza de cada clase que le falte o que sea mejor. Ponérsela es cosa de <see cref="Equiparse"/>.
    /// </summary>
    private async Task<bool> SacarElEquipoDeLaBovedaAsync(TransporteDeBot transporte, SentidosDelBot sentidos, Task cerrada, CancellationToken ct)
    {
        if (sentidos.Nivel <= GameLimits.NewbieMaxLevel || !sentidos.Vivo || _acompana is not null || sentidos.Comerciando
            || Environment.TickCount64 < _proximaMiradaDeLaBoveda || cerrada.IsCompleted || ct.IsCancellationRequested) return false;
        int libres = GameLimits.MaxInventorySlots - sentidos.RanurasOcupadas().Count;
        if (libres <= 0 || (sentidos.VecesQueVioLaBoveda > 0 && EquipoParaRetirar(sentidos).Count == 0) || !TieneBanquero(sentidos.Mapa))
            return false;
        _proximaMiradaDeLaBoveda = Environment.TickCount64 + EsperaDeLaBovedaMs;
        int mapa = sentidos.Mapa;
        bool SigueEnElBanco() => sentidos.Vivo && !cerrada.IsCompleted && !ct.IsCancellationRequested && sentidos.Mapa == mapa;
        if (!await AbrirElBancoAsync(transporte, sentidos, cerrada, ct, () => _acompana is not null) || !SigueEnElBanco()) return true;
        bool pidioAbrir = false;
        try
        {
            int vistas = sentidos.VecesQueVioLaBoveda;
            if (!(pidioAbrir = transporte.Mandar(Paquete(ClientPacketId.BankStart, _ => { })))) return true;
            for (int espera = 0; espera < 12 && sentidos.VecesQueVioLaBoveda == vistas && SigueEnElBanco(); espera++) await Task.Delay(250, ct);
            if (!SigueEnElBanco() || sentidos.VecesQueVioLaBoveda == vistas) return true;
            foreach (var (ranura, obj) in EquipoParaRetirar(sentidos).Take(libres))
            {
                if (!SigueEnElBanco()) return true;
                int antes = sentidos.CuantoTiene(o => o == obj);
                if (!transporte.Mandar(Paquete(ClientPacketId.BankExtractItem, w => w.WriteByte(ranura).WriteInteger(1)))) return true;
                for (int espera = 0; espera < 12 && sentidos.CuantoTiene(o => o == obj) == antes && SigueEnElBanco(); espera++) await Task.Delay(250, ct);
                if (sentidos.CuantoTiene(o => o == obj) > antes) Console.WriteLine($"[bot {Nombre}] saca de la bóveda obj {obj} para ponérselo en m{mapa}");
                await Task.Delay(_rng.Next(400, 900), ct);
            }
        }
        finally
        {
            if (pidioAbrir && !cerrada.IsCompleted) transporte.Mandar(Paquete(ClientPacketId.BankEnd, _ => { }));
        }
        return true;
    }

    /// <summary>Al terminar las compras en un pueblo con banquero: abre la bóveda y deja <see cref="LoQueGuarda"/>.</summary>
    private async Task GuardarLoQueNoUsaAsync(TransporteDeBot transporte, SentidosDelBot sentidos, Task cerrada, CancellationToken ct)
    {
        if (sentidos.Nivel <= GameLimits.NewbieMaxLevel || !sentidos.Vivo || _acompana is not null || !TieneBanquero(sentidos.Mapa)
            || cerrada.IsCompleted || ct.IsCancellationRequested) return;
        List<(byte Ranura, short Obj, int Cantidad)> Que() => LoQueGuarda(_entorno.Datos, sentidos.Mochila(), _clase == ClaseSastre,
            TiraConArco(_clase), obj => QuedaEnLaMochila(sentidos, obj), obj => GuardaLoDelOficio(sentidos, obj),
            obj => EsLoMejorParaPonerse(sentidos, obj));
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
