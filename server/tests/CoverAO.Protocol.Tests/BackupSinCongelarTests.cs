using System.Collections.Concurrent;
using CoverAO.Server.Data;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// EL BACKUP CONGELABA EL MUNDO, Y SACARLO DEL LOOP TENÍA UNA TRAMPA.
///
/// <para>La copia del almacén corría adentro del hilo del game loop —una vuelta de 40 ms— archivo por archivo.
/// Medido sobre el <c>Charfile</c> del servidor original, 9.918 personajes: CINCO SEGUNDOS de mundo detenido,
/// cada backup, para siempre. El autosave tenía lo mismo en chico: 0,75 ms por <c>.chr</c>, o sea dos ticks
/// enteros con cien conectados.</para>
///
/// <para>La trampa está en el arreglo, no en el diagnóstico. <c>File.Copy</c> abre el origen SIN
/// <c>FILE_SHARE_DELETE</c>, y en Windows eso impide renombrar sobre ese archivo: sacando la copia a otro hilo,
/// el <c>File.Move</c> con el que el almacén cierra cada escritura atómica se cae con sharing violation, y el
/// que pierde el guardado es el JUGADOR. Se ve como un personaje al que "se le borró lo de la última media
/// hora", una vez cada tanto y sin patrón — o sea, como nada. Por eso el test mide la escritura, no la copia.</para>
/// </summary>
public class BackupSinCongelarTests
{
    private const int Personajes = 400;   // suficiente para que el backup dure y las dos cosas se solapen
    private const string Stem = "coverao-20260816-120000";

    private static FileStore Poblar(string raiz)
    {
        var store = new FileStore(raiz);
        for (int i = 0; i < Personajes; i++) store.WriteCharacter($"pj{i:D4}", $"[STATS]\nGLD={i}\n");
        return store;
    }

    /// <summary>Guardar un personaje mientras el backup recorre el directorio no se puede caer.</summary>
    /// <remarks>El escritor va en un hilo PROPIO y el backup no arranca hasta que ese hilo confirmó que está
    /// corriendo. Con <c>Task.Run</c> el test daba rojo al correr la suite entera: xUnit satura el pool, el
    /// escritor no llegaba a empezar y el backup terminaba antes — cero solapamiento, o sea que no probaba
    /// nada.</remarks>
    [Fact]
    public void GuardarMientrasCorreElBackupNoFalla()
    {
        var raiz = Path.Combine(Path.GetTempPath(), "coverao-backup-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = Poblar(raiz);
            string destino = Path.Combine(raiz, "backups");

            var fallas = new ConcurrentBag<Exception>();
            int escrituras = 0;
            using var seguir = new CancellationTokenSource();
            using var arranco = new ManualResetEventSlim(false);
            var escritor = new Thread(() =>
            {
                arranco.Set();
                while (!seguir.IsCancellationRequested)
                {
                    // Justo el que el backup va a encontrar a mitad del recorrido.
                    try { store.WriteCharacter("pj0200", $"[STATS]\nGLD={Interlocked.Increment(ref escrituras)}\n"); }
                    catch (Exception ex) { fallas.Add(ex); }
                }
            }) { IsBackground = true };
            escritor.Start();
            arranco.Wait(TimeSpan.FromSeconds(30));

            int antes = Volatile.Read(ref escrituras);
            store.Backup(destino, Stem);
            int durante = Volatile.Read(ref escrituras) - antes;
            seguir.Cancel();
            escritor.Join(TimeSpan.FromSeconds(30));

            Assert.True(fallas.IsEmpty, "guardar mientras el backup copiaba tiró: " +
                                        string.Join(" | ", fallas.Select(e => e.GetType().Name + ": " + e.Message)));
            // Sin esto el test da verde sin haber probado nada: si las dos cosas no llegaron a solaparse, que
            // nadie se haya caído no significa absolutamente nada.
            Assert.True(durante >= 10, $"no llegaron a solaparse: sólo {durante} escrituras durante el backup");
            // Y lo copiado tiene que ser una versión ENTERA, no un pedazo de dos.
            string copiado = File.ReadAllText(Path.Combine(destino, Stem, "Charfile", "PJ0200.chr"));
            Assert.StartsWith("[STATS]", copiado);
            Assert.EndsWith("\n", copiado);
        }
        finally { if (Directory.Exists(raiz)) Directory.Delete(raiz, recursive: true); }
    }

    /// <summary>Un backup terminado no deja nada a medio armar.</summary>
    [Fact]
    public void ElBackupTerminadoNoDejaRestos()
    {
        var raiz = Path.Combine(Path.GetTempPath(), "coverao-backup-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new FileStore(raiz);
            store.WriteCharacter("rugura", "[STATS]\nGLD=1\n");
            string destino = Path.Combine(raiz, "backups");

            store.Backup(destino, Stem);

            Assert.Empty(Directory.EnumerateDirectories(destino, "parcial-*"));
            Assert.Single(Backups.Listar(destino));
        }
        finally { if (Directory.Exists(raiz)) Directory.Delete(raiz, recursive: true); }
    }

    /// <summary>Uno CORTADO A LA MITAD no cuenta como copia, que es de lo que dependía la retención: contándolo,
    /// a las N rotaciones no quedaba ninguna entera.</summary>
    [Fact]
    public void UnBackupCortadoNoCuentaNiSobrevive()
    {
        var raiz = Path.Combine(Path.GetTempPath(), "coverao-backup-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new FileStore(raiz);
            store.WriteCharacter("rugura", "[STATS]\nGLD=1\n");
            string destino = Path.Combine(raiz, "backups");
            Directory.CreateDirectory(Path.Combine(destino, "parcial-coverao-20200101-000000", "Charfile"));

            Assert.Empty(Backups.Listar(destino));   // para la retención no existe

            store.Backup(destino, Stem);

            Assert.Empty(Directory.EnumerateDirectories(destino, "parcial-*"));   // y el próximo backup lo limpia
            Assert.Single(Backups.Listar(destino));
        }
        finally { if (Directory.Exists(raiz)) Directory.Delete(raiz, recursive: true); }
    }
}
