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

    /// <summary>MORIR: el servidor le saca todo lo puesto sin avisarlo ranura por ranura, y el bot tiene que quedar igual.</summary>
    [Fact]
    public void Al_morir_el_bot_queda_sin_nada_puesto_como_en_el_servidor()
    {
        var p = new Player { CharIndex = 1, Name = "Aldo", Account = "aldo_bot", Pos = new WorldPos(1, 50, 50) };
        p.Inventory[3] = new InventorySlot { ObjIndex = 574, Amount = 1, Equipped = true };
        p.Inventory[4] = new InventorySlot { ObjIndex = 466, Amount = 1, Equipped = true };
        var s = new SentidosDelBot();
        foreach (byte ranura in new byte[] { 3, 4 })
            s.Recibir(ServerMessages.ChangeInventorySlot(ranura, p.Inventory[ranura].ObjIndex, p.Inventory[ranura].Amount, true, 0f, 1));

        PacketDispatcher.KillPlayer(new World(), _datos, p, "test");
        s.Recibir(ServerMessages.UpdateHP(0));   // lo que manda el golpe que lo mata

        Assert.False(p.Inventory[3].Equipped || p.Inventory[4].Equipped, "el servidor no lo desequipó: el test no mira nada");
        Assert.False(s.EstaPuesto(3));
        Assert.False(s.EstaPuesto(4));
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

    /// <summary>
    /// SE COME Y SE TOMA CON LA BARRA POR LA MITAD, no esperando el cero. La barra se la dice el SERVIDOR, con el
    /// mismo paquete que manda su reloj del hambre, así que esto pasa por la lectura de verdad.
    ///
    /// <para>Medido en `verdad7`: los ocho bots pasaron los quince minutos con el hambre en 10 de 100 y la sed en 20,
    /// llevando 99 manzanas y 99 botellas encima. En cero no se regenera la energía y sin energía no se pelea.</para>
    /// </summary>
    [Theory]
    // lo que manda el servidor en la barra | hay que reponerla
    [InlineData(0, true)]         // famélico: ahí ya se perdió la regeneración
    [InlineData(10, true)]        // con lo que arrancaban los bots de campo
    [InlineData(50, true)]        // la mitad: un jugador come acá
    [InlineData(51, false)]
    [InlineData(100, false)]      // recién comido
    public void Come_y_toma_cuando_la_barra_va_por_la_mitad(byte barra, bool repone)
    {
        var s = new SentidosDelBot();
        s.Recibir(ServerMessages.UpdateHungerAndThirst(barra, barra));

        Assert.Equal(repone, Bot.TieneQueComer(s));
        Assert.Equal(repone, Bot.TieneQueTomar(s));
    }

    /// <summary>Y CADA BARRA VA POR SU LADO: al comer, el servidor manda sólo el hambre (`UpdateHambre`), y la sed
    /// sigue pidiendo lo suyo.</summary>
    [Fact]
    public void Al_comer_deja_de_tener_hambre_pero_la_sed_sigue()
    {
        var s = new SentidosDelBot();
        s.Recibir(ServerMessages.UpdateHungerAndThirst(20, 20));
        Assert.True(Bot.TieneQueComer(s), "con la barra en 20 tendría que comer: el test no mira nada");

        s.Recibir(ServerMessages.UpdateHambre(100));

        Assert.False(Bot.TieneQueComer(s));
        Assert.True(Bot.TieneQueTomar(s));
    }
}
