using CoverAO.Server.Data;
using CoverAO.Server.Game;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// El <c>Server.ini</c>: la única perilla del que HOSTEA. Lo que se testea acá no es cosmético — de este
/// parseo depende <b>quién puede usar los ~70 comandos GM</b>, y un error silencioso (una clave mal leída, un
/// contador que no cuadra) deja al host sin poder administrar su propio servidor sin darse cuenta.
/// </summary>
public class ServerIniTests
{
    private static ServerIni Parse(string texto) => ServerIni.Desde(IniFile.Parse(texto), "test.ini");

    [Fact]
    public void Sin_archivo_arranca_con_defaults_y_sin_staff()
    {
        var cfg = ServerIni.Cargar(null);
        Assert.Null(cfg.Ruta);
        Assert.Equal(7666, cfg.StartPort);
        Assert.Equal(0, cfg.CantidadStaff);
        Assert.True(cfg.PuedeCrearPersonajes);
        Assert.True(cfg.AllowMultiLogins);
        Assert.False(cfg.ServerSoloGms);
        Assert.Equal(1, cfg.Exp);
        Assert.Equal(1, cfg.Oro);
        // Sin ini nadie es GM: es la diferencia entre "el server no está configurado" y "cualquiera manda".
        Assert.Equal(1, cfg.PrivilegioDe("Rugura"));
        Assert.False(cfg.EsStaff("Rugura"));
    }

