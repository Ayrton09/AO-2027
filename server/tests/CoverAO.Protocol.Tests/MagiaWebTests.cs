using CoverAO.Server.Game;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// EL MODELO DE MAGIA DE AOWEB. Es una decision del autor y NO el VB6: alla la resistencia del equipo se resta
/// como un numero plano y la skill de "Resistencia magica" no se usa para nada. Aca es un porcentaje, entra la
/// diferencia de nivel, cada clase tiene lo suyo y el atacante puede perforarla.
/// </summary>
public class MagiaWebTests
{
    /// <summary>LA HABILIDAD SALE DEL NIVEL, no de una skill guardada, y topea en 100 (aoweb getSimulatedSkill).</summary>
    [Fact]
    public void La_habilidad_magica_sale_del_nivel_y_topea_en_cien()
    {
        Assert.Equal(3, MagiaWeb.HabilidadMagica(1));
        Assert.Equal(30, MagiaWeb.HabilidadMagica(10));
        Assert.Equal(99, MagiaWeb.HabilidadMagica(33));
        Assert.Equal(100, MagiaWeb.HabilidadMagica(34));    // 34*3 = 102, topeado
        Assert.Equal(100, MagiaWeb.HabilidadMagica(50));
        Assert.Equal(0, MagiaWeb.HabilidadMagica(0));
    }

    /// <summary>
    /// EL DANO DEL LANZADOR: la tirada, +3 % por nivel, + el baculo, + el porcentaje de cada pieza, y al final
    /// el modificador de su clase.
    /// </summary>
    [Fact]
    public void El_dano_del_lanzador_acumula_en_orden()
    {
        var sinEquipo = new (int Bono, int Penetracion)[0];

        // Nivel 10: 100 + 30 % = 130. Sin clase en la tabla, el modificador es 1.
        var (dano, pen) = MagiaWeb.DanoDelLanzador(100, 10, 0, sinEquipo, 1.0);
        Assert.Equal(130, dano);
        Assert.Equal(0, pen);

        // El baculo suma PLANO y antes del porcentaje de las piezas (es el bono del VB6, que se conserva).
        Assert.Equal(140, MagiaWeb.DanoDelLanzador(100, 10, 10, sinEquipo, 1.0).Dano);

        // El porcentaje de cada pieza va sobre lo ACUMULADO, no sobre la tirada: 130 + 10 % = 143.
        var conArma = new[] { (Bono: 10, Penetracion: 5) };
        var (conBono, penetra) = MagiaWeb.DanoDelLanzador(100, 10, 0, conArma, 1.0);
        Assert.Equal(143, conBono);
        Assert.Equal(5, penetra);

        // Y la penetracion de las dos piezas se suma.
        Assert.Equal(12, MagiaWeb.DanoDelLanzador(100, 10, 0,
            new[] { (Bono: 0, Penetracion: 5), (Bono: 0, Penetracion: 7) }, 1.0).Penetracion);

        // El modificador de clase es lo ultimo y trunca: 130 * 0.88 = 114,4 -> 114.
        Assert.Equal(114, MagiaWeb.DanoDelLanzador(100, 10, 0, sinEquipo, 0.88).Dano);
    }

    /// <summary>
    /// LA RESISTENCIA ES UN PORCENTAJE y la diferencia de nivel cuenta SOLO a favor del que se defiende: si el
    /// que lanza tiene mas nivel el termino es cero, no negativo.
    /// </summary>
    [Fact]
    public void La_resistencia_suma_equipo_clase_y_diferencia_de_nivel()
    {
        // Mismo nivel: solo equipo y clase.
        Assert.Equal(34, MagiaWeb.ResistenciaEnPorciento(30, 4, nivelDelBlanco: 10, nivelDelLanzador: 10, penetracion: 0));

        // El blanco cinco niveles arriba: (45 - 30) * 2 = 30 mas.
        Assert.Equal(64, MagiaWeb.ResistenciaEnPorciento(30, 4, nivelDelBlanco: 15, nivelDelLanzador: 10, penetracion: 0));

        // El lanzador arriba NO le resta al blanco: el termino de la diferencia es cero.
        Assert.Equal(34, MagiaWeb.ResistenciaEnPorciento(30, 4, nivelDelBlanco: 10, nivelDelLanzador: 20, penetracion: 0));

        // La penetracion del atacante la perfora, y nunca queda negativa.
        Assert.Equal(14, MagiaWeb.ResistenciaEnPorciento(30, 4, 10, 10, penetracion: 20));
        Assert.Equal(0, MagiaWeb.ResistenciaEnPorciento(30, 4, 10, 10, penetracion: 500));
    }

    /// <summary>EL PISO ES UNO, no cero: por mucha resistencia que tenga, un hechizo siempre saca un punto.</summary>
    [Fact]
    public void Con_la_resistencia_el_piso_es_uno()
    {
        Assert.Equal(100, MagiaWeb.ConLaResistencia(100, 0));
        Assert.Equal(66, MagiaWeb.ConLaResistencia(100, 34));
        Assert.Equal(1, MagiaWeb.ConLaResistencia(100, 100));
        Assert.Equal(1, MagiaWeb.ConLaResistencia(100, 250));
    }

