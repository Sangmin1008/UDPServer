using System.Text.Json.Serialization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using UDPServer.Utils;

namespace UDPServer.Models;

// 패킷 타입 정의
public enum PacketType
{
    PlayerJoin = 1,     // 플레이어 접속 요청
    PlayerLeave = 2,    // 플레이어 접속 종료
    PlayerUpdate = 3,   // 플레이어 위치 및 상태 업데이트
    PlayerSpawn = 4,    // 플레이어 스폰 요청, 다른 플레이어에게 새 플레이어 접속 알림
    PlayerDespawn = 5,  // 플레이어 디스폰 요청, 다른 플레이어에게 플레이어 접속 종료 알림
    PlayerFire = 6,     // 플레이어 발사 이벤트
}

public class NetworkPacket
{
    public PacketType Type { get; set; }
    public int PlayerId { get; set; }
    
    // JSON 파싱 오류로 인해 별도의 컨버터 사용
    [JsonConverter(typeof(Vector3Converter))]
    public Vector3 Position { get; set; }
    
    [JsonConverter(typeof(Vector3Converter))]
    public Vector3 Rotation { get; set; }
    
    // 패킷 생성 시간
    public DateTime Timestamp { get; set; }

    public NetworkPacket()
    {
        Position = Vector3.Zero;
        Rotation = Vector3.Zero;
        Timestamp = DateTime.UtcNow;
    }

    public string ToJson()
    {
        // 패킷을 직렬화해서 JSON 문자열로 반환
        return JsonSerializer.Serialize(this);
    }

    public static NetworkPacket? FromJsonn(string json)
    {
        try
        {
            // JSON 문자열을 역직렬화해서 NetworkPacket 객체로 변환
            return JsonSerializer.Deserialize<NetworkPacket>(json);
        }
        catch (Exception)
        {
            return null;
        }
    }
    
    // 패킷을 UTF-8 바이트 배열로 변환
    public byte[] ToBytes()
    {
        string json = ToJson(); // 객체를 JSON 문자열로 변환
        // JSON 문자열을 UTF-8 바이트 배열로 인코딩
        return Encoding.UTF8.GetBytes(json);
    }
    
    // UTF-8 바이트 배열을 NetworkPacket 객체로 변환
    public static NetworkPacket? FromBytes(byte[] data, int bufferSize)
    {
        try
        {
            string json = Encoding.UTF8.GetString(data, 0, bufferSize);
            return FromJsonn(json);
        }
        catch (Exception)
        {
            return null;
        }
    }
}

// 전송 순서
// NetworkPacket 객체 -> ToJson() (문자열) -> ToBytes() (바이트 배열) -> UDP 전송

// 수신 순서
// UDP 수신 (바이트 배열) -> FromBytes() -> NetworkPacket 객체