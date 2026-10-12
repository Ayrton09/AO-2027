using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CoverAO.Server.Game;

namespace CoverAO.Server.Data;

/// <summary>
/// UNA ESCRITURA QUE SOBREVIVE A UNA BASE CAÍDA (fase5/postgres/DISENO.md, 3.5). Si al apagar la base no responde y la cola no
/// se vació, lo que quedaba se escribe en <c>data/rescate-AAAAMMDD-HHMMSS.json</c>, y el próximo arranque lo aplica ANTES de
/// cargar el mundo y de abrir los puertos. Cada uno guarda un estado entero —el personaje, la cuenta, los clanes—: aplicarlo
/// dos veces da lo mismo. Menos la suma a una bóveda, que se cuida aparte (<see cref="RescateDeAcreditacion"/>).
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
[JsonDerivedType(typeof(RescateDeSubastas), "subastas")]
[JsonDerivedType(typeof(RescateDelRecord), "record")]
[JsonDerivedType(typeof(RescateDeIpsBaneadas), "ips-baneadas")]
[JsonDerivedType(typeof(RescateDeAcreditacion), "acreditacion")]
public abstract record Rescate
{
    public abstract void Aplicar(IAlmacen almacen);

    /// <summary>Qué es, para el log: un método y no una propiedad, que iría al JSON.</summary>
    public virtual string Describir() => GetType().Name;
}

public sealed record RescateDePersonaje(FichaDePersonaje Ficha) : Rescate
{
    public override void Aplicar(IAlmacen almacen) => almacen.GuardarPersonaje(Ficha);
    public override string Describir() => $"guardar a {Ficha.Nombre}";
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

    public override string Describir() => $"alta de {Ficha.Nombre}";
}

public sealed record RescateDeCuenta(Account Cuenta) : Rescate
{
    public override void Aplicar(IAlmacen almacen) => almacen.GuardarCuenta(Cuenta);
    public override string Describir() => $"guardar la cuenta {Cuenta.Name}";
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

public sealed record RescateDeSubastas(SubastasGuardadas Subastas) : Rescate
{
    public override void Aplicar(IAlmacen almacen) => almacen.GuardarSubastas(Subastas);
}

public sealed record RescateDelRecord(int Record) : Rescate
{
    public override void Aplicar(IAlmacen almacen) => almacen.GuardarRecord(Record);
}

public sealed record RescateDeIpsBaneadas(IReadOnlyList<string> Ips) : Rescate
{
    public override void Aplicar(IAlmacen almacen) => almacen.GuardarIpsBaneadas(Ips);
}

/// <summary>
/// Una suma a la bóveda de un desconectado: la transferencia de oro (<see cref="Persistence.AcreditarBovedaAsync"/>). Es la
/// única que NO guarda un estado entero, así que aplicarla dos veces duplicaría el oro. Con <see cref="Antes"/>, el saldo que
/// tenía antes de la suma: si ya es <c>Antes + Monto</c>, entró y no se suma de nuevo. Sin él, la suma no llegó a empezar, y
/// <see cref="Rescates.AplicarLosPendientes"/> anota el saldo en el archivo antes de sumar.
/// </summary>
public sealed record RescateDeAcreditacion(string Nombre, long Monto, long? Antes) : Rescate
{
    public override void Aplicar(IAlmacen almacen)
    {
        long saldo = almacen.AcreditarBoveda(Nombre, 0) ?? throw new InvalidOperationException($"{Nombre} no existe");
        if (Antes is { } antes)
        {
            if (saldo == antes + Monto) return;   // ya había entrado
            if (saldo != antes)
                throw new InvalidOperationException($"la bóveda de {Nombre} tiene {saldo}, y no {antes} ni {antes + Monto}: no se sabe si entró");
        }
        almacen.AcreditarBoveda(Nombre, Monto);
    }

    /// <summary>La misma, con el saldo de ahora como el de antes (si el personaje existe).</summary>
    public RescateDeAcreditacion ConElSaldoDe(IAlmacen almacen) => this with { Antes = almacen.AcreditarBoveda(Nombre, 0) };

