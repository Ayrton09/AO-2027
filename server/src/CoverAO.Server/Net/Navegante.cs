using CoverAO.Server.Data;
using CoverAO.Server.Game;

namespace CoverAO.Server.Net;

/// <summary>
/// SABER IR A UN LUGAR. Dos cosas: un camino de baldosas adentro de un mapa, y qué salida tomar para llegar
/// a otro mapa.
///
/// <para><b>Por qué existe.</b> Sin esto los bots vivían en una burbuja de veinte baldosas alrededor de donde
/// nacían, cazando criaturas que no sueltan nada — medido: cero oro después de nueve minutos, y el comercio
/// entero sin poder dispararse nunca. Y los oficios ni hablar: la caña de pescar no cuesta nada, pero el que
/// la vende está en OTRO MAPA. Todo lo que le faltaba al bot para ser un jugador dependía de poder ir a algún
/// lado a propósito.</para>
///
/// <para><b>De dónde sale lo que sabe.</b> De los mapas del juego: qué baldosa está bloqueada
/// (<see cref="GameData.IsBlockedTile"/>) y a dónde lleva cada salida (<see cref="GameData.MapExits"/>). Eso
/// NO es espiar al servidor: es exactamente lo que trae el archivo de mapa del cliente, que es con lo que una
/// persona ve la pared y la puerta antes de caminar hacia ellas. Lo que el bot sigue sin saber es lo que la
/// pantalla no muestra — quién hay del otro lado, o si la criatura se movió.</para>
///
/// <para>Se construye UNA vez y la comparten todos los bots: después de armarse no se toca, así que se puede
/// leer desde cualquier hilo.</para>
/// </summary>
public sealed class Navegante
{
    /// <summary>Tope de baldosas que se exploran buscando un camino. Un mapa entero son 10.000, así que esto
    /// no recorta ningún viaje real; está para que un destino encerrado detrás de una pared no cueste el mapa
    /// completo cada vez que se pregunta.</summary>
    private const int TopeDeExploracion = 6_000;

    /// <summary>Tope de mapas que se atraviesan en un viaje. Cinco alcanzan para ir de una ciudad a la de al
    /// lado; más que eso es un viaje que un personaje de nivel uno no haría.</summary>
    private const int TopeDeMapas = 5;

    private readonly GameData _datos;

    /// <summary>Por mapa, sus salidas: a qué mapa llevan y en qué baldosa se pisan.</summary>
    private readonly Dictionary<int, List<(int Destino, byte X, byte Y)>> _salidas = new();

    public Navegante(GameData datos)
    {
        _datos = datos;
        foreach (var (desde, hasta) in datos.MapExits)
        {
            // Las salidas que caen en el MISMO mapa son teleports internos: no sirven para viajar y meterlas
            // en el grafo haría que el bot "cambie de mapa" hacia donde ya está.
            if (hasta.DestMap == desde.Map || hasta.DestMap <= 0) continue;
            if (!_salidas.TryGetValue(desde.Map, out var lista)) _salidas[desde.Map] = lista = new();
            lista.Add((hasta.DestMap, (byte)desde.X, (byte)desde.Y));
        }
    }

    /// <summary>Cuántos mapas tienen salida conocida. Sirve para que el arranque pueda decir si el mundo que
    /// cargó trae la información de viaje.</summary>
    public int MapasConSalida => _salidas.Count;

