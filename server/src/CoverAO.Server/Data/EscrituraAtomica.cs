namespace CoverAO.Server.Data;

/// <summary>
/// ESCRIBIR UN ARCHIVO SIN DEJARLO NUNCA A MEDIAS.
///
/// <para><c>File.WriteAllText</c> TRUNCA y después escribe. Entre las dos mitades hay un instante en que el
/// archivo, en el disco, tiene CERO bytes — y si el proceso muere ahí (corte de luz, OOM-killer, un
/// <c>docker stop</c> que vence su plazo, un <c>taskkill /F</c>) eso es lo que queda. No es teórico: mirándolo
/// con <c>os.stat</c> mientras el servidor lo reescribía, un <c>Server.ini</c> de 106 bytes se vio en cero
/// <b>646 veces</b> sobre 725.341 muestras y 974 reescrituras.</para>
///
/// <para>La receta es la de siempre: escribir a un temporal y renombrar encima. El rename sobre un archivo
/// existente es atómico en NTFS y en ext4 — o queda el nuevo entero, o queda el viejo entero.</para>
///
/// <para>Vive aparte de <see cref="FileStore"/> porque el almacén no es el único que escribe cosas que duelen
/// si quedan cortadas: el <c>Server.ini</c> —que decide QUIÉN ES STAFF, los puertos y las reglas de la casa—
/// lo reescriben el <c>/SETINIVAR</c> y el panel al cambiarle el rango a alguien, y hasta ahora lo hacían
/// truncando. Un corte ahí devolvía el servidor sin un solo GM.</para>
///
/// <para>El candado NO está acá a propósito: <see cref="FileStore"/> tiene el suyo, que además excluye al
/// backup mientras copia, y meter otro global cambiaría esa exclusión sin que se note. Cada llamador decide
/// con qué se excluye; lo que esta clase garantiza es la forma de la escritura, no el orden entre dos.</para>
/// </summary>
internal static class EscrituraAtomica
{
    /// <summary>Escribe el texto en el destino pasando por un temporal, y BAJA EL TEMPORAL AL DISCO antes de
    /// renombrar.
    ///
    /// <para>El flush no es prolijidad: sin él lo de arriba protege contra que MATEN EL PROCESO —los datos que
    /// el sistema ya recibió no se pierden porque el proceso muera, y está medido: 20 cortes de golpe, 3 de
    /// ellos adentro de la ventana, 0 archivos a medias— pero NO contra un corte de LUZ. NTFS y ext4 llevan
    /// diario de los METADATOS, no de los datos: el rename puede quedar asentado y el contenido todavía en
    /// caché, y al volver la luz el archivo existe con el tamaño nuevo y basura o ceros adentro. O sea que sin
    /// esta línea, el comentario de arriba prometía algo que no daba.</para>
    ///
    /// <para>El costo está MEDIDO en este equipo, con un archivo del tamaño de un <c>.chr</c>: 0,315 ms de
    /// mediana sin flush contra 1,090 ms con flush, o sea <b>+0,8 ms por archivo</b>. Se puede pagar porque el
    /// camino que guarda a MUCHOS a la vez —el autosave— corre fuera del hilo del game loop desde que se
    /// separaron las dos mitades; en el hilo sagrado sólo quedan guardados de a UN archivo (la desconexión,
    /// las sanciones de GM) y el <c>/GRABAR</c>, que es una acción deliberada de un GM y que el original
    /// también hace con el mundo pausado.</para></summary>
    public static void Escribir(string destino, string texto, System.Text.Encoding enc)
    {
        string tmp = destino + ".tmp";
        // StreamWriter sobre el FileStream, y no File.WriteAllText, SÓLO para poder pedir el flush con el
        // archivo todavía abierto. Los bytes que salen son los mismos —incluido el preámbulo de la
        // codificación, que es lo que WriteAllText escribe cuando se le pasa una— y hay un test que lo fija.
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            using (var w = new StreamWriter(fs, enc, leaveOpen: true)) w.Write(texto);
            fs.Flush(flushToDisk: true);
        }
        Renombrar(tmp, destino);
    }

    /// <summary>El rename es lo que vuelve atómica la escritura, y en Windows es también lo único que un
    /// tercero puede bloquear: <c>File.Move</c> con <c>overwrite</c> se cae con
    /// <c>UnauthorizedAccessException</c> si el destino está ABIERTO por alguien, y no hay <c>FileShare</c>
    /// que lo evite — las cinco combinaciones están probadas, ni siquiera <c>FileShare.Delete</c>.
    ///
    /// <para>Ese "alguien" no es un caso de laboratorio: es el antivirus escaneando al vuelo, el agente de
    /// backup del hosting, o OneDrive sincronizando la carpeta del usuario — y el <c>data/</c> vive al lado
    /// del ejecutable, que en Windows suele estar justo ahí. MEDIDO contra el servidor andando: con un solo
    /// proceso pidiéndole el TAMAÑO a los diez <c>.chr</c> del fixture —ni siquiera leyéndolos— se perdieron
    /// <b>181 de 10.620</b> escrituras (1,7%); con ese proceso apagado, 0 de 12.430; con el reintento puesto y
    /// el mirón encendido, 0 de 12.450. Lo que se pierde es el guardado del jugador, en silencio.</para>
    ///
    /// <para>El presupuesto de espera es CHICO a propósito: 1+2+4+8+16 ms, 31 ms en el peor caso. Esto corre
    /// también en el hilo del game loop —el <c>/GRABAR</c>, el guardado al desconectar— y ahí una espera larga
    /// congela el mundo entero. Un handle de escaneo se suelta en milisegundos, así que reintentar poco
    /// alcanza; esperar mucho cambiaría una pérdida rara por un tirón que ven todos.</para></summary>
    public static void Renombrar(string tmp, string destino)
    {
        for (int intento = 0; ; intento++)
        {
            try
            {
                File.Move(tmp, destino, overwrite: true);
                return;
            }
            catch (Exception ex) when (intento < 4 && ex is UnauthorizedAccessException or IOException)
            {
                Thread.Sleep(1 << intento);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                // Se avisa NOMBRANDO el archivo. Antes esto salía como un "[loop] action error: Access to the
                // path is denied." pelado, que no dice ni qué se perdió ni que se perdió algo.
                throw new IOException(
                    "no se pudo guardar '" + Path.GetFileName(destino) + "': alguien tiene el archivo abierto " +
                    "(antivirus, backup o sincronización de la carpeta). Se perdió este guardado.", ex);
            }
        }
    }
}
