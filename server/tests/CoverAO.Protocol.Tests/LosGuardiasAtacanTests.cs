using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// LOS GUARDIAS QUE ATACAN, regla de la casa <c>GuardiasAtacan</c> (decidida por el autor el 2026-09-23 a pedido del
/// tester). En el original ningún guardia pelea ni se mueve: <c>NPCAI</c> sale en su primera línea para los
/// GuardiasCity (AI_NPC.bas:700). Acá atacan a las facciones enemigas de su bando —el <c>Status</c> de su ficha—,
/// persiguen al que ven hasta que se va de su ciudad y después vuelven a su puesto, y a los newbies no los tocan.
/// Todo sale del <c>Server.ini</c>.
/// </summary>
public class LosGuardiasAtacanTests
{
    private const short Ciudad = 1, Afuera = 2;

    private static ServerIni Ini(string cuerpo = "") => ServerIni.Desde(IniFile.Parse("[INIT]\r\n" + cuerpo));

    /// <summary>Un guardia con los números del Guerrero del install (golpe 250-280, nivel 50) y un poder de ataque que
    /// casi no erra: lo que se mide es a quién le pega, no la tirada.</summary>
    private static NpcInfo Guardia(int bando = ServerIni.GuardiaImperial, int movement = 0, int maxHit = 280,
                                   IReadOnlyList<int>? hechizos = null) =>
        new NpcInfo(Index: 6, Name: "Guerrero", Body: 1, Head: 1, Heading: 3, MaxHp: 40_000, MinHit: Math.Min(250, maxHit),
                    MaxHit: maxHit, Def: 0, GiveExp: 0, GiveGld: 0, PoderEvasion: 0, PoderAtaque: 10_000)
            with { NpcType = 2, Status = bando, Movement = movement, Nivel = 50, Spells = hechizos };

    private static Player Jugador(World world, string nombre, byte faccion, byte x, byte y, byte nivel = 30, short mapa = Ciudad)
    {
        var p = new Player { CharIndex = world.AllocateCharIndex(), Name = nombre, Account = "cuenta_test", Level = nivel };
        p.FactionStatus = faccion;
        p.Pos = new WorldPos(mapa, x, y);
        p.MaxHp = p.Hp = 100_000;   // que aguante: lo que se mide es que le peguen, no que lo maten
        world.Add(p);
        return p;
    }

    /// <summary>Un guardia en (10,10) de la ciudad, con su sistema y el ini que se pida.</summary>
    private static (World world, NpcSystem sys, Npc guardia) Escena(NpcInfo ficha, string ini = "", GameData? data = null)
    {
        var world = new World();
        var sys = new NpcSystem(world, data ?? new GameData(), ini: Ini(ini));
        sys.AddSpawn(ficha, new WorldPos(Ciudad, 10, 10), posOrig: true);
        return (world, sys, world.Npcs.Single());
    }

    private long _tick;
    private void Correr(NpcSystem sys, int vueltas)
    {
        for (int i = 0; i < vueltas; i++) sys.Update(_tick += NpcSystem.AttackEveryTicks);
    }

    private static int Distancia(Npc n, Player p) => Math.Abs(n.Pos.X - p.Pos.X) + Math.Abs(n.Pos.Y - p.Pos.Y);

    // ── A QUIÉN ATACA ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void El_imperial_le_pega_al_republicano_que_tiene_al_lado()
    {
        var (world, sys, _) = Escena(Guardia(movement: 1));
        var enemigo = Jugador(world, "Enemigo", Player.StatusRepublicano, 10, 11);
        Correr(sys, 100);
        Assert.True(enemigo.Hp < enemigo.MaxHp, "el guardia imperial no le pegó al republicano que tenía al lado");
    }

    [Theory]
    [InlineData(Player.StatusCiudadano)]
    [InlineData(Player.StatusArmada)]
    public void A_los_de_su_bando_no_les_hace_nada(byte faccion)
    {
        var (world, sys, guardia) = Escena(Guardia());
        var propio = Jugador(world, "Propio", faccion, 10, 11);
        Correr(sys, 100);
        Assert.Equal(propio.MaxHp, propio.Hp);
        Assert.Equal(new WorldPos(Ciudad, 10, 10), guardia.Pos);
    }

    /// <summary>El control negativo de todo lo demás: con la regla apagada son los del original, que ni se mueven.</summary>
    [Fact]
    public void Con_la_regla_apagada_son_los_del_original()
    {
        var (world, sys, guardia) = Escena(Guardia(), "GuardiasAtacan=0\r\n");
        var pegado = Jugador(world, "Pegado", Player.StatusRepublicano, 10, 11);
        Jugador(world, "ALaVista", Player.StatusRepublicano, 10, 15);
        Correr(sys, 100);
        Assert.Equal(pegado.MaxHp, pegado.Hp);
        Assert.Equal(new WorldPos(Ciudad, 10, 10), guardia.Pos);
    }

