using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;

namespace CoverAO.Protocol.Tests;

public sealed class LasMetasDeLosBotsTests
{
    [Fact]
    public void El_hito_de_ahorro_se_conserva_aunque_el_bot_gaste_y_el_servidor_reinicie()
    {
        string ruta = Directory.CreateTempSubdirectory("coverao-metas-").FullName;
        try
        {
            var datos = GameData.Load();
            var mapas = new Navegante(datos);
            Bot BotNuevo() => new("Ciro", null, new EntornoDeBots(null!, new ServerIni(), 0, 0, datos, mapas, null!)
                { Metas = new MetasDeBots(ruta) }, 1);
            var s = new SentidosDelBot();
            s.Recibir(ServerMessages.UpdateUserStats(new Player
                { CharIndex = 500, Name = "Ciro", Account = "ciro_bot", Level = 50, Gold = 32_000 }));
            BotNuevo().ActualizarMetaDeNivel50(s);
            Assert.Equal(new MetaDeBot(RumboDeBot.Equipo, 50_000, 30_000), new MetasDeBots(ruta).Leer("ciro"));
            s.Recibir(ServerMessages.UpdateGold(51_000));
            BotNuevo().ActualizarMetaDeNivel50(s);
            s.Recibir(ServerMessages.UpdateGold(5_000));
            BotNuevo().ActualizarMetaDeNivel50(s);
            Assert.Equal(new MetaDeBot(RumboDeBot.Equipo, 50_000, 50_000), new MetasDeBots(ruta).Leer("Ciro"));
        }
        finally
        {
            File.Delete(Path.Combine(ruta, "bots-metas.json"));
            Directory.Delete(ruta);
        }
    }

    [Fact]
    public void Si_el_archivo_esta_tomado_relee_las_metas_existentes_y_reintenta_guardar_el_hito()
    {
        string ruta = Directory.CreateTempSubdirectory("coverao-metas-").FullName;
        string archivo = Path.Combine(ruta, "bots-metas.json");
        long ahora = 0;
        var anterior = new MetaDeBot(RumboDeBot.Abastecer, 6, 3);
        try
        {
            new MetasDeBots(ruta).Guardar("Rufo", anterior);
            MetasDeBots metas;
            using (File.Open(archivo, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                metas = new MetasDeBots(ruta, () => ahora);
                Assert.False(metas.Listo);
            }
            ahora += 30_000;
            metas.ReintentarPendiente();
            Assert.True(metas.Listo);
            Assert.Equal(anterior, metas.Leer("Rufo"));
            var ultima = anterior with { Progreso = 6 };
            Directory.CreateDirectory(archivo + ".tmp");
            try { metas.Guardar("Rufo", ultima); }
            finally { Directory.Delete(archivo + ".tmp"); }
            Assert.Equal(anterior, new MetasDeBots(ruta).Leer("Rufo"));
            ahora += 30_000;
            metas.ReintentarPendiente();
            Assert.Equal(ultima, new MetasDeBots(ruta).Leer("Rufo"));
        }
        finally
        {
            File.Delete(archivo);
            Directory.Delete(ruta);
        }
    }

    [Fact]
    public void Los_lotes_publicados_durante_el_bloqueo_se_suman_una_vez_al_recuperar_la_meta()
    {
        string ruta = Directory.CreateTempSubdirectory("coverao-lotes-").FullName;
        string archivo = Path.Combine(ruta, "bots-metas.json");
        var anterior = new MetaDeBot(RumboDeBot.Abastecer, 6, 3);
        long ahora = 0;
        try
        {
            new MetasDeBots(ruta).Guardar("Rufo", anterior);
            var datos = GameData.Load();
            var s = new SentidosDelBot();
            s.Recibir(ServerMessages.UpdateUserStats(new Player
                { CharIndex = 500, Name = "Rufo", Account = "rufo_bot", Level = 50 }));
            Bot bot;
            using (File.Open(archivo, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var metas = new MetasDeBots(ruta, () => ahora);
                Assert.False(metas.Listo);
                bot = new Bot("Rufo", null, new EntornoDeBots(null!, new ServerIni(), 0, 0, datos, new Navegante(datos), null!)
                    { Metas = metas }, 1);
                bot.ActualizarMetaDeNivel50(s);
                bot.AnotarLotePublicado(s);
                bot.AnotarLotePublicado(s);
                bot.ActualizarMetaDeNivel50(s);
            }

            ahora += 30_000;
            bot.ActualizarMetaDeNivel50(s);
            bot.ActualizarMetaDeNivel50(s);
            Assert.Equal(anterior with { Progreso = 5 }, new MetasDeBots(ruta).Leer("Rufo"));
            bot.AnotarLotePublicado(s);
            bot.AnotarLotePublicado(s);
            Assert.Equal(anterior with { Progreso = 6 }, new MetasDeBots(ruta).Leer("Rufo"));
        }
        finally
        {
            File.Delete(archivo);
            Directory.Delete(ruta);
        }
    }
}
