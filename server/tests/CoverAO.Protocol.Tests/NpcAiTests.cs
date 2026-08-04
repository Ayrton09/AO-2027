using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// Fija a quién le pega un NPC, que en el VB6 depende de <c>Hostile</c> (AI_NPC.bas:705, NPCAI):
///
///     Hostile &lt;&gt; 0  -> HostilMalvadoAI: le pega a CUALQUIER usuario adyacente.
///     Hostile  = 0  -> HostilBuenoAI (AI_NPC.bas:451): SÓLO le devuelve el golpe a su agresor,
///                      o sea al usuario cuyo nombre es `flags.AttackedBy` (que setea NPCAtacado,
///                      Modulo_UsUaRiOs.bas:1106, cuando el jugador lo ataca).
///
/// El slice no miraba `Hostile` para atacar —sólo para perseguir— así que todo NPC que no fuera mercader
/// ni banquero (aldeanos, sacerdotes, entrenadores) golpeaba sin provocación al que se le parara al lado.
/// Los NPCs de facción y los convertidores ni corren IA (el NPCAI sale antes, AI_NPC.bas:701).
/// </summary>
public class NpcAiTests
{
    private const int AttackTick = 25;   // NpcSystem.AttackEveryTicks: los NPCs actúan en los múltiplos de 25

    // PoderAtaque alto -> HitChance clampeada al 90% (Combat.HitChance), así que en 40 rondas la chance de
    // no pegar nunca es 0.1^40: el test es determinista en la práctica sin exponer el RNG del NpcSystem.
    private static NpcInfo Template(string name) =>
        new(Index: 1, Name: name, Body: 1, Head: 1, Heading: 3, MaxHp: 1000, MinHit: 1, MaxHit: 1,
            Def: 0, GiveExp: 0, GiveGld: 0, PoderEvasion: 0, PoderAtaque: 10_000);

    private static Player PlayerAt(World world, string name, byte x, byte y)
    {
        var p = new Player { CharIndex = world.AllocateCharIndex(), Name = name, Account = "cuenta_test" }; // sin Connection: los Send son no-op
        p.Pos = new WorldPos(1, x, y);
        p.MaxHp = p.Hp = 10_000;   // sobrevive las 40 rondas sin caer en KillPlayer
        world.Add(p);
        return p;
    }

    /// <summary>Deja al NPC en (10,10) con el jugador a `dist` tiles al sur. dist=1 (default) = pegado.</summary>
    private static (Npc npc, Player player, NpcSystem sys) Adjacent(string npcName = "Aldeano", byte dist = 1)
    {
        var world = new World();
        var data = new GameData();
        var npc = new Npc
        {
            CharIndex = world.AllocateCharIndex(), Template = Template(npcName), Hp = 1000,
            Pos = new WorldPos(1, 10, 10),
            // Quieto por defecto: los tests de ATAQUE no deben contaminarse con movimiento (VB6 TipoAI.ESTATICO).
            // Los de movimiento lo pisan a mano.
            Movement = 1,
        };
        world.AddNpc(npc);
        var player = PlayerAt(world, "Rugura", 10, (byte)(10 + dist));
        return (npc, player, new NpcSystem(world, data));
    }

    private static void RunRounds(NpcSystem sys, int rounds = 40)
    {
        for (int i = 1; i <= rounds; i++) sys.Update(AttackTick * i);
    }

    [Fact]
    public void Un_npc_no_hostil_no_pega_sin_provocacion()
    {
        var (npc, player, sys) = Adjacent();
        Assert.False(npc.Hostile);
        RunRounds(sys);
        Assert.Equal(player.MaxHp, player.Hp);   // el aldeano nunca levanta la mano
    }

    [Fact]
    public void Un_npc_no_hostil_le_devuelve_el_golpe_a_su_agresor()
    {
        var (npc, player, sys) = Adjacent();
        npc.AttackedBy = player.Name;            // lo que hace NPCAtacado cuando el jugador lo ataca
        RunRounds(sys);
        Assert.True(player.Hp < player.MaxHp, "el NPC no hostil debe devolverle el golpe a su agresor");
    }

