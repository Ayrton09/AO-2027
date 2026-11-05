namespace CoverAO.Server.Game;

/// <summary>Bajas enemigas acumuladas para cada rango, compartidas por los enlistadores y los bots.</summary>
public static class RangosDeFaccion
{
    public static ReadOnlySpan<int> Bajas(byte faccion) => faccion switch
    {
        Player.StatusArmada => [250, 350, 450, 550, 650, 700, 800, 900, 1000, 1100],
        Player.StatusCaos => [400, 500, 600, 700, 800, 900, 1000, 1100, 1200, 1300],
        Player.StatusMilicia => [200, 300, 400, 500, 600, 700, 800],
        _ => [],
    };

    public static int ParaRango(byte faccion, int rango) =>
        rango > 0 && rango <= Bajas(faccion).Length ? Bajas(faccion)[rango - 1] : int.MaxValue;

    public static int SegundaJerarquia(byte faccion) => faccion switch
    {
        Player.StatusArmada => 6,
        Player.StatusCaos => 8,
        Player.StatusMilicia => 4,
        _ => int.MaxValue,
    };

    public static bool CruzoUnAscenso(byte faccion, int bajas, int anteriores)
    {
        // El primer escalón se obtiene al enlistarse. Las visitas siguientes sólo corresponden a ascensos.
        var tabla = Bajas(faccion);
        for (int i = 1; i < tabla.Length; i++)
            if (bajas >= tabla[i] && anteriores < tabla[i]) return true;
        return false;
    }
}
