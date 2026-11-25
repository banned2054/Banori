using Banori.Core.Torrents;
using Banori.Features.Downloads;

namespace Banori.Components.Pages.Settings;

/// <summary>设置页「分享率配置」Tab 状态与保存/同步协调。</summary>
public sealed class ShareLimitsPageViewModel(
    ShareLimitSettingsStore              store,
    ILogger<ShareLimitsPageViewModel>    logger,
    DownloadService?                     downloadService = null)
{
    private readonly List<ShareLimitProfile> _profiles = [];

    public IReadOnlyList<ShareLimitProfile> Profiles => _profiles;

    public IReadOnlyList<string> AllSiteSources => TorrentSiteSource.AllLabels;

    public Guid?                  SelectedProfileId { get; private set; }
    public bool                   IsCreatingNew     { get; private set; }
    public ShareLimitProfileDraft Draft             { get; private set; } = new();

    public bool    Loaded        { get; private set; }
    public bool    Saving        { get; private set; }
    public bool    HasError      { get; private set; }
    public string? StatusMessage { get; private set; }

    public bool HasActiveEditor => IsCreatingNew || SelectedProfileId is not null;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        var settings = await store.GetAsync(cancellationToken);
        _profiles.Clear();
        _profiles.AddRange(settings.Profiles);
        IsCreatingNew = false;

        if (_profiles.Count > 0)
            SelectProfile(_profiles[0].Id);
        else
        {
            SelectedProfileId = null;
            Draft             = new ShareLimitProfileDraft();
        }

        Loaded = true;
    }

    public void SelectProfile(Guid? profileId)
    {
        StatusMessage = null;
        HasError      = false;
        IsCreatingNew = false;

        if (profileId is not { } id)
        {
            SelectedProfileId = null;
            Draft             = new ShareLimitProfileDraft();
            return;
        }

        var profile = _profiles.FirstOrDefault(item => item.Id == id);
        if (profile is null)
        {
            SelectedProfileId = null;
            Draft             = new ShareLimitProfileDraft();
            return;
        }

        SelectedProfileId = profile.Id;
        Draft             = ShareLimitProfileDraft.FromProfile(profile);
    }

    public void BeginCreate()
    {
        StatusMessage     = null;
        HasError          = false;
        IsCreatingNew     = true;
        SelectedProfileId = null;
        Draft             = new ShareLimitProfileDraft
        {
            Mode = TorrentShareLimitMode.Unlimited
        };
    }

    public void CancelCreate()
    {
        StatusMessage = null;
        HasError      = false;
        IsCreatingNew = false;
        if (_profiles.Count > 0)
            SelectProfile(_profiles[0].Id);
        else
            Draft = new ShareLimitProfileDraft();
    }

    /// <summary>返回已占用该站点来源的其他配置名称；未占用或归属当前编辑配置时返回 null。</summary>
    public string? OwnerOfSiteSource(string siteSource)
    {
        foreach (var profile in _profiles)
        {
            if (!IsCreatingNew && profile.Id == SelectedProfileId) continue;
            if (profile.SiteSources.Contains(siteSource, StringComparer.OrdinalIgnoreCase))
                return profile.Name;
        }

        return null;
    }

    public bool IsSiteSourceSelected(string siteSource) =>
        Draft.SiteSources.Contains(siteSource, StringComparer.OrdinalIgnoreCase);

    public void ToggleSiteSource(string siteSource, bool selected)
    {
        if (selected && OwnerOfSiteSource(siteSource) is not null) return;

        if (selected)
            Draft.SiteSources.Add(siteSource);
        else
            Draft.SiteSources.Remove(siteSource);
    }

    public async Task SaveCurrentAsync(CancellationToken cancellationToken = default)
    {
        if (!HasActiveEditor) return;

        Saving        = true;
        StatusMessage = null;
        HasError      = false;
        try
        {
            var existing = IsCreatingNew
                ? null
                : _profiles.FirstOrDefault(item => item.Id == SelectedProfileId);
            var targetId = existing?.Id ?? Guid.NewGuid();
            var updatedProfile = Draft.ToProfile(targetId);

            var nextProfiles = new List<ShareLimitProfile>(_profiles.Count + 1);
            if (existing is null)
            {
                nextProfiles.AddRange(_profiles);
                nextProfiles.Add(updatedProfile);
            }
            else
            {
                foreach (var item in _profiles)
                    nextProfiles.Add(item.Id == existing.Id ? updatedProfile : item);
            }

            var saved = await store.SaveAsync(new ShareLimitSettings(nextProfiles), cancellationToken);
            _profiles.Clear();
            _profiles.AddRange(saved.Profiles);
            IsCreatingNew     = false;
            SelectedProfileId = targetId;
            var persisted = _profiles.Single(item => item.Id == targetId);
            Draft = ShareLimitProfileDraft.FromProfile(persisted);

            var affectedSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (existing is not null) affectedSources.UnionWith(existing.SiteSources);
            affectedSources.UnionWith(persisted.SiteSources);

            await ApplySyncFeedbackAsync("分享率配置已保存", affectedSources, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ArgumentException exception)
        {
            HasError      = true;
            StatusMessage = exception.Message;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "保存分享率配置失败。");
            HasError      = true;
            StatusMessage = "保存分享率配置失败，请查看服务日志。";
        }
        finally
        {
            Saving = false;
        }
    }

    public async Task DeleteSelectedAsync(CancellationToken cancellationToken = default)
    {
        if (IsCreatingNew || SelectedProfileId is not { } id) return;

        var existing = _profiles.FirstOrDefault(item => item.Id == id);
        if (existing is null) return;

        Saving        = true;
        StatusMessage = null;
        HasError      = false;
        try
        {
            var nextProfiles = _profiles.Where(item => item.Id != id).ToList();
            var saved        = await store.SaveAsync(new ShareLimitSettings(nextProfiles), cancellationToken);
            _profiles.Clear();
            _profiles.AddRange(saved.Profiles);

            if (_profiles.Count > 0)
            {
                SelectedProfileId = _profiles[0].Id;
                Draft             = ShareLimitProfileDraft.FromProfile(_profiles[0]);
            }
            else
            {
                SelectedProfileId = null;
                Draft             = new ShareLimitProfileDraft();
            }

            await ApplySyncFeedbackAsync($"已删除配置「{existing.Name}」", existing.SiteSources, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "删除分享率配置失败。");
            HasError      = true;
            StatusMessage = "删除分享率配置失败，请查看服务日志。";
        }
        finally
        {
            Saving = false;
        }
    }

    private async Task ApplySyncFeedbackAsync(
        string actionText, IReadOnlyCollection<string> affectedSources, CancellationToken cancellationToken)
    {
        if (downloadService is null || affectedSources.Count == 0)
        {
            StatusMessage = $"{actionText}。";
            return;
        }

        var sync = await downloadService.SyncShareLimitsAsync(affectedSources, cancellationToken);
        if (!string.IsNullOrEmpty(sync.ErrorMessage))
        {
            HasError      = true;
            StatusMessage = $"{actionText}，但同步到下载器失败：{sync.ErrorMessage}";
            return;
        }

        if (!sync.DownloaderConfigured)
        {
            StatusMessage = $"{actionText}（下载器未启用，将在提交种子时生效）。";
            return;
        }

        StatusMessage = sync.SyncedTorrents > 0
            ? $"{actionText}，已同步更新 {sync.SyncedTorrents} 个下载器种子。"
            : $"{actionText}，当前没有需要更新的下载器种子。";
    }
}