    [Fact]
    public void Un_npc_no_hostil_no_le_pega_a_un_tercero()
    {
        var (npc, player, sys) = Adjacent();
        npc.AttackedBy = "Merlin";               // lo atacó OTRO: el de al lado es un transeúnte
        RunRounds(sys);
        Assert.Equal(player.MaxHp, player.Hp);
    }

    [Fact]
    public void Un_npc_pacifico_provocado_se_vuelve_hostil_y_le_pega_hasta_a_un_tercero()
    {
        // El combo NPCAtacado (Hostile=1) + HostilMalvadoAI: mientras dure la pelea, el pacífico provocado le
        // pega a CUALQUIERA que tenga al lado, no sólo a su agresor.
        var (npc, player, sys) = Adjacent();
        npc.AttackedBy = "Merlin";               // lo provocó Merlin...
        npc.Hostile = true;                      // ...y NPCAtacado lo puso hostil
        RunRounds(sys);
        Assert.True(player.Hp < player.MaxHp, "provocado, le pega también al transeúnte");
    }

    [Fact]
    public void El_pacifico_provocado_vuelve_a_ser_pacifico_cuando_se_queda_solo()
    {
        // VB6 RestoreOldMovement: Hostile vuelve a OldHostil. Si no, un aldeano provocado una vez quedaría
        // hostil para siempre y le pegaría a todo el que pasara.
        var (npc, player, sys) = Adjacent();
        npc.AttackedBy = player.Name;
        npc.Hostile = true;
        player.Pos = new WorldPos(1, 20, 20);    // se le va del lado
        RunRounds(sys, 1);
        Assert.False(npc.Hostile);
        Assert.Null(npc.AttackedBy);
    }

    [Fact]
    public void Un_hostil_de_ficha_sigue_hostil_despues_de_quedarse_solo()
    {
        // El RestoreOldMovement restaura a OldHostil, no a `false`: una fiera no se amansa por quedarse sola.
        var (npc, player, sys) = Adjacent("Lobo");
        npc.Hostile = npc.OriginallyHostile = true;
        player.Pos = new WorldPos(1, 20, 20);
        RunRounds(sys, 1);
        Assert.True(npc.Hostile);
    }

    [Fact]
    public void Un_npc_hostil_pega_sin_provocacion()
    {
        var (npc, player, sys) = Adjacent("Lobo");
        npc.Hostile = true;
        RunRounds(sys);
        Assert.True(player.Hp < player.MaxHp, "un NPC hostil ataca a cualquiera que tenga al lado");
    }

    [Fact]
    public void El_npc_no_hostil_se_olvida_del_agresor_cuando_se_le_va_del_lado()
    {
        // VB6 RestoreOldMovement (AI_NPC.bas:494): HostilBuenoAI limpia AttackedBy si no encuentra a quién
        // devolverle el golpe entre sus cuatro adyacentes.
        var (npc, player, sys) = Adjacent();
        npc.AttackedBy = player.Name;
        player.Pos = new WorldPos(1, 20, 20);    // el agresor se aleja
        RunRounds(sys, 1);
        Assert.Null(npc.AttackedBy);
    }

    // --- MOVIMIENTO: lo decide `Movement` (VB6 TipoAI, el `Select Case .Movement` de NPCAI), no la hostilidad.
    // 0 = NpcMaloAtacaUsersBuenos (IrUsuarioCercano: camina hacia el usuario que vea, MIRE O NO su Hostile),
    // 1 = ESTATICO, 3 = MueveAlAzar. El jugador se pone a 4 tiles: dentro de la visión pero sin tocarlo, así el
    // único que puede mover al NPC es IrUsuarioCercano. Un tick alcanza.

    [Fact]
    public void Un_estatico_no_se_mueve_aunque_tenga_al_jugador_a_la_vista()
    {
        var (npc, _, sys) = Adjacent(dist: 4);
        npc.Movement = 1;                        // ESTATICO
        var antes = npc.Pos;
        sys.Update(AttackTick);
        Assert.Equal(antes, npc.Pos);
    }

