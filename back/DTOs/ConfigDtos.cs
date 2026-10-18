namespace ERental.DTOs;

/// <summary>数据字典条目（需求 4.2.1 数据字典管理）</summary>
public class DictItemDto
{
    public int Id { get; init; }
    /// <summary>存储键，形如 dict.fault_type.001</summary>
    public string Key { get; init; } = string.Empty;
    /// <summary>字典项名称，如「电池故障」</summary>
    public string Value { get; init; } = string.Empty;
    /// <summary>备注说明</summary>
    public string? Desc { get; init; }
}

/// <summary>数据字典分组，如「故障类型」下的一组可选值</summary>
public class DictGroupDto
{
    public string Group { get; init; } = string.Empty;
    public string GroupName { get; init; } = string.Empty;
    public List<DictItemDto> Items { get; init; } = [];
}

/// <summary>数据字典新增 / 修改入参</summary>
public class DictRequest
{
    /// <summary>分组编码（新增时必填；修改时忽略）</summary>
    public string Group { get; init; } = string.Empty;
    /// <summary>字典项名称</summary>
    public string Value { get; init; } = string.Empty;
    /// <summary>备注</summary>
    public string? Desc { get; init; }
}

/// <summary>ComfyUI 地址保存 / 测试连接入参（系统配置页）。</summary>
public class ComfyUiUrlRequest
{
    /// <summary>ComfyUI 服务地址，形如 http://127.0.0.1:8188。</summary>
    public string Url { get; init; } = string.Empty;
}
