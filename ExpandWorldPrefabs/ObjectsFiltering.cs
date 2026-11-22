using System.Collections.Generic;
using System.Linq;
using Data;
using Service;
using UnityEngine;
using Common;

namespace ExpandWorld.Prefab;


public class ObjectsFiltering
{
  // Note: Can include the object itself.
  public static ZDOID[] GetNearby(int limit, Object[] objects, Vector3 pos, Quaternion rot, Functions f, ZDOID? self, bool random)
  {
    if (objects.Length == 0) return [];
    foreach (var o in objects) o.Roll(f, pos, rot);
    var maxRadius = objects.Max(o => o.MaxDistance);
    using var search = EwpProfiler.BeginSearch("GetNearby[array]", EwpProfiler.SearchActive ? string.Join(" || ", objects.Select(o => o.ProfilerFilter)) : "", maxRadius, maxRadius > 10000);
    ZDOID[] results;
    if (maxRadius > 10000)
    {
      var zdos = ZDOMan.instance.m_objectsByID.Values;
      results = GetObjects(limit, zdos, objects, f, self, random, search);
    }
    else
    {
      var zdoLists = GetSectorIndices(objects);
      results = GetObjects(limit, zdoLists, objects, f, self, random, search);
    }
    if (search != null) search.Returned = results.Length;
    return results;
  }
  public static ZDOID[] GetNearby(int limit, Object objects, Vector3 pos, Quaternion rot, Functions f, ZDOID? self, bool random)
  {
    objects.Roll(f, pos, rot);
    var maxRadius = objects.MaxDistance;
    using var search = EwpProfiler.BeginSearch("GetNearby[single]", EwpProfiler.SearchActive ? objects.ProfilerFilter : "", maxRadius, maxRadius > 10000);
    ZDOID[] results;
    if (maxRadius > 10000)
    {
      var zdos = ZDOMan.instance.m_objectsByID.Values;
      results = GetObjects(limit, zdos, objects, f, self, random, search);
    }
    else
    {
      var zdoLists = GetSectorIndices(objects);
      results = GetObjects(limit, zdoLists, objects, f, self, random, search);
    }
    if (search != null) search.Returned = results.Length;
    return results;
  }
  private static ZDOID[] GetObjects(int limit, List<List<ZDO>> zdoLists, Object objects, Functions f, ZDOID? self, bool random, EwpProfiler.SearchContext? search)
  {
    var query = search == null
      ? zdoLists.SelectMany(z => z).Where(z => objects.IsValid(z, f, self))
      : zdoLists.SelectMany(z => z).Where(z => EwpProfiler.Inspect(search, objects.IsValid(z, f, self)));
    if (limit > 0)
      query = random ? query.OrderBy(_ => Random.value).Take(limit) : query.OrderBy(z => Utils.DistanceXZ(z.m_position, objects.CachedPosition)).Take(limit);
    return [.. query.Select(z => z.m_uid)];
  }
  private static ZDOID[] GetObjects(int limit, Dictionary<ZDOID, ZDO>.ValueCollection zdos, Object objects, Functions f, ZDOID? self, bool random, EwpProfiler.SearchContext? search)
  {
    var query = search == null
      ? zdos.Where(z => objects.IsValid(z, f, self))
      : zdos.Where(z => EwpProfiler.Inspect(search, objects.IsValid(z, f, self)));
    if (limit > 0)
      query = random ? query.OrderBy(_ => Random.value).Take(limit) : query.OrderBy(z => Utils.DistanceXZ(z.m_position, objects.CachedPosition)).Take(limit);
    return [.. query.Select(z => z.m_uid)];
  }
  private static ZDOID[] GetObjects(int limit, List<List<ZDO>> zdoLists, Object[] objects, Functions f, ZDOID? self, bool random, EwpProfiler.SearchContext? search)
  {
    var query = search == null
      ? zdoLists.SelectMany(z => z).Where(z => objects.Any(o => o.IsValid(z, f, self)))
      : zdoLists.SelectMany(z => z).Where(z => EwpProfiler.Inspect(search, objects.Any(o => o.IsValid(z, f, self))));
    if (limit > 0)
      query = random ? query.OrderBy(_ => Random.value).Take(limit) : query.OrderBy(z => Utils.DistanceXZ(z.m_position, objects[0].CachedPosition)).Take(limit);
    return [.. query.Select(z => z.m_uid)];
  }
  private static ZDOID[] GetObjects(int limit, Dictionary<ZDOID, ZDO>.ValueCollection zdos, Object[] objects, Functions f, ZDOID? self, bool random, EwpProfiler.SearchContext? search)
  {
    var query = search == null
      ? zdos.Where(z => objects.Any(o => o.IsValid(z, f, self)))
      : zdos.Where(z => EwpProfiler.Inspect(search, objects.Any(o => o.IsValid(z, f, self))));
    if (limit > 0)
      query = random ? query.OrderBy(_ => Random.value).Take(limit) : query.OrderBy(z => Utils.DistanceXZ(z.m_position, objects[0].CachedPosition)).Take(limit);
    return [.. query.Select(z => z.m_uid)];
  }



