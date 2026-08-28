using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;

namespace CoverAO.Server.Net;

/// <summary>
/// EL SUBSISTEMA DE FAMILIARES. <b>Es un agregado nuestro, no un port</b> — el original tiene la data (las 9
/// fichas rotuladas <c>'FAMILIAR</c>, los hechizos 59 y 79, el pergamino 1168, la Veterinaria, los mensajes de
/// locale y hasta los controles del panel en el cliente) y le arrancó el código a propósito. Ver
/// <see cref="Familiar"/> y <c>fase5/familiares-plan.md</c>.
///
/// <para>Vive en su propio archivo porque es una pieza entera y separable, y porque conviene que se vea de
/// lejos qué parte del server NO puede compararse contra el oráculo VB6.</para>
/// </summary>
public sealed partial class PacketDispatcher
{
    /// <summary>El hechizo que llama y retira al familiar: "Llamado al familiar". Va por ÍNDICE y no por el
    /// <c>Tipo = 6</c> (uFamiliar) del <c>.dat</c> porque ese tipo lo comparte con el #61, "Señal a tu
    /// mascota", que es otra cosa — gatear por tipo haría que el de las mascotas invocara un familiar. Es el
    /// mismo criterio con el que el original cablea el portal de Lum por número de hechizo.</summary>
    public const short HechizoLlamadoAlFamiliar = 59;

    /// <summary>Sale gratis: el <c>.dat</c> le pone <c>MinSkill</c>, <c>ManaRequerido</c> y <c>StaRequerido</c>
    /// en 0, lo que además es la única forma de que le sirva al Cazador, que no tiene maná.</summary>

    // -------------------------------------------------------------------------------------------------------
    // ELEGIR
    // -------------------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>ElegirFamiliar</c> (id 115): el personaje elige QUÉ familiar lleva y cómo se llama. Se puede una sola
    /// vez — después es el que es, igual que la raza.
    ///
    /// <para>Va como paquete propio y no como campos nuevos de <c>LoginNewChar</c> para no sacar de la paridad
    /// de bytes al paquete de creación, que sí es del original. El cliente lo manda apenas entra al mundo con
    /// un personaje recién creado.</para>
    /// </summary>
    private void HandleElegirFamiliar(ClientSession session, ByteQueueReader r)
    {
        byte tipo = r.ReadByte();
        string nombre = r.ReadAsciiString();
        _loop.Enqueue(() =>
        {
            var p = session.Player;
            if (p is null) return;
            var fam = p.Familiar;

            if (fam.Existe) { session.Send(ServerMessages.ConsoleMsg("Ya tenes un familiar.", FuenteInfo)); return; }
            if (Familiar.TiposDeLaClase(p.Class).Length == 0)
            { session.Send(ServerMessages.ConsoleMsg("Tu clase no lleva familiar.", FuenteInfo)); return; }
            if (!Familiar.ClasePuedeLlevar(p.Class, tipo))
            { session.Send(ServerMessages.ConsoleMsg("Ese familiar no es de tu clase.", FuenteInfo)); return; }

            // El nombre pasa por el MISMO filtro que el de un personaje (sólo letras y espacios): un familiar
            // con dígitos se confundiría con una criatura del mundo en la barra de nombres, que es exactamente
            // el motivo por el que el original le prohíbe los dígitos a los personajes (TCP.bas:181).
            nombre = nombre.Trim();
            // El techo sale de `Naming`, que es donde vive: acá decía 20 y el helper corta en 18, así que el
            // `or > 20` era inerte y el mensaje le mentía al jugador sobre lo que podía escribir.
            if (nombre.Length < 2 || !Naming.IsValidCharName(nombre))
            { session.Send(ServerMessages.ConsoleMsg($"Nombre de familiar invalido (solo letras y espacios, 2 a {Naming.MaxCharNameLength}).", FuenteInfo)); return; }

            var ficha = _data.Npc(Familiar.FichaDe(tipo));
            if (ficha is null) { session.Send(ServerMessages.ConsoleMsg("Ese familiar no existe.", FuenteInfo)); return; }

            fam.Nacer(tipo, nombre, ficha.MaxHp, ficha.MinHit, ficha.MaxHit);
            EnseniarLlamadoAlFamiliar(session, p);
            session.Send(ServerMessages.ConsoleMsg($"{nombre} es tu familiar. Usa 'Llamado al familiar' para traerlo.", FuenteInfo));
            EnviarFichaDelFamiliar(session, p);
        });
    }

