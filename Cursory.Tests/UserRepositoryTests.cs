using Cursory.Core.Models;
using Cursory.Core.Services;

namespace Cursory.Tests;

[TestFixture]
public class UserRepositoryTests
{
    private string tempPath = null!;

    [SetUp]
    public void SetUp()
    {
        tempPath = Path.Combine(Path.GetTempPath(), $"cursory-test-{Guid.NewGuid():N}.json");
    }

    [TearDown]
    public void TearDown()
    {
        if (File.Exists(tempPath)) File.Delete(tempPath);
        var dir = Path.GetDirectoryName(tempPath);
        if (dir != null && Directory.Exists(dir))
        {
            foreach (var stray in Directory.GetFiles(dir, Path.GetFileName(tempPath) + "*.tmp"))
                File.Delete(stray);
        }
    }

    private static UserAccount MakeUser(string? id = null, string username = "player1") => new()
    {
        Id = id ?? Guid.NewGuid().ToString(),
        Username = username,
        DisplayName = username,
        PasswordHash = "hash",
        Role = UserRoles.Player,
        Color = "#7F77DD",
    };

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

    [Test]
    public void Missing_file_loads_as_empty()
    {
        var repo = new UserRepository(tempPath);
        Assert.That(repo.Count, Is.EqualTo(0));
        Assert.That(repo.GetAll(), Is.Empty);
    }

    [Test]
    public void Add_persists_and_round_trips_through_a_fresh_instance()
    {
        var repo = new UserRepository(tempPath);
        var user = MakeUser(username: "gungreeneyes");
        repo.Add(user);

        var reloaded = new UserRepository(tempPath);
        var fetched = reloaded.GetById(user.Id);
        Assert.That(fetched, Is.Not.Null);
        Assert.That(fetched!.Username, Is.EqualTo("gungreeneyes"));
    }

    [Test]
    public void Update_persists_and_round_trips_through_a_fresh_instance()
    {
        var repo = new UserRepository(tempPath);
        var user = MakeUser(username: "original");
        repo.Add(user);

        var modified = MakeUser(user.Id, "renamed");
        repo.Update(modified);

        var reloaded = new UserRepository(tempPath);
        var fetched = reloaded.GetById(user.Id);
        Assert.That(fetched, Is.Not.Null);
        Assert.That(fetched!.Username, Is.EqualTo("renamed"));
    }

    [Test]
    public void Delete_persists_and_round_trips_through_a_fresh_instance()
    {
        var repo = new UserRepository(tempPath);
        var user = MakeUser();
        repo.Add(user);
        repo.Delete(user.Id);

        var reloaded = new UserRepository(tempPath);
        Assert.That(reloaded.GetById(user.Id), Is.Null);
        Assert.That(reloaded.Count, Is.EqualTo(0));
    }

    [Test]
    public void Update_of_unknown_id_is_a_no_op()
    {
        var repo = new UserRepository(tempPath);
        var existing = MakeUser(username: "stays");
        repo.Add(existing);

        repo.Update(MakeUser(Guid.NewGuid().ToString(), "ghost"));

        Assert.That(repo.Count, Is.EqualTo(1));
        Assert.That(repo.GetByUsername("stays"), Is.Not.Null);
        Assert.That(repo.GetByUsername("ghost"), Is.Null);
    }

    [Test]
    public void Delete_of_unknown_id_leaves_others()
    {
        var repo = new UserRepository(tempPath);
        var user = MakeUser(username: "survivor");
        repo.Add(user);

        repo.Delete(Guid.NewGuid().ToString());

        Assert.That(repo.Count, Is.EqualTo(1));
        Assert.That(repo.GetByUsername("survivor"), Is.Not.Null);
    }

    [Test]
    public void GetByUsername_is_case_insensitive()
    {
        var repo = new UserRepository(tempPath);
        repo.Add(MakeUser(username: "GunGreenEyes"));

        Assert.That(repo.GetByUsername("gungreeneyes"), Is.Not.Null);
        Assert.That(repo.GetByUsername("GUNGREENEYES"), Is.Not.Null);
    }

    [Test]
    public void GetAll_returns_defensive_clones()
    {
        var repo = new UserRepository(tempPath);
        var user = MakeUser(username: "original");
        repo.Add(user);

        var result = repo.GetAll();
        result[0].Username = "mutated";

        Assert.That(repo.GetByUsername("original"), Is.Not.Null);
        Assert.That(repo.GetByUsername("mutated"), Is.Null);
    }

    [Test]
    public void GetById_returns_a_defensive_clone()
    {
        var repo = new UserRepository(tempPath);
        var user = MakeUser(username: "original");
        repo.Add(user);

        var fetched = repo.GetById(user.Id)!;
        fetched.Username = "mutated";

        Assert.That(repo.GetById(user.Id)!.Username, Is.EqualTo("original"));
    }

    [Test]
    public void GetByUsername_returns_a_defensive_clone()
    {
        var repo = new UserRepository(tempPath);
        var user = MakeUser(username: "original");
        repo.Add(user);

        var fetched = repo.GetByUsername("original")!;
        fetched.Username = "mutated";

        Assert.That(repo.GetByUsername("original"), Is.Not.Null);
        Assert.That(repo.GetByUsername("mutated"), Is.Null);
    }

    [Test]
    public void Count_reflects_adds_and_deletes()
    {
        var repo = new UserRepository(tempPath);
        Assert.That(repo.Count, Is.EqualTo(0));

        var first = MakeUser();
        var second = MakeUser();
        repo.Add(first);
        repo.Add(second);
        Assert.That(repo.Count, Is.EqualTo(2));

        repo.Delete(first.Id);
        Assert.That(repo.Count, Is.EqualTo(1));
    }

    [Test]
    public void Save_creates_a_missing_parent_directory()
    {
        var nestedPath = Path.Combine(Path.GetTempPath(), $"cursory-test-dir-{Guid.NewGuid():N}", "users.json");
        try
        {
            var repo = new UserRepository(nestedPath);
            repo.Add(MakeUser());

            Assert.That(File.Exists(nestedPath), Is.True);
        }
        finally
        {
            var dir = Path.GetDirectoryName(nestedPath)!;
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public void Save_leaves_no_stray_tmp_files()
    {
        var repo = new UserRepository(tempPath);
        repo.Add(MakeUser());
        repo.Update(MakeUser());
        repo.Delete(Guid.NewGuid().ToString());

        var dir = Path.GetDirectoryName(tempPath)!;
        var stray = Directory.GetFiles(dir, Path.GetFileNameWithoutExtension(tempPath) + "*.tmp");
        Assert.That(stray, Is.Empty);
    }
}
