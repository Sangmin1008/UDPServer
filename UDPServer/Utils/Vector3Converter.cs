using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UDPServer.Utils;

/// <summary>
/// System.Numerics.Vector3을 JSON으로 직렬화/역직렬화하기 위한 커스텀 컨버터
/// System.Text.Json이 Vector3의 {"X": 1, "Y": 0, "Z": 0} 형식을 제대로 처리하지 못하는 문제를 해결
/// </summary>
public class Vector3Converter : JsonConverter<Vector3>
{
    /// <summary>
    /// JSON에서 Vector3 객체로 읽기 (역직렬화)
    /// </summary>
    public override Vector3 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("Vector3은 JSON 객체여야 합니다.");
        }

        float x = 0, y = 0, z = 0;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                return new Vector3(x, y, z);
            }

            if (reader.TokenType == JsonTokenType.PropertyName)
            {
                string propertyName = reader.GetString()!;
                reader.Read(); // 값으로 이동

                switch (propertyName.ToUpper())
                {
                    case "X":
                        x = reader.GetSingle();
                        break;
                    case "Y":
                        y = reader.GetSingle();
                        break;
                    case "Z":
                        z = reader.GetSingle();
                        break;
                }
            }
        }

        throw new JsonException("Vector3 JSON 형식이 잘못되었습니다.");
    }

    /// <summary>
    /// Vector3 객체를 JSON으로 쓰기 (직렬화)
    /// </summary>
    public override void Write(Utf8JsonWriter writer, Vector3 value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteNumber("X", value.X);
        writer.WriteNumber("Y", value.Y);
        writer.WriteNumber("Z", value.Z);
        writer.WriteEndObject();
    }
}