    [Fact]
    public void Un_Movement0_camina_hacia_el_usuario_aunque_NO_sea_hostil()
    {
        // El punto del port: IrUsuarioCercano NO mira Hostile. Un gallo (Hostile=0, Movement=0) te sigue —
        // pegarte o no es otra cosa, y eso sí lo decide Hostile (en NpcTryAttack).
        var (npc, player, sys) = Adjacent(dist: 4);
        npc.Movement = 0;
        Assert.False(npc.Hostile);
        var antes = npc.Pos;
        sys.Update(AttackTick);
        Assert.NotEqual(antes, npc.Pos);
        // Y se acerca: el jugador está al sur, así que el paso greedy va al sur.
        Assert.True(npc.Pos.Y > antes.Y);
    }

    [Fact]
    public void Un_Movement0_NO_se_mueve_si_ya_lo_tiene_pegado()
    {
        // VB6 FindDirection devuelve 0 cuando Distancia = 1: no se mueve, ataca.
        var (npc, _, sys) = Adjacent(dist: 1);
        npc.Movement = 0;
        var antes = npc.Pos;
        sys.Update(AttackTick);
        Assert.Equal(antes, npc.Pos);
    }

    [Fact]
    public void Un_Movement0_SI_se_acerca_a_un_jugador_en_DIAGONAL()
    {
        // El gate "ya lo tengo al lado" del VB6 es `Distancia(Pos, Target) = 1` (GameLogic.bas:879) y `Distancia`
        // es MANHATTAN (Matematicas.bas:51: Abs(dx)+Abs(dy)), no Chebyshev. En diagonal la Manhattan da 2, así
        // que el VB6 SÍ se acerca. Con Chebyshev el NPC se congelaba y pararse en diagonal era una casilla segura
        // permanente — el bicho nunca podía atacarte, porque NpcTryAttack sólo mira los 4 ortogonales.
        var world = new World();
        var npc = new Npc { CharIndex = world.AllocateCharIndex(), Template = Template("Lobo"), Hp = 1000,
                            Pos = new WorldPos(1, 10, 10), Movement = 0 };
        world.AddNpc(npc);
        PlayerAt(world, "Rugura", 11, 11);            // DIAGONAL: Chebyshev 1, Manhattan 2
        new NpcSystem(world, new GameData()).Update(AttackTick);
        Assert.NotEqual(new WorldPos(1, 10, 10), npc.Pos);
    }

    [Fact]
    public void Contra_una_pared_el_NPC_ESQUIVA_en_vez_de_quedar_clavado()
    {
        // VB6 FindDirection (GameLogic.bas:892): con el objetivo en un eje CARDINAL y el tile directo ocupado,
        // NO se queda trabado — tira 50/50 y se va por un perpendicular. El paso greedy anterior tenía un
        // fallback que para este caso era INALCANZABLE (dx == 0), así que el NPC forcejeaba contra la pared.
        // Escenario: jugador justo al sur, con una pared de fixture entre los dos.
        var world = new World();
        var data = new GameData();
        data.AddBlocked(1, 10, 11);                   // la pared, en el camino directo
        var npc = new Npc { CharIndex = world.AllocateCharIndex(), Template = Template("Lobo"), Hp = 1000,
                            Pos = new WorldPos(1, 10, 10), Movement = 0 };
        world.AddNpc(npc);
        PlayerAt(world, "Rugura", 10, 13);            // 3 al sur: mismo eje, y la pared en el medio
        new NpcSystem(world, data).Update(AttackTick);
        // Se movió, y NO fue al sur (bloqueado): esquivó al este o al oeste.
        Assert.NotEqual(new WorldPos(1, 10, 10), npc.Pos);
        Assert.Equal(10, npc.Pos.Y);                  // el sidestep es horizontal
        Assert.True(npc.Pos.X == 9 || npc.Pos.X == 11);
    }

