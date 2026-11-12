using CoverAO.Server.Data;

namespace CoverAO.Protocol.Tests;

[CollectionDefinition(nameof(EntornoDelPanelDeStaff), DisableParallelization = true)]
public class EntornoDelPanelDeStaff { }

/// <summary>
/// DAR UN RANGO DESDE EL PANEL TIENE QUE SERVIR DE VERDAD.
///
/// <para>El panel de administración escribe el <c>Server.ini</c> con <see cref="ServerIni.GuardarStaff"/> y el
/// arranque lo lee con <see cref="ServerIni.Cargar"/>. Son las dos mitades de un par, y estaban desacopladas:
/// la que ESCRIBE ponía el contador adentro de la sección (<c>[Dioses] Dioses=1</c>) y la que LEE lo busca en
/// <c>[INIT]</c>. Resultado: ascender a alguien desde el panel no le daba el rango —ni en el momento, porque
/// <c>Recargar</c> vuelve a pasar por <c>Cargar</c>— y el panel igual contestaba
/// <i>"queda como Dios. Toma efecto en su próximo login"</i>.</para>
///
/// <para>Nadie lo cubría: no había un solo test sobre <c>GuardarStaff</c>. Y no se ve leyendo ninguna de las
/// dos funciones por separado —cada una es coherente consigo misma—, sólo mirando el PAR. Se encontró
/// ejecutándolo: ascender a Merlin desde la API y después entrar con él, que seguía sin poder usar
/// <c>/GRABAR</c>.</para>
/// </summary>
[Collection(nameof(EntornoDelPanelDeStaff))]
public class PanelDeStaffTests
{
    private static string IniNuevo(params string[] lineas)
    {
        string dir = Path.Combine(Path.GetTempPath(), "coverao-staff-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string ruta = Path.Combine(dir, "Server.ini");
        File.WriteAllLines(ruta, lineas);
        return ruta;
    }

    private static void Limpiar(string ruta)
    {
        var dir = Path.GetDirectoryName(ruta)!;
        if (Directory.Exists(dir)) { try { Directory.Delete(dir, recursive: true); } catch (IOException) { } }
    }

    [Theory]
    [InlineData((byte)16)]   // Admin
    [InlineData((byte)8)]    // Dios
    [InlineData((byte)4)]    // SemiDios
    [InlineData((byte)2)]    // Consejero
    [InlineData((byte)32)]   // RoleMaster
    public void Un_rango_dado_desde_el_panel_lo_ve_el_arranque(byte priv)
    {
        string ruta = IniNuevo("[INIT]", "MaxUsers=0");
        try
        {
            var ini = ServerIni.Cargar(ruta);
            Assert.True(ini.GuardarStaff("Merlin", priv), "GuardarStaff dijo que no pudo escribir");

            // 1) En el acto: GuardarStaff termina con un Recargar, así que el server vivo tiene que verlo ya.
            Assert.Equal(priv, ini.PrivilegioDe("Merlin"));

            // 2) Y DESPUÉS DE REINICIAR, que es lo que de verdad se rompía: una lectura desde cero del archivo.
            Assert.Equal(priv, ServerIni.Cargar(ruta).PrivilegioDe("Merlin"));
        }
        finally { Limpiar(ruta); }
    }

    [Fact]
    public void Sacarle_el_rango_a_alguien_tambien_sobrevive_al_reinicio()
    {
        string ruta = IniNuevo("[INIT]", "MaxUsers=0");
        try
        {
            var ini = ServerIni.Cargar(ruta);
            ini.GuardarStaff("Merlin", 8);
            ini.GuardarStaff("Rugura", 8);
            Assert.Equal(8, ServerIni.Cargar(ruta).PrivilegioDe("Rugura"));

            // priv 1 = User: lo saca de todas las listas.
            Assert.True(ini.GuardarStaff("Merlin", 1));
            var despues = ServerIni.Cargar(ruta);
            Assert.Equal(1, despues.PrivilegioDe("Merlin"));
            Assert.Equal(8, despues.PrivilegioDe("Rugura"));   // al otro no se lo lleva puesto
        }
        finally { Limpiar(ruta); }
    }

    [Theory]
    [InlineData("*Merlin", (byte)1)]
    [InlineData("+Merlin", (byte)1)]
    [InlineData("* Merlin", (byte)2)]
    [InlineData("+ Merlin", (byte)2)]
    public void Cambiar_el_rango_quita_las_entradas_con_prefijos_heredados(string entrada, byte nuevoRango)
    {
        string ruta = IniNuevo("[INIT]", "Admines=2", "Dioses=1", "[Admines]", $"Admin1={entrada}",
                              "Admin2=*Rugura", "[Dioses]", "Dios1=+MERLIN");
        try
        {
            var ini = ServerIni.Cargar(ruta);
            Assert.Equal(16, ini.PrivilegioDe("Merlin"));

            Assert.True(ini.GuardarStaff("merlin", nuevoRango));
            Assert.Equal(nuevoRango, ini.PrivilegioDe("Merlin"));
            var despues = ServerIni.Cargar(ruta);
            Assert.Equal(nuevoRango, despues.PrivilegioDe("Merlin"));
            Assert.Equal(16, despues.PrivilegioDe("Rugura"));
            var guardado = IniFile.Load(ruta);
            Assert.Equal("*Rugura", guardado.GetString("Admines", "Admin1", ""));
            Assert.Equal(0, guardado.GetInt("INIT", "Dioses", -1));
        }
        finally { Limpiar(ruta); }
    }

    [Fact]
    public void La_baja_falla_si_el_entorno_sigue_otorgando_el_rango()
    {
        const string contador = "COVERAO_INIT_ADMINES";
        const string entrada = "COVERAO_ADMINES_ADMIN1";
        string? contadorAnterior = Environment.GetEnvironmentVariable(contador);
        string? entradaAnterior = Environment.GetEnvironmentVariable(entrada);
        string ruta = IniNuevo("[INIT]", "Admines=1", "[Admines]", "Admin1=*Merlin");
        try
        {
            Environment.SetEnvironmentVariable(contador, "1");
            Environment.SetEnvironmentVariable(entrada, "*Merlin");
            var ini = ServerIni.Cargar(ruta);

            Assert.False(ini.GuardarStaff("Merlin", 1));
            Assert.Equal(16, ini.PrivilegioDe("Merlin"));
            Assert.Equal(0, IniFile.Load(ruta).GetInt("INIT", "Admines", -1));
        }
        finally
        {
            Environment.SetEnvironmentVariable(contador, contadorAnterior);
            Environment.SetEnvironmentVariable(entrada, entradaAnterior);
            Limpiar(ruta);
        }
    }

    [Fact]
    public void La_baja_no_cambia_los_rangos_de_otros_si_el_entorno_pisa_una_posicion()
    {
        const string entrada = "COVERAO_ADMINES_ADMIN2";
        string? entradaAnterior = Environment.GetEnvironmentVariable(entrada);
        string ruta = IniNuevo("[INIT]", "Admines=3", "[Admines]", "Admin1=*Merlin", "Admin2=Pedro", "Admin3=Ana");
        try
        {
            Environment.SetEnvironmentVariable(entrada, "Otro");
            string original = File.ReadAllText(ruta);
            var ini = ServerIni.Cargar(ruta);
            Assert.Equal(16, ini.PrivilegioDe("Merlin"));
            Assert.Equal(16, ini.PrivilegioDe("Otro"));
            Assert.Equal(16, ini.PrivilegioDe("Ana"));
            Assert.Equal(1, ini.PrivilegioDe("Pedro"));

            Assert.False(ini.GuardarStaff("Merlin", 1));

            Assert.Equal(original, File.ReadAllText(ruta));
            Assert.Equal(16, ini.PrivilegioDe("Merlin"));
            Assert.Equal(16, ini.PrivilegioDe("Otro"));
            Assert.Equal(16, ini.PrivilegioDe("Ana"));
            Assert.Equal(1, ini.PrivilegioDe("Pedro"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(entrada, entradaAnterior);
            Limpiar(ruta);
        }
    }

    /// <summary>Cambiar de rango no puede dejarlo en las dos listas: el desempate por autoridad taparía el
    /// rastro y el que hostea vería un Consejero que sigue pudiendo lo de antes.</summary>
    [Fact]
    public void Cambiar_de_rango_no_lo_deja_en_dos_listas()
    {
        string ruta = IniNuevo("[INIT]", "MaxUsers=0");
        try
        {
            var ini = ServerIni.Cargar(ruta);
            ini.GuardarStaff("Merlin", 8);    // Dios
            ini.GuardarStaff("Merlin", 2);    // ahora Consejero
            var despues = ServerIni.Cargar(ruta);
            Assert.Equal(2, despues.PrivilegioDe("Merlin"));
            Assert.Single(despues.Staff, s => string.Equals(s.Nombre, "Merlin", StringComparison.OrdinalIgnoreCase));
        }
        finally { Limpiar(ruta); }
    }

    /// <summary>Dos cambios de rango a la vez (el panel atiende cada pedido en su hilo): ninguno pisa al otro.</summary>
    [Fact]
    public void Dos_cambios_de_rango_a_la_vez_no_se_pisan()
    {
        string ruta = IniNuevo("[INIT]", "MaxUsers=0");
        try
        {
            var ini = ServerIni.Cargar(ruta);
            var tandas = Enumerable.Range(0, 2).Select(h => Enumerable.Range(0, 15).Select(i => $"Gm{h}x{i}").ToList()).ToList();
            using var largada = new Barrier(tandas.Count);
            var hilos = tandas.Select(tanda => new Thread(() =>
            {
                largada.SignalAndWait();
                foreach (var nombre in tanda) ini.GuardarStaff(nombre, 8);
            })).ToList();
            hilos.ForEach(h => h.Start());
            hilos.ForEach(h => h.Join());

            var despues = ServerIni.Cargar(ruta);
            foreach (var nombre in tandas.SelectMany(t => t)) Assert.Equal(8, despues.PrivilegioDe(nombre));
        }
        finally { Limpiar(ruta); }
    }

    /// <summary>Un contador disparatado en el archivo no cuelga el cambio de rango: la lista tiene el techo del arranque.</summary>
    [Fact]
    public void Un_contador_disparatado_no_cuelga_el_cambio_de_rango()
    {
        string ruta = IniNuevo("[INIT]", "Dioses=1000000", "[Dioses]", "Dios1=Merlin");
        try
        {
            var ini = ServerIni.Cargar(ruta);
            Assert.True(ini.GuardarStaff("Rugura", 8));

            var despues = ServerIni.Cargar(ruta);
            Assert.Equal(8, despues.PrivilegioDe("Merlin"));
            Assert.Equal(8, despues.PrivilegioDe("Rugura"));
            Assert.DoesNotContain("Dios1001=", File.ReadAllText(ruta));
        }
        finally { Limpiar(ruta); }
    }
}