    /// <summary>
    /// El camino de baldosas de <paramref name="desde"/> a <paramref name="hasta"/> adentro de un mapa, sin
    /// incluir la baldosa de partida. null si no hay camino.
    ///
    /// <para>Es una búsqueda en anchura sobre las cuatro direcciones, que es como camina el juego: no hay
    /// diagonales. Devuelve el camino entero y no sólo el primer paso porque el bot lo recorre revisando su
    /// vida en cada paso, y recalcular en cada baldosa sería tirar el trabajo.</para>
    /// </summary>
    public IReadOnlyList<(byte X, byte Y)>? Camino(int mapa, (byte X, byte Y) desde, (byte X, byte Y) hasta)
    {
        if (desde == hasta) return Array.Empty<(byte, byte)>();
        if (!SePuedePisar(mapa, hasta.X, hasta.Y)) return null;

        var deDonde = new Dictionary<int, int>();
        var cola = new Queue<int>();
        int inicio = Clave(desde.X, desde.Y), fin = Clave(hasta.X, hasta.Y);
        cola.Enqueue(inicio);
        deDonde[inicio] = -1;

        int vistos = 0;
        while (cola.Count > 0 && vistos++ < TopeDeExploracion)
        {
            int actual = cola.Dequeue();
            if (actual == fin) return Reconstruir(deDonde, fin);

            byte x = (byte)(actual & 0xff), y = (byte)(actual >> 8);
            for (byte rumbo = 1; rumbo <= 4; rumbo++)
            {
                var (dx, dy) = SentidosDelBot.Paso(rumbo);
                int nx = x + dx, ny = y + dy;
                if (!SePuedePisar(mapa, nx, ny)) continue;
                int vecino = Clave((byte)nx, (byte)ny);
                if (deDonde.ContainsKey(vecino)) continue;
                deDonde[vecino] = actual;
                cola.Enqueue(vecino);
            }
        }
        return null;
    }

    /// <summary>
    /// En qué baldosa de ESTE mapa hay que pararse para acercarse a <paramref name="mapaDestino"/>, y a
    /// cuántos mapas de distancia queda. null si no se llega en <see cref="TopeDeMapas"/> saltos.
    ///
    /// <para>Es una búsqueda en anchura sobre el grafo de mapas: encuentra la ruta entera y devuelve la
    /// primera puerta, que es lo único que el bot necesita para dar el próximo paso del viaje.</para>
    /// </summary>
    public ((byte X, byte Y) Puerta, int Saltos)? PuertaHacia(int mapaActual, int mapaDestino)
    {
        if (mapaActual == mapaDestino) return null;

        // BFS sobre mapas, recordando por qué puerta DE ESTE MAPA se entró a cada rama.
        var visto = new HashSet<int> { mapaActual };
        var cola = new Queue<(int Mapa, (byte X, byte Y) PrimeraPuerta, int Saltos)>();
        foreach (var (destino, x, y) in Salidas(mapaActual))
        {
            if (!visto.Add(destino)) continue;
            if (destino == mapaDestino) return ((x, y), 1);
            cola.Enqueue((destino, (x, y), 1));
        }

        while (cola.Count > 0)
        {
            var (mapa, puerta, saltos) = cola.Dequeue();
            if (saltos >= TopeDeMapas) continue;
            foreach (var (destino, _, _) in Salidas(mapa))
            {
                if (!visto.Add(destino)) continue;
                if (destino == mapaDestino) return (puerta, saltos + 1);
                cola.Enqueue((destino, puerta, saltos + 1));
            }
        }
        return null;
    }

    /// <summary>
    /// DÓNDE HAY UN NPC ASÍ. Busca primero en el mapa donde está y después en los que se alcanzan viajando,
    /// en orden de cercanía. Devuelve dónde aparece y a cuántos mapas queda.
    ///
    /// <para>Sale de los puntos de aparición del mundo, que es lo que hace que el bot pueda ir a comprar en
    /// vez de esperar a cruzarse un mercader de casualidad. Un jugador tiene el mismo conocimiento: sabe
    /// dónde está el herrero de su ciudad porque lo vio una vez.</para>
    /// </summary>
    public (int Mapa, byte X, byte Y, int Saltos)? DondeHay(int mapaActual, (byte X, byte Y) desde,
                                                           Func<int, bool> sirve)
    {
        // EN EL MAPA PROPIO SE EXIGE QUE SE PUEDA LLEGAR CAMINANDO. Muchos NPC están adentro de un edificio, y
        // devolver el primero que cumple sin mirar el camino mandaba al bot a una puerta que no existe: medido,
        // el vendedor de cañas del mapa 34 está en una baldosa libre y SIN CAMINO desde afuera, así que el
        // viaje fallaba siempre y el bot no compró nunca su herramienta. Se prueban todos los del mapa y se
        // elige el más cercano AL QUE SE LLEGA.
        if (EnElMapa(mapaActual, desde, sirve) is { } aca) return (mapaActual, aca.X, aca.Y, 0);

        // Mismo recorrido que PuertaHacia, pero mirando qué hay en cada mapa que se alcanza.
        var visto = new HashSet<int> { mapaActual };
        var cola = new Queue<(int Mapa, int Saltos)>();
        foreach (var (destino, _, _) in Salidas(mapaActual))
            if (visto.Add(destino)) cola.Enqueue((destino, 1));

        while (cola.Count > 0)
        {
            var (mapa, saltos) = cola.Dequeue();
            // En un mapa donde todavía no está no se puede verificar el camino —no se sabe por dónde va a
            // entrar—, así que ahí vale el primero. Al llegar, el bot vuelve a preguntar desde su posición y
            // ahí sí se exige camino.
            if (EnElMapa(mapa, null, sirve) is { } ahi) return (mapa, ahi.X, ahi.Y, saltos);
            if (saltos >= TopeDeMapas) continue;
            foreach (var (destino, _, _) in Salidas(mapa))
                if (visto.Add(destino)) cola.Enqueue((destino, saltos + 1));
        }
        return null;
    }

