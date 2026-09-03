using System.Net.Sockets;

var builder = DistributedApplication.CreateBuilder(args);

var mySql = builder
    .AddMySql("db")
    .WithImageTag("8.0")
    .WithLifetime(ContainerLifetime.Persistent)
    .WithDataVolume();

// Idempotent creation script for game database
var gameInitScriptPath = Path.Join(Path.GetDirectoryName(typeof(Program).Assembly.Location), "init_aaemu_game.sql");
var gameInitScript = File.ReadAllText(gameInitScriptPath);
var mySqlGameDb = mySql
    .AddDatabase("aaemu-game", "aaemu_game")
    .WithCreationScript(gameInitScript);

// The login server is the Go binary from aaemu-cluster/server. Run it
// outside Aspire and point the game at its internal listener.
var loginHost = builder.AddParameter("login-host", "127.0.0.1");
var loginPort = builder.AddParameter("login-port", "1234");

var gameServer = builder.AddProject<Projects.AAEmu_Game>("game-server")
    .WithEndpoint(name: "game-public", port: 1239, targetPort: 1239, isProxied: false, protocol: ProtocolType.Tcp,
        isExternal: true)
    .WithEndpoint(name: "game-stream-public", port: 1250, targetPort: 1250, isProxied: false,
        protocol: ProtocolType.Tcp, isExternal: true)
    .WithEndpoint(name: "health", targetPort: 1281, scheme: "http", isExternal: false)
    .WithHttpHealthCheck("/health/ready", endpointName: "health")
    .WithEnvironment("Connections__MySQLProvider__Database", mySqlGameDb.Resource.DatabaseName)
    .WithEnvironment("Connections__MySQLProvider__Host", mySql.Resource.PrimaryEndpoint.Property(EndpointProperty.Host))
    .WithEnvironment("Connections__MySQLProvider__Port", mySql.Resource.PrimaryEndpoint.Property(EndpointProperty.Port))
    .WithEnvironment("Connections__MySQLProvider__User", "root")
    .WithEnvironment("Connections__MySQLProvider__Password", mySql.Resource.PasswordParameter)
    .WithEnvironment("Connections__AutoApplyUpdates", "true")
    .WithEnvironment("LoginNetwork__Host", loginHost)
    .WithEnvironment("LoginNetwork__Port", loginPort)
    .WithReference(mySqlGameDb)
    .WaitFor(mySqlGameDb)
    .WithOtlpExporter();

builder.Build().Run();