    [Fact]
    public void Un_NPC_pisa_el_tile_de_un_CADAVER_y_lo_intercambia_de_lugar()
    {
        // VB6 LegalPosNPC (GameLogic.bas:642): `.UserIndex = 0 Or IsDeadChar` — un tile con un MUERTO SÍ es
        // legal. Y MoveNPCChar (MODULO_NPCs.bas:707) no lo atraviesa: lo INTERCAMBIA de lugar, empujando al
        // cadáver al tile del que venía el NPC. Antes el .NET bloqueaba contra cualquier jugador, vivo o muerto.
        var world = new World();
        var data = new GameData();
        // Encerrado: el único destino posible es el tile del cadáver, al sur. Si no, el paseo al azar se aleja
        // y el test mide otra cosa. (FindDirection no sirve acá: su PuedeNpc trata el tile del muerto como
        // ocupado —`.UserIndex = 0` a secas— así que el swap sólo pasa cuando el paso cae ahí igual.)
        foreach (var (bx, by) in new[] { (9, 10), (11, 10), (10, 9) }) data.AddBlocked(1, bx, by);
        var npc = new Npc { CharIndex = world.AllocateCharIndex(), Template = Template("Lobo"), Hp = 1000,
                            Pos = new WorldPos(1, 10, 10), Movement = 3 };   // MueveAlAzar, para forzar el paso
        world.AddNpc(npc);
        var muerto = PlayerAt(world, "Merlin", 10, 11);
        muerto.Hp = 0;                                  // cadáver
        Assert.False(muerto.IsAlive);
        var sys = new NpcSystem(world, data);
        // El wander es 1/12 y elige dirección al azar (1/4 el sur): con 900 rondas la chance de que nunca salga
        // es despreciable.
        for (int i = 1; i <= 900 && npc.Pos != new WorldPos(1, 10, 11); i++) sys.Update(AttackTick * i);
        Assert.Equal(new WorldPos(1, 10, 11), npc.Pos);     // el NPC ocupó el tile del cadáver
        Assert.Equal(new WorldPos(1, 10, 10), muerto.Pos);  // y el cadáver quedó donde estaba el NPC
    }

    [Fact]
    public void A_un_jugador_VIVO_no_lo_mueve_ningun_NPC()
    {
        // El control del test de arriba: el swap es SÓLO para cadáveres (IsDeadChar). A un vivo el NPC ni lo
        // toca — su tile no es legal.
        var world = new World();
        var npc = new Npc { CharIndex = world.AllocateCharIndex(), Template = Template("Lobo"), Hp = 1000,
                            Pos = new WorldPos(1, 10, 10), Movement = 3 };
        world.AddNpc(npc);
        var vivo = PlayerAt(world, "Rugura", 10, 11);
        var sys = new NpcSystem(world, new GameData());
        for (int i = 1; i <= 200; i++) sys.Update(AttackTick * i);
        Assert.Equal(new WorldPos(1, 10, 11), vivo.Pos);    // no se movió de su tile
        Assert.NotEqual(new WorldPos(1, 10, 11), npc.Pos);  // y el NPC nunca lo ocupó
    }

    [Fact]
    public void Si_el_objetivo_esta_RODEADO_el_NPC_no_forcejea()
    {
        // VB6 FindDirection (GameLogic.bas:885): `If Rodeado(Target) Then FindDirection = 0`. Si los 4
        // ortogonales del objetivo estan ocupados, no tiene sentido acercarse.
        var world = new World();
        var data = new GameData();
        foreach (var (bx, by) in new[] { (10, 12), (10, 14), (9, 13), (11, 13) }) data.AddBlocked(1, bx, by);
        var npc = new Npc { CharIndex = world.AllocateCharIndex(), Template = Template("Lobo"), Hp = 1000,
                            Pos = new WorldPos(1, 10, 10), Movement = 0 };
        world.AddNpc(npc);
        PlayerAt(world, "Rugura", 10, 13);            // encerrado por los 4 lados
        new NpcSystem(world, data).Update(AttackTick);
        Assert.Equal(new WorldPos(1, 10, 10), npc.Pos);
    }

