using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// VARIOS "ESTO ESTÁ MUERTO" DEL PROYECTO NO SON AFIRMACIONES SOBRE EL CÓDIGO: SON AFIRMACIONES SOBRE LA DATA.
///
/// <para><c>UserIncinera</c> no se portó porque su compuerta pide munición con <c>Snd3 &gt; 0</c> y ninguna de
/// las once flechas lo trae. La rama del orbe de <c>GolpeParaliza*</c> no se portó porque ningún objeto trae
/// <c>Paraliza</c>. El <c>LocaleMsg 260</c> se descartó porque ningún ítem trae las banderas de facción. Los
/// tres veredictos son correctos, y los tres vivían **sólo en prosa**: si la data cambiara, seguirían escritos
/// igual y nadie se enteraría.</para>
///
/// <para>Que no es hipotético: el cuarto de esa misma lista —el <c>LocaleMsg 471</c>, descartado con "cero
/// ítems <c>ObjType=39</c>"— era **falso**. Hay cuatro bolsas de oro, la rama del original está viva y el port
/// no la tenía; se encontró el 2026-09-04 volviendo a medir a mano lo que la nota daba por cerrado. Este
/// archivo existe para que la próxima vez lo diga un test y no una relectura.</para>
///
/// <para>Se mide sobre <c>gamedata/obj.json</c>, que es lo que el server CARGA. La fuente original
/// (<c>CoverAO-2021/…/Servidor/Dat/obj.dat</c>) no está en el repo, así que un test no puede leerla —y ahí
/// aparece el riesgo de este enfoque: si el transcode dejara de copiar una clave, "aparece en 0 objetos"
/// saldría verde sin que la data original haya cambiado. Lo tapa el CANARIO de más abajo.</para>
/// </summary>
public class VeredictosQueDescansanEnLaDataTests
{
    private static JsonElement[] Objetos()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "gamedata", "obj.json")))
            dir = dir.Parent;
        Assert.True(dir is not null, "no encontré gamedata/obj.json subiendo desde " + AppContext.BaseDirectory);
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir!.FullName, "gamedata", "obj.json")));
        return doc.RootElement.GetProperty("OBJ").EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.Object).Select(e => e.Clone()).ToArray();
    }

    /// <summary>El <c>obj.dat</c> escribe la misma clave con varias grafías y el lector del VB6 normaliza con
    /// <c>UCase$</c>, así que acá se busca sin distinguir mayúsculas.</summary>
    private static int Entero(JsonElement o, string clave)
    {
        foreach (var p in o.EnumerateObject())
            if (string.Equals(p.Name, clave, StringComparison.OrdinalIgnoreCase))
            {
                string s = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.ToString();
                var m = System.Text.RegularExpressions.Regex.Match(s, @"^\s*(-?\d+)");
                return m.Success ? int.Parse(m.Groups[1].Value) : 0;
            }
        return 0;
    }

    private static bool Trae(JsonElement o, string clave) =>
        o.EnumerateObject().Any(p => string.Equals(p.Name, clave, StringComparison.OrdinalIgnoreCase));

    private static int Cuantos(JsonElement[] objs, string clave) => objs.Count(o => Trae(o, clave));

    /// <summary>LA PREMISA DE TODO EL ARCHIVO. Sin esto, cambiar el nombre del paquete o romper el transcode
    /// dejaría todos los "no aparece en ningún objeto" de abajo en verde para siempre.</summary>
    [Fact]
    public void El_transcode_sigue_copiando_las_claves_que_el_port_no_modela()
    {
        var objs = Objetos();
        Assert.True(objs.Length == 1520,
            $"el obj.json trae {objs.Length} objetos y el obj.dat del install tiene 1520: el que cambió es el "
            + "transcode o la data, y hasta saber cuál los veredictos de abajo no valen");

        // EL CANARIO. `Snd3` no lo modela `ObjInfo` —el port no lo usa— y aun así el transcode lo copia crudo:
        // 89 objetos lo traen. Mientras esos 89 estén, "la clave X aparece en 0 objetos" es un hecho sobre la
        // DATA. Si el canario cae, lo que se rompió es el transcode y hay que arreglar eso, no el juego.
        Assert.True(Cuantos(objs, "Snd3") == 89,
            $"el canario cayó: Snd3 aparece en {Cuantos(objs, "Snd3")} objetos y eran 89. El transcode dejó de "
            + "copiar las claves que el port no modela, así que los veredictos por ausencia de este archivo "
            + "quedan sin fundamento");
    }

    /// <summary><c>UserIncinera</c> (SistemaCombate.bas:2172) está llamada de verdad, pero su único camino
    /// prende <c>Orbe</c> con munición equipada de <c>Snd3 &gt; 0</c>. Ninguna flecha lo trae, así que sale en
    /// la primera línea útil. Ver <c>fase5/GAPS-CODIGO-MUERTO.md</c> §4q.</summary>
    [Fact]
    public void UserIncinera_sigue_muerta_porque_ninguna_flecha_trae_Snd3()
    {
        var objs = Objetos();
        var flechas = objs.Where(o => Entero(o, "ObjType") == 32).ToArray();
        Assert.True(flechas.Length == 11, $"eran 11 flechas (otFlechas) y hay {flechas.Length}");

        var conSnd3 = flechas.Where(o => Entero(o, "Snd3") > 0).ToArray();
        Assert.True(conSnd3.Length == 0,
            $"{conSnd3.Length} flecha(s) traen ahora Snd3, así que la compuerta de UserIncinera SE PUEDE abrir "
            + "y el veredicto 'muerta' de GAPS-CODIGO-MUERTO.md §4q dejó de valer: hay que portarla");
    }

    /// <summary>La rama del ORBE de <c>GolpeParalizaUsuario</c> pide <c>Paraliza &gt; 0</c>, campo que no trae
    /// un solo objeto. La rama SIN orbe sí está viva y está portada (pelear a mano limpia paraliza).</summary>
    [Fact]
    public void La_rama_del_orbe_sigue_muerta_porque_ningun_objeto_trae_Paraliza()
    {
        var objs = Objetos();
        var conParaliza = objs.Where(o => Entero(o, "Paraliza") > 0).ToArray();
        Assert.True(conParaliza.Length == 0,
            $"{conParaliza.Length} objeto(s) traen ahora Paraliza: la rama del orbe de GolpeParaliza* dejó de "
            + "estar muerta y hay que portarla");
    }

    /// <summary>La otra mitad del mismo par: <c>GolpeParalizaNPC</c> está VIVA porque su rama del orbe pide un
    /// ObjIndex concreto —la Flecha Paralizante 1085— que sí existe y se equipa sin arco. Es el veredicto
    /// inverso, y va acá para que se caiga si algún día el objeto desaparece.</summary>
    [Fact]
    public void GolpeParalizaNpc_sigue_viva_porque_la_Flecha_Paralizante_existe()
    {
        var objs = Objetos();
        var flecha = objs.FirstOrDefault(o => Entero(o, "_index") == 1085);
        Assert.True(flecha.ValueKind == JsonValueKind.Object, "no está el objeto 1085 (Flecha Paralizante)");
        Assert.Equal(32, Entero(flecha, "ObjType"));
    }

    /// <summary>El <c>LocaleMsg 260</c> (tirar un ítem faccionario o newbie) se descartó porque ningún ítem
    /// trae las banderas, y el 30/31 (el ítem de teletransporte) porque <c>DesdeMap</c>/<c>HastaMap</c> no
    /// aparecen ni una vez.</summary>
    [Theory]
    [InlineData("Real")]
    [InlineData("Caos")]
    [InlineData("Milicia")]
    [InlineData("Newbie")]
    [InlineData("DesdeMap")]
    [InlineData("HastaMap")]
    public void Las_banderas_que_matan_al_260_y_al_30_31_siguen_sin_aparecer(string clave)
    {
        var objs = Objetos();
        int cuantos = Cuantos(objs, clave);
        Assert.True(cuantos == 0,
            $"la clave '{clave}' aparece ahora en {cuantos} objeto(s): el LocaleMsg que se descartó por su "
            + "ausencia dejó de estar muerto (GAPS-CODIGO-MUERTO.md, 'Muertos por data en este install')");
    }

    /// <summary>§3: la rama ARROJADIZA (<c>ArmasArrojadizas</c>, <c>SubTipo = 5</c>) no se porta porque no
    /// tiene un solo ítem. Es un veredicto por data, a diferencia del arpón —que no se porta por el
    /// anidamiento del <c>If</c>, y ese sí es estructural—.</summary>
    [Fact]
    public void La_rama_arrojadiza_sigue_sin_items()
    {
        var objs = Objetos();
        var armas = objs.Where(o => Entero(o, "ObjType") == 2).ToArray();
        Assert.True(armas.Length == 85, $"eran 85 armas (ObjType=2) y hay {armas.Length}: el barrido no está leyendo la data");

        var arrojadizas = armas.Where(o => Entero(o, "SubTipo") == 5).ToArray();
        Assert.True(arrojadizas.Length == 0,
            $"aparecieron {arrojadizas.Length} arma(s) arrojadiza(s): la rama que no se portó por falta de "
            + "ítems dejó de estar vacía (GAPS-CODIGO-MUERTO.md §3)");
    }

    /// <summary>§3: el bono de daño del ARCO_DEMONIO (objeto 666) se aplica contra <c>NPC_DEMONIO = 1</c>, y
    /// en esta data el NPC 1 es un <b>aldeano</b>, así que el bono no le toca nunca a un demonio. Si el 1
    /// dejara de ser el aldeano, el bono pasa a importar y hay que portarlo.</summary>
    [Fact]
    public void El_npc_1_sigue_siendo_el_aldeano_y_no_un_demonio()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "gamedata", "NPCs.json")))
            dir = dir.Parent;
        Assert.True(dir is not null, "no encontré gamedata/NPCs.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir!.FullName, "gamedata", "NPCs.json")));
        var npcs = doc.RootElement.GetProperty("NPC").EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.Object).Select(e => e.Clone()).ToArray();
        Assert.True(npcs.Length == 291, $"eran 291 NPCs y hay {npcs.Length}: el barrido no está leyendo la data");

        var uno = npcs.FirstOrDefault(e => Entero(e, "_index") == 1);
        Assert.True(uno.ValueKind == JsonValueKind.Object, "no está el NPC 1");
        string nombre = uno.EnumerateObject()
            .FirstOrDefault(p => string.Equals(p.Name, "Name", StringComparison.OrdinalIgnoreCase))
            .Value.GetString() ?? "";
        Assert.True(nombre.StartsWith("Aldeano", StringComparison.OrdinalIgnoreCase),
            $"el NPC 1 es ahora '{nombre}': el bono del ARCO_DEMONIO (obj 666) apuntaba al 1 dando por hecho "
            + "que era el aldeano, así que ese veredicto de GAPS-CODIGO-MUERTO.md §3 hay que rehacerlo");
    }

    /// <summary>EL QUE ESTABA MAL, y por eso va al revés que los otros. La nota descartaba el
    /// <c>LocaleMsg 471</c> con "cero ítems <c>ObjType=39</c>" y hay CUATRO bolsas de oro. La rama
    /// <c>otBolsas</c> (InvUsuario.bas:1838) se portó el 2026-09-04; este test fija la data que la sostiene,
    /// así que si las bolsas desaparecieran el que se cae es el port y no el jugador.</summary>
    [Fact]
    public void Las_bolsas_de_oro_existen_y_valen_algo()
    {
        var objs = Objetos();
        var bolsas = objs.Where(o => Entero(o, "ObjType") == 39).ToArray();
        Assert.True(bolsas.Length == 4,
            $"eran 4 bolsas de oro (otBolsas) y hay {bolsas.Length}");
        Assert.All(bolsas, b => Assert.True(Entero(b, "Valor") > 0,
            "una bolsa de oro sin Valor daría 0 monedas al abrirla"));
    }
}
