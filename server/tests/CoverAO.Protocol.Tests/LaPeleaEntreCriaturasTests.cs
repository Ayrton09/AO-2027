using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// LA PELEA ENTRE CRIATURAS, como el original (AiNpcAtacaNpc, AI_NPC.bas:538-631). El blanco se busca en lo que la
/// criatura ve —una caja de 6 por 6— y si no está ahí la da por perdida. El elemental de fuego no pega: le tira su
/// hechizo desde donde lo vea, y el dragón le contesta con uno suyo (NpcLanzaSpellSobreNpc, modHechizos.bas:134). Acá
/// la mascota perseguía al blanco por todo el mapa y el elemental de fuego peleaba cuerpo a cuerpo.
/// </summary>
public class LaPeleaEntreCriaturasTests
{
    private const short Cuna = 37;
    private const short ElementalDeFuego = 93, HechizoDeNpc = 36, Paralizar = 9, Inmovilizar = 24;
    private static readonly GameData Datos = GameData.Load();

    private static readonly NpcInfo Lobo = new(Index: 1, Name: "Lobo", Body: 1, Head: 1, Heading: 3, MaxHp: 1000,
        MinHit: 1, MaxHit: 1, Def: 0, GiveExp: 0, GiveGld: 0, PoderEvasion: 0, PoderAtaque: 10_000);

    private static (World Mundo, Player Amo) Escena()
    {
        var world = new World();
        var amo = new Player { CharIndex = world.AllocateCharIndex(), Name = "Rugura", Account = "cuenta_test" };
        amo.Pos = new WorldPos(Cuna, 50, 60);
        amo.MaxHp = amo.Hp = 1_000_000;
        world.Add(amo);
        return (world, amo);
    }

    private static Npc Criatura(World world, NpcInfo ficha, byte x, byte y, short dueno = 0, byte heading = 3)
    {
        var npc = new Npc
        {
            CharIndex = world.AllocateCharIndex(), Template = ficha, Hp = 1000, Pos = new WorldPos(Cuna, x, y),
            Movement = 1, OriginalMovement = 1, OwnerCharIndex = dueno, Heading = heading,
        };
        world.AddNpc(npc);
        return npc;
    }

    private static void ContraElBlanco(Npc mascota, Npc blanco)
    {
        mascota.TargetNpcCharIndex = blanco.CharIndex;
        mascota.Movement = GameLimits.TipoAiNpcAtacaNpc;
    }

    /// <summary>La distancia a la que el blanco recibió su primer daño, o -1 si no lo recibió.</summary>
    private static int PrimerDano(World world, Npc atacante, Npc blanco)
    {
        var sys = new NpcSystem(world, Datos);
        for (int i = 1; i <= 750; i++)
        {
            sys.Update(i);
            if (blanco.Hp < 1000)
                return Math.Abs(atacante.Pos.X - blanco.Pos.X) + Math.Abs(atacante.Pos.Y - blanco.Pos.Y);
        }
        return -1;
    }

    private static NpcInfo ElementalFuego()
    {
        var ficha = Datos.Npc(ElementalDeFuego);
        Assert.NotNull(ficha);                                                     // premisa
        Assert.Contains((int)HechizoDeNpc, ficha!.Spells!);
        return ficha with { Movement = 1 };
    }

    [Fact]
    public void El_elemental_de_fuego_castea_desde_lejos()
    {
        var (world, amo) = Escena();
        var elemental = Criatura(world, ElementalFuego(), 50, 50, amo.CharIndex);
        var blanco = Criatura(world, Lobo, 54, 50);
        ContraElBlanco(elemental, blanco);
        int distancia = PrimerDano(world, elemental, blanco);
        Assert.True(distancia > 1, distancia < 0 ? "no le tiró nada" : $"le pegó recién pegado, a {distancia}");
    }

    /// <summary>El control: una mascota que no es el elemental de fuego pega sólo cuando lo tiene pegado.</summary>
    [Fact]
    public void Una_mascota_comun_pega_recien_pegada()
    {
        var (world, amo) = Escena();
        var lobo = Criatura(world, Lobo, 50, 50, amo.CharIndex);
        var blanco = Criatura(world, Lobo, 54, 50);
        ContraElBlanco(lobo, blanco);
        Assert.Equal(1, PrimerDano(world, lobo, blanco));
    }