    /// <summary>Le pone el hechizo #59 en la primera ranura libre del libro. Sin esto el familiar sería
    /// inalcanzable: ningún objeto de <c>obj.dat</c> enseña ese hechizo — en el original el subsistema estaba
    /// apagado, así que nadie se ocupó de repartirlo.</summary>
    private void EnseniarLlamadoAlFamiliar(ClientSession session, Player p)
    {
        for (int i = 1; i <= GameLimits.MaxUserSpells; i++)
            if (p.Spells[i] == HechizoLlamadoAlFamiliar) return;   // ya lo tiene
        for (int i = 1; i <= GameLimits.MaxUserSpells; i++)
        {
            if (p.Spells[i] != 0) continue;
            p.Spells[i] = HechizoLlamadoAlFamiliar;
            session.Send(ServerMessages.ChangeSpellSlot((byte)i, p.Spells[i]));
            return;
        }
    }

    /// <summary>La ficha, a su dueño. Se manda cada vez que algo de lo que el panel muestra pudo cambiar.</summary>
    private static void EnviarFichaDelFamiliar(ClientSession? session, Player p) =>
        session?.Send(ServerMessages.FamiliarInfo(p.Familiar));

    private static void EnviarFichaDelFamiliar(Player p) =>
        p.Connection?.Send(ServerMessages.FamiliarInfo(p.Familiar));

    /// <summary>Engancha el aviso de <see cref="World.CambioElFamiliar"/>. Se llama una vez, al construir el
    /// despachador.</summary>
    private void EngancharAvisosDelFamiliar() => _world.CambioElFamiliar = EnviarFichaDelFamiliar;

    // -------------------------------------------------------------------------------------------------------
    // LLAMAR Y RETIRAR
    // -------------------------------------------------------------------------------------------------------

    /// <summary>
    /// El hechizo #59: si el familiar no está, lo trae; si está, lo retira. Los gates, en el orden del módulo
    /// de referencia: zona segura, muerto, y no tener familiar.
    /// </summary>
    private void LlamarORetirarFamiliar(ClientSession session, Player p, SpellInfo spell)
    {
        var fam = p.Familiar;
        if (!fam.Existe)
        { session.Send(ServerMessages.ConsoleMsg("No tenes ningun familiar.", FuenteInfo)); return; }

        // Retirar SIEMPRE se puede, aunque estés en ciudad: si no, al que entra a una zona segura con el
        // familiar afuera no le quedaría forma de guardarlo.
        if (fam.Invocado) { RetirarFamiliar(p, porGusto: true); EnviarFichaDelFamiliar(session, p); return; }

        if (fam.Muerto)
        { session.Send(ServerMessages.ConsoleMsg($"{fam.Nombre} esta muerto. Llevalo a una Veterinaria para que lo revivan.", FuenteInfo)); return; }
        if (!_data.IsPkMap(p.Pos.Map))
        { session.Send(ServerMessages.ConsoleMsg("No podes invocar a tu familiar en zonas seguras.", FuenteInfo)); return; }

        if (!TraerFamiliar(p))
        { session.Send(ServerMessages.ConsoleMsg($"No hay lugar aca para {fam.Nombre}.", FuenteInfo)); return; }

        // SIN `SpellCasted`, y no es un olvido: el 59 cuesta 0 de maná y 0 de stamina (Hechizos.dat), así que
        // lo único que esa llamada hacía de verdad era subir la skill de Magia y pagar su experiencia — o sea
        // que el bucle llamar/retirar era una canilla de skill y exp gratis. Es exactamente la canilla que el
        // comentario del `else` de los hechizos sin efecto dice haber cerrado, entrando por otra puerta.
        EmitSpellFx(p, p.Pos, p.CharIndex, spell);
        EnviarFichaDelFamiliar(session, p);
    }

    /// <summary>Pone al familiar en el mundo al lado de su dueño. Devuelve false si no había lugar.</summary>
    private bool TraerFamiliar(Player amo)
    {
        var fam = amo.Familiar;
        var plantilla = _data.Npc(Familiar.FichaDe(fam.Tipo));
        if (plantilla is null) return false;
        WorldPos? libre = null;
        foreach (var pos in FreeTilesAround(amo.Pos, 1)) { libre = pos; break; }
        if (libre is null) return false;

        // La ficha de NPCs.dat da el aspecto y la base; las barras salen del FAMILIAR, que es lo que sube de
        // nivel. Se hace con un `with` sobre el record en vez de guardar los valores en el Npc porque la
        // criatura los lee siempre de su plantilla — el mismo camino que usa el fixture para su agresor de 300.
        var conNivel = plantilla with
        {
            MaxHp = (int)fam.VidaMaxima,
            MinHit = (int)fam.GolpeMinimo,
            MaxHit = (int)fam.GolpeMaximo,
            GiveExp = 0,     // matar al familiar de otro no puede pagar experiencia
            GiveGld = 0,
            // Sus habilidades mágicas viajan en la ficha como los hechizos de cualquier criatura que sabe
            // magia: así las tira por el MISMO camino que ya existe, con su daño, su resistencia y su efecto.
            Spells = fam.HechizosQueTira(),
        };
        var criatura = new Npc
        {
            CharIndex = _world.AllocateCharIndex(),
            Template = conNivel,
            Hp = (int)System.Math.Max(1, fam.Vida),
            Pos = libre.Value,
            OwnerCharIndex = amo.CharIndex,
            Hostile = false,
            OriginallyHostile = false,
            Movement = GameLimits.TipoAiSigueAmo,
        };
        _world.AddNpc(criatura);
        _world.BroadcastToObservers(criatura.Pos, ServerMessages.CharacterCreate(criatura));
        fam.CharIndexEnElMundo = criatura.CharIndex;
        return true;
    }

