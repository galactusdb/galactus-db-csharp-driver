// Object mapping: nodes and relationships as the application's own classes,
// and the Cypher for everyday operations. Base class library only;
// docs/MAPPER.md is the contract every Galactus mapper follows.
//
//   [Node("Person")]
//   public sealed class Person {
//       [Key] public long Id { get; set; }
//       public string? Name { get; set; }
//       [Property("email_address")] public string? Email { get; set; }
//   }
//
//   var people = new Mapper(driver);
//   people.Save(new Person { Id = 1, Name = "Ann" });
//   Person? ann = people.Find<Person>(1L);
using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;

namespace Galactus.ObjectMapping;

/// <summary>A type that cannot be mapped, or a value that does not fit it.</summary>
public sealed class MappingException : Exception
{
    public MappingException(string message) : base(message) { }
}

/// <summary>Names the label of a node type. Without it the label is the class name.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
public sealed class NodeAttribute : Attribute
{
    public string? Label { get; }
    public NodeAttribute(string? label = null) => Label = label;
}

/// <summary>Marks a class as a relationship type and names its type.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
public sealed class RelationshipAttribute : Attribute
{
    public string? Type { get; }
    public RelationshipAttribute(string? type = null) => Type = type;
}

/// <summary>Marks the property that identifies a node to the application.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class KeyAttribute : Attribute { }

/// <summary>Names the database property a class property is stored as.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class PropertyAttribute : Attribute
{
    public string Name { get; }
    public PropertyAttribute(string name) => Name = name;
}

/// <summary>Leaves a class property out of the mapping.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class IgnoreAttribute : Attribute { }

internal sealed record MappedProperty(PropertyInfo Info, string Stored);

internal sealed class Shape
{
    static readonly ConcurrentDictionary<Type, Shape> Known = new();
    public Type Type { get; }
    public string Name { get; }
    public bool IsRelationship { get; }
    public List<MappedProperty> Properties { get; } = new();
    public MappedProperty? Key { get; }

    public static Shape Of(Type type) => Known.GetOrAdd(type, t => new Shape(t));

    Shape(Type type)
    {
        Type = type;
        var relationship = type.GetCustomAttribute<RelationshipAttribute>();
        IsRelationship = relationship is not null;
        Name = Checked(relationship is not null
                           ? relationship.Type ?? type.Name.ToUpperInvariant()
                           : type.GetCustomAttribute<NodeAttribute>()?.Label ?? type.Name,
                       $"{type.Name}'s label or relationship type");
        if (type.GetConstructor(Type.EmptyTypes) is null && !type.IsValueType)
            throw new MappingException($"{type.Name} needs a public constructor with no parameters");
        foreach (var info in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!info.CanRead || !info.CanWrite || info.GetIndexParameters().Length > 0 ||
                info.GetCustomAttribute<IgnoreAttribute>() is not null)
                continue;
            var stored = Checked(info.GetCustomAttribute<PropertyAttribute>()?.Name ?? DefaultName(info.Name),
                                 $"{type.Name}.{info.Name}");
            var mapped = new MappedProperty(info, stored);
            Properties.Add(mapped);
            if (info.GetCustomAttribute<KeyAttribute>() is null)
                continue;
            if (Key is not null)
                throw new MappingException($"{type.Name} has more than one key");
            Key = mapped;
        }
    }

    static string Checked(string value, string what) =>
        value.Length == 0 || value.Contains('`')
            ? throw new MappingException($"{what} must be a name without backticks, got '{value}'")
            : value;

    // Name is name, Id is id and URLPath is urlPath.
    internal static string DefaultName(string name)
    {
        int upper = 0;
        while (upper < name.Length && char.IsUpper(name[upper]))
            upper++;
        if (upper > 1 && upper < name.Length)
            upper--;
        return name[..upper].ToLowerInvariant() + name[upper..];
    }

    public Dictionary<string, object?> PropertiesOf(object value)
    {
        var map = new Dictionary<string, object?>();
        foreach (var property in Properties)
            map[property.Stored] = property.Info.GetValue(value);
        return map;
    }

    public object Build(IDictionary<string, object?> properties)
    {
        var value = Activator.CreateInstance(Type)!;
        foreach (var property in Properties)
        {
            properties.TryGetValue(property.Stored, out var stored);
            try
            {
                property.Info.SetValue(value, Convert(stored, property.Info.PropertyType));
            }
            catch (Exception e) when (e is InvalidCastException or OverflowException or FormatException)
            {
                throw new MappingException($"{Type.Name}.{property.Info.Name}: {e.Message}");
            }
        }
        return value;
    }

    // A database value as a property type: the number widths C# has from the
    // two the database has, lists as lists or arrays, null as the type's default.
    static object? Convert(object? value, Type target)
    {
        var inner = Nullable.GetUnderlyingType(target);
        if (value is null)
            return target.IsValueType && inner is null ? Activator.CreateInstance(target) : null;
        target = inner ?? target;
        if (target.IsInstanceOfType(value))
            return value;
        if (value is long whole)
        {
            if (target == typeof(int)) return checked((int)whole);
            if (target == typeof(short)) return checked((short)whole);
            if (target == typeof(byte)) return checked((byte)whole);
            if (target == typeof(double)) return (double)whole;
            if (target == typeof(float)) return (float)whole;
            if (target == typeof(decimal)) return (decimal)whole;
        }
        if (value is double real && target == typeof(float))
            return (float)real;
        if (value is string text && target == typeof(decimal))
            return decimal.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
        if (value is IList items && value is not byte[])
        {
            var element = target.IsArray ? target.GetElementType()!
                          : target.IsGenericType ? target.GetGenericArguments()[0] : null;
            if (element is not null)
            {
                var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(element))!;
                foreach (var item in items)
                    list.Add(Convert(item, element));
                if (!target.IsArray && target.IsInstanceOfType(list))
                    return list;
                if (target.IsArray)
                {
                    var array = Array.CreateInstance(element, list.Count);
                    list.CopyTo(array, 0);
                    return array;
                }
            }
        }
        throw new InvalidCastException($"a {value.GetType().Name} does not fit a {target.Name}");
    }
}

