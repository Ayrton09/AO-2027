using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// LAS CIUDADES ESTÁN CABLEADAS EN <see cref="Cities"/> Y TAMBIÉN VIAJAN EN <c>Ciudades.json</c>, y hasta
/// ahora nada cuidaba que dijeran lo mismo.
///
/// <para>Son 27 coordenadas: doce ciudades con su tile de spawn y su tile de resucitación, más la Prisión, la
/// Libertad e Intermundia. De ellas dependen dónde nace un personaje, adónde lo manda <c>/HOGAR</c>, adónde va
/// preso y de dónde sale. Están duplicadas a propósito —el código no lee el .json—, y una duplicación sin
/// guardia se separa sola: el día que alguien corrija un tile en el archivo, el servidor va a seguir mandando
/// gente al viejo y no se va a poner rojo nada.</para>
///
/// <para>Este test no opina sobre cuál de las dos fuentes debería mandar. Sólo fija que coincidan, que es lo
/// que hoy es cierto —verificado las 27 el 2026-09-01— y lo que hay que enterarse si deja de serlo.</para>
///
/// <para>El orden importa y no es arbitrario: las secciones del <c>.dat</c> vienen en el orden del enum
/// <c>eCiudad</c> del VB6 (Declares.bas), así que la primera es la ciudad 1. Por eso se recorren por posición
/// y no por nombre — los nombres del archivo están en mayúsculas y sin acentos, y aparearlos por texto sería
/// inventar una correspondencia que el original resuelve por índice.</para>
/// </summary>
public class CiudadesCableadasTests
{
    /// <summary>Las doce ciudades-hogar, en el orden del enum. Las tres últimas secciones del archivo
    /// (PRISION, LIBERTAD, INTERMUNDIA) son destinos de warp y se verifican aparte.</summary>
    private const int CiudadesHogar = 12;

    private static JsonElement Meta()
    {
        var d = GameData.Load();
        Assert.False(d.SourceDir is null, "este test necesita la gamedata en disco");
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(d.SourceDir!, "Ciudades.json")));
        return doc.RootElement.GetProperty("meta").Clone();
    }

    /// <summary>Un entero del .dat, cortando el comentario VB6 si lo hubiera.</summary>
    private static int Num(JsonElement sec, string clave)
    {
        Assert.True(sec.TryGetProperty(clave, out var v), $"falta {clave}");
        string s = v.GetString() ?? "";
        int i = 0;
        while (i < s.Length && char.IsDigit(s[i])) i++;
        Assert.True(i > 0, $"{clave} no empieza con un número: '{s}'");
        return int.Parse(s[..i]);
    }

    private static List<(string Nombre, JsonElement Sec)> Secciones()
    {
        var meta = Meta();
        return meta.EnumerateObject().Where(p => p.Name != "INIT")
                   .Select(p => (p.Name, p.Value)).ToList();
    }

    /// <summary>
    /// Las 24 coordenadas de las doce ciudades-hogar: spawn (X/Y/MAPA) y resucitación (X_DEAD/Y_DEAD/MAPA_DEAD).
    /// Se comparan TODAS de una y el mensaje nombra la ciudad, porque un rojo acá tiene que decir cuál se movió.
    /// </summary>
    [Fact]
    public void Las_doce_ciudades_hogar_coinciden_con_el_dat()
    {
        var secs = Secciones();
        Assert.True(secs.Count >= CiudadesHogar, $"el .dat trae {secs.Count} secciones y se esperaban al menos {CiudadesHogar}");

        var difieren = new List<string>();
        for (int i = 0; i < CiudadesHogar; i++)
        {
            var (nombre, sec) = secs[i];
            byte ciudad = (byte)(i + 1);
            var vivo = new WorldPos((short)Num(sec, "MAPA"), (byte)Num(sec, "X"), (byte)Num(sec, "Y"));
            var muerto = new WorldPos((short)Num(sec, "MAPA_DEAD"), (byte)Num(sec, "X_DEAD"), (byte)Num(sec, "Y_DEAD"));
            if (Cities.SpawnTile(ciudad) != vivo)
                difieren.Add($"{nombre} spawn: .dat {vivo} vs código {Cities.SpawnTile(ciudad)}");
            if (Cities.DeadTile(ciudad) != muerto)
                difieren.Add($"{nombre} resucitación: .dat {muerto} vs código {Cities.DeadTile(ciudad)}");
        }
        Assert.True(difieren.Count == 0,
            "Cities.cs y Ciudades.json dejaron de coincidir:\n  " + string.Join("\n  ", difieren));
    }

    /// <summary>
    /// Los tres destinos de warp, que en <see cref="Cities"/> no viven en las tablas sino en constantes
    /// sueltas. Se verifican aparte porque el .dat no les da X_DEAD: nadie resucita en la cárcel.
    /// </summary>
    [Theory]
    [InlineData("PRISION")]
    [InlineData("LIBERTAD")]
    [InlineData("INTERMUNDIA")]
    public void Los_destinos_de_warp_coinciden_con_el_dat(string seccion)
    {
        var sec = Secciones().Single(s => s.Nombre == seccion).Sec;
        var esperado = new WorldPos((short)Num(sec, "MAPA"), (byte)Num(sec, "X"), (byte)Num(sec, "Y"));
        var cableado = seccion switch
        {
            "PRISION" => Cities.Prision,
            "LIBERTAD" => Cities.Libertad,
            _ => Cities.Intermundia,
        };
        Assert.Equal(esperado, cableado);
    }

    /// <summary>
    /// EL CONTROL DEL CONTROL. Los dos tests de arriba comparan por POSICIÓN, así que si un día el .dat
    /// cambiara de orden compararían la ciudad equivocada y podrían pasar de casualidad. Acá se fija que la
    /// primera sección siga siendo Nix y la tercera Ullathorpe, que son las dos que el código nombra por
    /// constante (<c>Cities.Nix = 1</c>, <c>Cities.Ullathorpe = 3</c>).
    /// </summary>
    [Fact]
    public void El_orden_del_dat_es_el_del_enum()
    {
        var secs = Secciones();
        Assert.Equal("NIX", secs[Cities.Nix - 1].Nombre);
        Assert.Equal("ILLIANDOR", secs[Cities.Illiandor - 1].Nombre);
        Assert.Equal("ULLATHORPE", secs[Cities.Ullathorpe - 1].Nombre);
        Assert.Equal("RINKEL", secs[Cities.Rinkel - 1].Nombre);
        Assert.Equal("DUNGEONNEWBIE", secs[Cities.DungeonNewbie - 1].Nombre);
    }
}
