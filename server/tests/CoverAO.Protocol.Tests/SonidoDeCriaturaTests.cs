using System.Linq;
using CoverAO.Server.Data;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// LOS TRES SONIDOS PROPIOS DE CADA CRIATURA (NPCs.dat <c>Snd1/Snd2/Snd3</c>, MODULO_NPCs.bas:1095-1097).
/// El port no tenía el campo, así que pelear contra criaturas era MUDO: ni el rugido al atacar, ni el quejido
/// al recibir el golpe, ni el grito al morir.
///
/// <para><b>Por qué no lo vio nadie</b>, que es lo que vale recordar. El barrido de assets derivaba los ids de
/// sonido de los hechizos y de los objetos y <b>nunca miraba NPCs.dat</b>. Y el oráculo no podía verlo porque
/// su acción <c>Attack</c> pega AL AIRE — no hay ninguna criatura enfrente del personaje del barrido, así que
/// estas líneas no se ejecutan ni una vez en las 115 acciones. Los dos controles miran el protocolo, y por el
/// protocolo un <c>PlayWave</c> que no sale no se nota.</para>
///
/// <para>Salió de un jugador comparando carpetas de sonidos, no de un control.</para>
/// </summary>
public class SonidoDeCriaturaTests
{
    private static GameData LoadOrSkip()
    {
        var data = GameData.Load();
        Assert.False(data.SourceDir is null, "el test necesita gamedata en el disco");
        return data;
    }

    /// <summary>LA PREMISA: que la data REAL traiga los sonidos. Sin esto, todos los tests de abajo pasarían
    /// con un cargador que devuelve cero para todo — el verde que sale de no hacer nada.</summary>
    [Fact]
    public void La_data_real_trae_los_sonidos_de_las_criaturas()
    {
        var data = LoadOrSkip();
        var conSonido = data.Npcs.Values.Where(n => n.Snd1 > 0 || n.Snd2 > 0 || n.Snd3 > 0).ToList();
        // 64 de los 291 NPCs del install, y el número exacto se fija a propósito: si un cambio del transcode se
        // come el campo, la cuenta baja y esto lo dice en vez de dejar todo en silencio.
        //
        // OJO LA DIFERENCIA CON 66, que es la primera cifra que escribí y estaba mal: SESENTA Y SEIS traen las
        // claves y sólo SESENTA Y CUATRO traen un sonido. El "Gran Hechicero Garveloth" (560) y el "Profeta
        // Arcano" (617) las traen escritas en CERO, o sea mudos a propósito. Contar las claves no es contar los
        // sonidos, y el cargador tenía razón.
        Assert.Equal(64, conSonido.Count);
        var ids = data.Npcs.Values
            .SelectMany(n => new[] { n.Snd1, n.Snd2, n.Snd3 })
            .Where(s => s > 0).Distinct().ToList();
        Assert.Equal(107, ids.Count);
    }

    /// <summary>LA GRAFÍA RARA IGUAL CARGA. El .dat trae <c>SND1</c> en la mayoría de las fichas y <c>Snd1</c>
    /// en cuatro (los tres Lobos y el Demonio Abisal). Lo que fija este test es el RESULTADO —que esas cuatro
    /// tengan su sonido— y no el mecanismo: quien resuelve las grafías es <c>Candidatos</c>, que compara sin
    /// distinguir mayúsculas, así que quitar el segundo nombre del <c>Int(...)</c> no rompe nada.
    ///
    /// <para>Vale decirlo porque lo comprobé al revés: saboteé el cargador dejándole una sola grafía y los
    /// cuatro tests siguieron en verde. La protección real está en <c>Candidatos</c>, y lo que se cae si
    /// alguien la simplifica es esto.</para></summary>
    [Fact]
    public void Las_criaturas_con_la_grafia_rara_igual_suenan()
    {
        var data = LoadOrSkip();
        var json = System.IO.File.ReadAllText(System.IO.Path.Combine(data.SourceDir!, "npcs.json"));
        var doc = System.Text.Json.JsonDocument.Parse(json);
        int comprobadas = 0;
        foreach (var el in doc.RootElement.GetProperty("NPC").EnumerateArray())
        {
            if (!el.TryGetProperty("Snd1", out var v)) continue;      // la grafía rara, tal cual está en el .dat
            int idx = el.GetProperty("_index").GetInt32();
            if (!int.TryParse(v.GetString(), out int esperado) || esperado <= 0) continue;
            Assert.True(data.Npcs.TryGetValue(idx, out var npc), $"no cargó el NPC {idx}");
            Assert.Equal(esperado, npc!.Snd1);
            comprobadas++;
        }
        // La premisa: que existan. Si el transcode dejara de emitir la grafía rara, el foreach no entraría
        // nunca y el test pasaría sin comparar nada.
        Assert.True(comprobadas >= 3, $"sólo encontré {comprobadas} fichas con la grafía 'Snd1'");
    }