/// <summary>Runs the everyday operations on a driver, each as one statement.</summary>
public sealed class Mapper
{
    public Driver Driver { get; }
    public Mapper(Driver driver) => Driver = driver;

    static string Quote(string name) => "`" + name + "`";

    static Shape NodeShape(Type type)
    {
        var shape = Shape.Of(type);
        return shape.IsRelationship
            ? throw new MappingException($"{type.Name} is a relationship type, not a node type")
            : shape;
    }
    static Shape RelationshipShape(Type type)
    {
        var shape = Shape.Of(type);
        return shape.IsRelationship
            ? shape
            : throw new MappingException($"{type.Name} is not marked [Relationship]");
    }
    static Shape Keyed(Type type)
    {
        var shape = NodeShape(type);
        return shape.Key is null ? throw new MappingException($"{type.Name} has no key") : shape;
    }
    static object KeyValue(Shape shape, object? key) =>
        key ?? throw new MappingException($"{shape.Type.Name}: the key {shape.Key!.Info.Name} has no value");
    static string MatchKey(string variable, Shape shape, string parameter) =>
        $"({variable}:{Quote(shape.Name)} {{{Quote(shape.Key!.Stored)}: ${parameter}}})";

    /// <summary>Every mapped property of an object, by its stored name.</summary>
    public static Dictionary<string, object?> PropertiesOf(object value) =>
        Shape.Of(value.GetType()).PropertiesOf(value);

    /// <summary>An object of a node type from a node.</summary>
    public static T FromNode<T>(Node node) => (T)NodeShape(typeof(T)).Build(node.Properties);

    /// <summary>An object of a relationship type from a relationship.</summary>
    public static T FromRelationship<T>(Relationship relationship) =>
        (T)RelationshipShape(typeof(T)).Build(relationship.Properties);

    static T FromValue<T>(object? value) => value switch
    {
        Node node => FromNode<T>(node),
        Relationship relationship => FromRelationship<T>(relationship),
        UnboundRelationship unbound => (T)RelationshipShape(typeof(T)).Build(unbound.Properties),
        IDictionary<string, object?> map => (T)Shape.Of(typeof(T)).Build(map),
        _ => throw new MappingException(
            $"{typeof(T).Name}: expected a node, a relationship or a map, got {value?.GetType().Name ?? "null"}"),
    };

    static Dictionary<string, object?> Parameters(params (string Name, object? Value)[] entries)
    {
        var map = new Dictionary<string, object?>();
        foreach (var (name, value) in entries)
            map[name] = value;
        return map;
    }

    /// <summary>Makes a new node of the object and returns it as stored.</summary>
    public T Create<T>(T value) where T : notnull
    {
        var shape = NodeShape(typeof(T));
        var result = Driver.ExecuteQuery($"CREATE (n:{Quote(shape.Name)}) SET n = $properties RETURN n",
                                         Parameters(("properties", shape.PropertiesOf(value))));
        return FromValue<T>(result.Records[0]["n"]);
    }

    /// <summary>Creates the node with the object's key, or changes it, and returns it as stored.</summary>
    public T Save<T>(T value) where T : notnull
    {
        var shape = Keyed(typeof(T));
        var key = KeyValue(shape, shape.Key!.Info.GetValue(value));
        var result = Driver.ExecuteQuery($"MERGE {MatchKey("n", shape, "key")} SET n += $properties RETURN n",
                                         Parameters(("key", key), ("properties", shape.PropertiesOf(value))));
        return FromValue<T>(result.Records[0]["n"]);
    }

