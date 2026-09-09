using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// Los bots: personajes que juegan solos, apagados salvo que el host los pida.
///
/// <para>Lo que se cuida acá no es que caminen —eso se ve corriéndolos— sino las tres cosas que, si se
/// rompen, se rompen en silencio: que vengan apagados, que cada bot cuente como su propia dirección para los
/// cupos, y que el transporte no pierda ni deforme un byte.</para>
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

    /// <summary>Los nombres pasan por las MISMAS reglas que las de un jugador: la del personaje (sólo letras y
    /// espacios) y la de la cuenta, que lleva el sufijo. Un nombre que el servidor rechazaría sería un bot que
    /// no entra nunca.</summary>
    [Fact]
    public void Los_nombres_los_aceptaria_el_servidor()
    {
        foreach (var n in PobladoDeBots.Nombres)
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
        Assert.Equal(PobladoDeBots.Nombres.Count,
            PobladoDeBots.Nombres.Distinct(StringComparer.OrdinalIgnoreCase).Count());
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
}
