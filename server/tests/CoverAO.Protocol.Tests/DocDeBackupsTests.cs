using CoverAO.Server.Data;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// LA DOCUMENTACIÓN DE CÓMO RESTAURAR UN BACKUP, contrastada contra el código que los escribe.
///
/// <para>Existe por un error real: la guía de restauración decía copiar
/// <c>data/backups/backup-2026-08-08_0300/</c> y el servidor nombra sus copias <c>coverao-AAAAMMDD-HHMMSS</c>.
/// Esa carpeta nunca existió. Nadie lo notó porque una guía de restauración sólo se lee EL DÍA QUE HAY QUE
/// RESTAURAR, y ese día no es el momento de descubrir que la ruta no está.</para>
///
/// <para>El nombre y las tres carpetas salen de <see cref="Backups"/> y <see cref="FileStore"/>, así que si
/// alguien los cambia mañana, esto se cae y avisa. Es la única parte del proyecto donde una línea de un .md
/// es tan operativa como el código.</para>
/// </summary>
public class DocDeBackupsTests
{
    /// <summary>Sube desde el binario del test hasta la raíz del repo (la que tiene docs/ y hosting/).</summary>
    private static string? RaizDelRepo()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "docs"))) d = d.Parent;
        return d?.FullName;
    }

    private static string[] Guias()
    {
        string? raiz = RaizDelRepo();
        if (raiz is null) return Array.Empty<string>();
        return new[] { Path.Combine(raiz, "docs", "HOSTING.md"), Path.Combine(raiz, "hosting", "publish.ps1") }
            .Where(File.Exists).ToArray();
    }

    [Fact]
    public void Toda_ruta_de_backup_que_la_guia_escribe_existe_de_verdad()
    {
        var guias = Guias();
        Assert.NotEmpty(guias);   // si no se encuentran, el test no está probando nada

        // Cada `data/backups/LOQUESEA` que aparezca en una guía. No alcanza con pedir que el prefijo esté
        // MENCIONADO en algún lado del archivo: la primera versión de este test hacía eso y se quedó verde con
        // la guía saboteada, porque el prefijo bueno aparecía en otro párrafo. Lo que hay que mirar es la ruta
        // que el operador va a tipear.
        var rutas = new System.Text.RegularExpressions.Regex(@"data[/\\]backups[/\\]([A-Za-z0-9_.\-]+)");
        int miradas = 0;
        foreach (var g in guias)
        {
            foreach (System.Text.RegularExpressions.Match m in rutas.Matches(File.ReadAllText(g)))
            {
                miradas++;
                string nombre = m.Groups[1].Value;
                // Los de la poda, o los que se apartan antes de pisar la base: los dos los escribe el servidor.
                Assert.True(nombre.StartsWith(Backups.Prefijo, StringComparison.Ordinal)
                            || nombre.StartsWith(Backups.PrefijoDeLosApartados, StringComparison.Ordinal),
                    $"{Path.GetFileName(g)} manda a copiar de 'data/backups/{nombre}', y el servidor nombra sus " +
                    $"copias '{Backups.Prefijo}AAAAMMDD-HHMMSS'. Quien siga esa guía no va a encontrar la carpeta " +
                    "— y sólo se entera el día que necesita restaurar.");
            }
        }
        Assert.True(miradas > 0, "ninguna guía muestra una ruta de backup: o se borró la sección o cambió el formato");
    }

    [Fact]
    public void La_guia_nombra_las_TRES_carpetas_que_el_backup_contiene()
    {
        var guias = Guias();
        Assert.NotEmpty(guias);
        // Las mismas tres que copia FileStore.Backup. Que estén las tres importa: restaurar sólo Charfile deja
        // los personajes sin la cuenta que los lista, y no aparecen al entrar.
        foreach (var g in guias)
        {
            string texto = File.ReadAllText(g);
            if (!texto.Contains("data/backups") && !texto.Contains(@"data\backups")) continue;
            foreach (var carpeta in new[] { "Charfile", "Cuentas", "Misc" })
                Assert.True(texto.Contains(carpeta),
                    $"{Path.GetFileName(g)} explica cómo restaurar y no menciona '{carpeta}', que es una de las " +
                    "tres carpetas que el backup trae y hay que devolver a su lugar.");
        }
    }
}
