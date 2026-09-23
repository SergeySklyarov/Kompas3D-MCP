using System.Globalization;
using Kompas6API5;

namespace KompasMcp.P0Probe;

/// <summary>
/// How to get at the features of an existing document — established by trying every route the
/// interop declares and recording which one actually returned objects.
/// </summary>
/// <remarks>
/// <b>Why this is a separate file and not one call.</b> The first P2 run measured the obvious
/// routes and they were all dead ends:
/// <list type="bullet">
/// <item><c>ksDocument3D.FeatureCollection(0,0,0,0,0.0,objType)</c> returned <b>null</b> for
/// objType 0, −1, 24, 5 and 26.</item>
/// <item><c>ksPart.EntityCollection(o3d_baseExtrusion=24).GetCount()</c> returned <b>0</b> for a
/// document that demonstrably contains a base extrusion.</item>
/// <item><c>ksDocument3D.GetLastFeature()</c> <b>did</b> return the feature, and its
/// <c>ksFeature.type</c> was <b>105</b> (= <c>o3d_entity</c>), <em>not</em> 24. So
/// <c>ksFeature.type</c> is not the operation type, and a future <c>kompas_get_feature</c> that
/// filters on it would find nothing.</item>
/// </list>
/// The family of a feature therefore has to come from <c>ksFeature.GetObject()</c> → the entity's
/// own <c>type</c>, or from the definition interface the object answers QI for. Both are measured
/// here rather than assumed.
/// </remarks>
internal static class FeatureProbe
{
    /// <summary>One enumeration route that was tried, and what it gave back.</summary>
    public sealed record Route(string Name, string Outcome, int Count, IReadOnlyList<string> Features);

    /// <summary>
    /// A feature plus the two identities that actually distinguish families, and the entity behind
    /// it — <c>ksFeature.type</c> is not the operation type, so the entity is what the caller has
    /// to read parameters from.
    /// </summary>
    public sealed record Item(
        ksFeature? Feature,
        ksEntity? Entity,
        string Name,
        int FeatureType,
        int? EntityType,
        string DefinitionInterface);

    /// <summary>
    /// Tries every route and returns the first that yields features, along with the transcript of
    /// all of them. The transcript is the deliverable: it is what tells the next reader which
    /// routes are known-bad.
    /// </summary>
    public static (List<Item> Items, List<Route> Routes, string? WorkingRoute) Enumerate(ksDocument3D doc, ksPart part)
    {
        var routes = new List<Route>();
        List<Item>? best = null;
        string? working = null;

        // R1 — the part's own feature and its sub-feature tree.
        var partFeature = part.GetFeature() as ksFeature;
        if (partFeature is null)
        {
            routes.Add(new Route("part.GetFeature()", "вернул null", 0, Array.Empty<string>()));
        }
        else
        {
            routes.Add(new Route(
                "part.GetFeature()",
                $"«{partFeature.name}» type={partFeature.type}",
                1,
                new[] { $"{partFeature.name}:{partFeature.type}" }));

            var sub = Collect(partFeature.SubFeatureCollection(true, false), "part.GetFeature().SubFeatureCollection(through=true, lib=false)");
            routes.Add(new Route("part.GetFeature().SubFeatureCollection(true,false)", sub_outcome(sub), sub.Count, Names(sub)));
            if (sub.Count > 0 && best is null)
            {
                best = sub;
                working = "part.GetFeature().SubFeatureCollection(true,false)";
            }

            var subLib = Collect(partFeature.SubFeatureCollection(false, false), "part.GetFeature().SubFeatureCollection(through=false, lib=false)");
            routes.Add(new Route("part.GetFeature().SubFeatureCollection(false,false)", sub_outcome(subLib), subLib.Count, Names(subLib)));
            if (subLib.Count > 0 && best is null)
            {
                best = subLib;
                working = "part.GetFeature().SubFeatureCollection(false,false)";
            }
        }

        // R2 — the document-level FeatureCollection, over the objType values that are plausible
        // (including the collection/feature/entity type numbers, since 24 returned null).
        foreach (var objType in new[] { 0, -1, 24, 25, 26, 34, 105, 110, 119, 120 })
        {
            var name = $"doc.FeatureCollection(0,0,0,0,0.0,{objType})";
            try
            {
                var raw = doc.FeatureCollection(0, 0, 0, 0, 0d, objType);
                var items = Collect(raw, name);
                routes.Add(new Route(name, raw is null ? "вернул null" : sub_outcome(items), items.Count, Names(items)));
                if (items.Count > 0 && best is null)
                {
                    best = items;
                    working = name;
                }
            }
            catch (Exception ex)
            {
                routes.Add(new Route(name, ex.GetType().Name + ": " + ex.Message, 0, Array.Empty<string>()));
            }
        }

        // R3 — document and part entity collections by type, including the generic o3d_entity and
        // o3d_operationElement. The first run found that a base extrusion created as type 24 is
        // listed under 25 once stored, so both numbers are swept.
        foreach (var objType in new[] { 24, 25, 26, 34, 105, 110 })
        {
            foreach (var checkEntity in new[] { true, false })
            {
                var name = $"doc.EntityCollection({objType}, checkEntity={checkEntity})";
                try
                {
                    var raw = doc.EntityCollection((short)objType, checkEntity);
                    var items = EntityItems(raw, name);
                    routes.Add(new Route(name, raw is null ? "вернул null" : $"GetCount={CountOf(raw)}", items.Count, Names(items)));
                    if (items.Count > 0 && best is null)
                    {
                        best = items;
                        working = name;
                    }
                }
                catch (Exception ex)
                {
                    routes.Add(new Route(name, ex.GetType().Name + ": " + ex.Message, 0, Array.Empty<string>()));
                }
            }
        }

        foreach (var objType in new[] { 24, 25, 26, 34, 105, 110, 119, 120 })
        {
            var name = $"part.EntityCollection({objType})";
            try
            {
                var raw = part.EntityCollection((short)objType);
                var items = EntityItems(raw, name);
                routes.Add(new Route(name, raw is null ? "вернул null" : $"GetCount={CountOf(raw)}", items.Count, Names(items)));
                if (items.Count > 0 && best is null)
                {
                    best = items;
                    working = name;
                }
            }
            catch (Exception ex)
            {
                routes.Add(new Route(name, ex.GetType().Name + ": " + ex.Message, 0, Array.Empty<string>()));
            }
        }

        // R4 — the last feature, the route the first run proved works.
        var last = doc.GetLastFeature() as ksFeature;
        var lastItems = last is null ? new List<Item>() : new List<Item> { Describe(last, "doc.GetLastFeature()") };
        routes.Add(new Route("doc.GetLastFeature()", last is null ? "вернул null" : $"«{last.name}» type={last.type}", lastItems.Count, Names(lastItems)));
        if (lastItems.Count > 0 && best is null)
        {
            best = lastItems;
            working = "doc.GetLastFeature()";
        }

        // R5 — rollback marker, which is how КОМПАС itself defines "the rest of the tree".
        try
        {
            var rollback = doc.GetRollBackFeature() as ksFeature;
            routes.Add(new Route("doc.GetRollBackFeature()", rollback is null ? "вернул null (маркера нет)" : $"«{rollback.name}» type={rollback.type}", 0, Array.Empty<string>()));
        }
        catch (Exception ex)
        {
            routes.Add(new Route("doc.GetRollBackFeature()", ex.GetType().Name, 0, Array.Empty<string>()));
        }

        return (best ?? new List<Item>(), routes, working);
    }

