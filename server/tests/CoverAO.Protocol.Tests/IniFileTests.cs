using CoverAO.Server.Data;
using Xunit;

namespace CoverAO.Protocol.Tests;

public class IniFileTests
{
    [Fact]
    public void ParsesSectionsKeysAndTypes()
    {
        var ini = IniFile.Parse("[INIT]\r\nClase=2\r\nGm=1\r\n[STATS]\r\nELV=13\r\nGLD=999999\r\n");
        Assert.Equal((byte)2, ini.GetByte("INIT", "Clase"));
        Assert.True(ini.GetBool("INIT", "Gm"));
        Assert.Equal((short)13, ini.GetShort("STATS", "ELV"));
        Assert.Equal(999999L, ini.GetLong("STATS", "GLD"));
        Assert.True(ini.HasSection("STATS"));
        Assert.False(ini.HasSection("MISSING"));
    }

    [Fact]
    public void IgnoresCommentsAndBlankLines()
    {
        var ini = IniFile.Parse("; comment\r\n' vb comment\r\n\r\n[A]\r\nx=1\r\n");
        Assert.Equal(1, ini.GetInt("A", "x"));
    }

    [Fact]
    public void RoundTripsStably()
    {
        var ini = new IniFile();
        ini.Set("INIT", "Clase", "2");
        ini.Set("INIT", "Position", "1-50-50");
        ini.Set("STATS", "ELV", 13);
        var text = ini.ToText();
        var reparsed = IniFile.Parse(text);
        Assert.Equal(text, reparsed.ToText()); // idempotent round-trip
        Assert.Equal("1-50-50", reparsed.GetString("INIT", "Position"));
    }

    [Fact]
    public void PreservesInsertionOrder()
    {
        var ini = new IniFile();
        ini.Set("S", "b", "1");
        ini.Set("S", "a", "2");
        ini.Set("S", "c", "3");
        Assert.Equal("[S]\r\nb=1\r\na=2\r\nc=3\r\n\r\n", ini.ToText());
    }

    [Fact]
    public void UnHeaderConComentarioPegadoAbreSuSeccionIgual()
    {
        // Los .dat del VB6 comentan el header: `[NPC2] 'INVOCADO`. clsIniManager.Initialize toma lo que va
        // entre el '[' y el PRIMER ']' e ignora el resto. Exigir que la linea TERMINE en ']' no sólo perdia la
        // seccion: sus claves seguian cargandose en la ANTERIOR, contaminandola en silencio.
        var ini = IniFile.Parse("[UNO]\r\nName=Aldeano\r\n[DOS] 'INVOCADO\r\nName=Lobo\r\nHP=350\r\n");
        Assert.Equal("Aldeano", ini.GetString("UNO", "Name"));
        Assert.Equal("Lobo", ini.GetString("DOS", "Name"));       // la seccion existe
        Assert.Equal(350, ini.GetInt("DOS", "HP"));
        Assert.Equal("", ini.GetString("UNO", "HP"));             // y NO contamino a la anterior
    }

    [Fact]
    public void UnaLineaQueAbreCorcheteSinCerrarNoEsUnValor()
    {
        // VB6: el branch del '[' hace `If Pos Then ...` y no cae nunca al parseo de key=value, aunque la
        // linea traiga un '='. Si cayera, "[roto" se comeria la clave dentro de la seccion previa.
        var ini = IniFile.Parse("[UNO]\r\na=1\r\n[roto sin cierre x=9\r\nb=2\r\n");
        Assert.Equal("1", ini.GetString("UNO", "a"));
        Assert.Equal("2", ini.GetString("UNO", "b"));   // sigue en UNO: la linea rota no abrio nada
        Assert.Equal("", ini.GetString("UNO", "x"));    // y no dejo su clave suelta
    }

    [Fact]
    public void LasCuatroMarcasDeComentarioSeIgnoran()
    {
        // `;` y `'` son las del formato original; `//` y `#` se aceptan porque es lo que la gente escribe.
        // Lo que importa no es que la linea se ignore —una sin '=' ya se ignoraba de casualidad— sino que
        // un comentario CON un '=' adentro no entre como clave: `// StartPort=7666 ...` se cargaba como una
        // clave llamada "// StartPort". No pisaba ningun ajuste real, pero ensuciaba el ini parseado.
        var ini = IniFile.Parse(
            "[INIT]\r\n" +
            "; StartPort=1 comentario clasico\r\n" +
            "' StartPort=2 comentario VB6\r\n" +
            "// StartPort=3 comentario de programador\r\n" +
            "# StartPort=4 comentario de shell\r\n" +
            "StartPort=7666\r\n");

        Assert.Equal(7666, ini.GetInt("INIT", "StartPort"));      // gana la unica linea de verdad
        foreach (var basura in new[] { "; StartPort", "' StartPort", "// StartPort", "# StartPort" })
            Assert.Equal("", ini.GetString("INIT", basura));      // y ninguna marca dejo su clave suelta
    }
}