  public static bool HasNearby(IRangeIntValue? limit, Object[] objects, ZDO zdo, Functions f)
  {
    if (objects.Length == 0) return true;
    foreach (var o in objects) o.Roll(f, zdo.m_position, zdo.GetRotation());
    var l = limit?.Get(f);
    var maxRadius = objects.Max(o => o.MaxDistance);
    using var search = EwpProfiler.BeginSearch("HasNearby", EwpProfiler.SearchActive ? string.Join(" || ", objects.Select(o => o.ProfilerFilter)) : "", maxRadius, maxRadius > 10000);
    if (maxRadius > 10000)
    {
      var zdos = ZDOMan.instance.m_objectsByID.Values;
      if (l == null)
        return EwpProfiler.SearchResult(search, HasAllObjects(zdos, objects, zdo.m_uid, f, search));
      else
        return EwpProfiler.SearchResult(search, HasLimitObjects(zdos, l, objects, zdo.m_uid, f, search));
    }
    var zdoLists = GetSectorIndices(objects);
    if (l == null)
      return EwpProfiler.SearchResult(search, HasAllObjects(zdoLists, objects, zdo.m_uid, f, search));
    else
      return EwpProfiler.SearchResult(search, HasLimitObjects(zdoLists, l, objects, zdo.m_uid, f, search));
  }
  public static bool HasNotNearby(IRangeIntValue? limit, Object[] objects, ZDO zdo, Functions f)
  {
    if (objects.Length == 0) return true;
    foreach (var o in objects) o.Roll(f, zdo.m_position, zdo.GetRotation());
    var l = limit?.Get(f);
    var maxRadius = objects.Max(o => o.MaxDistance);
    using var search = EwpProfiler.BeginSearch("HasNotNearby", EwpProfiler.SearchActive ? string.Join(" || ", objects.Select(o => o.ProfilerFilter)) : "", maxRadius, maxRadius > 10000);
    // Match the broad-search strategy used by HasNearby and GetNearby.
    // Large absence checks must not enumerate a square of empty sectors.
    if (maxRadius > 10000)
    {
      var zdos = ZDOMan.instance.m_objectsByID.Values;
      return EwpProfiler.SearchResult(search, l == null
        ? !HasAllObjects(zdos, objects, zdo.m_uid, f, search)
        : !HasLimitObjects(zdos, l, objects, zdo.m_uid, f, search));
    }
    var zdoLists = GetSectorIndices(objects);
    if (l == null)
      return EwpProfiler.SearchResult(search, !HasAllObjects(zdoLists, objects, zdo.m_uid, f, search));
    else
      return EwpProfiler.SearchResult(search, !HasLimitObjects(zdoLists, l, objects, zdo.m_uid, f, search));
  }

  public static Dictionary<int, Dictionary<string, int>> GetCounts(Object[] objects, ZDO zdo, Functions f, out Dictionary<string, int> totals)
  {
    Dictionary<int, Dictionary<string, int>> counts = [];
    totals = [];
    if (objects.Length == 0) return counts;
    foreach (var o in objects) o.Roll(f, zdo.m_position, zdo.GetRotation());
    var maxRadius = objects.Max(o => o.MaxDistance);
    using var search = EwpProfiler.BeginSearch("GetCounts", EwpProfiler.SearchActive ? string.Join(" || ", objects.Select(o => o.ProfilerFilter)) : "", maxRadius, maxRadius > 10000);
    IEnumerable<ZDO> candidates = maxRadius > 10000
      ? ZDOMan.instance.m_objectsByID.Values
      : GetSectorIndices(objects).SelectMany(z => z);
    foreach (var z in candidates)
    {
      string? name = null;
      var counted = false;
      for (var index = 0; index < objects.Length; index++)
      {
        if (!objects[index].IsValid(z, f, zdo.m_uid)) continue;
        name ??= ZNetScene.instance.GetPrefab(z.GetPrefab())?.name;
        if (name == null) break;
        if (!counted)
        {
          totals.TryGetValue(name, out var total);
          totals[name] = total + objects[index].Weight;
          counted = true;
        }
        if (!counts.TryGetValue(index + 1, out var slot))
          counts[index + 1] = slot = [];
        slot.TryGetValue(name, out var current);
        slot[name] = current + objects[index].Weight;
      }
      if (search != null) EwpProfiler.Inspected(search, counted);
    }
    if (search != null) search.Returned = totals.Count;
    return counts;
  }

