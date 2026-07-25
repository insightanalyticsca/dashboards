using corporate_dashboards.Models;
using Microsoft.Extensions.Options;
using System.Security.Claims;

namespace corporate_dashboards.Services;

public interface ICxDashboardUploadAccessService
{
    bool CanAccess(ClaimsPrincipal user);
}

public sealed class CxDashboardUploadAccessService : ICxDashboardUploadAccessService
{
    private readonly CxDashboardUploadAccessOptions _options;

    public CxDashboardUploadAccessService(IOptions<CxDashboardUploadAccessOptions> options)
    {
        _options = options.Value;
    }

    public bool CanAccess(ClaimsPrincipal user)
    {
        if (_options.AllowAnonymous)
        {
            return true;
        }

        var userName = (user.Identity?.Name ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(userName)
            && _options.Users.Any(x => string.Equals(x?.Trim(), userName, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        foreach (var group in _options.Groups.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            if (user.IsInRole(group.Trim()))
            {
                return true;
            }
        }

        return false;
    }
}
