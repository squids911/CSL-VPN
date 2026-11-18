using Xunit;

namespace CSL.Vpn.IntegrationTests;

/// <summary>
/// All API tests share one ApiFixture instance and run sequentially. This
/// matters because the fixture resets the public schema on initialization —
/// per-class fixtures would race each other on DROP/CREATE SCHEMA and could
/// wipe the database underneath another class's running tests.
/// </summary>
[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<ApiFixture>
{
    public const string Name = "api";
}
