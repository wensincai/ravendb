﻿// -----------------------------------------------------------------------
//  <copyright file="MultiLoadInTransaction.cs" company="Hibernating Rhinos LTD">
//      Copyright (c) Hibernating Rhinos LTD. All rights reserved.
//  </copyright>
// -----------------------------------------------------------------------
using System.Transactions;

using Raven.Tests.Common;

using Xunit;
using Xunit.Extensions;

namespace Raven.Tests.MailingList
{
	public class MultiLoadInTransaction : RavenTest
	{
        [Theory]
        [PropertyData("Storages")]
		public void InsertAndSingleSelect(string storage)
		{
            using (var store = NewRemoteDocumentStore(requestedStorage: storage))
			{
				var expected = new Bar { Id = "test/bar/1", Foo = "Some value" };
				using (new TransactionScope())
				{
					using (var session = store.OpenSession())
					{
						session.Store(expected);
						session.SaveChanges();
					}
					using (var session = store.OpenSession())
					{
						var actual = session.Load<Bar>(expected.Id);
						Assert.Equal(expected.Id, actual.Id);
						Assert.Equal(expected.Foo, actual.Foo);
					}
	

					using (var session = store.OpenSession())
					{
						var actualList = session.Load<Bar>(new [] { expected.Id, "i do not exist" });
						Assert.Equal(2, actualList.Length);
						Assert.NotNull(actualList[0]);
						Assert.Null(actualList[1]);
						Assert.Equal(expected.Id, actualList[0].Id);
						Assert.Equal(expected.Foo, actualList[0].Foo);
					}
				}
			}
		}

		public class Bar
		{
			public string Id { get; set; }

			public string Foo { get; set; }
		}
	}
}