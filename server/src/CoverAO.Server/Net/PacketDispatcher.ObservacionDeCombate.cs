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
        if (_ini.Bots <= 0 || atacante.Connection is ClientSession { EsBot: true } || !AtacanteVisible(atacante)) return;
        foreach (var observador in _world.Observers(criatura.Pos))
            if (observador.Connection is ClientSession { EsBot: true } bot && World.InView(atacante.Pos, observador.Pos))
                bot.AvisarCombateAlBot(atacante.CharIndex, criatura.CharIndex);
    }
}
