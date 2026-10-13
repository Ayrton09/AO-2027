using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// Los bots: personajes que juegan solos, apagados salvo que el host los pida.
///
/// <para>Lo que se cuida acá no es que caminen —eso se ve corriéndolos— sino las cosas que, si se rompen, se
/// rompen en silencio: que vengan apagados, que cada bot cuente como su propia dirección para los cupos, que el
/// transporte no pierda ni deforme un byte, y que el que ya entró no gaste sus intentos creando otra vez la
/// cuenta.</para>
/// </summary>
public class BotsTests
{
    // ── La decisión del host ──────────────────────────────────────────────────────────────────────────

    /// <summary>APAGADOS: sin archivo, y con un archivo que no los menciona. Un servidor recién instalado
    /// tiene que mostrar los jugadores que tiene, no los que le puso el programa.</summary>
    [Fact]
    public void Vienen_apagados()
    {
        Assert.Equal(0, ServerIni.Desde(new IniFile()).Bots);
        Assert.Equal(0, ServerIni.Desde(IniFile.Parse("[INIT]\r\nMaxUsers=50\r\n")).Bots);
    }

    [Fact]
    public void El_host_decide_cuantos()
    {
        Assert.Equal(7, ServerIni.Desde(IniFile.Parse("[INIT]\r\nBots=7\r\n")).Bots);
    }

    /// <summary>Un número disparatado no puede dejar al servidor intentando abrir mil sesiones, y un negativo
    /// no significa nada: los dos se recortan al tramo que existe.</summary>
    [Fact]
    public void El_numero_tiene_tope_y_piso()
    {
        Assert.Equal(ServerIni.MaxBots, ServerIni.Desde(IniFile.Parse("[INIT]\r\nBots=99999\r\n")).Bots);
        Assert.Equal(0, ServerIni.Desde(IniFile.Parse("[INIT]\r\nBots=-3\r\n")).Bots);
    }

    // ── Los nombres ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>El tope del ini es "los nombres que hay para repartir". Si alguien sube el tope y no agrega
    /// nombres, el host pediría bots que nunca aparecen — y el arranque no diría por qué.</summary>
    [Fact]
    public void Hay_un_nombre_por_cada_bot_que_el_ini_permite()
    {
        Assert.True(PobladoDeBots.Nombres.Count >= ServerIni.MaxBots,
            $"el ini permite {ServerIni.MaxBots} bots y hay {PobladoDeBots.Nombres.Count} nombres");
    }

    /// <summary>Los nuevos entran de a seis cada cinco minutos (no se amontonan en la cuna); los que ya tienen personaje, todos al
    /// arrancar, como siempre.</summary>
    [Fact]
    public void Los_bots_nuevos_entran_por_tandas_y_los_viejos_de_una()
    {
        Assert.Equal(0, PobladoDeBots.RetardoDeEntrada(esNuevo: true, 0));
        Assert.True(PobladoDeBots.RetardoDeEntrada(true, PobladoDeBots.NuevosPorTanda - 1) < 60_000);
        Assert.Equal(PobladoDeBots.EntreTandasMs, PobladoDeBots.RetardoDeEntrada(true, PobladoDeBots.NuevosPorTanda));
        Assert.Equal(2 * PobladoDeBots.EntreTandasMs, PobladoDeBots.RetardoDeEntrada(true, 2 * PobladoDeBots.NuevosPorTanda));
        Assert.True(PobladoDeBots.RetardoDeEntrada(esNuevo: false, 95) < 3 * 60_000);
    }

    /// <summary>Los nombres pasan por las MISMAS reglas que las de un jugador: la del personaje (sólo letras y
    /// espacios) y la de la cuenta, que lleva el sufijo. Un nombre que el servidor rechazaría sería un bot que
    /// no entra nunca.</summary>
    [Fact]
    public void Los_nombres_los_aceptaria_el_servidor()
    {
        foreach (var n in PobladoDeBots.Nombres.Concat(PobladoDeBots.Suplentes))
        {
            Assert.True(Naming.IsValidCharName(n), $"'{n}' no es un nombre de personaje válido");
            Assert.True(Naming.IsValidName(n + "_bot"), $"la cuenta de '{n}' no sería válida");
        }
    }

