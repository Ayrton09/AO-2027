using System.Reflection;
using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;

namespace CoverAO.Protocol.Tests;

public sealed class LasMetasDeLosBotsTests
{
    [Fact]
    public async Task La_meta_antigua_de_ahorro_solo_se_cierra_cuando_se_equipa_la_pieza_elegida()
    {
        string ruta = Directory.CreateTempSubdirectory("coverao-meta-equipo-").FullName;
        string archivo = Path.Combine(ruta, "bots-metas.json");
        try
        {
            File.WriteAllText(archivo, """{"Ciro":{"Rumbo":0,"Objetivo":50000,"Progreso":50000}}""");
            var metas = new MetasDeBots(ruta);
            var ahorro = Assert.IsType<MetaDeBot>(metas.Leer("Ciro"));
            Assert.False(ahorro.Confirmada);
            metas.Guardar("Ciro", ahorro with { Pieza = 2 });
            var datos = GameData.Load();
            Bot Nuevo() => new("Ciro", null, new EntornoDeBots(null!, new ServerIni(), 0, 0, datos, new Navegante(datos), null!)
                { Metas = new MetasDeBots(ruta) }, 1);
            var s = new SentidosDelBot();
            s.Recibir(ServerMessages.UpdateUserStats(new Player
            {
                CharIndex = 500, Name = "Ciro", Account = "ciro_bot", Level = 50, Gold = 25_000,
            }));
            var bot = Nuevo();
            typeof(Bot).GetField("_clase", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(bot, GameLimits.ClaseGuerrero);
            s.Recibir(ServerMessages.ChangeInventorySlot(1, 15, 1, true, 0, 1));
            s.Recibir(ServerMessages.ChangeInventorySlot(2, 2, 1, false, 0, 1));
            bot.ActualizarMetaDeNivel50(s);
            Assert.Equal(ahorro with { Pieza = 2 }, new MetasDeBots(ruta).Leer("Ciro"));
            var transporte = new TransporteDeBot("Ciro");
            Assert.True((bool)typeof(Bot).GetMethod("Equiparse", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(bot, [transporte, s])!);
            var buf = new byte[4096];
            using var tiempo = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            Assert.Equal(2, await transporte.ReceiveAsync(buf, tiempo.Token));
            Assert.Equal((byte)ClientPacketId.EquipItem, (byte)(buf[0] ^ transporte.Clave));
            Assert.Equal(2, buf[1] ^ transporte.Clave);
            s.Recibir(ServerMessages.ChangeInventorySlot(2, 2, 1, true, 0, 1));
            bot.ActualizarMetaDeNivel50(s);
            Assert.Equal(ahorro with { Pieza = 2, Confirmada = true }, new MetasDeBots(ruta).Leer("Ciro"));
        }
        finally
        {
            File.Delete(archivo);
            Directory.Delete(ruta);
        }
    }

    [Fact]
    public void Un_cazador_con_arco_y_flechas_reemplaza_la_meta_de_espada_por_equipo_que_si_se_pone()
    {
        string ruta = Directory.CreateTempSubdirectory("coverao-meta-cazador-").FullName;
        string archivo = Path.Combine(ruta, "bots-metas.json");
        try
        {
            var datos = GameData.Load();
            var metas = new MetasDeBots(ruta);
            metas.Guardar("Ciro", new MetaDeBot(RumboDeBot.Equipo, 50_000, 50_000, Pieza: 2));
            var bot = new Bot("Ciro", null, new EntornoDeBots(null!, new ServerIni(), 0, 0, datos, new Navegante(datos), null!)
                { Metas = metas }, 1);
            typeof(Bot).GetField("_clase", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(bot, GameLimits.ClaseCazador);
            typeof(Bot).GetField("_miBando", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(bot, Player.StatusCiudadano);
            var p = new Player
            {
                CharIndex = 500, Name = "Ciro", Account = "ciro_bot", Level = 50, Gold = 50_000, Hp = 200, MaxHp = 200,
                Pos = new WorldPos(20, 75, 55),
            };
            var s = new SentidosDelBot();
            s.Recibir(ServerMessages.UserIndexInServer(p.CharIndex));
            s.Recibir(ServerMessages.ChangeMap(20));
            s.Recibir(ServerMessages.CharacterCreate(p));
            s.Recibir(ServerMessages.UpdateUserStats(p));
            s.Recibir(ServerMessages.ChangeInventorySlot(1, 478, 1, true, 0, 1));
            s.Recibir(ServerMessages.ChangeInventorySlot(2, 480, 20, true, 0, 1));
            s.Recibir(ServerMessages.CharacterCreate(new Npc { CharIndex = 501, Template = datos.Npc(19)!, Pos = new WorldPos(20, 77, 55) }));
            bot.ActualizarMetaDeNivel50(s);
            var meta = Assert.IsType<MetaDeBot>(metas.Leer("Ciro"));
            Assert.NotEqual((short)2, meta.Pieza);
            Assert.True(meta.Pieza > 0);
            Assert.False(meta.Confirmada);
        }
        finally
        {
            File.Delete(archivo);
            Directory.Delete(ruta);
        }
    }

    [Fact]
    public void Las_bajas_acumuladas_no_hacen_saltar_un_rango_que_el_bot_todavia_no_reclamo()
    {
        string ruta = Directory.CreateTempSubdirectory("coverao-meta-rango-pendiente-").FullName;
        string archivo = Path.Combine(ruta, "bots-metas.json");
        try
        {
            var datos = GameData.Load();
            var metas = new MetasDeBots(ruta);
            var bot = new Bot("Ciro", null, new EntornoDeBots(null!, new ServerIni(), 0, 0, datos, new Navegante(datos), null!)
                { Metas = metas }, 1);
            typeof(Bot).GetField("_miBando", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(bot, Player.StatusArmada);
            typeof(Bot).GetField("_clase", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(bot, GameLimits.ClaseGuerrero);
            var p = new Player
            {
                CharIndex = 500, Name = "Ciro", Account = "ciro_bot", Level = 50, Hp = 200, MaxHp = 200,
                FactionStatus = Player.StatusArmada, FactionRank = 2, RenegadosMatados = 900,
            };
            var s = new SentidosDelBot();
            s.Recibir(ServerMessages.UserIndexInServer(p.CharIndex));
            s.Recibir(ServerMessages.UpdateUserStats(p));
            s.Recibir(ServerMessages.MiniStats(p));
            s.Recibir(ServerMessages.CharMsgStatus(p, p, 0));
            bot.ActualizarMetaDeNivel50(s);
            Assert.Equal(new MetaDeBot(RumboDeBot.Faccion, 450, 450, Player.StatusArmada), metas.Leer("Ciro"));
        }
        finally
        {
            File.Delete(archivo);
            Directory.Delete(ruta);
        }
    }

    [Theory]
    [InlineData(Player.StatusArmada)]
    [InlineData(Player.StatusMilicia)]
    [InlineData(Player.StatusCaos)]
    public async Task Las_bajas_solas_no_cierran_la_meta_hasta_que_el_enlistador_confirma_el_rango(byte faccion)
    {
        string ruta = Directory.CreateTempSubdirectory("coverao-meta-rango-").FullName;
        string archivo = Path.Combine(ruta, "bots-metas.json");
        var datos = GameData.Load();
        var leveling = new Leveling(datos.LevelExp);
        using var almacen = AlmacenSqlite.EnMemoria();
        using var db = new Persistence(almacen, leveling);
        var dispatcher = new PacketDispatcher(new World(), new GameLoop(), db, datos, leveling);
        var transporte = new TransporteDeBot("Ciro");
        int objetivo = RangosDeFaccion.ParaRango(faccion, 2);
        var p = new Player
        {
            CharIndex = 500, Name = "Ciro", Account = "ciro_bot", Level = 50, Hp = 200, MaxHp = 200,
            FactionStatus = faccion, FactionRank = 1, RenegadosMatados = objetivo,
        };
        var sesion = new ClientSession(transporte, dispatcher) { Player = p };
        try
        {
            var metas = new MetasDeBots(ruta);
            var pendiente = new MetaDeBot(RumboDeBot.Faccion, objetivo, objetivo, faccion);
            metas.Guardar("Ciro", pendiente);
            var bot = new Bot("Ciro", null, new EntornoDeBots(null!, new ServerIni(), 0, 0, datos, new Navegante(datos), null!)
                { Metas = metas }, 1);
            typeof(Bot).GetField("_miBando", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(bot, faccion);
            var s = new SentidosDelBot();
            s.Recibir(ServerMessages.UserIndexInServer(p.CharIndex));
            s.Recibir(ServerMessages.UpdateUserStats(p));
            s.Recibir(ServerMessages.MiniStats(p));
            s.Recibir(ServerMessages.CharMsgStatus(p, p, 0));
            Assert.Equal((byte)1, s.RangoEn(faccion));
            bot.ActualizarMetaDeNivel50(s);
            Assert.Equal(pendiente, metas.Leer("Ciro"));

            var visto = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            transporte.Recibido += paquete =>
            {
                s.Recibir(paquete);
                if (s.RangoEn(faccion) == 2) visto.TrySetResult();
            };
            var enlistador = new Npc { CharIndex = 501, Template = datos.Npc(72)! };
            typeof(PacketDispatcher).GetMethod("Recompensar", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(dispatcher, [sesion, p, enlistador]);
            await visto.Task.WaitAsync(TimeSpan.FromSeconds(5));
            bot.ActualizarMetaDeNivel50(s);
            Assert.Equal(pendiente with { Confirmada = true }, new MetasDeBots(ruta).Leer("Ciro"));
        }
        finally
        {
            sesion.Player = null;
            sesion.Close("fin");
            File.Delete(archivo);
            Directory.Delete(ruta);
        }
    }

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
