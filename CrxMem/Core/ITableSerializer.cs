namespace CrxMem.Core
{
    public interface ITableSerializer
    {
        string Serialize(CheatTable table);

        CheatTable Deserialize(string content);

        void Save(string path, CheatTable table);

        CheatTable Load(string path);
    }
}
