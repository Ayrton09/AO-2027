using CoverAO.Server.Game;

namespace CoverAO.Server.Data;

/// <summary>
/// EL ALMACÉN, TIPADO: cuentas, personajes (<see cref="FichaDePersonaje"/>) y lo que el mundo guarda entre reinicios. Es la
/// costura donde entran los motores (fase5/postgres/DISENO.md): hoy los archivos o el SQLite viejo, los dos detrás de
/// <see cref="AlmacenDeTexto"/>; después SQLite relacional y Postgres.
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

    /// <summary>Lo que el mundo guarda con una clave: clanes, correo, suelo, llaves vendidas, el récord.</summary>
    string? LeerDelMundo(string clave);
    void GuardarEnElMundo(string clave, string texto);

    /// <summary>Copia consistente del almacén entero con el servidor andando; devuelve la ruta creada.</summary>
    string Backup(string destDir, string stem);
}
