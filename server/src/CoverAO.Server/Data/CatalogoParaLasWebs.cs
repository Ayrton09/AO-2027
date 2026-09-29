using CoverAO.Server.Game;

namespace CoverAO.Server.Data;

/// <summary>
/// EL CATÁLOGO DEL JUEGO PARA LAS WEBS (fase5/postgres/DISENO.md, decisión D3): los nombres de los objetos, hechizos, clases,
/// razas y mapas, para que una página muestre "Espada Larga" y no "2". Se copia de <c>gamedata/</c> a la base en cada
/// arranque (<see cref="AlmacenRelacional.PrepararParaLasWebs"/>); la autoridad sigue siendo <c>gamedata/</c>.
/// </summary>
public sealed record CatalogoParaLasWebs(
    IReadOnlyList<(int Id, string Nombre, int Tipo)> Objetos,
    IReadOnlyList<(int Id, string Nombre)> Hechizos,
    IReadOnlyList<(int Id, string Nombre)> Clases,
    IReadOnlyList<(int Id, string Nombre)> Razas,
    IReadOnlyList<(int Id, string Nombre)> Mapas)
{
    public static CatalogoParaLasWebs Desde(GameData datos) => new(
        datos.Objects.Values.OrderBy(o => o.Index).Select(o => ((int)o.Index, o.Name, o.ObjType)).ToList(),
        datos.Spells.Values.OrderBy(h => h.Index).Select(h => ((int)h.Index, h.Name)).ToList(),
        GameLimits.NombresDeClase.Select((nombre, i) => (i + 1, nombre)).ToList(),
        GameData.Razas.ToList(),
        datos.NombresDeMapas.Where(m => m.Value.Length > 0).OrderBy(m => m.Key).Select(m => (m.Key, m.Value)).ToList());
}
