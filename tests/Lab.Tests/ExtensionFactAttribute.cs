namespace Lab.Tests;

public sealed class ExtensionFactAttribute : FactAttribute
{
    public ExtensionFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("LAB_EXTENSIONS_TEST") != "1")
            Skip = "Use scripts/Verify-CI.ps1 -Suite Extensions for isolated real PostgreSQL/S3/DynamoDB checks.";
    }
}
