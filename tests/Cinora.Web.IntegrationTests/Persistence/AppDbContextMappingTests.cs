using Cinora.Domain.Entities;
using Cinora.Infrastructure.Identity;
using Cinora.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cinora.Web.IntegrationTests.Persistence;

/// <summary>
/// Locks in the explicit-interface resolution of the domain <c>User</c> set: the domain
/// <see cref="User"/> must map to the <c>Users</c> table while Identity's <c>ApplicationUser</c>
/// keeps <c>AspNetUsers</c>. Guards against a future silent wrong-table regression. Inspects the
/// EF model only — no database connection is opened.
/// </summary>
public sealed class AppDbContextMappingTests
{
    [Fact]
    public void Domain_User_maps_to_Users_table_and_ApplicationUser_maps_to_AspNetUsers()
    {
        using var context = new CinoraDbContextFactory().CreateDbContext([]);

        var domainUserTable = context.Model.FindEntityType(typeof(User))!.GetTableName();
        var identityUserTable = context.Model.FindEntityType(typeof(ApplicationUser))!.GetTableName();

        Assert.Equal("Users", domainUserTable);
        Assert.Equal("AspNetUsers", identityUserTable);
    }
}
