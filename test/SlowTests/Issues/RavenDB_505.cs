using FastTests;
using Raven.Client.Indexing;
using Raven.Client.Operations.Databases.Indexes;
using Xunit;

namespace SlowTests.Issues
{
    public class RavenDB_505 : RavenNewTestBase
    {
        [Fact]
        public void CreateDeleteCreateIndex()
        {
            using (var store = GetDocumentStore())
            {
                var indexDefinition = new IndexDefinition
                {
                    Name = " test",
                    Maps = { "from d in docs select new {d.Name}" }
                };
                for (int i = 0; i < 10; i++)
                {
                    store.Admin.Send(new PutIndexesOperation(new[] { indexDefinition}));
                    store.Admin.Send(new DeleteIndexOperation("test"));
                }
            }
        }
    }
}