    public override string Describir() => $"acreditar {Monto} a la bóveda de {Nombre}";
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
    /// a <c>*.aplicado</c>. Si la base no responde, tira: el servidor no puede arrancar sobre un mundo al que le falta lo
    /// último, y el archivo queda para el próximo intento. Lo que no se puede aplicar por OTRA cosa —un alta a una cuenta que
    /// ya se llenó, como hubiera pasado con la base andando; un personaje que ya no está— queda en el log y se sigue: si
    /// tirara, el servidor no arrancaría nunca más. Devuelve cuántas escrituras aplicó.
    /// </summary>
    public static int AplicarLosPendientes(string dataRoot, IAlmacen almacen)
    {
        if (!Directory.Exists(dataRoot)) return 0;
        int aplicadas = 0;
        foreach (var ruta in Directory.GetFiles(dataRoot, Prefijo + "*.json").Order(StringComparer.Ordinal))
        {
            var archivo = JsonSerializer.Deserialize<Archivo>(File.ReadAllText(ruta, Encoding.UTF8), Opciones)
                ?? throw new InvalidOperationException($"{ruta} está vacío");
            var sinAplicar = new List<string>();
            for (int i = 0; i < archivo.Rescates.Count; i++)
            {
                var rescate = archivo.Rescates[i];
                try
                {
                    // UNA SUMA ANOTA EL SALDO DE ANTES EN EL ARCHIVO, ANTES DE SUMAR: si la base se cae en el medio y el archivo
                    // se vuelve a aplicar en el próximo arranque, la que ya entró no se suma otra vez.
                    if (rescate is RescateDeAcreditacion { Antes: null } suma)
                    {
                        archivo.Rescates[i] = rescate = suma.ConElSaldoDe(almacen);
                        EscrituraAtomica.Escribir(ruta, JsonSerializer.Serialize(archivo, Opciones), new UTF8Encoding(false));
                    }
                    rescate.Aplicar(almacen);
                    aplicadas++;
                }
                catch (Exception ex) when (!ColaDeLaBase.EsQueLaBaseNoResponde(ex))
                {
                    sinAplicar.Add($"{rescate.Describir()} ({ex.Message})");
                }
            }
            File.Move(ruta, ruta + ".aplicado");
            Console.WriteLine($"[base] aplicado el rescate {Path.GetFileName(ruta)}: {archivo.Rescates.Count - sinAplicar.Count} escritura(s)" +
                              (archivo.SinRescate.Count > 0 ? $"; se habían perdido {archivo.SinRescate.Count}: {string.Join(", ", archivo.SinRescate)}" : ""));
            if (sinAplicar.Count > 0)
                Console.Error.WriteLine($"[base] del rescate {Path.GetFileName(ruta)} NO se pudieron aplicar {sinAplicar.Count}: " +
                                        string.Join("; ", sinAplicar));
        }
        return aplicadas;
    }

    /// <summary>
    /// LOS RESCATES NO VAN ENCIMA DE OTRA BASE. Restaurar un backup o importar es volver a otro punto, y lo rescatado se escribió
    /// sobre la base de antes: aplicado encima mezclaría personajes de los dos momentos —un ítem que pasó de mano estaría en
    /// las dos, o en ninguna—. Así que se apartan, renombrados a <c>*.sin-aplicar</c>, y se dice. Devuelve los nombres.
    /// </summary>
    public static IReadOnlyList<string> ApartarLosPendientes(string dataRoot)
    {
        if (!Directory.Exists(dataRoot)) return Array.Empty<string>();
        var apartados = new List<string>();
        foreach (var ruta in Directory.GetFiles(dataRoot, Prefijo + "*.json").Order(StringComparer.Ordinal))
        {
            File.Move(ruta, ruta + ".sin-aplicar");
            apartados.Add(Path.GetFileName(ruta) + ".sin-aplicar");
        }
        return apartados;
    }

    /// <summary>Para las pruebas: ida y vuelta por el mismo JSON del archivo.</summary>
    internal static Rescate IdaYVuelta(Rescate rescate)
        => JsonSerializer.Deserialize<Rescate>(JsonSerializer.Serialize(rescate, Opciones), Opciones)!;
}