    /// <summary>
    /// LA BALDOSA DE TRABAJO MÁS CERCANA: agua para pescar, o un árbol / yacimiento para el hacha y el
    /// piquete. Devuelve dónde está y a cuántos pasos, mirando sólo alrededor de <paramref name="desde"/>.
    ///
    /// <para>El agua sale del terreno del mapa y los árboles y yacimientos de los objetos que el mapa trae
    /// colocados — las dos cosas están en el archivo de mapa que tiene el cliente, y es con eso que una
    /// persona ve el río antes de caminar hasta él. El servidor NO manda el decorado fijo por la red, así que
    /// sin esto un bot no tendría forma de saber dónde pescar.</para>
    /// </summary>
    public ((byte X, byte Y) Donde, int Pasos)? DondeTrabajar(int mapa, (byte X, byte Y) desde, int radio,
                                                              bool agua, Func<int, bool>? esRecurso = null)
    {
        ((byte, byte), int)? mejor = null;
        void Probar(int x, int y)
        {
            int pasos = Math.Abs(x - desde.X) + Math.Abs(y - desde.Y);
            if (pasos > radio) return;
            if (mejor is not null && pasos >= mejor.Value.Item2) return;
            mejor = (((byte)x, (byte)y), pasos);
        }

        if (agua)
        {
            for (int x = Math.Max(GameLimits.MinXBorder, desde.X - radio); x <= Math.Min(GameLimits.MaxXBorder, desde.X + radio); x++)
            for (int y = Math.Max(GameLimits.MinYBorder, desde.Y - radio); y <= Math.Min(GameLimits.MaxYBorder, desde.Y + radio); y++)
                if (_datos.IsWater(mapa, x, y)) Probar(x, y);
        }
        else if (esRecurso is not null && _datos.MapObjects.TryGetValue(mapa, out var objetos))
        {
            foreach (var o in objetos)
                if (esRecurso(o.ObjIndex)) Probar(o.X, o.Y);
        }
        return mejor;
    }

    /// <summary>El mapa más cercano donde hay dónde ejercer ese oficio, y en qué baldosa. Sirve para IR a
    /// trabajar cuando en el mapa donde está no hay ni agua ni yacimientos.</summary>
    public (int Mapa, byte X, byte Y, int Saltos)? DondeSeTrabaja(int mapaActual, bool agua,
                                                                  Func<int, bool>? esRecurso = null)
    {
        var centro = ((byte)50, (byte)50);
        if (DondeTrabajar(mapaActual, centro, 90, agua, esRecurso) is { } aca)
            return (mapaActual, aca.Donde.X, aca.Donde.Y, 0);

        var visto = new HashSet<int> { mapaActual };
        var cola = new Queue<(int Mapa, int Saltos)>();
        foreach (var (destino, _, _) in Salidas(mapaActual))
            if (visto.Add(destino)) cola.Enqueue((destino, 1));

        while (cola.Count > 0)
        {
            var (mapa, saltos) = cola.Dequeue();
            if (DondeTrabajar(mapa, centro, 90, agua, esRecurso) is { } ahi)
                return (mapa, ahi.Donde.X, ahi.Donde.Y, saltos);
            if (saltos >= TopeDeMapas) continue;
            foreach (var (destino, _, _) in Salidas(mapa))
                if (visto.Add(destino)) cola.Enqueue((destino, saltos + 1));
        }
        return null;
    }