    /// <summary>Todo sonido que una criatura pueda pedir tiene que estar distribuido. Es el mismo control que
    /// hace <c>check-assets.mjs</c> del lado del cliente, pero acá corre con los tests: un archivo que falta
    /// deja a la criatura muda y no hay error de protocolo que lo delate.</summary>
    [Fact]
    public void Los_archivos_de_esos_sonidos_estan_todos()
    {
        var data = LoadOrSkip();
        var dir = BuscarEnElRepo(System.IO.Path.Combine("client-web", "assets", "sounds"));
        var faltan = data.Npcs.Values
            .SelectMany(n => new[] { n.Snd1, n.Snd2, n.Snd3 })
            .Where(s => s > 0).Distinct().OrderBy(s => s)
            .Where(s => !System.IO.File.Exists(System.IO.Path.Combine(dir, $"{s}.wav")))
            .ToList();
        Assert.True(faltan.Count == 0, "sonidos de criatura que no se distribuyen: " + string.Join(", ", faltan));
    }

    /// <summary>Un puñado de criaturas conocidas, con sus tres números leídos del .dat. Fija que el cargador
    /// mapee cada clave a su campo y no, por ejemplo, cruce el 2 con el 3 — un intercambio así haría que la
    /// criatura gritara al recibir el golpe y se quejara al morir, y ningún test de conteo lo vería.</summary>
    [Fact]
    public void Cada_clave_va_a_su_campo()
    {
        var data = LoadOrSkip();
        var json = System.IO.File.ReadAllText(System.IO.Path.Combine(data.SourceDir!, "npcs.json"));
        var doc = System.Text.Json.JsonDocument.Parse(json);
        int comprobados = 0;
        foreach (var el in doc.RootElement.GetProperty("NPC").EnumerateArray())
        {
            int idx = el.GetProperty("_index").GetInt32();
            if (!data.Npcs.TryGetValue(idx, out var npc)) continue;
            foreach (var (clave, leido) in new[] { ("SND1", npc!.Snd1), ("SND2", npc.Snd2), ("SND3", npc.Snd3) })
            {
                if (!el.TryGetProperty(clave, out var v)) continue;
                if (!int.TryParse(v.GetString(), out int esperado)) continue;
                Assert.Equal(esperado, leido);
                comprobados++;
            }
        }
        // La premisa del test: que haya comprobado algo. Con un JSON sin esas claves, el foreach no entra
        // nunca y los asserts de adentro no corren.
        Assert.True(comprobados >= 150, $"sólo se compararon {comprobados} claves; ¿cambió el formato del JSON?");
    }

    private static string BuscarEnElRepo(string relativo)
    {
        for (var dir = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var cand = System.IO.Path.Combine(dir.FullName, relativo);
            if (System.IO.Directory.Exists(cand) || System.IO.File.Exists(cand)) return cand;
        }
        throw new System.IO.DirectoryNotFoundException($"no encontré '{relativo}' desde {System.AppContext.BaseDirectory}");
    }
}