    /// <summary>Lo saca del mundo conservándole la vida. <paramref name="porGusto"/> distingue el retiro
    /// deliberado del que hace el cambio de mapa, para avisar sólo cuando corresponde.</summary>
    private void RetirarFamiliar(Player amo, bool porGusto)
    {
        var fam = amo.Familiar;
        if (!fam.Invocado) return;
        var criatura = _world.NpcByIndex(fam.CharIndexEnElMundo);
        if (criatura is not null)
        {
            fam.Vida = System.Math.Max(1, criatura.Hp);   // se lleva la vida con la que estaba peleando
            var donde = criatura.Pos;
            _world.RemoveNpc(criatura.CharIndex);          // esto ya le pone CharIndexEnElMundo en 0
            _world.BroadcastToObservers(donde, ServerMessages.CharacterRemove(criatura.CharIndex, faded: true));
        }
        fam.CharIndexEnElMundo = 0;
        if (porGusto) amo.Connection?.Send(ServerMessages.ConsoleMsg($"{fam.Nombre} se retira.", FuenteInfo));
    }

    /// <summary>
    /// EL FAMILIAR VIAJA CON SU DUEÑO, con la misma regla que las mascotas (<c>WarpMascotas</c>): se lo saca
    /// del mapa que se deja y se lo vuelve a poner al lado en el destino, salvo que el destino sea zona segura
    /// — ahí espera afuera y vuelve solo cuando su dueño pisa campo abierto.
    ///
    /// <para>Va aparte del barrido de mascotas porque el familiar NO ocupa uno de los tres lugares del cupo:
    /// no se doma ni se invoca contra ese tope, es del personaje desde que lo eligió.</para>
    /// </summary>
    private void WarpFamiliar(Player amo)
    {
        var fam = amo.Familiar;
        if (!fam.Existe || !fam.Invocado) return;
        RetirarFamiliar(amo, porGusto: false);
        if (!_data.IsPkMap(amo.Pos.Map))
        {
            amo.Connection?.Send(ServerMessages.ConsoleMsg(
                $"No se permiten familiares en zona segura. {fam.Nombre} te espera afuera.", FuenteInfo));
            EnviarFichaDelFamiliar(amo);
            return;
        }
        TraerFamiliar(amo);
        EnviarFichaDelFamiliar(amo);
    }

    // -------------------------------------------------------------------------------------------------------
    // CRECER, MORIR Y VOLVER
    // -------------------------------------------------------------------------------------------------------

    /// <summary>
    /// El familiar gana la MISMA experiencia que su dueño mientras está afuera y vivo.
    ///
    /// <para>Acá hubo que decidir y conviene que quede escrito: el módulo de referencia nunca dice de dónde
    /// sale la experiencia del familiar —su <c>CheckFamilyLevel</c> no lo llama nadie—, así que no hay nada
    /// que portar. Se eligió repartir la del dueño, y no que el familiar cobre por lo que mata él, por una
    /// razón de diseño: pagarle sólo por sus muertes premia dejarlo pelear solo y castiga usarlo de apoyo, que
    /// es justo para lo que sirve. Cobra sólo si está en el mundo: un familiar guardado no sube.</para>
    /// </summary>
    private void RepartirExperienciaAlFamiliar(ClientSession? session, Player p, long exp)
    {
        var fam = p.Familiar;
        if (!fam.Existe || !fam.Invocado || fam.Muerto || exp <= 0) return;
        var avisos = fam.GanarExperiencia(exp);
        if (avisos.Count == 0) return;

        // Subió de nivel: se le refrescan las barras a la criatura que ya está puesta, para que el aumento se
        // note en la pelea que está dando y no recién la próxima vez que la llamen.
        var criatura = _world.NpcByIndex(fam.CharIndexEnElMundo);
        if (criatura is not null)
        {
            criatura.Template = criatura.Template with
            {
                MaxHp = (int)fam.VidaMaxima, MinHit = (int)fam.GolpeMinimo, MaxHit = (int)fam.GolpeMaximo,
                Spells = fam.HechizosQueTira(),   // la habilidad que acaba de aprender entra en la pelea que está dando
            };
            criatura.Hp = (int)fam.VidaMaxima;
        }
        foreach (var aviso in avisos) session?.Send(ServerMessages.ConsoleMsg(aviso, FuenteInfo));
        EnviarFichaDelFamiliar(session, p);
    }