    [Fact]
    public void Al_newbie_no_lo_toca_salvo_que_el_ini_lo_pida()
    {
        var (world, sys, _) = Escena(Guardia(movement: 1));
        var novato = Jugador(world, "Novato", Player.StatusRepublicano, 10, 11, nivel: (byte)GameLimits.NewbieMaxLevel);
        Correr(sys, 100);
        Assert.Equal(novato.MaxHp, novato.Hp);

        var (world2, sys2, _) = Escena(Guardia(movement: 1), "GuardiasAtacanNewbies=1\r\n");
        var otro = Jugador(world2, "Novato", Player.StatusRepublicano, 10, 11, nivel: (byte)GameLimits.NewbieMaxLevel);
        Correr(sys2, 100);
        Assert.True(otro.Hp < otro.MaxHp, "con GuardiasAtacanNewbies=1 el guardia no le pegó al newbie");
    }

    /// <summary>Las guardas de cualquier criatura valen también para el guardia: al GM, al muerto, al invisible y al
    /// oculto no los ataca.</summary>
    [Theory]
    [InlineData("gm")]
    [InlineData("muerto")]
    [InlineData("invisible")]
    [InlineData("oculto")]
    public void Al_que_ninguna_criatura_ataca_el_guardia_tampoco(string quien)
    {
        var (world, sys, _) = Escena(Guardia(movement: 1));
        var p = Jugador(world, "Blanco", Player.StatusRepublicano, 10, 11);
        switch (quien)
        {
            case "gm": p.Privileges = 8; break;
            case "muerto": p.Hp = 0; break;
            case "invisible": p.Invisible = true; break;
            case "oculto": p.Oculto = true; break;
        }
        int antes = p.Hp;
        Correr(sys, 100);
        Assert.Equal(antes, p.Hp);
    }

    // ── LOS BANDOS DEL INI ────────────────────────────────────────────────────────────────────────────

    /// <summary>Los defaults son los del tester: cada bando ataca a todos menos a los suyos.</summary>
    [Theory]
    [InlineData(ServerIni.GuardiaImperial, Player.StatusCriminal, true)]
    [InlineData(ServerIni.GuardiaImperial, Player.StatusRepublicano, true)]
    [InlineData(ServerIni.GuardiaImperial, Player.StatusCaos, true)]
    [InlineData(ServerIni.GuardiaImperial, Player.StatusMilicia, true)]
    [InlineData(ServerIni.GuardiaImperial, Player.StatusCiudadano, false)]
    [InlineData(ServerIni.GuardiaImperial, Player.StatusArmada, false)]
    [InlineData(ServerIni.GuardiaRepublicano, Player.StatusCiudadano, true)]
    [InlineData(ServerIni.GuardiaRepublicano, Player.StatusArmada, true)]
    [InlineData(ServerIni.GuardiaRepublicano, Player.StatusCriminal, true)]
    [InlineData(ServerIni.GuardiaRepublicano, Player.StatusRepublicano, false)]
    [InlineData(ServerIni.GuardiaRepublicano, Player.StatusMilicia, false)]
    [InlineData(ServerIni.GuardiaDelCaos, Player.StatusCriminal, true)]
    [InlineData(ServerIni.GuardiaDelCaos, Player.StatusCiudadano, true)]
    [InlineData(ServerIni.GuardiaDelCaos, Player.StatusCaos, false)]
    public void Por_defecto_cada_bando_ataca_a_todos_menos_a_los_suyos(int bando, byte faccion, bool ataca)
    {
        Assert.Equal(ataca, new ServerIni().GuardiaAtacaA(bando, faccion));
        Assert.Equal(ataca, Ini().GuardiaAtacaA(bando, faccion));   // y un ini que no las menciona, igual
    }

    /// <summary>Las listas se escriben por nombre y se leen como las escribiría cualquiera: con mayúsculas, en
    /// plural, con un comentario al lado. Lo que no se reconoce se ignora y queda anotado para el aviso del
    /// arranque; una lista escrita vacía deja a ese bando sin enemigos.</summary>
    [Fact]
    public void Las_listas_del_ini_se_leen_por_nombre()
    {
        var cfg = Ini("GuardiasImperialesAtacanA=CAOS, renegados ; los de afuera\r\n"
                      + "GuardiasRepublicanosAtacanA=\r\n"
                      + "GuardiasDelCaosAtacanA=Ciudadano, Orcos\r\n");
        Assert.True(cfg.GuardiaAtacaA(ServerIni.GuardiaImperial, Player.StatusCaos));
        Assert.True(cfg.GuardiaAtacaA(ServerIni.GuardiaImperial, Player.StatusCriminal));
        Assert.False(cfg.GuardiaAtacaA(ServerIni.GuardiaImperial, Player.StatusRepublicano));
        Assert.False(cfg.GuardiaAtacaA(ServerIni.GuardiaRepublicano, Player.StatusCiudadano));
        Assert.True(cfg.GuardiaAtacaA(ServerIni.GuardiaDelCaos, Player.StatusCiudadano));
        Assert.False(cfg.GuardiaAtacaA(ServerIni.GuardiaDelCaos, Player.StatusCriminal));
        Assert.Equal(new[] { "Orcos" }, cfg.FaccionesDesconocidas);
    }

