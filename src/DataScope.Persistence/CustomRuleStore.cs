using DataScope.Core.Classification;
using Microsoft.EntityFrameworkCore;

namespace DataScope.Persistence;

/// <summary>The custom detection rules in force (D133): read at the start of every scan, and for the policy version shown elsewhere.</summary>
public static class CustomRuleStore
{
    /// <summary>The enabled rules, compiled. Read when a scan starts or resumes, so a change applies from then on.</summary>
    public static async Task<CustomRuleSet> CurrentAsync(DataScopeDbContext db, CancellationToken ct)
    {
        var rules = await db.CustomRules.AsNoTracking().Where(r => r.Enabled).OrderBy(r => r.Id).Take(CustomRules.MaxEnabled).ToListAsync(ct);
        return CustomRules.Compile(rules.Select(r => r.ToDefinition()));
    }

    /// <summary>The policy version files classified now get: the built-in version, plus the enabled custom rules' fingerprint.</summary>
    public static async Task<string> PolicyVersionAsync(DataScopeDbContext db, CancellationToken ct) => (await CurrentAsync(db, ct)).PolicyVersion;
}