    /// <summary>
    /// LE SACA EL ARMA a quien acaba de recibir un mordisco del familiar: es la habilidad "Desarma con los
    /// golpes" que aprenden el Ent y el Oso al nivel 30.
    ///
    /// <para>Vive acá y no en el sistema de criaturas porque desequipar toca el inventario, las animaciones y
    /// tres paquetes, y todo eso ya está escrito para el desarme del jugador — el del Ladrón, el Bardo y el
    /// Gladiador. La tirada la hizo el que llama; esto sólo aplica.</para>
    /// </summary>
    private void DesarmarPorElFamiliar(Player victima)
    {
        int slot = EquippedSlotOfCategory(victima, 1);   // el arma equipada
        if (slot <= 0) return;
        var inv = victima.Inventory[slot];
        inv.Equipped = false;
        victima.Inventory[slot] = inv;
        var arma = _data.Obj(inv.ObjIndex);
        if (arma is not null) ClearEquipAnim(victima, arma);
        victima.Connection?.Send(ServerMessages.ChangeInventorySlot((byte)slot, inv.ObjIndex, inv.Amount, false, 0f, 1));
        if (arma is not null) BroadcastEquipChange(victima, arma, equipped: false);
        victima.Connection?.Send(ServerMessages.ConsoleMsg("¡Te han desarmado!", FuenteCombate));
    }

    /// <summary>
    /// LA VETERINARIA. Revive al familiar muerto y le cura las heridas al que está entero — que es exactamente
    /// lo que su propia descripción en <c>NPCs.dat</c> ofrece: "Si tu familiar o mascota se encuentra en mal
    /// estado, acércamelo". El rol estaba declarado en el original (<c>eNPCType.Veterinarias = 11</c>) y no lo
    /// usaba nadie; el NPC igual quedó repartido por el mundo, con 27 apariciones.
    ///
    /// <para>La distancia se mide como el sacerdote revividor, que es el NPC más parecido: MANHATTAN y hasta
    /// 10 (VB6 <c>Distancia</c>, Matematicas.bas:43).</para>
    /// </summary>
    private void AtenderVeterinaria(ClientSession session, Player p, Npc vet)
    {
        if (DistanciaVB6(p.Pos, vet.Pos) > 10) { session.Send(ServerMessages.LocaleMsg(8)); return; }
        var fam = p.Familiar;
        if (!fam.Existe)
        { session.Send(ServerMessages.ConsoleMsg("No tenes ningun familiar que atender.", FuenteInfo)); return; }

        if (fam.Muerto)
        {
            fam.Muerto = false;
            fam.Vida = fam.VidaMaxima;
            session.Send(ServerMessages.ConsoleMsg($"¡{fam.Nombre} ha revivido!", FuenteInfo));
            EnviarFichaDelFamiliar(session, p);
            return;
        }
        if (fam.Vida < fam.VidaMaxima)
        {
            fam.Vida = fam.VidaMaxima;
            // Si está puesto en el mundo se le cura también la criatura, no sólo la ficha: si no, el jugador
            // ve la barra llena y la que está peleando sigue herida.
            var criatura = _world.NpcByIndex(fam.CharIndexEnElMundo);
            if (criatura is not null) criatura.Hp = (int)fam.VidaMaxima;
            session.Send(ServerMessages.ConsoleMsg($"{fam.Nombre} esta como nuevo.", FuenteInfo));
            EnviarFichaDelFamiliar(session, p);
            return;
        }
        session.Send(ServerMessages.ConsoleMsg($"{fam.Nombre} esta perfectamente.", FuenteInfo));
    }

    /// <summary>
    /// El #79, "Resucitar familiar o mascota", con efecto.
    ///
    /// <para>El hechizo en sí se porta FIEL y sin efecto —el original lo da por lanzado y nada más, y así está
    /// en la rama de estados—; esto es lo que le agregamos encima. Por eso va acá y no allá: si el que lo tira
    /// no tiene familiar muerto, no cambia ni un paquete respecto de lo que emite el original.</para>
    /// </summary>
    private void RevivirFamiliarSiHay(ClientSession session, Player p)
    {
        var fam = p.Familiar;
        if (!fam.Existe || !fam.Muerto) return;
        fam.Muerto = false;
        fam.Vida = fam.VidaMaxima;
        session.Send(ServerMessages.ConsoleMsg($"¡{fam.Nombre} ha revivido!", FuenteInfo));
        EnviarFichaDelFamiliar(session, p);
    }
}
