// The mapper's shared test (docs/MAPPER.md), and its checks that need no database.
using Galactus;
using Galactus.ObjectMapping;

[Node("Person")]
public sealed record Person
{
    [Key] public long Id { get; set; }
    public string? Name { get; set; }
    [Property("email_address")] public string? Email { get; set; }
    public long? Age { get; set; }
}

public sealed record Company
{
    [Key] public string? Name { get; set; }
}

[Relationship("WORKS_AT")]
public sealed record WorksAt
{
    public long Since { get; set; }
    public string? Role { get; set; }
}

public sealed record Note
{
    public string? Text { get; set; }
    public int Stars { get; set; }
    [Ignore] public string? Scratch { get; set; }
}

[Node("Bad`Label")]
public sealed class BadLabel
{
    public long X { get; set; }
}

public sealed class BadProperty
{
    [Property("a`b")] public long X { get; set; }
}

public static class MapperTests
{
    static void Check(bool ok, string name)
    {
        if (!ok)
            throw new Exception("mapper: " + name);
    }

    static bool Refused(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (MappingException)
        {
            return true;
        }
    }

    public static void Offline()
    {
        var ann = new Person { Id = 1, Name = "Ann", Email = "ann@example.com" };
        var properties = Mapper.PropertiesOf(ann);
        Check(properties.Count == 4 && (long)properties["id"]! == 1 && (string)properties["name"]! == "Ann" &&
              (string)properties["email_address"]! == "ann@example.com" && properties["age"] is null,
              "properties use stored names");
        var node = new Node(7, new List<object?> { "Person" },
                            new Dictionary<string, object?> {
                                { "id", 1L }, { "name", "Ann" }, { "email_address", "a@b" }, { "extra", true } });
        Check(Mapper.FromNode<Person>(node) == new Person { Id = 1, Name = "Ann", Email = "a@b" }, "a node becomes an object");
        var bare = new Node(7, new List<object?>(), new Dictionary<string, object?> { { "id", 2L } });
        Check(Mapper.FromNode<Person>(bare) == new Person { Id = 2 }, "a node that lacks properties");
        var wrong = new Node(7, new List<object?>(), new Dictionary<string, object?> { { "id", "one" } });
        Check(Refused(() => Mapper.FromNode<Person>(wrong)), "a value of the wrong kind");
        var note = Mapper.FromNode<Note>(new Node(1, new List<object?>(), new Dictionary<string, object?> { { "stars", 5L } }));
        Check(note.Stars == 5 && !Mapper.PropertiesOf(note).ContainsKey("scratch"), "widths and ignored properties");
        Check(Refused(() => Mapper.PropertiesOf(new BadLabel())), "a label with a backtick");
        Check(Refused(() => Mapper.PropertiesOf(new BadProperty())), "a property with a backtick");
        Check(Refused(() => Mapper.FromNode<WorksAt>(node)), "a relationship type from a node");
        Console.WriteLine("C# mapper checks passed");
    }

    public static void Live(string uri, string password, string? caFile)
    {
        using var driver = new Driver(uri, "gdb", password, trustedCertificates: caFile);
        driver.ExecuteQuery("MATCH (n) WHERE n:Person OR n:Company OR n:Note DETACH DELETE n");
        var mapper = new Mapper(driver);
        long[] Ids(List<Person> people) => people.Select(p => p.Id).ToArray();

        // 2. create, find, and the stored property name
        var ann = new Person { Id = 1, Name = "Ann", Email = "ann@example.com" };
        Check(mapper.Create(ann) == ann, "create returns the object as stored");
        Check(mapper.Find<Person>(1L) == ann, "find returns an equal object");
        var raw = driver.ExecuteQuery("MATCH (p:Person {id: 1}) RETURN p.email_address AS e, p.email AS none").Records[0];
        Check((string)raw["e"]! == "ann@example.com" && raw["none"] is null, "the stored property name");
        // 3. save changes, and creates
        ann.Age = 41;
        Check(mapper.Save(ann).Age == 41, "save changes");
        Check(mapper.Save(new Person { Id = 2, Name = "Bob" }) == new Person { Id = 2, Name = "Bob" }, "save creates");
        // 4. a missing key
        Check(mapper.Find<Person>(99L) is null, "a missing key");
        // 5. find all
        mapper.Save(new Person { Id = 3, Name = "Bob", Age = 41 });
        Check(Ids(mapper.FindAll<Person>()).SequenceEqual(new long[] { 1, 2, 3 }), "find all");
        Check(Ids(mapper.FindAll<Person>(new Dictionary<string, object?> { { "Name", "Bob" } })).SequenceEqual(new long[] { 2, 3 }),
              "find all with a filter");
        Check(Ids(mapper.FindAll<Person>(new Dictionary<string, object?> { { "Name", "Bob" }, { "Age", 41L } }))
                  .SequenceEqual(new long[] { 3 }),
              "find all with two filters");
        Check(Ids(mapper.FindAll<Person>(limit: 2)).SequenceEqual(new long[] { 1, 2 }), "find all with a limit");
        Check(Refused(() => mapper.FindAll<Person>(new Dictionary<string, object?> { { "Height", 1L } })),
              "a filter on a property the type lacks");
        // 6. relate and related
        var acme = mapper.Save(new Company { Name = "Acme" });
        Check(mapper.Relate(ann, new WorksAt { Since = 2020, Role = "engineer" }, acme) ==
              new WorksAt { Since = 2020, Role = "engineer" }, "relate");
        mapper.Relate(ann, new WorksAt { Since = 2021, Role = "lead" }, acme);
        var jobs = mapper.Related<WorksAt, Company>(ann);
        Check(jobs.Count == 1 && jobs[0].Relationship == new WorksAt { Since = 2021, Role = "lead" } &&
              jobs[0].Node == new Company { Name = "Acme" }, "related");
        Check(Refused(() => mapper.Relate(new Person { Id = 77, Name = "Nobody" }, new WorksAt { Since = 1 }, acme)),
              "relating a person who is not stored");
        // 7. a hand-written statement
        var found = mapper.Query<Person>("MATCH (p:Person) WHERE p.age >= $age RETURN p ORDER BY p.id",
                                         new Dictionary<string, object?> { { "age", 41L } });
        Check(Ids(found).SequenceEqual(new long[] { 1, 3 }), "query");
        var made = mapper.Query<Company>("RETURN {name: $n} AS c", new Dictionary<string, object?> { { "n", "Made up" } });
        Check(made.Count == 1 && made[0] == new Company { Name = "Made up" }, "query maps a map");
        // 8. delete
        Check(mapper.Delete<Person>(3L) && !mapper.Delete<Person>(3L), "delete");
        // 9. no key
        Check(mapper.Create(new Note { Text = "hello", Stars = 3 }) == new Note { Text = "hello", Stars = 3 }, "create with no key");
        Check(Refused(() => mapper.Save(new Note { Text = "x" })), "saving a type with no key");
        Check(Refused(() => mapper.Find<Note>("x")), "finding a type with no key");
        Check(Refused(() => mapper.Find<Person>(null!)), "finding by no key");
        // the operations join an open transaction
        driver.Begin();
        mapper.Save(new Person { Id = 50, Name = "Temporary" });
        driver.Rollback();
        Check(mapper.Find<Person>(50L) is null, "a rolled-back save");
        Console.WriteLine("C# mapper live test passed");
    }
}
