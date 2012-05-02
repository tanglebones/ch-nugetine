namespace nugetine.Internal.Interface
{
    internal interface IReWriter
    {
        void LoadConfig(string path);
        void Run();
    }
}