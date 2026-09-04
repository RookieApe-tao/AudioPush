using System.Text.Json;
using System.Text.Json.Serialization;

namespace YuPinTuiSong;

/// <summary>config.json 配置；首次运行自动生成，含随机访问令牌。</summary>
public sealed class Config
{
    public int Port { get; set; } = 8818;
    public int Mp3Bitrate { get; set; } = 192;
    public bool RequireAuth { get; set; } = true;
    public string Token { get; set; } = "";

    [JsonIgnore] public string FilePath { get; private set; } = "";

    static readonly JsonSerializerOptions WriteOpts = new() { WriteIndented = true };

    public static Config Load(string baseDir)
    {
        var path = Path.Combine(baseDir, "config.json");
        Config cfg;
        if (File.Exists(path))
        {
            try
            {
                cfg = JsonSerializer.Deserialize<Config>(File.ReadAllText(path)) ?? new Config();
            }
            catch (Exception ex)
            {
                Log.Warn($"config.json 解析失败，使用默认配置: {ex.Message}");
                cfg = new Config();
            }
        }
        else
        {
            cfg = new Config();
        }

        cfg.FilePath = path;
        if (string.IsNullOrWhiteSpace(cfg.Token)) cfg.Token = NewToken();
        if (!File.Exists(path)) cfg.Save();
        return cfg;
    }

    public void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(this, WriteOpts)); }
        catch (Exception ex) { Log.Warn("保存 config.json 失败: " + ex.Message); }
    }

    static string NewToken()
    {
        const string chars = "abcdefghjkmnpqrstuvwxyz23456789";   // 去掉易混淆的 i/l/o/0/1
        return new string(Enumerable.Range(0, 8).Select(_ => chars[Random.Shared.Next(chars.Length)]).ToArray());
    }
}