    [Fact]
    public void Los_GuardiasCity_no_corren_IA_ni_se_mueven()
    {
        // VB6 NPCAI (AI_NPC.bas:700) sale de una para TRES eNPCType: GuardiasCity(2), facciones(5) y
        // Convertidores(18). El .NET gateaba sólo por los flags de facción, que los pone el fixture a mano y la
        // data real NUNCA — así que los 7 entrenadores de clase de Ullathorpe (NpcType=2: Clérigo, Guerrero,
        // Bardo, Paladín, Mago, Mercenario, Asesino) abandonaban su puesto y seguían al jugador.
        foreach (var tipo in new[] { 2, 5, 18 })
        {
            var world = new World();
            var npc = new Npc
            {
                CharIndex = world.AllocateCharIndex(), Hp = 1000, Pos = new WorldPos(1, 10, 10), Movement = 0,
                Template = Template("Guerrero") with { NpcType = tipo },
            };
            world.AddNpc(npc);
            var player = PlayerAt(world, "Rugura", 10, 14);
            npc.AttackedBy = player.Name;    // ni provocado
            npc.Hostile = true;
            RunRounds(new NpcSystem(world, new GameData()));
            Assert.Equal(new WorldPos(1, 10, 10), npc.Pos);   // no se movió
            Assert.Equal(player.MaxHp, player.Hp);            // ni pegó
        }
    }

    /// <summary>De `escenas` escenarios frescos con el jugador en (px,py) y el NPC en (10,10), en cuántos se
    /// mueve el NPC en su PRIMER tick.
    ///
    /// Es la única forma limpia de separar las dos ramas: si VE al jugador lo persigue y se mueve SIEMPRE; si no
    /// lo ve, el VB6 lo hace <b>bailar</b> — un paso al azar 1/11 (AI_NPC.bas:361) — así que se mueve rara vez.
    /// Un solo tick no alcanza (fallaría ese 9%), y mirar si LLEGA tampoco: un paso de baile con suerte lo deja
    /// a dy=6 y ahí ya lo ve, porque la caja se evalúa desde su posición ACTUAL. Contar sobre escenas
    /// independientes hace la diferencia abismal (30/30 vs ~3/30) y la aserción deja de ser una moneda.</summary>
    private static int SeMuevenEnElPrimerTick(int px, int py, int escenas = 30)
    {
        int movidos = 0;
        for (int i = 0; i < escenas; i++)
        {
            var world = new World();
            var npc = new Npc { CharIndex = world.AllocateCharIndex(), Template = Template("Lobo"), Hp = 1000,
                                Pos = new WorldPos(1, 10, 10), Movement = 0 };
            world.AddNpc(npc);
            PlayerAt(world, "Rugura", (byte)px, (byte)py);
            new NpcSystem(world, new GameData()).Update(AttackTick);
            if (npc.Pos != new WorldPos(1, 10, 10)) movidos++;
        }
        return movidos;
    }

    [Fact]
    public void La_vision_de_IrUsuarioCercano_es_RECTANGULAR_8x6()
    {
        // VB6 AI_NPC.bas:50-51: RANGO_VISION_X=8, RANGO_VISION_Y=6. NO es un radio, y los dos ejes se comparan
        // por separado: a 8 tiles en X lo ve, a 7 en Y no. Copiar el Chebyshev de los gates de al lado habría
        // sido un bug — y (8,7) o (9,6) son justo los casos donde un radio y una caja difieren.
        Assert.Equal(30, SeMuevenEnElPrimerTick(18, 10));   // dx = 8: BORDE del rango X -> lo ve, persigue siempre
        Assert.Equal(30, SeMuevenEnElPrimerTick(10, 16));   // dy = 6: BORDE del rango Y -> lo ve, persigue siempre
        // Fuera de la caja no lo ve: sólo el baile 1/11. Con 30 escenas la media es ~2.7 (sd ~1.6), así que 10
        // está a más de 4 sigmas: el test no puede confundir "baila" con "persigue".
        Assert.True(SeMuevenEnElPrimerTick(19, 10) <= 10, "a 9 tiles en X no lo ve: sólo baila");
        Assert.True(SeMuevenEnElPrimerTick(10, 17) <= 10, "a 7 tiles en Y no lo ve: la caja es 8x6, no un radio");
    }

