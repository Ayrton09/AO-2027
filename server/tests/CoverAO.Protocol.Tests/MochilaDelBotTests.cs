using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// LA MOCHILA DEL BOT SIGUE A LA DEL SERVIDOR CUANDO COME Y TOMA.
///
/// <para>Contra el despachador de verdad: el personaje usa la botella con el mismo paquete que manda un cliente, y
/// lo que el servidor contesta lo lee <see cref="SentidosDelBot"/> por el transporte del bot. Si el servidor
/// cambiara la forma de avisar lo consumido, esto se pone rojo antes de que un bot vuelva a contar botellas que ya
/// se tomó.</para>
/// </summary>
public class MochilaDelBotTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "coverao_mochila_" + Guid.NewGuid().ToString("N"));
    private readonly GameData _datos = GameData.Load();
    private readonly PacketDispatcher _dispatcher;
    private readonly GameLoop _loop = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loopTask;

    public MochilaDelBotTests()
    {
        Directory.CreateDirectory(_root);
        var leveling = new Leveling(new Dictionary<int, long>());
        _dispatcher = new PacketDispatcher(new World(), _loop, new Persistence(new FileStore(_root), leveling), _datos, leveling);
        // Usar un objeto corre adentro del loop, así que el loop tiene que girar de verdad.
        _loopTask = _loop.RunAsync(_cts.Token);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _loopTask.Wait(TimeSpan.FromSeconds(5)); } catch { /* cancelado */ }
        try { Directory.Delete(_root, recursive: true); } catch { /* el temp queda */ }
        _cts.Dispose();
    }

    [Theory]
    [InlineData((short)43)]   // Botella de Agua
    [InlineData((short)26)]   // Pollo
    public async Task Lo_que_consume_en_el_servidor_se_descuenta_en_su_mochila(short obj)
    {
        if (_datos.Obj(obj) is not ({ IsDrink: true } or { IsFood: true })) return;   // sin la data del juego no hay qué probar

        var transporte = new TransporteDeBot("Aldo");
        var sentidos = new SentidosDelBot();
        transporte.Recibido += sentidos.Recibir;
        var sesion = new ClientSession(transporte, _dispatcher);
        var p = new Player { CharIndex = 1, Name = "Aldo", Account = "aldo_bot", Pos = new WorldPos(1, 50, 50), Hunger = 0, Thirst = 0 };
        p.Inventory[2] = new InventorySlot { ObjIndex = obj, Amount = 4 };
        sesion.Player = p;
        sentidos.Recibir(ServerMessages.ChangeInventorySlot(2, obj, 4, false, 0f, 1));   // lo que sabe desde el login

        _dispatcher.HandleOne(sesion, new ByteQueueReader(new ByteQueueWriter()
            .WriteByte((byte)ClientPacketId.UseItem).WriteByte(2).ToArray()));

        for (int i = 0; i < 250 && sentidos.CuantoTiene(o => o == obj) == 4; i++) await Task.Delay(20);
        Assert.Equal(3, p.Inventory[2].Amount);   // el servidor la consumió
        Assert.Equal(3, sentidos.CuantoTiene(o => o == obj));
        sesion.Close("fin del test");
    }

    /// <summary>
    /// EL ÚLTIMO NIVEL DE NOVATO YA CUENTA COMO "ESTOY POR PERDERLO". Al pasar de
    /// <see cref="GameLimits.NewbieMaxLevel"/> el servidor le saca de una todo lo de novato —siete de los ocho ítems
    /// del kit de nacimiento—, así que el bot tiene que salir a comprar lo suyo ANTES, mientras todavía tiene con qué.
    /// </summary>
    [Theory]
    [InlineData(1, false)]
    [InlineData(14, false)]
    [InlineData(15, true)]     // último nivel de novato: ya va de compras
    [InlineData(16, true)]     // ya se lo sacaron
    [InlineData(40, true)]
    public void En_el_ultimo_nivel_de_novato_ya_se_compra_lo_suyo(int nivel, bool seprepara)
    {
        Assert.Equal(seprepara, Bot.PorDejarDeSerNovato(nivel));
    }

    /// <summary>Y lo que el servidor le va a sacar es REAL: el kit de nacimiento de una clase cualquiera está lleno de
    /// objetos que el port trata como de novato, que es lo que hace falta para que la regla de arriba sirva.</summary>
    [Fact]
    public void El_kit_de_nacimiento_esta_lleno_de_cosas_que_se_pierden_al_dejar_de_ser_novato()
    {
        var datos = GameData.Load();
        var kit = datos.NewbieKit(2);
        if (kit.Count == 0) return;                       // sin la data del juego no hay kit que mirar

        int deNovato = kit.Count(it => datos.Obj((short)it.ObjIndex) is { IsNewbie: true });

        Assert.True(deNovato >= 4, $"el kit trae {deNovato} objetos de novato: la regla no tendría nada que proteger");
    }

    /// <summary>
    /// LO QUE COMPRÓ SE LO PONE. Con un escudo peor puesto y uno mejor en la mochila, el bot se cambia: si no, juntar
    /// plata para comprarse equipo mejor no sirve de nada. Es el caso de Ciro en `seguir1`, con el Escudo de Hierro +2
    /// guardado y el Escudo de Tortuga encima.
    /// </summary>
    [Fact]
    public void Se_cambia_por_lo_mejor_que_tiene_en_la_mochila()
    {
        var mochila = new (byte Ranura, int Valor, bool Puesto)[]
        {
            (4, 2, true),      // el Escudo de Tortuga, puesto
            (12, 8, false),    // el Escudo de Hierro +2, guardado
        };

        Assert.Equal((byte)12, Bot.QueSePoneDeEso(mochila));
    }

    /// <summary>
    /// Y NO SE CAMBIA POR ALGO PEOR NI POR ALGO IGUAL: el servidor saca lo puesto al poner lo nuevo, así que con dos
    /// que valen lo mismo el bot se pasaría la vida cambiándoselas.
    /// </summary>
    [Theory]
    [InlineData(8, 2, false)]    // lo de la mochila es peor
    [InlineData(8, 8, false)]    // valen lo mismo
    [InlineData(2, 8, true)]     // lo de la mochila es mejor
    public void Solo_se_cambia_por_algo_mejor(int puesto, int guardado, bool seCambia)
    {
        var mochila = new (byte Ranura, int Valor, bool Puesto)[] { (4, puesto, true), (12, guardado, false) };

        Assert.Equal(seCambia ? (byte?)12 : null, Bot.QueSePoneDeEso(mochila));
    }

    /// <summary>Sin nada puesto se pone lo mejor que tenga, que es como empieza cualquiera.</summary>
    [Fact]
    public void Sin_nada_puesto_se_pone_lo_mejor_de_la_mochila()
    {
        var mochila = new (byte Ranura, int Valor, bool Puesto)[] { (3, 1, false), (7, 5, false), (9, 3, false) };

        Assert.Equal((byte)7, Bot.QueSePoneDeEso(mochila));
        Assert.Null(Bot.QueSePoneDeEso(System.Array.Empty<(byte, int, bool)>()));
    }

    /// <summary>
    /// NO SE COMPRA NI SE PONE LO QUE SU RAZA TIENE PROHIBIDO. Contra la data de verdad: la Armadura de Cazador (360)
    /// lleva <c>RazasProhibidas=2,5</c> —números del obj.dat de Imperium, que <c>RaceForbidden</c> traduce— y es la que
    /// Ciro, enano, se compró en `seguir3` y no se pudo poner, quedándose con defensa 3 en vez de 24.
    /// </summary>
    [Fact]
    public void No_se_pone_la_armadura_que_su_raza_tiene_prohibida()
    {
        var datos = GameData.Load();
        if (datos.Obj(360) is not { } cazador) return;                 // sin la data del juego no hay nada que mirar
        byte prohibida = 0, permitida = 0;
        for (byte r = 1; r <= 6; r++)
            if (cazador.RaceForbidden(r)) prohibida = prohibida == 0 ? r : prohibida;
            else permitida = permitida == 0 ? r : permitida;
        Assert.True(prohibida != 0 && permitida != 0,
            "la Armadura de Cazador dejó de tener razas prohibidas: el test no prueba nada");

        Assert.False(Bot.PuedeUsarlo(cazador, clase: 8, raza: prohibida, genero: 1, nivel: 40));
        Assert.True(Bot.PuedeUsarlo(cazador, clase: 8, raza: permitida, genero: 1, nivel: 40));
    }

    /// <summary>Y las otras dos condiciones siguen valiendo: la clase prohibida y el nivel mínimo.</summary>
    [Fact]
    public void Tampoco_lo_que_su_clase_prohibe_ni_lo_que_le_queda_grande()
    {
        var datos = GameData.Load();
        if (datos.Obj(1000) is not { } escudo) return;                  // Escudo de Hierro +2
        byte prohibida = 0, permitida = 0;
        for (byte c = 1; c <= 18; c++)
            if (escudo.ClassForbidden(c)) prohibida = prohibida == 0 ? c : prohibida;
            else permitida = permitida == 0 ? c : permitida;
        Assert.True(prohibida != 0 && permitida != 0, "el Escudo de Hierro +2 dejó de tener clases prohibidas");

        Assert.False(Bot.PuedeUsarlo(escudo, clase: prohibida, raza: 1, genero: 1, nivel: 40));
        Assert.True(Bot.PuedeUsarlo(escudo, clase: permitida, raza: 1, genero: 1, nivel: 40));
        Assert.False(Bot.PuedeUsarlo(null, clase: permitida, raza: 1, genero: 1, nivel: 40));
    }

    /// <summary>
    /// EL BOT NO COMPRA LO QUE SU GÉNERO NO SE PUEDE PONER. Desde que el servidor respeta la marca de género
    /// (`4d758551`), un bot hombre que compre una de las 43 prendas de mujer paga por algo que después le rechazan:
    /// queda muerto en la mochila. El filtro de la COMPRA tiene que mirar lo mismo que la validación de la ACCIÓN.
    /// </summary>
    [Fact]
    public void No_compra_la_ropa_que_su_genero_no_puede_usar()
    {
        var datos = GameData.Load();
        var deMujer = System.Linq.Enumerable.Range(1, 1700).Select(i => datos.Obj((short)i))
                            .FirstOrDefault(o => o is { Gender: 2, IsArmor: true });
        if (deMujer is null) return;                     // sin data marcada no hay nada que medir

        Assert.False(Bot.PuedeUsarlo(deMujer, clase: 1, raza: 1, genero: 1, nivel: 40), "un hombre la compró igual");
        Assert.True(Bot.PuedeUsarlo(deMujer, clase: 1, raza: 1, genero: 2, nivel: 40), "una mujer dejó de poder usarla");
    }

    /// <summary>
    /// EL DESNUDO SE VISTE CON LO QUE TENGA, aunque no defienda. La regla exigía que lo de la mochila fuera MEJOR
    /// que lo puesto, y con nada puesto eso es <c>0 &gt; 0</c>: el bot que sólo tiene ropa de defensa cero se
    /// quedaba desnudo teniéndola encima. Desde que el desnudo no recupera energía eso lo deja sin poder trabajar
    /// ni pelear, y la energía sólo se repone al subir de nivel.
    ///
    /// <para>Medido en `tala1`: de los ocho bots, el único desnudo era el único con la energía en CERO (0 de 572),
    /// con las Vestimentas Tribales de defensa 0 en la mochila. Los otros siete, con una prenda puesta, la tenían
    /// llena.</para>
    /// </summary>
    [Theory]
    // lo que hay en la mochila | ¿hay algo puesto? | ¿se lo pone?
    [InlineData(0, false, true)]     // el caso: defensa cero y desnudo -> se viste igual
    [InlineData(0, true, false)]     // con algo puesto, defensa cero no mejora nada
    [InlineData(3, false, true)]     // desnudo y algo que defiende: obvio
    [InlineData(3, true, true)]      // puesto vale 2 (ver abajo): 3 es mejor
    public void El_desnudo_se_viste_aunque_no_defienda(int guardado, bool algoPuesto, bool seLoPone)
    {
        var mochila = algoPuesto
            ? new (byte Ranura, int Valor, bool Puesto)[] { (4, guardado, false), (9, 2, true) }
            : new (byte Ranura, int Valor, bool Puesto)[] { (4, guardado, false) };

        Assert.Equal(seLoPone ? (byte?)4 : null, Bot.QueSePoneDeEso(mochila));
    }

    /// <summary>Y con TODO puesto no hay nada que cambiar, aunque lo puesto no defienda.</summary>
    [Fact]
    public void Con_todo_puesto_no_se_cambia_nada()
    {
        var mochila = new (byte Ranura, int Valor, bool Puesto)[] { (4, 0, true) };
        Assert.Null(Bot.QueSePoneDeEso(mochila));
    }

    /// <summary>
    /// NO VENDE LO MEJOR QUE TIENE, porque lo recompra un segundo después. Medido en el log de economía del juego:
    /// cuatro pares venta+compra del MISMO objeto con cero o un segundo de diferencia —una armadura a 6.000 y de
    /// vuelta a 16.072— por <b>25.074 monedas</b> en una corrida. Pasa sobre todo al morir, que desequipa todo y
    /// deja la pieza en la mochila, donde el bucle de venta la toma por botín.
    /// </summary>
    [Theory]
    // lo que vale la pieza | lo mejor que tiene de esa clase | ¿se la guarda (no es botín)?
    [InlineData(24, 24, true)]    // ES lo mejor que tiene: no se vende
    [InlineData(24, 20, true)]    // todavía mejor que lo suyo
    [InlineData(3, 24, false)]    // peor que lo que tiene: eso sí es botín
    [InlineData(0, 0, false)]     // sin valor de combate no es equipo: no lo protege
    [InlineData(0, 24, false)]
    public void No_vende_lo_mejor_que_tiene_de_cada_clase(int vale, int mejor, bool seLoGuarda)
    {
        Assert.Equal(seLoGuarda, Bot.EsLoMejorQueTieneDeEsaClase(vale, mejor));
    }

    /// <summary>
    /// EL ARCO ES UN ARMA QUE EL BOT SE PONE, y la arrojadiza no. El filtro pedía <c>Proyectil == 0</c> con el
    /// comentario «las arrojadizas quedan afuera», pero las arrojadizas son <c>Proyectil == 2</c>: el arco
    /// (<c>== 1</c>) se caía de más. Son 11 de las 85 armas del catálogo.
    ///
    /// <para>El servidor desequipa TODO al morir, así que el Cazador —que nace con un Arco Simple y nada más— no
    /// tenía ningún camino para volver a armarse: peleaba a mano limpia, y su única fuente de plata es cazar. El
    /// arco pega cuerpo a cuerpo por la rama de arma de <c>MeleeDamage</c>; las flechas son para disparar.</para>
    /// </summary>
    [Fact]
    public void El_arco_es_un_arma_que_se_pone_y_la_arrojadiza_no()
    {
        var datos = GameData.Load();
        if (datos.Obj(1355) is not { } arcoDeNovato) return;      // sin la data del juego no hay nada que mirar
        Assert.Equal(1, arcoDeNovato.Proyectil);                  // es el arco con el que nace el Cazador
        Assert.True(Bot.EsArma(arcoDeNovato), "el arco del Cazador no cuenta como arma: queda desarmado al morir");

        int arcos = 0, arrojadizas = 0, cuerpoACuerpo = 0;
        for (short i = 1; i <= 1700; i++)
            if (datos.Obj(i) is { IsWeapon: true } a)
            {
                if (a.Proyectil == 1) { arcos++; Assert.True(Bot.EsArma(a)); }
                if (a.Proyectil == 2) { arrojadizas++; Assert.False(Bot.EsArma(a)); }
                if (a.Proyectil == 0) { cuerpoACuerpo++; Assert.True(Bot.EsArma(a)); }
            }
        Assert.True(arcos > 0, "la data dejó de tener arcos: el caso ya no aplica");
        Assert.True(arrojadizas > 0, "la data dejó de tener arrojadizas: el caso ya no aplica");
        Assert.True(cuerpoACuerpo > 0);
    }
}
