using CoverAO.Server.Data;
using CoverAO.Server.Net;

namespace CoverAO.Server.Game;

/// <summary>
/// Puebla el mundo con lo que cada mapa trae en su propio <c>.csm</c>: la fauna y los servicios de
/// <c>MapSpawns.json</c>, y los objetos de <c>MapObjects.json</c> (VB6 <c>MapData(m,x,y).ObjInfo</c>,
/// FileIO.bas:1326).
///
/// Vive acá y no en Program.cs por una razón concreta: mientras fue código suelto adentro del arranque no
/// se podía testear, y se comió un bug grave sin que nadie lo viera. El número de mapa es <b>short</b> —AO
/// tiene 863 mapas— pero el poblado hacía <c>new WorldPos((byte)mapNum, …)</c>. El cast truncaba: el mapa
/// 271 caía en el <b>15</b> (271 &amp; 255), o sea que la fauna de los mapas 271, 527 y 783 aparecía encima
/// del mapa 15 —el del fixture de los flows— y mataba a los personajes de test antes de que pudieran actuar.
/// El bug estuvo latente mientras sólo había 7 mapas transcodificados (ninguno &gt; 255) y se destapó solo al
/// poblar el mundo entero. <see cref="MapSpawnsTests"/> lo fija con los mapas de número alto de la data real.
/// </summary>
public static class WorldPopulator
{
    /// <summary>La fauna y los servicios que cada mapa trae en su .csm. Una criatura cazable (Hostile con
    /// GiveExp&gt;0) persigue al jugador; los guardias y los NPCs de servicio (GiveExp=0) quedan pasivos, así
    /// los pueblos no son una trampa mortal.
    /// <para><paramref name="saltearMapa"/> deja un mapa SIN su fauna. Lo usa el modo <c>--fixture</c> para el
    /// mapa del laboratorio: ahí el fixture arma su propio mundo controlado, pero encima le caían los 28 spawns
    /// que el mapa 15 trae en su .csm —veinte de ellos Escorpiones hostiles— y uno se paraba en el punto de
    /// aparición a pegarle al personaje de test mientras el flow medía otra cosa. Ver
    /// <c>laboratorio_quieto_flow</c>, que fija que el laboratorio esté quieto.</para></summary>
    public static (int Placed, int Maps) SpawnNpcs(NpcSystem npcSystem, GameData data, short? saltearMapa = null)
    {
        int placed = 0, maps = 0;
        foreach (var (mapNum, spawns) in data.MapSpawns)
        {
            if (saltearMapa is not null && ToMap(mapNum) == saltearMapa.Value) continue;
            maps++;
            foreach (var sp in spawns)
            {
                var t = data.Npc(sp.NpcIndex);
                if (t is null) continue;                   // índice sin ficha en NPCs.dat → se ignora
                bool aggro = t.Hostile && t.GiveExp > 0;
                npcSystem.AddSpawn(t, new WorldPos(ToMap(mapNum), (byte)sp.X, (byte)sp.Y), hostile: aggro);
                placed++;
            }
        }
        return (placed, maps);
    }

    /// <summary>Los objetos colocados del .csm: árboles, carteles, muebles y PUERTAS. Van al mismo
    /// <c>_ground</c> que los drops porque el VB6 también tiene UN objeto por tile.</summary>
    public static (int Placed, int Doors) PlaceObjects(World world, GameData data)
    {
        int placed = 0, doors = 0;
        foreach (var (mapNum, objs) in data.MapObjects)
            foreach (var o in objs)
            {
                var info = data.Obj(o.ObjIndex);
                if (info is null) continue;                // índice sin ficha en obj.dat → se ignora
                // VB6 FileIO.bas:1329: al cargar el mapa marca `ObjEsFijo = 1` para TODO menos las puertas. Un
                // objeto fijo no se le manda nunca al cliente (lo dibuja de su propia copia del mapa); la puerta
                // sí, porque su estado cambia en runtime y el que llega tiene que saber si está abierta.
                world.SetGround(new WorldPos(ToMap(mapNum), (byte)o.X, (byte)o.Y), (short)o.ObjIndex,
                                (short)Math.Max(1, o.Amount), esFijo: !info.IsDoor);
                placed++;
                if (info.IsDoor) doors++;
            }
        return (placed, doors);
    }

    /// <summary>Vuelve a poner lo que los jugadores habían dejado en el suelo (regla de la casa
    /// <c>PersistirSuelo</c>). Corre DESPUÉS de <see cref="PlaceObjects"/> y devuelve cuántos entraron y
    /// cuántos se descartaron.
    ///
    /// <para><b>El decorado del .csm manda.</b> Si el mapa cambió entre dos arranques y ahora hay un árbol
    /// donde alguien había dejado una espada, gana el árbol: el mundo tiene UN objeto por tile, y restaurar
    /// encima dejaría un agujero en el decorado del propio mapa que ya no se arregla solo. Perder la espada es
    /// molesto y acotado; que a un pueblo le falte un pedazo es permanente.</para>
    ///
    /// <para>Vive acá y no en <c>Program.cs</c> a propósito: lo de allá arriba no se puede testear, y este
    /// archivo existe justamente porque el truncamiento del número de mapa se coló por ese camino.</para></summary>
    public static (int Puestos, int Descartados) RestoreSuelo(
        World world, IEnumerable<(WorldPos Pos, short ObjIndex, short Amount)> suelo)
    {
        int puestos = 0, descartados = 0;
        foreach (var (pos, obj, amount) in suelo)
        {
            if (world.HayAlgoEnElSuelo(pos)) { descartados++; continue; }
            world.SetGround(pos, obj, amount);
            puestos++;
        }
        return (puestos, descartados);
    }

    /// <summary>El número de mapa como lo guarda <see cref="WorldPos"/>: short, nunca byte. Un mapa fuera de
    /// rango es data corrupta y se prefiere el fallo ruidoso al truncamiento silencioso.</summary>
    private static short ToMap(int mapNum) =>
        mapNum is > 0 and <= short.MaxValue
            ? (short)mapNum
            : throw new ArgumentOutOfRangeException(nameof(mapNum), mapNum, "número de mapa fuera de rango");
}
