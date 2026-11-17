using CoverAO.Protocol;
using CoverAO.Server;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;

namespace CoverAO.Protocol.Tests;

public class VersionDelClienteTests
{
    [Theory]
    [InlineData("")]
    [InlineData("Version=")]
    [InlineData("Version=1.0.0")]
    [InlineData("Version=99.99.99")]
    public void El_ini_viejo_o_vacio_no_desactiva_ni_cambia_la_version(string clave)
    {
        Assert.DoesNotContain("Version", ServerIni.ClavesConocidas);
        var datos = new GameData();
        var leveling = new Leveling(datos.LevelExp);
        using var almacen = AlmacenSqlite.EnMemoria();
        var ini = ServerIni.Desde(IniFile.Parse("[INIT]\n" + clave), "test.ini");
        var d = new PacketDispatcher(new World(), new GameLoop(), new Persistence(almacen, leveling), datos, leveling, serverIni: ini);
        var session = new ClientSession(new Transporte(), d);
        d.HandleOne(session, new ByteQueueReader(Paquete(ClientPacketId.ConnectAccount, actual: false)));
        Assert.True(session.Cerrada);
        var actual = new ClientSession(new Transporte(), d);
        try
        {
            d.HandleOne(actual, new ByteQueueReader(Paquete(ClientPacketId.ConnectAccount, actual: true)));
            Assert.False(actual.Cerrada);
        }
        finally { actual.Close("fin de prueba"); }
    }

    private sealed class Transporte : ITransport
    {
        public string RemoteEndpoint => "test";
        public readonly System.Collections.Concurrent.ConcurrentQueue<byte[]> Recibidos = new();
        public readonly TaskCompletionSource<byte[]> Primero = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<int> ReceiveAsync(byte[] buffer, CancellationToken ct) => new(0);
        public void Send(byte[] data) { Recibidos.Enqueue(data); Primero.TrySetResult(data); }
        public void Close(string reason) { }
    }

    private static byte[] Paquete(ClientPacketId id, bool actual)
    {
        var w = new ByteQueueWriter().WriteByte((byte)id).WriteAsciiString("cuenta").WriteAsciiString("clave");
        if (id is ClientPacketId.CreateNewAccount or ClientPacketId.ProcesosLogin) w.WriteAsciiString("1234");
        if (actual) w.WriteVersionDelJuego();
        else w.WriteByte(0).WriteByte(0).WriteByte(0);
        if (id == ClientPacketId.ProcesosLogin) return w.WriteByte(1).ToArray();
        if (id is ClientPacketId.LoginExistingChar or ClientPacketId.LoginNewChar) w.WriteAsciiString("Aldo");
        if (id == ClientPacketId.LoginNewChar) w.WriteByte(1).WriteByte(1).WriteByte(1).WriteByte(1).WriteInteger(1);
        if (id != ClientPacketId.CreateNewAccount) w.WriteAsciiString("mac").WriteLong(1);
        return w.ToArray();
    }

    [Theory]
    [InlineData(ClientPacketId.LoginExistingChar)]
    [InlineData(ClientPacketId.LoginNewChar)]
    [InlineData(ClientPacketId.ConnectAccount)]
    [InlineData(ClientPacketId.CreateNewAccount)]
    [InlineData(ClientPacketId.ProcesosLogin)]
    public async Task Los_cinco_ingresos_rechazan_otro_cliente_antes_de_operar(ClientPacketId id)
    {
        var world = new World();
        var loop = new GameLoop();
        var datos = new GameData();
        var leveling = new Leveling(datos.LevelExp);
        using var almacen = AlmacenSqlite.EnMemoria();
        var db = new Persistence(almacen, leveling);
        var d = new PacketDispatcher(world, loop, db, datos, leveling);
        var transporte = new Transporte();
        var session = new ClientSession(transporte, d);
        d.HandleOne(session, new ByteQueueReader(Paquete(id, actual: false)));
        Assert.True(session.Cerrada);
        var aviso = await transporte.Primero.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal((byte)ServerPacketId.ShowMessageBox, aviso[0]);
        Assert.Single(transporte.Recibidos);
        Assert.Empty(world.Players);
        Assert.Null(db.LoadAccount("cuenta"));
    }

    [Theory]
    [InlineData(ClientPacketId.LoginExistingChar)]
    [InlineData(ClientPacketId.LoginNewChar)]
    [InlineData(ClientPacketId.ConnectAccount)]
    [InlineData(ClientPacketId.CreateNewAccount)]
    [InlineData(ClientPacketId.ProcesosLogin)]
    public void Los_cinco_ingresos_aceptan_la_version_de_esta_compilacion(ClientPacketId id)
    {
        var datos = new GameData();
        var leveling = new Leveling(datos.LevelExp);
        using var almacen = AlmacenSqlite.EnMemoria();
        var d = new PacketDispatcher(new World(), new GameLoop(), new Persistence(almacen, leveling), datos, leveling);
        var transporte = new Transporte();
        var session = new ClientSession(transporte, d);
        d.HandleOne(session, new ByteQueueReader(Paquete(id, actual: true)));
        Assert.False(session.Cerrada);
        Assert.Empty(transporte.Recibidos);
        session.Close("fin de prueba");
    }
}