  private static bool HasAllObjects(List<List<ZDO>> zdoLists, Object[] objects, ZDOID? self, Functions f, EwpProfiler.SearchContext? search)
  {
    return search == null
      ? objects.All(o => zdoLists.Any(zdos => zdos.Any(z => o.IsValid(z, f, self))))
      : objects.All(o => zdoLists.Any(zdos => zdos.Any(z => EwpProfiler.Inspect(search, o.IsValid(z, f, self)))));
  }
  private static bool HasAllObjects(Dictionary<ZDOID, ZDO>.ValueCollection zdos, Object[] objects, ZDOID? self, Functions f, EwpProfiler.SearchContext? search)
  {
    return search == null
      ? objects.All(o => zdos.Any(z => o.IsValid(z, f, self)))
      : objects.All(o => zdos.Any(z => EwpProfiler.Inspect(search, o.IsValid(z, f, self))));
  }
  private static bool HasLimitObjects(List<List<ZDO>> zdoLists, ValueRange<int> limit, Object[] objects, ZDOID? self, Functions f, EwpProfiler.SearchContext? search)
  {
    var counter = 0;
    var useMax = limit.Max > 0;
    foreach (var list in zdoLists)
    {
      foreach (var z in list)
      {
        var valid = objects.FirstOrDefault(o => o.IsValid(z, f, self));
        if (search != null) EwpProfiler.Inspected(search, valid != null);
        if (valid == null) continue;
        counter += valid.Weight;
        if (useMax && limit.Max < counter) return false;
        if (limit.Min <= counter && !useMax) return true;
      }
    }
    return limit.Min <= counter && counter <= limit.Max;
  }

  private static bool HasLimitObjects(Dictionary<ZDOID, ZDO>.ValueCollection zdos, ValueRange<int> limit, Object[] objects, ZDOID? self, Functions f, EwpProfiler.SearchContext? search)
  {
    var counter = 0;
    var useMax = limit.Max > 0;
    foreach (var z in zdos)
    {
      var valid = objects.FirstOrDefault(o => o.IsValid(z, f, self));
      if (search != null) EwpProfiler.Inspected(search, valid != null);
      if (valid == null) continue;
      counter += valid.Weight;
      if (useMax && limit.Max < counter) return false;
      if (limit.Min <= counter && !useMax) return true;
    }
    return limit.Min <= counter && counter <= limit.Max;
  }
  private static List<List<ZDO>> GetSectorIndices(Object[] objects)
  {
    List<List<ZDO>> zdoLists = [];
    HashSet<ZoneSystem.SectorIndex> handled = [];
    foreach (var o in objects)
      GetSectorIndices(o, zdoLists, handled);
    return zdoLists;
  }

  private static List<List<ZDO>> GetSectorIndices(Object objects)
  {
    List<List<ZDO>> zdoLists = [];
    HashSet<ZoneSystem.SectorIndex> handled = [];
    GetSectorIndices(objects, zdoLists, handled);
    return zdoLists;
  }

  private static void GetSectorIndices(Object o, List<List<ZDO>> zdoLists, HashSet<ZoneSystem.SectorIndex> handled)
  {
    float radius = o.MaxDistance;
    var corner1 = ZoneSystem.GetZone(o.CachedPosition + new Vector3(-radius, 0, -radius));
    var corner2 = ZoneSystem.GetZone(o.CachedPosition + new Vector3(radius, 0, radius));
    var zm = ZDOMan.instance;
    for (var x = corner1.x; x <= corner2.x; x++)
    {
      for (var y = corner1.y; y <= corner2.y; y++)
      {
        var sector = ZoneSystem.SectorToIndex(new Vector2s(x, y));
        if (!handled.Add(sector)) continue;
        var list = zm.m_objectsBySector[sector.Sector];
        if (list != null)
          zdoLists.Add(list);
        // Portals are not added to m_objectsBySector, so have to be handled separately.
        if (zm.m_portalObjects.TryGetValue(sector, out var portalList))
          zdoLists.Add(portalList);
      }
    }
  }
}