    /// <summary>The node with the key, or the default when there is none.</summary>
    public T? Find<T>(object key)
    {
        var shape = Keyed(typeof(T));
        var result = Driver.ExecuteQuery($"MATCH {MatchKey("n", shape, "key")} RETURN n LIMIT 1",
                                         Parameters(("key", KeyValue(shape, key))));
        return result.Records.Count == 0 ? default : FromValue<T>(result.Records[0]["n"]);
    }

    /// <summary>Every node of the type whose properties equal those in <paramref name="filter"/>,
    /// which is keyed by the class's property names.</summary>
    public List<T> FindAll<T>(IDictionary<string, object?>? filter = null, int? limit = null)
    {
        var shape = NodeShape(typeof(T));
        var parameters = new Dictionary<string, object?>();
        var conditions = new List<string>();
        foreach (var (name, value) in filter ?? new Dictionary<string, object?>())
        {
            var property = shape.Properties.Find(p => p.Info.Name == name) ??
                           throw new MappingException($"{typeof(T).Name} has no property '{name}' to filter by");
            parameters[$"p{conditions.Count}"] = value;
            conditions.Add($"n.{Quote(property.Stored)} = $p{conditions.Count}");
        }
        var query = $"MATCH (n:{Quote(shape.Name)})";
        if (conditions.Count > 0)
            query += " WHERE " + string.Join(" AND ", conditions);
        query += " RETURN n";
        if (shape.Key is not null)
            query += $" ORDER BY n.{Quote(shape.Key.Stored)}";
        if (limit is int count)
        {
            if (count < 0)
                throw new MappingException("limit must be zero or more");
            parameters["limit"] = (long)count;
            query += " LIMIT $limit";
        }
        return Driver.ExecuteQuery(query, parameters).Records.Select(row => FromValue<T>(row["n"])).ToList();
    }

    /// <summary>Removes the node with the key and its relationships. Whether there was one.</summary>
    public bool Delete<T>(object key)
    {
        var shape = Keyed(typeof(T));
        var result = Driver.ExecuteQuery(
            $"MATCH {MatchKey("n", shape, "key")} DETACH DELETE n RETURN count(*) AS deleted",
            Parameters(("key", KeyValue(shape, key))));
        return (long)result.Records[0]["deleted"]! > 0;
    }

    /// <summary>Joins two stored nodes with a relationship, or changes the one between them.</summary>
    public TRelationship Relate<TFrom, TRelationship, TTo>(TFrom from, TRelationship relationship, TTo to)
        where TFrom : notnull where TRelationship : notnull where TTo : notnull
    {
        Shape a = Keyed(typeof(TFrom)), b = Keyed(typeof(TTo)), r = RelationshipShape(typeof(TRelationship));
        var result = Driver.ExecuteQuery(
            $"MATCH {MatchKey("a", a, "a")}, {MatchKey("b", b, "b")} " +
            $"MERGE (a)-[r:{Quote(r.Name)}]->(b) SET r += $properties RETURN r",
            Parameters(("a", KeyValue(a, a.Key!.Info.GetValue(from))), ("b", KeyValue(b, b.Key!.Info.GetValue(to))),
                       ("properties", r.PropertiesOf(relationship))));
        return result.Records.Count == 0
            ? throw new MappingException($"{a.Type.Name} or {b.Type.Name} was not found to relate")
            : FromValue<TRelationship>(result.Records[0]["r"]);
    }

    /// <summary>The relationships of one type from a node to nodes of another type.</summary>
    public List<(TRelationship Relationship, TTo Node)> Related<TRelationship, TTo>(object from)
    {
        Shape a = Keyed(from.GetType()), b = NodeShape(typeof(TTo)), r = RelationshipShape(typeof(TRelationship));
        var query = $"MATCH {MatchKey("a", a, "a")}-[r:{Quote(r.Name)}]->(b:{Quote(b.Name)}) RETURN r, b";
        if (b.Key is not null)
            query += $" ORDER BY b.{Quote(b.Key.Stored)}";
        var result = Driver.ExecuteQuery(query, Parameters(("a", KeyValue(a, a.Key!.Info.GetValue(from)))));
        return result.Records.Select(row => (FromValue<TRelationship>(row["r"]), FromValue<TTo>(row["b"]))).ToList();
    }

    /// <summary>Runs a statement and maps the first column of each row: a node, a relationship,
    /// or a map of properties.</summary>
    public List<T> Query<T>(string cypher, IDictionary<string, object?>? parameters = null)
    {
        Shape.Of(typeof(T));
        var result = Driver.ExecuteQuery(cypher, parameters);
        return result.Keys.Count == 0
            ? new List<T>()
            : result.Records.Select(row => FromValue<T>(row[result.Keys[0]])).ToList();
    }
}