    [Fact]
    public void Al_spawnear_se_copian_la_hostilidad_y_el_Movement_de_la_ficha()
    {
        // VB6 MODULO_NPCs.bas:994 (`.flags.OldHostil = .Hostile`) + :971 (`.Movement = val(...)`). Es el único
        // origen de ambos en producción; sin ellos ningún NPC del mundo real se comportaría según su data.
        var world = new World();
        var sys = new NpcSystem(world, new GameData());
        sys.AddSpawn(Template("Lobo") with { Movement = 0 }, new WorldPos(1, 30, 30), hostile: true);
        sys.AddSpawn(Template("Aldeano") with { Movement = 1 }, new WorldPos(1, 40, 40), hostile: false);
        var lobo = world.NpcAt(new WorldPos(1, 30, 30))!;
        var aldeano = world.NpcAt(new WorldPos(1, 40, 40))!;
        Assert.True(lobo.OriginallyHostile);
        Assert.Equal(0, lobo.Movement);
        Assert.False(aldeano.OriginallyHostile);
        Assert.Equal(1, aldeano.Movement);
        // Y el override explícito (lo que usa el fixture) gana sobre la ficha.
        sys.AddSpawn(Template("Lobo") with { Movement = 0 }, new WorldPos(1, 50, 50), movement: 1);
        Assert.Equal(1, world.NpcAt(new WorldPos(1, 50, 50))!.Movement);
    }

    [Fact]
    public void Los_enlistadores_y_convertidores_no_corren_ia_ni_devuelven_el_golpe()
    {
        // VB6 NPCAI (AI_NPC.bas:701) sale antes de pelear para eNPCType.facciones y eNPCType.Convertidores.
        foreach (var makeService in new Action<Npc>[] { n => n.FactionEnlist = 5, n => n.FactionConvert = 1 })
        {
            var (npc, player, sys) = Adjacent();
            makeService(npc);
            npc.AttackedBy = player.Name;        // aun provocado, no pelea
            RunRounds(sys);
            Assert.Equal(player.MaxHp, player.Hp);
        }
    }

    [Fact]
    public void A_un_GM_no_le_pega_ninguna_criatura()
    {
        // VB6 flags.AdminPerseguible: TCP.bas:1925 lo prende SÓLO en la rama del usuario común, así que un GM
        // queda en False y las tres ramas de la IA lo saltean (AI_NPC.bas:88/160/229).
        var (npc, player, sys) = Adjacent();
        npc.Hostile = true;                      // hostil de verdad: le pegaría a cualquiera
        npc.AttackedBy = player.Name;            // y encima está provocado
        player.Privileges = 8;                   // Dios
        Assert.False(player.AdminPerseguible);
        RunRounds(sys);
        Assert.Equal(player.MaxHp, player.Hp);
    }

    [Fact]
    public void Una_criatura_pega_UNA_sola_vez_por_tick()
    {
        // Regresión: al reordenar las fases de Update() quedaron DOS barridos de ataque y toda criatura pegaba
        // dos veces por tick — el doble de daño en todo el PvE, y ni la suite de 108 flows lo notó (miden el
        // daño POR GOLPE, que no cambia, no la frecuencia). El VB6 corre la fase de ataque UNA vez por NPCAI.
        var (npc, player, sys) = Adjacent();
        npc.Hostile = true;                       // le pega a cualquiera que tenga al lado
        int hpAntes = player.Hp;
        sys.Update(AttackTick);                   // UN solo tick de ataque
        int golpes = hpAntes - player.Hp;         // MinHit = MaxHit = 1 → daño == cantidad de golpes
        Assert.InRange(golpes, 0, 1);             // 0 si falló la tirada, 1 si pegó. Nunca 2.
    }

    [Fact]
    public void SEGUIR_sobre_un_MERCADER_tambien_se_revierte()
    {
        // El reset vivía detrás del early-return de mercader/banquero, así que un /SEGUIR sobre el herrero lo
        // dejaba en NPCDEFENSA para siempre: se iba caminando de la ciudad detrás del GM y no volvía nunca a su
        // puesto (el respawn sólo se dispara al morir). El VB6 no excluye a los mercaderes de NPCAI.
        var world = new World();
        var npc = new Npc { CharIndex = world.AllocateCharIndex(), Template = Template("Herrero"), Hp = 1000,
                            Pos = new WorldPos(1, 10, 10), Movement = 1,
                            Stock = new List<MerchantSlot>() };   // lo que lo hace mercader
        world.AddNpc(npc);
        var gm = PlayerAt(world, "Rugura", 10, 15);
        gm.Privileges = 8;
        npc.ToggleFollow(gm.Name);
        var sys = new NpcSystem(world, new GameData());
        for (int i = 1; i <= 5; i++) sys.Update(AttackTick * i);
        Assert.Equal(new WorldPos(1, 10, 10), npc.Pos);   // no se movió de su puesto
        Assert.Equal(1, npc.Movement);
    }

