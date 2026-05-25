using Cursory.Core.Services;

namespace Cursory.Tests;

[TestFixture]
public class UserRepositoryTests
{
    /// <summary>
    /// Regression for the boot-time crash: appsettings.json had `"Cursory:UsersPath": ""`
    /// and the old `??` fallback in CursoryServices treated empty-string as a real path,
    /// passing it into UserRepository whose Save() then fell over on File.Move(_, "",
    /// overwrite). The constructor now refuses the empty path up-front so the failure
    /// surfaces at DI-registration time with an actionable message rather than deep
    /// inside the first Save.
    /// </summary>
    [TestCase("")]
    [TestCase("   ")]
    [TestCase(null)]
    public void Constructor_rejects_empty_path(string? path)
    {
        Assert.Throws<ArgumentException>(() => new UserRepository(path!));
    }
}
