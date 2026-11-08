using System.Reflection;
using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;

namespace CoverAO.Protocol.Tests;

public class MetalurgiaDeLosBotsTests
{
    private static readonly GameData Datos = GameData.Load();
    private static MethodInfo Metodo(string nombre) => typeof(Bot).GetMethod(nombre, BindingFlags.Instance | BindingFlags.NonPublic)!;

    [Fact]
    public void El_equipo_de_oro_tiene_precio_de_subasta_sin_alterar_el_precio_del_mercader()
    {
        var o = Datos.Obj(1333)!;
        Assert.Equal(0, o.Value);
        Assert.Equal(20, o.IngotIron); Assert.Equal(40, o.IngotSilver); Assert.Equal(60, o.IngotGold);
        Assert.Equal(56400, Bot.ValorDeMercado(Datos, o.Index));
        Assert.Equal(0, o.Value);
        var f = new ServerMessages.FilaDeSubasta(1, o.Index, 1, 28200, 36660, "Rufo", 60, "");
        Assert.True(Bot.LeConvieneLaSubasta(f, Bot.ValorDeMercado(Datos, o.Index), "Ciro", 40000, permiteProductor: true));
        Assert.False(Bot.LeConvieneLaSubasta(f, Bot.ValorDeMercado(Datos, o.Index), "rufo", 40000, permiteProductor: true));
    }

