using CoverAO.Server.Data;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// DOS HILOS ESCRIBIENDO LA MISMA CUENTA.
///
/// <para><c>FileStore</c> escribe atómico —a un temporal y después rename— pero el temporal tenía un nombre
/// FIJO (<c>destino + ".tmp"</c>) y ningún candado. Con dos escrituras simultáneas del mismo archivo, las dos
/// abren el MISMO temporal: la segunda lo trunca mientras la primera lo está escribiendo, y después las dos
/// intentan renombrarlo. La que llega tarde encuentra que ya no está y tira <c>FileNotFoundException</c>.</para>
///
/// <para>No es teórico ni hace falta ser administrador. Las escrituras de cuenta salen de TRES hilos
/// distintos: el game loop (el rehash del login, <c>/DONADOR</c>), el pool de autenticación
/// (<c>CreateAccount</c> y el cambio de contraseña, que corren en <c>Task.Run</c> para no congelar el mundo) y
/// el hilo HTTP del panel de administración (banear, desbanear a todos). Y el pool admite una operación en
/// vuelo <b>por conexión</b>, así que veinte conexiones pidiendo cambiar la contraseña de la misma cuenta son
/// veinte escrituras simultáneas del mismo <c>.cnt</c>.</para>
///
/// <para>Lo que se pierde no es sólo la escritura: el que la pidió recibe "contraseña restablecida" mientras
/// la excepción se traga el cambio en un log. <c>FileStore</c> es además el almacén POR DEFECTO —SQLite es
/// opt-in por variable de entorno, y ése sí serializa con un lock desde siempre.</para>
/// </summary>
public class EscrituraConcurrenteTests
{
    [Fact]
    public void VeinteEscriturasSimultaneasDeLaMismaCuentaNoSePisan()
    {
        var raiz = Path.Combine(Path.GetTempPath(), "coverao-concurrencia-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new FileStore(raiz);
            const int hilos = 20;
            var contenidos = Enumerable.Range(0, hilos).Select(i => $"[PEPE]\nPassword=clave{i}\n").ToArray();
            var fallas = new System.Collections.Concurrent.ConcurrentBag<Exception>();

            Parallel.For(0, hilos, i =>
            {
                try { store.WriteAccount("pepe", contenidos[i]); }
                catch (Exception ex) { fallas.Add(ex); }
            });

            Assert.True(fallas.IsEmpty, "escribir la misma cuenta desde varios hilos tiró: " +
                                        string.Join(" | ", fallas.Select(e => e.GetType().Name + ": " + e.Message)));

            // Y lo que queda en disco tiene que ser UNA de las versiones enteras, no un pedazo de dos.
            var leido = store.ReadAccount("pepe");
            Assert.Contains(leido, contenidos);
        }
        finally { if (Directory.Exists(raiz)) Directory.Delete(raiz, recursive: true); }
    }

    /// <summary>El personaje corre el mismo riesgo por otro camino: el autoguardado del game loop y el
    /// <c>/MOD</c> de un GM sobre un objetivo OFFLINE tocan el mismo <c>.chr</c> desde hilos distintos.</summary>
    [Fact]
    public void VeinteEscriturasSimultaneasDelMismoPersonajeNoSePisan()
    {
        var raiz = Path.Combine(Path.GetTempPath(), "coverao-concurrencia-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new FileStore(raiz);
            const int hilos = 20;
            var contenidos = Enumerable.Range(0, hilos).Select(i => $"[STATS]\nGLD={i}\n").ToArray();
            var fallas = new System.Collections.Concurrent.ConcurrentBag<Exception>();

            Parallel.For(0, hilos, i =>
            {
                try { store.WriteCharacter("rugura", contenidos[i]); }
                catch (Exception ex) { fallas.Add(ex); }
            });

            Assert.True(fallas.IsEmpty, "escribir el mismo personaje desde varios hilos tiró: " +
                                        string.Join(" | ", fallas.Select(e => e.GetType().Name + ": " + e.Message)));
            Assert.Contains(store.ReadCharacter("rugura"), contenidos);
        }
        finally { if (Directory.Exists(raiz)) Directory.Delete(raiz, recursive: true); }
    }
}
