namespace CoverAO.Server.Net;

public sealed partial class PacketDispatcher
{
    // El rango vive en Server.ini y se aplica por nombre. Un nombre configurado todavía libre no puede
    // asignarse mediante el alta pública: el host crea primero el personaje en su cuenta y luego lo asciende.
    private bool AltaDeStaffReservado(ClientSession session, string nombre)
    {
        if (!_ini.EsStaff(nombre)) return false;
        Cartel(session, "Ese nombre está reservado para el staff. Elegí otro nombre.");
        return true;
    }

    // Tampoco se libera el nombre al borrar: otro dueño heredaría el rango que sigue en el ini. Antes de
    // borrar, el host debe quitarle el rango desde el panel o la configuración; el siguiente dueño será User.
    private bool BorradoDeStaffReservado(ClientSession session, string nombre)
    {
        if (!_ini.EsStaff(nombre)) return false;
        session.Send(ServerMessages.SendMsgBox(
            "Ese personaje es parte del staff: el administrador debe quitarle el rango antes de borrarlo.", 1));
        return true;
    }
}
