using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CoverAO.Server.Game;

namespace CoverAO.Server.Data;

/// <summary>
/// UNA ESCRITURA QUE SOBREVIVE A UNA BASE CAÍDA (fase5/postgres/DISENO.md, 3.5). Si al apagar la base no responde y la cola no
/// se vació, lo que quedaba se escribe en <c>data/rescate-AAAAMMDD-HHMMSS.json</c>, y el próximo arranque lo aplica ANTES de
/// cargar el mundo y de abrir los puertos. Cada uno guarda un estado entero —el personaje, la cuenta, los clanes—: aplicarlo
/// dos veces da lo mismo.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "tipo")]
[JsonDerivedType(typeof(RescateDePersonaje), "personaje")]
[JsonDerivedType(typeof(RescateDeAlta), "alta")]
[JsonDerivedType(typeof(RescateDeCuenta), "cuenta")]
[JsonDerivedType(typeof(RescateDeClanes), "clanes")]
[JsonDerivedType(typeof(RescateDeRechazos), "rechazos")]
[JsonDerivedType(typeof(RescateDeCorreo), "correo")]
[JsonDerivedType(typeof(RescateDelSuelo), "suelo")]
[JsonDerivedType(typeof(RescateDeLlaves), "llaves")]
[JsonDerivedType(typeof(RescateDelRecord), "record")]
[JsonDerivedType(typeof(RescateDeIpsBaneadas), "ips-baneadas")]
public abstract record Rescate
{
    public abstract void Aplicar(IAlmacen almacen);
}

public sealed record RescateDePersonaje(FichaDePersonaje Ficha) : Rescate
{
    public override void Aplicar(IAlmacen almacen) => almacen.GuardarPersonaje(Ficha);
}

/// <summary>Un alta: si el personaje ya entró —el alta llegó a la base antes de rendirse—, alcanza con guardarlo.</summary>
public sealed record RescateDeAlta(Account Cuenta, FichaDePersonaje Ficha) : Rescate
{
    public override void Aplicar(IAlmacen almacen)
    {
        if (almacen.ExistePersonaje(Ficha.Nombre)) almacen.GuardarPersonaje(Ficha);
        else if (!almacen.CrearPersonaje(Cuenta, Ficha))
            throw new InvalidOperationException($"el alta de {Ficha.Nombre} no entró: la cuenta {Cuenta.Name} ya tiene {AccountFile.MaxCharacters}");
    }
}

public sealed record RescateDeCuenta(Account Cuenta) : Rescate
{
    public override void Aplicar(IAlmacen almacen) => almacen.GuardarCuenta(Cuenta);
}

public sealed record RescateDeClanes(IReadOnlyList<ClanGuardado> Clanes) : Rescate
{
    public override void Aplicar(IAlmacen almacen) => almacen.GuardarClanes(Clanes);
}

public sealed record RescateDeRechazos(IReadOnlyList<KeyValuePair<string, string>> Rechazos) : Rescate
{
    public override void Aplicar(IAlmacen almacen) => almacen.GuardarRechazosDeClan(Rechazos);
}

public sealed record RescateDeCorreo(IReadOnlyDictionary<string, IReadOnlyList<MailEntry>> Casillas) : Rescate
{
    public override void Aplicar(IAlmacen almacen) => almacen.GuardarCasillas(Casillas);
}

public sealed record RescateDelSuelo(IReadOnlyList<ObjetoEnElSuelo> Suelo) : Rescate
{
    public override void Aplicar(IAlmacen almacen) => almacen.GuardarSuelo(Suelo);
}

public sealed record RescateDeLlaves(IReadOnlyList<LlaveVendida> Llaves) : Rescate
{
    public override void Aplicar(IAlmacen almacen) => almacen.GuardarLlavesVendidas(Llaves);
}

public sealed record RescateDelRecord(int Record) : Rescate
{
    public override void Aplicar(IAlmacen almacen) => almacen.GuardarRecord(Record);
}

public sealed record RescateDeIpsBaneadas(IReadOnlyList<string> Ips) : Rescate
{
    public override void Aplicar(IAlmacen almacen) => almacen.GuardarIpsBaneadas(Ips);
}

/// <summary>El archivo de rescate: lo que se escribe al apagar y lo que aplica el arranque.</summary>
public static class Rescates
{
    private const string Prefijo = "rescate-";

    private sealed record Archivo(DateTime Escrito, List<Rescate> Rescates, List<string> SinRescate);

    // Populate: la lista de personajes de la cuenta es de sólo lectura, y se llena en vez de reemplazarse.
    private static readonly JsonSerializerOptions Opciones = new()
    {
        WriteIndented = true, PreferredObjectCreationHandling = JsonObjectCreationHandling.Populate,
    };

    /// <summary>Lo que quedó sin aplicar al apagar, en orden, a <c>data/rescate-AAAAMMDD-HHMMSS.json</c>. Lo que no tiene
    /// rescate —un cambio a un desconectado, el último ingreso de una cuenta— va anotado, para que se sepa qué se perdió.
    /// Devuelve la ruta, o null si no había nada.</summary>
    public static string? Escribir(string dataRoot, IReadOnlyList<(string Que, Rescate? Rescate)> pendientes)
    {
        if (pendientes.Count == 0) return null;
        var archivo = new Archivo(DateTime.UtcNow,
            pendientes.Where(p => p.Rescate is not null).Select(p => p.Rescate!).ToList(),
            pendientes.Where(p => p.Rescate is null).Select(p => p.Que).ToList());
        Directory.CreateDirectory(dataRoot);
        string ruta = Path.Combine(dataRoot, $"{Prefijo}{DateTime.Now:yyyyMMdd-HHmmss}.json");
        EscrituraAtomica.Escribir(ruta, JsonSerializer.Serialize(archivo, Opciones), new UTF8Encoding(false));
        return ruta;
    }

    /// <summary>
    /// EL ARRANQUE APLICA LO RESCATADO, del más viejo al más nuevo y antes de cargar el mundo, y deja cada archivo renombrado
    /// a <c>*.aplicado</c>. Si algo no se puede aplicar, tira: el servidor no puede arrancar sobre un mundo al que le falta lo
    /// último. Devuelve cuántas escrituras aplicó.
    /// </summary>
    public static int AplicarLosPendientes(string dataRoot, IAlmacen almacen)
    {
        if (!Directory.Exists(dataRoot)) return 0;
        int aplicadas = 0;
        foreach (var ruta in Directory.GetFiles(dataRoot, Prefijo + "*.json").Order(StringComparer.Ordinal))
        {
            var archivo = JsonSerializer.Deserialize<Archivo>(File.ReadAllText(ruta, Encoding.UTF8), Opciones)
                ?? throw new InvalidOperationException($"{ruta} está vacío");
            foreach (var rescate in archivo.Rescates)
            {
                rescate.Aplicar(almacen);
                aplicadas++;
            }
            File.Move(ruta, ruta + ".aplicado");
            Console.WriteLine($"[base] aplicado el rescate {Path.GetFileName(ruta)}: {archivo.Rescates.Count} escritura(s)" +
                              (archivo.SinRescate.Count > 0 ? $"; se habían perdido {archivo.SinRescate.Count}: {string.Join(", ", archivo.SinRescate)}" : ""));
        }
        return aplicadas;
    }

    /// <summary>Para las pruebas: ida y vuelta por el mismo JSON del archivo.</summary>
    internal static Rescate IdaYVuelta(Rescate rescate)
        => JsonSerializer.Deserialize<Rescate>(JsonSerializer.Serialize(rescate, Opciones), Opciones)!;
}
