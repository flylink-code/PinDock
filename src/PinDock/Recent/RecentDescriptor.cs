namespace PinDock;

// 声明式「最近项目」描述：指定哪个应用读取哪个 JSON 文件，以及如何把其中的集合映射为 AppRecentItem。
public sealed class RecentDescriptor
{
    // 匹配的可执行文件名（忽略大小写），如 "obsidian.exe"
    public required string[] Exes { get; init; }

    // 相对 %APPDATA% 的 JSON 文件路径，不允许绝对路径与 ".."
    public required string SourceFile { get; init; }

    // 文件内容编码：空表示 UTF-8 JSON，"hex-utf8" 表示整个文件是十六进制编码的 UTF-8 JSON
    public string Encoding { get; init; } = "";

    public required RecentSource[] Sources { get; init; }
}

public sealed class RecentSource
{
    // JSON 顶层属性名；值可以是数组（元素为对象或路径字符串）或对象（属性名作为 {key}）
    public required string Collection { get; init; }

    // 仅支持 Folder、Workspace、File
    public required AppRecentItemKind Kind { get; init; }

    // 元素为对象时，保存路径的字段；空表示元素本身就是路径字符串
    public string PathField { get; init; } = "";

    // 保存 Unix 毫秒时间戳的字段；空或缺失时使用文件系统修改时间
    public string TimeField { get; init; } = "";

    // 值为 true 表示该项当前已在应用中打开
    public string OpenField { get; init; } = "";

    // 启动 URI 模板，{key} 替换为集合对象的属性名；空表示用应用本身打开路径
    public string LaunchUri { get; init; } = "";
}