    /// <summary>A 7 en X el blanco queda fuera de la caja de 6 por 6: la mascota lo da por perdido y vuelve con su amo.</summary>
    [Fact]
    public void La_mascota_pierde_al_blanco_que_no_ve()
    {
        var (world, amo) = Escena();
        var lobo = Criatura(world, Lobo, 50, 50, amo.CharIndex);
        var blanco = Criatura(world, Lobo, 57, 50);
        ContraElBlanco(lobo, blanco);
        var sys = new NpcSystem(world, Datos);
        for (int i = 1; i <= 10; i++) sys.Update(i);
        Assert.Equal(0, lobo.TargetNpcCharIndex);
        Assert.Equal(GameLimits.TipoAiSigueAmo, lobo.Movement);
        Assert.Equal(1000, blanco.Hp);
    }

    /// <summary>La regla de la casa FamiliarBusca: la mascota ociosa elige presa sólo si la ve para pelear (la caja de
    /// 6 por 6). A 7 la elegía y la soltaba en la vuelta siguiente, sin acercarse nunca.</summary>
    [Fact]
    public void La_mascota_ociosa_no_elige_una_presa_que_no_ve_para_pelear()
    {
        foreach (var (x, laElige) in new[] { ((byte)57, false), ((byte)55, true) })
        {
            var (world, amo) = Escena();
            amo.Pos = new WorldPos(Cuna, 52, 51);                                  // cerca: la presa le queda a la vista
            var mascota = Criatura(world, Lobo, 50, 50, amo.CharIndex);
            mascota.Movement = GameLimits.TipoAiSigueAmo;
            var presa = Criatura(world, Lobo, x, 50);
            presa.Hostile = presa.OriginallyHostile = true; presa.Attackable = true;   // hostil de nacimiento
            var sys = new NpcSystem(world, Datos) { FamiliarBuscaEnemigo = true };
            for (int i = 1; i <= 10; i++) sys.Update(i);
            Assert.Equal(laElige ? presa.CharIndex : (short)0, mascota.TargetNpcCharIndex);
        }
    }

    // ---- el dragón le contesta --------------------------------------------------------------------------------

    private static NpcInfo Dragon(short hechizo) => Lobo with { Name = "Dragón", NpcType = 20, Spells = new[] { (int)hechizo } };

    /// <summary>El elemental, mirando al blanco a tres baldosas, le castea; si el blanco es un dragón, le contesta. Quieto
    /// (inmovilizado) no se acerca, así que nadie llega a pegar cuerpo a cuerpo.</summary>
    private static Npc DuelaContra(NpcInfo fichaDelBlanco, out Npc blanco, bool quieto = true)
    {
        var (world, amo) = Escena();
        var elemental = Criatura(world, ElementalFuego(), 50, 50, amo.CharIndex, heading: 2);   // mira al este
        if (quieto) elemental.InmovilizadaHastaTick = long.MaxValue;
        blanco = Criatura(world, fichaDelBlanco, 53, 50);
        ContraElBlanco(elemental, blanco);
        var sys = new NpcSystem(world, Datos);
        for (int i = 1; i <= 750; i++) sys.Update(i);
        return elemental;
    }

    [Fact]
    public void El_dragon_le_contesta_al_elemental_de_fuego()
    {
        var elemental = DuelaContra(Dragon(HechizoDeNpc), out var dragon);
        Assert.True(dragon.Hp < 1000, "premisa: el elemental le tiró");
        Assert.True(elemental.Hp < 1000, "el dragón le tenía que contestar");
    }

    [Fact]
    public void Lo_que_no_es_dragon_no_le_contesta()
    {
        var elemental = DuelaContra(Lobo with { Spells = new[] { (int)HechizoDeNpc } }, out var blanco);
        Assert.True(blanco.Hp < 1000, "premisa: el elemental le tiró");
        Assert.Equal(1000, elemental.Hp);
    }

    /// <summary>Inmovilizar a otra criatura le saca la parálisis, y paralizarla le saca la inmovilización, con el
    /// contador largo (modHechizos.bas:172-192).</summary>
    [Fact]
    public void Paralizar_o_inmovilizar_a_otra_criatura_usa_el_contador_largo()
    {
        var elemental = DuelaContra(Dragon(Paralizar), out _);
        Assert.True(elemental.ParalyzedUntilTick > 750 + 1000, "el dragón lo tenía que paralizar con el contador largo");
        Assert.Equal(0, elemental.InmovilizadaHastaTick);                       // la parálisis le saca la inmovilización

        // Libre: a una criatura que YA está inmovilizada el original no la vuelve a inmovilizar (`If Inmovilizado = 0`).
        var inmovilizado = DuelaContra(Dragon(Inmovilizar), out _, quieto: false);
        Assert.True(inmovilizado.InmovilizadaHastaTick > 750 + 1000, "el dragón lo tenía que inmovilizar");
        Assert.Equal(0, inmovilizado.ParalyzedUntilTick);
    }
}