    /// <summary>Dos bots con el mismo nombre serían dos que pelean por el mismo personaje: el segundo no entra
    /// y el primero se lleva expulsiones sin explicación.</summary>
    [Fact]
    public void Los_nombres_no_se_repiten()
    {
        var todos = PobladoDeBots.Nombres.Concat(PobladoDeBots.Suplentes).ToList();
        Assert.Equal(todos.Count, todos.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    /// <summary>LOS DE RESERVA: con el máximo pedido y dos nombres que ya son de jugadores, entran igual el máximo, con los dos
    /// primeros de reserva al final. Con menos pedidos, el lugar del que falta lo toma el siguiente de la lista, como antes.</summary>
    [Fact]
    public void Un_nombre_tomado_lo_reemplaza_uno_de_reserva()
    {
        var tomados = new HashSet<string> { "Ciro", "Lira" };
        var todos = PobladoDeBots.NombresParaArrancar(ServerIni.MaxBots, tomados.Contains);
        Assert.Equal(ServerIni.MaxBots, todos.Count);
        Assert.DoesNotContain("Ciro", todos);
        Assert.DoesNotContain("Lira", todos);
        Assert.Equal(new[] { PobladoDeBots.Suplentes[0], PobladoDeBots.Suplentes[1] }, todos.TakeLast(2));

        var pocos = PobladoDeBots.NombresParaArrancar(10, tomados.Contains);
        Assert.Equal(PobladoDeBots.Nombres.Where(n => !tomados.Contains(n)).Take(10), pocos);
        Assert.Empty(PobladoDeBots.NombresParaArrancar(0, _ => false));

        Assert.Equal(PobladoDeBots.Nombres.Count, PobladoDeBots.LugarEnElPoblado(PobladoDeBots.Suplentes[0]));
        Assert.Equal(PobladoDeBots.Nombres.Count + 1, PobladoDeBots.LugarEnElPoblado(PobladoDeBots.Suplentes[1].ToUpperInvariant()));
    }

    /// <summary>EL NOMBRE QUE YA ES DE UN JUGADOR NO LO TOMA UN BOT: el personaje en otra cuenta, o la cuenta <c>_bot</c>
    /// registrada por una persona. El del bot mismo, sí.</summary>
    [Fact]
    public void El_nombre_que_ya_es_de_un_jugador_no_lo_toma_un_bot()
    {
        using var db = new Persistence(AlmacenSqlite.EnMemoria(), new CoverAO.Server.Game.Leveling(new Dictionary<int, long> { [1] = 300 }));
        var persona = db.CreateAccount("Persona", "clave");
        db.CreateCharacter(persona, "Milo", 1, race: 1, gender: 1, @class: 1, homeland: 1, head: 1);
        db.CreateAccount("Lira_bot", "clave");
        var deNadia = db.CreateAccount("Nadia_bot", "clave", esBot: true);
        db.CreateCharacter(deNadia, "Nadia", 2, race: 1, gender: 1, @class: 1, homeland: 1, head: 1);
        db.CreateAccount("Kael_bot", "clave", esBot: true);   // el bot creó su cuenta, y el personaje lo tomó otro
        db.CreateCharacter(persona, "Kael", 3, race: 1, gender: 1, @class: 1, homeland: 1, head: 1);

        Assert.True(PobladoDeBots.NombreAjeno("Milo", db));
        Assert.True(PobladoDeBots.NombreAjeno("Lira", db));
        Assert.True(PobladoDeBots.NombreAjeno("Kael", db));
        Assert.False(PobladoDeBots.NombreAjeno("Nadia", db));
        Assert.False(PobladoDeBots.NombreAjeno("Ciro", db));
    }

    /// <summary>
    /// EL NOMBRE TOMADO DESPUÉS DEL ARRANQUE: el bot lo mira antes de cada vida y deja el lugar sin intentar entrar (auditoría
    /// del 13/10: gastaba sus tres intentos contra "ya existe" y faltaba hasta reiniciar), y lo releva el primer nombre libre en
    /// el orden del arranque: con los de la lista tomados, el primero de reserva.
    /// </summary>
    [Fact]
    public async Task El_nombre_tomado_despues_del_arranque_lo_deja_y_lo_releva_el_siguiente()
    {
        using var db = new Persistence(AlmacenSqlite.EnMemoria(), new CoverAO.Server.Game.Leveling(new Dictionary<int, long> { [1] = 300 }));
        var credenciales = new CredencialesDeBots(Path.Combine(Path.GetTempPath(), "coverao_relevo_" + Guid.NewGuid().ToString("N")));
        var entorno = new EntornoDeBots(null!, null!, 0, 0, null!, null!, credenciales, db);
        var persona = db.CreateAccount("Persona", "clave");
        db.CreateCharacter(persona, "Milo", 1, race: 1, gender: 1, @class: 1, homeland: 1, head: 1);

        var vida = new Bot("Milo", null, entorno, semilla: 1).VivirAsync(CancellationToken.None);
        Assert.True(await vida.WaitAsync(TimeSpan.FromSeconds(10)));

        var poblado = new PobladoDeBots(entorno, credenciales, PobladoDeBots.Nombres);
        var relevo = poblado.Relevo();
        Assert.Equal(PobladoDeBots.Suplentes[0], relevo?.Nombre);
        Assert.Contains(relevo, poblado.Bots);
        Assert.Equal(PobladoDeBots.Suplentes[1], poblado.Relevo()?.Nombre);
    }

    /// <summary>LA CLAVE QUE NO LLEGÓ A <c>bots.txt</c> SE ESCRIBE EN EL PRÓXIMO LLAMADO, aunque sea con la misma clave: es
    /// el que hace el bot al entrar. Sin eso, en el próximo arranque nadie conocía la clave de su cuenta.</summary>
    [Fact]
    public void La_clave_que_no_llego_al_archivo_se_escribe_en_el_proximo_llamado()
    {
        string raiz = Path.Combine(Path.GetTempPath(), "coverao_botstxt_" + Guid.NewGuid().ToString("N"));
        string tomado = Path.Combine(raiz, "bots.txt");
        Directory.CreateDirectory(tomado);   // en el lugar del archivo, algo que no se deja pisar
        try
        {
            var credenciales = new CredencialesDeBots(raiz);
            Assert.False(credenciales.Guardar("Milo", "clave-de-prueba"));

            Directory.Delete(tomado);
            Assert.True(credenciales.Guardar("Milo", "clave-de-prueba"));
            Assert.Equal("clave-de-prueba", new CredencialesDeBots(raiz).Clave("Milo"));
        }
        finally { try { Directory.Delete(raiz, recursive: true); } catch (IOException) { } }
    }

    // ── El transporte ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// CADA BOT ES SU PROPIA DIRECCIÓN, y de eso depende que los cupos por dirección no los frenen a todos.
    ///
    /// <para><see cref="ConnectionGuard.IpOf"/> corta en el ÚLTIMO <c>:</c>, así que un endpoint sin el puerto
    /// ficticio del final (<c>bot:Aldo</c> en vez de <c>bot:Aldo:0</c>) dejaría a los N bots compartiendo la
    /// dirección <c>bot</c>: el cupo de cuentas nuevas por dirección los cortaría después de los primeros, y
    /// el motivo del rechazo hablaría de otra cosa. Es un detalle de formato que nada más recuerda.</para>
    /// </summary>
    [Fact]
    public void Cada_bot_cuenta_como_una_direccion_distinta()
    {
        string uno = ConnectionGuard.IpOf(new TransporteDeBot("Aldo").RemoteEndpoint);
        string otro = ConnectionGuard.IpOf(new TransporteDeBot("Bruma").RemoteEndpoint);
        Assert.NotEqual(uno, otro);
        Assert.Equal("bot:Aldo", uno);
    }

    /// <summary>El bot aprende la clave de sesión leyendo el <c>Logged</c>, igual que un cliente de verdad, y
    /// desde ahí enmascara con ella lo que manda. Si no la aprendiera, el servidor desenmascararía con una
    /// clave distinta y leería basura.</summary>
    [Fact]
    public async Task Aprende_la_clave_de_sesion_del_Logged_y_enmascara_con_ella()
    {
        var t = new TransporteDeBot("Ciro");
        Assert.Equal(Crypto.DefaultKey, t.Clave);

        t.Send(ServerMessages.Logged(77));
        Assert.Equal(77, t.Clave);

        var carga = new byte[] { 1, 2, 3, 250 };
        Assert.True(t.Mandar(carga));
        var buffer = new byte[16];
        int n = await t.ReceiveAsync(buffer, CancellationToken.None);

        Assert.Equal(carga.Length, n);
        for (int i = 0; i < n; i++)
            Assert.Equal(carga[i], (byte)(buffer[i] ^ 77));   // es lo que hace la sesión al recibirlo
    }

    /// <summary>Un paquete más largo que el buffer de la sesión se entrega en varias veces, sin perder ni
    /// reordenar nada. Con los paquetes del juego no pasa —son decenas de bytes contra un buffer de 4 KB— pero
    /// un transporte que pierde bytes cuando el buffer es chico está roto igual.</summary>
    [Fact]
    public async Task Un_paquete_mas_largo_que_el_buffer_no_pierde_bytes()
    {
        var t = new TransporteDeBot("Dalia");
        var carga = new byte[] { 10, 20, 30, 40, 50 };
        Assert.True(t.Mandar(carga));

        var recibido = new List<byte>();
        var chico = new byte[2];
        for (int vueltas = 0; recibido.Count < carga.Length && vueltas < 10; vueltas++)
        {
            int n = await t.ReceiveAsync(chico, CancellationToken.None);
            for (int i = 0; i < n; i++) recibido.Add((byte)(chico[i] ^ Crypto.DefaultKey));
        }
        Assert.Equal(carga, recibido.ToArray());
    }

    /// <summary>Que el bot se vaya tiene que verse desde la sesión como un peer que cerró el socket (0 bytes),
    /// que es lo que dispara el cierre ordenado: guardar el personaje y sacarlo del mundo.</summary>
    [Fact]
    public async Task Cuando_el_bot_se_va_la_sesion_ve_un_peer_cerrado()
    {
        var t = new TransporteDeBot("Elmo");
        t.Terminar();
        Assert.Equal(0, await t.ReceiveAsync(new byte[8], CancellationToken.None));
    }

    /// <summary>Con la sesión cerrada el bot no puede seguir mandando: sus paquetes no tienen a dónde ir.</summary>
    [Fact]
    public void Con_la_sesion_cerrada_no_se_manda_mas()
    {
        var t = new TransporteDeBot("Fabia");
        string motivo = "";
        t.Cerrado += m => motivo = m;

        t.Close("expulsado");

        Assert.Equal("expulsado", motivo);
        Assert.False(t.Mandar(new byte[] { 1 }));
    }

    // ── Volver a entrar ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// EL QUE YA ENTRÓ VUELVE A ENTRAR COMO CUALQUIERA. Su cuenta y su personaje existen, así que una vida que no
    /// llega al mundo —un plazo vencido, una excepción de su conducta— no lo manda a crearlos: parta de la forma
    /// que parta, la próxima es <c>Entrar</c>.
    /// </summary>
    [Theory]
    [InlineData(ComoEntrar.Entrar)]
    [InlineData(ComoEntrar.SoloPersonaje)]
    [InlineData(ComoEntrar.Alta)]
    public void El_que_ya_entro_no_vuelve_a_crear_la_cuenta(ComoEntrar como)
    {
        Assert.Equal(ComoEntrar.Entrar, Bot.DespuesDeUnFracaso(como, yaEntro: true));
    }

    /// <summary>EL CONTROL. El que no entró nunca sí prueba las tres formas en orden y vuelve a empezar: es lo que
    /// saca al bot que tiene la clave anotada pero no tiene la cuenta en los datos. Si esto también diera siempre
    /// <c>Entrar</c>, el test de arriba estaría verde por el motivo equivocado.</summary>
    [Fact]
    public void El_que_nunca_entro_prueba_las_tres_formas()
    {
        Assert.Equal(ComoEntrar.SoloPersonaje, Bot.DespuesDeUnFracaso(ComoEntrar.Entrar, yaEntro: false));
        Assert.Equal(ComoEntrar.Alta, Bot.DespuesDeUnFracaso(ComoEntrar.SoloPersonaje, yaEntro: false));
        Assert.Equal(ComoEntrar.Entrar, Bot.DespuesDeUnFracaso(ComoEntrar.Alta, yaEntro: false));
    }

    /// <summary>LOS CARTELES DEL SERVIDOR CERRADO POR UN RATO mandan a volver más tarde y no cuentan para rendirse: el
    /// mantenimiento, el modo sólo GMs, la creación cerrada y la base que no contesta, con el texto que manda el servidor. Los
    /// de la cuenta —la clave, el baneo, el nombre que ya existe— sí cuentan.</summary>
    [Fact]
    public void Los_carteles_del_servidor_cerrado_mandan_a_volver_mas_tarde()
    {
        static string Cartel(string campo) => (string)typeof(PacketDispatcher)
            .GetField(campo, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetRawConstantValue()!;

        foreach (var campo in new[] { "MensajeDeMantenimiento", "MensajeSoloGms", "MensajeCreacionCerrada", "CartelLaBaseNoResponde" })
            Assert.True(Bot.EsCartelDeVolverMasTarde(Cartel(campo)), campo);
        foreach (var campo in new[] { "CartelClaveIncorrecta", "CartelCuentaBaneada", "CartelYaExisteLaCuenta", "CartelYaExisteElPersonaje", "MensajeIpRepetida" })
            Assert.False(Bot.EsCartelDeVolverMasTarde(Cartel(campo)), campo);
    }

    /// <summary>LA ZONA NEWBIE CIERRA A PARTIR DEL NIVEL 16, con la misma cuenta que el servidor; las demás zonas, nunca.</summary>
    [Theory]
    [InlineData(15, "NEWBIE", true)]
    [InlineData(16, "NEWBIE", false)]
    [InlineData(16, " newbie ", false)]
    [InlineData(40, "CIUDAD", true)]
    public void La_zona_newbie_cierra_a_partir_del_nivel_16(int nivel, string zona, bool entra)
    {
        Assert.Equal(entra, Bot.PuedeEntrar(nivel, zona));
    }

    // ── Salir del pozo sin pociones ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// CON URGENCIA, EL VIAJE SIGUE SÓLO SI PARAR NO SIRVE: sin pociones que tomar y sin nada hostil cerca. Con pociones se para
    /// a tomar, y con algo que ataca se corta como siempre.
    /// </summary>
    [Theory]
    [InlineData(true, false, 0, false, true)]
    [InlineData(true, false, 3, false, false)]
    [InlineData(true, false, 0, true, false)]
    [InlineData(false, false, 0, false, false)]
    [InlineData(true, true, 0, false, false)]
    public void Con_urgencia_sigue_caminando_solo_si_parar_no_sirve(bool vivo, bool deMuerto, int pociones, bool hostilCerca, bool sigue)
    {
        Assert.Equal(sigue, Bot.SigueCaminandoConUrgencia(vivo, deMuerto, pociones, hostilCerca));
    }

    /// <summary>SIN POCIONES Y CON LA VIDA BAJA TOCA EL SACERDOTE; con pociones se toma una, y con la vida alta no hace falta.</summary>
    [Theory]
    [InlineData(0.30, 0, true)]
    [InlineData(0.30, 5, false)]
    [InlineData(0.90, 0, false)]
    public void Sin_pociones_y_con_la_vida_baja_toca_el_sacerdote(double salud, int pociones, bool toca)
    {
        Assert.Equal(toca, Bot.TocaSanarse(salud, pociones));
    }
}
