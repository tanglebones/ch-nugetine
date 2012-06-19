using System;
using System.Collections.Generic;
using System.Linq;
using MongoDB.Bson;

namespace nugetine.Internal
{
    internal static class Ex
    {
        public static ISet<T> ToSet<T>(this IEnumerable<T> enumerable)
        {
            ISet<T> set = new HashSet<T>();
            foreach (var e in enumerable)
            {
                set.Add(e);
            }
            return set;
        }

        public static IEnumerable<T> RemoveDuplicatesOn<T, TK>(this IEnumerable<T> enumerable, Func<T, TK> selector)
        {
            var seenSet = new HashSet<TK>();
            foreach (var item in enumerable)
            {
                var key = selector(item);
                if (seenSet.Contains(key)) continue;
                yield return item;
                seenSet.Add(key);
            }
        }

        public static void Overlay(this BsonDocument bson, IEnumerable<BsonElement> with)
        {
            foreach (var element in with)
            {
                if (bson.Contains(element.Name))
                {
                    var original = bson[element.Name];
                    if (original.BsonType == element.Value.BsonType)
                    {
                        if (original.BsonType == BsonType.Document)
                        {
                            bson[element.Name].AsBsonDocument.Overlay(element.Value.AsBsonDocument);
                            continue;
                        }
                        if (original.BsonType == BsonType.Array)
                        {
                            bson[element.Name] = new BsonArray(original.AsBsonArray.Concat(element.Value.AsBsonArray));
                            continue;
                        }
                    }
                }
                bson[element.Name] = element.Value;
            }
        }

        public static int IndexOf<T>(this IEnumerable<T> seq, Func<T,bool> pred)
        {
            var asArr = seq.ToArray();
            var length = asArr.Length;
            for (var i = 0; i < length; ++i) { if (pred(asArr[i])) return i; }
            return -1;
        }

        public static TV GetOrAdd<TK, TV>(this IDictionary<TK, TV> d, TK k, Func<TV> f)
        {
            TV v;
            if (d.TryGetValue(k, out v)) return v;
            v = f();
            d[k] = v;
            return v;
        }
    }
}