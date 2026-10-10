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
    /// <summary>
    /// LAS BALDOSAS QUE UN PERSONAJE PUEDE PISAR EN UN MAPA. NO son las 10.000 del mapa crudo: el servidor no deja
    /// pisar el anillo del borde (VB6 <c>MoveToLegalPos</c>), así que lo caminable es el recuadro
    /// <see cref="GameLimits.MinXBorder"/>..<see cref="GameLimits.MaxXBorder"/> por
    /// <see cref="GameLimits.MinYBorder"/>..<see cref="GameLimits.MaxYBorder"/> — 84 por 88 = <b>7392</b>.
    /// </summary>
    private const int BaldosasDelRecuadroPisable =
        (GameLimits.MaxXBorder - GameLimits.MinXBorder + 1) * (GameLimits.MaxYBorder - GameLimits.MinYBorder + 1);

    /// <summary>
    /// Tope de baldosas que se exploran buscando un camino: el recuadro pisable ENTERO, así que no recorta ningún
    /// viaje posible. Está para que un destino encerrado detrás de una pared no cueste más que eso.
    ///
    /// <para>Estaba en 6.000 con el comentario «un mapa entero son 10.000, así que esto no recorta nada» — pero el
    /// número a comparar no era el mapa crudo sino el recuadro que se puede pisar, que son 7392: el tope quedaba
    /// ADENTRO y sí recortaba. Medido contra la data: de los 200 mapas a cinco saltos de la cuna, CIEN tienen una
    /// región alcanzable mayor a 6.000, y 6449 de 42335 puertas (15,2%) que <c>Alcanzables</c> daba por buenas no
    /// las encontraba <see cref="Camino"/>. El viaje fallaba en el primer tramo sin caminar un paso.</para>
    ///
    /// <para>Se DERIVA del recuadro para que no se pueda volver a desfasar: si algún día se mueven los bordes, el
    /// tope los acompaña.</para>
    /// </summary>
    private const int TopeDeExploracion = BaldosasDelRecuadroPisable;

    /// <summary>¿El tope de exploración cubre todo lo que se puede pisar? Si no, hay caminos que existen y el
    /// navegante no encuentra — y peor, `Alcanzables` los da por buenos y `Camino` no. Es lo que verifica el
    /// test: una propiedad entre las dos constantes, no un caso de un mapa.</summary>
    public static bool ElTopeCubreElRecuadroPisable => TopeDeExploracion >= BaldosasDelRecuadroPisable;

    /// <summary>Tope de mapas que se atraviesan en un viaje. Cinco alcanzan para ir de una ciudad a la de al
    /// lado; más que eso es un viaje que un personaje de nivel uno no haría.</summary>
    public const int TopeDeMapas = 5;

    /// <summary>De cada mapa vecino se prueban hasta tantas entradas a zonas que no se tocan.</summary>
    private const int ZonasPorMapa = 2;

    /// <summary>
    /// UNA ZONA QUE ENTRA EN UNA PANTALLA NO GASTA EL TOPE de <see cref="ZonasPorMapa"/>: es una franja del borde o un bolsón, no un
    /// lugar. La pantalla del original es de 17 por 13 baldosas (VB6 <c>XWindow</c> y <c>YWindow</c>, Declares.bas:519-530, las
    /// mismas que dan <see cref="GameLimits.MinXBorder"/>). Se recorre y se devuelve igual; sólo no ocupa el lugar de las grandes.
    ///
    /// <para>Medido contra la data (01/10): de las entradas al mapa 20 desde el 16, ninguna da al pueblo —son bolsones de 1 a 57
    /// baldosas del lado de afuera de la muralla— y las dos primeras gastaban el tope, así que desde el Desierto de Rinkel la zona
    /// del pueblo, a la que se entra por el 98, el 99 o el 101, no se miraba nunca. Como las zonas no se tocan, recorrer todas las
    /// chicas cuesta, como mucho, lo que las baldosas del mapa.</para>
    /// </summary>
    private const int BaldosasDeUnaPantalla = 17 * 13;

    /// <summary>A cuántas baldosas de un NPC alcanza con quedar parado para poder hablarle: el mostrador
    /// atiende a tres (VB6 <c>Distancia</c>, Manhattan) y el doble clic no pide línea de vista.</summary>
    private const int RadioDeMostrador = 3;

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
    public IReadOnlyList<(byte X, byte Y)>? Camino(int mapa, (byte X, byte Y) desde, (byte X, byte Y) hasta,
                                                   IReadOnlySet<(byte X, byte Y)>? evitar = null)
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
                // Lo que el mapa no sabe —una criatura parada, otro personaje— lo trae el que llama en `evitar`.
                if (!SePuedePisar(mapa, nx, ny) || evitar?.Contains(((byte)nx, (byte)ny)) == true) continue;
                int vecino = Clave((byte)nx, (byte)ny);
                // UNA SALIDA NO SE CRUZA: pisarla saca del mapa, así que sólo vale como la baldosa final. Contándola
                // como piso, el camino más corto pasaba por encima de otras salidas y el viaje terminaba en cualquier
                // lado. Medido: una Pescadora que iba de la cuna a la puerta del mapa 62 cayó en el 185, volvió a la
                // cuna y cayó dos veces en el 208, siempre en el primer tramo.
                if (vecino != fin && EsSalida(mapa, nx, ny)) continue;
                if (deDonde.ContainsKey(vecino)) continue;
                deDonde[vecino] = actual;
                cola.Enqueue(vecino);
            }
        }
        return null;
    }

    /// <summary>
    /// En qué baldosa de ESTE mapa hay que pararse para acercarse a <paramref name="mapaDestino"/>, y a
    /// cuántos mapas de distancia queda. null si no se llega en <paramref name="topeDeSaltos"/> saltos (de
    /// entrada, <see cref="TopeDeMapas"/>).
    ///
    /// <para>Es una búsqueda en anchura sobre el grafo de mapas: encuentra la ruta entera y devuelve la
    /// primera puerta, que es lo único que el bot necesita para dar el próximo paso del viaje.</para>
    ///
    /// <para>Con <paramref name="meta"/>, si ninguna entrada al destino deja a <see cref="RadioDeMostrador"/> de ella, la primera que se
    /// encontró. Con <paramref name="soloALaMeta"/>, null, y la entrada tiene que dejar AL LADO de la meta, que es una salida y se pisa.
    /// El 185, por el que se entra a Suramei, está partido: desde el 186 y desde el 261 se cae lejos de la salida (9,94), y en la ronda
    /// 62 Lira entraba por ahí y quedaba "sin camino".</para>
    /// </summary>
    public ((byte X, byte Y) Puerta, int Saltos)? PuertaHacia(int mapaActual, int mapaDestino,
                                                             (byte X, byte Y)? desde = null,
                                                             (byte X, byte Y)? meta = null,
                                                             Func<int, bool>? pisable = null,
                                                             int topeDeSaltos = TopeDeMapas,
                                                             bool soloALaMeta = false)
    {
        if (mapaActual == mapaDestino) return null;
        // UN MAPA QUE NO DEJA ENTRAR NO ES DESTINO NI CAMINO. El que llama dice cuáles con `pisable`: para el bot, la zona NEWBIE a
        // partir del nivel 16, que el servidor rebota a la baldosa de al lado sin cambiar de mapa.
        if (pisable is not null && !pisable(mapaDestino)) return null;
        int alcance = soloALaMeta ? 1 : RadioDeMostrador;

        // SÓLO PUERTAS A LAS QUE SE LLEGA CAMINANDO, si se sabe desde dónde. Una salida suele ser una fila
        // entera de baldosas, y acá se tomaba la primera baldosa hacia cada mapa sin mirar si se alcanzaba: el
        // viaje fallaba en el primer tramo aunque hubiera otra baldosa de la misma salida a la vista. Medido sin
        // servidor: desde donde estaba parado un Minero en el mapa 62, la puerta (51,73) hacia el 194 que le daba
        // el navegante no tenía camino.
        var alcanzables = desde is { } d ? Alcanzables(mapaActual, d) : null;

        // Y SI EL DESTINO ES EL MAPA DE AL LADO Y SE SABE ADÓNDE SE VA, LA PUERTA SE ELIGE POR LO QUE HAY DEL OTRO
        // LADO: la primera que se alcanza puede dejar en una zona desde la que no se llega a la meta. Es la misma
        // cuenta que hace `DondeHay`; sin ella el viaje entraba por otra puerta que la que se había elegido.
        if (meta is { } objetivo)
        {
            var probadas = new List<HashSet<int>>();
            foreach (var (destino, x, y) in Salidas(mapaActual))
            {
                if (destino != mapaDestino || (alcanzables is not null && !alcanzables.Contains(Clave(x, y)))) continue;
                if (!_datos.MapExits.TryGetValue((mapaActual, x, y), out var llegada)) continue;
                (byte X, byte Y) entrada = ((byte)llegada.DestX, (byte)llegada.DestY);
                if (probadas.Any(z => z.Contains(Clave(entrada.X, entrada.Y)))) continue;
                probadas.Add(Alcanzables(destino, entrada));
                if (HayComoLlegar(destino, entrada, objetivo, alcance) is not null) return ((x, y), 1);
                if (ZonasGrandes(probadas) >= ZonasPorMapa) break;
            }
        }

        // CON LOS PIES, NO CON EL GRAFO. El recorrido de abajo mira si cada salida se alcanza CAMINANDO desde la
        // baldosa por la que se entra a su mapa, en todos los saltos y no sólo en el primero. El BFS propio que
        // había acá validaba el primer salto y de ahí en más recorría el grafo de mapas a secas, así que prometía
        // rutas que no existían.
        //
        // Medido replicando los dos recorridos contra la data, sobre 1242 destinos que el navegante declara
        // alcanzables desde 30 mapas con criaturas: 68 (5,5%) salían por la puerta equivocada, y el error crece con
        // la distancia — 0 de 79 a un salto, 4 de 150 a dos, 28 de 429 a cinco. Y no son errores sueltos: desde el
        // mapa 746 mandaba por la MISMA puerta mala a ocho destinos distintos, o sea entrando y volviendo por ahí.
        //
        // Y SI SE SABE ADÓNDE SE VA, POR LA ZONA DONDE QUEDA, como en el mapa de al lado: la primera zona del destino que aparece
        // puede ser un bolsón del borde. Medido contra la data (01/10): desde el 16, la primera entrada al 20 es un bolsón de afuera
        // de la muralla, y el pueblo se alcanza entrando por el 98. Si la meta no queda en ninguna, la primera, como antes.
        if (desde is { } origen)
        {
            ((byte X, byte Y) Puerta, int Saltos)? cualquiera = null;
            foreach (var (mapa, saltos, entrada, primera) in MapasPorSuEntrada(mapaActual, origen, pisable, topeDeSaltos))
            {
                if (mapa != mapaDestino) continue;
                if (meta is not { } adonde || HayComoLlegar(mapa, entrada, adonde, alcance) is not null) return (primera, saltos);
                cualquiera ??= (primera, saltos);
            }
            return soloALaMeta && meta is not null ? null : cualquiera;
        }

        // SIN SABER DESDE DÓNDE no hay pies que valgan: queda el grafo de mapas pelado. Es lo que se responde a
        // un "¿se llega de acá a allá?" sin un personaje parado en ningún lado.
        var visto = new HashSet<int> { mapaActual };
        var cola = new Queue<(int Mapa, (byte X, byte Y) PrimeraPuerta, int Saltos)>();
        foreach (var (destino, x, y) in Salidas(mapaActual))
        {
            if (alcanzables is not null && !alcanzables.Contains(Clave(x, y))) continue;
            if (pisable is not null && !pisable(destino)) continue;
            if (!visto.Add(destino)) continue;
            if (destino == mapaDestino) return ((x, y), 1);
            cola.Enqueue((destino, (x, y), 1));
        }

        while (cola.Count > 0)
        {
            var (mapa, puerta, saltos) = cola.Dequeue();
            if (saltos >= topeDeSaltos) continue;
            foreach (var (destino, _, _) in Salidas(mapa))
            {
                if (pisable is not null && !pisable(destino)) continue;
                if (!visto.Add(destino)) continue;
                if (destino == mapaDestino) return (puerta, saltos + 1);
                cola.Enqueue((destino, puerta, saltos + 1));
            }
        }
        return null;
    }

    /// <summary>
    /// LA PUERTA DE AL LADO: el camino a la salida más cercana que lleva al mismo mapa que <paramref name="puerta"/> Y a la misma zona
    /// de llegada, sin pisar lo que se pide evitar (la puerta misma, si la tapa alguien). Una puerta suele tener varias baldosas de
    /// ancho, y el viaje (<see cref="PuertaHacia"/>) elige siempre una sola; null si no hay otra.
    ///
    /// <para>Medido en `mid56` y `tardeN21` (03/10): la entrada oeste de Rinkel son cuatro baldosas, (9,69) a (9,72), y todo el que
    /// salía iba a (9,69), a la que sólo se llega por (10,69). Cuando uno entraba con (10,69) ocupada caía en la legal más cercana,
    /// que es (9,69), una salida: el que salía esperaba a que se corriera y el que entraba a que se le liberara (10,69), sin camino
    /// ninguno de los dos, hasta que alguno usaba la runa. Hasta ocho bots apilados y cuatro minutos y medio.</para>
    /// </summary>
    public IReadOnlyList<(byte X, byte Y)>? CaminoAUnaPuertaHermana(int mapa, (byte X, byte Y) desde, (byte X, byte Y) puerta,
                                                                  IReadOnlySet<(byte X, byte Y)> evitar)
    {
        if (!_datos.MapExits.TryGetValue((mapa, puerta.X, puerta.Y), out var aca)) return null;
        var zona = Alcanzables(aca.DestMap, ((byte)aca.DestX, (byte)aca.DestY));
        return CaminoALaPuertaMasCercana(mapa, desde, aca.DestMap, evitar,
            (x, y) => (x, y) != (puerta.X, puerta.Y) && _datos.MapExits.TryGetValue((mapa, x, y), out var alla)
                      && zona.Contains(Clave((byte)alla.DestX, (byte)alla.DestY)));
    }

    /// <summary>
    /// EL CAMINO A LA PUERTA MÁS CERCANA HACIA ESE MAPA, caminando desde donde está: la primera baldosa de salida hacia
    /// <paramref name="mapaDestino"/> que encuentra la búsqueda en anchura, sin cruzar otras salidas ni lo que se pide evitar. null
    /// si no hay ninguna. <see cref="PuertaHacia"/> elige la puerta por lo que hay del otro lado y en el orden de la data, no por la
    /// distancia, y para escapar lo que cuenta es el paso: medido en `early3` (29/09), una Druida en (10,9) del 195, con un Lobo
    /// pegándole, escapaba hacia el 275 por (9,8) —que pedía pasar por (10,8)— teniendo la salida (9,9) al lado, y murió ahí.
    /// </summary>
    public IReadOnlyList<(byte X, byte Y)>? CaminoALaPuertaMasCercana(int mapa, (byte X, byte Y) desde, int mapaDestino,
                                                                    IReadOnlySet<(byte X, byte Y)>? evitar = null,
                                                                    Func<int, int, bool>? cuenta = null)
    {
        bool LlevaAlDestino(int x, int y) => _datos.MapExits.TryGetValue((mapa, x, y), out var sal) && sal.DestMap == mapaDestino
                                             && (cuenta?.Invoke(x, y) ?? true);
        var deDonde = new Dictionary<int, int>();
        var cola = new Queue<int>();
        int inicio = Clave(desde.X, desde.Y);
        cola.Enqueue(inicio);
        deDonde[inicio] = -1;
        int vistos = 0;
        while (cola.Count > 0 && vistos++ < TopeDeExploracion)
        {
            int actual = cola.Dequeue();
            byte x = (byte)(actual & 0xff), y = (byte)(actual >> 8);
            if (actual != inicio && LlevaAlDestino(x, y)) return Reconstruir(deDonde, actual);
            for (byte rumbo = 1; rumbo <= 4; rumbo++)
            {
                var (dx, dy) = SentidosDelBot.Paso(rumbo);
                int nx = x + dx, ny = y + dy;
                if (!SePuedePisar(mapa, nx, ny) || evitar?.Contains(((byte)nx, (byte)ny)) == true) continue;
                // Una salida a otro lado no se cruza (ver `Camino`); la del destino es la meta.
                if (EsSalida(mapa, nx, ny) && !LlevaAlDestino(nx, ny)) continue;
                int vecino = Clave((byte)nx, (byte)ny);
                if (deDonde.ContainsKey(vecino)) continue;
                deDonde[vecino] = actual;
                cola.Enqueue(vecino);
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
                                                           Func<int, bool> sirve, Func<int, bool>? mapaSirve = null,
                                                           Func<int, bool>? pisable = null)
    {
        // EL MAPA TAMBIÉN TIENE QUE SERVIR. No alcanza con que el NPC sea el que se busca: un mercader no le
        // vende a un ciudadano en un mapa cuyo `battleMode` no es 2 ni 3 (VB6 Protocol.bas:5205, portado igual
        // en `MercaderMeAtiende`). Sin este filtro el bot viajaba, determinista, a Nix —el mapa vecino, que
        // tiene los tres vendedores de herramientas y battleMode 1— y ahí NADIE lo atendía. Medido: de los 863
        // mapas del mundo sólo 55 tienen battleMode 2 o 3, y de ésos 23 tienen mercader.
        if (mapaSirve is not null && !mapaSirve(mapaActual)) { /* el propio no sirve: se busca afuera */ }
        else
        // EN EL MAPA PROPIO SE EXIGE QUE SE PUEDA LLEGAR CAMINANDO. Muchos NPC están adentro de un edificio, y
        // devolver el primero que cumple sin mirar el camino mandaba al bot a una puerta que no existe: medido,
        // el vendedor de cañas del mapa 34 está en una baldosa libre y SIN CAMINO desde afuera, así que el
        // viaje fallaba siempre y el bot no compró nunca su herramienta. Se prueban todos los del mapa y se
        // elige el más cercano AL QUE SE LLEGA.
        {
            if (EnElMapa(mapaActual, desde, sirve) is { } aca) return (mapaActual, aca.X, aca.Y, 0);
        }

        // Mismo recorrido que PuertaHacia, pero mirando qué hay en cada mapa que se alcanza.
        // Y LA PRIMERA PUERTA, IGUAL QUE AHÍ: sólo las que se alcanzan desde donde se está. Eligiendo por cualquier
        // salida, el mapa de destino podía quedar detrás de una puerta sin camino, y como `PuertaHacia` sí la
        // descarta, el viaje moría en el primer tramo y la búsqueda siguiente volvía a elegir lo mismo. Medido: un
        // Minero en el mapa 62 al que se mandaba al vendedor de piquetes del 194 por (51,73), la única salida hacia
        // allá, que no se alcanza desde ninguno de los puntos del 62 que se probaron.
        // Y EN CADA MAPA SE BUSCA DESDE LA BALDOSA POR LA QUE SE ENTRA, que se sabe: cada salida dice dónde se
        // aparece del otro lado. Primero se devolvía el primero que cumplía —y el bot viajaba hasta un mercader
        // encerrado: de los ocho que venden la caña se llega a tres—; después se lo probaba desde unas baldosas de
        // muestra repartidas por el mapa, que no son por donde se entra. Medido: a una Pescadora envenenada del
        // mapa 62 se la mandaba al sacerdote del 64, que se alcanza desde alguna muestra pero no desde (10,36), la
        // baldosa de llegada; entraba, no había camino, y lo volvía a intentar cada uno o dos minutos con el veneno
        // encima. Un mapa puede tener entradas a zonas que no se tocan, así que de cada uno se prueban hasta dos.
        foreach (var (mapa, saltos, entrada, _) in MapasPorSuEntrada(mapaActual, desde, pisable))
            if ((mapaSirve is null || mapaSirve(mapa)) && EnElMapa(mapa, entrada, sirve) is { } ahi)
                return (mapa, ahi.X, ahi.Y, saltos);
        return null;
    }

    /// <summary>
    /// EL MAPA MÁS CERCANO QUE SIRVE, con la baldosa por la que se entra. Es a dónde ir cuando lo que importa es el
    /// mapa y no algo adentro de él: un bot al que se le terminó la sesión en un mapa pk camina hasta uno seguro para
    /// irse a dormir. La entrada es la baldosa de llegada, así que ir hasta ella es cruzar la puerta.
    ///
    /// <para>Y DE ESE MAPA, POR DONDE SE ENTRA AL MAPA Y NO A UNA FRANJA: entre sus entradas a los mismos saltos, la primera cuya
    /// zona no entra en una pantalla (<see cref="BaldosasDeUnaPantalla"/>); si ninguna, la primera, como antes. Las entradas salen en
    /// el orden de la data, y la primera de un mapa puede ser una franja del lado de afuera de la muralla, como en
    /// <see cref="PuebloMasCercano"/>. Medido (03/10, en las corridas guardadas): desde el 98 la primera entrada a Rinkel es (10,8),
    /// una franja de 113 baldosas entre el borde y la muralla oeste sin mercader ni sacerdote; la del pueblo, (10,69), salía segunda.
    /// Hubo unas 185 llegadas ahí (Milo 69, Petra 65, Osmar 36): Petra y Osmar se sentaban a fabricar y después no llegaban al
    /// sacerdote ni a las tiendas, y Rufo, caminando al
    /// mapa seguro, quedaba sin tienda y la runa lo devolvía a Nix, donde no le venden (en `mid52` murió rehaciendo el rescate).
    /// Lo mismo pasa entrando al Puerto de Illiandor (179) desde el 275.</para>
    /// </summary>
    public (int Mapa, byte X, byte Y, int Saltos)? MapaMasCercano(int mapaActual, (byte X, byte Y) desde,
                                                                 Func<int, bool> mapaSirve, Func<int, bool>? pisable = null,
                                                                 int topeDeSaltos = TopeDeMapas)
    {
        (int Mapa, byte X, byte Y, int Saltos)? primera = null;
        foreach (var (mapa, saltos, entrada, _) in MapasPorSuEntrada(mapaActual, desde, pisable, topeDeSaltos))
        {
            if (primera is { } p)
            {
                if (saltos > p.Saltos) break;
                if (mapa != p.Mapa) continue;
            }
            else if (!mapaSirve(mapa)) continue;
            if (Alcanzables(mapa, entrada).Count > BaldosasDeUnaPantalla) return (mapa, entrada.X, entrada.Y, saltos);
            primera ??= (mapa, entrada.X, entrada.Y, saltos);
        }
        return primera;
    }

    /// <summary>
    /// ¿ES UN BOLSILLO? Desde esa entrada de <paramref name="mapa"/> no se llega a ningún otro mapa sin volver a
    /// <paramref name="deDonde"/>: la zona de esa entrada sólo tiene salidas para atrás.
    /// </summary>
    public bool EsUnBolsillo(int mapa, (byte X, byte Y) entrada, int deDonde, Func<int, bool>? pisable = null) =>
        MapaMasCercano(mapa, entrada, m => m != deDonde, m => m != deDonde && (pisable?.Invoke(m) ?? true), 1) is null;

    /// <summary>
    /// EL PUEBLO MÁS CERCANO DONDE SE LLEGA A ALGUIEN QUE SIRVE, con la baldosa por la que se entra: como
    /// <see cref="MapaMasCercano"/>, pero la entrada cuenta sólo si desde ella se llega a un NPC que <paramref name="sirve"/>, a pie o
    /// abriendo una puerta (<see cref="SeLlegaAbriendoUnaPuerta"/>). La primera entrada que aparece de un mapa puede ser una franja
    /// del lado de afuera de la muralla.
    ///
    /// <para>Medido en `mid7` (01/10): Milo cazaba en el 97 e iba de compras a Rinkel, y el primer mapa con mercader del recorrido
    /// era el 20 entrando por (10,8): una franja de 113 baldosas entre el borde y la muralla oeste, sin ningún mercader. La zona del
    /// pueblo, por (10,69), salía segunda, a los mismos dos saltos. Llegaba, no había camino ni puerta, y volvía: siete veces en
    /// media hora. Contra la data, desde la primera criatura de cada mapa de caza: en 17 de los 194 que tienen un pueblo a cinco
    /// saltos, la primera entrada a ese pueblo no llevaba a ningún mercader (Rinkel desde el desierto y desde el 97, el 179 y el
    /// 184).</para>
    /// </summary>
    public (int Mapa, byte X, byte Y, int Saltos)? PuebloMasCercano(int mapaActual, (byte X, byte Y) desde,
                                                                   Func<int, bool> mapaSirve, Func<int, bool> sirve,
                                                                   Func<int, bool>? pisable = null,
                                                                   int topeDeSaltos = TopeDeMapas)
    {
        foreach (var (mapa, saltos, entrada, _) in MapasPorSuEntrada(mapaActual, desde, pisable, topeDeSaltos))
            if (mapaSirve(mapa) && SeLlegaAbriendoUnaPuerta(mapa, entrada, sirve)) return (mapa, entrada.X, entrada.Y, saltos);
        return null;
    }

    /// <summary>
    /// ¿SE LLEGA A UNO QUE SIRVE, a pie o abriendo UNA puerta? Es lo que hace el bot ya en el mapa: <c>AbrirUnaPuertaAsync</c> abre
    /// la que, sola, da camino (<see cref="AbrirlaDaCamino"/>), así que un mercader detrás de dos puertas seguidas no lo atiende
    /// nunca. Alcanza con quedar a <see cref="RadioDeMostrador"/> del mostrador, como en <see cref="EnElMapa"/>. Sólo se prueban las
    /// puertas que tocan lo que se alcanza a pie: abrir otra no cambia nada.
    /// </summary>
    public bool SeLlegaAbriendoUnaPuerta(int mapa, (byte X, byte Y) desde, Func<int, bool> sirve)
    {
        if (!_datos.MapSpawns.TryGetValue(mapa, out var lista) || !lista.Any(s => sirve(s.NpcIndex))) return false;
        var aPie = Alcanzables(mapa, desde);
        if (AlgunoQueSirveAlAlcance(lista, aPie, sirve)) return true;
        if (!_datos.MapObjects.TryGetValue(mapa, out var objetos)) return false;
        bool TocaLoQueSeAlcanza(int baldosa)
        {
            byte x = (byte)(baldosa & 0xff), y = (byte)(baldosa >> 8);
            for (byte rumbo = 1; rumbo <= 4; rumbo++)
            {
                var (dx, dy) = SentidosDelBot.Paso(rumbo);
                int nx = x + dx, ny = y + dy;
                if (nx is >= 0 and <= 255 && ny is >= 0 and <= 255 && aPie.Contains(Clave((byte)nx, (byte)ny))) return true;
            }
            return false;
        }
        foreach (var o in objetos)
        {
            if (!Bot.PuertaQueSeAbre(_datos.Obj(o.ObjIndex))) continue;
            var libres = BaldosasQueDestraba(((byte)o.X, (byte)o.Y));
            if (libres.Any(TocaLoQueSeAlcanza) && AlgunoQueSirveAlAlcance(lista, Alcanzables(mapa, desde, libres), sirve)) return true;
        }
        return false;
    }

    /// <summary>
    /// ¿SE LLEGA A UNO QUE SIRVE ABRIENDO HASTA <paramref name="puertas"/> PUERTAS? Como <see cref="SeLlegaAbriendoUnaPuerta"/>, para los
    /// que viven detrás de más de una: a Jerek, el enlistador de la Armada (Distrito Real, 60), se llega desde la entrada de Banderbill
    /// pasando las puertas de (73,76) y de (73,42). Ver <see cref="PuertasDelCamino"/>.
    /// </summary>
    public bool SeLlegaAbriendoPuertas(int mapa, (byte X, byte Y) desde, Func<int, bool> sirve, int puertas) =>
        _datos.MapSpawns.TryGetValue(mapa, out var lista)
        && lista.Where(s => sirve(s.NpcIndex)).Select(s => ((byte)s.X, (byte)s.Y)).ToList() is { Count: > 0 } metas
        && PuertasDelCamino(mapa, desde, metas, RadioDeMostrador) is { } enElCamino && enElCamino.Count <= puertas;

    /// <summary>
    /// LAS PUERTAS QUE HAY QUE ABRIR, en orden, para quedar a <paramref name="radio"/> o menos de alguna de <paramref name="metas"/>: el
    /// camino más corto contando como libres todas las puertas cerradas que se abren, y de ese camino, las que todavía están cerradas.
    /// Vacía si se llega sin abrir ninguna; null si ni abriéndolas todas.
    ///
    /// <para>Elegir de a una "la que más acerca" en línea recta no sirve: en el 60 la zona de la izquierda, sin puertas, queda más
    /// cerca de Jerek que lo que abre cualquier puerta, y del otro lado de una pared.</para>
    /// </summary>
    public IReadOnlyList<(byte X, byte Y)>? PuertasDelCamino(int mapa, (byte X, byte Y) desde, IReadOnlyList<(byte X, byte Y)> metas,
                                                            int radio)
    {
        var puertaDe = new Dictionary<int, (byte X, byte Y)>();
        if (_datos.MapObjects.TryGetValue(mapa, out var objetos))
            foreach (var o in objetos)
                if (Bot.PuertaQueSeAbre(_datos.Obj(o.ObjIndex)))
                    foreach (int b in BaldosasQueDestraba(((byte)o.X, (byte)o.Y))) puertaDe.TryAdd(b, ((byte)o.X, (byte)o.Y));
        var libres = puertaDe.Keys.ToHashSet();
        bool EsMeta(byte x, byte y)
        {
            foreach (var m in metas) if (Math.Abs(x - m.X) + Math.Abs(y - m.Y) <= radio) return true;
            return false;
        }
        var deDonde = new Dictionary<int, int>();
        var cola = new Queue<int>();
        int inicio = Clave(desde.X, desde.Y);
        deDonde[inicio] = -1;
        cola.Enqueue(inicio);
        while (cola.Count > 0 && deDonde.Count < TopeDeExploracion)
        {
            int actual = cola.Dequeue();
            byte x = (byte)(actual & 0xff), y = (byte)(actual >> 8);
            if (EsMeta(x, y) && !EsSalida(mapa, x, y))
            {
                var orden = new List<(byte X, byte Y)>();
                var camino = Reconstruir(deDonde, actual);
                foreach (var (cx, cy) in camino)
                    if (puertaDe.TryGetValue(Clave(cx, cy), out var puerta) && _datos.IsBlockedTile(mapa, cx, cy) && !orden.Contains(puerta))
                        orden.Add(puerta);
                return orden;
            }
            for (byte rumbo = 1; rumbo <= 4; rumbo++)
            {
                var (dx, dy) = SentidosDelBot.Paso(rumbo);
                int nx = x + dx, ny = y + dy;
                if (!SePuedePisar(mapa, nx, ny, libres)) continue;
                int vecino = Clave((byte)nx, (byte)ny);
                if (deDonde.ContainsKey(vecino)) continue;
                deDonde[vecino] = actual;
                // A una salida se llega, pero no se sigue de largo: pisarla saca del mapa.
                if (!EsSalida(mapa, nx, ny)) cola.Enqueue(vecino);
            }
        }
        return null;
    }

    /// <summary>Cuántas de esas zonas no entran en una pantalla (ver <see cref="BaldosasDeUnaPantalla"/>).</summary>
    private static int ZonasGrandes(List<HashSet<int>> zonas) => zonas.Count(z => z.Count > BaldosasDeUnaPantalla);

    /// <summary>
    /// Los mapas a los que se llega viajando desde <paramref name="desde"/>, en orden de cercanía, cada uno con la
    /// baldosa por la que se entra. Sólo cuentan las salidas a las que se llega caminando dentro de cada mapa, y de
    /// cada mapa se prueban hasta <see cref="ZonasPorMapa"/> entradas a zonas que no se tocan, sin contar las que entran en una
    /// pantalla (<see cref="BaldosasDeUnaPantalla"/>).
    /// </summary>
    private IEnumerable<(int Mapa, int Saltos, (byte X, byte Y) Entrada, (byte X, byte Y) PrimeraPuerta)>
        MapasPorSuEntrada(int mapaActual, (byte X, byte Y) desde, Func<int, bool>? pisable = null,
                          int topeDeSaltos = TopeDeMapas, IReadOnlySet<int>? libresAca = null)
    {
        var alcanzables = Alcanzables(mapaActual, desde, libresAca);
        var zonas = new Dictionary<int, List<HashSet<int>>>();
        var cola = new Queue<(int Mapa, int Saltos, (byte X, byte Y) Entrada, HashSet<int> Zona,
                              (byte X, byte Y) PrimeraPuerta)>();
        // LA PRIMERA PUERTA se arrastra por toda la rama: es por donde hay que salir DE ESTE mapa para
        // terminar allá, y es lo único que el bot necesita para dar el próximo paso del viaje.
        void Entrar(int mapa, byte x, byte y, int saltos, (byte X, byte Y)? primera)
        {
            if (!_datos.MapExits.TryGetValue((mapa, x, y), out var llegada) || llegada.DestMap == mapaActual) return;
            if (pisable is not null && !pisable(llegada.DestMap)) return;
            (byte X, byte Y) entrada = ((byte)llegada.DestX, (byte)llegada.DestY);
            if (!zonas.TryGetValue(llegada.DestMap, out var deEse)) zonas[llegada.DestMap] = deEse = new();
            if (ZonasGrandes(deEse) >= ZonasPorMapa || deEse.Any(z => z.Contains(Clave(entrada.X, entrada.Y)))) return;
            var zona = Alcanzables(llegada.DestMap, entrada);
            deEse.Add(zona);
            cola.Enqueue((llegada.DestMap, saltos, entrada, zona, primera ?? (x, y)));
        }
        foreach (var (_, x, y) in Salidas(mapaActual))
            if (alcanzables.Contains(Clave(x, y))) Entrar(mapaActual, x, y, 1, null);

        while (cola.Count > 0)
        {
            var (mapa, saltos, entrada, zona, primera) = cola.Dequeue();
            yield return (mapa, saltos, entrada, primera);
            // EL TOPE CORTA ACÁ, que es el único lugar donde ahorra algo: filtrar después de enumerar deja hecho
            // el trabajo de los mapas que se descartan, y cada mapa enumerado cuesta un `Alcanzables` —una
            // búsqueda en anchura de hasta 7392 baldosas—. Medido sobre los 322 mapas con criaturas: a dos
            // saltos se llega a 12 mapas y a cinco, a 74, o sea 7,5 veces más de los que se usaban.
            if (saltos >= topeDeSaltos) continue;
            foreach (var (_, x, y) in Salidas(mapa))
                if (zona.Contains(Clave(x, y))) Entrar(mapa, x, y, saltos + 1, primera);
        }
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
        // El agua no se pisa, así que la baldosa del recurso nunca tiene camino DIRECTO: alcanza con poder
        // pararse al lado. Lo mismo vale para un árbol o un yacimiento, que también ocupan su baldosa.
        else if (esRecurso is not null && _datos.MapObjects.TryGetValue(mapa, out var objetos))
        {
            foreach (var o in objetos)
                if (esRecurso(o.ObjIndex)) Probar(o.X, o.Y);
        }
        // Y que se pueda LLEGAR: sin esto el bot caminaba hacia un río del otro lado de una pared y gastaba
        // los doce clics contra el aire.
        if (mejor is { } m && HayComoLlegar(mapa, desde, m.Item1) is null) return null;
        return mejor;
    }

    /// <summary>El mapa más cercano donde hay dónde ejercer ese oficio, y en qué baldosa. Sirve para IR a
    /// trabajar cuando en el mapa donde está no hay ni agua ni yacimientos.</summary>
    public (int Mapa, byte X, byte Y, int Saltos)? DondeSeTrabaja(int mapaActual, bool agua,
                                                                  Func<int, bool>? esRecurso = null,
                                                                  (byte X, byte Y)? desde = null,
                                                                  Func<int, bool>? mapaSirve = null,
                                                                  Func<int, bool>? pisable = null)
    {
        // DESDE DÓNDE SE BUSCA: desde donde está parado y, en cada mapa al que se viaja, desde la baldosa por la que se
        // entra. Antes se buscaba desde el centro de cada mapa, (50,50), y en los mapas con minas el centro es pared:
        // medido sin servidor, en el 33, el 50 y el 205 —los tres con yacimientos más cercanos a la cuna— el centro no
        // se pisa, así que ninguno aparecía y el Minero no encontraba nunca dónde trabajar, aunque desde la entrada del
        // 33 se llega a 56 de sus 59 yacimientos. Es el mismo recorrido que usa `DondeHay`.
        var inicio = desde ?? ((byte)50, (byte)50);
        if ((mapaSirve is null || mapaSirve(mapaActual)) && DondeTrabajar(mapaActual, inicio, 90, agua, esRecurso) is { } aca)
            return (mapaActual, aca.Donde.X, aca.Donde.Y, 0);

        foreach (var (mapa, saltos, entrada, _) in MapasPorSuEntrada(mapaActual, inicio, pisable))
            if ((mapaSirve is null || mapaSirve(mapa)) && DondeTrabajar(mapa, entrada, 90, agua, esRecurso) is { } ahi)
                return (mapa, ahi.Donde.X, ahi.Donde.Y, saltos);
        return null;
    }

    /// <summary>El que cumple en ese mapa y al que se llega caminando desde <paramref name="d"/>; de los que lo
    /// tienen, el más cercano. Para otro mapa, <paramref name="d"/> es la baldosa por la que se entra.</summary>
    private (byte X, byte Y)? EnElMapa(int mapa, (byte X, byte Y) d, Func<int, bool> sirve)
    {
        if (!_datos.MapSpawns.TryGetValue(mapa, out var lista)) return null;
        (byte X, byte Y)? mejor = null;
        int mejorPasos = int.MaxValue;
        foreach (var s in lista)
        {
            if (!sirve(s.NpcIndex)) continue;
            var donde = ((byte)s.X, (byte)s.Y);
            int pasos = Math.Abs(s.X - d.X) + Math.Abs(s.Y - d.Y);
            if (pasos >= mejorPasos) continue;
            if (HayComoLlegar(mapa, d, donde, RadioDeMostrador) is null) continue;
            mejor = donde;
            mejorPasos = pasos;
        }
        return mejor;
    }

    /// <summary>
    /// LAS BALDOSAS QUE DESTRABA ABRIR ESA PUERTA: las DOS que ocupa, <c>(X,Y)</c> y <c>(X-1,Y)</c>. Es lo que el
    /// servidor desbloquea al abrirla (<c>AccionParaPuerta</c>, PacketDispatcher.cs:8163, que las recorre en ese
    /// orden), y el <c>.csm</c> las trae bloqueadas: abrir una puerta es, literalmente, quitarles el bloqueo.
    /// </summary>
    public static IReadOnlySet<int> BaldosasQueDestraba((byte X, byte Y) puerta)
    {
        var libres = new HashSet<int> { Clave(puerta.X, puerta.Y) };
        if (puerta.X > GameLimits.MinXBorder) libres.Add(Clave((byte)(puerta.X - 1), puerta.Y));
        return libres;
    }

    /// <summary>
    /// ¿ABRIR ESA PUERTA LE DA UNA SALIDA hacia <paramref name="mapaDestino"/>? Como <see cref="AbrirlaDaCamino"/>, pero para el viaje:
    /// primero, que abrirla deje a pie alguna salida del mapa que antes no se alcanzaba (es barato, y descarta casi todas las puertas de
    /// un pueblo); y de esas, que por alguna se llegue al destino en <paramref name="topeDeSaltos"/> mapas.
    ///
    /// <para>Es el arreglo de fondo de Lindos (62): la vuelta a Illiandor (194) es el teleport (51,73), detrás de la puerta (50,85), y
    /// con la puerta cerrada desde las Águilas sólo se llega a pie al 63 y al 64. Lo medido el 30/09: Osmar 28 minutos en el nivel 3 en
    /// Lindos (`early9`).</para>
    /// </summary>
    public bool AbrirlaDaSalidaHacia(int mapa, (byte X, byte Y) desde, (byte X, byte Y) puerta, int mapaDestino,
                                     (byte X, byte Y)? meta, Func<int, bool>? pisable, int topeDeSaltos = TopeDeMapas)
    {
        if (mapa == mapaDestino) return false;
        var libres = BaldosasQueDestraba(puerta);
        var antes = Alcanzables(mapa, desde);
        var despues = Alcanzables(mapa, desde, libres);
        if (!Salidas(mapa).Any(s => despues.Contains(Clave(s.X, s.Y)) && !antes.Contains(Clave(s.X, s.Y)))) return false;
        foreach (var (m, _, entrada, _) in MapasPorSuEntrada(mapa, desde, pisable, topeDeSaltos, libres))
            if (m == mapaDestino && (meta is not { } adonde || HayComoLlegar(m, entrada, adonde, RadioDeMostrador) is not null))
                return true;
        return false;
    }

    /// <summary>
    /// ¿ABRIR ESA PUERTA LE DA CAMINO a un NPC que sirve, en este mapa? Se recorre el mapa tratando como libres
    /// las baldosas que la puerta ocupa, que es exactamente lo que pasa cuando se abre.
    ///
    /// <para>Hace falta para ELEGIR cuál abrir. El bot abría la más cercana que veía, y en un mapa de ciudad hay
    /// diez o veinte: medido contra la data —contando, como el juego, que se atiende a tres baldosas del
    /// mostrador—, en los 23 mapas que atienden hay 146 mercaderes y 62 se alcanzan a pie; de los 84 encerrados,
    /// abrir UNA puerta libera 36, repartidos en ocho mapas. En el mapa 20 una sola puerta —la de (50,86), a
    /// siete pasos de la entrada del sur— libera a SEIS vendedores de pergaminos. Y la única que se lo vio abrir
    /// en campo, la del mapa 34, sumaba treinta baldosas y NINGÚN mercader.</para>
    ///
    /// <para>Preguntarlo antes es la única forma: abriéndola para ver ya gastó el viaje, y de la puerta no se
    /// vuelve — el servidor no la cierra sola.</para>
    /// </summary>
    public bool AbrirlaDaCamino(int mapa, (byte X, byte Y) desde, (byte X, byte Y) puerta, Func<int, bool> sirve) =>
        _datos.MapSpawns.TryGetValue(mapa, out var lista)
        && AlgunoQueSirveAlAlcance(lista, Alcanzables(mapa, desde, BaldosasQueDestraba(puerta)), sirve);

    /// <summary>¿Alguno de los que nacen en <paramref name="lista"/> y sirven queda a <see cref="RadioDeMostrador"/> o menos de
    /// alguna de esas baldosas? Es la distancia a la que atiende el mostrador, igual que en <see cref="EnElMapa"/>.</summary>
    private static bool AlgunoQueSirveAlAlcance(IReadOnlyList<MapNpcSpawn> lista, HashSet<int> alcanzables, Func<int, bool> sirve)
    {
        foreach (var s in lista)
        {
            if (!sirve(s.NpcIndex)) continue;
            // Alcanza con quedar a RadioDeMostrador del mostrador, igual que en EnElMapa.
            for (int dx = -RadioDeMostrador; dx <= RadioDeMostrador; dx++)
                for (int resto = RadioDeMostrador - Math.Abs(dx), dy = -resto; dy <= resto; dy++)
                {
                    int px = s.X + dx, py = s.Y + dy;
                    if (px is >= 0 and <= 255 && py is >= 0 and <= 255
                        && alcanzables.Contains(Clave((byte)px, (byte)py))) return true;
                }
        }
        return false;
    }

    /// <summary>
    /// Camino hasta el objetivo, o hasta la baldosa alcanzable MÁS CERCANA a él dentro de
    /// <paramref name="radio"/>. Devuelve null si no hay ninguna.
    ///
    /// <para><b>Por qué un radio y no la baldosa exacta.</b> Un NPC ocupa la suya, así que ahí no se puede
    /// parar. Y más importante: al servidor no le hace falta que estés al lado ni que lo VEAS — el mostrador
    /// atiende a tres baldosas contadas en Manhattan (VB6 <c>Distancia</c>) y el doble clic sólo exige que la
    /// baldosa caiga en la ventana de visión. No hay chequeo de línea de vista. O sea que se le puede comprar
    /// a un mercader que está adentro de un edificio parándose del otro lado de la pared, que es justo lo que
    /// hacía falta: medido sobre los mapas del mundo, la mayoría de los mercaderes están encerrados y el bot
    /// que exigía llegar a su baldosa se pasaba la sesión viajando de uno a otro sin poder comprarle a
    /// ninguno.</para>
    /// </summary>
    public IReadOnlyList<(byte X, byte Y)>? HayComoLlegar(int mapa, (byte X, byte Y) desde,
                                                          (byte X, byte Y) objetivo, int radio = 1,
                                                          IReadOnlySet<(byte X, byte Y)>? evitar = null)
    {
        // NI LA PARADA ES UNA SALIDA: pisarla saca del mapa, y lo que se busca es quedar cerca del objetivo EN ESTE. Hay criaturas
        // que nacen encima de una —46 en el mundo, como el Murciélago de (19,7) del 11, en la fila de la salida al 555—, y el
        // camino al coto terminaba ahí: medido en `early1`, dos bots de nivel 1 cayeron en el 555, volvieron al 11 y otra vez,
        // seis y ocho veces seguidas.
        if (!EsSalida(mapa, objetivo.X, objetivo.Y) && Camino(mapa, desde, objetivo, evitar) is { } directo) return directo;
        // De cerca hacia afuera: la primera baldosa alcanzable es la mejor parada.
        for (int d = 1; d <= radio; d++)
        {
            for (int dx = -d; dx <= d; dx++)
            {
                int resto = d - Math.Abs(dx);
                foreach (int dy in resto == 0 ? new[] { 0 } : new[] { -resto, resto })
                {
                    int nx = objetivo.X + dx, ny = objetivo.Y + dy;
                    if (!SePuedePisar(mapa, nx, ny) || EsSalida(mapa, nx, ny)) continue;
                    if (Camino(mapa, desde, ((byte)nx, (byte)ny), evitar) is { } cerca) return cerca;
                }
            }
        }
        return null;
    }

    /// <summary>
    /// HACIA DÓNDE ALEJARSE de <paramref name="amenaza"/>: el camino a la baldosa a <paramref name="pasos"/> o menos que queda más
    /// lejos de ella, contada en Manhattan porque en el juego se pega en cruz. Sin pisar la baldosa de la amenaza, lo que el que
    /// llama trae en <paramref name="evitar"/> ni una salida, que saca del mapa. null si ninguna queda más lejos que donde está.
    ///
    /// <para>Entre las que quedan igual de lejos gana la primera que encuentra la búsqueda en anchura, que es la de menos pasos.</para>
    /// </summary>
    public IReadOnlyList<(byte X, byte Y)>? LejosDe(int mapa, (byte X, byte Y) desde, (byte X, byte Y) amenaza, int pasos,
                                                    IReadOnlySet<(byte X, byte Y)>? evitar = null)
    {
        static int Distancia(int x, int y, (byte X, byte Y) b) => Math.Abs(x - b.X) + Math.Abs(y - b.Y);
        var deDonde = new Dictionary<int, int>();
        var pasosHasta = new Dictionary<int, int>();
        var cola = new Queue<int>();
        int inicio = Clave(desde.X, desde.Y);
        deDonde[inicio] = -1;
        pasosHasta[inicio] = 0;
        cola.Enqueue(inicio);
        int mejor = inicio, mejorDistancia = Distancia(desde.X, desde.Y, amenaza);
        while (cola.Count > 0)
        {
            int actual = cola.Dequeue();
            byte x = (byte)(actual & 0xff), y = (byte)(actual >> 8);
            int d = Distancia(x, y, amenaza);
            if (d > mejorDistancia) { mejor = actual; mejorDistancia = d; }
            if (pasosHasta[actual] >= pasos) continue;
            for (byte rumbo = 1; rumbo <= 4; rumbo++)
            {
                var (dx, dy) = SentidosDelBot.Paso(rumbo);
                int nx = x + dx, ny = y + dy;
                if (!SePuedePisar(mapa, nx, ny) || evitar?.Contains(((byte)nx, (byte)ny)) == true) continue;
                if (nx == amenaza.X && ny == amenaza.Y) continue;
                if (EsSalida(mapa, nx, ny)) continue;
                int vecino = Clave((byte)nx, (byte)ny);
                if (deDonde.ContainsKey(vecino)) continue;
                deDonde[vecino] = actual;
                pasosHasta[vecino] = pasosHasta[actual] + 1;
                cola.Enqueue(vecino);
            }
        }
        return mejor == inicio ? null : Reconstruir(deDonde, mejor);
    }

    /// <summary>Los mapas a los que se llega a <paramref name="topeDeSaltos"/> saltos o menos, cada uno con la
    /// baldosa por la que se entra. Es <c>MapasPorSuEntrada</c> con el tope a la vista, para poder medir que el
    /// corte sea de verdad y no un filtro puesto después de hacer el trabajo.</summary>
    public IEnumerable<(int Mapa, int Saltos, (byte X, byte Y) Entrada, (byte X, byte Y) PrimeraPuerta)>
        MapasAlcanzables(int mapaActual, (byte X, byte Y) desde, int topeDeSaltos,
                         Func<int, bool>? pisable = null) =>
        MapasPorSuEntrada(mapaActual, desde, pisable, topeDeSaltos);

    /// <summary>
    /// EL COTO MÁS POBLADO a <paramref name="saltosMax"/> mapas o menos, contando el propio: el mapa con más criaturas que sirven
    /// entre las que nacen en baldosas a las que se llega (desde donde se entra, o desde donde está en el propio), y la más cercana
    /// de ellas para ir. Entre los igual de poblados, el primero que se encuentra, que es el más cercano. null si no hay ninguno.
    /// </summary>
    public (int Mapa, byte X, byte Y, int Saltos, int Cuantas)? CotoMasPoblado(int mapaActual, (byte X, byte Y) desde,
                                                                              Func<int, bool> sirve, int saltosMax,
                                                                              Func<int, bool>? pisable = null,
                                                                              Func<int, bool>? cotoSirve = null)
    {
        (int Mapa, byte X, byte Y, int Saltos, int Cuantas)? mejor = null;
        void Probar(int mapa, (byte X, byte Y) entrada, int saltos)
        {
            // El que no sirve de coto se cruza igual: el filtro es para quedarse, no para pasar.
            if (cotoSirve is not null && !cotoSirve(mapa)) return;
            int cuantas = CuantasSeAlcanzan(mapa, entrada, sirve);
            if (cuantas == 0 || (mejor is { } ya && ya.Cuantas >= cuantas)) return;
            if (EnElMapa(mapa, entrada, sirve) is { } ahi) mejor = (mapa, ahi.X, ahi.Y, saltos, cuantas);
        }
        Probar(mapaActual, desde, 0);
        // El tope va al enumerador y no a un `if` de acá: así los mapas de más no se exploran siquiera.
        foreach (var (mapa, saltos, entrada, _) in MapasPorSuEntrada(mapaActual, desde, pisable, saltosMax))
            Probar(mapa, entrada, saltos);
        return mejor;
    }

    /// <summary>
    /// LO QUE MÁS RINDE en ese mapa: de las criaturas que nacen en baldosas a las que se llega caminando desde
    /// <paramref name="desde"/>, la que más da según <paramref name="rinde"/>, con el punto más cercano donde nace. Es
    /// la otra mitad de <see cref="CuantasSeAlcanzan"/>: una dice cuántas hay, ésta dice cuánto valen.
    /// </summary>
    public (byte X, byte Y, int Rinde)? LoQueMasRinde(int mapa, (byte X, byte Y) desde, Func<int, int> rinde)
    {
        if (!_datos.MapSpawns.TryGetValue(mapa, out var lista)) return null;
        var alcanzables = Alcanzables(mapa, desde);
        (byte X, byte Y, int Rinde)? mejor = null;
        int mejorPasos = int.MaxValue;
        foreach (var s in lista)
        {
            int da = rinde(s.NpcIndex);
            if (da <= 0 || !alcanzables.Contains(Clave((byte)s.X, (byte)s.Y))) continue;
            int pasos = Math.Abs(s.X - desde.X) + Math.Abs(s.Y - desde.Y);
            // La que más rinde; entre dos que rinden lo mismo, la más cercana.
            if (mejor is { } ya && (da < ya.Rinde || (da == ya.Rinde && pasos >= mejorPasos))) continue;
            mejor = ((byte)s.X, (byte)s.Y, da);
            mejorPasos = pasos;
        }
        return mejor;
    }

    /// <summary>
    /// EL COTO QUE MÁS RINDE a <paramref name="saltosMax"/> mapas o menos, contando el propio. Mismo recorrido que
    /// <see cref="CotoMasPoblado"/>, pero midiendo la criatura más gorda en vez de la cantidad.
    /// </summary>
    public (int Mapa, byte X, byte Y, int Saltos, int Rinde)? CotoQueMasRinde(int mapaActual, (byte X, byte Y) desde,
                                                                             Func<int, int> rinde, int saltosMax,
                                                                             Func<int, bool>? pisable = null,
                                                                             Func<int, bool>? cotoSirve = null,
                                                                             Func<int, int>? desempate = null)
    {
        (int Mapa, byte X, byte Y, int Saltos, int Rinde)? mejor = null;
        void Probar(int mapa, (byte X, byte Y) entrada, int saltos)
        {
            if (cotoSirve is not null && !cotoSirve(mapa)) return;
            if (LoQueMasRinde(mapa, entrada, rinde) is not { } ahi) return;
            // Entre los que rinden igual, el primero que se encuentra —el más cercano— salvo que haya con qué desempatar.
            if (mejor is { } ya && (ya.Rinde > ahi.Rinde
                                    || (ya.Rinde == ahi.Rinde && (desempate is null || desempate(ya.Mapa) >= desempate(mapa)))))
                return;
            mejor = (mapa, ahi.X, ahi.Y, saltos, ahi.Rinde);
        }
        Probar(mapaActual, desde, 0);
        // El tope va al enumerador y no a un `if` de acá: así los mapas de más no se exploran siquiera.
        foreach (var (mapa, saltos, entrada, _) in MapasPorSuEntrada(mapaActual, desde, pisable, saltosMax))
            Probar(mapa, entrada, saltos);
        return mejor;
    }

    /// <summary>
    /// LO QUE SUMA ese mapa: la experiencia de todas las criaturas que nacen en baldosas a las que se llega caminando desde
    /// <paramref name="desde"/>, según <paramref name="rinde"/>, con la más gorda y el punto más cercano donde nace. Es
    /// <see cref="LoQueMasRinde"/> contando todas y no sólo la mejor.
    /// </summary>
    public (byte X, byte Y, int Rinde, int Suma)? LoQueSuma(int mapa, (byte X, byte Y) desde, Func<int, int> rinde)
    {
        if (!_datos.MapSpawns.TryGetValue(mapa, out var lista)) return null;
        var alcanzables = Alcanzables(mapa, desde);
        (byte X, byte Y, int Rinde)? mejor = null;
        int mejorPasos = int.MaxValue, suma = 0;
        foreach (var s in lista)
        {
            int da = rinde(s.NpcIndex);
            if (da <= 0 || !alcanzables.Contains(Clave((byte)s.X, (byte)s.Y))) continue;
            suma += da;
            int pasos = Math.Abs(s.X - desde.X) + Math.Abs(s.Y - desde.Y);
            if (mejor is { } ya && (da < ya.Rinde || (da == ya.Rinde && pasos >= mejorPasos))) continue;
            mejor = ((byte)s.X, (byte)s.Y, da);
            mejorPasos = pasos;
        }
        return mejor is { } m ? (m.X, m.Y, m.Rinde, suma) : null;
    }

    /// <summary>
    /// EL COTO QUE MÁS SUMA a <paramref name="saltosMax"/> mapas o menos, contando el propio (<see cref="LoQueSuma"/>), entre los que
    /// suman al menos <paramref name="minimo"/>. Entre los que suman tres cuartos del mejor o más, el que prefiere
    /// <paramref name="desempate"/>: sin eso, todos los que salen del mismo lugar van al mismo mapa. Sin desempate, el que más suma, y
    /// entre iguales el más cercano.
    /// </summary>
    public (int Mapa, byte X, byte Y, int Saltos, int Rinde, int Suma)? CotoQueMasSuma(int mapaActual, (byte X, byte Y) desde,
                                                                                         Func<int, int> rinde, int saltosMax,
                                                                                         Func<int, bool>? pisable = null,
                                                                                         Func<int, bool>? cotoSirve = null,
                                                                                         Func<int, int>? desempate = null,
                                                                                         int minimo = 1)
    {
        var cotos = new List<(int Mapa, byte X, byte Y, int Saltos, int Rinde, int Suma)>();
        void Probar(int mapa, (byte X, byte Y) entrada, int saltos)
        {
            if (cotoSirve is not null && !cotoSirve(mapa)) return;
            if (LoQueSuma(mapa, entrada, rinde) is { } ahi && ahi.Suma >= minimo)
                cotos.Add((mapa, ahi.X, ahi.Y, saltos, ahi.Rinde, ahi.Suma));
        }
        Probar(mapaActual, desde, 0);
        foreach (var (mapa, saltos, entrada, _) in MapasPorSuEntrada(mapaActual, desde, pisable, saltosMax))
            Probar(mapa, entrada, saltos);
        if (cotos.Count == 0) return null;
        int mejor = cotos.Max(c => c.Suma);
        var parejos = cotos.Where(c => (long)c.Suma * 4 >= (long)mejor * 3);
        // El primero de cada orden es el más cercano: los mapas salen en orden de cercanía.
        return desempate is null
            ? cotos.First(c => c.Suma == mejor)
            : parejos.Aggregate((a, b) => desempate(b.Mapa) > desempate(a.Mapa) ? b : a);
    }

    /// <summary>Cuántas criaturas que cumplen <paramref name="sirve"/> nacen en baldosas de ese mapa a las que se llega caminando
    /// desde <paramref name="desde"/>.</summary>
    public int CuantasSeAlcanzan(int mapa, (byte X, byte Y) desde, Func<int, bool> sirve)
    {
        if (!_datos.MapSpawns.TryGetValue(mapa, out var spawns)) return 0;
        var alcanzables = Alcanzables(mapa, desde);
        return spawns.Count(sp => sirve(sp.NpcIndex) && alcanzables.Contains(Clave((byte)sp.X, (byte)sp.Y)));
    }

    /// <summary>Todas las baldosas a las que se llega caminando desde <paramref name="desde"/> en ese mapa. Es
    /// una sola búsqueda en anchura para responder muchas preguntas de "¿se llega?" seguidas, en vez de una
    /// búsqueda por cada baldosa de salida. El tope es el recuadro pisable entero, para no cortar un mapa
    /// abierto a la mitad.</summary>
    private HashSet<int> Alcanzables(int mapa, (byte X, byte Y) desde,
                                     IReadOnlySet<int>? comoSiEstuvieranLibres = null)
    {
        var vistos = new HashSet<int>();
        var cola = new Queue<int>();
        int inicio = Clave(desde.X, desde.Y);
        vistos.Add(inicio);
        cola.Enqueue(inicio);
        // EL MISMO TOPE QUE `Camino`: con uno propio, este declaraba alcanzable una puerta que después el otro no
        // encontraba, y el bot elegía destinos a los que su propio navegante no sabía llegar.
        while (cola.Count > 0 && vistos.Count < TopeDeExploracion)
        {
            int actual = cola.Dequeue();
            byte x = (byte)(actual & 0xff), y = (byte)(actual >> 8);
            for (byte rumbo = 1; rumbo <= 4; rumbo++)
            {
                var (dx, dy) = SentidosDelBot.Paso(rumbo);
                int nx = x + dx, ny = y + dy;
                if (!SePuedePisar(mapa, nx, ny, comoSiEstuvieranLibres)) continue;
                int vecino = Clave((byte)nx, (byte)ny);
                // A una salida se llega, pero no se sigue de largo: pisarla saca del mapa.
                if (vistos.Add(vecino) && !EsSalida(mapa, nx, ny)) cola.Enqueue(vecino);
            }
        }
        return vistos;
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
    private bool SePuedePisar(int mapa, int x, int y, IReadOnlySet<int>? comoSiEstuvieranLibres = null) =>
        x >= GameLimits.MinXBorder && x <= GameLimits.MaxXBorder
        && y >= GameLimits.MinYBorder && y <= GameLimits.MaxYBorder
        && (comoSiEstuvieranLibres?.Contains(Clave((byte)x, (byte)y)) == true || !_datos.IsBlockedTile(mapa, x, y))
        && !_datos.IsWater(mapa, x, y);

    private bool EsSalida(int mapa, int x, int y) => _datos.MapExits.ContainsKey((mapa, x, y));

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
