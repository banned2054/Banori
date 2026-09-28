using MediaManager.Data;
using MediaManager.Features.Proxies;
using System.ComponentModel.DataAnnotations;

namespace MediaManager.Components.Pages.Settings;

/// <summary>代理设置页状态：代理列表、编辑草稿与增删改操作。随页面组件创建和释放。</summary>
public sealed class ProxiesPageViewModel(
    ProxyService                 proxyService,
    ILogger<ProxiesPageViewModel> logger)
{
    private readonly List<ProxyView> _proxies = [];

    public IReadOnlyList<ProxyView> Proxies => _proxies;
    public ProxyDraft               Draft   { get; } = new();

    public bool    Editing            { get; private set; }
    public bool    Saving             { get; private set; }
    public bool    Loaded             { get; private set; }
    public bool    OperationSucceeded { get; private set; }
    public string? StatusMessage      { get; private set; }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        _proxies.Clear();
        _proxies.AddRange(await proxyService.ListAsync(cancellationToken));
        Loaded = true;
    }

    public void BeginCreate()
    {
        Draft.Reset();
        Editing       = true;
        StatusMessage = null;
    }

    public void BeginEdit(ProxyView proxy)
    {
        Draft.Load(proxy);
        Editing       = true;
        StatusMessage = null;
    }

    public void CancelEdit()
    {
        Editing = false;
    }

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        Saving = true;
        try
        {
            await proxyService.SaveAsync(Draft.ToRequest(),
                                         string.IsNullOrWhiteSpace(Draft.Password) ? null : Draft.Password,
                                         cancellationToken);
            OperationSucceeded = true;
            StatusMessage      = "代理已保存。";
            Editing            = false;
            Draft.Password     = null;
            await LoadAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "保存代理失败。");
            OperationSucceeded = false;
            StatusMessage      = exception is ArgumentException ? exception.Message : "保存失败，请查看服务日志。";
        }
        finally
        {
            Saving = false;
        }
    }

    public async Task DeleteAsync(ProxyView proxy, CancellationToken cancellationToken = default)
    {
        try
        {
            await proxyService.DeleteAsync(proxy.Id, cancellationToken);
            OperationSucceeded = true;
            StatusMessage      = $"代理“{proxy.Name}”已删除；引用它的订阅已改回直连。";
            await LoadAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "删除代理 {ProxyName} 失败。", proxy.Name);
            OperationSucceeded = false;
            StatusMessage      = "删除失败，请查看服务日志。";
        }
    }
}

/// <summary>代理编辑模型。密码留空表示沿用已保存值。</summary>
public sealed class ProxyDraft
{
    public Guid? Id { get; set; }

    [Required(ErrorMessage = "请输入代理名称。")]
    [StringLength(50, ErrorMessage = "代理名称长度必须为 1 到 50 个字符。")]
    public string Name { get; set; } = string.Empty;

    public ProxyProtocol Protocol { get; set; } = ProxyProtocol.Http;

    [Required(ErrorMessage = "请填写代理主机。")]
    [StringLength(255, ErrorMessage = "代理主机不能超过 255 个字符。")]
    public string Host { get; set; } = string.Empty;

    [Range(1, 65535, ErrorMessage = "代理端口必须是 1 到 65535 的整数。")]
    public int Port { get; set; } = 7890;

    [StringLength(100, ErrorMessage = "代理用户名不能超过 100 个字符。")]
    public string? Username { get; set; }

    [DataType(DataType.Password)]
    public string? Password { get; set; }

    public void Reset()
    {
        Id       = null;
        Name     = string.Empty;
        Protocol = ProxyProtocol.Http;
        Host     = string.Empty;
        Port     = 7890;
        Username = null;
        Password = null;
    }

    public void Load(ProxyView proxy)
    {
        Id       = proxy.Id;
        Name     = proxy.Name;
        Protocol = proxy.Protocol;
        Host     = proxy.Host;
        Port     = proxy.Port;
        Username = proxy.Username;
        Password = null;
    }

    internal SaveProxyRequest ToRequest()
    {
        return new SaveProxyRequest(Id, Name, Protocol, Host, Port, Username);
    }
}