    /// <summary>El que cumple en ese mapa. Con <paramref name="desde"/> se exige además que haya camino, y de
    /// los que lo tienen se elige el más cercano.</summary>
    private (byte X, byte Y)? EnElMapa(int mapa, (byte X, byte Y)? desde, Func<int, bool> sirve)
    {
        if (!_datos.MapSpawns.TryGetValue(mapa, out var lista)) return null;
        (byte X, byte Y)? mejor = null;
        int mejorPasos = int.MaxValue;
        foreach (var s in lista)
        {
            if (!sirve(s.NpcIndex)) continue;
            var donde = ((byte)s.X, (byte)s.Y);
            if (desde is not { } d) return donde;
            int pasos = Math.Abs(s.X - d.X) + Math.Abs(s.Y - d.Y);
            if (pasos >= mejorPasos) continue;
            if (HayComoLlegar(mapa, d, donde) is null) continue;
            mejor = donde;
            mejorPasos = pasos;
        }
        return mejor;
    }

    /// <summary>Camino hasta el objetivo o hasta una baldosa pegada a él. Un NPC ocupa su baldosa, así que
    /// "llegar" es quedar al lado.</summary>
    public IReadOnlyList<(byte X, byte Y)>? HayComoLlegar(int mapa, (byte X, byte Y) desde, (byte X, byte Y) objetivo)
    {
        if (Camino(mapa, desde, objetivo) is { } directo) return directo;
        for (byte rumbo = 1; rumbo <= 4; rumbo++)
        {
            var (dx, dy) = SentidosDelBot.Paso(rumbo);
            int nx = objetivo.X + dx, ny = objetivo.Y + dy;
            if (!SePuedePisar(mapa, nx, ny)) continue;
            if (Camino(mapa, desde, ((byte)nx, (byte)ny)) is { } vecino) return vecino;
        }
        return null;
    }

    private List<(int Destino, byte X, byte Y)> Salidas(int mapa) =>
        _salidas.TryGetValue(mapa, out var l) ? l : Vacio;

    private static readonly List<(int Destino, byte X, byte Y)> Vacio = new();

    /// <summary>
    /// ¿Se puede pisar A PIE? Tres cosas: el anillo muerto del borde, que el servidor no deja pisar (VB6
    /// MoveToLegalPos), la pared, y EL AGUA.
    ///
    /// <para>El agua es la que faltaba y la que rompía todo: a pie no se entra al agua —el servidor rechaza el
    /// paso y devuelve al jugador a donde estaba— así que un camino que la cruzaba dejaba al bot empujando
    /// contra el río para siempre. Medido: tres bots parados en la misma baldosa del mapa 37 intentando llegar
    /// a la tienda del mapa de al lado, viaje tras viaje.</para>
    ///
    /// <para>Lo que NO se mira es si hay alguien parado ahí: eso cambia todo el tiempo y el bot lo descubre
    /// caminando, igual que una persona.</para>
    /// </summary>
    private bool SePuedePisar(int mapa, int x, int y) =>
        x >= GameLimits.MinXBorder && x <= GameLimits.MaxXBorder
        && y >= GameLimits.MinYBorder && y <= GameLimits.MaxYBorder
        && !_datos.IsBlockedTile(mapa, x, y)
        && !_datos.IsWater(mapa, x, y);

    private static int Clave(byte x, byte y) => x | (y << 8);

    private static List<(byte X, byte Y)> Reconstruir(Dictionary<int, int> deDonde, int fin)
    {
        var camino = new List<(byte, byte)>();
        for (int nodo = fin; nodo != -1; nodo = deDonde[nodo])
            camino.Add(((byte)(nodo & 0xff), (byte)(nodo >> 8)));
        camino.RemoveAt(camino.Count - 1);   // la baldosa de partida no es un paso
        camino.Reverse();
        return camino;
    }
}
