namespace CoverAO.Server.Game;

/// <summary>
/// Los factores de oficio POR CLASE del original (las funciones <c>Mod*</c> de <c>Trabajo.bas</c>): cuánto
/// más skill le pide una tarea a quien no es del oficio.
///
/// <para>El gate no es "skill ≥ requisito" sino <c>requisito ≤ skill / factor</c>, o sea que el factor
/// MULTIPLICA lo que hace falta: con factor 3, un mineral de MinSkill 30 pide 90 puntos de Minería.</para>
///
/// <para><b>El install nombra la clase equivocada en dos de las cinco.</b> <c>ModCarpinteria</c> premia al
/// Carpintero, <c>Modalquimia</c> al Druida y <c>ModSastreria</c> al Sastre —las tres bien—, pero
/// <c>ModFundicion</c> (Trabajo.bas:944) le da el ÷1 al <b>Guerrero</b>, que no es un oficio, y
/// <c>ModHerreria</c> (:994) se lo da al <b>Minero</b> en vez de al Herrero. Son dos líneas cambiadas de
/// lugar entre cuatro hermanas.</para>
///
/// <para><b>Lo decide la data, no la lectura.</b> Los <c>MinSkill</c> de <c>obj.dat</c> son Hierro 30, Plata
/// 60 y Oro 80, que es EXACTAMENTE la tabla del minero que documenta la wiki del juego; multiplicados por
/// 1,5 y por 3 dan la del herrero y la del resto, y los tres "no puede hacer" de esa tabla caen solos por
/// pasarse de 100 (oro×1,5 = 120, plata×3 = 180, oro×3 = 240):</para>
///
/// <list type="table">
/// <item><term>Minero (×1)</term><description>hierro 30, plata 60, oro 80</description></item>
/// <item><term>Herrero (×1,5)</term><description>hierro 45, plata 90, oro imposible</description></item>
/// <item><term>El resto (×3)</term><description>hierro 90, plata y oro imposibles</description></item>
/// </list>
///
/// <para>Por eso acá los factores cuelgan de la clase que corresponde y no de la que dice el install. Es la
/// única divergencia deliberada de esta tanda, y la decidió el autor: el tester llegó a este archivo
/// reportando que su MINERO necesitaba 90 de minería para fundir hierro.</para>
/// </summary>
public static class Oficios
{
    // eClass del original (Declares.bas:117).
    private const byte Herrero = 12, Minero = 14;

    /// <summary>Factor de FUNDIR MINERAL (VB6 <c>ModFundicion</c>). Ver la nota de la clase.</summary>
    public static double ModFundicion(byte clase) =>
        clase == Minero ? 1.0 : clase == Herrero ? 1.5 : 3.0;

    /// <summary>El gate literal del original: <c>MinSkill &lt;= UserSkills(mineria) / ModFundicion(Clase)</c>
    /// (Trabajo.bas:225). Va en punto flotante como allá —<c>ModFundicion</c> es <c>Single</c> y la barra de
    /// VB6 no trunca—, así que el 1,5 del herrero no se pierde: 30 × 1,5 = 45 clavado.</summary>
    public static bool PuedeFundir(int minSkillDelMineral, int mineria, byte clase) =>
        minSkillDelMineral <= mineria / ModFundicion(clase);

    /// <summary>
    /// UN OFICIO DE FABRICAR, con lo que el servidor mira para aceptar una receta: la skill, el mínimo que pide cada
    /// objeto, lo que gasta UNA unidad de cada material, la herramienta que tiene que estar PUESTA (0 = ninguna) y la
    /// energía que cobra cada paquete. Lo usan el handler (<c>PacketDispatcher.DoCraft</c>) y los bots: dos copias de
    /// la misma regla se desincronizan solas, y el bot terminaría pidiendo lo que el servidor rechaza en silencio.
    /// </summary>
    public sealed record Fabricacion(int Skill, Func<Data.ObjInfo, int> Pide,
                                     Func<Data.ObjInfo, (int Obj, int PorUnidad)[]> Materiales, int Herramienta, int Energia);

    /// <summary>VB6 CarpinteroConstruirItem (Trabajo.bas:747): leña, 20 de energía, sin herramienta.</summary>
    public static readonly Fabricacion Carpinteria = new(GameLimits.SkillCarpinteria, o => o.SkillCarpentry,
        o => new[] { (GameLimits.WoodObj, o.Wood) }, Herramienta: 0, Energia: 20);

    /// <summary>VB6 druidaConstruirItem (Trabajo.bas:2428): raíces, la Olla (887) puesta, 30 de energía.</summary>
    public static readonly Fabricacion Alquimia = new(GameLimits.SkillAlquimia, o => o.SkillAlchemy,
        o => new[] { (GameLimits.RootObj, o.Roots) }, Herramienta: 887, Energia: 30);

    /// <summary>VB6 SastreConstruirItem (Trabajo.bas:390): las tres pieles, el Costurero (886) puesto, 50 de energía.</summary>
    public static readonly Fabricacion Sastreria = new(GameLimits.SkillSastreria, o => o.SkillTailoring,
        o => new[]
        {
            (GameLimits.SkinWolfObj, o.SkinWolf),
            (GameLimits.SkinBrownBearObj, o.SkinBrownBear),
            (GameLimits.SkinPolarBearObj, o.SkinPolarBear),
        }, Herramienta: 886, Energia: 50);
}
