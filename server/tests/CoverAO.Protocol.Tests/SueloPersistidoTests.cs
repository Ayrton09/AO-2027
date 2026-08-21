using CoverAO.Server.Data;
using CoverAO.Server.Game;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// LA REGLA DE LA CASA <c>PersistirSuelo</c> (informe del tester, 2.27): que lo que quedó tirado sobreviva a un
/// reinicio. El original no lo hace — su suelo vive en <c>MapData().ObjInfo</c> y se repuebla del .csm en cada
/// arranque—, así que esto es un agregado y viene apagado. <see cref="ReglasDeLaCasaTests"/> fija el default.
///
/// <para>Lo que fija este archivo es el COMPORTAMIENTO, no que la clave se lea: un test que sólo comprobara
/// "el interruptor está prendido" pasaría con un guardado que no guarda nada. Los tres que pueden caerse de
/// verdad son negativos — que el decorado del mapa NO viaje, que las puertas NO viajen, y que restaurar no
/// pise lo que el .csm ya puso.</para>
/// </summary>
public class SueloPersistidoTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "coverao_suelo_" + Guid.NewGuid().ToString("N"));
    private readonly Persistence _db;
    private readonly FileStore _store;

    public SueloPersistidoTests()
    {
        Directory.CreateDirectory(_root);
        _store = new FileStore(_root);
        _db = new Persistence(_store, new Leveling(new GameData().LevelExp));
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private static WorldPos P(int map, int x, int y) => new((short)map, (byte)x, (byte)y);

    [Fact]
    public void Lo_que_alguien_dejo_en_el_suelo_vuelve_despues_del_reinicio()
    {
        var antes = new World();
        antes.SetGround(P(1, 50, 50), objIndex: 38, amount: 3);      // tres pociones rojas
        antes.SetGround(P(1, 51, 50), objIndex: 12, amount: 1);
        antes.SetGround(P(34, 20, 20), objIndex: 7, amount: 900);    // otro mapa, y una pila grande

        _db.SaveSuelo(antes.SueloNoFijo());

        // El reinicio: un mundo nuevo, vacío, como sale de arrancar el proceso.
        var despues = new World();
        var (puestos, descartados) = WorldPopulator.RestoreSuelo(despues, _db.LoadSuelo());

        Assert.Equal(3, puestos);
        Assert.Equal(0, descartados);
        Assert.True(despues.TryGetGround(P(1, 50, 50), out var pociones));
        Assert.Equal((short)38, pociones.ObjIndex);
        Assert.Equal((short)3, pociones.Amount);
        Assert.True(despues.TryGetGround(P(34, 20, 20), out var pila));
        Assert.Equal((short)900, pila.Amount);
    }

    /// <summary>EL NEGATIVO QUE IMPORTA. El decorado del mapa —árboles, carteles, muebles: ~30 mil tiles— se
    /// repuebla del .csm en cada arranque. Si se colara en el archivo del suelo, cada guardado escribiría el
    /// mundo entero en vez de un puñado de drops, y al restaurar quedaría duplicado sobre lo que el .csm ya
    /// puso. El filtro es <c>_groundFijo</c>, y sacarlo tiene que poner esto en rojo.</summary>
    [Fact]
    public void El_decorado_del_mapa_no_se_guarda()
    {
        var w = new World();
        w.SetGround(P(1, 10, 10), objIndex: 500, amount: 1, esFijo: true);   // un árbol del .csm
        w.SetGround(P(1, 11, 10), objIndex: 501, amount: 1, esFijo: true);
        w.SetGround(P(1, 12, 10), objIndex: 38, amount: 1);                  // lo que dejó un jugador

        var guardado = w.SueloNoFijo().ToList();

        Assert.Single(guardado);
        Assert.Equal((short)38, guardado[0].ObjIndex);
        Assert.Equal(P(1, 12, 10), guardado[0].Pos);
    }

    /// <summary>Un tile que dejó de ser fijo vuelve a contar, y uno que pasó a serlo deja de contar. No es
    /// hipotético: <c>SetGround</c> es el mismo camino para el drop de un jugador y para el decorado del .csm,
    /// y la bandera se pisa en cada llamada.</summary>
    [Fact]
    public void La_bandera_de_fijo_manda_en_cada_escritura()
    {
        var w = new World();
        w.SetGround(P(1, 5, 5), 38, 1);                       // drop
        Assert.Single(w.SueloNoFijo());
        w.SetGround(P(1, 5, 5), 500, 1, esFijo: true);        // el .csm lo pisa
        Assert.Empty(w.SueloNoFijo());
        w.SetGround(P(1, 5, 5), 38, 1);                       // y vuelve a ser un drop
        Assert.Single(w.SueloNoFijo());
    }

    /// <summary>EL OTRO NEGATIVO: restaurar no puede pisar lo que el .csm acaba de colocar. Si el mapa cambió
    /// entre dos arranques, el decorado nuevo gana y el drop viejo se descarta — el mundo con un agujero en su
    /// propio decorado es permanente; la espada perdida, no.</summary>
    [Fact]
    public void Restaurar_no_pisa_el_decorado_que_ya_puso_el_mapa()
    {
        var w = new World();
        w.SetGround(P(1, 30, 30), objIndex: 500, amount: 1, esFijo: true);   // el árbol nuevo del .csm

        var guardadoViejo = new[] { (Pos: P(1, 30, 30), ObjIndex: (short)38, Amount: (short)5),
                                    (Pos: P(1, 31, 30), ObjIndex: (short)12, Amount: (short)1) };
        var (puestos, descartados) = WorldPopulator.RestoreSuelo(w, guardadoViejo);

        Assert.Equal(1, puestos);
        Assert.Equal(1, descartados);
        Assert.True(w.TryGetGround(P(1, 30, 30), out var tile));
        Assert.Equal((short)500, tile.ObjIndex);      // sigue siendo el árbol, no las pociones
    }

    /// <summary>EQUIVALENCIA. <c>SueloNoFijo()</c> dejó de recorrer los 30.694 tiles del suelo filtrando por
    /// <c>_groundFijo</c> y ahora lee un índice que se mantiene al día en los dos mutadores — 2,535 ms por
    /// volcado contra 0,006, y eso corre en el hilo del game loop. Un refactor así no puede cambiar NADA de lo
    /// que devuelve, y lo que lo rompería no es el camino feliz: es un tile que cambia de bando (drop →
    /// decorado → drop) y queda en los dos conjuntos o en ninguno.
    ///
    /// <para>La secuencia es determinista y mezcla las tres operaciones sobre los MISMOS tiles a propósito. La
    /// expectativa la lleva el test por su cuenta, así que no compara el código contra sí mismo.</para></summary>
    [Fact]
    public void El_indice_dice_exactamente_lo_mismo_que_recorrer_todo()
    {
        var w = new World();
        var esperado = new Dictionary<WorldPos, (short Obj, short Amount)>();
        int semilla = 12345;
        int Siguiente(int tope) { semilla = (semilla * 1103515245 + 12345) & 0x7FFFFFFF; return semilla % tope; }

        for (int paso = 0; paso < 4000; paso++)
        {
            var pos = P(1 + Siguiente(3), 1 + Siguiente(20), 1 + Siguiente(20));
            switch (Siguiente(3))
            {
                case 0:   // un drop: cuenta
                    w.SetGround(pos, 38, 2);
                    esperado[pos] = (38, 2);
                    break;
                case 1:   // decorado del .csm: NO cuenta, y saca el tile si antes era un drop
                    w.SetGround(pos, 500, 1, esFijo: true);
                    esperado.Remove(pos);
                    break;
                default:  // levantarlo
                    w.RemoveGround(pos);
                    esperado.Remove(pos);
                    break;
            }
        }

        var obtenido = w.SueloNoFijo().ToDictionary(t => t.Pos, t => (t.ObjIndex, t.Amount));
        Assert.Equal(esperado.Count, obtenido.Count);
        foreach (var (pos, valor) in esperado)
        {
            Assert.True(obtenido.TryGetValue(pos, out var v), $"falta el tile {pos}");
            Assert.Equal(valor, v);
        }
        // La premisa: que la secuencia haya dejado algo Y haya ejercido las tres ramas. Con un `esperado`
        // vacío los asserts de arriba pasarían sin comparar nada.
        Assert.True(esperado.Count > 20, $"la secuencia dejó sólo {esperado.Count} tiles: no probó gran cosa");
    }

    /// <summary>El contador que decide si hay algo que volcar. Va en <c>World</c> —el único lugar por donde
    /// pasan las dos mutaciones— para que un call site nuevo no pueda olvidarse de marcar el sucio.</summary>
    [Fact]
    public void Cada_cambio_del_suelo_mueve_el_contador()
    {
        var w = new World();
        long cero = w.SueloCambios;
        w.SetGround(P(1, 1, 1), 38, 1);
        Assert.True(w.SueloCambios > cero);
        long trasPoner = w.SueloCambios;
        w.RemoveGround(P(1, 1, 1));                   // levantarlo también cuenta: el archivo tiene que perderlo
        Assert.True(w.SueloCambios > trasPoner);
    }

    /// <summary>Un archivo roto NO puede impedir que arranque el servidor. El suelo no es autoridad de nada
    /// —el mundo se puebla igual del .csm—, así que lo correcto es perderlo y seguir.</summary>
    /// <summary>Los dos tests que ensucian el archivo escriben por <see cref="IStore.WriteMisc"/> y NO con un
    /// <c>File.WriteAllText</c> a una ruta armada a mano. Costó descubrirlo: <c>FileStore</c> pasa la clave a
    /// MAYÚSCULAS (<c>Misc/SUELO.JSON</c>), y un archivo escrito como <c>suelo.json</c> lo encuentra Windows
    /// —que no distingue mayúsculas— pero no Linux, donde el server también corre. Ahí el test pasaría porque
    /// el archivo no existe, o sea un VERDE que no probó nada.</summary>
    private void EnsuciarElArchivo(string contenido) => _store.WriteMisc("suelo", contenido);

    [Fact]
    public void Un_archivo_ilegible_no_tumba_el_arranque()
    {
        EnsuciarElArchivo("{esto no es json");
        // La premisa: que el archivo EXISTA y tenga eso adentro. Sin esto, "devolvió vacío" tambien seria el
        // resultado de no haber escrito nada.
        Assert.Equal("{esto no es json", _store.ReadMisc("suelo"));
        Assert.Empty(_db.LoadSuelo());
    }

    /// <summary>Filas con basura se saltean una por una en vez de tumbar el resto. El archivo lo escribe el
    /// server, pero es texto en el disco del host y alguien lo puede editar.</summary>
    [Fact]
    public void Las_filas_invalidas_se_descartan_sin_llevarse_las_buenas()
    {
        EnsuciarElArchivo(
            """{"1":[[50,50,38,2],[300,50,38,1],[51,50,0,1],[52,50,38,0],[53,50],[54,50,38,7]],"0":[[1,1,38,1]]}""");

        var leido = _db.LoadSuelo();

        // Entran la primera y la última: x=300 no es un byte, objIndex 0 y amount 0 no existen, la fila corta
        // no tiene amount, y el mapa 0 no es un mapa.
        Assert.Equal(2, leido.Count);
        Assert.Contains(leido, t => t.Pos == P(1, 50, 50) && t.Amount == 2);
        Assert.Contains(leido, t => t.Pos == P(1, 54, 50) && t.Amount == 7);
    }

    /// <summary>Sin archivo —el primer arranque con la regla recién prendida— no hay suelo y no hay ruido.</summary>
    [Fact]
    public void Sin_archivo_el_suelo_arranca_vacio()
    {
        Assert.Empty(_db.LoadSuelo());
    }
}
