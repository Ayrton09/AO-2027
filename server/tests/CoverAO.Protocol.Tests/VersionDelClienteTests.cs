using System;
using System.IO;
using CoverAO.Server.Data;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// LA VERSIÓN QUE EL SERVIDOR EXIGE (<c>[INIT] Version</c>, VB6 <c>ULTIMAVERSION</c> / <c>VersionOK</c>).
///
/// <para>La clave ya venía en el <c>Server.ini</c> del install y no la leía nadie: el host la escribía
/// creyendo que servía. Lo que se fija acá es la lectura y —sobre todo— <b>que ausente signifique apagado</b>,
/// que es la única diferencia con el original y la que evita que encenderlo por accidente deje a todos afuera.
/// La comparación en sí es un <c>==</c> de texto contra <c>"M.m.r"</c>, igual que allá.</para>
/// </summary>
public class VersionDelClienteTests : IDisposable
{
    private readonly string _ini = Path.Combine(Path.GetTempPath(), "coverao_ver_" + Guid.NewGuid().ToString("N") + ".ini");

    public void Dispose() { try { File.Delete(_ini); } catch { } }

    private ServerIni Cargar(string cuerpo)
    {
        File.WriteAllText(_ini, "[INIT]\r\nStartPort=7666\r\n" + cuerpo);
        return ServerIni.Cargar(_ini);
    }

    [Fact]
    public void Sin_la_clave_no_hay_version_exigida()
    {
        // EL CASO QUE IMPORTA. Es el de los 194 flows y el de cualquier host que no sepa que la clave existe:
        // vacía = sin control, o sea exactamente lo que hacía el servidor hasta ahora.
        Assert.Equal("", Cargar("").VersionRequerida);
    }

    [Fact]
    public void Con_la_clave_se_lee_tal_cual()
    {
        Assert.Equal("1.0.0", Cargar("Version=1.0.0\r\n").VersionRequerida);
    }

    [Fact]
    public void Se_le_sacan_los_espacios_de_los_bordes()
    {
        // El ini se edita a mano: un espacio de más no puede ser la diferencia entre entrar y no entrar.
        Assert.Equal("0.13.7", Cargar("Version=  0.13.7  \r\n").VersionRequerida);
    }

    [Fact]
    public void Un_valor_vacio_es_lo_mismo_que_no_estar()
    {
        // `Version=` a secas: el host la dejó en blanco. No puede quedar exigiendo la cadena vacía, que no
        // coincide con ningún cliente.
        Assert.Equal("", Cargar("Version=\r\n").VersionRequerida);
    }

    [Fact]
    public void Es_una_clave_conocida()
    {
        // Si no está en la lista, el arranque la reporta como "el host escribió una opción que no entiendo" —
        // justo lo contrario de lo que pasaba antes, que era entenderla y no usarla.
        Assert.Contains("Version", ServerIni.ClavesConocidas);
    }

    [Fact]
    public void Deja_de_figurar_como_ausente_cuando_el_host_la_escribio()
    {
        // El arranque lista las opciones que corren en su valor por defecto. Sin la clave, `Version` es una
        // de ellas; con la clave escrita, no.
        Assert.Contains("Version", Cargar("").ClavesAusentes);
        Assert.DoesNotContain("Version", Cargar("Version=1.0.0\r\n").ClavesAusentes);
    }

    [Theory]
    // La comparación del original es de TEXTO, no numérica: `VersionOK = (Ver = ULTIMAVERSION)`. Estas filas
    // fijan qué cadena arma el servidor con los tres bytes, que es lo que se compara.
    [InlineData(1, 0, 0, "1.0.0")]
    [InlineData(1, 4, 5, "1.4.5")]
    [InlineData(0, 13, 7, "0.13.7")]
    [InlineData(255, 255, 255, "255.255.255")]
    public void Los_tres_bytes_arman_la_cadena_con_puntos(byte mayor, byte menor, byte rev, string esperado)
    {
        Assert.Equal(esperado, $"{mayor}.{menor}.{rev}");
    }
}
