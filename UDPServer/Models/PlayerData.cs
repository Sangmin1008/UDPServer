using System.Net;
using System.Numerics;

namespace UDPServer.Models;

public class PlayerData
{
    public int PlayerId { get; set; }
    public Vector3 Position { get; set; }
    public Vector3 Rotation { get; set; }
    public IPEndPoint EndPoint { get; set; }
    public DateTime LastUpdateTime { get; set; }

    public PlayerData(int playerId, IPEndPoint endPoint)
    {
        PlayerId = playerId;
        EndPoint = endPoint;
        Position = Vector3.Zero;
        Rotation = Vector3.Zero;
        LastUpdateTime = DateTime.UtcNow;
    }
    
    // 플레이어의 위치와 회전 정보를 업데이트 하는 메서드
    public void UpdateTransform(Vector3 position, Vector3 rotation)
    {
        Position = position;
        Rotation = rotation;
    }
}