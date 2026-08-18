using CoverAO.Server.Data;

namespace CoverAO.Protocol.Tests;

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
            Assert.Single(despues.Staff.Where(s => string.Equals(s.Nombre, "Merlin", StringComparison.OrdinalIgnoreCase)));
        }
        finally { Limpiar(ruta); }
    }
}
