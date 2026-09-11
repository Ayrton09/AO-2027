using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// EL CUERPO DEL BOT CONTRA EL SERVIDOR DE VERDAD: lo que el bot cree de su nivel, sus barras y su meditación es lo que
/// decidió el servidor. Los dos casos pasan por la lógica real —la subida de nivel de <see cref="Leveling"/> y el
/// despachador con el loop girando—, así que si el servidor cambia qué deja lleno o cómo avisa, esto se pone rojo.
/// </summary>
public class CuerpoDelBotTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "coverao_cuerpo_" + Guid.NewGuid().ToString("N"));
    private readonly GameData _datos = GameData.Load();
    private readonly PacketDispatcher _dispatcher;
    private readonly GameLoop _loop = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loopTask;

    public CuerpoDelBotTests()
    {
        Directory.CreateDirectory(_root);
        var leveling = new Leveling(_datos.LevelExp);
        _dispatcher = new PacketDispatcher(new World(), _loop, new Persistence(new FileStore(_root), leveling), _datos, leveling);
        // Meditar y caminar corren adentro del loop, así que el loop tiene que girar de verdad.
        _loopTask = _loop.RunAsync(_cts.Token);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _loopTask.Wait(TimeSpan.FromSeconds(5)); } catch { /* cancelado */ }
        try { Directory.Delete(_root, recursive: true); } catch { /* el temp queda */ }
        _cts.Dispose();
    }

    /// <summary>SUBIR DE NIVEL: el bot queda con los topes, el nivel y las barras que le dejó el servidor.</summary>
    [Fact]
    public void Al_subir_de_nivel_el_bot_ve_lo_que_le_dejo_el_servidor()
    {
        if (_datos.LevelExp.Count == 0) return;   // sin la data del juego no hay curva de experiencia
        var leveling = new Leveling(_datos.LevelExp);
        var p = new Player
        {
            CharIndex = 1, Name = "Aldo", Account = "aldo_bot", Class = 2,
            MaxHp = 60, Hp = 20, MaxMana = 100, Mana = 10, MaxStamina = 80, Stamina = 5,
        };
        p.Attributes[GameLimits.AttrInteligencia] = 18;
        p.Attributes[GameLimits.AttrConstitucion] = 18;
        leveling.InitNewCharacter(p);
        var s = new SentidosDelBot();
        s.Recibir(ServerMessages.UpdateUserStats(p));

        Assert.True(leveling.Grant(p, p.Elu) > 0, "no subió de nivel: el test no mira nada");
        s.Recibir(ServerMessages.UpdateUserStatsForLevel(p));

        Assert.Equal((short)p.Level, s.Nivel);
        Assert.Equal(p.MaxHp, s.VidaMaxima);
        Assert.Equal((double)p.Hp / p.MaxHp, s.Salud, 3);
        Assert.Equal((double)p.Mana / p.MaxMana, s.Mana, 3);
        Assert.Equal((double)p.Stamina / p.MaxStamina, s.Fuelle, 3);
    }

    /// <summary>MEDITAR: el bot sabe si está meditando aunque lo decida el servidor, que la corta al caminar.</summary>
    [Fact]
    public async Task Meditar_el_bot_sabe_lo_que_decidio_el_servidor()
    {
        var transporte = new TransporteDeBot("Aldo");
        var s = new SentidosDelBot();
        transporte.Recibido += s.Recibir;
        var sesion = new ClientSession(transporte, _dispatcher);
        var p = new Player { CharIndex = 1, Name = "Aldo", Account = "aldo_bot", Pos = new WorldPos(1, 50, 50), MaxMana = 100, Mana = 10 };
        sesion.Player = p;

        _dispatcher.HandleOne(sesion, Paquete(ClientPacketId.Meditate));
        await Esperar(() => p.Meditating && s.Meditando);
        Assert.True(p.Meditating, "el servidor no lo puso a meditar: el test no mira nada");
        Assert.True(s.Meditando);

        _dispatcher.HandleOne(sesion, Paquete(ClientPacketId.Walk, 1));   // caminar corta la meditación
        await Esperar(() => !p.Meditating && !s.Meditando);
        Assert.False(p.Meditating, "caminar no cortó la meditación: el test no mira nada");
        Assert.False(s.Meditando);
        sesion.Close("fin del test");
    }

    private static ByteQueueReader Paquete(ClientPacketId id, params byte[] datos)
    {
        var w = new ByteQueueWriter().WriteByte((byte)id);
        foreach (byte dato in datos) w.WriteByte(dato);
        return new ByteQueueReader(w.ToArray());
    }

    private static async Task Esperar(Func<bool> listo)
    {
        for (int i = 0; i < 250 && !listo(); i++) await Task.Delay(20);
    }
}
