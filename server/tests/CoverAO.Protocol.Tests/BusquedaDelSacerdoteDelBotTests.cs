using System.Reflection;
using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;

namespace CoverAO.Protocol.Tests;

/// <summary>Regresion del fantasma detenido junto al spawn de un sacerdote que todavia no puede atenderlo.</summary>
public sealed class BusquedaDelSacerdoteDelBotTests
{
    private static readonly Lazy<(GameData Datos, Navegante Mapas)> Mundo = new(() =>
    {
        var d = GameData.Load();
        return (d, new Navegante(d));
    });
    private static readonly MethodInfo Buscar = typeof(Bot).GetMethod("BuscarSacerdoteAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private sealed class Escena
    {
        public Bot Bot { get; }
        public SentidosDelBot Sentidos { get; } = new() { MiNombre = "Aldo" };
        public TransporteDeBot Transporte { get; } = new("Aldo");
        private readonly TaskCompletionSource _cerrada = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Escena()
        {
            var d = Mundo.Value.Datos;
            Assert.False(d.IsPkMap(208));
            Assert.Contains(d.MapSpawns[208], n => n.NpcIndex == 5 && n.X == 55 && n.Y == 51);
            var p = new Player
            {
                CharIndex = 500, Name = "Aldo", Account = "aldo_busqueda", Class = 13, Level = 4,
                Pos = new WorldPos(208, 56, 51), Hp = 0, MaxHp = 55, Stamina = 0, MaxStamina = 154,
                FactionStatus = Player.StatusCiudadano,
            };
            Sentidos.Recibir(ServerMessages.UserIndexInServer(500));
            Sentidos.Recibir(ServerMessages.UserCharIndexInServer(500));
            Sentidos.Recibir(ServerMessages.ChangeMap(208));
            Sentidos.Recibir(ServerMessages.CharacterCreate(p));
            Sentidos.Recibir(ServerMessages.UpdateUserStats(p));
            Bot = new Bot("Aldo", null, new EntornoDeBots(null!, new ServerIni(), 0, 0, d, Mundo.Value.Mapas, null!), 1);
        }

        public Task<bool> BuscarUnaVez(CancellationToken ct) =>
            (Task<bool>)Buscar.Invoke(Bot, new object[] { Transporte, Sentidos, _cerrada.Task, ct })!;

        public void VerSacerdote(byte x, byte y) => Sentidos.Recibir(ServerMessages.CharacterCreate(new Npc
        {
            CharIndex = 600, Template = Mundo.Value.Datos.Npc(5)!, Hp = 80_000, Pos = new WorldPos(208, x, y), IsReviver = true,
        }));

        public async Task<byte[]> Proximo(CancellationToken ct)
        {
            var b = new byte[4096];
            int n = await Transporte.ReceiveAsync(b, ct);
            return b.Take(n).Select(x => (byte)(x ^ Transporte.Clave)).ToArray();
        }
    }

    [Fact]
    public async Task Cerca_del_spawn_sin_sacerdote_visible_la_busqueda_espera_y_admite_cancelacion_en_vez_de_girar_en_vacio()
    {
        var e = new Escena();
        Assert.False(e.Sentidos.Vivo);
        using var corte = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        // Task.Run protege al runner incluso si una regresion convierte la conducta en un ciclo sin awaits.
        var trabajo = Task.Run(async () =>
        {
            for (int intento = 0; intento < 10; intento++) await e.BuscarUnaVez(corte.Token);
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => trabajo.WaitAsync(TimeSpan.FromSeconds(4)));
    }

    [Fact]
    public async Task Si_ve_al_sacerdote_fuera_de_alcance_camino_a_su_posicion_actual_y_no_confunde_el_spawn_con_la_llegada()
    {
        var e = new Escena();
        // Posicion actual recibida del servidor dentro de la ventana6Y, fuera del alcance4Y del altar.
        // Es una escena de protocolo especifica; no cambia el spawn nativo ni afirma que el NPC5 de campo deambule.
        e.VerSacerdote(55, 57);
        using var corte = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        var pedido = e.Proximo(corte.Token);
        var buscar = Task.Run(() => e.BuscarUnaVez(corte.Token));
        try
        {
            var primero = await Task.WhenAny(buscar, pedido);
            Assert.Same(pedido, primero);
            Assert.Equal((byte)ClientPacketId.Walk, (await pedido)[0]);
        }
        finally
        {
            corte.Cancel();
            try { await buscar.WaitAsync(TimeSpan.FromSeconds(4)); } catch (OperationCanceledException) { }
            try { await pedido; } catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task Tras_seis_llegadas_al_spawn_sin_sacerdote_atendible_pide_el_regreso_al_hogar_del_muerto()
    {
        var e = new Escena();
        using var corte = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        var pedido = e.Proximo(corte.Token);
        var buscar = Task.Run(async () =>
        {
            for (int i = 0; i < 6; i++) Assert.True(await e.BuscarUnaVez(corte.Token));
        });
        try { Assert.Equal(new byte[] { (byte)ClientPacketId.RegresarHogar }, await pedido.WaitAsync(TimeSpan.FromSeconds(5))); }
        finally
        {
            corte.Cancel();
            try { await buscar.WaitAsync(TimeSpan.FromSeconds(4)); } catch (OperationCanceledException) { }
            try { await pedido; } catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task Con_sacerdote_visible_al_alcance_espera_sin_volver_al_hogar_aunque_consulte_mas_de_seis_veces()
    {
        var e = new Escena();
        e.VerSacerdote(55, 51);
        using var corte = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        var buscar = Task.Run(async () =>
        {
            for (int i = 0; i < 7; i++) Assert.True(await e.BuscarUnaVez(corte.Token));
        });
        await buscar.WaitAsync(TimeSpan.FromSeconds(5));
        // La busqueda ya termino: se inspeccionan todos los pedidos que dejo, sin imponerle un paso innecesario.
        for (;;)
        {
            using var lectura = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            byte[] p;
            try { p = await e.Proximo(lectura.Token); }
            catch (OperationCanceledException) { break; }
            Assert.NotEqual((byte)ClientPacketId.RegresarHogar, p[0]);
        }
    }
}
