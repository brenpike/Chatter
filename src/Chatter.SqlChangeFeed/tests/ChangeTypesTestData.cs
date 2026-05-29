namespace Chatter.SqlChangeFeed.Tests
{
    internal class FakeRowData : Chatter.CQRS.IMessage
    {
        public int Id { get; set; }
        public string Name { get; set; }
    }
}