    /// <summary>The entity's own type number — the identity that does distinguish families.</summary>
    public static int? EntityType(Item item) => item.EntityType;

    public static Item Describe(ksFeature feature, string via)
    {
        var owner = feature.GetObject();
        var entity = owner as ksEntity;
        var definition = entity?.GetDefinition();
        var definitionName = DefinitionName(definition);

        return new Item(feature, entity, feature.name, feature.type, entity?.type, definitionName);
    }

    /// <summary>
    /// What an object <em>is</em>, decided by QueryInterface against the vendor interop rather than
    /// by its CLR type name — which is always <c>System.__ComObject</c> and says nothing.
    /// </summary>
    private static string DefinitionName(object? definition)
    {
        if (definition is null)
        {
            return "null";
        }

        try
        {
            return ComDiscovery.Probe(definition).FirstOrDefault()?.FullName ?? "<ни одного интерфейса по QI>";
        }
        catch (Exception ex)
        {
            return "<" + ex.GetType().Name + ">";
        }
    }

    private static string sub_outcome(List<Item> items) =>
        items.Count > 0 ? $"дал {items.Count} признаков" : "вернул пустую коллекцию или null";

    private static List<Item> Collect(object? raw, string route)
    {
        var list = new List<Item>();
        if (raw is not ksFeatureCollection collection)
        {
            return list;
        }

        try
        {
            collection.refresh();
        }
        catch (Exception)
        {
            // refresh is best effort; the index walk below is what matters.
        }

        var count = collection.GetCount();
        for (var i = 0; i < count; i++)
        {
            try
            {
                var feature = collection.GetByIndex(i);
                if (feature is not null && (object)feature is ksFeature cast)
                {
                    list.Add(Describe(cast, route));
                }
            }
            catch (Exception)
            {
                // One unreadable index must not lose the rest of the tree.
            }
        }

        return list;
    }

    /// <summary>
    /// Turns an entity collection into feature-shaped items. An entity collection holds
    /// <c>ksEntity</c>, so the feature is taken from <c>entity.GetFeature()</c>; where that is
    /// null the entity itself is reported, because the point of the route is to find the operation.
    /// </summary>
    private static List<Item> EntityItems(object? raw, string route)
    {
        var list = new List<Item>();
        if (raw is not ksEntityCollection collection)
        {
            return list;
        }

        var count = collection.GetCount();
        for (var i = 0; i < count; i++)
        {
            try
            {
                if (collection.GetByIndex(i) is not ksEntity entity)
                {
                    continue;
                }

                var feature = entity.GetFeature() as ksFeature;
                if (feature is not null)
                {
                    list.Add(Describe(feature, route));
                    continue;
                }

                var definition = entity.GetDefinition();
                list.Add(new Item(
                    null,
                    entity,
                    entity.name,
                    entity.type,
                    entity.type,
                    DefinitionName(definition)));
            }
            catch (Exception)
            {
                // Unreadable index: skip, the transcript already recorded the route.
            }
        }

        return list;
    }

    private static int CountOf(object raw)
    {
        try
        {
            if (raw is ksEntityCollection entities)
            {
                return entities.GetCount();
            }

            if (raw is ksFeatureCollection features)
            {
                return features.GetCount();
            }
        }
        catch (Exception)
        {
            return -1;
        }

        return -1;
    }

    private static IReadOnlyList<string> Names(List<Item> items) =>
        items.Select(i => $"«{i.Name}» f.type={i.FeatureType} e.type={i.EntityType?.ToString(CultureInfo.InvariantCulture) ?? "<нет>"} def={i.DefinitionInterface}")
            .ToArray();
}