    /// <summary>En la pelea manda la lista del ini y no el default. Cada uno en su escena: con los dos al lado, el
    /// guardia se engancha con el primero que encuentra y el otro quedaría a salvo por el orden, no por la regla.</summary>
    [Fact]
    public void La_lista_del_ini_manda_sobre_el_default_en_la_pelea()
    {
        const string SoloAlCaos = "GuardiasImperialesAtacanA=Caos\r\n";
        var (world, sys, _) = Escena(Guardia(movement: 1), SoloAlCaos);
        var republicano = Jugador(world, "Republicano", Player.StatusRepublicano, 10, 11);
        Correr(sys, 100);
        Assert.Equal(republicano.MaxHp, republicano.Hp);

        var (world2, sys2, _) = Escena(Guardia(movement: 1), SoloAlCaos);
        var caotico = Jugador(world2, "Caotico", Player.StatusCaos, 10, 11);
        Correr(sys2, 100);
        Assert.True(caotico.Hp < caotico.MaxHp, "el guardia no le pegó al del Caos que el ini le pidió atacar");
    }

    /// <summary>
    /// CON LA DATA DE VERDAD. Los siete guardias imperiales de Ullathorpe salen de su mapa con sus fichas del
    /// install, y un republicano de nivel 30 que se le planta a uno a la vista termina golpeado; un ciudadano en
    /// el mismo lugar, no. Es lo que las escenas de arriba no pueden decir: que las fichas reales califican —su
    /// tipo, su bando, su golpe— y que las paredes de la ciudad no los dejan clavados.
    /// </summary>
    [Fact]
    public void En_Ullathorpe_los_guardias_de_la_data_van_por_el_republicano()
    {
        var d = GameData.Load();
        (World, Player) Probar(byte faccion)
        {
            var world = new World();
            var sys = new NpcSystem(world, d, ini: new ServerIni());
            foreach (var s in d.MapSpawns[Ciudad])
                if (d.Npcs.TryGetValue(s.NpcIndex, out var f) && f.NpcType == 2)
                    sys.AddSpawn(f, new WorldPos(Ciudad, (byte)s.X, (byte)s.Y), posOrig: true);
            Assert.Equal(7, world.Npcs.Count(n => n.Template.Status == ServerIni.GuardiaImperial));
            // Al lado del Guerrero de la entrada (33,25), a cuatro baldosas: lo tiene que ir a buscar.
            var p = Jugador(world, "Visitante", faccion, 33, 29);
            Correr(sys, 150);
            return (world, p);
        }

        var (_, republicano) = Probar(Player.StatusRepublicano);
        Assert.True(republicano.Hp < republicano.MaxHp, "los guardias de Ullathorpe no le hicieron nada al republicano");
        var (mundo, ciudadano) = Probar(Player.StatusCiudadano);
        Assert.Equal(ciudadano.MaxHp, ciudadano.Hp);
        Assert.All(mundo.Npcs, n => Assert.Equal(n.StartPos, n.Pos));   // ni se movieron de su puesto
    }

    // ── LA PERSECUCIÓN ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Al que ve lo va a buscar, y una vez que lo marcó NO lo suelta porque salga de la vista: lo sigue mientras siga
    /// en su ciudad. Es la diferencia con cualquier otra criatura, que pierde al jugador apenas sale de su caja 8×6.
    /// Cuando se va del mapa —de la ciudad—, el guardia vuelve a su puesto.
    /// </summary>
    [Fact]
    public void Persigue_hasta_que_te_vas_de_la_ciudad_y_vuelve_a_su_puesto()
    {
        var (world, sys, guardia) = Escena(Guardia());
        var enemigo = Jugador(world, "Enemigo", Player.StatusRepublicano, 10, 15);
        // Un testigo del bando del guardia, lejos: con la ciudad vacía la IA no corre (VB6: sin usuarios no hay IA).
        Jugador(world, "Testigo", Player.StatusCiudadano, 60, 60);

        Correr(sys, 20);
        Assert.Equal(1, Distancia(guardia, enemigo));                       // lo fue a buscar y lo tiene pegado

        enemigo.Pos = new WorldPos(Ciudad, 30, 15);                           // se aleja fuera de la caja de visión
        Correr(sys, 40);
        Assert.Equal(1, Distancia(guardia, enemigo));                       // y lo siguió igual

        enemigo.Pos = new WorldPos(Afuera, 30, 15);                           // se fue de la ciudad
        Correr(sys, 60);
        Assert.Equal(new WorldPos(Ciudad, 10, 10), guardia.Pos);            // de vuelta en su puesto
        Assert.Equal(0, guardia.GuardiaPersigueA);
    }

