using System.Buffers;
using System.Net;
using UDPServer.Core;

namespace UDPServer.Models;

public class PacketJob : IJob
{
    private readonly UDPGameServer _server;
    private readonly byte[] _buffer;
    private readonly int _bufferSize;
    private readonly IPEndPoint _clientEP;

    public PacketJob(UDPGameServer server, byte[] buffer, int bufferSize, IPEndPoint clientEP)
    {
        _server = server;
        _buffer = buffer;
        _bufferSize = bufferSize;
        _clientEP = clientEP;
    }
    
    public void Execute()
    {
        try
        {
            _server.ProcessPacket(_buffer, _bufferSize, _clientEP);
        }
        catch (Exception e)
        {
            Console.WriteLine($"[서버] PacketJob 설정 오류 : {e.Message}");
            // ArrayPool<byte>.Shared.Return(_buffer);
        }
    }
}