    [Fact]
    public void Encuentra_el_archivo_aunque_el_nombre_este_en_otra_capitalizacion()
    {
        // En Linux el nombre del archivo distingue mayúsculas: un host que guarda "server.ini" en minúscula
        // vería el servidor arrancar con los defaults y SIN GMs, sin ninguna pista de por qué. En Windows este
        // test pasa trivialmente (el FS ya es case-insensitive); en Linux es el que cubre el agujero.
        string dir = Path.Combine(Path.GetTempPath(), "coverao-ini-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "server.ini"), "[INIT]\nDioses=1\n[Dioses]\nDios1=Thorin\n");
            var cfg = ServerIni.Cargar(Path.Combine(dir, "Server.ini"));   // se pide con otra capitalización
            Assert.NotNull(cfg.Ruta);
            Assert.Equal(8, cfg.PrivilegioDe("Thorin"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Un_directorio_sin_ini_no_revienta()
    {
        string dir = Path.Combine(Path.GetTempPath(), "coverao-ini-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            // Ni con vecinos que se le parecen: la búsqueda tolerante compara el nombre COMPLETO, no un prefijo.
            File.WriteAllText(Path.Combine(dir, "Server.ini.ejemplo"), "[INIT]\nDioses=1\n[Dioses]\nDios1=Thorin\n");
            var cfg = ServerIni.Cargar(Path.Combine(dir, "Server.ini"));
            Assert.Null(cfg.Ruta);
            Assert.Equal(0, cfg.CantidadStaff);
        }
        finally { Directory.Delete(dir, true); }
        // Y un directorio que ni existe tampoco tira excepción.
        Assert.Null(ServerIni.Cargar(Path.Combine(dir, "Server.ini")).Ruta);
    }

    [Fact]
    public void Lee_los_ajustes_de_INIT()
    {
        var cfg = Parse("""
            [INIT]
            StartPort=8000
            MaxUsers=250
            AllowMultiLogins=0
            LimiteConexionesPorIp=4
            PuedeCrearPersonajes=0
            ServerSoloGMs=1
            IdleLimit=15
            """);
        Assert.Equal(8000, cfg.StartPort);
        Assert.Equal(250, cfg.MaxUsers);
        Assert.False(cfg.AllowMultiLogins);
        Assert.Equal(4, cfg.LimiteConexionesPorIp);
        Assert.False(cfg.PuedeCrearPersonajes);
        Assert.True(cfg.ServerSoloGms);
        Assert.Equal(15, cfg.IdleLimitMinutos);
    }

    [Theory]
    // Exp/Oro del original son un MULTIPLICADOR directo (`.GiveEXP = GiveEXP * Expc`, MODULO_NPCs.bas:996): el
    // `Exp=200` del install es ×200. Se leían como porcentaje, con un `/ 100` que el VB6 no tiene.
    [InlineData(1, 1)] [InlineData(5, 5)] [InlineData(100, 100)] [InlineData(200, 200)]
    [InlineData(0, 1)]       // piso 1: nadie quiere un server que no da experiencia
    [InlineData(-500, 1)]
    [InlineData(99_999, ServerIni.TasaMaxima)]   // el Integer del original
    public void Los_rates_son_un_multiplicador_directo(int escrito, int multiplicador)
    {
        var cfg = Parse($"[INIT]\nExp={escrito}\nOro={escrito}\n");
        Assert.Equal(multiplicador, cfg.Exp);
        Assert.Equal(multiplicador, cfg.Oro);
    }

    /// <summary>
    /// EL Server.ini DE ANTES. La plantilla traía <c>Exp=100</c> como lo normal y al actualizar el armador conserva
    /// el ini del host: sin aviso, ese server pasaba a dar cien veces la experiencia sin que nadie lo decidiera.
    /// Los porcentajes de antes eran múltiplos de 100, y ése es el caso que tiene que avisar; una tasa elegida
    /// con la escala nueva no.
    /// </summary>
    [Theory]
    [InlineData(100, 100, true)]    // la plantilla vieja, tal cual
    [InlineData(200, 200, true)]    // el install del original, leído con la escala vieja
    [InlineData(1, 300, true)]      // con que una de las dos lo sea
    [InlineData(1, 1, false)]
    [InlineData(5, 10, false)]
    [InlineData(150, 1, false)]     // no es un porcentaje redondo: alguien lo eligió
    public void Avisa_cuando_la_tasa_parece_un_porcentaje_de_antes(int exp, int oro, bool avisa)
    {
        var cfg = Parse($"[INIT]\nExp={exp}\nOro={oro}\n");
        var aviso = cfg.AvisoDeTasaVieja();
        Assert.Equal(avisa, aviso is not null);
        if (avisa) Assert.Contains($"x{exp}", aviso);
    }

    [Fact]
    public void Lee_las_perillas_de_operacion()
    {
        var cfg = Parse("[INIT]\nMinutosGuardarUsuarios=15\nMinutosBackup=60\nBackupsAConservar=10\nLogDias=30\n");
        Assert.Equal(15, cfg.AutosaveMinutos);
        Assert.Equal(60, cfg.BackupMinutos);
        Assert.Equal(10, cfg.BackupsAConservar);
        Assert.Equal(30, cfg.LogDias);
    }

    /// <summary>Sin claves, el servidor se cuida SOLO: autosave, backup y log andando.
    ///
    /// Este test decía lo contrario —autosave 0 y backup 0— con el argumento de que era "una decisión del
    /// host". El problema es que el servidor arranca perfectamente sin <c>Server.ini</c>, así que esa
    /// decisión se tomaba sola y para el lado peligroso: quien borraba el archivo, le erraba al nombre, o
    /// levantaba la imagen de Docker configurando por variables de entorno, se quedaba sin backups y sin
    /// autosave <b>sin enterarse</b>. Los valores son los mismos que trae el Server.ini del paquete, para que
    /// el servidor no se comporte distinto según si el archivo está o no.</summary>
    [Fact]
    public void Sin_claves_el_servidor_se_cuida_solo()
    {
        var cfg = Parse("[INIT]\n");
        Assert.Equal(15, cfg.AutosaveMinutos);
        Assert.Equal(30, cfg.BackupMinutos);        // una copia cada media hora
        Assert.Equal(7, cfg.LogDias);
        Assert.Equal(48, cfg.BackupsAConservar);    // 48 x 30 min = un dia REAL de historia hacia atras
    }

    /// <summary>Y el host los apaga si quiere: un <c>0</c> explícito manda sobre el default. Es la otra mitad
    /// del contrato — defaults sanos, pero de la config no se adueña nadie.</summary>
    [Fact]
    public void Un_cero_explicito_los_apaga()
    {
        var cfg = Parse("[INIT]\nMinutosGuardarUsuarios=0\nMinutosBackup=0\n");
        Assert.Equal(0, cfg.AutosaveMinutos);
        Assert.Equal(0, cfg.BackupMinutos);
    }

    [Fact]
    public void Un_valor_absurdo_de_operacion_se_acota()
    {
        // Un backup por minuto durante un año no puede convertirse en un problema operativo silencioso.
        var cfg = Parse("[INIT]\nMinutosGuardarUsuarios=99999\nMinutosBackup=99999\nBackupsAConservar=99999\nLogDias=-3\n");
        Assert.Equal(240, cfg.AutosaveMinutos);
        Assert.Equal(1440, cfg.BackupMinutos);
        Assert.Equal(500, cfg.BackupsAConservar);
        Assert.Equal(0, cfg.LogDias);
    }

    [Fact]
    public void Carga_las_listas_de_staff_con_su_privilegio()
    {
        var cfg = Parse("""
            [INIT]
            Admines=1
            Dioses=2
            SemiDioses=1
            Consejeros=1
            RolesMasters=1
            [Admines]
            Admin1=Thorin
            [Dioses]
            Dios1=Rugura
            Dios2=Merlin
            [SemiDioses]
            SemiDios1=Semi
            [Consejeros]
            Consejero1=Conse
            [RolesMasters]
            RM1=Rolero
            """);
        Assert.Equal(6, cfg.CantidadStaff);
        Assert.Equal(16, cfg.PrivilegioDe("Thorin"));
        Assert.Equal(8, cfg.PrivilegioDe("Rugura"));
        Assert.Equal(8, cfg.PrivilegioDe("Merlin"));
        Assert.Equal(4, cfg.PrivilegioDe("Semi"));
        Assert.Equal(2, cfg.PrivilegioDe("Conse"));
        Assert.Equal(32, cfg.PrivilegioDe("Rolero"));
        Assert.Equal(1, cfg.PrivilegioDe("UnJugadorCualquiera"));
    }

    [Fact]
    public void El_nombre_no_distingue_mayusculas_y_admite_el_prefijo_del_original()
    {
        // El Server.ini del install trae nombres con '*' o '+' adelante; el VB6 los descarta.
        var cfg = Parse("[INIT]\nDioses=2\n[Dioses]\nDios1=*RUGURA\nDios2=+ Merlin\n");
        Assert.Equal(8, cfg.PrivilegioDe("rugura"));
        Assert.Equal(8, cfg.PrivilegioDe("RuGuRa"));
        Assert.Equal(8, cfg.PrivilegioDe("Merlin"));   // el espacio tras el '+' se recorta
    }

    [Fact]
    public void El_contador_manda_sobre_las_entradas_que_sobran()
    {
        // Dioses=1 con dos entradas: la segunda NO se carga. Es como cuenta el original (For i = 1 To N) y es
        // lo que hace que bajar el contador alcance para sacar a alguien sin borrar su línea.
        var cfg = Parse("[INIT]\nDioses=1\n[Dioses]\nDios1=Rugura\nDios2=Colado\n");
        Assert.Equal(1, cfg.CantidadStaff);
        Assert.Equal(1, cfg.PrivilegioDe("Colado"));
    }

    [Fact]
    public void Un_contador_sin_seccion_no_revienta()
    {
        // Error de tipeo clásico: se declara el contador y se olvida la sección. No puede tirar el arranque.
        var cfg = Parse("[INIT]\nDioses=3\n");
        Assert.Equal(0, cfg.CantidadStaff);
    }

    [Fact]
    public void Un_nombre_en_dos_listas_se_queda_con_el_rango_mas_alto()
    {
        // El original resuelve con una cadena de ElseIf ordenada de mayor a menor autoridad.
        var cfg = Parse("[INIT]\nAdmines=1\nConsejeros=1\n[Admines]\nAdmin1=Thorin\n[Consejeros]\nConsejero1=Thorin\n");
        Assert.Equal(1, cfg.CantidadStaff);
        Assert.Equal(16, cfg.PrivilegioDe("Thorin"));
    }

    [Fact]
    public void El_desempate_no_puede_ir_por_el_valor_del_bit()
    {
        // El caso que rompe la implementación "ingenua": el bit de RoleMaster (32) es MÁS GRANDE que el de
        // Admin (16), y no por eso manda más. El desempate va por orden de autoridad, no por número.
        var cfg = Parse("[INIT]\nAdmines=1\nRolesMasters=1\n[Admines]\nAdmin1=Thorin\n[RolesMasters]\nRM1=Thorin\n");
        Assert.Equal(16, cfg.PrivilegioDe("Thorin"));
        // Y en el orden inverso de declaración da lo mismo: manda la lista, no el orden del archivo.
        var cfg2 = Parse("[INIT]\nRolesMasters=1\nAdmines=1\n[RolesMasters]\nRM1=Thorin\n[Admines]\nAdmin1=Thorin\n");
        Assert.Equal(16, cfg2.PrivilegioDe("Thorin"));
    }

    [Theory]
    // TCP.bas:1941. No es cosmético: este byte viaja en el CharacterCreate y es lo que hace que el cliente
    // pinte el nick con el color de GM (ColorNombresPriv, Case 7,8,9).
    [InlineData(16, 9)]  // Admin
    [InlineData(8, 9)]   // Dios
    [InlineData(4, 8)]   // SemiDios
    [InlineData(2, 7)]   // Consejero
    [InlineData(32, 7)]  // RoleMaster
    [InlineData(1, 0)]   // usuario común: 0 = no se toca el status de facción
    public void El_status_de_GM_es_el_que_pinta_el_nick(int priv, int status) =>
        Assert.Equal((byte)status, ServerIni.StatusDeStaff((byte)priv));

    /// <summary>
    /// LA TASA DEL INI NO PRENDE UN EVENTO. Los multiplicadores del panel son el evento del GM (<c>/EXP</c>,
    /// <c>/ORO</c>) y arrancan apagados. Vivían en el mismo campo que la tasa del server, y un <c>/EXP</c> sobre
    /// un server de ×10 la tomaba por un evento en curso y la bajaba a ×1; los avisos de "evento en curso"
    /// también anunciaban la tasa como si fuera un evento.
    /// </summary>
    [Fact]
    public void La_tasa_del_ini_no_prende_un_evento_en_el_panel()
    {
        var cfg = Parse("[INIT]\nExp=10\nOro=3\nServerSoloGMs=1\n");
        var admin = new AdminConfig();
        cfg.AplicarA(admin);
        Assert.Equal(1, admin.ExpMult);
        Assert.Equal(1, admin.GoldMult);
        Assert.True(admin.OnlyGms);                 // lo demás sí se vuelca
        Assert.Equal(10, cfg.Exp);                  // y la tasa queda donde se la lee en cada pago
    }

    [Fact]
    public void El_status_de_GM_no_se_persiste_encima_de_la_faccion()
    {
        // El agujero que esto tapa: hacer GM a un Armada Real le pisaba el status con 9, y 9 no es un status
        // legal — al sacarlo de la lista, el saneo del cargador se lo devolvía como ciudadano sin ejército.
        var p = new Player { CharIndex = 1, Name = "Thorin", Account = "cuenta",
                             FactionStatus = Player.StatusArmada, Privileges = 1 };
        p.PrivilegioCivilBajoStaff = p.Privileges;
        p.StatusCivilBajoStaff = p.FactionStatus;
        p.Privileges = 8;
        p.FactionStatus = ServerIni.StatusDeStaff(8);

        Assert.Equal(9, p.FactionStatus);                      // lo que ve el cliente: color de GM
        Assert.Equal(Player.StatusArmada, p.FactionStatusPersistido);   // lo que va al .chr: su facción real
        Assert.Equal(1, p.PrivilegiosPersistidos);             // y el rango NO se guarda: lo decide el ini
    }

    [Fact]
    public void Un_cambio_de_faccion_bajo_el_override_no_le_apaga_el_nick_al_GM()
    {
        // Sin esto, un GM que le pega a un ciudadano se volvía criminal EN VIVO: el status 9 se perdía y el
        // nick dejaba de verse como GM hasta el próximo login. Y peor, el cambio no llegaba al .chr — se
        // persistía el status civil viejo, así que la sanción se evaporaba al reloguear.
        var p = new Player { CharIndex = 1, Name = "Thorin", Account = "cuenta",
                             FactionStatus = Player.StatusCiudadano, Privileges = 1 };
        p.StatusCivilBajoStaff = p.FactionStatus;
        p.FactionStatus = ServerIni.StatusDeStaff(8);

        p.CambiarFaccion(Player.StatusCriminal);
        Assert.Equal(9, p.FactionStatus);                                 // sigue con el color de GM
        Assert.Equal(Player.StatusCriminal, p.FactionStatusPersistido);   // y la sanción sí se guarda
    }

    [Fact]
    public void Los_relojes_del_mundo_se_configuran_en_minutos()
    {
        int diaPrevio = DayNight.PeriodTicks, cicloPrevio = Weather.CicloTicks, lluviaPrevia = Weather.LluviaPorciento;
        try
        {
            Parse("[INIT]\nMinutosDelDia=40\nMinutosDeClima=10\nPorcentajeLluvia=50\n").AplicarRelojes();
            // 40 min de día / 4 períodos = 10 min por período = 15000 vueltas de 40 ms.
            Assert.Equal(40 * 60_000 / GameLoop.TickMs / 4, DayNight.PeriodTicks);
            Assert.Equal(10 * 60_000 / GameLoop.TickMs, Weather.CicloTicks);
            Assert.Equal(50, Weather.LluviaPorciento);
            // Y el efecto de verdad: con la mitad del ciclo lloviendo, el minuto 4 llueve y el 6 no.
            Assert.Equal(WeatherState.Lluvia, Weather.At(4L * 60_000 / GameLoop.TickMs));
            Assert.Equal(WeatherState.Despejado, Weather.At(6L * 60_000 / GameLoop.TickMs));
        }
        finally
        {
            DayNight.PeriodTicks = diaPrevio; Weather.CicloTicks = cicloPrevio; Weather.LluviaPorciento = lluviaPrevia;
        }
    }

    [Fact]
    public void Un_dia_de_cero_minutos_no_hace_amanecer_25_veces_por_segundo()
    {
        // El typo que se comete editando a mano. Sin piso, PeriodTicks queda en 0 → el mundo cambia de
        // período en CADA vuelta del loop, con su aviso de chat cada vez, y el server queda inusable.
        int diaPrevio = DayNight.PeriodTicks, cicloPrevio = Weather.CicloTicks, lluviaPrevia = Weather.LluviaPorciento;
        try
        {
            var cfg = Parse("[INIT]\nMinutosDelDia=0\nMinutosDeClima=-5\nPorcentajeLluvia=500\n");
            Assert.Equal(1, cfg.MinutosDelDia);
            Assert.Equal(1, cfg.MinutosDeClima);
            Assert.Equal(100, cfg.PorcentajeLluvia);
            cfg.AplicarRelojes();
            Assert.True(DayNight.PeriodTicks >= 1);
            Assert.True(Weather.CicloTicks >= 1);
        }
        finally
        {
            DayNight.PeriodTicks = diaPrevio; Weather.CicloTicks = cicloPrevio; Weather.LluviaPorciento = lluviaPrevia;
        }
    }

    /// <summary>Ubica un archivo del repo subiendo directorios desde donde corre el test (bin/…).</summary>
    private static string BuscarEnElRepo(string relativo)
    {
        for (var dir = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var cand = System.IO.Path.Combine(dir.FullName, relativo);
            if (System.IO.File.Exists(cand)) return cand;
        }
        throw new System.IO.FileNotFoundException($"no encontré {relativo} subiendo desde {System.AppContext.BaseDirectory}");
    }

    /// <summary>
    /// El autosave sale de <c>[INTERVALOS] IntervaloGuardarUsuarios</c>, que es la clave que lee el VB6
    /// (<c>FileIO.bas:1511</c>). Acá se leía <c>[INIT] MinutosGuardarUsuarios</c> —el nombre de la VARIABLE
    /// donde el original guarda ese valor (<c>Admin.bas:42</c>), no el de la clave—, así que contra el
    /// Server.ini real la búsqueda fallaba, caía al default 0 y el autosave quedaba apagado.
    ///
    /// Se testea contra el ARCHIVO REAL que distribuimos, no contra un string de laboratorio: el bug no era
    /// que el parseo estuviera mal, era que nadie lo había cruzado contra el ini del install.
    /// </summary>
    [Fact]
    public void El_Server_ini_que_distribuimos_prende_el_autosave()
    {
        var cfg = ServerIni.Cargar(BuscarEnElRepo(System.IO.Path.Combine("src", "CoverAO-2021", "Servidor", "Server.ini")));
        Assert.Equal(15, cfg.AutosaveMinutos);   // IntervaloGuardarUsuarios=15 en [INTERVALOS]
    }

    /// <summary>
    /// Los cinco relojes de combate salen del <c>[INTERVALOS]</c> del Server.ini del install, no de números
    /// cableados. El de golpe estaba en 1200 en <c>Program.cs</c> contra los 1000 del archivo: se peleaba un
    /// 20% más lento que el original y nadie lo notaba porque el gate no falla, sólo tarda distinto.
    /// </summary>
    [Fact]
    public void El_Server_ini_que_distribuimos_manda_los_cinco_relojes_de_combate()
    {
        var cfg = ServerIni.Cargar(BuscarEnElRepo(System.IO.Path.Combine("src", "CoverAO-2021", "Servidor", "Server.ini")));
        Assert.Equal(1000, cfg.IntervaloCastear);      // IntervaloLanzaHechizo
        Assert.Equal(400, cfg.IntervaloMagiaGolpe);
        Assert.Equal(400, cfg.IntervaloGolpeMagia);
        Assert.Equal(400, cfg.IntervaloGolpeUsar);
        Assert.Equal(1000, cfg.IntervaloAtacar);       // IntervaloUserPuedeAtacar (estaba cableado en 1200)
        Assert.Equal(700, cfg.IntervaloTrabajo);       // IntervaloTrabajo: estaba en el archivo y no se leia
    }

    /// <summary>La cadencia de TRABAJAR sale del ini, y hasta el 2026-09-03 no salia de ningun lado: la clave
    /// estaba escrita en el Server.ini del install y el port la ignoraba, con el 700 cableado en el codigo.
    /// El original la usa en DOS lugares con el mismo contador — la faena continua y el ROBO.</summary>
    [Fact]
    public void La_cadencia_de_trabajar_sale_del_ini()
    {
        const string conClave = "[INTERVALOS]\nIntervaloTrabajo=1500\n";
        Assert.Equal(700, ServerIni.Cargar(null).IntervaloTrabajo);              // sin archivo, el del install
        Assert.Equal(1500, Parse(conClave).IntervaloTrabajo);
        // El 0 es legal en esta familia: apaga el reloj, como el resto de los intervalos de combate.
        Assert.Equal(0, Parse("[INTERVALOS]\nIntervaloTrabajo=0\n").IntervaloTrabajo);
        // Y en [INIT] no la tiene que encontrar: es una seccion del original y la clave vive en [INTERVALOS].
        Assert.Equal(700, Parse("[INIT]\nIntervaloTrabajo=1500\n").IntervaloTrabajo);
        // El escape del fixture cuelga de que la clave este ESCRITA, asi que tiene que verse como tal.
        Assert.True(Parse(conClave).IntervaloEscrito("IntervaloTrabajo"));
        Assert.False(Parse("[INTERVALOS]\nIntervaloGolpeUsar=400\n").IntervaloEscrito("IntervaloTrabajo"));
    }

    /// <summary>Los segundos de la cuenta de salida salen del ini, como en el original (FileIO.bas:1512); estaban
    /// cableados en 5.</summary>
    [Fact]
    public void La_cuenta_de_salida_sale_del_ini()
    {
        Assert.Equal(5, ServerIni.Cargar(null).IntervaloCerrarConexion);
        Assert.Equal(12, Parse("[INTERVALOS]\nIntervaloCerrarConexion=12\n").IntervaloCerrarConexion);
        Assert.Equal(0, Parse("[INTERVALOS]\nIntervaloCerrarConexion=0\n").IntervaloCerrarConexion);
        Assert.Equal(5, Parse("[INIT]\nIntervaloCerrarConexion=12\n").IntervaloCerrarConexion);
    }

    [Fact]
    public void El_autosave_lee_la_clave_del_VB6_y_tambien_la_vieja()
    {
        // La clave del original manda.
        Assert.Equal(15, Parse("[INTERVALOS]\nIntervaloGuardarUsuarios=15\n").AutosaveMinutos);
        // Y si alguien ya escribió la que inventamos, se le sigue respetando.
        Assert.Equal(7, Parse("[INIT]\nMinutosGuardarUsuarios=7\n").AutosaveMinutos);
        // Sin ninguna de las dos: el default, que ahora es PRENDIDO (ver `Sin_claves_el_servidor_se_cuida_solo`).
        // Acá decía 0 y este assert es el que avisó del cambio: el default vive en un solo lugar y hay dos
        // tests que lo miran.
        Assert.Equal(15, Parse("[INIT]\nStartPort=7666\n").AutosaveMinutos);
        // El tope sigue acotando (240 = 4 horas).
        Assert.Equal(240, Parse("[INTERVALOS]\nIntervaloGuardarUsuarios=99999\n").AutosaveMinutos);
    }

    [Fact]
    public void El_puerto_publico_del_websocket_solo_vale_si_lo_ponen_y_es_valido()
    {
        // Por defecto NO se anuncia nada distinto: el 99% de los hosts no remapea puertos y tiene que
        // seguir funcionando sin tocar el ini.
        Assert.Equal(0, Parse("[INIT]\nStartPort=7666\n").PuertoWebSocketPublico);
        Assert.Equal(25001, Parse("[INIT]\nPuertoWebSocketPublico=25001\n").PuertoWebSocketPublico);

        // Fuera de rango se ignora y vale el real. Anunciarle al cliente un puerto imposible lo dejaria sin
        // poder conectarse NUNCA, y con un sintoma que no señala al ini: la pagina carga y el socket falla.
        foreach (var basura in new[] { "0", "-1", "70000", "99999", "hola" })
            Assert.Equal(0, Parse($"[INIT]\nPuertoWebSocketPublico={basura}\n").PuertoWebSocketPublico);
    }

    [Fact]
    public void El_cliente_recibe_el_puerto_anunciado_y_no_el_real()
    {
        // Lo que de verdad importa: que el JSON que se baja el cliente lleve el puerto de AFUERA. El
        // servidor sigue escuchando en el suyo; lo unico que cambia es lo que se le dice al de afuera.
        var raiz = Path.Combine(Path.GetTempPath(), "coverao-ws-publico-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(raiz);
        try
        {
            var conRemapeo = new CoverAO.Server.Net.StaticFileServer(7669, raiz, 7667, "", 25001);
            Assert.Contains("\"wsPort\":25001", conRemapeo.ConfigJson);

            // Y sin la clave (0), el de siempre: el real.
            var normal = new CoverAO.Server.Net.StaticFileServer(7669, raiz, 7667, "", 0);
            Assert.Contains("\"wsPort\":7667", normal.ConfigJson);
        }
        finally { Directory.Delete(raiz, recursive: true); }
    }

    /// <summary>EL ALCANCE DEL HECHIZO (pedido del autor, 2026-09-27): viene prendido, se apaga con 0, y el cliente se entera
    /// por el mismo JSON que el puerto.</summary>
    [Fact]
    public void El_alcance_del_hechizo_viene_prendido_y_le_llega_al_cliente()
    {
        Assert.True(Parse("[INIT]\nStartPort=7666\n").AlcanceDelHechizo);
        Assert.False(Parse("[INIT]\nAlcanceDelHechizo=0\n").AlcanceDelHechizo);
        Assert.Contains("AlcanceDelHechizo", ServerIni.ClavesConocidas);

        var raiz = Path.Combine(Path.GetTempPath(), "coverao-alcance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(raiz);
        try
        {
            Assert.Contains("\"alcanceHechizo\":true", new CoverAO.Server.Net.StaticFileServer(7669, raiz, 7667).ConfigJson);
            var apagado = new CoverAO.Server.Net.StaticFileServer(7669, raiz, 7667, alcanceDelHechizo: false);
            Assert.Contains("\"alcanceHechizo\":false", apagado.ConfigJson);
            using var json = System.Text.Json.JsonDocument.Parse(apagado.ConfigJson);   // y sigue siendo JSON válido
            Assert.Equal(7667, json.RootElement.GetProperty("wsPort").GetInt32());
        }
        finally { Directory.Delete(raiz, recursive: true); }
    }

    [Fact]
    public void Sin_override_cambiar_faccion_es_un_set_comun()
    {
        // La rama que corren todos los jugadores (y todos los flows): tiene que ser indistinguible del set.
        var p = new Player { CharIndex = 1, Name = "Rugura", Account = "cuenta",
                             FactionStatus = Player.StatusCiudadano };
        p.CambiarFaccion(Player.StatusArmada);
        Assert.Equal(Player.StatusArmada, p.FactionStatus);
        Assert.Equal(Player.StatusArmada, p.FactionStatusPersistido);
    }
}
