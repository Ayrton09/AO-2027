using System.Globalization;
using CoverAO.Server.Game;

namespace CoverAO.Server.Data;

/// <summary>
/// <see cref="IAlmacen"/> sobre los almacenes que guardan TEXTO (<see cref="IStore"/>): los archivos <c>.chr</c>/<c>.cnt</c> y
/// el SQLite viejo, que guarda el mismo INI en una columna. Convierte fichas y cuentas a ese texto con
/// <see cref="CharacterFile"/> y <see cref="AccountFile"/>, y deja dónde viven los bytes al <see cref="IStore"/>.
/// </summary>
public sealed class AlmacenDeTexto : IAlmacen
{
    private readonly IStore _store;

    public AlmacenDeTexto(IStore store) => _store = store;

    public bool ExisteCuenta(string nombre) => _store.AccountExists(nombre);

    public Account? LeerCuenta(string nombre)
    {
        var texto = _store.ReadAccount(nombre);
        return texto is null ? null : AccountFile.FromIni(IniFile.Parse(texto), nombre);
    }

    public void GuardarCuenta(Account cuenta) => _store.WriteAccount(cuenta.Name, AccountFile.ToIni(cuenta).ToText());
    public IEnumerable<string> NombresDeCuentas() => _store.AllAccountNames();

    public bool ExistePersonaje(string nombre) => _store.CharacterExists(nombre);

    public FichaDePersonaje? LeerPersonaje(string nombre)
    {
        var texto = _store.ReadCharacter(nombre);
        return texto is null ? null : CharacterFile.FichaDesdeIni(IniFile.Parse(texto), nombre);
    }

    public void GuardarPersonaje(FichaDePersonaje ficha) => _store.WriteCharacter(ficha.Nombre, CharacterFile.ToIni(ficha).ToText());

    /// <summary>El <c>.chr</c> y el nombre en la lista <c>[PJS]</c> de la cuenta GUARDADA. El tope se mira antes de escribir
    /// nada: la lista del <c>.cnt</c> tiene diez lugares, y un undécimo se caía al guardar la cuenta y dejaba un personaje
    /// que no figuraba en ninguna.</summary>
    public bool CrearPersonaje(Account cuenta, FichaDePersonaje ficha)
    {
        var guardada = LeerCuenta(cuenta.Name) ?? cuenta;
        if (!guardada.Characters.Contains(ficha.Nombre, StringComparer.OrdinalIgnoreCase))
        {
            if (guardada.Characters.Count >= AccountFile.MaxCharacters) return false;
            guardada.Characters.Add(ficha.Nombre);
        }
        GuardarPersonaje(ficha);
        GuardarCuenta(guardada);
        return true;
    }

    public bool ApartarPersonaje(string nombre) => _store.ArchivarCharacter(nombre);
    public IEnumerable<string> NombresDePersonajes() => _store.AllCharacterNames();

    public CharacterSummary? LeerResumen(string nombre)
    {
        var texto = _store.ReadCharacter(nombre);
        return texto is null ? null : CharacterFile.SummaryFromIni(IniFile.Parse(texto), nombre);
    }

    /// <summary>
    /// Por CLAVE, y no leyendo la ficha entera para volver a escribirla: reescribir el personaje completo para mover un
    /// número pisaría con los defaults cualquier clave que el archivo tenga y la ficha no —las del original que el port
    /// no usa, por ejemplo—.
    /// </summary>
    public long? AcreditarBoveda(string nombre, long monto)
    {
        string? texto = _store.ReadCharacter(nombre);
        if (texto is null) return null;
        var ini = IniFile.Parse(texto);
        long saldo = ini.GetLong("STATS", "BANCO") + monto;
        ini.Set("STATS", "BANCO", saldo.ToString(CultureInfo.InvariantCulture));
        _store.WriteCharacter(nombre, ini.ToText());
        return saldo;
    }

    public IReadOnlyList<CharacterFile.FilaDeRanking> FilasDeRanking()
    {
        var comoSeEscribe = CajaDeLosNombres();
        var filas = new List<CharacterFile.FilaDeRanking>();
        foreach (var nombre in _store.AllCharacterNames())
        {
            try
            {
                var texto = _store.ReadCharacter(nombre);
                if (texto is null) continue;
                var lindo = comoSeEscribe.TryGetValue(nombre, out var n) ? n : nombre;
                filas.Add(CharacterFile.RankingFromIni(IniFile.Parse(texto), lindo));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[publico] no se pudo leer '{nombre}' para la tabla: {ex.Message}");
            }
        }
        return filas;
    }

    /// <summary>CÓMO SE ESCRIBE CADA NOMBRE, que el <c>.chr</c> no guarda.
    ///
    /// <para>El archivo de un personaje se llama con el nombre en MAYÚSCULAS —el original hace lo mismo,
    /// <c>CharPath &amp; UCase$(.Name) &amp; ".chr"</c> (FileIO.bas:1597)— y adentro no hay ninguna clave con
    /// el nombre. O sea que barrer el almacén devuelve "RUGURA", y la lista de conectados —que sale del mundo
    /// vivo— dice "Rugura": la misma persona con dos grafías en la misma pantalla.</para>
    ///
    /// <para>La caja que el jugador escribió vive en la lista <c>[PJS]</c> de su cuenta, que es de donde la
    /// saca el propio juego al mostrar los personajes. Un personaje que no figure en ninguna cuenta se queda
    /// con el nombre del archivo, que es lo único que hay de él.</para></summary>
    private Dictionary<string, string> CajaDeLosNombres()
    {
        var mapa = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var cuenta in _store.AllAccountNames())
        {
            try
            {
                var texto = _store.ReadAccount(cuenta);
                if (texto is null) continue;
                foreach (var pj in AccountFile.FromIni(IniFile.Parse(texto), cuenta).Characters)
                    mapa[pj] = pj;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[publico] no se pudo leer la cuenta '{cuenta}': {ex.Message}");
            }
        }
        return mapa;
    }

    public string? LeerDelMundo(string clave) => _store.ReadMisc(clave);
    public void GuardarEnElMundo(string clave, string texto) => _store.WriteMisc(clave, texto);
    public string Backup(string destDir, string stem) => _store.Backup(destDir, stem);
}