    /// <summary>Con <c>GuardiasPersiguen=0</c> no sale de su puesto, pero al que se le pone al lado le pega igual.</summary>
    [Fact]
    public void Quieto_no_persigue_pero_pega_al_que_pasa_al_lado()
    {
        var (world, sys, guardia) = Escena(Guardia(), "GuardiasPersiguen=0\r\n");
        var enemigo = Jugador(world, "Enemigo", Player.StatusRepublicano, 10, 15);
        Correr(sys, 40);
        Assert.Equal(new WorldPos(Ciudad, 10, 10), guardia.Pos);
        Assert.Equal(enemigo.MaxHp, enemigo.Hp);

        enemigo.Pos = new WorldPos(Ciudad, 10, 11);
        Correr(sys, 100);
        Assert.True(enemigo.Hp < enemigo.MaxHp, "el guardia quieto no le pegó al que se le puso al lado");
    }

    /// <summary>El que su ficha deja estático (la Torre, los cuatro de Rinkel) no camina aunque la regla persiga.</summary>
    [Fact]
    public void El_estatico_de_la_ficha_no_camina()
    {
        var (world, sys, guardia) = Escena(Guardia(movement: 1));
        Jugador(world, "Enemigo", Player.StatusRepublicano, 10, 15);
        Correr(sys, 40);
        Assert.Equal(new WorldPos(Ciudad, 10, 10), guardia.Pos);
    }

    /// <summary>Sin golpe ni un hechizo que lastime —los de Rinkel, los Cazadores y las Torres, cuyo único hechizo es
    /// un buff propio— el guardia se queda como en el original: no persigue a nadie para no hacerle nada.</summary>
    [Fact]
    public void El_que_no_tiene_con_que_pelear_se_queda_como_en_el_original()
    {
        var data = new GameData();
        data.AddSpellForTest(new SpellInfo(Index: 90, Name: "Prevención del mal", SubeHp: 0, MinHp: 0, MaxHp: 0,
                                           ManaCost: 0, Target: 1));
        var (world, sys, guardia) = Escena(Guardia(maxHit: 0, hechizos: new[] { 90 }), data: data);
        var enemigo = Jugador(world, "Enemigo", Player.StatusRepublicano, 10, 15);
        Correr(sys, 40);
        Assert.Equal(new WorldPos(Ciudad, 10, 10), guardia.Pos);
        Assert.Equal(enemigo.MaxHp, enemigo.Hp);
    }

    /// <summary>
    /// EL QUE PARALIZA DESPUÉS PEGA. El Paladín y el Asesino guardias saben un solo hechizo, el Tornado, que
    /// paraliza. Una criatura común gasta cada turno en intentarlo de nuevo aunque el blanco ya esté quieto (el
    /// contador se consume antes de mirar), así que no le pega nunca mientras dure; el guardia, al que ya está
    /// paralizado, le pega.
    /// </summary>
    [Fact]
    public void El_guardia_que_paraliza_despues_le_pega_al_paralizado()
    {
        var data = new GameData();
        data.AddSpellForTest(new SpellInfo(Index: 65, Name: "Tornado", SubeHp: 0, MinHp: 0, MaxHp: 0, ManaCost: 0,
                                           Target: 4, Paraliza: 1));
        var (world, sys, _) = Escena(Guardia(movement: 1, maxHit: 240, hechizos: new[] { 65 }), data: data);
        var enemigo = Jugador(world, "Enemigo", Player.StatusRepublicano, 10, 11);

        for (int i = 0; i < 40 && !enemigo.IsParalyzed(_tick); i++) Correr(sys, 1);
        Assert.True(enemigo.IsParalyzed(_tick), "el guardia no le tiró el Tornado");
        Assert.Equal(enemigo.MaxHp, enemigo.Hp);                            // el hechizo no lastima: sólo paraliza

        Correr(sys, 40);                                                     // la parálisis dura 50 vueltas
        Assert.True(enemigo.IsParalyzed(_tick));
        Assert.True(enemigo.Hp < enemigo.MaxHp, "el guardia gastó los turnos en paralizar de nuevo en vez de pegarle");
    }
}
