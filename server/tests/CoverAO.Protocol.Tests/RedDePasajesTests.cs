using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

public sealed class RedDePasajesTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Cada_pirata_vende_un_pasaje_utilizable_por_destino_permitido(bool arreglarRinkel)
    {
        var data = GameData.Load();
        var ini = CargarIni(arreglarRinkel ? "" : "PasesDeRinkel=0");
        data.ConfigurarPasajes(ini);
        var mundo = new World();
        WorldPopulator.SpawnNpcs(new NpcSystem(mundo, data), data);
        WorldPopulator.PlaceObjects(mundo, data);
        int[] imperiales = [1, 34, 61], republicanos = [64, 179, 183], neutrales = [99, 111, 150, 181, 217];
        var todos = imperiales.Concat(republicanos).Concat(neutrales).ToArray();
        int total = 0;
        foreach (int origen in todos)
        {
            var pirata = Assert.Single(data.MapSpawns[origen], s => data.Npc(s.NpcIndex)?.NpcType == 7);
            Assert.True(mundo.NpcAt(new((short)origen, (byte)pirata.X, (byte)pirata.Y)) is { IsTransportador: true, IsMerchant: true });
            var ficha = data.Npc(pirata.NpcIndex)!;
            var stock = ficha.StockItems!;
            Assert.Equal(stock.Count, ficha.NroItems);
            Assert.Equal(stock.ToArray(), ficha.LineasDelDat!.ToArray());
            var posibles = neutrales.Contains(origen) ? todos
                : imperiales.Contains(origen) ? imperiales.Concat(neutrales) : republicanos.Concat(neutrales);
            var destinos = stock.Select(s =>
            {
                Assert.Equal(100, s.Amount);
                var pase = data.Obj(s.ObjIndex)!;
                Assert.Equal(36, pase.ObjType);
                Assert.Equal(origen, pase.PasajeDesdeMap);
                Assert.InRange(pase.Index, (short)1, short.MaxValue);
                Assert.True(pase.Value > 0 && pase.Crucial > 0);
                Assert.False(data.IsBlockedTile(pase.PasajeHastaMap, pase.PasajeHastaX, pase.PasajeHastaY));
                Assert.False(data.IsWater(pase.PasajeHastaMap, pase.PasajeHastaX, pase.PasajeHastaY));
                return pase.PasajeHastaMap == 20 ? 99 : pase.PasajeHastaMap;
            });
            Assert.Equal(posibles.Where(d => d != origen).Order(), destinos.Order());
            total += stock.Count;
        }
        Assert.Equal(92, total);
        Assert.Equal(61, data.Obj(1075)!.PasajeHastaMap);
        Assert.Equal(arreglarRinkel ? 99 : 20, data.Obj(595)!.PasajeDesdeMap);
        Assert.Equal(3500, data.Obj(595)!.Value);
    }

    [Fact]
    public void La_opcion_apagada_conserva_los_piratas_y_mostradores_del_install()
    {
        var data = GameData.Load();
        var mostradorDeOrac = data.Npc(150)!.StockItems!.ToArray();
        data.ConfigurarPasajes(CargarIni("PasajesPorFaccion=0\r\nPasesDeRinkel=0"));
        Assert.DoesNotContain(data.MapSpawns[1], s => data.Npc(s.NpcIndex)?.NpcType == 7);
        Assert.Equal(mostradorDeOrac, data.Npc(150)!.StockItems);
        Assert.Equal(181, data.Obj(1075)!.PasajeHastaMap);
        Assert.Equal(20, data.Obj(595)!.PasajeDesdeMap);
    }

    [Fact]
    public void El_bot_encuentra_el_pasaje_a_Tiama_en_un_mostrador_que_lo_atiende()
    {
        var datos = GameData.Load();
        var ini = new ServerIni();
        datos.ConfigurarPasajes(ini);
        var viajero = Assert.Single(datos.MapSpawns[1], s => datos.Npc(s.NpcIndex)?.NpcType == 7);
        Assert.True(Bot.PuedeComprarlePasaje(datos, ini, viajero.NpcIndex, 1, 218, Player.StatusArmada));
        Assert.False(Bot.PuedeComprarlePasaje(datos, ini, viajero.NpcIndex, 1, 218, Player.StatusRepublicano));
        Assert.True(Bot.PuedeComprarlePasaje(datos, ini, 108, 150, 218, Player.StatusMilicia));
    }

    [Theory]
    [InlineData(150, 1, Player.StatusRepublicano, false)]
    [InlineData(181, 179, Player.StatusCiudadano, true)]
    [InlineData(1, 34, Player.StatusCiudadano, false)]
    [InlineData(179, 183, Player.StatusMilicia, false)]
    public async Task Compra_real_y_viaje_desde_el_mostrador(short origen, short destino, byte faccion, bool cruceDeAgua)
    {
        var datos = GameData.Load();
        var ini = new ServerIni();
        datos.ConfigurarPasajes(ini);
        var mundo = new World();
        var loop = new GameLoop();
        var nivel = new Leveling(datos.LevelExp);
        using var db = new Persistence(AlmacenSqlite.EnMemoria(), nivel);
        var dispatcher = new PacketDispatcher(mundo, loop, db, datos, nivel, serverIni: ini);
        var sp = Assert.Single(datos.MapSpawns[origen], s => datos.Npc(s.NpcIndex)?.NpcType == 7);
        var sistema = new NpcSystem(mundo, datos);
        sistema.AddSpawn(datos.Npc(sp.NpcIndex)!, new(origen, (byte)sp.X, (byte)sp.Y));
        var pirata = Assert.Single(mundo.Npcs);
        Assert.True(pirata.IsMerchant && pirata.IsTransportador);
        int ranura = pirata.Stock!.FindIndex(s => datos.Obj(s.ObjIndex)?.PasajeHastaMap == destino) + 1;
        Assert.True(ranura > 0);
        short pase = pirata.Stock[ranura - 1].ObjIndex;
        var pos = new WorldPos(origen, (byte)(sp.X + (origen is 1 or 179 ? 1 : -1)), (byte)sp.Y);
        Assert.False(datos.IsBlockedTile(pos.Map, pos.X, pos.Y));
        Assert.False(datos.IsWater(pos.Map, pos.X, pos.Y));
        var p = new Player
        {
            CharIndex = mundo.AllocateCharIndex(), Account = "viajero", Name = "Viajero", Hp = 100, MaxHp = 100,
            Pos = pos, FactionStatus = faccion, Gold = 100000,
        };
        p.Inventory[1] = new InventorySlot { ObjIndex = 1035, Amount = 1 }; // Lindos → Rinkel, de otro puerto
        mundo.Add(p);
        var sesion = new ClientSession(new Transporte(), dispatcher) { Player = p };
        p.Connection = sesion;
        using var fin = new CancellationTokenSource();
        var corriendo = loop.RunAsync(fin.Token);
        try
        {
            async Task Mandar(params byte[] bytes)
            {
                dispatcher.HandleOne(sesion, new ByteQueueReader(bytes));
                await loop.SincronizarContinuacionesAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
            await Mandar((byte)ClientPacketId.DoubleClick, (byte)sp.X, (byte)sp.Y);
            Assert.Equal(pirata.CharIndex, p.TradingWith);
            await Mandar((byte)ClientPacketId.CommerceSell, 1, 1, 0);
            Assert.Equal(1, p.Inventory[1].Amount); // venderlo no deja un pasaje de otro puerto en el mostrador
            Assert.Equal(100000, p.Gold);
            await Mandar((byte)ClientPacketId.CommerceBuy, (byte)ranura, 1, 0);
            Assert.Equal(pase, p.Inventory[2].ObjIndex);
            Assert.True(p.Gold < 100000);
            await Mandar((byte)ClientPacketId.CommerceEnd);
            if (cruceDeAgua)
            {
                p.Skills[GameLimits.SkillNavegacion] = 79;
                await Mandar((byte)ClientPacketId.UseItem, 2);
                Assert.Equal(origen, p.Pos.Map);
                Assert.Equal(pase, p.Inventory[2].ObjIndex);
                p.Skills[GameLimits.SkillNavegacion] = 80;
            }
            await Mandar((byte)ClientPacketId.UseItem, 2);
            Assert.Equal(destino, p.Pos.Map);
            Assert.True(p.Inventory[2].IsEmpty);
        }
        finally
        {
            fin.Cancel();
            await corriendo;
            sesion.Close("fin de prueba");
        }
    }

    private static ServerIni CargarIni(string opciones)
    {
        string ruta = Path.Combine(Path.GetTempPath(), "pasajes-" + Guid.NewGuid().ToString("N") + ".ini");
        try { File.WriteAllText(ruta, "[INIT]\r\n" + opciones); return ServerIni.Cargar(ruta); }
        finally { File.Delete(ruta); }
    }

    private sealed class Transporte : ITransport
    {
        public string RemoteEndpoint => "prueba de pasajes";
        public ValueTask<int> ReceiveAsync(byte[] buffer, CancellationToken ct) => new(0);
        public void Send(byte[] data) { }
        public void Close(string reason) { }
    }
}
