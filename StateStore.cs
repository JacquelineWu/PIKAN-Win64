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
    /// 上次浏览的**目录**（图片库根 / 选中的文件夹）。
    /// 只记 picture 位置不够：换台机器、图片挪过地方后序号会对不上，
    /// 而目录在的话可以重新扫一遍，新增/删掉的图也能自然反映出来。
    /// 「选择的文件」这种零散来源退化为第一个文件的目录，仅作兜底参考。
    /// </summary>
    public string LastRoot { get; set; } = "";

    /// <summary>
    /// 上次浏览的目录属于哪种来源：`gallery`（我的图片库）/ `folder`（选的文件夹）/ `files`（手工挑的文件）。
    /// `files` 时不能直接按目录重扫——那批图是用户挑出来的，目录里可能有别的几百张。
    /// </summary>
    public string LastKind { get; set; } = "";

    /// <summary>
    /// 「最后一次成功显示」的那张图的路径 —— 下次启动预热的就是它。
    /// 与 LastUri 的区别：那个记的是「停在第几张」（切过去就记，哪怕没解出来），
    /// 这个只在真的解出画面后才记，预热一张打不开的图毫无意义。
    /// </summary>
    public string LastOkUri { get; set; } = "";

    /// <summary>上次会话的图片列表快照（供「回到上次」在换源后仍能复原）。</summary>
    public List<string> Snapshot { get; set; } = new();

    /// <summary>档案列表是否倒序（false = 名称从 A 到 Z）。</summary>
    public bool SortDesc { get; set; }

    /// <summary>左侧档案列表拖出来的宽度（布局单位）；0 = 没拖过，用默认宽度。</summary>
    public double ListWidth { get; set; }

    /// <summary>档案列表是否处在「收起」状态（只留档案名一栏）。Ctrl+B 或箭头按钮切换。</summary>
    public bool ListCollapsed { get; set; }

    /// <summary>用户在「选项」里勾选的看图策略（五项，默认全开）。</summary>
    public AppOptions Options { get; set; } = new();

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

    private static readonly JsonSerializerOptions JsonOpts = new()
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
            return JsonSerializer.Deserialize<AppState>(json, JsonOpts) ?? new AppState();
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

            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch
        {
            // 写盘失败无所谓，最多是丢一次「回到上次」
        }
    }
}
