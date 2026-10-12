namespace CoverAO.Server.Net;

/// <summary>
/// EL JUEGO SE JUEGA CON LA APP DE ESCRITORIO (decisión del autor, 12/10). Con <c>SoloEscritorio=1</c> en el Server.ini el
/// server sólo deja jugar a la app: ella agrega <see cref="Cabecera"/> a cada pedido (el HTTP de la página y el WebSocket del
/// juego), y un navegador no puede ponerle una cabecera propia ni a la navegación ni al WebSocket. Sin la cabecera el
/// WebSocket se rechaza, la página del juego se reemplaza por la de descarga y el puerto clásico (TCP) no se abre.
///
/// <para>No es una frontera contra el que escribe su propio programa: ése copia la cabecera. Saca del medio la consola del
/// navegador, las extensiones y los scripts pegados, que es con lo que se hace trampa en un juego web. Contra el programa
/// propio está lo que el server no acepta (ver <see cref="PacketDispatcher"/>, <c>Imposible</c>).</para>
/// </summary>
public static class Escritorio
{
    /// <summary>La cabecera que agrega la app de escritorio (<c>desktop/main.js</c>), con su versión como valor.</summary>
    public const string Cabecera = "X-CoverAO-Escritorio";

    /// <summary>La página que ve el navegador en lugar del juego, en la carpeta del cliente.</summary>
    public const string PaginaDeDescarga = "descargar.html";

    /// <summary>¿El pedido viene de la app? Basta con que traiga la cabecera con algún valor.</summary>
    public static bool EsLaApp(string? valorDeLaCabecera) => !string.IsNullOrWhiteSpace(valorDeLaCabecera);

    /// <summary>Qué archivo se sirve para <paramref name="ruta"/>: la del juego (<c>index.html</c>) se cambia por la de
    /// descarga cuando el server es sólo para la app y el pedido no viene de ella. El resto se sirve igual: los gráficos,
    /// la página pública de estado y la misma de descarga no dejan jugar a nadie.</summary>
    public static string RutaAServir(string ruta, bool soloEscritorio, bool esLaApp) =>
        soloEscritorio && !esLaApp && string.Equals(ruta, "index.html", StringComparison.OrdinalIgnoreCase)
            ? PaginaDeDescarga : ruta;
}