    /// <summary>
    /// CONTRA UN NPC: el porcentaje entra SOLO si la criatura declara resistencia, y despues se le resta
    /// ademas su defensa magica plana. Sin data —que es lo que hay hoy— no cambia nada.
    /// </summary>
    [Fact]
    public void El_dano_contra_un_npc_pide_que_declare_resistencia()
    {
        // Sin resistencia declarada: pasa entero (y sin defensa plana, igual).
        Assert.Equal(100, MagiaWeb.DanoContraNpc(100, resistenciaDelNpc: 0, defensaMagicaDelNpc: 0,
                                                 nivelDelLanzador: 10, penetracion: 0));

        // Con resistencia 60 contra un lanzador de nivel 10 (habilidad 30): (60-30)*2 = 60 %, mas la defensa 10
        // -> 70 % de 100 son 70, quedan 30, y despues la defensa plana: 20.
        Assert.Equal(20, MagiaWeb.DanoContraNpc(100, 60, 10, nivelDelLanzador: 10, penetracion: 0));

        // La penetracion tambien perfora la del NPC.
        Assert.Equal(30, MagiaWeb.DanoContraNpc(100, 60, 10, nivelDelLanzador: 10, penetracion: 10));

        // Y el piso es uno.
        Assert.Equal(1, MagiaWeb.DanoContraNpc(10, 100, 90, nivelDelLanzador: 1, penetracion: 0));
    }

    /// <summary>
    /// EL HECHIZO DE UNA CRIATURA es una cuenta mas corta: no entra la diferencia de nivel ni la penetracion.
    /// Es de aoweb, y ademas evita que un personaje de nivel alto quede inmune a la magia de cualquier bicho.
    /// </summary>
    [Fact]
    public void El_hechizo_de_un_npc_no_mira_el_nivel_del_blanco()
    {
        // 30 de equipo + 4 de clase = 34 % de 100 son 34 -> quedan 66.
        Assert.Equal(66, MagiaWeb.DanoDeNpcAJugador(100, resistenciaDelEquipo: 30, resistenciaDeSuClase: 4,
                                                    defensaMagicaDelEquipo: 0));

        // Sin nada puesto, el hechizo pasa entero.
        Assert.Equal(100, MagiaWeb.DanoDeNpcAJugador(100, 0, 0, 0));

        // La defensa magica plana del equipo se resta al final.
        Assert.Equal(56, MagiaWeb.DanoDeNpcAJugador(100, 30, 4, defensaMagicaDelEquipo: 10));

        // No pone piso: lo pone el que llama.
        Assert.Equal(-10, MagiaWeb.DanoDeNpcAJugador(10, 0, 0, 20));
    }

    /// <summary>
    /// LAS TABLAS DE CLASE, REMAPEADAS POR NOMBRE. Es lo que mas facil salia mal: en aoweb el 1 es el Mago y el
    /// 8 el Paladin; aca el 1 es el Clerigo y el 8 el Gladiador. Copiadas por indice, el Gladiador —que no
    /// castea— heredaba los numeros del Paladin.
    /// </summary>
    [Fact]
    public void Las_tablas_de_clase_estan_remapeadas_por_nombre()
    {
        const int clerigo = 1, mago = 2, guerrero = 3, gladiador = 8, paladin = 9, cazador = 10;

        // El Mago es el que mas dano magico hace y el que menos resiste.
        Assert.Equal(1.00, MagiaWeb.ModDanoDeLaClase(mago));
        Assert.Equal(0, MagiaWeb.ModResistenciaDeLaClase(mago));

        // El Clerigo, un escalon abajo en dano y con algo de resistencia.
        Assert.Equal(0.88, MagiaWeb.ModDanoDeLaClase(clerigo));
        Assert.Equal(4, MagiaWeb.ModResistenciaDeLaClase(clerigo));

        // El Guerrero no hace dano magico y es el que mas resiste.
        Assert.Equal(0.00, MagiaWeb.ModDanoDeLaClase(guerrero));
        Assert.Equal(12, MagiaWeb.ModResistenciaDeLaClase(guerrero));

        Assert.Equal(0.78, MagiaWeb.ModDanoDeLaClase(paladin));
        Assert.Equal(6, MagiaWeb.ModResistenciaDeLaClase(cazador));

        // LAS TRES QUE NO ESTAN EN LA TABLA DE AOWEB (autor, 10/10): el Nigromante dana como el Clerigo y resiste como el Druida;
        // Mercenario y Gladiador resisten como el Cazador. El dano del Gladiador sigue en el por defecto: no tiene mana.
        const int mercenario = 17, nigromante = 18;
        Assert.Equal(0.88, MagiaWeb.ModDanoDeLaClase(nigromante));
        Assert.Equal(3, MagiaWeb.ModResistenciaDeLaClase(nigromante));
        Assert.Equal(6, MagiaWeb.ModResistenciaDeLaClase(mercenario));
        Assert.Equal(1.0, MagiaWeb.ModDanoDeLaClase(gladiador));
        Assert.Equal(6, MagiaWeb.ModResistenciaDeLaClase(gladiador));
    }
}