/// <summary>单个分享率配置方案的编辑草稿。</summary>
public sealed class ShareLimitProfileDraft
{
    public string                Name                            { get; set; } = string.Empty;
    public TorrentShareLimitMode Mode                            { get; set; } = TorrentShareLimitMode.UseGlobal;
    public bool                  RatioLimitEnabled               { get; set; }
    public double                RatioLimit                      { get; set; }
    public bool                  SeedingTimeLimitEnabled         { get; set; }
    public int                   SeedingTimeLimitMinutes         { get; set; }
    public bool                  InactiveSeedingTimeLimitEnabled { get; set; }
    public int                   InactiveSeedingTimeLimitMinutes { get; set; }

    public HashSet<string> SiteSources { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static ShareLimitProfileDraft FromProfile(ShareLimitProfile profile)
    {
        var draft = new ShareLimitProfileDraft
        {
            Name                            = profile.Name,
            Mode                            = profile.Mode,
            RatioLimitEnabled               = profile.RatioLimitEnabled,
            RatioLimit                      = profile.RatioLimit,
            SeedingTimeLimitEnabled         = profile.SeedingTimeLimitEnabled,
            SeedingTimeLimitMinutes         = profile.SeedingTimeLimitMinutes,
            InactiveSeedingTimeLimitEnabled = profile.InactiveSeedingTimeLimitEnabled,
            InactiveSeedingTimeLimitMinutes = profile.InactiveSeedingTimeLimitMinutes
        };
        draft.SiteSources.UnionWith(profile.SiteSources);
        return draft;
    }

    public ShareLimitProfile ToProfile(Guid id) => new(
        id,
        Name,
        Mode,
        RatioLimitEnabled,
        RatioLimit,
        SeedingTimeLimitEnabled,
        SeedingTimeLimitMinutes,
        InactiveSeedingTimeLimitEnabled,
        InactiveSeedingTimeLimitMinutes,
        [.. SiteSources]);
}
