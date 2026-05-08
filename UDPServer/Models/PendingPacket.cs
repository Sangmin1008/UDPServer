using System.Net;

namespace UDPServer.Models;

public class PendingPacket
{
    public NetworkPacket Packet { get; set; }
    public IPEndPoint TargetEndPoint { get; set; }
    public DateTime LastSentTime { get; set; }
    public int RetryCount { get; set; }
}