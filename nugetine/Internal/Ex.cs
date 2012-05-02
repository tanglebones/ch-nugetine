using System;
using System.Collections.Generic;
using System.Linq;
using MongoDB.Bson;

namespace nugetine.Internal
{
    internal static class Ex
    {
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
    }
}