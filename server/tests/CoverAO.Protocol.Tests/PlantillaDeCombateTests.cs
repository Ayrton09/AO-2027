using CoverAO.Server.Data;
using CoverAO.Server.Game;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// EL ARNÉS DE PERSONAJES ARMADOS. No es una regla del juego: es la herramienta con la que un flow pide "un
/// Guerrero de nivel 45" en vez de mandarle diez <c>/CI</c> y cruzar los dedos.
///
/// <para>Lo que se cuida acá es lo que haría que un flow mida mal: que el personaje armado tenga los números
/// de verdad de su nivel, que no pueda usar nada que un jugador no pueda, y que dos corridas den lo mismo.</para>
/// </summary>
public class PlantillaDeCombateTests : IDisposable
{
    private const byte Guerrero = 1, Mago = 2, Clerigo = 3;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "coverao_plantilla_" + Guid.NewGuid().ToString("N"));

    private (GameData Datos, Leveling Niveles, Persistence Db)? Mundo()
    {
        var datos = GameData.Load();
        if (datos.ItemCount == 0 || datos.LevelExp.Count == 0) return null;
        Directory.CreateDirectory(_root);
        var niveles = new Leveling(datos.LevelExp, datos.LifeMod);
        return (datos, niveles, new Persistence(AlmacenSqlite.EnMemoria(), niveles));
    }

    /// <summary>Un personaje creado POR EL CAMINO REAL: los atributos salen del alta según raza y clase, y de
    /// la constitución depende cuánta vida da cada nivel. Uno fabricado a mano sube y no engorda.</summary>
    private static Player Nuevo((GameData Datos, Leveling Niveles, Persistence Db) m, byte clase, byte raza = 1)
    {
        var acc = m.Db.CreateAccount("cuenta " + clase + raza, "clave_de_prueba");
        return m.Db.CreateCharacter(acc, "Probador" + clase + raza, 1, raza, 1, clase, 1, 1,
                                    m.Datos.NewbieKit(clase), m.Datos.ModRaza);
    }

    /// <summary>El nivel se sube por el camino real, así que la vida tiene que crecer con él. Si esto fallara,
    /// un flow de combate estaría midiendo contra un muñeco de nivel 45 con la vida de uno de nivel 1.</summary>
    [Fact]
    public void Armar_a_nivel_45_da_los_numeros_de_un_nivel_45()
    {
        if (Mundo() is not { } m) return;

        var novato = Nuevo(m, Guerrero);
        int vidaDeNovato = novato.MaxHp;

        var veterano = Nuevo(m, Guerrero);
        PlantillaDeCombate.Armar(veterano, m.Datos, m.Niveles, Guerrero, 45);

        Assert.Equal(45, veterano.Level);
        Assert.True(veterano.MaxHp > vidaDeNovato,
            $"un nivel 45 tiene que tener más vida que un novato ({veterano.MaxHp} vs {vidaDeNovato})");
    }

    /// <summary>Y lo mismo dos veces tiene que dar lo mismo. Un arnés que varía entre corridas convierte
    /// cualquier flow que lo use en intermitente, y encontrar eso después cuesta un día.</summary>
    [Fact]
    public void Armar_dos_veces_da_el_mismo_personaje()
    {
        if (Mundo() is not { } m) return;

        var a = Nuevo(m, Guerrero);
        var b = Nuevo(m, Guerrero);
        PlantillaDeCombate.Armar(a, m.Datos, m.Niveles, Guerrero, 30);
        PlantillaDeCombate.Armar(b, m.Datos, m.Niveles, Guerrero, 30);

        Assert.Equal(a.Level, b.Level);
        for (int slot = 1; slot <= GameLimits.MaxInventorySlots; slot++)
        {
            Assert.Equal(a.Inventory[slot].ObjIndex, b.Inventory[slot].ObjIndex);
            Assert.Equal(a.Inventory[slot].Amount, b.Inventory[slot].Amount);
            Assert.Equal(a.Inventory[slot].Equipped, b.Inventory[slot].Equipped);
        }
    }

    /// <summary>Sale con el equipo PUESTO y con pociones. Un muñeco con el arma en la mochila pelea a mano
    /// limpia, que es justo lo que nos pasó con los bots.</summary>
    [Fact]
    public void Sale_vestido_y_con_pociones()
    {
        if (Mundo() is not { } m) return;

        var p = Nuevo(m, Guerrero);
        PlantillaDeCombate.Armar(p, m.Datos, m.Niveles, Guerrero, 40);

        var puestos = Enumerable.Range(1, GameLimits.MaxInventorySlots)
            .Select(s => p.Inventory[s]).Where(i => i.Equipped && i.ObjIndex > 0).ToList();
        Assert.True(puestos.Count >= 2, $"tendría que llevar al menos arma y armadura puestas, lleva {puestos.Count}");
        Assert.Contains(puestos, i => m.Datos.Obj(i.ObjIndex)!.IsWeapon);
        Assert.Contains(puestos, i => m.Datos.Obj(i.ObjIndex)!.IsArmor);

        int pociones = Enumerable.Range(1, GameLimits.MaxInventorySlots)
            .Select(s => p.Inventory[s]).Where(i => m.Datos.Obj(i.ObjIndex) is { IsPotion: true }).Sum(i => i.Amount);
        Assert.True(pociones >= PlantillaDeCombate.PocionesPorColor, $"pocas pociones: {pociones}");
    }

    /// <summary>
    /// NADA QUE UN JUGADOR NO PUEDA USAR. Es la mitad que hace al arnés honesto: si el muñeco pudiera llevar
    /// el arma que su clase tiene prohibida, el flow estaría midiendo una pelea que en el juego no puede
    /// ocurrir, y el resultado no diría nada sobre el juego.
    /// </summary>
    [Fact]
    public void No_lleva_nada_prohibido_para_su_clase_su_raza_ni_su_nivel()
    {
        if (Mundo() is not { } m) return;

        foreach (byte clase in new[] { Guerrero, Mago, Clerigo })
        foreach (byte raza in new byte[] { 1, 5 })   // humano y enano: el enano tiene ropa prohibida propia
        {
            var p = Nuevo(m, clase, raza);
            PlantillaDeCombate.Armar(p, m.Datos, m.Niveles, clase, 25);

            for (int slot = 1; slot <= GameLimits.MaxInventorySlots; slot++)
            {
                var inv = p.Inventory[slot];
                if (inv.ObjIndex <= 0 || m.Datos.Obj(inv.ObjIndex) is not { } o) continue;
                Assert.False(o.ClassForbidden(clase), $"{o.Name} está prohibido para la clase {clase}");
                Assert.False(o.RaceForbidden(raza), $"{o.Name} está prohibido para la raza {raza}");
                Assert.True(o.MinLevel <= p.Level, $"{o.Name} pide nivel {o.MinLevel} y el personaje tiene {p.Level}");
            }
        }
    }

    /// <summary>Un mago y un guerrero no salen con la misma arma. Si salieran iguales, el arnés no estaría
    /// mirando las prohibiciones y el test de arriba estaría pasando por casualidad.</summary>
    [Fact]
    public void Dos_clases_distintas_no_salen_con_el_mismo_equipo()
    {
        if (Mundo() is not { } m) return;

        var guerrero = PlantillaDeCombate.EquipoPara(m.Datos, Guerrero, 1, 40).ToList();
        var mago = PlantillaDeCombate.EquipoPara(m.Datos, Mago, 1, 40).ToList();

        Assert.NotEmpty(guerrero);
        Assert.NotEmpty(mago);
        Assert.NotEqual(guerrero, mago);
    }

    /// <summary>Armar no BAJA de nivel: el juego no sabe hacerlo y fingirlo daría un personaje con números que
    /// nadie podría tener.</summary>
    [Fact]
    public void No_baja_de_nivel()
    {
        if (Mundo() is not { } m) return;

        var p = Nuevo(m, Guerrero);
        PlantillaDeCombate.Armar(p, m.Datos, m.Niveles, Guerrero, 40);
        PlantillaDeCombate.Armar(p, m.Datos, m.Niveles, Guerrero, 10);

        Assert.Equal(40, p.Level);
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { /* el temp queda */ } }
}
