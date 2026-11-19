using CoverAO.Server.Data;

namespace CoverAO.Server.Game;

internal static class MandadoParaVestirse
{
    internal const int Paga = 150;
    internal const int OroParaVestirse = 60;
    internal const int EsperaEntrePagosSeg = 30 * 60;
    internal const string Encargo = "Banquero: Llevá este encargo al sastre de este pueblo. Te va a pagar al entregarlo.";

    internal static bool Necesita(int nivel, int energia, int oro, long banco) =>
        nivel > GameLimits.NewbieMaxLevel && energia < 10 && oro < OroParaVestirse
        && banco < OroParaVestirse - oro;

    internal static bool PuedeVolverACobrar(long ultimo, long ahora) =>
        ultimo == 0 || ahora - ultimo >= EsperaEntrePagosSeg;

    internal static bool EsPueblo(GameData datos, int mapa) =>
        !datos.IsPkMap(mapa) && datos.MapZone(mapa) == "CIUDAD";

    internal static bool EsSastre(NpcInfo? ficha) => ficha is { StockItems.Count: > 0 }
        && ficha.Name.StartsWith("Sastre", StringComparison.OrdinalIgnoreCase);

    internal static bool PuedeVestir(ObjInfo? ropa, int nivel, byte clase, byte raza, byte genero) =>
        ropa is { IsArmor: true, IsNewbie: false } && ropa.MinLevel <= nivel
        && !ropa.ClassForbidden(clase) && !ropa.RaceForbidden(raza)
        && Equipment.PuedeUsarloSuGenero(Equipment.GeneroDeLaPrenda(ropa), genero);

    internal static bool VendeRopaBasica(GameData datos, NpcInfo? ficha, int nivel, byte clase, byte raza, byte genero) =>
        EsSastre(ficha) && ficha!.StockItems!.Any(s => datos.Obj((short)s.ObjIndex) is { Value: > 0 and <= OroParaVestirse } ropa
            && PuedeVestir(ropa, nivel, clase, raza, genero));
}
