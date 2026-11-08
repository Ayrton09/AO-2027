using CoverAO.Server.Game;

namespace CoverAO.Server.Net;

public sealed partial class PacketDispatcher
{
    private static bool AtacanteVisible(Player p) => !p.AdminInvisible && !p.Invisible && !p.Oculto;

    private static void AvisarAtaqueAlBot(Player atacante, Player victima)
    {
        if (victima.Connection is not ClientSession { EsBot: true } bot) return;
        bot.AvisarCombateAlBot(AtacanteVisible(atacante) && World.InView(atacante.Pos, victima.Pos)
            ? atacante.CharIndex : (short)0);
    }

    // Las animaciones normales no siempre identifican el blanco. Sólo se avisa al bot que podía ver a los dos.
    private void AvisarAtaqueACriatura(Player atacante, Npc criatura)
    {
        if (_ini.Bots > 0) AvisarCombateConCriatura(_world, atacante, criatura, persigue: false);
    }

    internal static void AvisarCombateConCriatura(World mundo, Player persona, Npc criatura, bool persigue)
    {
        if (persona.Connection is ClientSession { EsBot: true } || !AtacanteVisible(persona)) return;
        foreach (var observador in mundo.Observers(criatura.Pos))
            if (observador.Connection is ClientSession { EsBot: true } bot && World.InView(persona.Pos, observador.Pos))
                bot.AvisarCombateAlBot(persona.CharIndex, criatura.CharIndex, persigue);
    }
}
