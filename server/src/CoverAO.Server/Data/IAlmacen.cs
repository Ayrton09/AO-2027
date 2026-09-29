using CoverAO.Server.Game;

namespace CoverAO.Server.Data;

/// <summary>
/// EL ALMACÉN, TIPADO: cuentas, personajes (<see cref="FichaDePersonaje"/>) y lo que el mundo guarda entre reinicios. Es la
/// costura donde entran los motores (fase5/postgres/DISENO.md): los archivos o el SQLite viejo, los dos detrás de
/// <see cref="AlmacenDeTexto"/>, y la base SQLite con sus tablas (<see cref="AlmacenSqlite"/>); después, Postgres.
///
/// <para>Sincrónico a propósito. En qué hilo corre lo decide el que llama, y el diseño apunta a que sea UNO solo: el
/// escritor de la cola de la base. Así ninguna implementación necesita candados propios para ordenar lo que escribe.</para>
/// </summary>
public interface IAlmacen
{
    bool ExisteCuenta(string nombre);
    Account? LeerCuenta(string nombre);
    void GuardarCuenta(Account cuenta);
    /// <summary>Todos los nombres de cuenta (la gestión de baneos del panel los recorre).</summary>
    IEnumerable<string> NombresDeCuentas();

    bool ExistePersonaje(string nombre);
    FichaDePersonaje? LeerPersonaje(string nombre);
    /// <summary>Guarda a un personaje que ya existe. El alta es <see cref="CrearPersonaje"/>.</summary>
    void GuardarPersonaje(FichaDePersonaje ficha);
    /// <summary>EL ALTA: el personaje y su lugar en la cuenta, juntos. Si la cuenta no está guardada todavía, se guarda la
    /// que viene. false si la cuenta ya tiene los <see cref="AccountFile.MaxCharacters"/> personajes.</summary>
    bool CrearPersonaje(Account cuenta, FichaDePersonaje ficha);
    /// <summary>APARTA el personaje en vez de borrarlo y deja el nombre libre. false si no había nada que apartar.</summary>
    bool ApartarPersonaje(string nombre);
    /// <summary>Los personajes vivos, sin los apartados.</summary>
    IEnumerable<string> NombresDePersonajes();
    /// <summary>Lo que muestra la lista de personajes de la cuenta.</summary>
    CharacterSummary? LeerResumen(string nombre);

    /// <summary>Suma a la bóveda de un personaje desconectado y devuelve el saldo, o null si no existe. Toca SOLO el saldo:
    /// es el <c>GetVar</c>/<c>WriteVar</c> sobre <c>[STATS] BANCO</c> del original (VB6 Protocol.bas:15832).</summary>
    long? AcreditarBoveda(string nombre, long monto);

    /// <summary>Una fila de la tabla de posiciones por cada personaje vivo, con el nombre escrito como lo eligió el jugador.
    /// Un personaje ilegible se saltea: no puede voltear la tabla.</summary>
    IReadOnlyList<CharacterFile.FilaDeRanking> FilasDeRanking();

    // ── Lo que guarda el mundo. Se lee al arrancar y se escribe desde el volcado diferido del juego. ─────────────────

    IReadOnlyList<ClanGuardado> LeerClanes();
    /// <summary>Reemplaza TODOS los clanes: el que no viene, se cerró.</summary>
    void GuardarClanes(IReadOnlyList<ClanGuardado> clanes);

    /// <summary>Los motivos de rechazo que esperan a que su aspirante entre: personaje y motivo.</summary>
    IReadOnlyList<KeyValuePair<string, string>> LeerRechazosDeClan();
    void GuardarRechazosDeClan(IReadOnlyList<KeyValuePair<string, string>> rechazos);

    /// <summary>Todas las casillas de correo: destinatario y sus cartas, en orden.</summary>
    IReadOnlyDictionary<string, IReadOnlyList<MailEntry>> LeerCorreo();
    /// <summary>Reemplaza SÓLO las casillas que vienen —las que cambiaron—; una lista vacía la deja sin cartas. Las demás no
    /// se tocan.</summary>
    void GuardarCasillas(IReadOnlyDictionary<string, IReadOnlyList<MailEntry>> casillas);

    IReadOnlyList<ObjetoEnElSuelo> LeerSuelo();
    /// <summary>Reemplaza todo el suelo guardado.</summary>
    void GuardarSuelo(IReadOnlyList<ObjetoEnElSuelo> suelo);

    IReadOnlyList<LlaveVendida> LeerLlavesVendidas();
    /// <summary>Reemplaza todas las llaves vendidas.</summary>
    void GuardarLlavesVendidas(IReadOnlyList<LlaveVendida> llaves);

    /// <summary>El pico histórico de conectados (VB6 <c>recordusuarios</c>); 0 si no hay.</summary>
    int LeerRecord();
    void GuardarRecord(int record);

    /// <summary>Las IPs baneadas (el <c>Dat/BanIps.dat</c> del original), tal como se escribieron.</summary>
    IReadOnlyList<string> LeerIpsBaneadas();
    /// <summary>Reemplaza la lista entera: es corta, y cada cambio del panel o de un GM la guarda toda.</summary>
    void GuardarIpsBaneadas(IReadOnlyList<string> ips);

    /// <summary>Copia consistente del almacén entero con el servidor andando; devuelve la ruta creada.</summary>
    string Backup(string destDir, string stem);
}
