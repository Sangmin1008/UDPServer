using System.Text.Json.Serialization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using UDPServer.Utils;
using MemoryPack;

namespace UDPServer.Models;

// 패킷 타입 정의
public enum PacketType : byte
{
    PlayerJoin = 1,     // 플레이어 접속 요청
    PlayerLeave = 2,    // 플레이어 접속 종료
    PlayerUpdate = 3,   // 플레이어 위치 및 상태 업데이트
    PlayerSpawn = 4,    // 플레이어 스폰 요청, 다른 플레이어에게 새 플레이어 접속 알림
    PlayerDespawn = 5,  // 플레이어 디스폰 요청, 다른 플레이어에게 플레이어 접속 종료 알림
    PlayerFire = 6,     // 플레이어 발사 이벤트
    Heartbeat = 7,      // 하트비트
    Timeout = 8,        // 플레이어 타임 아웃
    PlayerHit = 9,      // 플레이어 피격 이벤트
    ItemSpawn = 10,     // 아이템 스폰
    ItemPickup = 11,    // 아이템 픽업
    ItemConsumed = 12,  // 아이템 소비
    PlayerEmoticon = 13,// 이모티콘
    
    JoinSuccess = 14,   // 접속 성공
    Ack = 99            // 수신 확인 응답
}

[MemoryPackable]
public partial class NetworkPacket
{
    public PacketType Type { get; set; }
    public uint Sequence { get; set; }
    public bool IsReliable { get; set; }
    public int PlayerId { get; set; }
    public Vector3 Position { get; set; }
    public Vector3 Rotation { get; set; }
    public int TargetId { get; set; }
    public int Damage { get; set; }
    public int ItemId { get; set; }
    public int ItemType { get; set; }
    public int EmoticonId { get; set; }
    public DateTime Timestamp { get; set; }

    [MemoryPackConstructor]
    public NetworkPacket()
    {
        Position = Vector3.Zero;
        Rotation = Vector3.Zero;
        Timestamp = DateTime.UtcNow;
    }
    
    public byte[] ToBytes()
    {
        // JSON 문자열 생성 비용 및 인코딩 비용 제로
        return MemoryPackSerializer.Serialize(this);
    }


    public static NetworkPacket? FromBytes(byte[] data, int bufferSize)
    {
        try
        {
            // 힙 할당이 없는 ReadOnlySpan 구조체로 버퍼 슬라이싱
            ReadOnlySpan<byte> packetSpan = new ReadOnlySpan<byte>(data, 0, bufferSize);
            
            // 바이너리 오프셋을 그대로 읽어 고속 역직렬화 수행
            return MemoryPackSerializer.Deserialize<NetworkPacket>(packetSpan);
        }
        catch (Exception)
        {
            return null;
        }
    }
}