    [Fact]
    public void SEGUIR_sobre_una_criatura_SIN_dueño_se_revierte_sola()
    {
        // El /SEGUIR del original casi no se nota, y no por un bug de este port: la fase de ATAQUE corre antes
        // que la de movimiento y termina en RestoreOldMovement en cuanto no tiene a nadie adyacente a quien
        // pegarle, sacándole el NPCDEFENSA en el mismo tick. Se porta el mecanismo, así que el port lo reproduce.
        var world = new World();
        var npc = new Npc { CharIndex = world.AllocateCharIndex(), Template = Template("Lobo"), Hp = 1000,
                            Pos = new WorldPos(1, 10, 10), Movement = 1 };   // ESTATICO, como un mercader
        world.AddNpc(npc);
        var gm = PlayerAt(world, "Rugura", 10, 16);   // a 6 tiles: dentro de la caja 8×6, lejos de "adyacente"
        gm.Privileges = 8;
        npc.ToggleFollow(gm.Name);                    // exactamente lo que hace /SEGUIR
        Assert.Equal(GameLimits.TipoAiNpcDefensa, npc.Movement);
        new NpcSystem(world, new GameData()).Update(AttackTick);
        Assert.Equal(new WorldPos(1, 10, 10), npc.Pos);   // no llegó a dar un paso
        Assert.Equal(1, npc.Movement);                    // y le devolvieron el modo de su ficha, no un 0
    }

    [Fact]
    public void SEGUIR_sobre_una_MASCOTA_si_la_hace_perseguir_al_GM()
    {
        // El caso que SÍ queda vivo en el original: a una criatura con dueño (`MaestroUser > 0`) la fase de
        // ataque no le corre, así que nadie le restaura el Movement y SeguirAgresor (AI_NPC.bas:375) llega a
        // ejecutarse — persigue al nombre que tiene en AttackedBy mientras lo vea en la caja 8×6.
        var world = new World();
        var dueño = PlayerAt(world, "Merlin", 30, 30);
        var npc = new Npc { CharIndex = world.AllocateCharIndex(), Template = Template("Lobo"), Hp = 1000,
                            Pos = new WorldPos(1, 10, 10), Movement = 8, OwnerCharIndex = dueño.CharIndex };
        world.AddNpc(npc);
        var gm = PlayerAt(world, "Rugura", 10, 15);
        gm.Privileges = 8;
        npc.ToggleFollow(gm.Name);
        var sys = new NpcSystem(world, new GameData());
        for (int i = 1; i <= 3; i++) sys.Update(AttackTick * i);
        Assert.True(npc.Pos.Y > 10, $"la mascota debía acercarse al GM y quedó en {npc.Pos}");
        Assert.Equal(GameLimits.TipoAiNpcDefensa, npc.Movement);   // a ella nadie se lo restaura
        // Y el segundo /SEGUIR la devuelve a seguir a su amo.
        npc.ToggleFollow(gm.Name);
        Assert.False(npc.Follow);
        Assert.Equal(8, npc.Movement);
    }

