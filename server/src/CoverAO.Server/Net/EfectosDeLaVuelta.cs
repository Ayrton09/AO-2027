using CoverAO.Server.Data;
using CoverAO.Server.Game;

namespace CoverAO.Server.Net;

/// <summary>
/// LO QUE LE PASA A CADA VIVO EN CADA VUELTA DEL LOOP: los efectos que vencen, el frío, el veneno, el fuego, la curación, el
/// maná al meditar y la energía. El original los corre en CADA pasada del loop (mMainLoop.bas:155-215), cada uno con su propio
/// contador; ver fase5/unidades-del-loop.md. Vivía adentro del Program.cs, donde ninguna prueba lo alcanzaba.
/// </summary>
public static class EfectosDeLaVuelta
{
    public static void Aplicar(Player pl, long tick, World world, GameData gameData, ServerIni serverIni, PacketDispatcher? dispatcher,
        Random regenRng)
    {
        // Avisos GRANULARES, como el original: un paquete de 3-5 bytes por stat que cambió, en vez del
        // UpdateUserStats de ~26. Éste es el bucle más caliente que hay (corre por jugador cada 40 ms).
        bool cambioHp = false, cambioMana = false, cambioSta = false;
        if (pl.ExpireEffects(tick))   // se venció un boost temporal de poción
        {
            // VB6 DuracionPociones (General.bas:1166): manda los DOS atributos aunque haya cambiado uno
            // solo — el propio autor lo comenta ("No me gusta que ambos... pero bueno"). Es paridad.
            pl.Connection?.Send(ServerMessages.UpdateDexterity(pl.Agility));
            pl.Connection?.Send(ServerMessages.UpdateStrenght(pl.Strength));
        }
        if (!pl.IsParalyzed(tick) && pl.ClientParalyzed) // paralysis wore off → toggle the client flag back
        {
            pl.ClientParalyzed = false;
            pl.ParalyzedByCharIndex = 0;
            pl.Connection?.Send(ServerMessages.ParalizeOK());
        }
        if (!pl.IsBlinded(tick) && pl.ClientBlind) // blindness wore off → tell the client
        {
            pl.ClientBlind = false;
            pl.Connection?.Send(ServerMessages.BlindNoMore());
        }
        // Invisibilidad por hechizo vencida → volver visible (el /INVISIBLE de GM deja InvisibleUntilTick=0,
        // así que no la toca: su invisibilidad es permanente hasta re-togglear).
        dispatcher?.VencerInvisibilidad(pl, tick);
        // Metamorfosis vencida → vuelve a su forma (regla de la casa MetamorfosisDelDruida; con la regla
        // apagada nadie se transforma nunca y este chequeo es un `!= 0` que siempre da falso, y con la
        // regla en permanente —el default— el tick de vencimiento no lo alcanza ninguna vuelta).
        dispatcher?.VencerMetamorfosis(pl, tick);
        // DoPermanecerOculto (VB6 Trabajo.bas:37): el ocultamiento vencido revela al jugador (LocaleMsg 307).
        // No pisa la invisibilidad por hechizo/GM: si Invisible sigue activo, no se emite SetInvisible.
        if (pl.OcultoUntilTick != 0 && tick >= pl.OcultoUntilTick)
        {
            pl.OcultoUntilTick = 0;
            if (pl.Oculto)
            {
                pl.Oculto = false;
                if (!pl.Invisible)
                {
                    world.BroadcastToObservers(pl.Pos, ServerMessages.SetInvisible(pl.CharIndex, false), pl.CharIndex);
                    pl.Connection?.Send(ServerMessages.SetInvisible(pl.CharIndex, false));
                    pl.Connection?.Send(ServerMessages.LocaleMsg(307));
                }
            }
        }
        // EfectoFrio (VB6 General.bas:854, llamado desde mMainLoop.bas:164). Gate del original: estar
        // DESNUDO —sin armadura equipada— y ser usuario común, no GM. Cada IntervaloFrio (50 vueltas = 2 s)
        // saca el 5% del máximo: en un mapa de NIEVE se lo saca a la VIDA y PUEDE MATAR; en cualquier otro
        // terreno se lo saca a la ENERGÍA. Los 95 mapas nevados del install no gateaban nada hasta ahora
        // porque el transcode se salteaba el campo `terrain` del header de cada .csm.
        if (!pl.IsGm && Equipment.EquippedOfCategory(pl, gameData, 2) is null
            && Survival.Cumplio(ref pl.ColdCounter, Survival.ColdIntervalTicks))
        {
            if (gameData.IsSnowMap(pl.Pos.Map))
            {
                pl.Connection?.Send(ServerMessages.LocaleMsg(46));
                pl.Hp -= Survival.ColdBite(pl.MaxHp);
                if (pl.Hp < 1) { pl.Hp = 0; PacketDispatcher.KillPlayer(world, gameData, pl, "el frio"); }
                cambioHp = true;
            }
            else
            {
                pl.Stamina = Math.Max(0, pl.Stamina - Survival.ColdBite(pl.MaxStamina));
                cambioSta = true;
            }
        }
        if (!pl.IsAlive) { AvisarAlMuerto(pl, cambioHp, cambioSta); return; }   // lo mató el frío
        // EL RELOJ DEL PORTAL DE LUM (VB6 PasarSegundotelep, General.bas:1855). En el original cuelga de
        // un timer de UN SEGUNDO aparte del loop; acá se cuenta en vueltas como todo lo demás.
        if (pl.CreoTeleport && ++pl.PortalTickCounter >= Survival.TicksPorSegundo)
        {
            pl.PortalTickCounter = 0;
            PacketDispatcher.PasoDelPortalDeLum(world, gameData, pl);
        }
        if (PacketDispatcher.EfectoVeneno(world, gameData, pl, tick, regenRng)) cambioHp = true;   // y el UpdateHP
        if (!pl.IsAlive) { AvisarAlMuerto(pl, cambioHp, cambioSta); return; }   // lo mató el veneno
        // EfectoIncinerado (VB6 FileIO.bas:2010, llamado desde mMainLoop.bas:170). NO es veneno: pega
        // RandomNumber(45, 65) por golpe de fuego —contra el 1 a 5 del veneno— y avisa con LocaleMsg 48.
        // El original excluye a los GMs (`Privilegios And PlayerType.User`).
        //
        // SON DOS RELOJES DISTINTOS Y ESA ES LA GRACIA. El contador `Counters.Incinerado` mide la DURACIÓN
        // y avanza `DeltaTick` —que es (ms transcurridos / 40), o sea ~1 por vuelta— en CADA vuelta, se
        // pegue o no; a las 50 vueltas (2 s) el fuego se apaga con el LocaleMsg 389. El DAÑO, en cambio,
        // sale cuando lo deja la cadencia de GOLPE, y se la CONSUME: mientras te quemás no podés pegar.
        //
        // Acá el fuego duraba CINCUENTA GOLPES de uno cada 20 s: dieciséis minutos y ~2.700 de daño, contra
        // los ~110 de dos golpes del original. El 50 estaba leído como "golpes" cuando es "vueltas".
        //
        // Y AL APAGARSE EL CONTADOR QUEDA EN UN NÚMERO AL AZAR entre -100 y 100 ("Invi variable :D", dice
        // la fuente). Como el hechizo NO lo resetea al prender el fuego (modHechizos.bas:1150 sólo toca el
        // flag), la quemadura SIGUIENTE dura `50 - ese número`: entre 0 y 6 s, y una de cada cuatro se
        // apaga en el acto sin hacer daño. Es del original y se porta igual.
        if (pl.Incinerado && !pl.IsGm)
        {
            if (pl.IncineradoCounter < 50)
            {
                pl.IncineradoCounter++;
                // La cadencia sale del ini y NO del 0 del fixture: ese apagado existe para que los flows
                // manden PAQUETES en ráfaga, y esto no lo manda un cliente. Con 0 el fuego pegaría en cada
                // vuelta y mataría a cualquiera en dos segundos.
                if (CombatTiming.PermiteAtacar(pl, Environment.TickCount64, serverIni.IntervaloAtacar))
                {
                    pl.Connection?.Send(ServerMessages.LocaleMsg(48));
                    world.BroadcastToObservers(pl.Pos, ServerMessages.PlayWave(78, pl.Pos.X, pl.Pos.Y));
                    pl.Hp -= regenRng.Next(45, 66);                   // RandomNumber(45, 65), inclusivo
                    if (pl.Hp < 1) { pl.Hp = 0; PacketDispatcher.KillPlayer(world, gameData, pl, "el fuego"); }
                    cambioHp = true;
                }
            }
            else
            {
                pl.IncineradoCounter = regenRng.Next(-100, 101);      // RandomNumber(-100, 100), inclusivo
                pl.Incinerado = false;
                pl.Connection?.Send(ServerMessages.LocaleMsg(389));   // se apagó
            }
        }
        // MURIÓ EN ESTA VUELTA (frío, veneno o fuego): lo que sigue es de vivos. Como `IsAlive` es la vida mayor que cero, la
        // curación natural de abajo le devolvía vida a un cadáver sin pasar por la resurrección, y el fuego volvía a matarlo
        // (auditoría externa del 23/10).
        if (!pl.IsAlive) { AvisarAlMuerto(pl, cambioHp, cambioSta); return; }
        // Sanar (VB6 General.bas + mMainLoop.bas:186-206): la curación natural es LENTA de pie (64 s) y
        // rápida descansando (4 s), y devuelve RandomNumber(2, 5% de la energía máxima) avisando con
        // LocaleMsg 389. Sólo si no hay hambre ni sed (mMainLoop.bas:184 envuelve Sanar+RecStamina).
        if (pl.Hunger > 0 && pl.Thirst > 0 && pl.Hp < pl.MaxHp
            && Survival.Cumplio(ref pl.HpCounter, pl.Resting ? Survival.HealRestingIntervalTicks : Survival.HealIntervalTicks))
        {
            pl.Hp = Math.Min(pl.MaxHp, pl.Hp + Survival.HealAmount(pl.MaxStamina, (lo, hi) => regenRng.Next(lo, hi + 1)));
            pl.Connection?.Send(ServerMessages.LocaleMsg(389));
            cambioHp = true;   // VB6 Sanar: WriteUpdateHP
        }
        // Maná: en el VB6 NO hay regen pasiva — sólo vuelve MEDITANDO (Sanar no toca el maná). Antes acá se
        // regalaban 5/tick caminando/peleando, debilitando la gestión de recurso del mago. La cadencia de
        // meditar se mantiene en 3 s: es la que verificaron los flujos, y DoMeditar tiene su propia lógica
        // de suerte por skill que no es parte de esta corrección de unidades.
        // Y cuánto vuelve NO es una constante: el original tira los dados CADA VUELTA con una suerte que
        // sale de la skill Meditar (1 de 35 con la skill en 0, 1 de 5 con la skill en 100) y devuelve un
        // porcentaje del maná MÁXIMO (Trabajo.bas:2094-2103). Acá había un +20 fijo cada 3 s: le regalaba
        // maná al que recién empieza, se lo escatimaba al mago grande, y dejaba a la skill Meditar sin
        // ningún efecto — o sea que entrenarla no servía para nada.
        if (pl.Meditating && pl.Mana < pl.MaxMana
            && regenRng.Next(1, Survival.SuerteAlMeditar(pl.Skills[GameLimits.SkillMeditar]) + 1) == 1)
        {
            pl.Mana = Math.Min(pl.MaxMana, pl.Mana + Survival.ManaAlMeditar(pl.MaxMana, gameData.PorcentajeRecuperoMana));
            cambioMana = true;   // VB6: WriteUpdateMana
            dispatcher?.MeditarSubeSkill(pl);   // Trabajo.bas:2113, adentro del mismo `If res = 1`
        }
        // RecStamina (VB6 General.bas:1093): cada 50 vueltas (2 s), descanses o no —el original le pasa ese
        // 50 literal en las dos ramas—, y con un corte que el port no tenía: no recupera NADA mientras
        // estás trabajando.
        if (pl.Hunger > 0 && pl.Thirst > 0 && pl.Stamina < pl.MaxStamina && !pl.Laborando
            && Survival.RecuperaEnergia(Equipment.EstaDesnudo(pl, gameData), pl.Mounted)
            && Survival.Cumplio(ref pl.StaCounter, Survival.StaminaIntervalTicks))
        {
            pl.Stamina = Math.Min(pl.MaxStamina, pl.Stamina + Survival.StaminaAmount(
                pl.MaxStamina, pl.Skills[GameLimits.SkillSupervivencia], (lo, hi) => regenRng.Next(lo, hi + 1)));
            cambioSta = true;   // VB6 RecStamina: WriteUpdateSta
        }
        // El descanso TERMINA SOLO cuando la vida y la energía están llenas (VB6 mMainLoop.bas:220): apaga
        // el flag, manda RestOK y avisa. El port dejaba al jugador descansando para siempre.
        if (pl.Resting && pl.Hp == pl.MaxHp && pl.Stamina == pl.MaxStamina)
        {
            pl.Resting = false;
            pl.Connection?.Send(ServerMessages.RestOK());
            pl.Connection?.Send(ServerMessages.ConsoleMsg("Has terminado de descansar.", 0));
        }
        if (cambioHp) pl.Connection?.Send(ServerMessages.UpdateHP((short)pl.Hp));
        if (cambioMana) pl.Connection?.Send(ServerMessages.UpdateMana((short)pl.Mana));
        if (cambioSta) pl.Connection?.Send(ServerMessages.UpdateSta((short)pl.Stamina));
    }

    /// <summary>Los avisos de lo que cambió en la vuelta en la que murió: los mismos que al final de una vuelta de vivo.</summary>
    private static void AvisarAlMuerto(Player pl, bool cambioHp, bool cambioSta)
    {
        if (cambioHp) pl.Connection?.Send(ServerMessages.UpdateHP((short)pl.Hp));
        if (cambioSta) pl.Connection?.Send(ServerMessages.UpdateSta((short)pl.Stamina));
    }
}
