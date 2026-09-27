using Banned.AniParser.Models.Enums;

namespace MediaManager.Core.Rules.Selection;

/// <summary>
///     AniParser 语言组合的客观基础语言组成，供“包含指定语言”匹配使用。
///     该展开只回答“候选是否含有指定语言”，不表示多语言天然优于单语言；排序只依据配置列表序。
/// </summary>
public static class LanguageComposition
{
    public static IReadOnlySet<EnumLanguage> Expand(EnumLanguage language)
    {
        return language switch
        {
            EnumLanguage.Jp      => Set(EnumLanguage.Jp),
            EnumLanguage.Sc      => Set(EnumLanguage.Sc),
            EnumLanguage.Tc      => Set(EnumLanguage.Tc),
            EnumLanguage.Eng     => Set(EnumLanguage.Eng),
            EnumLanguage.JpSc    => Set(EnumLanguage.Jp, EnumLanguage.Sc),
            EnumLanguage.JpTc    => Set(EnumLanguage.Jp, EnumLanguage.Tc),
            EnumLanguage.ScTc    => Set(EnumLanguage.Sc, EnumLanguage.Tc),
            EnumLanguage.EngSc   => Set(EnumLanguage.Eng, EnumLanguage.Sc),
            EnumLanguage.EngTc   => Set(EnumLanguage.Eng, EnumLanguage.Tc),
            EnumLanguage.JpScTc  => Set(EnumLanguage.Jp, EnumLanguage.Sc, EnumLanguage.Tc),
            EnumLanguage.EngScTc => Set(EnumLanguage.Eng, EnumLanguage.Sc, EnumLanguage.Tc),
            _                    => Set(language)
        };
    }

    /// <summary>配置语言是否命中候选语言：完全匹配要求相等；包含匹配要求配置的基础语言全部出现在候选中。</summary>
    public static bool Matches(EnumLanguage configured, EnumLanguage? candidate, LanguageMatchMode mode)
    {
        if (candidate is null) return false;
        if (candidate.Value == configured) return true;
        if (mode == LanguageMatchMode.Exact) return false;

        return Expand(configured).IsSubsetOf(Expand(candidate.Value));
    }

    private static IReadOnlySet<EnumLanguage> Set(params EnumLanguage[] languages)
    {
        return new HashSet<EnumLanguage>(languages);
    }
}
