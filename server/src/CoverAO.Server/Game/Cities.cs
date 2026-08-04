namespace CoverAO.Server.Game;

/// <summary>Ciudades-hogar del VB6 (<c>Dat/Ciudades.dat</c>, enum <c>eCiudad</c> 1..12). Cada ciudad tiene un
/// tile de spawn (donde nace/aparece un personaje vivo de esa tierra natal) y un tile de resucitación
/// (adonde va un muerto que usa <c>RegresarHogar</c>, InvUsuario.bas:1361).</summary>
public static class Cities
{
    public const byte Ullathorpe = 3;   // ciudad por defecto (tierra natal newbie)
    public const byte Rinkel = 5;       // VB6 cRinkel (Declares.bas:148) — hogar del renegado al perder la ciudadanía

    /// <summary>Tile de spawn (vivo) por ciudad — Ciudades.dat X/Y/MAPA.</summary>
    private static readonly IReadOnlyDictionary<byte, WorldPos> Spawn = new Dictionary<byte, WorldPos>
    {
        [1] = new(34, 40, 88),    // Nix
        [2] = new(194, 52, 79),   // Illiandor
        [3] = new(1, 47, 44),     // Ullathorpe
        [4] = new(59, 50, 45),    // Banderbill
        [5] = new(20, 75, 55),    // Rinkel
        [6] = new(37, 57, 74),    // Dungeon Newbie
        [7] = new(62, 72, 40),    // Lindos
        [8] = new(151, 23, 80),   // Arghal
        [9] = new(218, 46, 35),   // Tiama
        [10] = new(180, 33, 74),  // Orac
        [11] = new(185, 50, 78),  // Suramei
        [12] = new(111, 88, 80),  // Nueva
    };

    /// <summary>Tile de resucitación (Dead) por ciudad — Ciudades.dat X_DEAD/Y_DEAD/MAPA_DEAD.</summary>
    private static readonly IReadOnlyDictionary<byte, WorldPos> Dead = new Dictionary<byte, WorldPos>
    {
        [1] = new(34, 22, 60),
        [2] = new(194, 61, 69),
        [3] = new(1, 50, 33),
        [4] = new(59, 15, 15),
        [5] = new(20, 50, 50),
        [6] = new(37, 61, 68),
        [7] = new(64, 46, 47),
        [8] = new(151, 20, 63),
        [9] = new(218, 34, 71),
        [10] = new(180, 24, 68),
        [11] = new(184, 65, 34),
        [12] = new(112, 28, 59),
    };

    /// <summary>Cárcel y punto de liberación (Ciudades.dat, secciones PRISION y LIBERTAD). El VB6 manda acá
    /// al encarcelado (<c>Encarcelar</c>, Admin.bas:174) y lo suelta en Libertad al cumplir (<c>PurgarPenas</c>).</summary>
    public static readonly WorldPos Prision = new(47, 53, 32);
    public static readonly WorldPos Libertad = new(47, 52, 59);

    /// <summary>Tile donde nace un personaje de esa tierra natal (fallback: Ullathorpe).</summary>
    public static WorldPos SpawnTile(byte city) => Spawn.TryGetValue(city, out var p) ? p : Spawn[Ullathorpe];

    /// <summary>Tile de resucitación de la ciudad-hogar (fallback: Ullathorpe).</summary>
    public static WorldPos DeadTile(byte city) => Dead.TryGetValue(city, out var p) ? p : Dead[Ullathorpe];
}
