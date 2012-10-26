using MongoDB.Bson;

namespace nugetine.Internal.Interface
{
    internal interface IGleaner
    {
        BsonDocument Run();
    }
}