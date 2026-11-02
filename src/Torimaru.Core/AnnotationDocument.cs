using System.Text.Json;
using System.Text.Json.Serialization;

namespace Torimaru.Core;

/// <summary>
/// 注釈の集合。これ自体が保存フォーマットになり、PNG の tEXt チャンクに埋め込まれる。
/// 「平らに焼いた PNG」を配布しつつ、後から再編集できるのはこの仕組みのため。
/// </summary>
public sealed class AnnotationDocument
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    /// <summary>注釈前のオリジナル画像への相対パス。</summary>
    public string? OriginalRelativePath { get; set; }

    /// <summary>ステップツールが次に振る番号。</summary>
    public int NextStepNumber { get; set; } = 1;

    public List<AnnotationShape> Shapes { get; set; } = [];

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public string ToJson() => JsonSerializer.Serialize(this, SerializerOptions);

    /// <summary>JSON から復元する。壊れていれば null を返す（例外は投げない）。</summary>
    public static AnnotationDocument? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var doc = JsonSerializer.Deserialize<AnnotationDocument>(json, SerializerOptions);
            if (doc is null)
            {
                return null;
            }

            // 未知の種類は読み飛ばす（将来バージョンで保存したファイルを開いても壊れないように）
            doc.Shapes = doc.Shapes.Where(s => ShapeKind.IsKnown(s.Kind)).ToList();
            if (doc.NextStepNumber < 1)
            {
                doc.NextStepNumber = 1;
            }

            return doc;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public AnnotationDocument Clone() => new()
    {
        Version = Version,
        OriginalRelativePath = OriginalRelativePath,
        NextStepNumber = NextStepNumber,
        Shapes = Shapes.Select(s => s.Clone()).ToList(),
    };
}