    [Fact]
    public void Tras_tres_listas_sin_oro_usa_el_excedente_y_reanuda_el_encargo_al_recibir_oro()
    {
        var (bot, sentidos, _) = Preparar("Rufo", 12);
        typeof(Bot).GetField("_clase", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(bot, (byte)12);
        typeof(Bot).GetField("_recetaDeHerreria", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(bot, (short)1333);
        sentidos.Recibir(ServerMessages.ChangeInventorySlot(1, 386, 100, false, 0, 1));
        sentidos.Recibir(ServerMessages.ChangeInventorySlot(2, 387, 60, false, 0, 1));
        int Tiene(int obj) => sentidos.CuantoTiene(i => i == obj);
        short? Elegir() => (short?)Metodo("ElegirFabricacion").Invoke(bot,
            [sentidos, Datos.BlacksmithRecipes, Oficios.Herreria, 100, (Func<int, int>)Tiene]);
        Assert.Null(Elegir());
        for (int visita = 0; visita < 2; visita++) Metodo("AnotarMaterialEnElMercado").Invoke(bot, [sentidos, 50_000]);
        Assert.Null(Elegir());
        Metodo("AnotarMaterialEnElMercado").Invoke(bot, [sentidos, 50_000]);
        Assert.Equal((short)1007, Elegir()); // Usa 80 de hierro y 20 de plata, por encima de la reserva.
        sentidos.Recibir(ServerMessages.ChangeInventorySlot(1, 386, 20, false, 0, 1));
        sentidos.Recibir(ServerMessages.ChangeInventorySlot(2, 387, 40, false, 0, 1));
        Assert.Null(Elegir());
        sentidos.Recibir(ServerMessages.ChangeInventorySlot(3, 388, 60, false, 0, 1));
        Assert.Equal((short)1333, Elegir());
    }

    [Theory]
    [InlineData(100, 13000, 20000, 80, true)]
    [InlineData(100, 16001, 20000, 80, false)]
    [InlineData(100, 13000, 12999, 80, false)]
    [InlineData(100, 13000, 20000, 0, false)]
    [InlineData(1000, 13000, 20000, 80, false)]
    [InlineData(0, 13000, 20000, 80, false)]
    public void Compra_solo_lotes_necesarios_que_puede_pagar(short cantidad, int precio, int oro, int faltan, bool acepta)
    {
        var f = new ServerMessages.FilaDeSubasta(1, 386, cantidad, precio, precio, "Quim", 60, "");
        Assert.Equal(acepta, Bot.LeConvieneElLote(f, 200, "Rufo", oro, faltan));
        Assert.False(Bot.LeConvieneElLote(f, 200, "quim", oro, faltan));
    }

    [Fact]
    public void El_minero_reparte_los_metales_y_guarda_el_crudo_para_fundir()
    {
        var (b, s, _) = Preparar("Quim", 14);
        Assert.Equal((short)192, Metodo("MineralQueBusca").Invoke(b, [s]));
        s.Recibir(ServerMessages.ChangeInventorySlot(1, 386, 100, false, 0, 1));
        Assert.Equal((short)193, Metodo("MineralQueBusca").Invoke(b, [s]));
        s.Recibir(ServerMessages.ChangeInventorySlot(2, 387, 100, false, 0, 1));
        Assert.Equal((short)194, Metodo("MineralQueBusca").Invoke(b, [s]));
        s.Recibir(ServerMessages.ChangeInventorySlot(3, 194, 3500, false, 0, 1));
        Assert.False((bool)Metodo("EsBotin").Invoke(b, [s, (short)194])!);
        Assert.True((bool)Metodo("MineroTieneReserva").Invoke(b, [s])!);
    }

    [Fact]
    public void El_herrero_no_vende_sus_lingotes_al_mercader_ni_fabrica_sin_limite()
    {
        var (b, s, _) = Preparar("Rufo", 12);
        s.Recibir(ServerMessages.ChangeInventorySlot(1, 386, 100, false, 0, 1));
        Assert.False((bool)Metodo("EsBotin").Invoke(b, [s, (short)386])!);
        s.Recibir(ServerMessages.ChangeInventorySlot(2, 1333, 3, false, 0, 1));
        Assert.True((bool)Metodo("HayLugarParaProducir").Invoke(b, [s])!); // reserva uno para usar
        s.Recibir(ServerMessages.ChangeInventorySlot(2, 1333, 4, false, 0, 1));
        Assert.False((bool)Metodo("HayLugarParaProducir").Invoke(b, [s])!);
    }

    [Fact]
    public void Los_nudillos_comprados_reemplazan_los_viejos_y_no_desplazan_un_arma()
    {
        var (b,s,_) = Preparar("Fabia",8);
        s.Recibir(ServerMessages.ChangeInventorySlot(1,1331,1,true,0,1));
        Assert.True((bool)Metodo("MejoraLoQueTiene").Invoke(b,[s,(short)1333])!);
        s.Recibir(ServerMessages.ChangeInventorySlot(2,1333,1,false,0,1));
        Assert.True((bool)Metodo("Equiparse").Invoke(b,[new TransporteDeBot("Fabia"),s])!);
        s.Recibir(ServerMessages.ChangeInventorySlot(3,2,1,false,0,1));
        Assert.False((bool)Metodo("MejoraLoQueTiene").Invoke(b,[s,(short)1333])!);
    }

    [Fact]
    public void Al_volver_desnudo_se_pone_la_ropa_antes_de_agotar_su_energia_defendiendose()
    {
        var (b,s,_) = Preparar("Quim",14);
        var ropa = Datos.Objects.Values.First(o => o.IsArmor && Bot.PuedeUsarlo(o,14,5,1,50));
        // La identidad del constructor se confirma al entrar; acá se fija la del personaje de esta prueba.
        typeof(Bot).GetField("_raza",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(b,(byte)5);
        typeof(Bot).GetField("_genero",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(b,(byte)1);
        s.Recibir(ServerMessages.ChangeInventorySlot(1,ropa.Index,1,false,0,1));
        Assert.True((bool)Metodo("VestirseSiEstaDesnudo").Invoke(b,[new TransporteDeBot("Quim"),s])!);
        s.Recibir(ServerMessages.ChangeInventorySlot(1,ropa.Index,1,true,0,1));
        Assert.False((bool)Metodo("VestirseSiEstaDesnudo").Invoke(b,[new TransporteDeBot("Quim"),s])!);
    }

    [Fact]
    public async Task El_herrero_puede_vender_un_sobrante_de_la_ranura_equipada_y_conserva_su_equipo()
    {
        var (b,s,_) = Preparar("Rufo",12);
        var t = new TransporteDeBot("Rufo");
        s.Recibir(ServerMessages.ChangeInventorySlot(1,1333,2,true,0,1));
        using var fin = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var tarea = (Task)Metodo("PrepararProduccionParaSubastarAsync").Invoke(b,[t,s,fin.Token])!;
        var bytes = new byte[128];
        int leidos = await t.ReceiveAsync(bytes,fin.Token);
        Assert.Equal(2,leidos);
        Assert.Equal((byte)ClientPacketId.EquipItem,(byte)(bytes[0]^t.Clave));
        Assert.Equal(1,(byte)(bytes[1]^t.Clave));
        s.Recibir(ServerMessages.ChangeInventorySlot(1,1333,2,false,0,1));
        await tarea;
        Assert.Equal(1,Metodo("CantidadParaSubastar").Invoke(b,[s,(short)1333,2]));
        s.Recibir(ServerMessages.ChangeInventorySlot(1,1333,1,false,0,1));
        Assert.Equal(0,Metodo("CantidadParaSubastar").Invoke(b,[s,(short)1333,1]));
    }

    [Theory]
    [InlineData(192)]
    [InlineData(193)]
    public void El_minero_encuentra_las_minas_fuera_del_radio_de_compras(int mineral)
    {
        var nav = new Navegante(Datos);
        bool Peligro(int ficha) => Bot.EsPeligrosoParaElBot(Datos.Npc(ficha),411,50,true,true,Player.StatusCiudadano,Datos,new ServerIni());
        bool Faena(int mapa) => Bot.SeTrabajaElArbolAhi(Datos.IsPkMap(mapa),Datos.MapSpawns.GetValueOrDefault(mapa)?.Select(n=>n.NpcIndex)??[],Peligro);
        var cruza = Bot.SeCruzaBuscando(Datos,new ServerIni(),Player.StatusCiudadano,50,411,true,true);
        bool Recurso(int obj) => Datos.Obj(obj)?.MineralIndex == mineral;
        Assert.Null(nav.DondeSeTrabaja(20,false,Recurso,(22,81),Faena,cruza));
        var mina = nav.DondeSeTrabaja(20,false,Recurso,(22,81),Faena,cruza,Bot.SaltosDelOficio);
        Assert.NotNull(mina);
        Assert.InRange(mina.Value.Saltos,Navegante.TopeDeMapas+1,Bot.SaltosDelOficio);
    }

    [Theory]
    [InlineData(49, 900)]
    [InlineData(50, 50)]
    public void Al_nivel_maximo_la_caza_valora_el_oro(int nivel, int esperado)
    {
        var npc = Datos.Npcs.Values.First() with { GiveExp = 900, GiveGld = 50 };
        Assert.Equal(esperado, Bot.RecompensaDePresa(npc, nivel));
    }

    [Fact]
    public void Las_bajas_y_la_identidad_salen_de_la_ficha_persistida()
    {
        var (_, s, p) = Preparar("Quim", 13); // este personaje existía antes de agregar al minero al reparto
        p.NpcsKilled = 50;
        s.Recibir(ServerMessages.MiniStats(p));
        Assert.Equal(50, s.CriaturasMatadas);
        Assert.Equal(((byte)13, p.Race, p.Gender), s.Identidad);
        var antes = (Mapa:20, Experiencia:49, Nivel:50, Seguidas:5);
        Assert.Equal(1, Bot.OtraBusquedaSinGanar(antes,20,s.CriaturasMatadas,50).Seguidas);
    }

    [Fact]
    public void El_transporte_rodea_la_vision_del_enemigo_sin_encerrarse_si_ya_lo_vio()
    {
        var zonas = Bot.ZonasQueEvitaElTransportista((20,20), [(40,40)]);
        Assert.Contains(((byte)32,(byte)34), zonas);
        Assert.Contains(((byte)48,(byte)46), zonas);
        Assert.DoesNotContain(((byte)31,(byte)40), zonas);
        Assert.DoesNotContain(((byte)40,(byte)47), zonas);
        Assert.Empty(Bot.ZonasQueEvitaElTransportista((39,40), [(40,40)]));
        // Los obstáculos se aplican al camino completo, no sólo al paso que chocó con el enemigo.
        var nav = new Navegante(Datos);
        var camino = nav.Camino(15,(30,40),(50,40),zonas);
        Assert.NotNull(camino);
        Assert.DoesNotContain(camino, zonas.Contains);
    }

    [Theory]
    [InlineData(25_000, false)]
    [InlineData(50_000, true)]
    public async Task La_meta_de_ahorro_pospone_el_viaje_por_mejoras_hasta_juntar_el_oro(int oro, bool sale)
    {
        string ruta = Directory.CreateTempSubdirectory("coverao-meta-viaje-").FullName;
        string archivo = Path.Combine(ruta, "bots-metas.json");
        try
        {
            var metas = new MetasDeBots(ruta);
            metas.Guardar("Ciro", new MetaDeBot(RumboDeBot.Equipo, 50_000, 0));
            var (bot, sentidos, p) = Preparar("Ciro", GameLimits.ClaseGuerrero, metas: metas);
            typeof(Bot).GetField("_clase", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(bot, GameLimits.ClaseGuerrero);
            typeof(Bot).GetField("_miBando", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(bot, Player.StatusCiudadano);
            var subastador = Datos.MapSpawns[20].First(n => Datos.Npc(n.NpcIndex)?.NpcType == 16);
            p.Pos = new WorldPos(20, (byte)(subastador.X + 2), (byte)subastador.Y);
            sentidos.Recibir(ServerMessages.CharacterCreate(p));
            sentidos.Recibir(ServerMessages.UpdateGold(oro - 10_000));
            sentidos.Recibir(ServerMessages.BankInit(goliath: true, bankGold: 10_000));
            bot.ActualizarMetaDeNivel50(sentidos);

            // Una conexión cerrada detiene la marcha una vez que el planificador tomó la decisión.
            var viaje = (Task<bool>)Metodo("MercadoDelOficioAsync").Invoke(bot,
                [new TransporteDeBot("Ciro"), sentidos, Task.CompletedTask, CancellationToken.None])!;
            Assert.Equal(sale, await viaje);
        }
        finally
        {
            File.Delete(archivo);
            Directory.Delete(ruta);
        }
    }

    [Theory]
    [InlineData(20)]
    [InlineData(34)]
    public async Task El_minero_funde_vende_los_tres_metales_y_el_herrero_fabrica_y_vende_por_el_servidor(short mercado)
    {
        var datos = GameData.Load();
        string rutaMetas = Directory.CreateTempSubdirectory("coverao-meta-oficio-").FullName;
        var metas = new MetasDeBots(rutaMetas);
        using var almacen = AlmacenSqlite.EnMemoria();
        using var db = new Persistence(almacen, new Leveling(Datos.LevelExp));
        var mundo = new World(); var loop = new GameLoop();
        var dispatcher = new PacketDispatcher(mundo, loop, db, datos, new Leveling(Datos.LevelExp));
        using var fin = new CancellationTokenSource(TimeSpan.FromSeconds(150));
        var tareas = new List<Task>(); var transportes = new List<TransporteDeBot>();
        var cerrado = new TaskCompletionSource().Task;
        var subSpawn = Datos.MapSpawns[mercado].First(n => Datos.Npc(n.NpcIndex)?.NpcType == 16);
        var bancoSpawn = Datos.MapSpawns[mercado].First(n => Datos.Npc(n.NpcIndex)?.NpcType == 4);
        Npc PonerNpc(MapNpcSpawn sp, bool banco) {
            var n = new Npc {CharIndex=mundo.AllocateCharIndex(),Template=Datos.Npc(sp.NpcIndex)!,
                Pos=new WorldPos(mercado,(byte)sp.X,(byte)sp.Y),IsBanker=banco,IsSubastador=!banco};
            mundo.AddNpc(n);return n;
        }
        var sub = PonerNpc(subSpawn,false); var banco = PonerNpc(bancoSpawn,true);
        foreach(var sp in Datos.MapSpawns[mercado].Where(n=>Datos.Npc(n.NpcIndex)?.NpcType==4 && n!=bancoSpawn))
            PonerNpc(sp,true);
        WorldPopulator.PlaceObjects(mundo,datos); // también bloquea las puertas, como en el servidor real
        (Bot b, SentidosDelBot s, Player p, TransporteDeBot t) Conectar(string nombre, byte clase) {
            var (b,s,p)=Preparar(nombre,clase,mundo.AllocateCharIndex(),datos,metas);
            p.Gold=200000;p.FactionStatus=Player.StatusCiudadano;
            var cuenta=db.CreateAccount(p.Account,"clave_larga_de_prueba",esBot:true);
            db.CreateCharacter(cuenta,p.Name,p.CharIndex,p.Race,p.Gender,p.Class,1,1);
            db.SaveCharacter(p);
            var t=new TransporteDeBot(nombre);t.Recibido+=s.Recibir;transportes.Add(t);
            var sesion=new ClientSession(t,dispatcher){Player=p};p.Connection=sesion;mundo.Add(p);
            s.Recibir(ServerMessages.UserIndexInServer(p.CharIndex));s.Recibir(ServerMessages.UpdateUserStats(p));
            tareas.Add(sesion.ReceiveLoopAsync(fin.Token));return(b,s,p,t);
        }
        var minero=Conectar("Quim",14);var herrero=Conectar("Rufo",12);var comprador=Conectar("Ciro",3);
        herrero.p.Inventory[10]=new InventorySlot{ObjIndex=2,Amount=1};
        herrero.s.Recibir(ServerMessages.ChangeInventorySlot(10,2,1,false,0,1));
        void Situar((Bot b,SentidosDelBot s,Player p,TransporteDeBot t) a,WorldPos pos,Npc? n=null) {
            a.p.Pos=pos;a.p.TargetNpc=n?.CharIndex??0;
            a.s.Recibir(ServerMessages.ChangeMap(pos.Map));a.s.Recibir(ServerMessages.CharacterCreate(a.p));
            if(n is not null)a.s.Recibir(ServerMessages.CharacterCreate(n));
        }
        async Task Llamar(string metodo,(Bot b,SentidosDelBot s,Player p,TransporteDeBot t) a,bool conCerrada=true) {
            var argumentos=conCerrada?new object[]{a.t,a.s,cerrado,fin.Token}:new object[]{a.t,a.s,fin.Token};
            var tarea=(Task)Metodo(metodo).Invoke(a.b,argumentos)!;
            await tarea.WaitAsync(TimeSpan.FromSeconds(30));
            if(tarea is Task<bool> hizo)Assert.True(hizo.Result,metodo);
        }
        void EnElMercado((Bot b,SentidosDelBot s,Player p,TransporteDeBot t) a) =>
            Situar(a,new WorldPos(mercado,(byte)(sub.Pos.X+2),sub.Pos.Y),sub);
        void Lista((Bot b,SentidosDelBot s,Player p,TransporteDeBot t) a) => a.s.Recibir(ServerMessages.SubastasLista(false,
            mundo.Subastas.Abiertas.Select(f=>new ServerMessages.FilaDeSubasta(f.Id,f.Objeto,f.Cantidad,(int)f.PrecioInicial,(int)f.PrecioFinal,f.Vendedor,3600,"")).ToArray()));
        var primerasVentas=new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var equipoPublicado=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        loop.OnTickHandler = _ => {
            foreach(var p in mundo.Players)if(p.Laborando)dispatcher.TickTrabajo((ClientSession?)p.Connection,p,Environment.TickCount64);
            if(mundo.Subastas.Abiertas.Count()==3 && metas.Leer("Quim")?.Progreso==3)
                primerasVentas.TrySetResult(minero.p.Pos.Map);
            if(mundo.Subastas.Abiertas.Any(f=>f.Vendedor=="Rufo" && f.Objeto==1333) && metas.Leer("Rufo")?.Progreso==1)
                equipoPublicado.TrySetResult();
        };
        tareas.Add(loop.RunAsync(fin.Token));
        try {
            var nav = new Navegante(datos);
            Assert.Null(nav.HayComoLlegar(1,(75,82),(78,75),2));
            Assert.NotEmpty(nav.PuertasDelCamino(1,(75,82),[(78,75)],2)!);
            Assert.NotNull(nav.DondeTrabajar(1,(75,82),20,false,i=>datos.Obj(i)?.IsForge==true,puertas:3));
            Situar(minero,new WorldPos(1,75,82));
            short[] crudos=[192,193,194], lingotes=[386,387,388], cantidades=[20,40,60];
            for(byte i=0;i<3;i++) {
                short bruto=(short)(cantidades[i]*Oficios.MineralesPorLingote(crudos[i]));
                minero.p.Inventory[i+1]=new InventorySlot{ObjIndex=crudos[i],Amount=bruto};
                minero.s.Recibir(ServerMessages.ChangeInventorySlot((byte)(i+1),crudos[i],bruto,false,0,1));
            }
            for(int i=0;i<3;i++) {
                typeof(Bot).GetField("_proximaFundicion",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(minero.b,0L);
                await Llamar("FundirAsync",minero);
                Assert.Equal(cantidades[i],minero.p.Inventory.Where(v=>v.ObjIndex==lingotes[i]).Sum(v=>v.Amount));
            }
            Assert.DoesNotContain(minero.p.Inventory,v=>crudos.Contains(v.ObjIndex));
            EnElMercado(minero);
            if(mercado==34) {
                // El mostrador de Nix queda detrás de una pared: atiende a tres baldosas, nunca a dos.
                Assert.Null(nav.HayComoLlegar(34,(64,37),(sub.Pos.X,sub.Pos.Y),2));
                Assert.NotNull(nav.HayComoLlegar(34,(64,37),(sub.Pos.X,sub.Pos.Y),3));
                Situar(minero,new WorldPos(34,64,37),sub);
            }
            short[] provisiones=[24,43,38];
            for(byte i=0;i<provisiones.Length;i++) {
                minero.p.Inventory[i+10]=new InventorySlot{ObjIndex=provisiones[i],Amount=100};
                minero.s.Recibir(ServerMessages.ChangeInventorySlot((byte)(i+10),provisiones[i],100,false,0,1));
            }
            // Con provisiones suficientes entrega los lingotes aunque todavía pueda mejorar su equipo.
            // Ejecuta la decisión normal: antes se iba de compras a otra ciudad con toda la carga encima.
            using(var venta=CancellationTokenSource.CreateLinkedTokenSource(fin.Token)) {
                var conducta=(Task)Metodo("ComportarseAsync").Invoke(minero.b,[minero.t,minero.s,cerrado,venta.Token])!;
                try { Assert.Equal(mercado,await primerasVentas.Task.WaitAsync(TimeSpan.FromSeconds(30))); }
                catch(TimeoutException ex) {
                    throw new Exception($"El minero no publicó: tarea={conducta.Status}, identidad={minero.s.Identidad}, posición={minero.p.Pos}, ventas={mundo.Subastas.Abiertas.Count()}, vivo={minero.s.Vivo}, ubicado={minero.s.Ubicado}, oro={minero.s.Oro}",ex);
                }
                finally {
                    venta.Cancel();
                    try { await conducta; } catch(OperationCanceledException) { }
                }
            }
            Assert.Equal(3,mundo.Subastas.Abiertas.Count());
            if(mercado==34) {
                Situar(minero,new WorldPos(34,64,37));
                Assert.True(await (Task<bool>)Metodo("AbrirElBancoAsync").Invoke(minero.b,
                    [minero.t,minero.s,cerrado,fin.Token,null])!);
                Assert.Equal(29,mundo.NpcByIndex(minero.p.TargetNpc)!.Pos.X);
                Assert.Equal(0,minero.s.Banco);
            }
            // Un pedido ya elegido de Nudillos de Oro: completa sus tres materiales antes de cambiar de receta.
            typeof(Bot).GetField("_recetaDeHerreria",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(herrero.b,(short?)1333);
            typeof(Bot).GetField("_proximaFabricacion",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(herrero.b,long.MaxValue);
            for(int i=0;i<3;i++) {
                EnElMercado(herrero);Lista(herrero);await Llamar("ComprarEnLaSubastaAsync",herrero,false);
                Situar(herrero,new WorldPos(mercado,(byte)(banco.Pos.X+1),banco.Pos.Y),banco);
                await Llamar("RetirarLoDeLaSubastaAsync",herrero);
            }
            Assert.Empty(mundo.Subastas.Abiertas);
            for(int i=0;i<3;i++)Assert.Equal(cantidades[i],herrero.p.Inventory.Where(v=>v.ObjIndex==lingotes[i]).Sum(v=>v.Amount));
            EnElMercado(herrero);
            for(byte i=0;i<provisiones.Length;i++) {
                herrero.p.Inventory[i+12]=new InventorySlot{ObjIndex=provisiones[i],Amount=100};
                herrero.s.Recibir(ServerMessages.ChangeInventorySlot((byte)(i+12),provisiones[i],100,false,0,1));
            }
            typeof(Bot).GetField("_proximoMercadoDelOficio",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(herrero.b,Environment.TickCount64+120_000);
            using(var fabricar=CancellationTokenSource.CreateLinkedTokenSource(fin.Token)) {
                var conducta=(Task)Metodo("ComportarseAsync").Invoke(herrero.b,[herrero.t,herrero.s,cerrado,fabricar.Token])!;
                try { await equipoPublicado.Task.WaitAsync(TimeSpan.FromSeconds(45)); }
                finally {
                    fabricar.Cancel();
                    try { await conducta; } catch(OperationCanceledException) { }
                }
            }
            Assert.DoesNotContain(herrero.p.Inventory,v=>lingotes.Contains(v.ObjIndex));
            Assert.Equal(1333,Assert.Single(mundo.Subastas.Abiertas).Objeto);
            EnElMercado(comprador);Lista(comprador);await Llamar("ComprarEnLaSubastaAsync",comprador,false);
            Assert.Empty(mundo.Subastas.Abiertas);
            Assert.Contains(comprador.p.BankInventory,v=>v.ObjIndex==1333&&v.Amount==1);
            Assert.True(minero.p.Bank>0);Assert.True(herrero.p.Bank>0);
            db.EsperarEscriturasPendientes();
            var guardado=db.LoadCharacter(comprador.p.Name,comprador.p.CharIndex,comprador.p.Account);
            Assert.NotNull(guardado);
            Assert.Contains(guardado.BankInventory,v=>v.ObjIndex==1333&&v.Amount==1);
            Assert.Equal(comprador.p.Gold,guardado.Gold);
            var reabiertas = new MetasDeBots(rutaMetas);
            Assert.Equal(new MetaDeBot(RumboDeBot.Abastecer, 6, 3), reabiertas.Leer("Quim"));
            Assert.Equal(new MetaDeBot(RumboDeBot.Abastecer, 6, 1), reabiertas.Leer("Rufo"));
        }
        finally {
            fin.Cancel();foreach(var t in transportes)t.Terminar();loop.Stop();
            try{await Task.WhenAll(tareas);}catch(OperationCanceledException){}
            File.Delete(Path.Combine(rutaMetas, "bots-metas.json"));
            Directory.Delete(rutaMetas);
        }
    }

    [Fact]
    public async Task El_herrero_con_skill_completa_fabrica_equipo_de_oro_consumiendo_los_tres_metales()
    {
        var (bot, sentidos, p) = Preparar("Rufo", 12);
        using var almacen = AlmacenSqlite.EnMemoria();
        using var db = new Persistence(almacen, new Leveling(Datos.LevelExp));
        var mundo = new World(); var loop = new GameLoop();
        var dispatcher = new PacketDispatcher(mundo, loop, db, Datos, new Leveling(Datos.LevelExp));
        using var fin = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var transporte = new TransporteDeBot("Rufo");
        transporte.Recibido += sentidos.Recibir;
        var sesion = new ClientSession(transporte, dispatcher) { Player = p };
        p.Connection = sesion; mundo.Add(p);
        p.Skills[GameLimits.SkillHerreria] = 100;
        short[] objetos = [386,387,388]; short[] cantidades = [20,40,60];
        for (byte i=0;i<objetos.Length;i++)
        {
            p.Inventory[i+1] = new InventorySlot { ObjIndex=objetos[i], Amount=cantidades[i] };
            sentidos.Recibir(ServerMessages.ChangeInventorySlot((byte)(i+1),objetos[i],cantidades[i],false,0,1));
        }
        var corriendo = loop.RunAsync(fin.Token); var recibiendo = sesion.ReceiveLoopAsync(fin.Token);
        try
        {
            var fabrica = (Task<bool>)Metodo("FabricarAsync").Invoke(bot, [transporte,sentidos,new TaskCompletionSource().Task,fin.Token])!;
            Assert.True(await fabrica.WaitAsync(TimeSpan.FromSeconds(8)));
            Assert.Equal(1,p.Inventory.Where(i=>i.ObjIndex==1333).Sum(i=>i.Amount));
            Assert.DoesNotContain(p.Inventory,i=>objetos.Contains(i.ObjIndex));
            Assert.Equal(950,p.Stamina);
            Assert.Equal(100,p.Skills[GameLimits.SkillHerreria]);
        }
        finally
        {
            fin.Cancel(); transporte.Terminar(); loop.Stop();
            try { await Task.WhenAll(corriendo,recibiendo); } catch(OperationCanceledException) { }
        }
    }

    private static (Bot, SentidosDelBot, Player) Preparar(string nombre, byte clase, short indice = 500,
        GameData? datos = null, MetasDeBots? metas = null)
    {
        var p = new Player { CharIndex=indice, Name=nombre, Account=nombre+"_qa", Class=clase,Race=1,Gender=1,
            Pos=new WorldPos(20,50,50),Level=50,Hp=500,MaxHp=500,Stamina=1000,MaxStamina=1000,Gold=50000,Hunger=100,Thirst=100 };
        var s = new SentidosDelBot { MiNombre=nombre };
        s.Recibir(ServerMessages.UserIndexInServer(p.CharIndex));s.Recibir(ServerMessages.ChangeMap(20));
        s.Recibir(ServerMessages.CharacterCreate(p));s.Recibir(ServerMessages.UpdateUserStats(p));
        p.Skills[GameLimits.SkillMineria]=100;p.Skills[GameLimits.SkillHerreria]=100;
        s.Recibir(ServerMessages.SendSkills(p.Skills));
        s.Recibir(ServerMessages.UpdateHungerAndThirst(100,100));
        datos ??= Datos;
        var b = new Bot(nombre,null,new EntornoDeBots(null!,new ServerIni(),0,0,datos,new Navegante(datos),null!)
            { Metas = metas },42);
        return (b,s,p);
    }
}
