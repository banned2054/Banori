using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Internal;
using Microsoft.EntityFrameworkCore.Storage;
using System.Reflection;

// MigrationsAssembly 是 EF 公开但标注为内部兼容性的扩展点，EF1001 预期存在；版本随包锁定。
#pragma warning disable EF1001

namespace MediaManager.Data.Migrations;

/// <summary>
///     同一程序集内维护 Sqlite 与 MySql 两套迁移（默认命名空间与 .MySql 子命名空间各一套，各含独立模型快照）。
///     EF 不允许一个程序集里混用两套迁移与快照，这里按当前 provider 过滤，
///     使 MigrateAsync 与设计时工具只会看到属于当前 provider 的迁移集。
/// </summary>
public sealed class ProviderAwareMigrationsAssembly(
    ICurrentDbContext                               currentContext,
    IDbContextOptions                               options,
    IMigrationsIdGenerator                          idGenerator,
    IDiagnosticsLogger<DbLoggerCategory.Migrations> logger,
    IDatabaseProvider                               databaseProvider)
    : MigrationsAssembly(currentContext, options, idGenerator, logger)
{
    private IReadOnlyDictionary<string, TypeInfo>? _filteredMigrations;

    /// <summary>Sqlite 与 MySql provider 的名称都含“MySql”之外的可区分片段，这里以名称包含判断，兼容 Pomelo 系与改名后的 provider。</summary>
    private bool IsMySql => databaseProvider.Name.Contains("MySql", StringComparison.OrdinalIgnoreCase);

    private Type ContextType { get; } = currentContext.Context.GetType();

    public override IReadOnlyDictionary<string, TypeInfo> Migrations =>
        _filteredMigrations ??= FilterMigrations();

    public override ModelSnapshot? ModelSnapshot
    {
        get
        {
            var snapshotType = Assembly.GetTypes()
                                       .Where(type => typeof(ModelSnapshot).IsAssignableFrom(type) &&
                                                      type != typeof(ModelSnapshot))
                                       .SingleOrDefault(type => type.GetCustomAttribute<DbContextAttribute>()
                                                                   ?.ContextType == ContextType &&
                                                                IsMySqlMigrationType(type) == IsMySql);
            return snapshotType is null ? null : (ModelSnapshot)Activator.CreateInstance(snapshotType)!;
        }
    }

    private IReadOnlyDictionary<string, TypeInfo> FilterMigrations()
    {
        var isMySql  = IsMySql;
        var filtered = new SortedList<string, TypeInfo>();
        foreach (var (id, type) in base.Migrations)
            if (IsMySqlMigrationType(type.AsType()) == isMySql)
                filtered.Add(id, type);

        return filtered;
    }

    private static bool IsMySqlMigrationType(Type type) =>
        type.Namespace?.StartsWith("MediaManager.Data.Migrations.MySql", StringComparison.Ordinal) == true;
}
