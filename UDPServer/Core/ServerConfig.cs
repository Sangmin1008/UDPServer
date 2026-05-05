namespace UDPServer.Core;

public class ServerConfig
{
    public string ServerIP { get; set; }
    public int ServerPort { get; set; }
    public int MaxPlayers { get; set; }
    public int BufferSize { get; set; } // 1024 bytes
    public int WorkerThreadCount { get; set; }
    public int PlayerTimeoutSeconds { get; set; }

    public ServerConfig()
    {
        ServerIP = "127.0.0.1";
        ServerPort = 7777;
        MaxPlayers = 100;
        BufferSize = 1024;
        WorkerThreadCount = Environment.ProcessorCount;
        PlayerTimeoutSeconds = 10;
    }
}