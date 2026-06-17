using System.Net;

namespace UDPServer.Models;

public class PendingPacket
{
    public byte[] Payload { get; set; }
    public IPEndPoint TargetEndPoint { get; set; }
    public uint Sequence { get; set; }
    public DateTime LastSentTime { get; set; }
    public int RetryCount { get; set; }
}