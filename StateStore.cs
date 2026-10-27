using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PIKAN;

/// <summary>
/// 上次浏览位置的持久化（鸿蒙版走 preferences，Windows 版走一个 JSON 文件）。
///
/// 存什么：不存图片本身，只存「上次停在第几张 + 那张的路径 / 文件名 / 总数 / 图片来源」。
/// 为什么两个都要存：文件夹里会新增照片，纯靠序号会指错图；纯靠路径又在换了图片源时找不到。
///   · 路径命中    → 精确定位到同一张（最可靠）
///   · 路径没命中  → 仅在「总数一致」时按序号回退（大概率还是同一批、顺序未变）
///   · 都没有      → 从第 1 张开始，不做无根据的猜测
/// </summary>
public sealed class AppState
{
    public const int SnapshotMax = 600;

    public int LastIdx { get; set; } = -1;
    public string LastUri { get; set; } = "";
    public string LastName { get; set; } = "";
    public int LastCount { get; set; }
    public string LastSrc { get; set; } = "";

    /// <summary>
    /// 「最后一次成功显示」的那张图的路径 —— 下次启动预热的就是它。
    /// 与 LastUri 的区别：那个记的是「停在第几张」（切过去就记，哪怕没解出来），
    /// 这个只在真的解出画面后才记，预热一张打不开的图毫无意义。
    /// </summary>
    public string LastOkUri { get; set; } = "";

    /// <summary>上次会话的图片列表快照（供「回到上次」在换源后仍能复原）。</summary>
    public List<string> Snapshot { get; set; } = new();

    private static string FilePath
    {
        get
        {
            string dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PIKAN");
            Directory.CreateDirectory(dir);
            return System.IO.Path.Combine(dir, "state.json");
        }
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static AppState Load()
    {
        try
        {
            string path = FilePath;
            if (!File.Exists(path))
            {
                return new AppState();
            }

            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<AppState>(json, Options) ?? new AppState();
        }
        catch
        {
            return new AppState();
        }
    }

    public void Save()
    {
        try
        {
            if (Snapshot.Count > SnapshotMax)
            {
                Snapshot = Snapshot.Take(SnapshotMax).ToList();
            }

            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
        }
        catch
        {
            // 写盘失败无所谓，最多是丢一次「回到上次」
        }
    }
}