    [Fact]
    public void Una_mascota_en_modo_SEGUIR_tambien_PEGA()
    {
        // SeguirAgresor es el ÚNICO camino por el que golpea una criatura con dueño: NPCAI (AI_NPC.bas:702) deja
        // a las mascotas fuera de la fase de ataque. El port no lo replicaba y el modo seguir sólo caminaba: la
        // mascota se le paraba al lado a la víctima sin hacerle nada.
        var world = new World();
        var dueño = PlayerAt(world, "Merlin", 30, 30);
        var npc = new Npc { CharIndex = world.AllocateCharIndex(), Template = Template("Lobo"), Hp = 1000,
                            Pos = new WorldPos(1, 10, 10), Movement = 8, OwnerCharIndex = dueño.CharIndex };
        world.AddNpc(npc);
        var victima = PlayerAt(world, "Pepe", 10, 11);   // PEGADA, y usuario común (perseguible)
        npc.ToggleFollow(victima.Name);
        var sys = new NpcSystem(world, new GameData());
        RunRounds(sys);
        Assert.True(victima.Hp < victima.MaxHp, "la mascota en modo seguir debe golpear a su objetivo");
    }

    [Fact]
    public void Una_mascota_de_la_ARMADA_no_persigue_a_un_no_renegado()
    {
        // Freno de facción de SeguirAgresor (AI_NPC.bas:455-462): si el AMO es de la Armada y ni él ni el
        // objetivo son renegados, la persecución se corta en el primer tick y la mascota vuelve con su amo.
        var world = new World();
        var amo = PlayerAt(world, "Merlin", 30, 30);
        amo.FactionStatus = Player.StatusArmada;
        var npc = new Npc { CharIndex = world.AllocateCharIndex(), Template = Template("Lobo"), Hp = 1000,
                            Pos = new WorldPos(1, 10, 10), Movement = 8, OwnerCharIndex = amo.CharIndex };
        world.AddNpc(npc);
        var ciudadano = PlayerAt(world, "Pepe", 10, 14);
        ciudadano.FactionStatus = Player.StatusCiudadano;   // no renegado
        npc.ToggleFollow(ciudadano.Name);
        new NpcSystem(world, new GameData()).Update(AttackTick);
        Assert.Equal(new WorldPos(1, 10, 10), npc.Pos);              // no lo persiguió
        Assert.Null(npc.AttackedBy);
        Assert.Equal(GameLimits.TipoAiSigueAmo, npc.Movement);      // volvió con su amo (FollowAmo)
    }
    /// <summary>
    /// El NPC que sabe MAGIA la tira en vez de pegar (VB6 LanzaSpells + NpcLanzaUnSpell, AI_NPC.bas:169/295).
    /// Son 70 de los 291 del install; hasta que el transcode no trajo el campo, peleaban sólo a melee.
    /// </summary>
    [Fact]
    public void Un_npc_con_hechizos_ataca_con_magia()
    {
        var world = new World();
        var data = new GameData();
        // El hechizo tiene que existir en GameData o el casteo se cae al piso en silencio.
        data.AddSpellForTest(new SpellInfo(Index: 36, Name: "Hechizo de NPC", SubeHp: 2,
            MinHp: 30, MaxHp: 50, ManaCost: 0, Target: 3));
        var npc = new Npc
        {
            CharIndex = world.AllocateCharIndex(), Hp = 1000, Pos = new WorldPos(1, 10, 10), Movement = 1,
            // OJO: `Hostile` es del Npc, NO del NpcInfo — ponerlo sólo en el template no hace nada.
            Hostile = true, OriginallyHostile = true,
            // MinHit/MaxHit en 0: si el daño aparece, vino de la magia y no de un golpe.
            Template = Template("Mago") with { MinHit = 0, MaxHit = 0, Spells = new[] { 36 } },
        };
        world.AddNpc(npc);
        var player = PlayerAt(world, "Rugura", 10, 11);
        RunRounds(new NpcSystem(world, data));

        Assert.True(player.Hp < player.MaxHp, "el mago nunca tiró el hechizo");
    }

    [Fact]
    public void Un_npc_sin_hechizos_sigue_pegando_a_melee()
    {
        // El contrapeso: la rama nueva no debe robarle el golpe al 76% de los NPCs que no saben magia.
        // Adjacent() arma un no-hostil; para que pegue hace falta que sea su agresor (VB6 HostilBuenoAI).
        var (npc, player, sys) = Adjacent("Lobo");
        npc.AttackedBy = player.Name;
        Assert.Null(npc.Template.Spells);
        RunRounds(sys);
        Assert.True(player.Hp < player.MaxHp);
    }

}
